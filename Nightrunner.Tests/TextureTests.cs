// Port of the byte-exact cases of nightrunner-main/tests/test_texture.py: IMGC header parse, header serialisation
// (the C# writes headers only through ImgcEncoder, so round trips go parse → encode), level layout sizes, level
// padding detection, the block/plain decoders, and encode/decode bit-exactness where the C# supports it.
//
// Not ported:
//   TestHeader.test_json_roundtrip, to_json/from_json in test_stats_raw_bit_exact — no JSON form of ImgcHeader in C#.
//   TestHeader.test_bad_inputs (set_geometry refusals: depth on a 2D, mips > 63) — C# has no header builder;
//     the enum-hole / zero-dimension checks are ported through CheckGeometry; the strict length check is
//     ParseRefusesTrailingBytes.
//   TestLayout.test_check_payload_and_split_join (split/join) — no split_payload/join_payload; CheckPayload is ported
//     and levels are read back through TextureResource.
//   TestLevelPadding.test_split_join_both_layouts (DDS half), test_codec_roundtrip_extract_build — no extract/build
//     CLI in C# (DDS sources build through Core/Project/TextureSource, HdrTextureTests). The auto-detected tight/padded read is ported via TextureResource.
//   TestDds — ported in DdsTests.
//   TestPng.test_uncompressed_decode_and_preview (RG8_SNORM preview) — the C# decoder rebuilds normal Z for every
//     two-channel plain format and maps SNORM +1 to 254, so the preview is not comparable; R/G/A of RGBA16F and the
//     ARGB8 channel order are ported.
//   TestPng.test_png_to_imgc (RG8_SNORM import, stats recomputation) — ImgcEncoder refuses RG8_SNORM, and keeps the
//     template's stats by design instead of recomputing them. The RGBA8 / R8 halves are ported via ImgcEncoder.
//   TestSamples (all) — needs out/samples/textures.rpack + .rpx from the Python tools and the texture codec/CLI.
//   TestCorpus codec roundtrip — no TextureCodec in C#; the corpus tests here check header parse, layout against
//     the real part sizes, header re-serialisation and decode instead.
using System.Buffers.Binary;
using Nightrunner.Core.Rpack;
using Nightrunner.Core.Texture;

namespace Nightrunner.Tests;

public class TextureTests
{
    private static ImgcHeader Header(int w, int h, int d, byte type, int mips, byte fmt, uint flags = 0x44) =>
        ImgcHeader.Parse(Synth.ImgcHeaderBytes(w, h, d, type, mips, fmt, flags));

    private static byte[] Bgra(int w, int h, int seed = 7, int max = 256)
    {
        var rng = new Random(seed);
        var b = new byte[w * h * 4];
        for (int i = 0; i < b.Length; i++) b[i] = (byte)rng.Next(max);
        return b;
    }

    // ---- header -------------------------------------------------------------------------------------------

    [Fact]
    public void HeaderParse()
    {
        var raw = Synth.ImgcHeaderBytes(256, 128, 1, Synth.Tex2D, 9, 68, flags: 0x64, reserved: 7,
                                        mipSplit: 0x1000016800000400);
        Assert.Equal(80, raw.Length);
        Assert.Equal("IMGC"u8.ToArray(), raw[..4]);
        Assert.Equal(0x20191127u, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4)));
        var back = ImgcHeader.Parse(raw);
        Assert.Equal((256, 128, 1, 9, TextureType.Texture2D, (byte)68, 0x64u),
                     (back.Width, back.Height, back.Depth, back.MipCount, back.Type, back.Format, back.Flags));
        Assert.Equal(7, back.Reserved);
        Assert.Equal(0x1000016800000400ul, back.MipSplit);
        Assert.Equal(0.1f, back.Stats().Min[0], 6);
        Assert.Equal(0.9f, back.Stats().Max[0], 6);
        Assert.Equal(0.5f, back.Stats().Mean[2], 6);
        Assert.Equal(80, back.StoredSize);
        Assert.Equal("BC7", back.FormatName);
    }

    [Fact]
    public void HeaderSerializeRoundTrip()
    {
        // parse → rebuild with the same geometry must give the stored bytes back
        var raw = Synth.ImgcHeaderBytes(256, 128, 1, Synth.Tex2D, 9, 68, flags: 0x64, reserved: 7,
                                        mipSplit: 0x1000016800000400);
        var hd = ImgcHeader.Parse(raw);
        var enc = ImgcEncoder.Encode(hd, new byte[256 * 128 * 4], 256, 128, hd.MipCount);
        Assert.Equal(raw, enc.Header);
        Assert.Equal(hd.PayloadSize(), enc.Bitmap.Length);
        Assert.Equal(9, enc.Mips);
    }

    [Fact]
    public void ExtensionAndTail()
    {
        byte[] ext = [.. "error_no_pow2_tex_org.png"u8, 0];
        var raw = Synth.ImgcHeaderBytes(0, 0, 1, 0, 0, 0, flags: 0x02, stats: new byte[48], extension: ext);
        Assert.Equal(112, raw.Length);
        var back = ImgcHeader.Parse(raw);
        Assert.Equal(106, back.HeaderSize);
        Assert.True(back.HeaderOnly);
        Assert.Equal("error_no_pow2_tex_org.png", back.Reference);
        Assert.Equal(new byte[6], back.Tail);
        Assert.Equal(112, back.StoredSize);

        // non-zero tail bytes survive a parse → rebuild
        var raw2 = Synth.ImgcHeaderBytes(4, 4, 1, Synth.Tex2D, 1, 38, extension: ext, tail: [1, 2, 3, 4, 5, 6]);
        var h2 = ImgcHeader.Parse(raw2);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, h2.Tail);
        Assert.Equal(raw2, ImgcEncoder.Encode(h2, new byte[4 * 4 * 4], 4, 4, 1).Header);
    }

    [Fact]
    public void StatsRawBitExact()
    {
        // arbitrary bit patterns (incl. NaN-ish floats) must survive
        var stats = Enumerable.Range(0, 48).Select(i => (byte)i).ToArray();
        var raw = Synth.ImgcHeaderBytes(4, 4, 1, Synth.Tex2D, 1, 0, stats: stats);
        var hd = ImgcHeader.Parse(raw);
        Assert.Equal(stats, hd.StatsRaw);
        Assert.Equal(raw, ImgcEncoder.Encode(hd, new byte[4 * 4 * 4], 4, 4, 1).Header);
    }

    [Fact]
    public void BadInputs()
    {
        Assert.Throws<ImgcException>(() => ImgcHeader.Parse([.. "IMGC"u8, .. new byte[70]]));
        Assert.Throws<ImgcException>(() => ImgcHeader.Parse([.. "IMGX"u8, .. new byte[76]]));
        var raw = Synth.ImgcHeaderBytes(4, 4, 1, Synth.Tex2D, 1, 0);
        Assert.Throws<ImgcException>(() => ImgcHeader.Parse(Synth.PatchU32(raw, 4, 0x20191128)));   // version
        Assert.Throws<ImgcException>(() => ImgcHeader.Parse(Synth.PatchU32(raw, 8, 79)));            // header_size < 80
        Assert.Throws<ImgcException>(() => ImgcHeader.Parse(Synth.PatchU32(raw, 8, 96)));            // pads past the part
        ImgcHeader.Parse([.. raw, .. new byte[16]], strictLength: false);
        // geometry the layout refuses: enum hole, zero dimension, mip count 0, type 3
        Assert.Throws<ImgcException>(() => Header(4, 4, 1, Synth.Tex2D, 1, 4).LevelLayout());
        Assert.Throws<ImgcException>(() => Header(0, 4, 1, Synth.Tex2D, 1, 0).CheckGeometry());
        Assert.Throws<ImgcException>(() => Header(4, 4, 1, Synth.Tex2D, 0, 0).CheckGeometry());
        Assert.Throws<ImgcException>(() => Header(4, 4, 1, 3, 1, 0).CheckGeometry());
    }

    [Fact]
    public void ParseRefusesTrailingBytes()
    {
        var raw = Synth.ImgcHeaderBytes(4, 4, 1, Synth.Tex2D, 1, 0);
        Assert.Throws<ImgcException>(() => ImgcHeader.Parse([.. raw, .. new byte[16]]));
    }

    // ---- level layout -------------------------------------------------------------------------------------

    private static (int Mip, int Face, int W, int H, int D, long Offset, long Size, long Padded) T(ImgcLevel l) =>
        (l.Mip, l.Face, l.Width, l.Height, l.Depth, l.Offset, l.Size, l.PaddedSize);

    [Fact]
    public void Layout2DBlock()
    {
        var hd = Header(256, 256, 1, Synth.Tex2D, 9, 59);
        var s = hd.LevelLayout();
        Assert.Equal((0, 0, 256, 256, 1, 0L, 32768L, 32768L), T(s[0]));
        Assert.Equal((6, 0, 4, 4, 1, 43680L, 8L, 16L), T(s[6]));
        Assert.Equal((8, 0, 1, 1, 1, 43712L, 8L, 16L), T(s[8]));
        Assert.Equal(43728, hd.PayloadSize());
    }

    [Fact]
    public void Layout2DNonBlockNpot()
    {
        var hd = Header(160, 554, 1, Synth.Tex2D, 10, 46);
        var s = hd.LevelLayout();
        Assert.Equal(160 * 554 * 8, s[0].Size);
        Assert.Equal((80, 277, 1), (s[1].Width, s[1].Height, s[1].Depth));
        Assert.Equal(80 * 277 * 8, s[1].Size);
        Assert.Equal((1, 1, 1), (s[9].Width, s[9].Height, s[9].Depth));
        Assert.Equal(s.Sum(l => l.PaddedSize), hd.PayloadSize());
        Assert.Equal(0, hd.PayloadSize() % 16);
    }

    [Fact]
    public void LayoutCube()
    {
        var hd = Header(64, 64, 1, Synth.TexCube, 7, 66);
        var s = hd.LevelLayout();
        Assert.Equal(42, s.Count);
        Assert.Equal([.. Enumerable.Range(0, 6).Select(f => (0, f)), (1, 0)], s.Take(7).Select(l => (l.Mip, l.Face)));   // mip-major
        Assert.Equal(5 * 4096, s[5].Offset);     // face 5 of mip 0
        Assert.Equal(6 * 4096, s[6].Offset);     // mip 1 face 0
        Assert.Equal(6 * (4096 + 1024 + 256 + 64 + 16 + 16 + 16), hd.PayloadSize());
    }

    [Fact]
    public void LayoutVolume()
    {
        var hd = Header(32, 16, 8, Synth.TexVolume, 6, 38);
        var s = hd.LevelLayout();
        Assert.Equal((32, 16, 8), (s[0].Width, s[0].Height, s[0].Depth));
        Assert.Equal(32 * 16 * 8 * 4, s[0].Size);
        Assert.Equal((4, 2, 1), (s[3].Width, s[3].Height, s[3].Depth));
        Assert.Equal((1, 1, 1), (s[5].Width, s[5].Height, s[5].Depth));
        Assert.Equal(16 * 8 * 4, s[1].Size / s[1].Depth);                        // slice size
        Assert.Equal(32, Header(2, 2, 3, Synth.TexVolume, 1, 59).PayloadSize());  // engine_pc default_env.dds
    }

    [Fact]
    public void CheckPayload()
    {
        var hd = Header(37, 5, 1, Synth.Tex2D, 6, 63);
        long size = hd.PayloadSize();
        Assert.Equal(6, hd.CheckPayload(size).Count);
        Assert.Throws<ImgcException>(() => hd.CheckPayload(size + 16));
        Assert.Throws<ImgcException>(() => hd.CheckPayload(size, levelPadding: 0));
    }

    [Fact]
    public void LevelBytesTable()
    {
        foreach (var (il, w, h, want) in new (byte, int, int, long)[] { (59, 5, 5, 32), (63, 1, 1, 8), (68, 4, 4, 16), (0, 7, 3, 21), (46, 2, 2, 32) })
            Assert.Equal(want, ImgcFormats.LevelBytes(il, w, h, 1));
    }

    // ---- level padding (third-party tight layout) ---------------------------------------------------------
    // a third-party frank.rpack: 12 × 4096² BC1/BC4, part 24 bytes short = the three 8-byte tail mips stored
    // without their 16-byte stride. Synthetic 64² BC1 with 7 mips has the same 3 × 8 → 3 × 16 tail.

    [Fact]
    public void LevelPaddingLayoutAndDetection()
    {
        var hd = Header(64, 64, 1, Synth.Tex2D, 7, 59);
        var padded = hd.LevelLayout();
        var tight = hd.LevelLayout(0);
        Assert.Equal([2048L, 512, 128, 32, 8, 8, 8], padded.Select(l => l.Size));
        Assert.Equal([2048L, 512, 128, 32, 16, 16, 16], padded.Select(l => l.PaddedSize));
        Assert.Equal([2048L, 512, 128, 32, 8, 8, 8], tight.Select(l => l.PaddedSize));
        Assert.Equal([0L, 2048, 2560, 2688, 2720, 2728, 2736], tight.Select(l => l.Offset));
        Assert.Equal((2768L, 2744L), (hd.PayloadSize(), hd.PayloadSize(0)));
        Assert.Equal(16, hd.DetectLevelPadding(2768));
        Assert.Equal(0, hd.DetectLevelPadding(2744));
        foreach (long bad in new long[] { 2745, 2760, 2769, 0 })
            Assert.Throws<ImgcException>(() => hd.DetectLevelPadding(bad));
        // a texture whose every level is already a multiple of 16 has one layout only: it is reported as stock
        var h16 = Header(16, 16, 1, Synth.Tex2D, 1, 38);
        Assert.Equal(h16.PayloadSize(), h16.PayloadSize(0));
        Assert.Equal(16, h16.DetectLevelPadding(1024));
    }

    [Fact]
    public void LevelPaddingOtherThan0Or16IsRefused()
    {
        Assert.Throws<ImgcException>(() => Header(64, 64, 1, Synth.Tex2D, 7, 59).LevelLayout(8));
    }

    [Fact]
    public void TightAndPaddedReadBackThroughAPack()
    {
        // one tight and one padded texture: the reader detects the padding and serves the same levels from both
        var raw = Synth.ImgcHeaderBytes(64, 64, 1, Synth.Tex2D, 7, 59);
        var hd = ImgcHeader.Parse(raw);
        var levels = Synth.RandomLevels(hd);
        var bm0 = Synth.JoinLevels(hd, levels, 0);
        var bm16 = Synth.JoinLevels(hd, levels, 16);
        Assert.Equal((2744, 2768), (bm0.Length, bm16.Length));
        Assert.Equal(levels.SelectMany(l => l), bm0);     // back-to-back, nothing inserted

        using var tmp = new TempDir();
        var pk = tmp.OpenPack(
        [
            Synth.Texture("tight.png", raw, bm0),
            Synth.Texture("padded.png", raw, bm16),
            Synth.Texture("short.png", raw, bm16[..^16]),
            Synth.Texture("ref.png", Synth.ImgcHeaderBytes(0, 0, 1, 0, 0, 0, flags: 0x02, extension: [.. "x.png"u8, 0]), null),
        ]);
        var tight = TextureResource.Open(pk, 0);
        var pad = TextureResource.Open(pk, 1);
        Assert.Equal((0, 16), (tight.LevelPadding, pad.LevelPadding));
        Assert.True(tight.HasBitmap && pad.HasBitmap);
        for (int i = 0; i < levels.Count; i++)
        {
            Assert.Equal(levels[i], tight.ReadLevel(tight.Levels[i]));
            Assert.Equal(levels[i], pad.ReadLevel(pad.Levels[i]));
        }
        Assert.Equal(64, tight.Decode(tight.TopLevel!.Value).Width);

        var bad = TextureResource.Open(pk, 2);
        Assert.False(bad.HasBitmap);
        Assert.Contains("2768", bad.PayloadProblem);
        Assert.Contains("2744", bad.PayloadProblem);

        var headerOnly = TextureResource.Open(pk, 3);
        Assert.True(headerOnly.Header.HeaderOnly);
        Assert.Null(headerOnly.BitmapPart);
        Assert.Null(headerOnly.PayloadProblem);
        Assert.Equal("x.png", headerOnly.Header.Reference);
    }

    // ---- decoders -----------------------------------------------------------------------------------------

    private static byte Red(DecodedImage img, int i) => img.Bgra[i * 4 + 2];
    private static byte Green(DecodedImage img, int i) => img.Bgra[i * 4 + 1];

    [Fact]
    public void Bc4Decoder()
    {
        // unsigned: a=255 > b=0 → 8-entry ramp; all indices 0 → 1.0 everywhere; indices 1 → 0.0
        var v = TextureDecoder.Decode(63, [255, 0, 0, 0, 0, 0, 0, 0], 4, 4);
        Assert.All(Enumerable.Range(0, 16), i => Assert.Equal(255, Red(v, i)));
        v = TextureDecoder.Decode(63, [255, 0, 0x49, 0x92, 0x24, 0x49, 0x92, 0x24], 4, 4);   // index 1 (001 001 ...)
        Assert.All(Enumerable.Range(0, 16), i => Assert.Equal(0, Red(v, i)));
        // signed: a=-128 → clamps to -1; b = 127 → 1; a<b → 6-entry ramp, code 6 = -1, code 7 = 1
        v = TextureDecoder.Decode(62, [0x80, 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], 4, 4);      // index 7
        Assert.All(Enumerable.Range(0, 16), i => Assert.Equal(255, Red(v, i)));
        v = TextureDecoder.Decode(62, [0x80, 0x7F, 0xB6, 0x6D, 0xDB, 0xB6, 0x6D, 0xDB], 4, 4);      // index 6
        Assert.All(Enumerable.Range(0, 16), i => Assert.Equal(0, Red(v, i)));
        // BC5: [R block][G block]
        v = TextureDecoder.Decode(65, [255, 0, 0, 0, 0, 0, 0, 0, 0, 255, 0, 0, 0, 0, 0, 0], 4, 4, rebuildNormalZ: false);
        Assert.All(Enumerable.Range(0, 16), i => Assert.Equal((255, 0), (Red(v, i), Green(v, i))));
        // partial block crop
        v = TextureDecoder.Decode(63, [128, 0, 0, 0, 0, 0, 0, 0], 3, 2);
        Assert.Equal((3, 2, 24), (v.Width, v.Height, v.Bgra.Length));
        Assert.All(Enumerable.Range(0, 6), i => Assert.Equal(128, Red(v, i)));
    }

    [Fact]
    public void UncompressedDecode()
    {
        // 2×1 ARGB8 → bytes B,G,R,A
        var v = TextureDecoder.Decode(32, [1, 2, 3, 4, 5, 6, 7, 8], 2, 1);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, v.Bgra);
        Assert.Equal((3, 2, 1, 4), (Red(v, 0), Green(v, 0), v.Bgra[0], v.Bgra[3]));
        // RGBA16F clipped: 2.0 → 255, -1.0 → 0, NaN alpha → 0
        var raw = new byte[8];
        BinaryPrimitives.WriteHalfLittleEndian(raw, (Half)2.0f);
        BinaryPrimitives.WriteHalfLittleEndian(raw.AsSpan(2), (Half)(-1.0f));
        BinaryPrimitives.WriteHalfLittleEndian(raw.AsSpan(4), (Half)0.5f);
        BinaryPrimitives.WriteHalfLittleEndian(raw.AsSpan(6), Half.NaN);
        v = TextureDecoder.Decode(46, raw, 1, 1);
        Assert.Equal((255, 0, 0), (Red(v, 0), Green(v, 0), v.Bgra[3]));
        // BC1 preview shape
        v = TextureDecoder.Decode(59, new byte[8], 2, 2);
        Assert.Equal(16, v.Bgra.Length);
    }

    [Fact]
    public void HalfFloatPreviewRounds()
    {
        var raw = new byte[8];
        BinaryPrimitives.WriteHalfLittleEndian(raw.AsSpan(4), (Half)0.5f);
        Assert.Equal(128, TextureDecoder.Decode(46, raw, 1, 1).Bgra[0]);
    }

    // ---- encoder ------------------------------------------------------------------------------------------

    [Fact]
    public void EncodeRgba8()
    {
        // test_png_to_imgc, RGBA8 / R8 halves: 10×6, pixel (0,0) transparent black, the rest (200, 100, 0, 255)
        var template = ImgcHeader.Parse(Synth.ImgcHeaderBytes(4, 4, 1, Synth.Tex2D, 1, 38, flags: 0x44));
        var bgra = new byte[10 * 6 * 4];
        for (int i = 1; i < 60; i++)
        {
            bgra[i * 4] = 0;
            bgra[i * 4 + 1] = 100;
            bgra[i * 4 + 2] = 200;
            bgra[i * 4 + 3] = 255;
        }
        var enc = ImgcEncoder.Encode(template, bgra, 10, 6);
        var h = ImgcHeader.Parse(enc.Header);
        Assert.Equal((10, 6, 4, (byte)38, 0x44u), (h.Width, h.Height, h.MipCount, h.Format, h.Flags));
        Assert.Equal(h.PayloadSize(), enc.Bitmap.Length);
        Assert.Equal(template.StatsRaw, h.StatsRaw);     // stats are carried from the template, never recomputed
        var levels = h.CheckPayload(enc.Bitmap.Length);
        Assert.Equal(new byte[] { 200, 100, 0, 255 }, enc.Bitmap[4..8]);
        Assert.Equal(4, levels[^1].Size);

        var r8 = ImgcHeader.Parse(Synth.ImgcHeaderBytes(4, 4, 1, Synth.Tex2D, 1, 0));
        enc = ImgcEncoder.Encode(r8, bgra, 10, 6, mipCount: 1);
        h = ImgcHeader.Parse(enc.Header);
        Assert.Equal(((byte)0, 1, 64), (h.Format, h.MipCount, enc.Bitmap.Length));
        Assert.Equal(new byte[] { 0, 200 }, enc.Bitmap[..2]);
    }

    [Fact]
    public void EncoderRefusals()
    {
        Assert.Null(ImgcEncoder.Refusal(66));         // BC6H: written from floats ...
        Assert.True(ImgcEncoder.IsFloat(66));
        Assert.NotNull(ImgcEncoder.Refusal(16));      // RG8_SNORM (Python's PNG import writes it)
        Assert.NotNull(ImgcEncoder.Refusal(4));       // enum hole
        var cube = ImgcHeader.Parse(Synth.ImgcHeaderBytes(4, 4, 1, Synth.TexCube, 1, 38));
        Assert.Throws<ImgcException>(() => ImgcEncoder.Encode(cube, new byte[64], 4, 4));
        var bc6 = ImgcHeader.Parse(Synth.ImgcHeaderBytes(4, 4, 1, Synth.Tex2D, 1, 66));
        Assert.Throws<ImgcException>(() => ImgcEncoder.Encode(bc6, new byte[64], 4, 4));   // ... never from 8-bit
    }

    [Theory]
    [InlineData((byte)38)]   // RGBA8
    [InlineData((byte)32)]   // ARGB8
    public void PlainFourChannelRoundTripIsExact(byte format)
    {
        var template = ImgcHeader.Parse(Synth.ImgcHeaderBytes(4, 4, 1, Synth.Tex2D, 1, format));
        var bgra = Bgra(8, 8);
        var enc = ImgcEncoder.Encode(template, bgra, 8, 8, mipCount: 1);
        Assert.Equal(bgra, TextureDecoder.Decode(format, enc.Bitmap, 8, 8).Bgra);
    }

    /// <summary>B2: an alpha-tested BC1 texture keeps its holes (punch-through mode); an opaque one stays opaque.</summary>
    [Theory]
    [InlineData((byte)59)]   // BC1
    [InlineData((byte)70)]   // BC1_SRGB
    public void Bc1CutoutKeepsItsHoles(byte format)
    {
        var template = ImgcHeader.Parse(Synth.ImgcHeaderBytes(4, 4, 1, Synth.Tex2D, 1, format));
        var bgra = Bgra(8, 8);
        for (int i = 0; i < 64; i++) bgra[i * 4 + 3] = (byte)((i % 8 + i / 8) % 3 == 0 ? 0 : 255);
        var dec = TextureDecoder.Decode(format, ImgcEncoder.Encode(template, bgra, 8, 8, mipCount: 1).Bitmap, 8, 8);
        for (int i = 0; i < 64; i++) Assert.Equal(bgra[i * 4 + 3], dec.Bgra[i * 4 + 3]);

        for (int i = 0; i < 64; i++) bgra[i * 4 + 3] = 255;
        dec = TextureDecoder.Decode(format, ImgcEncoder.Encode(template, bgra, 8, 8, mipCount: 1).Bitmap, 8, 8);
        Assert.All(Enumerable.Range(0, 64), i => Assert.Equal(255, dec.Bgra[i * 4 + 3]));
    }

    [Theory]
    [InlineData((byte)0)]    // R8
    [InlineData((byte)15)]   // RG8
    public void PlainRedGreenRoundTripIsExact(byte format)
    {
        var template = ImgcHeader.Parse(Synth.ImgcHeaderBytes(4, 4, 1, Synth.Tex2D, 1, format));
        var bgra = Bgra(8, 8);
        var enc = ImgcEncoder.Encode(template, bgra, 8, 8, mipCount: 1);
        var dec = TextureDecoder.Decode(format, enc.Bitmap, 8, 8);
        for (int i = 0; i < 64; i++)
        {
            Assert.Equal(bgra[i * 4 + 2], dec.Bgra[i * 4 + 2]);
            if (format == 15) Assert.Equal(bgra[i * 4 + 1], dec.Bgra[i * 4 + 1]);
        }
    }

    /// <summary>
    /// BC4/BC5 (the project's own block encoder): the decoded values are exactly palette entries and the extremes
    /// survive, so encode → decode → encode reproduces the blocks bit for bit.
    /// </summary>
    [Theory]
    [InlineData((byte)63)]   // BC4
    [InlineData((byte)62)]   // BC4_SNORM
    [InlineData((byte)65)]   // BC5
    [InlineData((byte)64)]   // BC5_SNORM
    public void RedGreenBlocksAreStableUnderDecodeEncode(byte format)
    {
        bool signed = ImgcFormats.Name(format).EndsWith("SNORM");
        var template = ImgcHeader.Parse(Synth.ImgcHeaderBytes(4, 4, 1, Synth.Tex2D, 1, format));
        var bgra = Bgra(16, 12, max: signed ? 255 : 256);          // SNORM stores -127..127, shown 0..254
        var first = ImgcEncoder.Encode(template, bgra, 16, 12, mipCount: 1);
        var dec = TextureDecoder.Decode(format, first.Bitmap, 16, 12, rebuildNormalZ: false);
        var second = ImgcEncoder.Encode(template, dec.Bgra, 16, 12, mipCount: 1);
        Assert.Equal(first.Bitmap, second.Bitmap);
        // the first pass keeps each block's extremes exactly
        for (int i = 0; i < 16 * 12; i++)
            Assert.InRange(Math.Abs(dec.Bgra[i * 4 + 2] - bgra[i * 4 + 2]), 0, 255 / 14 + 1);
    }

    [Fact]
    public void FlatBlocksEncodeExactly()
    {
        var template = ImgcHeader.Parse(Synth.ImgcHeaderBytes(4, 4, 1, Synth.Tex2D, 1, 64));   // BC5_SNORM
        var bgra = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i++)
        {
            bgra[i * 4 + 2] = 127;   // 0.0
            bgra[i * 4 + 1] = 254;   // +1.0
        }
        var enc = ImgcEncoder.Encode(template, bgra, 4, 4, mipCount: 1);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 127, 127, 0, 0, 0, 0, 0, 0 }, enc.Bitmap);
        var dec = TextureDecoder.Decode(64, enc.Bitmap, 4, 4, rebuildNormalZ: false);
        Assert.All(Enumerable.Range(0, 16), i => Assert.Equal((127, 254), (Red(dec, i), Green(dec, i))));
    }

    // ---- corpus (install-backed) --------------------------------------------------------------------------

    [Fact]
    public async Task CommonTexturesHeadAndHeaderOnly()
    {
        var gi = Installs.Require("dltb");
        var path = Path.Combine(gi.Assets!, "common_textures_0_pc.rpack");
        if (!File.Exists(path)) Assert.Skip("common_textures_0_pc.rpack missing");
        using var cat = new RpackCatalog();
        await cat.LoadAsync([path], ct: TestContext.Current.CancellationToken);
        var pk = cat.IndexedPacks[0].Pack!;

        int n = 0, rebuilt = 0;
        var decoded = new HashSet<byte>();
        for (int i = 0; i < pk.Count && n < 200; i++)
        {
            if (pk.Logicals[i].Type != 0x20) continue;
            n++;
            var tex = TextureResource.Open(pk, i);
            var hd = tex.Header;
            if (hd.HeaderOnly) continue;
            Assert.Null(tex.PayloadProblem);
            Assert.Equal(16, tex.LevelPadding);
            Assert.Equal(tex.BitmapSize, hd.PayloadSize());

            // header re-serialisation: rebuild with the same geometry and compare to the stored part
            if (hd.Type == TextureType.Texture2D && ImgcEncoder.CanEncode(hd.Format) && hd.Width * hd.Height <= 128 * 128
                && rebuilt < 40)
            {
                var stored = pk.ReadPart((int)pk.Logicals[i].FirstPart);
                var enc = ImgcEncoder.Encode(hd, new byte[hd.Width * hd.Height * 4], hd.Width, hd.Height, hd.MipCount);
                Assert.Equal(stored, enc.Header);
                Assert.Equal(tex.BitmapSize, enc.Bitmap.Length);
                rebuilt++;
            }
            if (TextureDecoder.CanDecode(hd.Format) && decoded.Add(hd.Format))
            {
                var top = tex.TopLevel!.Value;
                Assert.Equal(top.Width * top.Height * 4, tex.Decode(top).Bgra.Length);
            }
        }
        Assert.Equal(200, n);
        Assert.True(rebuilt > 0, "no small encodable texture among the first 200");

        // by name: the index moves with every game patch
        var hits = cat.Lookup("bullet_trail_0000_fxd.dds", 0x20);
        Assert.Single(hits);
        var (_, index) = cat.Split(hits[0]);
        Assert.Equal(1, pk.Logicals[index].PartCount);                  // header-only: no 0x21 bitmap part
        Assert.Equal(0x20, pk.PartType((int)pk.Logicals[index].FirstPart));
        var ho = TextureResource.Open(pk, index);
        Assert.True(ho.Header.HeaderOnly);
        Assert.Null(ho.PayloadProblem);
        Assert.False(ho.HasBitmap);
    }

    [Fact]
    public void EngineCubesAndVolumes()
    {
        var gi = Installs.Require("dltb");
        var path = Path.Combine(gi.Assets!, "engine_pc.rpack");
        if (!File.Exists(path)) Assert.Skip("engine_pc.rpack missing");
        using var pk = RpackFile.Open(path);
        var kinds = new HashSet<TextureType>();
        for (int i = 0; i < pk.Count; i++)
        {
            if (pk.Logicals[i].Type != 0x20) continue;
            var tex = TextureResource.Open(pk, i);
            if (tex.Header.Type == TextureType.Texture2D) continue;
            kinds.Add(tex.Header.Type);
            Assert.Null(tex.PayloadProblem);
            Assert.Equal(tex.BitmapSize, tex.Header.PayloadSize(tex.LevelPadding!.Value));
            Assert.Equal(tex.Header.MipCount * tex.Header.Faces, tex.Levels.Count);
        }
        Assert.Equal(new HashSet<TextureType> { TextureType.Cube, TextureType.Volume }, kinds);
    }
}
