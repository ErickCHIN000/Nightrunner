using System.Buffers.Binary;

namespace Nightrunner.Core.Texture;

/// <summary>
/// IMGC → DDS, the writer half of the prototype's <c>texture/dds.py</c>: always a DX10 header (148 bytes, the same
/// bytes the prototype and DyingLightExplorer write), then the texels face-major with the IMGC level padding removed.
/// The texels are copied as stored — block-compressed data stays block-compressed, so a GPU samples it as is.
/// </summary>
/// <remarks>
/// <code>
/// 0 "DDS " | 4 124 | 8 flags 0x1007 | LINEARSIZE (block) or PITCH | MIPMAPCOUNT (mips > 1) | DEPTH (volume)
/// 12 height | 16 width | 20 block ? bytes of (mip 0, face 0) : width × unit | 24 volume ? depth : 0 | 28 mips
/// 76 pixel format {32, FOURCC, "DX10"} | 108 caps TEXTURE | MIPMAP|COMPLEX (mips > 1) | COMPLEX (cube/volume)
/// 112 caps2 cube 0xFE00 / volume 0x200000 | 128 DX10 {dxgi, dimension 3 (2D) / 4 (3D), misc cube 0x4, 1, 0}
/// </code>
/// A format with no DXGI equivalent, or a header-only record, is refused.
/// </remarks>
public static class DdsWriter
{
    public const int HeaderSize = 148;

    /// <summary>DXGI_FORMAT per IL id (formats.py; ids without an unambiguous DXGI format are absent).</summary>
    public static readonly IReadOnlyDictionary<byte, int> Dxgi = new Dictionary<byte, int>
    {
        [0] = 61, [1] = 63, [2] = 62, [3] = 64, [6] = 54, [7] = 56, [8] = 58, [9] = 57, [10] = 59, [12] = 41,
        [13] = 42, [14] = 43, [15] = 49, [16] = 51, [17] = 50, [18] = 52, [19] = 34, [20] = 35, [21] = 37, [22] = 36,
        [23] = 38, [24] = 16, [25] = 17, [26] = 18, [27] = 85, [30] = 26, [32] = 87, [33] = 91, [34] = 88, [38] = 28,
        [39] = 31, [40] = 30, [41] = 32, [44] = 24, [45] = 25, [46] = 10, [47] = 11, [48] = 13, [49] = 12, [50] = 14,
        [51] = 2, [52] = 3, [53] = 4, [54] = 55, [55] = 45, [56] = 40, [58] = 20, [59] = 71, [60] = 74, [61] = 77,
        [62] = 81, [63] = 80, [64] = 84, [65] = 83, [66] = 95, [67] = 96, [68] = 98, [70] = 72, [71] = 78, [73] = 99,
        [74] = 29,
    };

    public static string? Refusal(ImgcHeader h) =>
        h.HeaderOnly ? "header-only IMGC record (flag 0x02) has no texels to export"
        : !Dxgi.ContainsKey(h.Format) ? $"IL format {h.Format} ({h.FormatName}) has no DXGI equivalent; DDS export refused"
        : null;

    public static byte[] Header(ImgcHeader h, IReadOnlyList<ImgcLevel>? levels = null)
    {
        if (Refusal(h) is { } why) throw new ImgcException(why);
        levels ??= h.LevelLayout();
        var f = ImgcFormats.Get(h.Format);
        uint flags = 0x1 | 0x2 | 0x4 | 0x1000 | (f.Block ? 0x80000u : 0x8u);
        uint caps = 0x1000, caps2 = 0;
        if (h.MipCount > 1) { flags |= 0x20000; caps |= 0x400000 | 0x8; }
        if (h.IsCube) { caps |= 0x8; caps2 = 0x200 | 0xFC00; }
        if (h.IsVolume) { flags |= 0x800000; caps |= 0x8; caps2 = 0x200000; }
        long pitch = f.Block ? levels[0].Size : (long)h.Width * f.Unit;
        var b = new byte[HeaderSize];
        var s = b.AsSpan();
        "DDS "u8.CopyTo(s);
        W(s, 4, 124); W(s, 8, flags); W(s, 12, (uint)h.Height); W(s, 16, (uint)h.Width); W(s, 20, (uint)pitch);
        W(s, 24, h.IsVolume ? (uint)h.Depth : 0); W(s, 28, (uint)h.MipCount);
        W(s, 76, 32); W(s, 80, 0x4); "DX10"u8.CopyTo(s[84..]);
        W(s, 108, caps); W(s, 112, caps2);
        W(s, 128, (uint)Dxgi[h.Format]); W(s, 132, h.IsVolume ? 4u : 3u); W(s, 136, h.IsCube ? 0x4u : 0); W(s, 140, 1);
        return b;
    }

    /// <summary>Levels in DDS order: face-major (IMGC stores them mip-major).</summary>
    public static IEnumerable<ImgcLevel> FaceMajor(IEnumerable<ImgcLevel> levels) =>
        levels.OrderBy(l => l.Face).ThenBy(l => l.Mip);

    /// <summary>A whole texture as a DDS file in memory.</summary>
    public static byte[] ToDds(TextureResource tex)
    {
        if (tex.PayloadProblem is { } why) throw new ImgcException(why);
        var header = Header(tex.Header, tex.Levels);
        var outb = new byte[HeaderSize + tex.Levels.Sum(l => l.Size)];
        header.CopyTo(outb, 0);
        int at = HeaderSize;
        foreach (var l in FaceMajor(tex.Levels))
        {
            tex.ReadLevel(l).CopyTo(outb, at);
            at += (int)l.Size;
        }
        return outb;
    }

    /// <summary>Stream a texture to a DDS file one level at a time.</summary>
    public static void Write(TextureResource tex, Stream to)
    {
        if (tex.PayloadProblem is { } why) throw new ImgcException(why);
        to.Write(Header(tex.Header, tex.Levels));
        foreach (var l in FaceMajor(tex.Levels)) to.Write(tex.ReadLevel(l));
    }

    /// <summary>
    /// The HDR form of a texture: every surface decoded to linear floats and written as an RGBA16F DDS (DXGI 10, same
    /// header rules, face-major). BC6H decodes to halves exactly, so nothing is lost; an RGBA16F texture is written
    /// as stored (<see cref="ToDds"/>). This is what a project edits in place of a PNG for an HDR texture.
    /// </summary>
    public static byte[] ToFloatDds(TextureResource tex)
    {
        if (tex.PayloadProblem is { } why) throw new ImgcException(why);
        var h = tex.Header;
        if (TextureDecoder.FloatReason(h.Format) is { } notFloat) throw new ImgcException(notFloat);
        if (h.FormatName == "RGBA16F") return ToDds(tex);
        if (h.IsVolume) throw new ImgcException($"{h.FormatName} volume textures have no float export");
        var geometry = new ImgcHeader { Width = h.Width, Height = h.Height, Depth = 1, Format = 46, Packed = h.Packed };
        var levels = geometry.LevelLayout(ImgcHeader.LevelPaddingTight);
        var outb = new byte[HeaderSize + levels.Sum(l => l.Size)];
        Header(geometry, levels).CopyTo(outb, 0);
        int at = HeaderSize;
        foreach (var l in FaceMajor(tex.Levels))
        {
            var img = TextureDecoder.DecodeFloat(h.Format, tex.ReadLevel(l), l.Width, l.Height);
            foreach (float v in img.Rgba)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(outb.AsSpan(at), BitConverter.HalfToUInt16Bits((Half)v));
                at += 2;
            }
        }
        return outb;
    }

    private static void W(Span<byte> s, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(s[at..], v);
}
