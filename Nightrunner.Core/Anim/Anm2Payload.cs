using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Nightrunner.Core.Anim;

/// <summary>
/// One 64 KiB payload segment exactly as stored: per-track flags, constants, per-stream bias/scale and the i16 codes
/// of every stored row (boundary copies and the pad row included).
/// </summary>
/// <remarks>
/// Rows are block-major: key block <c>b</c> holds rows <c>16b … 16b + RowsInBlock − 1</c>. Row <c>r</c> of block
/// <c>b</c> is local frame <c>15b + r</c>; row 15 of a full block repeats row 0 of the next block, and a partial last
/// block (k &lt; 15 intervals) stores k + 2 rows, the last a pad row after the final key.
/// </remarks>
public sealed class Anm2Segment
{
    /// <summary>Frame intervals in this segment (its <c>segFrames</c> entry).</summary>
    public required int Frames { get; init; }
    /// <summary>Per track: bit k &lt; 6 = stream k constant, bit 6 = all three scale streams constant, bit 7 = 1.</summary>
    public required byte[] Flags { get; init; }
    /// <summary>Constant streams, track-major, order rx ry rz tx ty tz sx sy sz.</summary>
    public required float[] Constants { get; init; }
    public required float[] Bias { get; init; }
    public required float[] Scale { get; init; }
    /// <summary><c>[row × NumAnimated + stream]</c>.</summary>
    public required short[] Codes { get; init; }
    /// <summary>
    /// The 6 bytes after the static block's five u16s, kept verbatim: zero in V3 clips, left-over bytes in the V2
    /// clips (<c>solid_head_empty</c> <c>C9 38 00 00 00 00</c>).
    /// </summary>
    public byte[] StaticPad { get; init; } = new byte[6];

    /// <summary>
    /// V0 block layout: a segment whose frames are a multiple of 15 stores one more key block (the last key again and
    /// a pad row), so it always has <c>frames / 15 + 1</c> blocks; V3/V2/V1 stop at <c>ceil(frames / 15)</c>.
    /// </summary>
    public bool TrailingBlock { get; init; }

    public int NumAnimated => Bias.Length;
    public int Blocks => BlocksFor(Frames, TrailingBlock);
    public int Rows => RowsFor(Frames, TrailingBlock);
    /// <summary>The last block stores a row after the final key (a partial block, or the V0 trailing block).</summary>
    public bool HasPadRow => TrailingBlock || Frames % 15 != 0;

    public static int BlocksFor(int frames, bool trailing = false) => trailing ? frames / 15 + 1 : (frames + 14) / 15;
    public static int RowsInBlock(int frames, int block) => Math.Min(16, frames - 15 * block + 2);
    public static int RowsFor(int frames, bool trailing = false)
    {
        int nb = BlocksFor(frames, trailing);
        return 16 * (nb - 1) + RowsInBlock(frames, nb - 1);
    }

    /// <summary>Stored row of local frame <paramref name="local"/> (the row where the frame is not a boundary copy).</summary>
    public static int RowOf(int frames, int local, bool trailing = false) =>
        !trailing && local == frames && frames % 15 == 0 ? 16 * (frames / 15 - 1) + 15 : 16 * (local / 15) + local % 15;

    public int RowOf(int local) => RowOf(Frames, local, TrailingBlock);

    public float Value(int row, int stream) => Codes[row * NumAnimated + stream] * Scale[stream] + Bias[stream];
}

/// <summary>A clip as stored: header plus decoded segments. <see cref="Encode"/> re-serialises it byte for byte.</summary>
/// <remarks>
/// Segment layout of V3/V2/V1 (all offsets relative to the segment, which starts at <c>payload + s × 0x10000</c>):
/// <code>
/// u16 off16[nblk + 2]            [static block, key block 0 … nblk−1, end] in 16-byte units; zero pad to 16
/// static: u16 nConst, nAnim, nStreams (= 9T), sbBytes (= ceil(nAnim/8)·64), skipAnim (= 0); 6 pad bytes (kept)
///         per 8-stream group: f32 bias[8], f32 scale[8]     (unused lanes 0)
///         f32 const[nConst]; u8 flags[T]; zero pad to 16
/// key block: per 8-stream group, u16 vectors of 8 lanes (lane j = stream 8g + j):
///         vector 0 lane j: bits 15..12 width code of row j, 11..8 width code of row 8 + j, 7..0 first data bits
///         then ceil((Σ width − 8) / 16) more vectors; each lane is one MSB-first bit string of the row deltas
///         (width code 15 = 16 bits; deltas sign-extended); unused lanes of the last group hold code −32768
/// x₀ = d₀, x₁ = x₀ + d₁, x_r = sat16(2x_{r−1} − x_{r−2}) + d_r  (16-bit wrapping add)
/// </code>
/// A segment that is not the last is zero-padded to 64 KiB. V0 keeps the static block in the header
/// (<see cref="Anm2Header.StaticBlock"/>, flags without bit 7) and its single payload block is
/// <c>u16 off16[nblk + 1]</c> [key block 0 … nblk−1, end] then the key blocks, with the trailing block of
/// <see cref="Anm2Segment.TrailingBlock"/>.
/// </remarks>
public sealed class Anm2Payload
{
    public required Anm2Header Header { get; init; }
    public required Anm2Segment[] Segments { get; init; }

    public int NumTracks => Header.NumTracks;

    /// <summary>Is stream <paramref name="k"/> (0..8) of a track with these flags constant?</summary>
    public static bool IsConstant(byte flags, int k) => ((flags >> (k < 6 ? k : 6)) & 1) != 0;

    /// <summary>Animated and constant streams of a segment in storage order, as <c>track × 9 + k</c>.</summary>
    public static (int[] Animated, int[] Constant) StreamMap(ReadOnlySpan<byte> flags)
    {
        var a = new List<int>(flags.Length * 9);
        var c = new List<int>(flags.Length * 9);
        for (int t = 0; t < flags.Length; t++)
            for (int k = 0; k < 9; k++)
                (IsConstant(flags[t], k) ? c : a).Add(t * 9 + k);
        return (a.ToArray(), c.ToArray());
    }

    // ---- decode ---------------------------------------------------------------------------------------------

    /// <summary>Decode a whole clip (plain 0x40 bytes, or 0x44 ‖ 0x45).</summary>
    public static Anm2Payload Decode(ReadOnlySpan<byte> clip)
    {
        var h = Anm2Header.Parse(clip);
        return Decode(h, clip.Slice(h.HeaderSize, (int)h.PayloadSize));
    }

    /// <summary>Decode the payload (0x45 part). Every segment must be consumed exactly, and the last must end at the payload end.</summary>
    public static Anm2Payload Decode(Anm2Header h, ReadOnlySpan<byte> payload)
    {
        if (payload.Length != h.PayloadSize) throw new Anm2FormatException($"payload is {payload.Length} bytes, header says {h.PayloadSize}");
        if (h.Version == 0) return new Anm2Payload { Header = h, Segments = [DecodeV0(h, payload)] };
        var segs = new Anm2Segment[h.NumBlocks];
        for (int s = 0; s < segs.Length; s++)
        {
            int start = s * Anm2Header.SegmentBytes;
            int len = Math.Min(Anm2Header.SegmentBytes, payload.Length - start);
            if (len <= 0) throw new Anm2FormatException($"segment {s} starts past the payload end");
            var seg = DecodeSegment(payload.Slice(start, len), h.NumTracks, h.SegmentFrames[s], s, out int end);
            if (s == segs.Length - 1 ? end != len : end > len)
                throw new Anm2FormatException($"segment {s} ends at {end}, {(s == segs.Length - 1 ? "payload segment is" : "segment limit is")} {len}");
            // V1 has no numStatic field
            if (h.Version != 1 && seg.Constants.Length != h.NumStatic)
                throw new Anm2FormatException($"segment {s} holds {seg.Constants.Length} constant streams, header numStatic is {h.NumStatic}");
            segs[s] = seg;
        }
        return new Anm2Payload { Header = h, Segments = segs };
    }

    private sealed record StaticPart(float[] Bias, float[] Scale, float[] Constants, byte[] Flags, byte[] Pad, int End);

    private static StaticPart ReadStatic(ReadOnlySpan<byte> S, int so, int tracks, string where, bool flagBit7)
    {
        if (so + 16 > S.Length) throw new Anm2FormatException($"{where}: static block at {so} past the end");
        int nConst = BinaryPrimitives.ReadUInt16LittleEndian(S[so..]);
        int nAnim = BinaryPrimitives.ReadUInt16LittleEndian(S[(so + 2)..]);
        int nStreams = BinaryPrimitives.ReadUInt16LittleEndian(S[(so + 4)..]);
        int sbBytes = BinaryPrimitives.ReadUInt16LittleEndian(S[(so + 6)..]);
        int skip = BinaryPrimitives.ReadUInt16LittleEndian(S[(so + 8)..]);
        if (nStreams != 9 * tracks) throw new Anm2FormatException($"{where}: nStreams {nStreams} != 9 × {tracks}");
        if (nConst + nAnim != nStreams) throw new Anm2FormatException($"{where}: nConst {nConst} + nAnim {nAnim} != nStreams {nStreams}");
        int ng = (nAnim + 7) / 8;
        if (sbBytes != ng * 64) throw new Anm2FormatException($"{where}: sbBytes {sbBytes} != ceil(nAnim/8)·64");
        if (skip != 0) throw new Anm2UnsupportedException($"{where}: skipAnim {skip} (only 0 is understood)");
        int bo = so + 16, co = bo + sbBytes, fo = co + 4 * nConst;
        int staticEnd = (fo + tracks + 15) & ~15;
        if (staticEnd > S.Length) throw new Anm2FormatException($"{where}: static block past the end");
        var bias = new float[nAnim];
        var scale = new float[nAnim];
        for (int a = 0; a < nAnim; a++)
        {
            int g = a >> 3, j = a & 7;
            bias[a] = BinaryPrimitives.ReadSingleLittleEndian(S[(bo + g * 64 + 4 * j)..]);
            scale[a] = BinaryPrimitives.ReadSingleLittleEndian(S[(bo + g * 64 + 32 + 4 * j)..]);
        }
        var constants = MemoryMarshal.Cast<byte, float>(S.Slice(co, 4 * nConst)).ToArray();
        var flags = S.Slice(fo, tracks).ToArray();
        int counted = 0;
        foreach (byte f in flags)
        {
            if (flagBit7 && (f & 0x80) == 0) throw new Anm2FormatException($"{where}: track flags 0x{f:X2} without bit 7");
            for (int k = 0; k < 9; k++) if (IsConstant(f, k)) counted++;
        }
        if (counted != nConst) throw new Anm2FormatException($"{where}: flags mark {counted} constant streams, nConst is {nConst}");
        return new StaticPart(bias, scale, constants, flags, S.Slice(so + 10, 6).ToArray(), staticEnd);
    }

    /// <summary>Decode the key blocks; block <c>b</c> starts at <paramref name="blockAt"/>[b] and must end at [b + 1].</summary>
    private static short[] ReadBlocks(ReadOnlySpan<byte> S, ReadOnlySpan<int> blockAt, int frames, bool trailing, int nAnim, string where)
    {
        int nb = Anm2Segment.BlocksFor(frames, trailing), ng = (nAnim + 7) / 8;
        var codes = new short[Anm2Segment.RowsFor(frames, trailing) * nAnim];
        Span<int> widths = stackalloc int[16];
        Span<short> xs = stackalloc short[16];
        for (int b = 0; b < nb; b++)
        {
            int o = blockAt[b];
            int nr = Anm2Segment.RowsInBlock(frames, b);
            int rowBase = 16 * b;
            for (int g = 0; g < ng; g++)
            {
                if (o < 0 || o + 16 > S.Length) throw new Anm2FormatException($"{where} block {b}: group {g} past the end");
                int tot = 0;
                for (int j = 0; j < 8; j++)
                {
                    int lane = BinaryPrimitives.ReadUInt16LittleEndian(S[(o + 2 * j)..]);
                    widths[j] = Width(lane >> 12);
                    widths[8 + j] = Width((lane >> 8) & 15);
                }
                for (int r = 0; r < nr; r++) tot += widths[r];
                int nw = tot > 8 ? (tot - 8 + 15) >> 4 : 0;
                int groupEnd = o + 16 * (1 + nw);
                if (groupEnd > S.Length) throw new Anm2FormatException($"{where} block {b}: group {g} bits past the end");
                int lanes = Math.Min(8, nAnim - 8 * g);
                for (int j = 0; j < lanes; j++)
                {
                    ulong acc = BinaryPrimitives.ReadUInt16LittleEndian(S[(o + 2 * j)..]) & 0xFFu;
                    int have = 8, word = 0;
                    for (int r = 0; r < nr; r++)
                    {
                        int w = widths[r];
                        int d = 0;
                        if (w != 0)
                        {
                            while (have < w)
                            {
                                acc = (acc << 16) | BinaryPrimitives.ReadUInt16LittleEndian(S[(o + 16 * (1 + word) + 2 * j)..]);
                                word++;
                                have += 16;
                            }
                            have -= w;
                            int v = (int)(acc >> have) & ((1 << w) - 1);
                            d = (v << (32 - w)) >> (32 - w);
                        }
                        int x = r switch
                        {
                            0 => d,
                            1 => xs[0] + d,
                            _ => Sat16(2 * xs[r - 1] - xs[r - 2]) + d,
                        };
                        xs[r] = (short)x;   // wrapping add
                    }
                    int stream = 8 * g + j;
                    for (int r = 0; r < nr; r++) codes[(rowBase + r) * nAnim + stream] = xs[r];
                }
                o = groupEnd;
            }
            if (o != blockAt[b + 1]) throw new Anm2FormatException($"{where} block {b}: bits end at {o}, offset table says {blockAt[b + 1]}");
        }
        return codes;
    }

    private static Anm2Segment DecodeSegment(ReadOnlySpan<byte> S, int tracks, int frames, int index, out int end)
    {
        string where = $"segment {index}";
        int nb = Anm2Segment.BlocksFor(frames);
        int tbl = 2 * (nb + 2);
        if (S.Length < tbl) throw new Anm2FormatException($"{where}: offset table past the end");
        var offs = new int[nb + 2];
        for (int i = 0; i < offs.Length; i++) offs[i] = BinaryPrimitives.ReadUInt16LittleEndian(S[(2 * i)..]) * 16;
        int so = offs[0];
        if (so < tbl) throw new Anm2FormatException($"{where}: static block at {so} overlaps the offset table");
        var st = ReadStatic(S, so, tracks, where, flagBit7: true);
        if (offs[1] != st.End) throw new Anm2FormatException($"{where}: key block 0 at {offs[1]}, static block ends at {st.End}");
        var codes = ReadBlocks(S, offs.AsSpan(1), frames, false, st.Bias.Length, where);
        end = offs[nb + 1];
        return new Anm2Segment
        {
            Frames = frames, Flags = st.Flags, Constants = st.Constants, Bias = st.Bias, Scale = st.Scale, Codes = codes, StaticPad = st.Pad,
        };
    }

    private static Anm2Segment DecodeV0(Anm2Header h, ReadOnlySpan<byte> payload)
    {
        int frames = h.SegmentFrames[0];
        var st = ReadStatic(h.StaticBlock, 0, h.NumTracks, "V0 static block", flagBit7: false);
        if (st.End != h.StaticBlock.Length)
            throw new Anm2FormatException($"V0 static block is {h.StaticBlock.Length} bytes, its layout ends at {st.End}");
        int nb = Anm2Segment.BlocksFor(frames, trailing: true);
        int tbl = 2 * (nb + 1);
        if (payload.Length < tbl) throw new Anm2FormatException("V0 payload: offset table past the end");
        var offs = new int[nb + 1];
        for (int i = 0; i < offs.Length; i++) offs[i] = BinaryPrimitives.ReadUInt16LittleEndian(payload[(2 * i)..]) * 16;
        if (offs[0] < tbl) throw new Anm2FormatException($"V0 payload: key block 0 at {offs[0]} overlaps the offset table");
        var codes = ReadBlocks(payload, offs, frames, true, st.Bias.Length, "V0 payload");
        if (offs[nb] != payload.Length) throw new Anm2FormatException($"V0 payload ends at {offs[nb]}, payload is {payload.Length} bytes");
        return new Anm2Segment
        {
            Frames = frames, Flags = st.Flags, Constants = st.Constants, Bias = st.Bias, Scale = st.Scale, Codes = codes,
            StaticPad = st.Pad, TrailingBlock = true,
        };
    }

    private static int Width(int code) => code == 15 ? 16 : code;
    private static int Sat16(int v) => v < short.MinValue ? short.MinValue : v > short.MaxValue ? short.MaxValue : v;

    // ---- encode ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Serialise to the plain 0x40 form (header ‖ payload) in the header's version; the header's sizes, counts and
    /// segment table are recomputed from the segments, its other fields (VFR, V0 rate word and trailer, reserved
    /// bytes, pads) kept. Split with <see cref="Anm2Resource.SplitStreamPair"/> for the 0x44 + 0x45 form.
    /// </summary>
    public byte[] Encode()
    {
        var h = Header;
        if (h.Version == 0) return EncodeV0();
        if (h.Version is not (1 or 2 or 3)) throw new Anm2UnsupportedException($"cannot write header version {h.Version}");
        if (h.Version == 1 && h.NumPose != 0) throw new Anm2FormatException("a V1 header has no pose list");
        var parts = new byte[Segments.Length][];
        for (int s = 0; s < Segments.Length; s++)
        {
            if (Segments[s].TrailingBlock) throw new Anm2FormatException($"segment {s}: the trailing key block is V0 only");
            parts[s] = EncodeSegment(Segments[s], NumTracks, s);
        }
        long payload = (long)Anm2Header.SegmentBytes * (Segments.Length - 1) + parts[^1].Length;
        var header = new Anm2Header
        {
            Version = h.Version, Payload16 = (uint)(payload / 16), TimeBound = h.TimeBound, FrameBound = h.FrameBound,
            NumStatic = (ushort)Segments[0].Constants.Length, TrackHashes = h.TrackHashes, PoseHashes = h.PoseHashes,
            SegmentFrames = Segments.Select(x => (ushort)x.Frames).ToArray(), VfrDen = h.VfrDen, Vfr = h.Vfr, Pad = h.Pad,
            Reserved = h.Version == 1 && h.Reserved.Length != 10 ? new byte[10] : h.Reserved,
            Header16 = Anm2Header.Header16For(h.Version, h.NumTracks, h.NumPose, Segments.Length, h.Vfr.Length),
        };
        var hb = header.Write();
        var outb = new byte[hb.Length + payload];
        hb.CopyTo(outb, 0);
        for (int s = 0; s < parts.Length; s++) parts[s].CopyTo(outb, hb.Length + (long)s * Anm2Header.SegmentBytes);
        return outb;
    }

    private byte[] EncodeV0()
    {
        var h = Header;
        if (Segments.Length != 1) throw new Anm2UnsupportedException($"cannot write a V0 clip with {Segments.Length} payload blocks");
        var seg = Segments[0];
        if (!seg.TrailingBlock) throw new Anm2FormatException("a V0 payload needs the trailing key block layout");
        int T = NumTracks;
        CheckSegment(seg, T, 0);
        var stat = new byte[StaticSize(seg.NumAnimated, seg.Constants.Length, T)];
        WriteStatic(stat, 0, seg);
        int size = PayloadSizeV0(seg, T);
        if (size > Anm2Header.SegmentBytes) throw new Anm2FormatException($"V0 payload is {size} bytes, over the 64 KiB block the engine reads");
        var payload = new byte[size];
        int nb = seg.Blocks;
        int o = WriteBlocks(payload, (2 * (nb + 1) + 15) & ~15, seg, (b, at) => BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2 * b), (ushort)(at / 16)));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2 * nb), (ushort)(o / 16));
        if (o != size) throw new InvalidOperationException($"V0 payload: wrote {o} bytes, sized {size}");
        var header = new Anm2Header
        {
            Version = 0, Payload16 = (uint)(size / 16), TimeBound = h.FrameBound, FrameBound = h.FrameBound,
            NumStatic = (ushort)seg.Constants.Length, TrackHashes = h.TrackHashes, SegmentFrames = [(ushort)seg.Frames], Vfr = [],
            Rate = h.Rate, StaticBlock = stat, Trailer = h.Trailer, Reserved = h.Reserved.Length == 4 ? h.Reserved : new byte[4], Pad = h.Pad,
            Header16 = Anm2Header.Header16ForV0(stat.Length, T, 1, h.Trailer.Length),
        };
        var hb = header.Write();
        var outb = new byte[hb.Length + payload.Length];
        hb.CopyTo(outb, 0);
        payload.CopyTo(outb, hb.Length);
        return outb;
    }

    /// <summary>Encoded size of a V3/V2/V1 segment in bytes, without building it.</summary>
    public static int SegmentSize(Anm2Segment seg, int tracks)
    {
        int size = StaticEnd(seg.Blocks, seg.NumAnimated, seg.Constants.Length, tracks);
        Span<int> widths = stackalloc int[16];
        for (int b = 0; b < seg.Blocks; b++) size += BlockSize(seg, b, widths);
        return size;
    }

    /// <summary>Encoded size of a V0 payload (offset table and key blocks; the static block is in the header).</summary>
    public static int PayloadSizeV0(Anm2Segment seg, int tracks)
    {
        int size = (2 * (seg.Blocks + 1) + 15) & ~15;
        Span<int> widths = stackalloc int[16];
        for (int b = 0; b < seg.Blocks; b++) size += BlockSize(seg, b, widths);
        return size;
    }

    internal static int StaticSize(int nAnim, int nConst, int tracks) => (16 + ((nAnim + 7) / 8) * 64 + 4 * nConst + tracks + 15) & ~15;

    internal static int StaticEnd(int nb, int nAnim, int nConst, int tracks) =>
        ((2 * (nb + 2) + 15) & ~15) + StaticSize(nAnim, nConst, tracks);

    /// <summary>Bytes of key block <paramref name="b"/> (all groups).</summary>
    internal static int BlockSize(Anm2Segment seg, int b, Span<int> widths)
    {
        int nA = seg.NumAnimated, ng = (nA + 7) / 8, nr = Anm2Segment.RowsInBlock(seg.Frames, b), size = 0;
        for (int g = 0; g < ng; g++)
        {
            GroupWidths(seg, b, g, nr, widths);
            int tot = 0;
            for (int r = 0; r < nr; r++) tot += widths[r];
            size += 16 * (1 + (tot > 8 ? (tot - 8 + 15) >> 4 : 0));
        }
        return size;
    }

    /// <summary>Delta of row r for one lane given its codes (inverse of the integrator, wrapping).</summary>
    private static int Delta(ReadOnlySpan<short> x, int r) => (short)(r switch
    {
        0 => x[0],
        1 => x[1] - x[0],
        _ => x[r] - Sat16(2 * x[r - 1] - x[r - 2]),
    });

    /// <summary>Minimum signed width of a delta; a needed 15 becomes 16 (width code 15 means 16 bits).</summary>
    private static int NeedWidth(int d)
    {
        if (d == 0) return 0;
        int m = d < 0 ? ~d : d;                       // bits needed = significant bits of m, plus the sign
        int w = 33 - int.LeadingZeroCount(m) - 0;     // LeadingZeroCount(0) = 32 → w = 1
        return w == 15 ? 16 : w;
    }

    /// <summary>Unused lanes of the last group hold the code −32768 on every row (so row 0 is 16 bits wide there).</summary>
    public const short UnusedLaneCode = short.MinValue;

    private static void LaneCodes(Anm2Segment seg, int rowBase, int nr, int stream, Span<short> x)
    {
        int nA = seg.NumAnimated;
        for (int r = 0; r < nr; r++) x[r] = stream < 0 ? UnusedLaneCode : seg.Codes[(rowBase + r) * nA + stream];
    }

    private static void GroupWidths(Anm2Segment seg, int b, int g, int nr, Span<int> widths)
    {
        widths.Clear();
        int nA = seg.NumAnimated, lanes = Math.Min(8, nA - 8 * g), rowBase = 16 * b;
        Span<short> x = stackalloc short[16];
        for (int j = 0; j < Math.Min(8, lanes + 1); j++)
        {
            LaneCodes(seg, rowBase, nr, j < lanes ? 8 * g + j : -1, x);
            for (int r = 0; r < nr; r++)
            {
                int w = NeedWidth(Delta(x, r));
                if (w > widths[r]) widths[r] = w;
            }
        }
    }

    private static void CheckSegment(Anm2Segment seg, int tracks, int index)
    {
        if (seg.Flags.Length != tracks) throw new Anm2FormatException($"segment {index}: {seg.Flags.Length} flags for {tracks} tracks");
        if (seg.Codes.Length != seg.Rows * seg.NumAnimated) throw new Anm2FormatException($"segment {index}: {seg.Codes.Length} codes, expected {seg.Rows} rows × {seg.NumAnimated}");
    }

    /// <summary>Static block at <paramref name="so"/>: counts, bias/scale groups, constants, flags (the zero pad is the buffer's).</summary>
    private static void WriteStatic(Span<byte> sp, int so, Anm2Segment seg)
    {
        int nA = seg.NumAnimated, ng = (nA + 7) / 8, nC = seg.Constants.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(sp[so..], (ushort)nC);
        BinaryPrimitives.WriteUInt16LittleEndian(sp[(so + 2)..], (ushort)nA);
        BinaryPrimitives.WriteUInt16LittleEndian(sp[(so + 4)..], (ushort)(nA + nC));
        BinaryPrimitives.WriteUInt16LittleEndian(sp[(so + 6)..], (ushort)(ng * 64));
        if (seg.StaticPad.Length == 6) seg.StaticPad.CopyTo(sp[(so + 10)..]);
        int bo = so + 16;
        for (int a = 0; a < nA; a++)
        {
            int g = a >> 3, j = a & 7;
            BinaryPrimitives.WriteSingleLittleEndian(sp[(bo + g * 64 + 4 * j)..], seg.Bias[a]);
            BinaryPrimitives.WriteSingleLittleEndian(sp[(bo + g * 64 + 32 + 4 * j)..], seg.Scale[a]);
        }
        int co = bo + ng * 64;
        MemoryMarshal.AsBytes(seg.Constants.AsSpan()).CopyTo(sp[co..]);
        seg.Flags.CopyTo(sp[(co + 4 * nC)..]);
    }

    /// <summary>Key blocks from <paramref name="o"/>; <paramref name="blockAt"/>(b, offset) records each block's start. Returns the end.</summary>
    private static int WriteBlocks(Span<byte> sp, int o, Anm2Segment seg, Action<int, int> blockAt)
    {
        int nb = seg.Blocks, nA = seg.NumAnimated, ng = (nA + 7) / 8;
        Span<int> widths = stackalloc int[16];
        Span<short> x = stackalloc short[16];
        for (int b = 0; b < nb; b++)
        {
            blockAt(b, o);
            int nr = Anm2Segment.RowsInBlock(seg.Frames, b), rowBase = 16 * b;
            for (int g = 0; g < ng; g++)
            {
                GroupWidths(seg, b, g, nr, widths);
                int tot = 0;
                for (int r = 0; r < nr; r++) tot += widths[r];
                int nw = tot > 8 ? (tot - 8 + 15) >> 4 : 0;
                int lanes = Math.Min(8, nA - 8 * g);
                for (int j = 0; j < 8; j++)
                {
                    int code0 = widths[j] == 16 ? 15 : widths[j];
                    int code8 = widths[8 + j] == 16 ? 15 : widths[8 + j];
                    // Bit string: first 8 bits into the header lane, then 16 per vector, MSB first.
                    ulong acc = 0;
                    int have = 0, word = -1;
                    int lane = (code0 << 12) | (code8 << 8);
                    LaneCodes(seg, rowBase, nr, j < lanes ? 8 * g + j : -1, x);
                    for (int r = 0; r <= nr; r++)
                    {
                        if (r < nr)
                        {
                            int w = widths[r];
                            if (w == 0) continue;
                            int d = Delta(x, r);
                            acc = (acc << w) | ((uint)d & ((1u << w) - 1));
                            have += w;
                        }
                        else if (have > 0)
                        {
                            // flush: pad the tail with zero bits up to the next unit
                            int unit = word < 0 ? 8 : 16;
                            acc <<= unit - have;
                            have = unit;
                        }
                        while (have >= (word < 0 ? 8 : 16))
                        {
                            int unit = word < 0 ? 8 : 16;
                            have -= unit;
                            int v = (int)(acc >> have) & ((1 << unit) - 1);
                            if (word < 0) lane |= v;
                            else BinaryPrimitives.WriteUInt16LittleEndian(sp[(o + 16 * (1 + word) + 2 * j)..], (ushort)v);
                            word++;
                        }
                    }
                    BinaryPrimitives.WriteUInt16LittleEndian(sp[(o + 2 * j)..], (ushort)lane);
                }
                o += 16 * (1 + nw);
            }
        }
        return o;
    }

    private static byte[] EncodeSegment(Anm2Segment seg, int tracks, int index)
    {
        CheckSegment(seg, tracks, index);
        int nb = seg.Blocks;
        int size = SegmentSize(seg, tracks);
        if (size > Anm2Header.SegmentBytes) throw new Anm2FormatException($"segment {index} is {size} bytes, over 64 KiB");
        var S = new byte[size];
        int so = (2 * (nb + 2) + 15) & ~15;
        BinaryPrimitives.WriteUInt16LittleEndian(S, (ushort)(so / 16));
        WriteStatic(S, so, seg);
        int o = WriteBlocks(S, StaticEnd(nb, seg.NumAnimated, seg.Constants.Length, tracks), seg,
                            (b, at) => BinaryPrimitives.WriteUInt16LittleEndian(S.AsSpan(2 * (1 + b)), (ushort)(at / 16)));
        BinaryPrimitives.WriteUInt16LittleEndian(S.AsSpan(2 * (1 + nb)), (ushort)(o / 16));
        if (o != size) throw new InvalidOperationException($"segment {index}: wrote {o} bytes, sized {size}");
        return S;
    }
}
