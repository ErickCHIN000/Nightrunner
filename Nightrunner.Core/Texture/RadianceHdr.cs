using System.Globalization;
using System.Text;

namespace Nightrunner.Core.Texture;

/// <summary>
/// Radiance RGBE (<c>.hdr</c>) files: one linear RGB surface, the common HDR interchange format. Reads flat and
/// new-style run-length scanlines in the standard <c>-Y h +X w</c> orientation; writes run-length scanlines.
/// </summary>
/// <remarks>
/// Decoding follows Radiance's own <c>colr_color</c>: a mantissa m with exponent e is (m + 0.5)·2^(e−136), and e = 0 is
/// black. Refused by name rather than guessed: XYZE pixels, an <c>EXPOSURE</c> or <c>COLORCORR</c> other than 1
/// (readers disagree on whether to undo them), other orientations, and old-style run-length scanlines.
/// </remarks>
public static class RadianceHdr
{
    public static FloatImage Read(byte[] file)
    {
        int pos = 0;
        string Line()
        {
            int end = file.AsSpan(pos).IndexOf((byte)'\n');
            if (end < 0) throw new ImgcException(".hdr: header is not terminated");
            string s = Encoding.ASCII.GetString(file, pos, end).TrimEnd('\r');
            pos += end + 1;
            return s;
        }

        string first = Line();
        if (!first.StartsWith("#?")) throw new ImgcException(".hdr: missing the #?RADIANCE signature");
        for (string line = Line(); line.Length > 0; line = Line())
        {
            if (line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "FORMAT" when value != "32-bit_rle_rgbe":
                    throw new ImgcException($".hdr: FORMAT={value} (only 32-bit_rle_rgbe is read)");
                case "EXPOSURE" when !IsOne(value):
                    throw new ImgcException($".hdr: EXPOSURE={value}; save without exposure (1)");
                case "COLORCORR" when value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(v => !IsOne(v)):
                    throw new ImgcException($".hdr: COLORCORR={value}; save without colour correction");
            }
        }
        var res = Line().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (res.Length != 4 || res[0] != "-Y" || res[2] != "+X"
            || !int.TryParse(res[1], out int height) || !int.TryParse(res[3], out int width) || width < 1 || height < 1)
            throw new ImgcException($".hdr: resolution '{string.Join(' ', res)}' (only -Y h +X w is read)");

        var img = new FloatImage(width, height);
        var scan = new byte[width * 4];
        for (int y = 0; y < height; y++)
        {
            var rest = file.AsSpan(pos);
            if (width is >= 8 and <= 0x7FFF && rest.Length >= 4 && rest[0] == 2 && rest[1] == 2 && rest[2] < 128)
            {
                if ((rest[2] << 8 | rest[3]) != width)
                    throw new ImgcException($".hdr: scanline {y} says width {rest[2] << 8 | rest[3]}, header {width}");
                pos += 4;
                for (int c = 0; c < 4; c++)
                {
                    for (int x = 0; x < width;)
                    {
                        if (pos >= file.Length) throw new ImgcException($".hdr: truncated at scanline {y}");
                        int count = file[pos++];
                        if (count > 128)
                        {
                            count -= 128;
                            if (x + count > width || pos >= file.Length)
                                throw new ImgcException($".hdr: run overflows scanline {y}");
                            byte v = file[pos++];
                            for (int i = 0; i < count; i++) scan[(x++) * 4 + c] = v;
                        }
                        else
                        {
                            if (count == 0 || x + count > width || pos + count > file.Length)
                                throw new ImgcException($".hdr: bad literal run on scanline {y}");
                            for (int i = 0; i < count; i++) scan[(x++) * 4 + c] = file[pos++];
                        }
                    }
                }
            }
            else
            {
                if (rest.Length >= 3 && rest[0] == 1 && rest[1] == 1 && rest[2] == 1)
                    throw new ImgcException(".hdr: old-style run-length scanlines are not read; re-save the file");
                if (rest.Length < width * 4) throw new ImgcException($".hdr: truncated at scanline {y}");
                rest[..(width * 4)].CopyTo(scan);
                pos += width * 4;
            }
            for (int x = 0; x < width; x++)
            {
                int e = scan[x * 4 + 3], at = (y * width + x) * 4;
                float f = e == 0 ? 0 : MathF.ScaleB(1f, e - 136);
                for (int c = 0; c < 3; c++) img.Rgba[at + c] = e == 0 ? 0 : (scan[x * 4 + c] + 0.5f) * f;
                img.Rgba[at + 3] = 1f;
            }
        }
        return img;
    }

    private static bool IsOne(string v) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d == 1;

    /// <summary>RGB (alpha dropped, negatives and NaN written as 0) as a run-length Radiance file.</summary>
    public static byte[] Write(FloatImage image)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {image.Height} +X {image.Width}\n"));
        int width = image.Width;
        var channels = new byte[4][];
        for (int c = 0; c < 4; c++) channels[c] = new byte[width];
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = (y * width + x) * 4;
                float r = Clean(image.Rgba[at]), g = Clean(image.Rgba[at + 1]), b = Clean(image.Rgba[at + 2]);
                float max = Math.Max(r, Math.Max(g, b));
                if (max < 1e-32f)
                {
                    for (int c = 0; c < 4; c++) channels[c][x] = 0;
                    continue;
                }
                int e = Math.ILogB(max) + 1;                  // max = m·2^e with m in [0.5, 1)
                double d = Math.ScaleB(255.9999, -e);
                channels[0][x] = (byte)(r * d);
                channels[1][x] = (byte)(g * d);
                channels[2][x] = (byte)(b * d);
                channels[3][x] = (byte)(e + 128);
            }
            if (width is < 8 or > 0x7FFF)
            {
                for (int x = 0; x < width; x++)
                    for (int c = 0; c < 4; c++) ms.WriteByte(channels[c][x]);
                continue;
            }
            ms.Write([2, 2, (byte)(width >> 8), (byte)width]);
            foreach (var ch in channels) RunLength(ch, ms);
        }
        return ms.ToArray();
    }

    private static float Clean(float v) => float.IsNaN(v) || v < 0 ? 0 : Math.Min(v, 1e38f);

    private static void RunLength(byte[] ch, Stream to)
    {
        for (int i = 0; i < ch.Length;)
        {
            int run = 1;
            while (i + run < ch.Length && run < 127 && ch[i + run] == ch[i]) run++;
            if (run >= 4)
            {
                to.WriteByte((byte)(128 + run));
                to.WriteByte(ch[i]);
                i += run;
                continue;
            }
            int start = i;
            while (i < ch.Length && i - start < 128)
            {
                int r = 1;
                while (i + r < ch.Length && r < 4 && ch[i + r] == ch[i]) r++;
                if (r >= 4) break;
                i++;
            }
            to.WriteByte((byte)(i - start));
            to.Write(ch, start, i - start);
        }
    }
}
