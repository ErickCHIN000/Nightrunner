using System.Buffers.Binary;
using Nightrunner.Core.Project;
using Nightrunner.Core.Rpack;
using Nightrunner.Core.Texture;

namespace Nightrunner.Tests;

/// <summary>
/// BC6H encoding, the HDR sources (float DDS, BC6H DDS, Radiance .hdr) and the project build of an HDR texture.
/// Synthetic cases always run; the shipped round trips skip without an install.
/// </summary>
public class HdrTextureTests
{
    /// <summary>A smooth HDR gradient from 0.1 upwards (a decade per ~30 px) with 1 % noise.</summary>
    internal static FloatImage Hdr(int w, int h, int seed = 5, bool signed = false)
    {
        var rng = new Random(seed);
        var img = new FloatImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double t = (x + y * 0.7) / 90;                 // 3 decades over ~90 px: gradients like a sky
                for (int c = 0; c < 3; c++)
                {
                    double v = Math.Pow(10, -1 + 3 * t + c * 0.15) * (1 + 0.01 * rng.NextDouble());
                    if (signed && ((x / 4 + y / 4 + c) & 1) == 1) v = -v;       // one sign per block and channel
                    img.Rgba[(y * w + x) * 4 + c] = (float)v;
                }
                img.Rgba[(y * w + x) * 4 + 3] = 1;
            }
        return img;
    }

    private static double Log2Error(float a, float b) => Math.Abs(Math.Log2(Math.Abs(a) + 1.0 / 1024) - Math.Log2(Math.Abs(b) + 1.0 / 1024));

    private static (double Rms, double Max) Compare(FloatImage a, FloatImage b)
    {
        double sum = 0, max = 0;
        int n = 0;
        for (int i = 0; i < a.Rgba.Length; i++)
        {
            if ((i & 3) == 3) continue;
            double e = Log2Error(a.Rgba[i], b.Rgba[i]);
            sum += e * e;
            max = Math.Max(max, e);
            n++;
        }
        return (Math.Sqrt(sum / n), max);
    }

    // ---- block codec ---------------------------------------------------------------------------------------

    /// <summary>
    /// The one-region decoder here against BCnEncoder.Net's, on random blocks of each of the four modes, both
    /// signednesses: the bit layout (including the reversed high base bits of modes 12-14) and the endpoint maths agree.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneRegionDecoderMatchesTheLibrary(bool signed)
    {
        var rng = new Random(signed ? 2 : 1);
        byte fmt = signed ? (byte)67 : (byte)66;
        Span<ushort> mine = stackalloc ushort[48];
        foreach (var mode in Bc6h.OneRegion)
        {
            for (int i = 0; i < 500; i++)
            {
                var block = new byte[16];
                rng.NextBytes(block);
                block[0] = (byte)(block[0] & ~31 | mode.Code);
                block[8] &= 0xFD;           // bit 65: the anchor index's top bit is implicit, keep the others random
                Assert.True(Bc6h.TryDecodeBlock(block, signed, mine));
                var lib = TextureDecoder.DecodeFloat(fmt, block, 4, 4);
                for (int p = 0; p < 16; p++)
                    for (int c = 0; c < 3; c++)
                    {
                        ushort want = BitConverter.HalfToUInt16Bits((Half)lib.Rgba[p * 4 + c]);
                        if (want == 0x8000) want = 0;       // -0 and +0 decode the same
                        ushort got = mine[p * 3 + c] == 0x8000 ? (ushort)0 : mine[p * 3 + c];
                        Assert.True(want == got, $"{mode} block {i} pixel {p} channel {c}: {got:X4} != {want:X4}");
                    }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EncodeDecodeSmallHdrImage(bool signed)
    {
        var src = Hdr(20, 12, signed: signed);         // not a multiple of 4: edge blocks repeat
        var blocks = Bc6h.Encode(src, signed);
        Assert.Equal(5 * 3 * 16, blocks.Length);
        var back = TextureDecoder.DecodeFloat(signed ? (byte)67 : (byte)66, blocks, 20, 12);
        var (rms, max) = Compare(src, back);
        Assert.True(rms < 0.02 && max < 0.15, $"log2 error rms {rms:F4} max {max:F4}");
        for (int i = 0; i < src.Rgba.Length; i++)
            if ((i & 3) != 3) Assert.Equal(Math.Sign(src.Rgba[i]), Math.Sign(back.Rgba[i]));
        // every block is one of the four one-region modes and decodes identically here
        Span<ushort> mine = stackalloc ushort[48];
        for (int b = 0; b < blocks.Length; b += 16)
            Assert.True(Bc6h.TryDecodeBlock(blocks.AsSpan(b, 16), signed, mine));
    }

    /// <summary>
    /// A decoded BC6H surface is exactly representable, so re-encoding it must land very close to it — and most
    /// blocks come back bit for bit.
    /// </summary>
    [Fact]
    public void DecodedBlocksReencodeAlmostExactly()
    {
        var first = Bc6h.Encode(Hdr(64, 64, seed: 9), false);
        var decoded = TextureDecoder.DecodeFloat(66, first, 64, 64);
        var second = Bc6h.Encode(decoded, false);
        var (rms, max) = Compare(decoded, TextureDecoder.DecodeFloat(66, second, 64, 64));
        int same = 0;
        for (int b = 0; b < first.Length; b += 16)
            if (first.AsSpan(b, 16).SequenceEqual(second.AsSpan(b, 16))) same++;
        Assert.True(rms < 0.0005 && max < 0.01 && same * 16 >= first.Length * 95 / 100,
                    $"log2 error rms {rms:F5} max {max:F4}, {same} of {first.Length / 16} blocks identical");
    }

    /// <summary>
    /// Tiny and huge values in one block: BCnEncoder.Net wraps the endpoint delta here and returns 65,504 for pixels
    /// near zero. Nothing may come back larger than the block's own maximum, and UF16 clamps out-of-range input.
    /// </summary>
    [Fact]
    public void WideRangeBlocksDoNotWrap()
    {
        var img = new FloatImage(4, 4);
        float[] values = [0, 1e-4f, 0.02f, 3, 60000, 1e9f, float.PositiveInfinity, -5, float.NaN, 0.5f, 1, 2, 4, 8, 16, 32];
        for (int p = 0; p < 16; p++)
        {
            img.Rgba[p * 4] = values[p];
            img.Rgba[p * 4 + 1] = values[15 - p];
            img.Rgba[p * 4 + 2] = values[(p * 7) % 16];
            img.Rgba[p * 4 + 3] = 1;
        }
        Assert.Equal(12, Bc6h.OutOfRange(img, signed: false));   // 1e9, +inf, -5, NaN in three channels
        var back = TextureDecoder.DecodeFloat(66, Bc6h.Encode(img, false), 4, 4);
        for (int i = 0; i < 64; i++)
        {
            if ((i & 3) == 3) continue;
            float v = img.Rgba[i], got = back.Rgba[i];
            Assert.InRange(got, 0f, 65504f);
            if (float.IsNaN(v) || v <= 0.02f) Assert.True(got < 1000, $"{v} came back as {got}");
        }
    }

    [Fact]
    public void FloatEncoderLevelsAndRefusals()
    {
        var template = ImgcHeader.Parse(Synth.ImgcHeaderBytes(8, 8, 1, Synth.Tex2D, 4, 66));
        var chain = Hdr(8, 8).MipChain(4);
        var enc = ImgcEncoder.EncodeFloat(template, chain, 4);
        var h = ImgcHeader.Parse(enc.Header);
        Assert.Equal((8, 8, 4, (byte)66), (h.Width, h.Height, h.MipCount, h.Format));
        Assert.Equal(h.PayloadSize(), enc.Bitmap.Length);
        Assert.Equal(template.StatsRaw, h.StatsRaw);                       // carried, never recomputed

        var cube = ImgcHeader.Parse(Synth.ImgcHeaderBytes(8, 8, 1, Synth.TexCube, 2, 66));
        var faces = Enumerable.Range(0, 6).Select(f => Hdr(8, 8, seed: f)).ToList();
        var surfaces = faces.Select(f => f).Concat(faces.Select(f => f.Downsample())).ToList();
        Assert.Equal(cube.PayloadSize(), ImgcEncoder.EncodeFloat(cube, surfaces, 2).Bitmap.Length);

        Assert.Throws<ImgcException>(() => ImgcEncoder.EncodeFloat(template, chain, 3));          // wrong count
        Assert.Throws<ImgcException>(() => ImgcEncoder.EncodeFloat(template, [chain[0], chain[2], chain[1], chain[3]], 4));
        Assert.Throws<ImgcException>(() => ImgcEncoder.Encode(template, new byte[256], 8, 8));    // 8-bit into BC6H
        var bc1 = ImgcHeader.Parse(Synth.ImgcHeaderBytes(8, 8, 1, Synth.Tex2D, 1, 59));
        Assert.Throws<ImgcException>(() => ImgcEncoder.EncodeFloat(bc1, [chain[0]], 1));

        // RGBA16F from floats is the half conversion
        var f16 = ImgcHeader.Parse(Synth.ImgcHeaderBytes(8, 8, 1, Synth.Tex2D, 1, 46));
        var plain = ImgcEncoder.EncodeFloat(f16, [chain[0]], 1);
        var round = TextureDecoder.DecodeFloat(46, plain.Bitmap, 8, 8);
        for (int i = 0; i < 256; i++) Assert.Equal((float)(Half)chain[0].Rgba[i], round.Rgba[i]);
    }

    // ---- Radiance .hdr -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(40, 6)]      // run-length scanlines
    [InlineData(5, 3)]       // too narrow for run-length: flat
    public void RadianceRoundTrip(int w, int h)
    {
        var src = Hdr(w, h);
        for (int i = 0; i < w * 4; i += 4) src.Rgba[i] = src.Rgba[i + 1] = src.Rgba[i + 2] = 7;   // a run
        var back = RadianceHdr.Read(RadianceHdr.Write(src));
        Assert.Equal((w, h), (back.Width, back.Height));
        for (int p = 0; p < w * h; p++)
        {
            float max = Math.Max(src.Rgba[p * 4], Math.Max(src.Rgba[p * 4 + 1], src.Rgba[p * 4 + 2]));
            for (int c = 0; c < 3; c++)
                Assert.True(Math.Abs(src.Rgba[p * 4 + c] - back.Rgba[p * 4 + c]) <= max / 200 + 1e-12,
                            $"pixel {p} channel {c}: {src.Rgba[p * 4 + c]} -> {back.Rgba[p * 4 + c]}");
            Assert.Equal(1f, back.Rgba[p * 4 + 3]);
        }
    }

    [Fact]
    public void RadianceRefusals()
    {
        byte[] File(string header, int w = 2, int h = 1) =>
            [.. System.Text.Encoding.ASCII.GetBytes(header), .. new byte[w * h * 4]];
        Assert.Equal(2, RadianceHdr.Read(File("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 2\n")).Width);
        Assert.Equal(2, RadianceHdr.Read(File("#?RGBE\nEXPOSURE=1.0\n\n-Y 1 +X 2\n")).Width);
        foreach (var bad in new[]
                 {
                     "P6\n\n-Y 1 +X 2\n",
                     "#?RADIANCE\nFORMAT=32-bit_rle_xyze\n\n-Y 1 +X 2\n",
                     "#?RADIANCE\nEXPOSURE=2\n\n-Y 1 +X 2\n",
                     "#?RADIANCE\nCOLORCORR=1 0.5 1\n\n-Y 1 +X 2\n",
                     "#?RADIANCE\n\n+Y 1 +X 2\n",
                 })
            Assert.Throws<ImgcException>(() => RadianceHdr.Read(File(bad)));
        Assert.Throws<ImgcException>(() => RadianceHdr.Read(File("#?RADIANCE\n\n-Y 2 +X 2\n")));      // truncated
    }

    // ---- project build -------------------------------------------------------------------------------------

    /// <summary>The stored header part and bitmap part of a texture.</summary>
    private static (byte[] Header, byte[] Bitmap) Parts(TextureResource tex)
    {
        var lg = tex.Pack.Logicals[tex.LogicalIndex];
        byte[]? header = null;
        for (int k = 0; k < lg.PartCount && header is null; k++)
            if (tex.Pack.PartType((int)lg.FirstPart + k) == 0x20) header = tex.Pack.ReadPart((int)lg.FirstPart + k);
        return (header!, tex.Pack.ReadPart(tex.BitmapPart!.Value));
    }

    private static (byte[], int, int) NoPng(string path) => throw new InvalidOperationException($"PNG loader called for {path}");

    /// <summary>A shipped-like BC6H texture in a synthetic pack: 64x32, 7 mips.</summary>
    private static TextureResource Bc6Texture(TempDir tmp, string name)
    {
        var header = Synth.ImgcHeaderBytes(64, 32, 1, Synth.Tex2D, 7, 66, flags: 0x64);
        var enc = ImgcEncoder.EncodeFloat(ImgcHeader.Parse(header), Hdr(64, 32).MipChain(7), 7);
        var pack = tmp.OpenPack([Synth.Texture(name, enc.Header, enc.Bitmap)], name: "source.rpack");
        return TextureResource.Open(pack, 0);
    }

    private static TextureAsset AddHdr(ModProject project, TextureResource tex)
    {
        var dir = Path.Combine(project.Folder, TextureAsset.Folder);
        Directory.CreateDirectory(dir);
        var dds = Path.Combine(dir, TextureAsset.HdrName(tex.Name));
        File.WriteAllBytes(dds, DdsWriter.ToFloatDds(tex));
        var asset = TextureAsset.From(tex.Pack, tex.LogicalIndex, tex, "source.rpack", "dltb");
        asset.Save(dds + TextureAsset.Extension);
        return asset;
    }

    [Fact]
    public void FloatDdsIsLosslessAndCarriesEveryMip()
    {
        using var tmp = new TempDir();
        var tex = Bc6Texture(tmp, "sky_hqrfl.dds");
        var dds = DdsWriter.ToFloatDds(tex);
        var df = DdsReader.Read(dds);
        Assert.Equal((64, 32, 7, (byte)46), (df.Width, df.Height, df.MipCount, df.Format));
        var levels = DdsReader.Levels(df, dds);
        for (int i = 0; i < tex.Levels.Count; i++)
        {
            var l = tex.Levels[i];
            var want = TextureDecoder.DecodeFloat(66, tex.ReadLevel(l), l.Width, l.Height);
            Assert.Equal(want.Rgba, TextureDecoder.DecodeFloat(46, levels[i], l.Width, l.Height).Rgba);
        }
    }

    [Fact]
    public void Bc6hTextureBuildsFromItsFloatDds()
    {
        using var tmp = new TempDir();
        var tex = Bc6Texture(tmp, "sky_hqrfl.dds");
        var project = ModProject.Create(tmp.File("p"), "p", "dltb");
        AddHdr(project, tex);

        var result = ProjectBuilder.BuildTextures(project, null, NoPng, TestContext.Current.CancellationToken);
        Assert.True(result.Verified, result.Verdict);
        Assert.Single(result.Built);
        Assert.Empty(result.Skipped);
        using var pack = RpackFile.Open(result.PackPath);
        var built = TextureResource.Open(pack, 0);
        Assert.Equal("sky_hqrfl.dds", built.Name);
        Assert.Equal(Parts(tex).Header, Parts(built).Header);
        Assert.Equal(7, built.Levels.Count);
        for (int i = 0; i < built.Levels.Count; i++)
        {
            var l = built.Levels[i];
            var (rms, max) = Compare(TextureDecoder.DecodeFloat(66, tex.ReadLevel(tex.Levels[i]), l.Width, l.Height),
                                     TextureDecoder.DecodeFloat(66, built.ReadLevel(l), l.Width, l.Height));
            Assert.True(rms < 0.0005 && max < 0.01, $"mip {i}: log2 error rms {rms:F5} max {max:F4}");
        }
    }

    [Fact]
    public void Bc6hDdsBuildsVerbatim()
    {
        using var tmp = new TempDir();
        var tex = Bc6Texture(tmp, "sky_hqrfl.dds");
        var project = ModProject.Create(tmp.File("p"), "p", "dltb");
        var asset = AddHdr(project, tex);
        var source = Path.Combine(project.Folder, TextureAsset.Folder, "sky_hqrfl.dds");
        File.WriteAllBytes(source, DdsWriter.ToDds(tex));                  // the byte-identical export
        var enc = TextureSource.Encode(source, asset, NoPng, w => Assert.Fail(w));
        Assert.Equal(Parts(tex).Bitmap, enc.Bitmap);
        Assert.Equal(Parts(tex).Header, enc.Header);

        // SF16 target from the UF16 blocks: decoded and re-encoded, not copied
        asset.BuildFormat = 67;
        Assert.Null(TextureSource.Refusal(source, asset));
        var sf = TextureSource.Encode(source, asset, NoPng, _ => { });
        Assert.Equal(67, ImgcHeader.Parse(sf.Header).Format);
    }

    [Fact]
    public void RadianceSourceRegeneratesMipsAndWarns()
    {
        using var tmp = new TempDir();
        var tex = Bc6Texture(tmp, "menu_sky.hdr");
        var project = ModProject.Create(tmp.File("p"), "p", "dltb");
        var asset = AddHdr(project, tex);
        var dir = Path.Combine(project.Folder, TextureAsset.Folder);
        File.Delete(Path.Combine(dir, "menu_sky.hdr.dds"));
        File.Delete(Path.Combine(dir, "menu_sky.hdr.dds" + TextureAsset.Extension));
        var hdr = Path.Combine(dir, "menu_sky.hdr");
        File.WriteAllBytes(hdr, RadianceHdr.Write(Hdr(64, 32)));
        asset.Save(hdr + TextureAsset.Extension);

        var warnings = new List<string>();
        var enc = TextureSource.Encode(hdr, asset, NoPng, warnings.Add);
        Assert.Equal(7, ImgcHeader.Parse(enc.Header).MipCount);
        Assert.Contains(warnings, w => w.Contains("box filter"));

        var result = ProjectBuilder.BuildTextures(project, null, NoPng, TestContext.Current.CancellationToken);
        Assert.True(result.Verified, result.Verdict);
        Assert.Single(result.Built);
    }

    [Fact]
    public void WrongSourcesAreRefusedByName()
    {
        using var tmp = new TempDir();
        var tex = Bc6Texture(tmp, "sky_hqrfl.dds");
        var project = ModProject.Create(tmp.File("p"), "p", "dltb");
        var asset = AddHdr(project, tex);
        var dir = Path.Combine(project.Folder, TextureAsset.Folder);

        // a PNG (what an older Add to project wrote: a tone-mapped preview) for a BC6H texture
        var png = Path.Combine(dir, "sky_hqrfl.dds.png");
        File.WriteAllBytes(png, [0]);
        Assert.Contains("PNG cannot carry", TextureSource.Refusal(png, asset));

        // both sources describe the same resource: neither is built
        asset.Save(png + TextureAsset.Extension);
        var (specs, built, skipped) = ProjectBuilder.TextureSpecs(project, NoPng, TestContext.Current.CancellationToken);
        Assert.Empty(specs);
        Assert.Equal(2, skipped.Count);
        Assert.All(skipped, s => Assert.Contains("2 source files", s));
        File.Delete(png);
        File.Delete(png + TextureAsset.Extension);

        // a DDS in an unrelated format
        var bc1 = ImgcHeader.Parse(Synth.ImgcHeaderBytes(64, 32, 1, Synth.Tex2D, 1, 59));
        var dds = Path.Combine(dir, "sky_hqrfl.dds");
        File.WriteAllBytes(dds, DdsTests.Dds(bc1, Synth.RandomLevels(bc1)));
        Assert.Contains("DDS is BC1", TextureSource.Refusal(dds, asset));

        // a cube DDS for a 2D texture
        var cube = ImgcHeader.Parse(Synth.ImgcHeaderBytes(8, 8, 1, Synth.TexCube, 1, 46));
        File.WriteAllBytes(dds, DdsTests.Dds(cube, Synth.RandomLevels(cube)));
        Assert.Contains("Cube", TextureSource.Refusal(dds, asset));

        // .hdr for an 8-bit target
        asset.BuildFormat = 59;
        var hdr = Path.Combine(dir, "x.hdr");
        File.WriteAllBytes(hdr, RadianceHdr.Write(Hdr(4, 4)));
        Assert.Contains("not BC1", TextureSource.Refusal(hdr, asset));
        Assert.DoesNotContain((byte)66, TextureSource.BuildFormats(png));
        Assert.Contains((byte)66, TextureSource.BuildFormats(hdr));
    }

    [Fact]
    public void PreviewOfHdrSources()
    {
        using var tmp = new TempDir();
        var tex = Bc6Texture(tmp, "sky_hqrfl.dds");
        var dds = tmp.Write("sky.dds", DdsWriter.ToFloatDds(tex));
        var img = TextureSource.Preview(dds, minWidth: 16);
        Assert.NotNull(img);
        Assert.Equal((16, 8), (img.Width, img.Height));        // the smallest mip at least 16 wide
        Assert.Null(TextureSource.Preview(tmp.Write("bad.dds", [1, 2, 3])));
    }

    // ---- shipped textures (install-backed) -----------------------------------------------------------------

    /// <summary>
    /// A few shipped BC6H textures per game through the whole HDR route: float DDS → build → decode. The header
    /// comes back byte-identical (same geometry; statistics carried), the texels within the encoder's error, and
    /// the BC6H DDS export builds back bit for bit.
    /// </summary>
    [Theory]
    [InlineData("dltb", "dlc_frontier_envprobes_pc.rpack")]
    [InlineData("dltb", "engine_pc.rpack")]
    [InlineData("dl2", "city_envprobes_pc.rpack")]
    [InlineData("dl2", "engine_pc.rpack")]
    public void ShippedBc6hRoundTrip(string game, string packName)
    {
        var install = Installs.Require(game);
        var path = install.Rpacks().FirstOrDefault(p => Path.GetFileName(p) == packName);
        if (path is null) Assert.Skip($"{packName} missing");
        using var pack = RpackFile.Open(path);
        using var tmp = new TempDir();
        int done = 0;
        for (int i = 0; i < pack.Count && done < 3; i++)
        {
            if (pack.Logicals[i].Type != 0x20) continue;
            var tex = TextureResource.Open(pack, i);
            if (!tex.Header.FormatName.StartsWith("BC6H") || !tex.HasBitmap || tex.Header.Width > 1024) continue;
            done++;
            var asset = TextureAsset.From(pack, i, tex, packName, game);
            var (shippedHeader, shippedBitmap) = Parts(tex);

            var floatDds = tmp.Write($"{done}_{game}.dds", DdsWriter.ToFloatDds(tex));
            var enc = TextureSource.Encode(floatDds, asset, NoPng, w => Assert.Fail($"{tex.Name}: {w}"));
            Assert.Equal(shippedHeader, enc.Header);
            var built = ImgcHeader.Parse(enc.Header).LevelLayout(tex.LevelPadding ?? 16);
            for (int l = 0; l < built.Count; l++)
            {
                var lv = built[l];
                var shipped = TextureDecoder.DecodeFloat(66, tex.ReadLevel(tex.Levels[l]), lv.Width, lv.Height);
                var mine = TextureDecoder.DecodeFloat(66, enc.Bitmap.AsSpan((int)lv.Offset, (int)lv.Size), lv.Width, lv.Height);
                var (rms, max) = Compare(shipped, mine);
                Assert.True(rms < 0.001 && max < 0.02, $"{tex.Name} mip {l}: log2 error rms {rms:F5} max {max:F4}");
            }

            var blockDds = tmp.Write($"{done}_{game}_bc6h.dds", DdsWriter.ToDds(tex));
            var verbatim = TextureSource.Encode(blockDds, asset, NoPng, w => Assert.Fail($"{tex.Name}: {w}"));
            Assert.Equal(shippedBitmap, verbatim.Bitmap);
            Assert.Equal(shippedHeader, verbatim.Header);
        }
        if (done == 0) Assert.Skip($"no BC6H texture up to 1024 wide in {packName}");
    }
}
