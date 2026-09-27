using System.Buffers.Binary;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace Nightrunner.Core.Texture;

/// <summary>A decoded surface: 8-bit BGRA, top-down, no padding.</summary>
public sealed record DecodedImage(int Width, int Height, byte[] Bgra)
{
    public int Stride => Width * 4;
}

/// <summary>
/// Decodes one IMGC level to BGRA32 for display. Refuses rather than approximating: a format with no decoder
/// here raises with its name, it is never shown as garbage.
/// </summary>
/// <remarks>
/// BC1-BC5 (including the SNORM variants, which are most of the corpus' normal maps) are decoded here, where the
/// signed endpoint handling is explicit. BC6H and BC7 come from BCnEncoder.Net — their mode and partition tables
/// are the hard part and are not worth re-deriving.
/// </remarks>
public static class TextureDecoder
{
    private static readonly BcDecoder Bc = new();

    /// <summary>Formats this decoder can show. Everything else is refused by name.</summary>
    public static bool CanDecode(byte formatId) => Reason(formatId) is null;

    /// <summary>Why a format cannot be shown, or null when it can.</summary>
    public static string? Reason(byte formatId)
    {
        if (!ImgcFormats.IsKnown(formatId)) return $"IL format id {formatId} is not in the native enum";
        var f = ImgcFormats.Get(formatId);
        return f.Name switch
        {
            "BC1" or "BC1_SRGB" or "BC2" or "BC3" or "BC3_SRGB" => null,
            "BC4" or "BC4_SNORM" or "BC5" or "BC5_SNORM" => null,
            "BC6H_UF16" or "BC6H_SF16" or "BC7" or "BC7_SRGB" => null,
            "R8" or "R8_SNORM" or "R8_UINT" or "R8_NO_TYPELESS" => null,
            "RG8" or "RG8_SNORM" or "RG8_UINT" => null,
            "RGBA8" or "RGBA8_SRGB" or "RGBA8_SNORM" or "RGBA8_UINT" => null,
            "ARGB8" or "ARGB8_SRGB" or "XRGB8" => null,
            "R16" or "R16_SNORM" or "R16F" => null,
            "RGBA16" or "RGBA16_SNORM" or "RGBA16F" => null,
            _ => $"no decoder for {f.Name} yet",
        };
    }

    /// <summary>Decode one level's tight bytes. <paramref name="rebuildNormalZ"/> only affects two-channel data.</summary>
    public static DecodedImage Decode(byte formatId, ReadOnlySpan<byte> level, int width, int height,
                                      bool rebuildNormalZ = true)
    {
        if (Reason(formatId) is { } why) throw new ImgcException(why);
        var f = ImgcFormats.Get(formatId);
        var dst = new byte[(long)width * height * 4 is var n && n <= int.MaxValue
            ? (int)n
            : throw new ImgcException($"level {width}x{height} is too large to display")];

        switch (f.Name)
        {
            case "BC1" or "BC1_SRGB":
                Blocks(level, dst, width, height, 8, (src, px) => BlockDecoders.Bc1Block(src, px, true));
                break;
            case "BC2":
                Blocks(level, dst, width, height, 16, (src, px) =>
                {
                    BlockDecoders.Bc1Block(src[8..], px, false);
                    BlockDecoders.Bc2Alpha(src, px);
                });
                break;
            case "BC3" or "BC3_SRGB":
                Blocks(level, dst, width, height, 16, (src, px) =>
                {
                    BlockDecoders.Bc1Block(src[8..], px, false);
                    BlockDecoders.AlphaBlock(src, px, 3, 4, signed: false);
                });
                break;
            case "BC4" or "BC4_SNORM":
                Blocks(level, dst, width, height, 8, (src, px) =>
                {
                    px.Clear();
                    BlockDecoders.AlphaBlock(src, px, 2, 4, f.Name.EndsWith("SNORM"));
                    for (int i = 0; i < 16; i++)
                    {
                        px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2];   // grey
                        px[i * 4 + 3] = 255;
                    }
                });
                break;
            case "BC5" or "BC5_SNORM":
                bool snorm = f.Name.EndsWith("SNORM");
                Blocks(level, dst, width, height, 16, (src, px) =>
                {
                    px.Clear();
                    BlockDecoders.AlphaBlock(src, px, 2, 4, snorm);          // R
                    BlockDecoders.AlphaBlock(src[8..], px, 1, 4, snorm);     // G
                    for (int i = 0; i < 16; i++) px[i * 4 + 3] = 255;
                });
                if (rebuildNormalZ) BlockDecoders.RebuildNormalZ(dst);
                break;
            case "BC7" or "BC7_SRGB":
                FromLibrary(level, dst, width, height, CompressionFormat.Bc7);
                break;
            case "BC6H_UF16":
                FromLibraryHdr(level, dst, width, height, CompressionFormat.Bc6U);
                break;
            case "BC6H_SF16":
                FromLibraryHdr(level, dst, width, height, CompressionFormat.Bc6S);
                break;
            default:
                Uncompressed(f, level, dst, width, height);
                break;
        }
        return new DecodedImage(width, height, dst);
    }

    private delegate void BlockFn(ReadOnlySpan<byte> src, Span<byte> pixels);

    /// <summary>Walk 4x4 blocks and scatter each into the image, clipping at the edges.</summary>
    private static void Blocks(ReadOnlySpan<byte> src, byte[] dst, int width, int height, int blockBytes, BlockFn fn)
    {
        int bw = (width + 3) / 4, bh = (height + 3) / 4;
        long need = (long)bw * bh * blockBytes;
        if (src.Length < need)
            throw new ImgcException($"level is {src.Length} bytes, {need} needed for {width}x{height}");
        Span<byte> px = stackalloc byte[64];
        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                fn(src.Slice((by * bw + bx) * blockBytes, blockBytes), px);
                for (int y = 0; y < 4; y++)
                {
                    int iy = by * 4 + y;
                    if (iy >= height) break;
                    for (int x = 0; x < 4; x++)
                    {
                        int ix = bx * 4 + x;
                        if (ix >= width) break;
                        px.Slice((y * 4 + x) * 4, 4).CopyTo(dst.AsSpan((iy * width + ix) * 4, 4));
                    }
                }
            }
        }
    }

    private static void FromLibrary(ReadOnlySpan<byte> src, byte[] dst, int width, int height, CompressionFormat fmt)
    {
        var pixels = Bc.DecodeRaw(src.ToArray(), width, height, fmt);
        for (int i = 0; i < pixels.Length && i < width * height; i++)
        {
            var c = pixels[i];
            dst[i * 4] = c.b;
            dst[i * 4 + 1] = c.g;
            dst[i * 4 + 2] = c.r;
            dst[i * 4 + 3] = c.a;
        }
    }

    /// <summary>BC6H is HDR: tone-map (Reinhard) and gamma-encode so it can be shown on an 8-bit surface.</summary>
    private static void FromLibraryHdr(ReadOnlySpan<byte> src, byte[] dst, int width, int height,
                                       CompressionFormat fmt)
    {
        var pixels = Bc.DecodeRawHdr(src.ToArray(), width, height, fmt);
        for (int i = 0; i < pixels.Length && i < width * height; i++)
        {
            var c = pixels[i];
            dst[i * 4] = ToneMap(c.b);
            dst[i * 4 + 1] = ToneMap(c.g);
            dst[i * 4 + 2] = ToneMap(c.r);
            dst[i * 4 + 3] = 255;
        }
    }

    internal static byte ToneMap(float v)
    {
        if (float.IsNaN(v) || v <= 0) return 0;
        float mapped = v / (1f + v);                      // Reinhard
        return (byte)Math.Clamp(MathF.Pow(mapped, 1f / 2.2f) * 255f, 0, 255);
    }

    /// <summary>Why a format cannot be decoded to linear floats (the HDR path), or null when it can.</summary>
    public static string? FloatReason(byte formatId) => ImgcFormats.Name(formatId) switch
    {
        "BC6H_UF16" or "BC6H_SF16" or "RGBA16F" or "RGBA32F" => null,
        var name => $"{name} is not a float format this decodes (BC6H, RGBA16F, RGBA32F)",
    };

    /// <summary>
    /// Decode one level to linear floats, exactly: BC6H through the library (every value it returns is a half, checked
    /// over 96 million shipped samples), RGBA16F/RGBA32F as stored. BC6H has no alpha; it reads 1.
    /// </summary>
    public static FloatImage DecodeFloat(byte formatId, ReadOnlySpan<byte> level, int width, int height)
    {
        if (FloatReason(formatId) is { } why) throw new ImgcException(why);
        var name = ImgcFormats.Name(formatId);
        long need = ImgcFormats.LevelBytes(formatId, width, height, 1);
        if (level.Length < need)
            throw new ImgcException($"level is {level.Length} bytes, {need} needed for {width}x{height} {name}");
        var img = new FloatImage(width, height);
        var dst = img.Rgba;
        int n = width * height;
        switch (name)
        {
            case "BC6H_UF16" or "BC6H_SF16":
                var px = Bc.DecodeRawHdr(level[..(int)need].ToArray(), width, height,
                                         name == "BC6H_UF16" ? CompressionFormat.Bc6U : CompressionFormat.Bc6S);
                for (int i = 0; i < n; i++)
                {
                    dst[i * 4] = px[i].r;
                    dst[i * 4 + 1] = px[i].g;
                    dst[i * 4 + 2] = px[i].b;
                    dst[i * 4 + 3] = 1f;
                }
                break;
            case "RGBA16F":
                for (int i = 0; i < n * 4; i++)
                    dst[i] = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(level[(i * 2)..]));
                break;
            default:   // RGBA32F
                for (int i = 0; i < n * 4; i++) dst[i] = BinaryPrimitives.ReadSingleLittleEndian(level[(i * 4)..]);
                break;
        }
        return img;
    }

    /// <summary>Plain sample formats: widen to 8 bits per channel and fill the missing ones.</summary>
    private static void Uncompressed(ImgcFormat f, ReadOnlySpan<byte> src, byte[] dst, int width, int height)
    {
        long need = (long)width * height * f.Unit;
        if (src.Length < need)
            throw new ImgcException($"level is {src.Length} bytes, {need} needed for {width}x{height}");
        int n = width * height;
        bool snorm = f.Kind == FormatKind.Snorm;
        bool half = f.Kind == FormatKind.Float;
        int bytesPerChannel = f.Unit / Math.Max(1, f.Channels.Length);

        for (int i = 0; i < n; i++)
        {
            int at = i * f.Unit;
            Span<byte> outPx = dst.AsSpan(i * 4, 4);
            outPx[0] = outPx[1] = outPx[2] = 0;
            outPx[3] = 255;
            for (int c = 0; c < f.Channels.Length; c++)
            {
                byte v = Sample(src, at + c * bytesPerChannel, bytesPerChannel, snorm, half);
                switch (f.Channels[c])
                {
                    case 'R': outPx[2] = v; break;
                    case 'G': outPx[1] = v; break;
                    case 'B': outPx[0] = v; break;
                    case 'A': outPx[3] = v; break;
                }
            }
            if (f.Channels == "R") outPx[0] = outPx[1] = outPx[2];          // grey
            else if (f.Channels == "RG") outPx[0] = 0;
        }
        if (f.Channels == "RG") BlockDecoders.RebuildNormalZ(dst);
    }

    private static byte Sample(ReadOnlySpan<byte> src, int at, int bytes, bool snorm, bool half) => bytes switch
    {
        1 => snorm ? Unit(Math.Max((sbyte)src[at] / 127f, -1f), true) : src[at],
        2 when half => Unit((float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(src.Slice(at, 2))), false),
        2 => snorm
            ? Unit(Math.Max(BitConverter.ToInt16(src.Slice(at, 2)) / 32767f, -1f), true)
            : Unit(BitConverter.ToUInt16(src.Slice(at, 2)) / 65535f, false),
        4 => Unit(BitConverter.ToSingle(src.Slice(at, 4)), false),
        _ => src[at],
    };

    /// <summary>The prototype's preview rule: signed (v+1)/2, NaN 0, clip to [0,1], ×255 rounded half to even.</summary>
    private static byte Unit(float v, bool signed)
    {
        if (float.IsNaN(v)) v = 0;
        if (signed) v = (v + 1f) / 2f;
        return (byte)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f, MidpointRounding.ToEven);
    }
}
