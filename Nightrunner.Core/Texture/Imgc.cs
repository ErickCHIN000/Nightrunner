using System.Buffers.Binary;

namespace Nightrunner.Core.Texture;

public sealed class ImgcException(string message) : Exception(message);

public enum TextureType
{
    Texture2D = 0,
    Cube = 1,
    Volume = 2,
    Invalid = 3,
}

/// <summary>One (mip, face) surface inside the bitmap part.</summary>
/// <param name="Size">Tight bytes.</param>
/// <param name="PaddedSize">Bytes including the per-level padding (stock: padded to 16).</param>
public readonly record struct ImgcLevel(
    int Mip, int Face, int Width, int Height, int Depth, long Offset, long Size, long PaddedSize);

/// <summary>
/// The IMGC header carried by a texture's 0x20 part, and the layout of its 0x21 bitmap part.
/// Ported from nightrunner/texture/imgc.py.
/// </summary>
/// <remarks>
/// 80-byte fixed part; the stored part is (header_size + 15) &amp; ~15 bytes. Payload is mip-major: for each mip,
/// each face, a level padded to a 16-byte stride — except the third-party tight variant, which stores levels
/// back-to-back and is accepted only when the part size equals the tight sum.
/// </remarks>
public sealed class ImgcHeader
{
    public const uint Magic = 0x43474D49;    // 'IMGC' little-endian
    public const uint KnownVersion = 0x20191127;
    public const int FixedSize = 80;
    public const byte FlagNoBitmap = 0x02;   // bit 1: no bitmap; the extension holds a reference name
    public const int MaxMips = 63;
    public const int LevelPaddingStock = 16;
    public const int LevelPaddingTight = 0;

    public uint Version { get; init; } = KnownVersion;
    public int HeaderSize { get; init; } = FixedSize;
    public uint Flags { get; init; }

    /// <summary>The three float4 statistics exactly as stored (never interpreted, round-trips bit-exact).</summary>
    public byte[] StatsRaw { get; init; } = new byte[48];

    public int Width { get; init; }
    public int Height { get; init; }
    public int Depth { get; init; } = 1;
    public byte Reserved { get; init; }
    public byte Format { get; init; }
    public byte Packed { get; init; }        // type | mip_count << 2
    public ulong MipSplit { get; init; }
    public byte[] Extension { get; init; } = [];
    public byte[] Tail { get; init; } = [];

    public TextureType Type => (TextureType)(Packed & 3);
    public int MipCount => Packed >> 2;
    public bool IsCube => Type == TextureType.Cube;
    public bool IsVolume => Type == TextureType.Volume;
    public int Faces => IsCube ? 6 : 1;
    public bool HeaderOnly => (Flags & FlagNoBitmap) != 0;
    public string FormatName => ImgcFormats.Name(Format);
    public int StoredSize => (HeaderSize + 15) & ~15;

    /// <summary>NUL-terminated name in the extension of header-only records.</summary>
    public string? Reference
    {
        get
        {
            if (Extension.Length == 0) return null;
            int nul = Array.IndexOf(Extension, (byte)0);
            return System.Text.Encoding.UTF8.GetString(Extension, 0, nul < 0 ? Extension.Length : nul);
        }
    }

    public (float[] Min, float[] Max, float[] Mean) Stats()
    {
        float[] Read(int at)
        {
            var v = new float[4];
            for (int i = 0; i < 4; i++)
                v[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
                    StatsRaw.AsSpan(at + i * 4, 4)));
            return v;
        }
        return (Read(0), Read(16), Read(32));
    }

    /// <param name="strictLength">Refuse a part longer than the padded header (the prototype's default).</param>
    public static ImgcHeader Parse(ReadOnlySpan<byte> raw, bool strictLength = true)
    {
        if (raw.Length < FixedSize)
            throw new ImgcException($"IMGC header is {raw.Length} bytes, shorter than the 80-byte fixed part");
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(raw);
        if (magic != Magic)
            throw new ImgcException($"IMGC: bad magic 0x{magic:X8}");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]);
        if (version != KnownVersion)
            throw new ImgcException($"IMGC: version 0x{version:X8} (only 0x{KnownVersion:X8} is known)");
        int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw[8..]);
        if (headerSize < FixedSize)
            throw new ImgcException($"IMGC: header_size {headerSize} < {FixedSize}");
        int stored = (headerSize + 15) & ~15;
        if (strictLength ? raw.Length != stored : raw.Length < stored)
            throw new ImgcException($"IMGC: part is {raw.Length} bytes but header_size {headerSize} pads to {stored}");

        return new ImgcHeader
        {
            Version = version,
            HeaderSize = headerSize,
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(raw[12..]),
            StatsRaw = raw.Slice(16, 48).ToArray(),
            Width = BinaryPrimitives.ReadUInt16LittleEndian(raw[64..]),
            Height = BinaryPrimitives.ReadUInt16LittleEndian(raw[66..]),
            Depth = raw[68],
            Reserved = raw[69],
            Format = raw[70],
            Packed = raw[71],
            MipSplit = BinaryPrimitives.ReadUInt64LittleEndian(raw[72..]),
            Extension = raw[FixedSize..headerSize].ToArray(),
            Tail = raw[headerSize..stored].ToArray(),
        };
    }

    /// <summary>The checks the native reader applies before laying out a bitmap.</summary>
    public void CheckGeometry()
    {
        if (Width == 0 || Height == 0 || Depth == 0)
            throw new ImgcException($"IMGC: zero dimension {Width}x{Height}x{Depth}");
        if (MipCount == 0) throw new ImgcException("IMGC: mip count 0");
        if (Type == TextureType.Invalid) throw new ImgcException("IMGC: texture type 3 (invalid)");
        if (!ImgcFormats.IsKnown(Format))
            throw new ImgcException($"IMGC: IL format id {Format} is not in the native enum");
    }

    public static (int W, int H, int D) MipDims(int w, int h, int d, int mip) =>
        (Math.Max(1, w >> mip), Math.Max(1, h >> mip), Math.Max(1, d >> mip));

    /// <summary>Mip-major list of surfaces with their offsets inside the bitmap part.</summary>
    public List<ImgcLevel> LevelLayout(int levelPadding = LevelPaddingStock)
    {
        if (levelPadding is not (LevelPaddingStock or LevelPaddingTight))
            throw new ImgcException($"IMGC: level padding {levelPadding} (only {LevelPaddingStock} and {LevelPaddingTight} exist)");
        CheckGeometry();
        var levels = new List<ImgcLevel>(MipCount * Faces);
        long offset = 0;
        for (int mip = 0; mip < MipCount; mip++)
        {
            var (mw, mh, md) = MipDims(Width, Height, Depth, mip);
            long length = ImgcFormats.LevelBytes(Format, mw, mh, md);
            long stride = levelPadding > 0 ? (length + levelPadding - 1) / levelPadding * levelPadding : length;
            for (int face = 0; face < Faces; face++)
            {
                levels.Add(new ImgcLevel(mip, face, mw, mh, md, offset, length, stride));
                offset += stride;
            }
        }
        return levels;
    }

    public long PayloadSize(int levelPadding = LevelPaddingStock)
    {
        var levels = LevelLayout(levelPadding);
        return levels.Count == 0 ? 0 : levels[^1].Offset + levels[^1].PaddedSize;
    }

    /// <summary>
    /// Which padding reproduces <paramref name="bitmapSize"/>: 16 (stock, tried first) or 0 (the third-party tight
    /// layout). Never repairs or guesses a size.
    /// </summary>
    public int DetectLevelPadding(long bitmapSize)
    {
        long padded = PayloadSize(LevelPaddingStock);
        if (padded == bitmapSize) return LevelPaddingStock;
        long tight = PayloadSize(LevelPaddingTight);
        if (tight == bitmapSize) return LevelPaddingTight;
        throw new ImgcException(
            $"IMGC: computed payload {padded} bytes (padded) / {tight} bytes (tight) != bitmap part " +
            $"{bitmapSize} bytes ({Width}x{Height}x{Depth} {FormatName} {Type} mips={MipCount})");
    }

    /// <summary>Level layout validated against the real part size.</summary>
    public List<ImgcLevel> CheckPayload(long bitmapSize, int? levelPadding = null)
    {
        int padding = levelPadding ?? DetectLevelPadding(bitmapSize);
        var levels = LevelLayout(padding);
        long total = levels.Count == 0 ? 0 : levels[^1].Offset + levels[^1].PaddedSize;
        if (total != bitmapSize)
            throw new ImgcException($"IMGC: computed payload {total} bytes (level_padding {padding}) != bitmap " +
                                    $"part {bitmapSize} bytes");
        return levels;
    }

    public override string ToString() =>
        $"{Width}x{Height}{(Depth > 1 ? "x" + Depth : "")} {FormatName} {Type} mips={MipCount}";
}
