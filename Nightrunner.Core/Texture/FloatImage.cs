namespace Nightrunner.Core.Texture;

/// <summary>A linear floating-point surface: RGBA, four floats per pixel, top-down, no padding.</summary>
/// <remarks>What the HDR path works in: BC6H decodes to it, float DDS and Radiance <c>.hdr</c> sources read into it,
/// and the BC6H encoder takes it. Formats without alpha carry 1.</remarks>
public sealed class FloatImage
{
    public int Width { get; }
    public int Height { get; }
    public float[] Rgba { get; }

    public FloatImage(int width, int height, float[]? rgba = null)
    {
        if (width <= 0 || height <= 0) throw new ImgcException($"empty image {width}x{height}");
        long n = (long)width * height * 4;
        if (n > int.MaxValue) throw new ImgcException($"{width}x{height} is too large");
        rgba ??= new float[n];
        if (rgba.Length != n) throw new ImgcException($"{rgba.Length} floats is not a {width}x{height} RGBA image");
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    /// <summary>Half-size box filter (odd edges repeat their last row/column), the float twin of the BGRA one.</summary>
    public FloatImage Downsample()
    {
        int w = Math.Max(1, Width >> 1), h = Math.Max(1, Height >> 1);
        var dst = new float[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Min(y * 2, Height - 1), y1 = Math.Min(y * 2 + 1, Height - 1);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Min(x * 2, Width - 1), x1 = Math.Min(x * 2 + 1, Width - 1);
                for (int c = 0; c < 4; c++)
                    dst[(y * w + x) * 4 + c] = (Rgba[(y0 * Width + x0) * 4 + c] + Rgba[(y0 * Width + x1) * 4 + c]
                                              + Rgba[(y1 * Width + x0) * 4 + c] + Rgba[(y1 * Width + x1) * 4 + c]) * 0.25f;
            }
        }
        return new FloatImage(w, h, dst);
    }

    /// <summary>This image and <paramref name="count"/> - 1 box-filtered levels below it.</summary>
    public List<FloatImage> MipChain(int count)
    {
        var chain = new List<FloatImage>(count) { this };
        while (chain.Count < count) chain.Add(chain[^1].Downsample());
        return chain;
    }

    /// <summary>Tone-mapped (Reinhard, gamma 2.2) BGRA for display — the same mapping the BC6H preview uses.</summary>
    public DecodedImage ToDisplay()
    {
        var dst = new byte[Width * Height * 4];
        for (int i = 0; i < Width * Height; i++)
        {
            dst[i * 4] = TextureDecoder.ToneMap(Rgba[i * 4 + 2]);
            dst[i * 4 + 1] = TextureDecoder.ToneMap(Rgba[i * 4 + 1]);
            dst[i * 4 + 2] = TextureDecoder.ToneMap(Rgba[i * 4]);
            dst[i * 4 + 3] = 255;
        }
        return new DecodedImage(Width, Height, dst);
    }
}
