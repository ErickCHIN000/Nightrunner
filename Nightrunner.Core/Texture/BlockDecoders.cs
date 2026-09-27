using System.Buffers.Binary;

namespace Nightrunner.Core.Texture;

/// <summary>
/// Block-compression decoders (BC1-BC5). Output is BGRA32, the layout WPF's Bgra32 bitmap wants.
/// </summary>
/// <remarks>
/// These are the standard DXT/RGTC layouts, not engine-specific: BC1 colour blocks, BC3/BC4/BC5 alpha blocks.
/// BC6H (HDR) and anything else is refused by <see cref="TextureDecoder"/> rather than approximated.
/// </remarks>
internal static class BlockDecoders
{
    /// <summary>Decode one BC1 colour block into <paramref name="rgba"/> (16 pixels, BGRA order).</summary>
    internal static void Bc1Block(ReadOnlySpan<byte> src, Span<byte> rgba, bool punchThroughAlpha)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(src);
        ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(src[2..]);
        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(src[4..]);

        Span<byte> pal = stackalloc byte[16];   // 4 colours, BGRA
        Rgb565(c0, pal[..4]);
        Rgb565(c1, pal.Slice(4, 4));
        bool third = punchThroughAlpha && c0 <= c1;
        for (int ch = 0; ch < 3; ch++)
        {
            int a = pal[ch], b = pal[4 + ch];
            if (third)
            {
                pal[8 + ch] = (byte)((a + b) / 2);
                pal[12 + ch] = 0;
            }
            else
            {
                pal[8 + ch] = (byte)((2 * a + b) / 3);
                pal[12 + ch] = (byte)((a + 2 * b) / 3);
            }
        }
        pal[3] = pal[7] = pal[11] = 255;
        pal[15] = third ? (byte)0 : (byte)255;

        for (int i = 0; i < 16; i++)
        {
            int sel = (int)((bits >> (i * 2)) & 3);
            pal.Slice(sel * 4, 4).CopyTo(rgba.Slice(i * 4, 4));
        }
    }

    /// <summary>BC3/BC4 alpha block: 8 interpolated values, 3 bits per pixel, written into one channel.</summary>
    internal static void AlphaBlock(ReadOnlySpan<byte> src, Span<byte> dst, int channel, int stride, bool signed)
    {
        Span<byte> pal = stackalloc byte[8];
        if (signed)
        {
            sbyte a0 = (sbyte)src[0], a1 = (sbyte)src[1];
            // SNORM is shown as UNORM for preview: -1..1 mapped to 0..255
            int v0 = (a0 == -128 ? -127 : a0) + 127;
            int v1 = (a1 == -128 ? -127 : a1) + 127;
            Interpolate((byte)v0, (byte)v1, a0 > a1, pal);
        }
        else
        {
            Interpolate(src[0], src[1], src[0] > src[1], pal);
        }

        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)src[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++)
        {
            int sel = (int)((bits >> (i * 3)) & 7);
            dst[i * stride + channel] = pal[sel];
        }
    }

    private static void Interpolate(byte a0, byte a1, bool sixValue, Span<byte> pal)
    {
        pal[0] = a0;
        pal[1] = a1;
        if (sixValue)
        {
            for (int i = 1; i <= 6; i++) pal[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
        }
        else
        {
            for (int i = 1; i <= 4; i++) pal[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
            pal[6] = 0;
            pal[7] = 255;
        }
    }

    /// <summary>BC2's 4-bit-per-pixel explicit alpha.</summary>
    internal static void Bc2Alpha(ReadOnlySpan<byte> src, Span<byte> rgba)
    {
        for (int i = 0; i < 16; i++)
        {
            int nibble = (src[i / 2] >> (i % 2 == 0 ? 0 : 4)) & 0xF;
            rgba[i * 4 + 3] = (byte)(nibble * 17);
        }
    }

    private static void Rgb565(ushort c, Span<byte> bgra)
    {
        int r = (c >> 11) & 0x1F, g = (c >> 5) & 0x3F, b = c & 0x1F;
        bgra[0] = (byte)((b << 3) | (b >> 2));
        bgra[1] = (byte)((g << 2) | (g >> 4));
        bgra[2] = (byte)((r << 3) | (r >> 2));
        bgra[3] = 255;
    }

    /// <summary>Rebuild Z from a two-channel normal map so BC5 previews look like normals, not red/green.</summary>
    internal static void RebuildNormalZ(Span<byte> bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            double x = bgra[i + 2] / 127.5 - 1.0;   // R
            double y = bgra[i + 1] / 127.5 - 1.0;   // G
            double z = Math.Sqrt(Math.Max(0.0, 1.0 - x * x - y * y));
            bgra[i] = (byte)Math.Clamp((z + 1.0) * 127.5, 0, 255);
            bgra[i + 3] = 255;
        }
    }
}
