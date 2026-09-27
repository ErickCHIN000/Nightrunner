using System.Buffers.Binary;

namespace Nightrunner.Core.Anim;

/// <summary>Thrown when bytes are not a valid ANM2 clip (a load-time check failed or the payload does not parse).</summary>
public sealed class Anm2FormatException(string message) : Exception(message);

/// <summary>Thrown for an ANM2 form that exists but is not supported here (byte-swapped files, layouts no shipped clip uses).</summary>
public sealed class Anm2UnsupportedException(string message) : Exception(message);

/// <summary>
/// ANM2 clip header, versions 3, 2, 1 and 0 (resource 0x40, or the 0x44 part of a 0x44 + 0x45 stream pair).
/// </summary>
/// <remarks>
/// Versions 3 and 2:
/// <code>
/// +0x00 "ANM2"  +0x04 u16 endian (42)  +0x06 u16 version (3; 2 has no numPose and its hashes start at +0x1C)
/// +0x08 u32 payload16   +0x0C u16 header16   +0x0E u16 blockSize16 (0x1000)   +0x10 u16 numBlocks
/// +0x12 u16 timeBound   +0x14 u16 frameBound +0x16 u16 numVfr   +0x18 u16 numTracks   +0x1A u16 numStatic
/// +0x1C u32 numPose (V3 only)
/// u32 trackHash[numTracks] (list A) · u32 poseHash[numPose] (list B) · u16 segFrames[numBlocks]
/// u16 vfrDen · {u16 len, rate}[numVfr] · zero pad to 16
/// </code>
/// Version 1 (<c>Header_Version1</c>; 13 DL2 clips): the V3 payload (64 KiB segments), a shorter header —
/// <code>
/// +0x08 u16 numFrames (frameBound + 1)  +0x0A u16 numTracks  +0x0C u16 numBlocks  +0x0E u16 headerSize (bytes)
/// +0x10 u32 fileSize   +0x14 u16 numVfr   +0x16 10 bytes no header accessor, validator or byte-swap reads (zero; kept)
/// +0x20 u32 trackHash[numTracks] · u16 segFrames[numBlocks] · u16 vfrDen · {u16 len, rate}[numVfr] · pad to 16 (kept:
///       not always zero)
/// </code>
/// Version 0 (<c>Header_Version0</c>; the <c>*_demo</c> dev clips): the static block sits in the header and the
/// payload holds only key blocks —
/// <code>
/// +0x08 u16 rate word: the V0 sampler takes frame = time × (bit 15 ? bits 8..14 : low byte) / low byte (30 or 0xBC3C: × 1)
/// +0x0A u16 numFrames (frameBound + 1)   +0x0C u16 numFrames again (the sampler clamps to it)   +0x0E u16 numTracks
/// +0x10 u16 static block size   +0x12 u16 trailer size   +0x14 u16 numBlocks   +0x16 u16 header data end
/// +0x18 u32 fileSize   +0x1C u32 unread (zero; kept)
/// +0x20 static block (the V3 static block, flags without bit 7) · u32 trackHash[numTracks] · u16 segEnd[numBlocks]
///       (cumulative frames) · trailer (a NUL-terminated string in every shipped clip) · zero fill to 2 KiB
/// </code>
/// <see cref="Parse"/> runs the engine's load checks (<c>anim_Anm2HdrV3_Validate</c> and the V2/V1/V0 validators
/// in <c>engine_x64_rwdi.dll</c>) plus the layout checks the stored sizes imply; the payload is not checked there,
/// so <see cref="Anm2Payload.Decode"/> checks it instead.
/// </remarks>
public sealed class Anm2Header
{
    public const uint Magic = 0x324D4E41;   // 'ANM2' little-endian
    public const ushort Endian = 42;
    public const ushort BlockSize16 = 0x1000;
    public const int SegmentBytes = BlockSize16 * 16;   // 64 KiB
    /// <summary>A V0 header is filled to a multiple of this (the engine rounds header data end up to it).</summary>
    public const int V0HeaderAlign = 0x800;

    public ushort Version { get; init; } = 3;
    public uint Payload16 { get; init; }
    public ushort Header16 { get; init; }
    public ushort TimeBound { get; init; }
    public ushort FrameBound { get; init; }
    public ushort NumStatic { get; init; }
    public required uint[] TrackHashes { get; init; }
    public uint[] PoseHashes { get; init; } = [];
    /// <summary>Frame intervals per payload block (V0 stores them cumulatively; this is per block for every version).</summary>
    public required ushort[] SegmentFrames { get; init; }
    public ushort VfrDen { get; init; } = 1;
    /// <summary>The VFR table (V3/V2/V1; empty for V0, which has none).</summary>
    public required (ushort Len, ushort Rate)[] Vfr { get; init; }
    /// <summary>The pad after the laid-out header, kept verbatim (V3/V2: zero in every shipped clip; V1: sometimes not; V0: the zero fill to 2 KiB).</summary>
    public byte[] Pad { get; init; } = [];
    /// <summary>V0: the rate word at +0x08 (time → frame factor <c>(bit 15 ? bits 8..14 : low byte) / low byte</c>; 1 in every shipped clip).</summary>
    public ushort Rate { get; init; }
    /// <summary>V0: the static block at +0x20, as stored (decoded by <see cref="Anm2Payload"/>).</summary>
    public byte[] StaticBlock { get; init; } = [];
    /// <summary>V0: the bytes after the segment-end table up to the header data end.</summary>
    public byte[] Trailer { get; init; } = [];
    /// <summary>Fixed-header bytes no engine header code reads: V0 +0x1C..0x20, V1 +0x16..0x20 (zero in every shipped clip). Kept verbatim.</summary>
    public byte[] Reserved { get; init; } = [];

    public int NumTracks => TrackHashes.Length;
    public int NumPose => PoseHashes.Length;
    public int NumBlocks => SegmentFrames.Length;
    public int HeaderSize => Header16 * 16;
    public long PayloadSize => (long)Payload16 * 16;
    public int FixedSize => Version == 2 ? 0x1C : 0x20;
    public bool IsPoseWeights => PoseHashes.Length > 0;
    /// <summary>The VFR table is the identity every shipped clip carries: (1; (frameBound, 1)).</summary>
    public bool IdentityVfr => VfrDen == 1 && Vfr.Length == 1 && Vfr[0].Len == FrameBound && Vfr[0].Rate == 1;

    /// <summary>Header bytes up to and including the VFR table (V0: the trailer), before the pad.</summary>
    public int LayoutEnd => Version == 0
        ? 0x20 + StaticBlock.Length + 4 * NumTracks + 2 * NumBlocks + Trailer.Length
        : FixedSize + 4 * NumTracks + 4 * NumPose + 2 * NumBlocks + 2 + 4 * Vfr.Length;

    /// <summary>Version word of a buffer that starts with the ANM2 magic, or −1.</summary>
    public static int PeekVersion(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic
            ? BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) : -1;

    /// <summary>
    /// Parse and validate. <paramref name="data"/> is the whole clip (plain 0x40) or just the 0x44 part; with
    /// <paramref name="payloadLength"/> (the 0x45 part size) the size check is made against header + that part.
    /// </summary>
    public static Anm2Header Parse(ReadOnlySpan<byte> data, long? payloadLength = null)
    {
        if (data.Length < 0x1C) throw new Anm2FormatException($"{data.Length} bytes is shorter than an ANM2 header");
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
            throw new Anm2FormatException($"bad magic 0x{BinaryPrimitives.ReadUInt32LittleEndian(data):X8} (expected 'ANM2')");
        ushort endian = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if (endian != Endian)
            throw endian == 0x2A00
                ? new Anm2UnsupportedException("byte-swapped ANM2 (endian 0x2A00) is not supported")
                : new Anm2FormatException($"endian word {endian} (expected 42)");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        return version switch
        {
            0 => ParseV0(data, payloadLength),
            1 => ParseV1(data, payloadLength),
            2 or 3 => ParseV23(data, version, payloadLength),
            _ => throw new Anm2UnsupportedException($"ANM2 header version {version} is not supported"),
        };
    }

    private static Anm2Header ParseV23(ReadOnlySpan<byte> data, ushort version, long? payloadLength)
    {
        int fixedSize = version == 2 ? 0x1C : 0x20;
        if (data.Length < fixedSize) throw new Anm2FormatException($"{data.Length} bytes is shorter than a V{version} header");

        uint payload16 = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        ushort header16 = BinaryPrimitives.ReadUInt16LittleEndian(data[0x0C..]);
        ushort blockSize16 = BinaryPrimitives.ReadUInt16LittleEndian(data[0x0E..]);
        ushort numBlocks = BinaryPrimitives.ReadUInt16LittleEndian(data[0x10..]);
        ushort timeBound = BinaryPrimitives.ReadUInt16LittleEndian(data[0x12..]);
        ushort frameBound = BinaryPrimitives.ReadUInt16LittleEndian(data[0x14..]);
        ushort numVfr = BinaryPrimitives.ReadUInt16LittleEndian(data[0x16..]);
        ushort numTracks = BinaryPrimitives.ReadUInt16LittleEndian(data[0x18..]);
        ushort numStatic = BinaryPrimitives.ReadUInt16LittleEndian(data[0x1A..]);
        uint numPose = version == 3 ? BinaryPrimitives.ReadUInt32LittleEndian(data[0x1C..]) : 0;

        if (blockSize16 != BlockSize16) throw new Anm2FormatException($"blockSize16 0x{blockSize16:X} (must be 0x1000)");
        if (payload16 == 0) throw new Anm2FormatException("payload16 is 0");
        if (numBlocks != (payload16 + BlockSize16 - 1) / BlockSize16)
            throw new Anm2FormatException($"numBlocks {numBlocks} != ceil(payload16 {payload16} / 0x1000)");
        if (timeBound == 0 || frameBound == 0) throw new Anm2FormatException($"zero bound (timeBound {timeBound}, frameBound {frameBound})");
        if (numTracks == 0) throw new Anm2FormatException("numTracks is 0");
        if (numVfr == 0) throw new Anm2FormatException("numVfr is 0");
        if (numStatic > 9 * numTracks) throw new Anm2FormatException($"numStatic {numStatic} > 9 × numTracks {numTracks}");
        if (numPose != 0 && !(numPose > 9L * numTracks - 9 && numPose <= 9L * numTracks))
            throw new Anm2FormatException($"numPose {numPose} is outside (9T−9, 9T] for T = {numTracks}");

        long layoutEnd = fixedSize + 4L * numTracks + 4L * numPose + 2L * numBlocks + 2 + 4L * numVfr;
        long padded = (layoutEnd + 15) & ~15L;
        if (header16 * 16L != padded)
            throw new Anm2FormatException($"header16 {header16} × 16 != laid-out header size {padded}");
        if (data.Length < padded) throw new Anm2FormatException($"{data.Length} bytes is shorter than the {padded}-byte header");

        int at = fixedSize;
        var tracks = ReadU32s(data, ref at, numTracks);
        var poses = ReadU32s(data, ref at, (int)numPose);
        var seg = ReadSegFrames(data, ref at, numBlocks, frameBound);
        var (vfrDen, vfr) = ReadVfr(data, ref at, numVfr);
        long domain = 0, codomain = 0;
        foreach (var (len, rate) in vfr) { domain += len; codomain += (long)len * rate; }
        // VFR rule (inferred from the validator's description and the identity table every clip carries):
        // the lengths cover the time domain, and length × rate / den covers the frame codomain.
        if (vfrDen == 0 || domain != timeBound || codomain != (long)frameBound * vfrDen)
            throw new Anm2FormatException($"VFR table (den {vfrDen}, Σlen {domain}, Σlen·rate {codomain}) does not map timeBound {timeBound} onto frameBound {frameBound}");

        long total = padded + payload16 * 16L;
        long have = payloadLength is { } pl ? padded + pl : data.Length;
        if (have < total) throw new Anm2FormatException($"clip is {have} bytes, header + payload need {total}");

        return new Anm2Header
        {
            Version = version, Payload16 = payload16, Header16 = header16, TimeBound = timeBound, FrameBound = frameBound,
            NumStatic = numStatic, TrackHashes = tracks, PoseHashes = poses, SegmentFrames = seg, VfrDen = vfrDen, Vfr = vfr,
            Pad = data[(int)layoutEnd..(int)padded].ToArray(),
        };
    }

    /// <summary>
    /// Header_Version1: the engine's checks (numFrames, numTracks, numBlocks non-zero; numBlocks = ceil((fileSize −
    /// headerSize) / 64 KiB); Σ segFrames ≥ numFrames − 1; numVfr non-zero; Σ len·rate / den = numFrames − 1), plus
    /// Σ segFrames = numFrames − 1 (the engine only bounds it; every shipped clip is exact) and the stored header
    /// size = the laid-out size.
    /// </summary>
    private static Anm2Header ParseV1(ReadOnlySpan<byte> data, long? payloadLength)
    {
        if (data.Length < 0x20) throw new Anm2FormatException($"{data.Length} bytes is shorter than a V1 header");
        ushort numFrames = BinaryPrimitives.ReadUInt16LittleEndian(data[0x08..]);
        ushort numTracks = BinaryPrimitives.ReadUInt16LittleEndian(data[0x0A..]);
        ushort numBlocks = BinaryPrimitives.ReadUInt16LittleEndian(data[0x0C..]);
        ushort headerSize = BinaryPrimitives.ReadUInt16LittleEndian(data[0x0E..]);
        uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(data[0x10..]);
        ushort numVfr = BinaryPrimitives.ReadUInt16LittleEndian(data[0x14..]);

        if (numFrames == 0) throw new Anm2FormatException("V1: numFrames is 0");
        if (numTracks == 0) throw new Anm2FormatException("V1: numTracks is 0");
        if (numBlocks == 0) throw new Anm2FormatException("V1: numBlocks is 0");
        if (fileSize < headerSize || numBlocks != (fileSize - headerSize + 0xFFFFL) >> 16)
            throw new Anm2FormatException($"V1: numBlocks {numBlocks} != ceil((fileSize {fileSize} − headerSize {headerSize}) / 64 KiB)");
        if (numVfr == 0) throw new Anm2FormatException("V1: numVfr is 0");
        if (numFrames == 1) throw new Anm2UnsupportedException("V1 clip with one frame (no shipped clip has one)");

        long layoutEnd = 0x20 + 4L * numTracks + 2L * numBlocks + 2 + 4L * numVfr;
        long padded = (layoutEnd + 15) & ~15L;
        if (headerSize != padded) throw new Anm2FormatException($"V1: headerSize {headerSize} != laid-out header size {padded}");
        if (data.Length < padded) throw new Anm2FormatException($"{data.Length} bytes is shorter than the {padded}-byte header");
        if ((fileSize - headerSize) % 16 != 0) throw new Anm2FormatException($"V1: payload of {fileSize - headerSize} bytes is not a multiple of 16");

        int at = 0x20;
        var tracks = ReadU32s(data, ref at, numTracks);
        var seg = ReadSegFrames(data, ref at, numBlocks, numFrames - 1);
        var (vfrDen, vfr) = ReadVfr(data, ref at, numVfr);
        long domain = 0, codomain = 0;
        foreach (var (len, rate) in vfr) { domain += len; codomain += (long)len * rate; }
        if (vfrDen == 0 || domain == 0 || domain > ushort.MaxValue || codomain != (long)(numFrames - 1) * vfrDen)
            throw new Anm2FormatException($"V1: VFR table (den {vfrDen}, Σlen {domain}, Σlen·rate {codomain}) does not end at frame {numFrames - 1}");

        long have = payloadLength is { } pl ? padded + pl : data.Length;
        if (have < fileSize) throw new Anm2FormatException($"clip is {have} bytes, V1 fileSize is {fileSize}");

        return new Anm2Header
        {
            Version = 1, Payload16 = (fileSize - headerSize) / 16, Header16 = (ushort)(headerSize / 16), TimeBound = (ushort)domain,
            FrameBound = (ushort)(numFrames - 1), TrackHashes = tracks, SegmentFrames = seg, VfrDen = vfrDen, Vfr = vfr,
            Reserved = data[0x16..0x20].ToArray(), Pad = data[(int)layoutEnd..(int)padded].ToArray(),
        };
    }

    /// <summary>
    /// Header_Version0: the engine checks only nConst + nAnim = 9 × numTracks. Here also: the stored sizes match the
    /// layout (static block size, trailer size, header data end, fileSize), and the shapes the shipped clips have —
    /// one payload block, both frame counts equal and Σ segEnd = numFrames − 1 — anything else is refused by name.
    /// </summary>
    private static Anm2Header ParseV0(ReadOnlySpan<byte> data, long? payloadLength)
    {
        if (data.Length < 0x30) throw new Anm2FormatException($"{data.Length} bytes is shorter than a V0 header");
        ushort rate = BinaryPrimitives.ReadUInt16LittleEndian(data[0x08..]);
        ushort numFrames = BinaryPrimitives.ReadUInt16LittleEndian(data[0x0A..]);
        ushort numFrames2 = BinaryPrimitives.ReadUInt16LittleEndian(data[0x0C..]);
        ushort numTracks = BinaryPrimitives.ReadUInt16LittleEndian(data[0x0E..]);
        ushort staticSize = BinaryPrimitives.ReadUInt16LittleEndian(data[0x10..]);
        ushort trailerSize = BinaryPrimitives.ReadUInt16LittleEndian(data[0x12..]);
        ushort numBlocks = BinaryPrimitives.ReadUInt16LittleEndian(data[0x14..]);
        ushort dataEnd = BinaryPrimitives.ReadUInt16LittleEndian(data[0x16..]);
        uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(data[0x18..]);
        int nConst = BinaryPrimitives.ReadUInt16LittleEndian(data[0x20..]);
        int nAnim = BinaryPrimitives.ReadUInt16LittleEndian(data[0x22..]);

        if (nConst + nAnim != 9 * numTracks)
            throw new Anm2FormatException($"V0: nConst {nConst} + nAnim {nAnim} != 9 × numTracks {numTracks}");
        if (numTracks == 0) throw new Anm2FormatException("V0: numTracks is 0");
        if (numBlocks != 1) throw new Anm2UnsupportedException($"V0 clip with {numBlocks} payload blocks (every shipped V0 clip has 1)");
        if (numFrames < 2) throw new Anm2UnsupportedException($"V0 clip with {numFrames} frames");
        if (numFrames2 != numFrames) throw new Anm2UnsupportedException($"V0 clip whose second frame count {numFrames2} differs from numFrames {numFrames}");
        long layoutEnd = 0x20L + staticSize + 4L * numTracks + 2L * numBlocks + trailerSize;
        if (dataEnd != layoutEnd)
            throw new Anm2FormatException($"V0: header data end {dataEnd} != 0x20 + static {staticSize} + hashes + segEnd + trailer {trailerSize} = {layoutEnd}");
        int headerSize = (dataEnd + V0HeaderAlign - 1) & ~(V0HeaderAlign - 1);
        if (data.Length < headerSize) throw new Anm2FormatException($"{data.Length} bytes is shorter than the {headerSize}-byte V0 header");
        if (fileSize <= headerSize || (fileSize - headerSize) % 16 != 0)
            throw new Anm2FormatException($"V0: fileSize {fileSize} leaves no 16-byte-aligned payload after the {headerSize}-byte header");

        int at = 0x20 + staticSize;
        var tracks = ReadU32s(data, ref at, numTracks);
        var ends = new ushort[numBlocks];
        var seg = new ushort[numBlocks];
        int prev = 0;
        for (int i = 0; i < numBlocks; i++, at += 2)
        {
            ends[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
            if (ends[i] <= prev) throw new Anm2FormatException($"V0: segment end {ends[i]} does not follow {prev}");
            seg[i] = (ushort)(ends[i] - prev);
            prev = ends[i];
        }
        if (prev != numFrames - 1) throw new Anm2UnsupportedException($"V0 clip whose last segment end {prev} is not numFrames − 1 = {numFrames - 1}");

        long have = payloadLength is { } pl ? headerSize + pl : data.Length;
        if (have < fileSize) throw new Anm2FormatException($"clip is {have} bytes, V0 fileSize is {fileSize}");

        return new Anm2Header
        {
            Version = 0, Payload16 = (uint)(fileSize - headerSize) / 16, Header16 = (ushort)(headerSize / 16),
            TimeBound = (ushort)(numFrames - 1), FrameBound = (ushort)(numFrames - 1), NumStatic = (ushort)nConst,
            TrackHashes = tracks, SegmentFrames = seg, Vfr = [], Rate = rate,
            StaticBlock = data.Slice(0x20, staticSize).ToArray(), Trailer = data.Slice(at, trailerSize).ToArray(),
            Reserved = data[0x1C..0x20].ToArray(), Pad = data[dataEnd..headerSize].ToArray(),
        };
    }

    private static uint[] ReadU32s(ReadOnlySpan<byte> data, ref int at, int n)
    {
        var a = new uint[n];
        for (int i = 0; i < n; i++, at += 4) a[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
        return a;
    }

    private static ushort[] ReadSegFrames(ReadOnlySpan<byte> data, ref int at, int n, int frameBound)
    {
        var seg = new ushort[n];
        long sum = 0;
        for (int i = 0; i < seg.Length; i++, at += 2)
        {
            seg[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
            if (seg[i] == 0) throw new Anm2FormatException($"segment {i} has 0 frames");
            sum += seg[i];
        }
        if (sum != frameBound) throw new Anm2FormatException($"Σ segFrames {sum} != frameBound {frameBound}");
        return seg;
    }

    private static (ushort Den, (ushort, ushort)[] Vfr) ReadVfr(ReadOnlySpan<byte> data, ref int at, int n)
    {
        ushort den = BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
        at += 2;
        var vfr = new (ushort, ushort)[n];
        for (int i = 0; i < vfr.Length; i++, at += 4)
            vfr[i] = (BinaryPrimitives.ReadUInt16LittleEndian(data[at..]), BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 2)..]));
        return (den, vfr);
    }

    /// <summary>Serialise (pad kept when it has the right length, else zeros). <see cref="Header16"/> must match the laid-out size.</summary>
    public byte[] Write()
    {
        int size = Version == 0 ? (LayoutEnd + V0HeaderAlign - 1) & ~(V0HeaderAlign - 1) : (LayoutEnd + 15) & ~15;
        if (Header16 * 16 != size) throw new Anm2FormatException($"header16 {Header16} does not match the laid-out size {size}");
        var b = new byte[size];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], Endian);
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], Version);
        int at;
        switch (Version)
        {
            case 0:
                if (NumBlocks != 1) throw new Anm2UnsupportedException($"cannot write a V0 header with {NumBlocks} payload blocks");
                if (Reserved.Length != 4) throw new Anm2FormatException("a V0 header needs its 4 reserved bytes");
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x08..], Rate);
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x0A..], (ushort)(FrameBound + 1));
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x0C..], (ushort)(FrameBound + 1));
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x0E..], (ushort)NumTracks);
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x10..], (ushort)StaticBlock.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x12..], (ushort)Trailer.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x14..], (ushort)NumBlocks);
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x16..], (ushort)LayoutEnd);
                BinaryPrimitives.WriteUInt32LittleEndian(s[0x18..], (uint)(size + PayloadSize));
                Reserved.CopyTo(s[0x1C..]);
                StaticBlock.CopyTo(s[0x20..]);
                at = 0x20 + StaticBlock.Length;
                foreach (uint h in TrackHashes) { BinaryPrimitives.WriteUInt32LittleEndian(s[at..], h); at += 4; }
                int end = 0;
                foreach (ushort f in SegmentFrames) { end += f; BinaryPrimitives.WriteUInt16LittleEndian(s[at..], (ushort)end); at += 2; }
                Trailer.CopyTo(s[at..]);
                at += Trailer.Length;
                break;
            case 1:
                if (Reserved.Length != 10) throw new Anm2FormatException("a V1 header needs its 10 reserved bytes");
                if (NumPose != 0) throw new Anm2FormatException("a V1 header has no pose list");
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x08..], (ushort)(FrameBound + 1));
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x0A..], (ushort)NumTracks);
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x0C..], (ushort)NumBlocks);
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x0E..], (ushort)size);
                BinaryPrimitives.WriteUInt32LittleEndian(s[0x10..], (uint)(size + PayloadSize));
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x14..], (ushort)Vfr.Length);
                Reserved.CopyTo(s[0x16..]);
                at = 0x20;
                foreach (uint h in TrackHashes) { BinaryPrimitives.WriteUInt32LittleEndian(s[at..], h); at += 4; }
                at = WriteSegAndVfr(s, at);
                break;
            case 2 or 3:
                BinaryPrimitives.WriteUInt32LittleEndian(s[8..], Payload16);
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x0C..], Header16);
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x0E..], BlockSize16);
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x10..], (ushort)NumBlocks);
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x12..], TimeBound);
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x14..], FrameBound);
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x16..], (ushort)Vfr.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x18..], (ushort)NumTracks);
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x1A..], NumStatic);
                if (Version == 3) BinaryPrimitives.WriteUInt32LittleEndian(s[0x1C..], (uint)NumPose);
                else if (NumPose != 0) throw new Anm2FormatException("a V2 header has no pose list");
                at = FixedSize;
                foreach (uint h in TrackHashes) { BinaryPrimitives.WriteUInt32LittleEndian(s[at..], h); at += 4; }
                foreach (uint h in PoseHashes) { BinaryPrimitives.WriteUInt32LittleEndian(s[at..], h); at += 4; }
                at = WriteSegAndVfr(s, at);
                break;
            default:
                throw new Anm2UnsupportedException($"cannot write header version {Version}");
        }
        if (Pad.Length == size - at) Pad.CopyTo(s[at..]);
        return b;
    }

    private int WriteSegAndVfr(Span<byte> s, int at)
    {
        foreach (ushort f in SegmentFrames) { BinaryPrimitives.WriteUInt16LittleEndian(s[at..], f); at += 2; }
        BinaryPrimitives.WriteUInt16LittleEndian(s[at..], VfrDen);
        at += 2;
        foreach (var (len, rate) in Vfr)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(s[at..], len);
            BinaryPrimitives.WriteUInt16LittleEndian(s[(at + 2)..], rate);
            at += 4;
        }
        return at;
    }

    /// <summary>Header16 for a V3/V2/V1 header with these counts (V1 has no pose list).</summary>
    public static ushort Header16For(int version, int tracks, int poses, int blocks, int vfr) =>
        (ushort)(((version == 2 ? 0x1C : 0x20) + 4 * tracks + 4 * poses + 2 * blocks + 2 + 4 * vfr + 15) / 16);

    /// <summary>Header16 of a V0 header: static block, hashes, segment ends and trailer, filled to 2 KiB.</summary>
    public static ushort Header16ForV0(int staticBytes, int tracks, int blocks, int trailer) =>
        (ushort)(((0x20 + staticBytes + 4 * tracks + 2 * blocks + trailer + V0HeaderAlign - 1) & ~(V0HeaderAlign - 1)) / 16);
}
