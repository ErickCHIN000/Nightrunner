using System.Numerics;

namespace Nightrunner.Core.Anim;

/// <summary>
/// A decoded ANM2 clip as values: per key, per track, the nine streams <c>rx ry rz tx ty tz sx sy sz</c>.
/// Rotation streams hold the stereographic vector <c>v = q.xyz / (1 + w)</c>; <see cref="Rotation"/> turns it into
/// a quaternion. Pose-weight clips (<see cref="IsPoseWeights"/>) use the same streams as generic weights
/// (<see cref="Weight"/>), one per list-B hash.
/// </summary>
/// <remarks>
/// Values are parent-local, metres, Y-up. Keys run 0..<see cref="FrameBound"/> (frameBound intervals); there is no
/// frame rate in the clip. Stream values are held as <c>code × scale + bias</c> in double, which is exact for the
/// stored codes, so the recipe encoder recovers the same codes. A key stored more than once (the last row of a key
/// block or of a segment repeats the next block's first row) is kept in <see cref="EndCopies"/> when the stored
/// copy differs from the key — at every segment seam (each segment quantises on its own) and at cutscene hard cuts,
/// where the copy is the pose before the cut and the key the pose after it (up to 2,446 m apart). A cut falls on a
/// seam or, when it lands on a 15-frame block boundary inside a segment, on that block's row 15 (the notes' "row-15
/// quirk", 10 shipped clips). <see cref="PadRows"/> holds a partial block's trailing pad row when it is not a copy of
/// the last key (never, in shipped data). A sampler that interpolates inside the frame before such a key should use
/// the copy; frame values alone are what the engine shows on whole frames.
/// </remarks>
public sealed class Anm2Clip
{
    public ushort Version { get; set; } = 3;
    public required uint[] TrackHashes { get; init; }
    public uint[] PoseHashes { get; init; } = [];
    public required ushort FrameBound { get; init; }
    public ushort TimeBound { get; init; }
    public ushort VfrDen { get; init; } = 1;
    public (ushort Len, ushort Rate)[] Vfr { get; init; } = [];
    /// <summary>The header pad of the source (verbatim).</summary>
    public byte[] HeaderPad { get; init; } = [];
    /// <summary><c>[key × 9T + track × 9 + k]</c>.</summary>
    public required double[] Values { get; init; }
    /// <summary>Frame → all 9T stream values of the copy stored at the end of the block (or segment) that ends there.</summary>
    public SortedDictionary<int, double[]> EndCopies { get; init; } = [];
    /// <summary>Frame (a segment's last key) → the partial block's pad row, when it is not a copy of that key.</summary>
    public SortedDictionary<int, double[]> PadRows { get; init; } = [];
    /// <summary>The exact decode this clip came from (null for a fresh clip); the encoder reuses its quantisation.</summary>
    public Anm2Payload? Source { get; init; }

    public int TrackCount => TrackHashes.Length;
    public int StreamCount => 9 * TrackHashes.Length;
    public int KeyCount => FrameBound + 1;
    public bool IsPoseWeights => PoseHashes.Length > 0;

    public static Anm2Clip Decode(ReadOnlySpan<byte> clip) => FromPayload(Anm2Payload.Decode(clip));

    public double Stream(int key, int track, int k) => Values[key * StreamCount + track * 9 + k];

    /// <summary>Stereographic decode <c>q = (2v, 1 − |v|²) / (1 + |v|²)</c>, xyzw.</summary>
    public Quaternion Rotation(int key, int track)
    {
        int i = key * StreamCount + track * 9;
        return FromStereo(Values[i], Values[i + 1], Values[i + 2]);
    }

    public Vector3 Translation(int key, int track)
    {
        int i = key * StreamCount + track * 9 + 3;
        return new Vector3((float)Values[i], (float)Values[i + 1], (float)Values[i + 2]);
    }

    public Vector3 Scale(int key, int track)
    {
        int i = key * StreamCount + track * 9 + 6;
        return new Vector3((float)Values[i], (float)Values[i + 1], (float)Values[i + 2]);
    }

    /// <summary>Weight of list-B entry <paramref name="pose"/> (stream <c>pose</c> in track-major order) at a key.</summary>
    public double Weight(int key, int pose)
    {
        if ((uint)pose >= (uint)PoseHashes.Length) throw new ArgumentOutOfRangeException(nameof(pose));
        return Values[key * StreamCount + pose];
    }

    public static Quaternion FromStereo(double x, double y, double z)
    {
        double n2 = x * x + y * y + z * z, inv = 1.0 / (1.0 + n2);
        return new Quaternion((float)(2 * x * inv), (float)(2 * y * inv), (float)(2 * z * inv), (float)((1 - n2) * inv));
    }

    /// <summary>Inverse of <see cref="FromStereo"/>; the quaternion is first flipped to w ≥ 0.</summary>
    public static (double X, double Y, double Z) ToStereo(Quaternion q)
    {
        double x = q.X, y = q.Y, z = q.Z, w = q.W;
        double n = Math.Sqrt(x * x + y * y + z * z + w * w);
        if (n == 0) throw new ArgumentException("zero quaternion");
        if (w < 0) n = -n;
        x /= n; y /= n; z /= n; w /= n;
        return (x / (1 + w), y / (1 + w), z / (1 + w));
    }

    /// <summary>Bulk TRS for display: key-major arrays, rotation xyzw (4 floats), translation and scale (3 floats).</summary>
    public (float[] Rotation, float[] Translation, float[] Scale) ToTrs()
    {
        int n = KeyCount * TrackCount;
        var r = new float[n * 4];
        var t = new float[n * 3];
        var s = new float[n * 3];
        for (int i = 0; i < n; i++)
        {
            int v = i * 9;
            var q = FromStereo(Values[v], Values[v + 1], Values[v + 2]);
            r[4 * i] = q.X; r[4 * i + 1] = q.Y; r[4 * i + 2] = q.Z; r[4 * i + 3] = q.W;
            for (int k = 0; k < 3; k++)
            {
                t[3 * i + k] = (float)Values[v + 3 + k];
                s[3 * i + k] = (float)Values[v + 6 + k];
            }
        }
        return (r, t, s);
    }

    /// <summary>Values of one exact decode: every key from its canonical row, the other stored copies kept apart.</summary>
    public static Anm2Clip FromPayload(Anm2Payload p)
    {
        var h = p.Header;
        int T = h.NumTracks, S = 9 * T, keys = h.FrameBound + 1;
        var values = new double[keys * S];
        var endCopies = new SortedDictionary<int, double[]>();
        var pads = new SortedDictionary<int, double[]>();
        var seamOwn = new List<(int Frame, double[] Row)>();   // a segment's own copy of the seam key
        var row = new double[S];
        int F = 0;
        for (int s = 0; s < p.Segments.Length; s++)
        {
            var seg = p.Segments[s];
            bool last = s == p.Segments.Length - 1;
            var (anim, cons) = Anm2Payload.StreamMap(seg.Flags);
            var constRow = new double[S];
            for (int c = 0; c < cons.Length; c++) constRow[cons[c]] = seg.Constants[c];
            int nA = seg.NumAnimated, nb = seg.Blocks;
            void Fill(int stored, Span<double> dst)
            {
                constRow.CopyTo(dst);
                int at = stored * nA;
                for (int a = 0; a < nA; a++) dst[anim[a]] = (double)seg.Codes[at + a] * seg.Scale[a] + seg.Bias[a];
            }
            int localEnd = last ? seg.Frames : seg.Frames - 1;
            for (int l = 0; l <= localEnd; l++) Fill(seg.RowOf(l), values.AsSpan((F + l) * S, S));
            // row 15 of every block that is followed by another block in this segment
            for (int b = 0; b + 1 < nb; b++)
            {
                Fill(16 * b + 15, row);
                int f = F + 15 * (b + 1);
                if (!row.AsSpan().SequenceEqual(values.AsSpan(f * S, S))) endCopies[f] = (double[])row.Clone();
            }
            int endRow = seg.RowOf(seg.Frames);
            var own = new double[S];
            Fill(endRow, own);
            if (!last) seamOwn.Add((F + seg.Frames, own));
            if (seg.HasPadRow)
            {
                Fill(endRow + 1, row);
                if (!row.AsSpan().SequenceEqual(own)) pads[F + seg.Frames] = (double[])row.Clone();
            }
            F += seg.Frames;
        }
        foreach (var (f, own) in seamOwn)
            if (!own.AsSpan().SequenceEqual(values.AsSpan(f * S, S))) endCopies[f] = own;
        return new Anm2Clip
        {
            Version = h.Version, TrackHashes = h.TrackHashes, PoseHashes = h.PoseHashes, FrameBound = h.FrameBound,
            TimeBound = h.TimeBound, VfrDen = h.VfrDen, Vfr = h.Vfr, HeaderPad = h.Pad, Values = values,
            EndCopies = endCopies, PadRows = pads, Source = p,
        };
    }

    /// <summary>Largest seam jump (metres, over translation streams) — a hard cut when well above quantisation.</summary>
    public double MaxSeamJump()
    {
        double m = 0;
        foreach (var (f, copy) in EndCopies)
            for (int t = 0; t < TrackCount; t++)
                for (int k = 3; k < 6; k++)
                    m = Math.Max(m, Math.Abs(copy[t * 9 + k] - Values[f * StreamCount + t * 9 + k]));
        return m;
    }
}
