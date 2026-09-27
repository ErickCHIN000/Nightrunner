using System.Buffers.Binary;
using System.IO.Compression;

namespace Nightrunner.Core.Texture;

/// <summary>
/// A minimal PNG encoder (8-bit RGBA, no filtering, zlib from the BCL) so Core can write previews without a UI
/// imaging stack. Output is plain and valid; it is not byte-identical to any other encoder.
/// </summary>
public static class PngWriter
{
    private static readonly uint[] Crc = BuildCrc();

    public static void Write(DecodedImage img, Stream to)
    {
        to.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, img.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), img.Height);
        ihdr[8] = 8;        // bit depth
        ihdr[9] = 6;        // RGBA
        Chunk(to, "IHDR", ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + img.Width * 4];
            for (int y = 0; y < img.Height; y++)
            {
                var src = img.Bgra.AsSpan(y * img.Stride, img.Width * 4);
                for (int x = 0; x < img.Width; x++)
                {
                    row[1 + x * 4] = src[x * 4 + 2];
                    row[2 + x * 4] = src[x * 4 + 1];
                    row[3 + x * 4] = src[x * 4];
                    row[4 + x * 4] = src[x * 4 + 3];
                }
                z.Write(row);
            }
        }
        Chunk(to, "IDAT", raw.ToArray());
        Chunk(to, "IEND", []);
    }

    public static void Write(DecodedImage img, string path)
    {
        using var fs = File.Create(path);
        Write(img, fs);
    }

    private static void Chunk(Stream to, string type, byte[] data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        to.Write(word);
        var head = System.Text.Encoding.ASCII.GetBytes(type);
        to.Write(head);
        to.Write(data);
        uint c = 0xFFFFFFFF;
        foreach (byte b in head) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (byte b in data) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(word, c ^ 0xFFFFFFFF);
        to.Write(word);
    }

    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
