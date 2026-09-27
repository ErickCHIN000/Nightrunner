using System.Buffers.Binary;
using Nightrunner.Core.Rpack;
using Nightrunner.Core.Texture;

namespace Nightrunner.Tests;

// Port of the DDS writer cases of nightrunner-main/tests/test_texture.py (test_header_bytes, test_face_order, the
// no-DXGI refusal) and of the reader (test_roundtrip_all_geometries, test_legacy_fourcc_and_masks, test_refusals).
public class DdsTests
{
    private static ImgcHeader Header(int w, int h, int d, byte type, int mips, byte fmt) =>
        ImgcHeader.Parse(Synth.ImgcHeaderBytes(w, h, d, type, mips, fmt));

    [Fact]
    public void HeaderBytes()
    {
        var hdr = DdsWriter.Header(Header(64, 64, 1, Synth.TexCube, 7, 66));
        Assert.Equal(148, hdr.Length);
        Assert.Equal("DDS "u8.ToArray(), hdr[..4]);
        uint U(int at) => BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(at));
        Assert.Equal((124u, 0x1007u | 0x80000 | 0x20000, 64u, 64u, 4096u, 0u, 7u), (U(4), U(8), U(12), U(16), U(20), U(24), U(28)));
        Assert.Equal((32u, 4u), (U(76), U(80)));
        Assert.Equal("DX10"u8.ToArray(), hdr[84..88]);
        Assert.Equal((0x1000u | 0x400008, 0xFE00u), (U(108), U(112)));
        Assert.Equal((95u, 3u, 4u, 1u, 0u), (U(128), U(132), U(136), U(140), U(144)));

        hdr = DdsWriter.Header(Header(32, 16, 8, Synth.TexVolume, 6, 38));
        Assert.Equal(0x1007u | 0x8 | 0x20000 | 0x800000, U(8));
        Assert.Equal(32u * 4, U(20));
        Assert.Equal(8u, U(24));
        Assert.Equal((0x1000u | 0x400008, 0x200000u), (U(108), U(112)));
        Assert.Equal((28u, 4u, 0u, 1u, 0u), (U(128), U(132), U(136), U(140), U(144)));
    }

    [Fact]
    public void FaceOrder()
    {
        var hd = Header(4, 4, 1, Synth.TexCube, 2, 63);
        var levels = hd.LevelLayout();
        var order = DdsWriter.FaceMajor(levels).Select(l => (l.Face, l.Mip)).ToList();
        Assert.Equal([(0, 0), (0, 1), (1, 0)], order.Take(3));
    }

    [Fact]
    public void NoDxgiIsRefused() =>
        Assert.Throws<ImgcException>(() => DdsWriter.Header(Header(4, 4, 1, Synth.Tex2D, 1, 35)));   // BGRA8

    /// <summary>Real textures, one per format present, against the prototype's imgc_to_dds bytes (sha256 recorded).</summary>
    [Fact]
    public void ShippedTexturesExport()
    {
        var install = Installs.Require("dltb");
        var path = install.Rpacks().First(p => Path.GetFileName(p) == "gui_hud_pc.rpack");
        using var pack = RpackFile.Open(path);
        int n = 0;
        for (int i = 0; i < pack.Count && n < 40; i++)
        {
            if (pack.Logicals[i].Type != 0x20) continue;
            var tex = TextureResource.Open(pack, i);
            if (tex.PayloadProblem is not null || DdsWriter.Refusal(tex.Header) is not null) continue;
            var dds = DdsWriter.ToDds(tex);
            Assert.Equal(DdsWriter.HeaderSize + tex.Levels.Sum(l => l.Size), dds.Length);
            using var ms = new MemoryStream();
            DdsWriter.Write(tex, ms);
            Assert.Equal(dds, ms.ToArray());
            n++;
        }
        Assert.True(n > 0);
    }

    // ---- reader (test_roundtrip_all_geometries, test_legacy_fourcc_and_masks, test_refusals) ----------------

    public static TheoryData<int, int, int, byte, int, byte> Cases => new()
    {
        { 256, 256, 1, 0, 9, 59 }, { 64, 64, 1, 1, 7, 66 }, { 32, 16, 8, 2, 6, 38 }, { 160, 554, 1, 0, 10, 46 },
        { 5, 3, 1, 0, 1, 63 }, { 1, 1, 1, 0, 1, 0 }, { 128, 128, 1, 1, 1, 64 }, { 8, 8, 4, 2, 4, 61 },
        { 12774, 100, 1, 0, 1, 16 }, { 3, 3, 1, 0, 2, 32 }, { 16, 16, 16, 2, 1, 8 },
    };

    /// <summary>IMGC → DDS → IMGC, through the writer's header and the reader: header and payload bit-exact.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void RoundTripAllGeometries(int w, int h, int d, byte type, int mips, byte fmt)
    {
        var raw = Synth.ImgcHeaderBytes(w, h, d, type, mips, fmt);
        var hd = ImgcHeader.Parse(raw);
        var levels = Synth.RandomLevels(hd);
        var dds = Dds(hd, levels);
        Assert.Equal(DdsWriter.HeaderSize + levels.Sum(l => l.Length), dds.Length);

        var df = DdsReader.Read(dds);
        Assert.Equal((w, h, type == 2 ? d : 1, mips, (TextureType)type, fmt, "dx10"),
                     (df.Width, df.Height, df.Depth, df.MipCount, df.Type, df.Format, df.IdentifiedBy));
        var back = DdsReader.Levels(df, dds);
        Assert.Equal(levels, back);
        var enc = ImgcEncoder.FromLevels(hd, df.Width, df.Height, df.MipCount, back);
        Assert.Equal(raw, enc.Header);
        Assert.Equal(Synth.JoinLevels(hd, levels), enc.Bitmap);
    }

    /// <summary>A DDS file the way DdsWriter lays one out: header, then the levels face-major.</summary>
    internal static byte[] Dds(ImgcHeader hd, IReadOnlyList<byte[]> levels)
    {
        var layout = hd.LevelLayout();
        var order = DdsWriter.FaceMajor(layout).ToList();
        using var ms = new MemoryStream();
        ms.Write(DdsWriter.Header(hd, layout));
        foreach (var l in order) ms.Write(levels[layout.IndexOf(l)]);
        return ms.ToArray();
    }

    private static byte[] Legacy(int w, int h, int mips, string? fourcc = null, byte[]? fourccRaw = null,
                                 (uint R, uint G, uint B, uint A) masks = default, uint bitCount = 0, uint pfFlags = 0)
    {
        var b = new byte[128];
        "DDS "u8.CopyTo(b);
        void W(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
        W(4, 124); W(8, 0x1007u | (mips > 1 ? 0x20000u : 0)); W(12, (uint)h); W(16, (uint)w); W(28, (uint)mips);
        W(76, 32);
        if (fourcc is not null || fourccRaw is not null)
        {
            W(80, 0x4);
            (fourccRaw ?? System.Text.Encoding.ASCII.GetBytes(fourcc!)).CopyTo(b, 84);
        }
        else
        {
            W(80, pfFlags); W(88, bitCount); W(92, masks.R); W(96, masks.G); W(100, masks.B); W(104, masks.A);
        }
        W(108, 0x1000);
        return b;
    }

    [Fact]
    public void LegacyFourCCAndMasks()
    {
        var hd = Header(8, 8, 1, Synth.Tex2D, 4, 59);
        var body = new byte[hd.LevelLayout().Sum(l => l.Size)];
        var d = DdsReader.Read([.. Legacy(8, 8, 4, "DXT1"), .. body]);
        Assert.Equal(((byte)59, "fourcc", 128, 4), (d.Format, d.IdentifiedBy, d.DataOffset, d.MipCount));
        Assert.Equal(65, DdsReader.Read([.. Legacy(8, 8, 1, "ATI2"), .. new byte[64]]).Format);
        Assert.Equal(64, DdsReader.Read([.. Legacy(8, 8, 1, "BC5S"), .. new byte[64]]).Format);
        d = DdsReader.Read([.. Legacy(2, 2, 1, masks: (0x00FF0000, 0xFF00, 0xFF, 0xFF000000), bitCount: 32, pfFlags: 0x41), .. new byte[16]]);
        Assert.Equal(((byte)32, "masks"), (d.Format, d.IdentifiedBy));
        Assert.Equal(38, DdsReader.Read([.. Legacy(2, 2, 1, masks: (0xFF, 0xFF00, 0xFF0000, 0xFF000000), bitCount: 32, pfFlags: 0x41), .. new byte[16]]).Format);
        Assert.Equal(0, DdsReader.Read([.. Legacy(2, 2, 1, masks: (0xFF, 0, 0, 0), bitCount: 8, pfFlags: 0x20000), .. new byte[4]]).Format);
        Assert.Equal(46, DdsReader.Read([.. Legacy(2, 2, 1, fourccRaw: BitConverter.GetBytes(113u)), .. new byte[32]]).Format);
    }

    [Fact]
    public void Refusals()
    {
        var hd = Header(8, 8, 1, Synth.Tex2D, 4, 59);
        var good = Dds(hd, Synth.RandomLevels(hd));
        Assert.Throws<ImgcException>(() => DdsReader.Read([.. good, 0]));                  // wrong length
        var bad = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(140), 2);                     // arraySize 2
        Assert.Throws<ImgcException>(() => DdsReader.Read(bad));
        bad = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(128), 72);                    // BC1_UNORM_SRGB
        Assert.Throws<ImgcException>(() => DdsReader.Read(bad));
        Assert.Equal(59, DdsReader.Read(bad, srgbToLinear: true).Format);
        Assert.Equal(70, DdsReader.Read(bad, allowUnobserved: true).Format);
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(128), 9999);                  // unknown DXGI
        Assert.Throws<ImgcException>(() => DdsReader.Read(bad));
        Assert.Throws<ImgcException>(() => DdsReader.Read([.. Legacy(8, 8, 1, "DXT2"), .. new byte[32]]));
        Assert.Throws<ImgcException>(() => DdsReader.Read([.. "DDX "u8, .. good.AsSpan(4)]));
        // tier C (RGBA32F) is refused unless allowed — the float route reads it with allowUnobserved
        var f32 = Header(4, 4, 1, Synth.Tex2D, 1, 51);
        var dds32 = Dds(f32, Synth.RandomLevels(f32));
        Assert.Throws<ImgcException>(() => DdsReader.Read(dds32));
        Assert.Equal(51, DdsReader.Read(dds32, allowUnobserved: true).Format);
    }
}
