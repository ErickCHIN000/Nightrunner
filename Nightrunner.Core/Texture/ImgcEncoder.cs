using System.Buffers.Binary;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using CommunityToolkit.HighPerformance;

namespace Nightrunner.Core.Texture;

/// <summary>An encoded texture: the two parts a texture resource is made of.</summary>
public sealed record EncodedTexture(byte[] Header, byte[] Bitmap, int Width, int Height, int Mips, string FormatName)
{
    public string Summary => $"{Width}x{Height} {FormatName}, {Mips} mips, {Bitmap.Length:N0} bytes";
}

/// <summary>
/// Turns edited pixels back into an IMGC header + bitmap payload, in the format the original texture used.
/// </summary>
/// <remarks>
/// Mip chains are regenerated with a box filter. The header is rebuilt from the original one — statistics,
/// flags, extension and tail bytes are carried through untouched — with only the geometry and mip count updated.
/// A format this cannot honour is refused by name rather than silently written as something else.
/// </remarks>
public static class ImgcEncoder
{
    /// <summary>Why a format cannot be written yet, or null when it can (from some source: see <see cref="IsFloat"/>).</summary>
    public static string? Refusal(byte formatId)
    {
        if (!ImgcFormats.IsKnown(formatId)) return $"IL format id {formatId} is not in the native enum";
        return ImgcFormats.Get(formatId).Name switch
        {
            "BC1" or "BC1_SRGB" or "BC2" or "BC3" or "BC3_SRGB" or "BC7" or "BC7_SRGB" => null,
            "BC4" or "BC4_SNORM" or "BC5" or "BC5_SNORM" => null,
            "R8" or "RG8" or "RGBA8" or "RGBA8_SRGB" or "ARGB8" => null,
            "BC6H_UF16" or "BC6H_SF16" or "RGBA16F" => null,
            var name when name.EndsWith("SNORM") =>
                $"{name}: signed encoding is only implemented for BC4 and BC5",
            var name => $"{name}: no encoder yet",
        };
    }

    /// <summary>
    /// Formats written from linear floats (<see cref="EncodeFloat"/>), never from 8-bit pixels: an 8-bit source cannot
    /// carry HDR, so <see cref="Encode"/> refuses them.
    /// </summary>
    public static bool IsFloat(byte formatId) => ImgcFormats.Name(formatId) is "BC6H_UF16" or "BC6H_SF16" or "RGBA16F";

    public static bool CanEncode(byte formatId) => Refusal(formatId) is null;

    /// <summary>
    /// Encode BGRA32 pixels into the parts of a texture resource.
    /// </summary>
    /// <param name="template">The original header — everything not derived from the pixels is copied from it.</param>
    /// <param name="mipCount">How many levels to write; 0 means a full chain down to 1x1.</param>
    public static EncodedTexture Encode(ImgcHeader template, ReadOnlySpan<byte> bgra, int width, int height,
                                        int mipCount = 0, int levelPadding = ImgcHeader.LevelPaddingStock)
    {
        if (Refusal(template.Format) is { } why) throw new ImgcException(why);
        if (IsFloat(template.Format))
            throw new ImgcException($"{template.FormatName} is a float format: 8-bit pixels cannot carry it " +
                                    "(build it from a float DDS or a Radiance .hdr)");
        if (template.IsCube || template.IsVolume)
            throw new ImgcException($"{template.Type} textures cannot be written yet (2D only)");
        if (width <= 0 || height <= 0) throw new ImgcException("empty image");
        if (bgra.Length < (long)width * height * 4)
            throw new ImgcException($"{bgra.Length} bytes is not a {width}x{height} BGRA image");

        int maxMips = 1;
        for (int w = width, h = height; w > 1 || h > 1; maxMips++)
        {
            w = Math.Max(1, w >> 1);
            h = Math.Max(1, h >> 1);
        }
        int mips = mipCount <= 0 ? maxMips : Math.Min(mipCount, maxMips);

        // build the mip chain in BGRA, then encode each level
        var levels = new List<(byte[] Data, int W, int H)>(mips);
        var current = bgra.ToArray();
        int cw = width, ch = height;
        for (int m = 0; m < mips; m++)
        {
            levels.Add((current, cw, ch));
            if (m + 1 == mips) break;
            (current, cw, ch) = Downsample(current, cw, ch);
        }

        var format = ImgcFormats.Get(template.Format);
        var payload = new List<byte>();
        foreach (var (data, w, h) in levels)
        {
            byte[] encoded = format.Block
                ? format.Name.StartsWith("BC4") || format.Name.StartsWith("BC5")
                    ? EncodeRedGreen(format.Name, data, w, h)
                    : EncodeBlocks(format.Name, data, w, h)
                : EncodePlain(format, data, w, h);
            long expected = ImgcFormats.LevelBytes(template.Format, w, h, 1);
            if (encoded.Length != expected)
                throw new ImgcException($"{format.Name} {w}x{h}: encoder produced {encoded.Length} bytes, " +
                                        $"the layout needs {expected}");
            payload.AddRange(encoded);
            if (levelPadding > 0)
            {
                int pad = (int)(((encoded.Length + levelPadding - 1) / levelPadding * levelPadding) - encoded.Length);
                for (int i = 0; i < pad; i++) payload.Add(0);
            }
        }

        var header = BuildHeader(template, width, height, mips);
        return new EncodedTexture(header, payload.ToArray(), width, height, mips, format.Name);
    }

    /// <summary>
    /// Encode linear-float surfaces (<see cref="IsFloat"/> formats) into the parts of a texture resource.
    /// </summary>
    /// <param name="surfaces">Every surface, IMGC order: mip-major, then face (six per mip for a cube).</param>
    public static EncodedTexture EncodeFloat(ImgcHeader template, IReadOnlyList<FloatImage> surfaces, int mips,
                                             int levelPadding = ImgcHeader.LevelPaddingStock)
    {
        if (Refusal(template.Format) is { } why) throw new ImgcException(why);
        if (!IsFloat(template.Format))
            throw new ImgcException($"{template.FormatName} is not written from floats");
        if (template.IsVolume) throw new ImgcException($"{template.FormatName} volume textures cannot be written");
        if (surfaces.Count == 0 || surfaces.Count != mips * template.Faces)
            throw new ImgcException($"{surfaces.Count} surfaces for {mips} mips x {template.Faces} face(s)");
        int width = surfaces[0].Width, height = surfaces[0].Height;
        var levels = new byte[surfaces.Count][];
        for (int i = 0; i < surfaces.Count; i++)
        {
            var (w, h, _) = ImgcHeader.MipDims(width, height, 1, i / template.Faces);
            var s = surfaces[i];
            if (s.Width != w || s.Height != h)
                throw new ImgcException($"surface {i} is {s.Width}x{s.Height}, mip {i / template.Faces} needs {w}x{h}");
            levels[i] = template.FormatName switch
            {
                "BC6H_UF16" => Bc6h.Encode(s, signed: false),
                "BC6H_SF16" => Bc6h.Encode(s, signed: true),
                _ => HalfRgba(s),
            };
        }
        return FromLevels(template, width, height, mips, levels, levelPadding);
    }

    private static byte[] HalfRgba(FloatImage s)
    {
        var dst = new byte[s.Rgba.Length * 2];
        for (int i = 0; i < s.Rgba.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(dst.AsSpan(i * 2), BitConverter.HalfToUInt16Bits((System.Half)s.Rgba[i]));
        return dst;
    }

    /// <summary>
    /// Join already-encoded surfaces (IMGC order, tight) into a bitmap part with the texture's level padding, under
    /// the template header with new geometry. Every surface must be exactly the size the layout needs.
    /// </summary>
    public static EncodedTexture FromLevels(ImgcHeader template, int width, int height, int mips,
                                            IReadOnlyList<byte[]> levels, int levelPadding = ImgcHeader.LevelPaddingStock)
    {
        if (width is <= 0 or > 0xFFFF || height is <= 0 or > 0xFFFF)
            throw new ImgcException($"{width}x{height} does not fit the IMGC u16 dimensions");
        if (mips is < 1 or > ImgcHeader.MaxMips) throw new ImgcException($"{mips} mips (1-{ImgcHeader.MaxMips})");
        var header = BuildHeader(template, width, height, mips);
        var layout = ImgcHeader.Parse(header).LevelLayout(levelPadding);
        if (layout.Count != levels.Count)
            throw new ImgcException($"{levels.Count} surfaces, the layout needs {layout.Count}");
        long total = layout[^1].Offset + layout[^1].PaddedSize;
        var payload = new byte[total];
        for (int i = 0; i < layout.Count; i++)
        {
            if (levels[i].Length != layout[i].Size)
                throw new ImgcException($"{template.FormatName} mip {layout[i].Mip} face {layout[i].Face}: " +
                                        $"{levels[i].Length} bytes, the layout needs {layout[i].Size}");
            levels[i].CopyTo(payload, layout[i].Offset);
        }
        return new EncodedTexture(header, payload, width, height, mips, template.FormatName);
    }

    /// <summary>The original header with new geometry — every other byte is the original's.</summary>
    private static byte[] BuildHeader(ImgcHeader t, int width, int height, int mips)
    {
        var raw = new byte[t.StoredSize];
        BinaryPrimitives.WriteUInt32LittleEndian(raw, ImgcHeader.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), t.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(8), (uint)t.HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(12), t.Flags);
        t.StatsRaw.CopyTo(raw, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(64), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(66), (ushort)height);
        raw[68] = (byte)t.Depth;
        raw[69] = t.Reserved;
        raw[70] = t.Format;
        raw[71] = (byte)(((byte)t.Type & 3) | (mips << 2));
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(72), t.MipSplit);
        t.Extension.CopyTo(raw, ImgcHeader.FixedSize);
        t.Tail.CopyTo(raw, t.HeaderSize);
        return raw;
    }

    private static byte[] EncodeBlocks(string formatName, byte[] bgra, int width, int height)
    {
        var encoder = new BcEncoder();
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Quality = CompressionQuality.Balanced;
        encoder.OutputOptions.Format = formatName switch
        {
            // any alpha below 255 keeps BC1's punch-through mode, so a cutout's holes survive a rebuild
            "BC1" or "BC1_SRGB" => HasAlpha(bgra) ? CompressionFormat.Bc1WithAlpha : CompressionFormat.Bc1,
            "BC2" => CompressionFormat.Bc2,
            "BC3" or "BC3_SRGB" => CompressionFormat.Bc3,
            _ => CompressionFormat.Bc7,
        };

        var pixels = new ColorRgba32[width * height];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new ColorRgba32(bgra[i * 4 + 2], bgra[i * 4 + 1], bgra[i * 4], bgra[i * 4 + 3]);
        var view = new ReadOnlyMemory2D<ColorRgba32>(pixels, height, width);
        return encoder.EncodeToRawBytes(view, 0, out _, out _);
    }

    private static bool HasAlpha(byte[] bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4)
            if (bgra[i] < 255) return true;
        return false;
    }

    /// <summary>
    /// BC4 and BC5, signed or not: one 8-byte block per channel, written by our own encoder so the signed
    /// variants come out right (the library only does the unsigned ones).
    /// </summary>
    private static byte[] EncodeRedGreen(string formatName, byte[] bgra, int width, int height)
    {
        bool two = formatName.StartsWith("BC5");
        bool signed = formatName.EndsWith("SNORM");
        int bw = (width + 3) / 4, bh = (height + 3) / 4;
        int blockBytes = two ? 16 : 8;
        var dst = new byte[bw * bh * blockBytes];

        Span<byte> red = stackalloc byte[16];
        Span<byte> green = stackalloc byte[16];
        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                for (int y = 0; y < 4; y++)
                {
                    int sy = Math.Min(by * 4 + y, height - 1);
                    for (int x = 0; x < 4; x++)
                    {
                        int sx = Math.Min(bx * 4 + x, width - 1);
                        int at = (sy * width + sx) * 4;
                        red[y * 4 + x] = bgra[at + 2];
                        green[y * 4 + x] = bgra[at + 1];
                    }
                }
                int offset = (by * bw + bx) * blockBytes;
                BlockEncoders.AlphaBlock(red, dst.AsSpan(offset, 8), signed);
                if (two) BlockEncoders.AlphaBlock(green, dst.AsSpan(offset + 8, 8), signed);
            }
        }
        return dst;
    }

    /// <summary>Plain sample formats: narrow BGRA back to the channels the format stores.</summary>
    private static byte[] EncodePlain(ImgcFormat format, byte[] bgra, int width, int height)
    {
        int n = width * height;
        var dst = new byte[(long)n * format.Unit is var len && len <= int.MaxValue
            ? (int)len
            : throw new ImgcException("level too large")];
        for (int i = 0; i < n; i++)
        {
            int at = i * format.Unit;
            for (int c = 0; c < format.Channels.Length; c++)
            {
                dst[at + c] = format.Channels[c] switch
                {
                    'R' => bgra[i * 4 + 2],
                    'G' => bgra[i * 4 + 1],
                    'B' => bgra[i * 4],
                    'A' => bgra[i * 4 + 3],
                    _ => 255,          // X (ignored) channels
                };
            }
        }
        return dst;
    }

    /// <summary>Half-size box filter on BGRA.</summary>
    private static (byte[] Data, int W, int H) Downsample(byte[] src, int width, int height)
    {
        int w = Math.Max(1, width >> 1), h = Math.Max(1, height >> 1);
        var dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Min(y * 2, height - 1), y1 = Math.Min(y * 2 + 1, height - 1);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Min(x * 2, width - 1), x1 = Math.Min(x * 2 + 1, width - 1);
                for (int c = 0; c < 4; c++)
                {
                    int sum = src[(y0 * width + x0) * 4 + c] + src[(y0 * width + x1) * 4 + c]
                            + src[(y1 * width + x0) * 4 + c] + src[(y1 * width + x1) * 4 + c];
                    dst[(y * w + x) * 4 + c] = (byte)(sum / 4);
                }
            }
        }
        return (dst, w, h);
    }
}
