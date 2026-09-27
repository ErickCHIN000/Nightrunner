using System.Buffers.Binary;

namespace Nightrunner.Core.Texture;

/// <summary>A parsed DDS header, in IMGC terms (the prototype's <c>DdsFile</c>).</summary>
/// <param name="IdentifiedBy">"dx10", "fourcc", "d3dfmt" or "masks".</param>
public sealed record DdsFile(int Width, int Height, int Depth, int MipCount, TextureType Type, byte Format, int? Dxgi,
                             int DataOffset, string IdentifiedBy, int AlphaMode, IReadOnlyList<string> Warnings)
{
    public int Faces => Type == TextureType.Cube ? 6 : 1;
    public string FormatName => ImgcFormats.Name(Format);

    /// <summary>An IMGC header with this geometry and format (nothing else set) — for level maths.</summary>
    public ImgcHeader Geometry() => new()
    {
        Width = Width, Height = Height, Depth = Depth, Format = Format,
        Packed = (byte)((int)Type | (MipCount << 2)),
    };

    /// <summary>Surfaces in file order (face-major), offsets relative to the texel data, tight.</summary>
    public List<ImgcLevel> FileLevels()
    {
        var levels = new List<ImgcLevel>();
        long offset = 0;
        foreach (var l in DdsWriter.FaceMajor(Geometry().LevelLayout(ImgcHeader.LevelPaddingTight)))
        {
            levels.Add(l with { Offset = offset, PaddedSize = l.Size });
            offset += l.Size;
        }
        return levels;
    }

    public override string ToString() =>
        $"{Width}x{Height}{(Depth > 1 ? "x" + Depth : "")} {FormatName} {Type} mips={MipCount} ({IdentifiedBy})";
}

/// <summary>
/// DDS → IMGC geometry, format and texels: the reader half of the prototype's <c>texture/dds.py</c>, ported rule for
/// rule. DX10 headers, legacy FourCC (DXT1/3/5, ATI1/2, BC4U/S, BC5U/S), the numeric D3DFMT codes and legacy masks
/// that name exactly one IL layout are accepted; texels must be tight and exactly the sum of the level sizes long.
/// Anything else is refused by name, never repaired.
/// </summary>
public static class DdsReader
{
    private const int LegacyDataOffset = 128, Dx10DataOffset = 148;
    private const uint DdsdMipMapCount = 0x20000, DdsdDepth = 0x800000;
    private const uint Caps2Cube = 0x200, Caps2AllFaces = 0xFC00, Caps2Volume = 0x200000;
    private const uint DdpfFourCC = 0x4, DdpfRgb = 0x40, DdpfLuminance = 0x20000, DdpfAlpha = 0x2;

    /// <summary>DXGI → IL: the first IL id (in enum order) that maps to each DXGI format.</summary>
    public static readonly IReadOnlyDictionary<int, byte> DxgiToIl = DdsWriter.Dxgi
        .OrderBy(kv => kv.Key)
        .GroupBy(kv => kv.Value)
        .ToDictionary(g => g.Key, g => g.First().Key);

    private static readonly Dictionary<string, byte> FourCC = new()
    {
        ["DXT1"] = 59, ["DXT3"] = 60, ["DXT5"] = 61,
        ["ATI1"] = 63, ["BC4U"] = 63, ["BC4S"] = 62,
        ["ATI2"] = 65, ["BC5U"] = 65, ["BC5S"] = 64,
    };

    private static readonly Dictionary<uint, byte> D3dFormat = new()
    {
        [36] = 47, [110] = 48, [111] = 6, [112] = 19, [113] = 46, [114] = 12, [115] = 24, [116] = 51,
    };

    private static readonly Dictionary<(uint Bits, uint R, uint G, uint B, uint A), byte> Masks = new()
    {
        [(32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000)] = 32,
        [(32, 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000)] = 38,
        [(32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0x00000000)] = 34,
        [(16, 0x000000FF, 0x0000FF00, 0x00000000, 0x00000000)] = 15,
        [(8, 0x000000FF, 0x00000000, 0x00000000, 0x00000000)] = 0,
        [(16, 0x0000FFFF, 0x00000000, 0x00000000, 0x00000000)] = 7,
        [(24, 0x00FF0000, 0x0000FF00, 0x000000FF, 0x00000000)] = 29,
        [(16, 0x0000F800, 0x000007E0, 0x0000001F, 0x00000000)] = 27,
    };

    /// <summary>Parse and validate a whole DDS file held in memory.</summary>
    /// <param name="srgbToLinear">Map an sRGB DXGI format onto the linear IL id with the same payload.</param>
    /// <param name="allowUnobserved">Accept IL ids that never occur in the shipped corpus (tier C).</param>
    public static DdsFile Read(ReadOnlySpan<byte> file, bool srgbToLinear = false, bool allowUnobserved = false) =>
        ReadHeader(file[..Math.Min(file.Length, Dx10DataOffset)], file.Length, srgbToLinear, allowUnobserved);

    /// <summary>Parse a header, checking the texel length against <paramref name="fileLength"/> without the texels.</summary>
    public static DdsFile ReadHeader(ReadOnlySpan<byte> head, long fileLength, bool srgbToLinear = false,
                                     bool allowUnobserved = false)
    {
        if (fileLength < LegacyDataOffset || head.Length < LegacyDataOffset)
            throw new ImgcException($"DDS: {fileLength} bytes, shorter than the 128-byte header");
        if (!head[..4].SequenceEqual("DDS "u8))
            throw new ImgcException($"DDS: bad magic {Convert.ToHexString(head[..4])}");
        static uint U(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32LittleEndian(s[at..]);
        if (U(head, 4) != 124) throw new ImgcException($"DDS: dwSize {U(head, 4)} != 124");
        uint flags = U(head, 8), height = U(head, 12), width = U(head, 16), depth = U(head, 24), mips = U(head, 28);
        uint pfSize = U(head, 76), pfFlags = U(head, 80), bitCount = U(head, 88);
        var fourcc = head.Slice(84, 4);
        if (pfSize != 32) throw new ImgcException($"DDS: pixel format dwSize {pfSize} != 32");
        uint caps2 = U(head, 112);
        var warnings = new List<string>();

        uint mipCount = (flags & DdsdMipMapCount) != 0 ? Math.Max(1, mips) : 1;
        if (mips > 1 && (flags & DdsdMipMapCount) == 0)
        {
            mipCount = mips;
            warnings.Add($"dwMipMapCount={mips} without DDSD_MIPMAPCOUNT; using it");
        }
        bool cube = (caps2 & Caps2Cube) != 0;
        bool volume = (caps2 & Caps2Volume) != 0 || ((flags & DdsdDepth) != 0 && depth > 1);
        int dataOffset = LegacyDataOffset, alphaMode = 0;
        int? dxgi;
        byte il;
        string identified;

        if ((pfFlags & DdpfFourCC) != 0 && fourcc.SequenceEqual("DX10"u8))
        {
            if (fileLength < Dx10DataOffset || head.Length < Dx10DataOffset)
                throw new ImgcException("DDS: truncated DX10 header");
            uint format = U(head, 128), dimension = U(head, 132), misc = U(head, 136), arraySize = U(head, 140), misc2 = U(head, 144);
            dataOffset = Dx10DataOffset;
            if (dimension == 4) volume = true;
            else if (dimension == 2) throw new ImgcException("DDS: 1D textures (DX10 resourceDimension 2) have no IMGC type");
            else if (dimension != 3) throw new ImgcException($"DDS: DX10 resourceDimension {dimension} is invalid");
            if ((misc & 0x4) != 0) cube = true;
            if (arraySize != 1)
                throw new ImgcException($"DDS: arraySize {arraySize}; IMGC has no texture-array field");
            alphaMode = (int)(misc2 & 0x7);
            if (alphaMode > 4) throw new ImgcException($"DDS: DX10 miscFlags2 alpha mode {alphaMode} is invalid");
            if (!DxgiToIl.TryGetValue((int)format, out il))
                throw new ImgcException($"DDS: DXGI format {format} has no IL equivalent");
            var f = ImgcFormats.Get(il);
            if (f.Srgb)
            {
                byte linear = ImgcFormats.SrgbToLinear[il];
                if (srgbToLinear)
                {
                    warnings.Add($"DXGI {format} {f.Name} mapped to linear IL {linear} {ImgcFormats.Name(linear)} " +
                                 "(payload identical; srgb_to_linear)");
                    il = linear;
                }
                else if (!allowUnobserved)
                    throw new ImgcException(
                        $"DDS: DXGI {format} -> IL {il} {f.Name}: no sRGB IL id occurs in the shipped corpus. Re-export as " +
                        $"{ImgcFormats.Name(linear)} (DXGI {DdsWriter.Dxgi[linear]})");
            }
            dxgi = (int)format;
            identified = "dx10";
        }
        else
        {
            (il, identified) = Legacy(pfFlags, fourcc, bitCount, (U(head, 92), U(head, 96), U(head, 100), U(head, 104)));
            dxgi = DdsWriter.Dxgi.TryGetValue(il, out var d) ? d : null;
        }

        var fmt = ImgcFormats.Get(il);
        if (fmt.Tier != "A" && !allowUnobserved)
            throw new ImgcException($"DDS: IL {il} {fmt.Name} never occurs in the shipped corpus (tier C mapping)");
        if (cube && volume) throw new ImgcException("DDS: both cube and volume flags set");
        TextureType type;
        if (cube)
        {
            uint faces = caps2 & Caps2AllFaces;
            if (faces != Caps2AllFaces && (dataOffset == LegacyDataOffset || faces != 0))
                throw new ImgcException($"DDS: partial cube map (caps2 0x{caps2:X}); IMGC cubes carry all six faces");
            if (depth is not (0 or 1)) throw new ImgcException($"DDS: cube with depth {depth}");
            (type, depth) = (TextureType.Cube, 1);
        }
        else if (volume)
        {
            if (depth < 1) throw new ImgcException("DDS: volume with depth 0");
            if (depth > 255) throw new ImgcException($"DDS: volume depth {depth} exceeds the IMGC u8 depth field");
            type = TextureType.Volume;
        }
        else
        {
            if (depth > 1) throw new ImgcException($"DDS: dwDepth {depth} on a non-volume texture");
            (type, depth) = (TextureType.Texture2D, 1);
        }
        if (width < 1 || height < 1) throw new ImgcException($"DDS: {width}x{height}");
        if (width > 0xFFFF || height > 0xFFFF)
            throw new ImgcException($"DDS: {width}x{height} exceeds the IMGC u16 dimension fields");
        if (mipCount > 63) throw new ImgcException($"DDS: {mipCount} mips exceed the IMGC 6-bit mip count");

        var dds = new DdsFile((int)width, (int)height, (int)depth, (int)mipCount, type, il, dxgi, dataOffset,
                              identified, alphaMode, warnings);
        long expected = dataOffset + dds.FileLevels().Sum(l => l.Size);
        if (fileLength != expected)
            throw new ImgcException(
                $"DDS: file is {fileLength} bytes but a tightly packed {width}x{height}x{depth} {fmt.Name} " +
                $"{(cube ? "cube " : "")}with {mipCount} mips needs exactly {expected} (data at {dataOffset}); " +
                "row-padded or truncated files are refused");
        return dds;
    }

    private static (byte Il, string By) Legacy(uint pfFlags, ReadOnlySpan<byte> fourcc, uint bitCount,
                                              (uint R, uint G, uint B, uint A) m)
    {
        if ((pfFlags & DdpfFourCC) != 0)
        {
            string text = System.Text.Encoding.Latin1.GetString(fourcc);
            if (FourCC.TryGetValue(text, out var il)) return (il, "fourcc");
            uint code = BinaryPrimitives.ReadUInt32LittleEndian(fourcc);
            if (D3dFormat.TryGetValue(code, out il)) return (il, "d3dfmt");
            if (text is "DXT2" or "DXT4")
                throw new ImgcException($"DDS FourCC {text} is premultiplied-alpha BC2/BC3; the engine has no such format");
            throw new ImgcException($"DDS FourCC {text} / D3DFMT {code} is not a known IL layout");
        }
        if ((pfFlags & (DdpfRgb | DdpfLuminance | DdpfAlpha)) != 0)
        {
            if (Masks.TryGetValue((bitCount, m.R, m.G, m.B, m.A), out var il)) return (il, "masks");
            throw new ImgcException($"DDS legacy pixel format (bits={bitCount}, masks R=0x{m.R:X8} G=0x{m.G:X8} " +
                                    $"B=0x{m.B:X8} A=0x{m.A:X8}) does not identify an IL layout; save as DX10");
        }
        throw new ImgcException($"DDS pixel format flags 0x{pfFlags:X} carry neither FourCC nor RGB masks");
    }

    /// <summary>Tight per-surface texels in IMGC (mip-major) order — what <see cref="ImgcEncoder.FromLevels"/> joins.</summary>
    public static List<byte[]> Levels(DdsFile dds, ReadOnlySpan<byte> file)
    {
        var byKey = new Dictionary<(int Mip, int Face), byte[]>();
        foreach (var l in dds.FileLevels())
            byKey[(l.Mip, l.Face)] = file.Slice(dds.DataOffset + (int)l.Offset, (int)l.Size).ToArray();
        return dds.Geometry().LevelLayout(ImgcHeader.LevelPaddingTight).Select(l => byKey[(l.Mip, l.Face)]).ToList();
    }
}
