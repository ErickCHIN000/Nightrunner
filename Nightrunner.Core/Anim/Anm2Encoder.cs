using System.Numerics;

namespace Nightrunner.Core.Anim;

/// <summary>
/// What the encoder takes from <see cref="Anm2Clip.Source"/> (the decode a clip came from) — information the values
/// cannot show. All on by default, which makes decode → encode bit-exact; all off is the plain recipe (fresh clips
/// have no source, so it is what they get anyway). Edited streams and layouts that no longer fit fall back to the recipe.
/// </summary>
public sealed class Anm2EncodeOptions
{
    /// <summary>Keep a stream animated when the source stored it animated (it varied below one quantisation step, so it decodes flat).</summary>
    public bool ReuseSourceStreams { get; init; } = true;
    /// <summary>Keep the source's segment layout (a few clips carry layouts the greedy rule does not produce).</summary>
    public bool ReuseSourceLayout { get; init; } = true;
    /// <summary>
    /// Keep the source's per-segment bias/scale for streams whose values still lie exactly on that grid: Techland's
    /// float rounding sometimes left a code range one step asymmetric, which recomputing min/max cannot reproduce.
    /// </summary>
    public bool ReuseSourceGrid { get; init; } = true;
    /// <summary>
    /// Header version to write (default: the clip's). V2 and V1 have no pose list; V0 is one payload block of at most
    /// 64 KiB and takes its rate word and trailer from a V0 <see cref="Anm2Clip.Source"/>.
    /// </summary>
    public ushort? Version { get; init; }

    /// <summary>The plain recipe, ignoring the source.</summary>
    public static Anm2EncodeOptions Recipe => new() { ReuseSourceStreams = false, ReuseSourceLayout = false, ReuseSourceGrid = false };
}

/// <summary>Result of <see cref="Anm2Encoder.SelfCheck"/>: the encoded clip re-read and compared with its source values.</summary>
public sealed record Anm2CheckResult(bool HeaderOk, string? Problem, double MaxRotationDegrees, double MaxTranslation,
                                     double MaxScale, double MaxWeight, int Segments, int Bytes);

/// <summary>
/// Values → ANM2, following the shipped encoder recipe (§2.2 of the animation notes, checked on the corpus):
/// <list type="bullet">
/// <item>a stream is constant iff it is constant over the whole clip (every stored copy included); a track's three
///   scale streams are constant together or not at all;</item>
/// <item>per segment and animated stream: <c>bias = (min + max) / 2</c>, <c>scale = max((max − min) / 65534, floor)</c>
///   with floor 2e-5 for rotation and translation streams and 2e-4 for scale streams, codes rounded half away from 0;</item>
/// <item>segments are grown greedily by 15-interval key blocks while the segment stays ≤ 64 KiB; a hard cut
///   (<see cref="Anm2Clip.EndCopies"/> differing by more than <see cref="CutThreshold"/>) ends the segment unless it
///   falls on a block boundary;</item>
/// <item>row widths are the minimum shared by the 8 lanes of a group; identity VFR; one pose is written as two keys.</item>
/// </list>
/// Measured over the install (tools/AnimCheck): with the source reused every shipped clip re-encodes bit-exactly;
/// the recipe alone reproduces 99.887 % of stream bias/scale pairs and 99.72 % of segment layouts.
/// </summary>
public static class Anm2Encoder
{
    public const float FloorRotTrans = 2e-5f;
    public const float FloorScale = 2e-4f;

    public static byte[] Encode(Anm2Clip clip, Anm2EncodeOptions? options = null) => Quantize(clip, options).Encode();

    public static Anm2Payload Quantize(Anm2Clip clip, Anm2EncodeOptions? options = null)
    {
        options ??= new Anm2EncodeOptions();
        int T = clip.TrackCount, S = clip.StreamCount, N = clip.FrameBound;
        if (T == 0) throw new Anm2FormatException("a clip needs at least one track");
        if (N == 0) throw new Anm2FormatException("frameBound 0: write a single pose as two identical keys (frameBound 1)");
        if (clip.Values.Length != clip.KeyCount * S) throw new Anm2FormatException($"{clip.Values.Length} values for {clip.KeyCount} keys × {S} streams");
        ushort version = options.Version ?? clip.Version;
        if (version > 3) throw new Anm2UnsupportedException($"cannot write header version {version}");
        if (version < 3 && clip.PoseHashes.Length != 0) throw new Anm2UnsupportedException($"a V{version} header has no pose list");
        var srcHeader = clip.Source?.Header;
        if (version == 0 && srcHeader?.Version != 0)
            throw new Anm2UnsupportedException("writing a V0 clip needs a V0 source clip (its rate word and trailer)");
        bool trailing = version == 0;
        foreach (double v in clip.Values)
            if (!double.IsFinite(v) || Math.Abs(v) > float.MaxValue) throw new Anm2FormatException($"value {v} is not a finite float");

        var src = clip.Source is { } sp && sp.NumTracks == T && sp.Header.FrameBound == N ? sp : null;

        // A stream is constant iff it is constant over every stored value (and, reusing the source, was constant there).
        var constant = new bool[S];
        for (int i = 0; i < S; i++)
        {
            double v0 = clip.Values[i];
            bool c = true;
            for (int k = 1; k <= N && c; k++) c = clip.Values[k * S + i] == v0;
            foreach (var copy in clip.EndCopies.Values) c &= copy[i] == v0;
            foreach (var pad in clip.PadRows.Values) c &= pad[i] == v0;
            constant[i] = c;
        }
        if (src is not null && options.ReuseSourceStreams)
            foreach (var seg in src.Segments)
                for (int t = 0; t < T; t++)
                    for (int k = 0; k < 9; k++)
                        if (!Anm2Payload.IsConstant(seg.Flags[t], k)) constant[t * 9 + k] = false;
        var flags = new byte[T];
        for (int t = 0; t < T; t++)
        {
            int f = version == 0 ? 0 : 0x80;   // V0 flags carry no bit 7
            for (int k = 0; k < 6; k++) if (constant[t * 9 + k]) f |= 1 << k;
            if (constant[t * 9 + 6] && constant[t * 9 + 7] && constant[t * 9 + 8]) f |= 0x40;
            flags[t] = (byte)f;
        }
        var (anim, cons) = Anm2Payload.StreamMap(flags);
        var constants = new float[cons.Length];
        for (int c = 0; c < cons.Length; c++) constants[c] = (float)clip.Values[cons[c]];

        bool grid = options.ReuseSourceGrid;
        List<Anm2Segment>? segs = null;
        if (src is not null && options.ReuseSourceLayout)
        {
            segs = [];
            int at = 0;
            foreach (var ss in src.Segments)
            {
                var seg = BuildSegment(clip, src, grid, at, ss.Frames, flags, constants, anim, trailing);
                if (!trailing && Anm2Payload.SegmentSize(seg, T) > Anm2Header.SegmentBytes) { segs = null; break; }
                segs.Add(seg);
                at += ss.Frames;
            }
        }
        if (trailing)
        {
            // V0: the whole clip is one payload block
            if (segs is not { Count: 1 }) segs = [BuildSegment(clip, src, grid, 0, N, flags, constants, anim, true)];
            int size = Anm2Payload.PayloadSizeV0(segs[0], T);
            if (size > Anm2Header.SegmentBytes) throw new Anm2UnsupportedException($"V0 payload would be {size} bytes, over the one 64 KiB block V0 has");
        }
        segs ??= Greedy(clip, src, grid, flags, constants, anim);

        var header = new Anm2Header
        {
            Version = version, TimeBound = clip.TimeBound != 0 ? clip.TimeBound : clip.FrameBound, FrameBound = clip.FrameBound,
            NumStatic = (ushort)cons.Length, TrackHashes = clip.TrackHashes, PoseHashes = clip.PoseHashes,
            SegmentFrames = segs.Select(s => (ushort)s.Frames).ToArray(), VfrDen = clip.Vfr.Length > 0 ? clip.VfrDen : (ushort)1,
            Vfr = version == 0 ? [] : clip.Vfr.Length > 0 ? clip.Vfr : [(clip.FrameBound, 1)], Pad = clip.HeaderPad,
            Rate = version == 0 ? srcHeader!.Rate : (ushort)0, Trailer = version == 0 ? srcHeader!.Trailer : [],
            Reserved = version is 0 or 1 && srcHeader?.Version == version ? srcHeader.Reserved : version switch { 0 => new byte[4], 1 => new byte[10], _ => [] },
        };
        return new Anm2Payload { Header = header, Segments = segs.ToArray() };
    }

    /// <summary>A hard cut: a stored copy of a seam key that differs from the key by more than this in some stream.</summary>
    public const double CutThreshold = 1e-3;

    /// <summary>Frames where a segment must end because the two stored copies of the key differ (camera cuts, teleports).</summary>
    public static SortedSet<int> Cuts(Anm2Clip clip)
    {
        var cuts = new SortedSet<int>();
        int S = clip.StreamCount;
        foreach (var (f, copy) in clip.EndCopies)
            for (int i = 0; i < S; i++)
                if (Math.Abs(copy[i] - clip.Values[f * S + i]) > CutThreshold) { cuts.Add(f); break; }
        return cuts;
    }

    /// <summary>
    /// Greedy layout: 15-interval key blocks while the segment stays ≤ 64 KiB. A cut that falls on a key-block
    /// boundary stays inside the segment (row 15 keeps the key before the cut, the next block's row 0 the key after
    /// it: the "row-15 quirk"); any other cut ends the segment there.
    /// </summary>
    private static List<Anm2Segment> Greedy(Anm2Clip clip, Anm2Payload? src, bool grid, byte[] flags, float[] constants, int[] anim)
    {
        int T = clip.TrackCount, N = clip.FrameBound;
        var cuts = Cuts(clip);
        var segs = new List<Anm2Segment>();
        int start = 0;
        while (start < N)
        {
            // A cut on a key-block boundary is carried by the block's last row; any other cut ends the segment.
            int stop = N;
            foreach (int c in cuts.GetViewBetween(start + 1, N))
                if ((c - start) % 15 != 0) { stop = c; break; }
            int remaining = stop - start;
            int frames = Math.Min(15, remaining);
            var seg = BuildSegment(clip, src, grid, start, frames, flags, constants, anim);
            if (Anm2Payload.SegmentSize(seg, T) > Anm2Header.SegmentBytes)
                throw new Anm2FormatException($"one key block at frame {start} is over 64 KiB ({T} tracks)");
            while (frames < remaining)
            {
                int next = Math.Min(frames + 15, remaining);
                var cand = BuildSegment(clip, src, grid, start, next, flags, constants, anim);
                if (Anm2Payload.SegmentSize(cand, T) > Anm2Header.SegmentBytes) break;
                seg = cand;
                frames = next;
            }
            segs.Add(seg);
            start += frames;
        }
        return segs;
    }

    /// <summary>Values of every stored row of a segment [start, start + frames] (end copies and the pad row included).</summary>
    private static double[] SegmentRows(Anm2Clip clip, int start, int frames, int[] anim, bool trailing, out int rows)
    {
        int S = clip.StreamCount, nA = anim.Length, nb = Anm2Segment.BlocksFor(frames, trailing);
        if (nA == 0) { rows = Anm2Segment.RowsFor(frames, trailing); return []; }
        bool lastSegment = start + frames == clip.FrameBound;
        rows = Anm2Segment.RowsFor(frames, trailing);
        var outv = new double[rows * nA];
        for (int b = 0; b < nb; b++)
        {
            int nr = Anm2Segment.RowsInBlock(frames, b);
            int realRows = Math.Min(16, frames - 15 * b + 1);
            for (int r = 0; r < nr; r++)
            {
                int o = (16 * b + r) * nA;
                if (r == realRows)
                {
                    // pad row after a partial block: a copy of the last row unless the source said otherwise
                    if (clip.PadRows.TryGetValue(start + frames, out var pad))
                        for (int a = 0; a < nA; a++) outv[o + a] = pad[anim[a]];
                    else
                        Array.Copy(outv, o - nA, outv, o, nA);
                    continue;
                }
                int f = start + 15 * b + r;
                // the block's last row is a stored copy when the key is stored again later (next block or segment)
                bool copyRow = r == realRows - 1 && !(lastSegment && b == nb - 1);
                if (copyRow && clip.EndCopies.TryGetValue(f, out var copy))
                    for (int a = 0; a < nA; a++) outv[o + a] = copy[anim[a]];
                else
                    for (int a = 0; a < nA; a++) outv[o + a] = clip.Values[f * S + anim[a]];
            }
        }
        return outv;
    }

    private static Anm2Segment BuildSegment(Anm2Clip clip, Anm2Payload? source, bool grid, int start, int frames, byte[] flags,
                                            float[] constants, int[] anim, bool trailing = false)
    {
        int nA = anim.Length;
        var vals = SegmentRows(clip, start, frames, anim, trailing, out int rows);
        var bias = new float[nA];
        var scale = new float[nA];
        var codes = new short[rows * nA];
        var src = SourceSegment(source, start, frames, flags);
        var srcAnim = src is null ? null : Anm2Payload.StreamMap(src.Flags).Animated;
        for (int a = 0; a < nA; a++)
        {
            if (grid && src is not null && srcAnim![a] == anim[a] && OnGrid(vals, a, nA, rows, src.Bias[a], src.Scale[a]))
            {
                bias[a] = src.Bias[a];
                scale[a] = src.Scale[a];
            }
            else
            {
                double mn = double.PositiveInfinity, mx = double.NegativeInfinity;
                for (int r = 0; r < rows; r++)
                {
                    double v = vals[r * nA + a];
                    if (v < mn) mn = v;
                    if (v > mx) mx = v;
                }
                float floor = anim[a] % 9 >= 6 ? FloorScale : FloorRotTrans;
                double sc = (mx - mn) / 65534.0;
                bias[a] = (float)((mn + mx) / 2);
                scale[a] = sc > floor ? (float)sc : floor;
            }
            double b = bias[a], s = scale[a];
            for (int r = 0; r < rows; r++)
            {
                double x = Math.Round((vals[r * nA + a] - b) / s, MidpointRounding.AwayFromZero);
                codes[r * nA + a] = (short)Math.Clamp(x, short.MinValue, short.MaxValue);
            }
        }
        return new Anm2Segment
        {
            Frames = frames, Flags = flags, Constants = constants, Bias = bias, Scale = scale, Codes = codes,
            StaticPad = src?.StaticPad ?? new byte[6], TrailingBlock = trailing,
        };
    }

    private static Anm2Segment? SourceSegment(Anm2Payload? p, int start, int frames, byte[] flags)
    {
        if (p is null || p.NumTracks != flags.Length) return null;
        int F = 0;
        foreach (var s in p.Segments)
        {
            if (F == start) return s.Frames == frames && s.Flags.AsSpan().SequenceEqual(flags) ? s : null;
            if (F > start) return null;
            F += s.Frames;
        }
        return null;
    }

    private static bool OnGrid(double[] vals, int a, int nA, int rows, float bias, float scale)
    {
        double b = bias, s = scale;
        if (!(s > 0)) return false;
        for (int r = 0; r < rows; r++)
        {
            double v = vals[r * nA + a];
            double c = Math.Round((v - b) / s);
            if (c < -32768 || c > 32767 || c * s + b != v) return false;
        }
        return true;
    }

    // ---- fresh clips ----------------------------------------------------------------------------------------

    /// <summary>
    /// A clip from float TRS, key-major (<c>[key × tracks + track]</c>), parent-local metres, quaternions xyzw
    /// (normalised and flipped to w ≥ 0 here). One key is written as two identical keys, as the shipped clips do.
    /// </summary>
    public static Anm2Clip FromTrs(uint[] trackHashes, int keys, ReadOnlySpan<Quaternion> rotation,
                                   ReadOnlySpan<Vector3> translation, ReadOnlySpan<Vector3> scale)
    {
        int T = trackHashes.Length;
        if (T == 0 || keys < 1) throw new ArgumentException("need at least one track and one key");
        if (keys > ushort.MaxValue) throw new Anm2UnsupportedException($"{keys} keys: frameBound is a u16");
        int n = keys * T;
        if (rotation.Length != n || translation.Length != n || scale.Length != n)
            throw new ArgumentException($"expected {n} entries per array (keys × tracks)");
        int outKeys = keys == 1 ? 2 : keys;
        var v = new double[outKeys * 9 * T];
        for (int key = 0; key < outKeys; key++)
            for (int t = 0; t < T; t++)
            {
                int i = Math.Min(key, keys - 1) * T + t, o = (key * T + t) * 9;
                var (x, y, z) = Anm2Clip.ToStereo(rotation[i]);
                v[o] = x; v[o + 1] = y; v[o + 2] = z;
                v[o + 3] = translation[i].X; v[o + 4] = translation[i].Y; v[o + 5] = translation[i].Z;
                v[o + 6] = scale[i].X; v[o + 7] = scale[i].Y; v[o + 8] = scale[i].Z;
            }
        return FromStreams(trackHashes, [], outKeys - 1, v);
    }

    /// <summary>A clip from raw stream values <c>[key × 9T + track × 9 + k]</c> (pose weights or stereographic TRS).</summary>
    public static Anm2Clip FromStreams(uint[] trackHashes, uint[] poseHashes, int frameBound, double[] values)
    {
        if (frameBound is < 1 or > ushort.MaxValue) throw new Anm2UnsupportedException($"frameBound {frameBound} is outside 1..65535");
        int T = trackHashes.Length;
        if (poseHashes.Length != 0 && !(poseHashes.Length > 9 * T - 9 && poseHashes.Length <= 9 * T))
            throw new Anm2FormatException($"{poseHashes.Length} pose hashes for {T} tracks (needs 9T−9 < n ≤ 9T)");
        if (values.Length != (frameBound + 1) * 9 * T) throw new ArgumentException("values length is not keys × 9T");
        return new Anm2Clip
        {
            TrackHashes = trackHashes, PoseHashes = poseHashes, FrameBound = (ushort)frameBound, TimeBound = (ushort)frameBound,
            Vfr = [((ushort)frameBound, 1)], Values = values,
        };
    }

    /// <summary>
    /// Re-read an encoded clip (header validation and exact payload consumption) and measure its error against the
    /// values it was encoded from: rotation angle in degrees, translation and scale per component, pose weights.
    /// </summary>
    public static Anm2CheckResult SelfCheck(Anm2Clip source, byte[] encoded)
    {
        Anm2Clip back;
        try { back = Anm2Clip.Decode(encoded); }
        catch (Exception e) when (e is Anm2FormatException or Anm2UnsupportedException)
        {
            return new Anm2CheckResult(false, e.Message, double.NaN, double.NaN, double.NaN, double.NaN, 0, encoded.Length);
        }
        string? problem = null;
        if (back.FrameBound != source.FrameBound || back.TrackCount != source.TrackCount ||
            !back.TrackHashes.AsSpan().SequenceEqual(source.TrackHashes) || !back.PoseHashes.AsSpan().SequenceEqual(source.PoseHashes))
            problem = "header fields differ from the source";
        double rot = 0, pos = 0, scl = 0, w = 0;
        if (problem is null)
            for (int key = 0; key < source.KeyCount; key++)
                for (int t = 0; t < source.TrackCount; t++)
                {
                    if (source.IsPoseWeights)
                    {
                        for (int k = 0; k < 9; k++) w = Math.Max(w, Math.Abs(back.Stream(key, t, k) - source.Stream(key, t, k)));
                        continue;
                    }
                    rot = Math.Max(rot, AngleDegrees(Stereo(source, key, t), Stereo(back, key, t)));
                    for (int k = 3; k < 6; k++) pos = Math.Max(pos, Math.Abs(back.Stream(key, t, k) - source.Stream(key, t, k)));
                    for (int k = 6; k < 9; k++) scl = Math.Max(scl, Math.Abs(back.Stream(key, t, k) - source.Stream(key, t, k)));
                }
        return new Anm2CheckResult(problem is null, problem, rot, pos, scl, w, back.Source!.Segments.Length, encoded.Length);
    }

    private static (double X, double Y, double Z, double W) Stereo(Anm2Clip c, int key, int t)
    {
        double x = c.Stream(key, t, 0), y = c.Stream(key, t, 1), z = c.Stream(key, t, 2);
        double n2 = x * x + y * y + z * z, inv = 1 / (1 + n2);
        return (2 * x * inv, 2 * y * inv, 2 * z * inv, (1 - n2) * inv);
    }

    /// <summary>Rotation angle between two unit quaternions, in double (acos of a float dot is useless near 1).</summary>
    private static double AngleDegrees((double X, double Y, double Z, double W) a, (double X, double Y, double Z, double W) b)
    {
        double dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W, sg = dot < 0 ? -1 : 1;
        double dx = a.X - sg * b.X, dy = a.Y - sg * b.Y, dz = a.Z - sg * b.Z, dw = a.W - sg * b.W;
        return 4 * Math.Asin(Math.Min(1, Math.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw) / 2)) * 180 / Math.PI;
    }
}
