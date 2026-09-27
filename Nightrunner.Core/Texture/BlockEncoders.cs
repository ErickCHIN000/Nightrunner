namespace Nightrunner.Core.Texture;

/// <summary>
/// Single-channel block encoding (BC4 / BC5), signed and unsigned.
/// </summary>
/// <remarks>
/// Written here rather than taken from a library because the signed variants are what the games' normal maps
/// use (BC5_SNORM is 7,762 of the corpus' textures) and the block's mode has to be chosen deliberately: this
/// always emits the eight-interpolated-value mode with <c>a0 &gt; a1</c>, so no palette slot is pinned to the
/// extremes and the same code serves both signednesses — only how the endpoints are stored differs.
/// </remarks>
internal static class BlockEncoders
{
    /// <summary>
    /// Encode 16 channel samples into one 8-byte BC4 block.
    /// </summary>
    /// <param name="values">16 samples in raster order, already in the stored domain (0..255, or -127..127 + 128).</param>
    /// <param name="dst">8 bytes of output.</param>
    /// <param name="signed">Store the endpoints as signed bytes (SNORM).</param>
    internal static void AlphaBlock(ReadOnlySpan<byte> values, Span<byte> dst, bool signed)
    {
        byte min = 255, max = 0;
        foreach (byte v in values)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }

        // a0 > a1 selects the eight-value mode; equal endpoints mean a flat block and every index 0
        byte a0 = max, a1 = min;
        Span<byte> palette = stackalloc byte[8];
        palette[0] = a0;
        palette[1] = a1;
        if (a0 > a1)
            for (int i = 1; i <= 6; i++)
                palette[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
        else
            for (int i = 2; i < 8; i++) palette[i] = a0;

        ulong bits = 0;
        for (int i = 0; i < 16; i++)
        {
            int best = 0, bestError = int.MaxValue;
            for (int p = 0; p < 8; p++)
            {
                int error = Math.Abs(palette[p] - values[i]);
                if (error >= bestError) continue;
                bestError = error;
                best = p;
                if (error == 0) break;
            }
            bits |= (ulong)best << (i * 3);
        }

        dst[0] = signed ? ToSigned(a0) : a0;
        dst[1] = signed ? ToSigned(a1) : a1;
        for (int i = 0; i < 6; i++) dst[2 + i] = (byte)(bits >> (8 * i));
    }

    /// <summary>
    /// The decoder shows SNORM as UNORM by adding 127; this is the inverse, clamped away from -128 (which the
    /// format reserves).
    /// </summary>
    internal static byte ToSigned(byte shown) => unchecked((byte)(sbyte)Math.Clamp(shown - 127, -127, 127));
}
