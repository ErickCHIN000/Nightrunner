using Nightrunner.Core.Rpack;
using Nightrunner.Core.Sdb;
using Nightrunner.Core.Texture;

namespace Nightrunner.Tests;

/// <summary>The material preview recipes (port of <c>gui/matpreview.py::compose</c>) on synthetic materials.</summary>
public class MaterialPreviewTests
{
    private static SdbMaterial Material(string tokens, (string Param, string Texture)[] bindings, SdbParameter[]? parameters = null, bool nonRendering = false)
    {
        var variant = new SdbVariant(0, 0, 0, 1, bindings.Select((b, i) => new SdbBinding(i, i, b.Param, b.Texture, 0, true, null)).ToList());
        var route = new SdbRoute(0, 0, 0, 0, 0, 0, tokens, "opaque", [], parameters ?? [], [variant]);
        return new SdbMaterial(0, "m.mat", "t", [route], nonRendering);
    }

    private static SdbParameter Vec(string name, params float[] v) =>
        new(0, name, 0, false, 0, true, v.Length switch { 1 => 2, 2 => 3, _ => 4 }, null,
            Convert.ToHexString(v.SelectMany(BitConverter.GetBytes).ToArray()).ToLowerInvariant(), null, null);

    /// <summary>A w×h BGRA image from a per-pixel colour.</summary>
    private static DecodedImage Image(int w, int h, Func<int, int, (byte B, byte G, byte R, byte A)> px) =>
        new(w, h, Enumerable.Range(0, w * h).SelectMany(i => { var c = px(i % w, i / w); return new[] { c.B, c.G, c.R, c.A }; }).ToArray());

    /// <summary>A 2×2 BGRA image of one colour.</summary>
    private static DecodedImage Solid(byte b, byte g, byte r, byte a) =>
        new(2, 2, Enumerable.Repeat(new[] { b, g, r, a }, 4).SelectMany(x => x).ToArray());

    [Fact]
    public void PlainAndHidden()
    {
        var plain = MaterialPreview.Compose(Material("opaque;", [("dif_0_tex", "d.png"), ("nrm_0_tex", "n.png")]), null, _ => throw new InvalidOperationException());
        Assert.Equal(("plain", AlphaMode.Opaque, "d.png", "n.png"), (plain.Recipe, plain.Alpha, plain.PlainTexture, plain.NormalTexture));
        Assert.True(MaterialPreview.Compose(Material("", [], nonRendering: true), null, _ => null).Hidden);
    }

    [Fact]
    public void HairIsAlphaTestedCoverage()
    {
        var m = Material("opaque;dit_0_tex;", [("dif_0_tex", "d.png"), ("dit_0_tex", "c.png")], [Vec("dif_0_val", 0.5f, 1, 1)]);
        var s = MaterialPreview.Compose(m, null, n => n == "d.png" ? Solid(200, 100, 200, 255) : Solid(0, 0, 128, 255));
        Assert.Equal(("dither_cutout", AlphaMode.Mask, 0.25f), (s.Recipe, s.Alpha, s.Cutoff));
        Assert.Equal(new byte[] { 200, 100, 100, 128 }, s.Image!.Bgra[..4]);     // R × 0.5 tint, alpha = red of the dither map
    }

    [Fact]
    public void OpacityUsesRanges()
    {
        var m = Material("opaque;opc_0_tex;", [("dif_0_tex", "d.png"), ("opc_0_tex", "o.png")], [Vec("opc_3_ranges", 100, 200)]);
        var s = MaterialPreview.Compose(m, null, n => n == "d.png" ? Solid(10, 20, 30, 255) : Solid(0, 0, 150, 255));
        Assert.Equal((AlphaMode.Blend, (byte)128), (s.Alpha, s.Image!.Bgra[3]));   // (150 − 100) / (200 − 100) = 0.5
        var unsupported = MaterialPreview.Compose(Material("opc_uv_1_on;", [("dif_0_tex", "d.png"), ("opc_0_tex", "o.png")]), null, _ => Solid(0, 0, 0, 0));
        Assert.Equal("plain", unsupported.Recipe);
    }

    [Fact]
    public void EyeBlendsIrisScleraVeins()
    {
        var m = Material("eyes_blicks_on;od1_tex;od2_tex;off_tex;",
            [("dif_0_tex", "veins.png"), ("od1_tex", "sclera.png"), ("od2_tex", "iris.png"), ("off_tex", "mask.png")]);
        var s = MaterialPreview.Compose(m, null, n => n switch
        {
            "veins.png" => Solid(0, 0, 255, 255),
            "sclera.png" => Solid(255, 255, 255, 0),       // sclera alpha 0: the iris shows
            "iris.png" => Solid(0, 255, 0, 255),
            _ => Solid(0, 0, 0, 255),                       // no veins
        });
        Assert.Equal("eye_layers", s.Recipe);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, s.Image!.Bgra[..4]);
    }

    [Fact]
    public void ModelOverrideReplacesBinding()
    {
        var s = MaterialPreview.Compose(Material("", [("dif_0_tex", "d.png")]), new Dictionary<string, string> { ["dif_0_tex"] = "x.png" }, _ => null);
        Assert.Equal("x.png", s.PlainTexture);
    }

    [Fact]
    public void PlainCarriesDiffuseScale()
    {
        var tinted = MaterialPreview.Compose(Material("opaque;", [("dif_0_tex", "d.png")], [Vec("dif_0_val", 0.5f, 0.25f, 1)]), null, _ => null);
        Assert.Equal(new[] { 0.5f, 0.25f, 1f }, tinted.Tint);
        var white = MaterialPreview.Compose(Material("opaque;", [("dif_0_tex", "d.png")], [Vec("dif_0_val", 1, 1, 1)]), null, _ => null);
        Assert.Null(white.Tint);
    }

    /// <summary>A 3-column, 2-row gradient: row 0 black → red → white, row 1 blue everywhere.</summary>
    private static DecodedImage Gradient() => Image(3, 2, (x, y) => y == 1 ? ((byte)255, (byte)0, (byte)0, (byte)255)
        : x switch { 0 => ((byte)0, (byte)0, (byte)0, (byte)255), 1 => ((byte)0, (byte)0, (byte)255, (byte)255), _ => ((byte)255, (byte)255, (byte)255, (byte)255) });

    [Fact]
    public void GradientMapLooksUpTheIndexInTheBaseRow()
    {
        // index 0.25 → column 0.5 (between black and red); index 0.75 → column 1.5 (between red and white)
        var index = Image(2, 1, (x, _) => x == 0 ? ((byte)64, (byte)64, (byte)64, (byte)255) : ((byte)191, (byte)191, (byte)191, (byte)255));
        var m = Material("opaque;grd_0_tex;idx_0_tex;", [("grd_0_tex", "g.png"), ("idx_0_tex", "i.png"), ("nrm_0_tex", "n.png")]);
        var s = MaterialPreview.Compose(m, null, n => n == "g.png" ? Gradient() : index);
        Assert.Equal(("gradient_map", AlphaMode.Opaque, "n.png"), (s.Recipe, s.Alpha, s.NormalTexture));
        Assert.Equal((2, 1), (s.Image!.Width, s.Image.Height));
        Assert.Equal(new byte[] { 0, 0, 128, 255 }, s.Image.Bgra[..4]);             // half-way black → red
        Assert.Equal(new byte[] { 127, 127, 255, 255 }, s.Image.Bgra[4..]);        // half-way red → white
        Assert.Contains(s.Warnings, w => w.Contains("base row"));

        var row1 = MaterialPreview.Compose(Material("opaque;grd_0_tex;idx_0_tex;", [("grd_0_tex", "g.png"), ("idx_0_tex", "i.png")], [Vec("grd_0_base", 1)]),
                                           null, n => n == "g.png" ? Gradient() : index);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, row1.Image!.Bgra[..4]);
    }

    [Fact]
    public void GradientMapIndexModifiersAndConstant()
    {
        var index = Image(1, 1, (_, _) => ((byte)0, (byte)0, (byte)0, (byte)255));
        var remapped = MaterialPreview.Compose(Material("grd_0_tex;idx_0_tex;", [("grd_0_tex", "g.png"), ("idx_0_tex", "i.png")],
                                                        [Vec("idx_0_range_out_min", 1)]), null, n => n == "g.png" ? Gradient() : index);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, remapped.Image!.Bgra);      // out = 1 whatever the index: the white end
        var constant = MaterialPreview.Compose(Material("grd_0_tex;idx_0_src_const_on;", [("grd_0_tex", "g.png")], [Vec("idx_0_const", 0.5f)]),
                                               null, n => n == "g.png" ? Gradient() : null);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, constant.Image!.Bgra);          // column 1: red
    }

    [Fact]
    public void GradientMappedHairIsAlphaTested()
    {
        var index = Image(2, 2, (_, _) => ((byte)0, (byte)0, (byte)0, (byte)255));
        var cover = Image(2, 2, (x, _) => ((byte)0, (byte)0, x == 0 ? (byte)200 : (byte)0, (byte)255));
        var m = Material("opaque;dit_0_tex;grd_0_tex;idx_0_tex;", [("grd_0_tex", "g.png"), ("idx_0_tex", "i.png"), ("dit_0_tex", "c.png")]);
        var s = MaterialPreview.Compose(m, null, n => n switch { "g.png" => Gradient(), "i.png" => index, _ => cover });
        Assert.Equal(("gradient_map+dither_cutout", AlphaMode.Mask), (s.Recipe, s.Alpha));
        Assert.Equal((byte)200, s.Image!.Bgra[3]);
        Assert.Equal((byte)0, s.Image.Bgra[7]);
    }

    [Fact]
    public void GradientMapRefusesRuntimeIndexSources()
    {
        var m = Material("grd_0_tex;idx_0_tex;idx_0_src_vtx_on;", [("grd_0_tex", "g.png"), ("idx_0_tex", "i.png")]);
        var s = MaterialPreview.Compose(m, null, _ => Gradient());
        Assert.Equal(("none", (DecodedImage?)null), (s.Recipe, s.Image));
        Assert.Contains(s.Warnings, w => w.Contains("idx_0_src_vtx_on"));
        Assert.Contains("gradient map not composed", s.Warnings);
    }

    [Fact]
    public void NoColourSaysWhy()
    {
        var glass = MaterialPreview.Compose(Material("glass;", [("nrm_0_tex", "n.png"), ("blood_tex", "b.png")]), null, _ => null);
        Assert.Equal("none", glass.Recipe);
        Assert.Contains(glass.Warnings, w => w.StartsWith("no colour texture") && w.Contains("nrm_0_tex") && !w.Contains("blood"));
        var dyed = MaterialPreview.Compose(Material("opaque;", [("dif_0_tex", "d.png"), ("idx_tex", "i.png"), ("grd_tex", "g.png")]), null, _ => null);
        Assert.Equal("plain", dyed.Recipe);
        Assert.Contains("dye gradient (idx_tex on UV1, grd_tex) not applied", dyed.Warnings);
    }

    /// <summary>
    /// The viewer's surface of stock gradient-mapped NPC outfit and hair materials: composed from the real index and
    /// gradient textures (the census, <c>tools/MeshCheck --materials</c>, counts the rest).
    /// </summary>
    [Theory]
    [InlineData("dltb", "npc_man_torso_b.mat", "gradient_map")]
    [InlineData("dltb", "sh_wmn_hair_system_a_base_a.mat", "gradient_map+dither_cutout")]
    [InlineData("dl2", "wmn_srv_torso_a.mat", "gradient_map")]
    public async Task StockGradientMapsCompose(string game, string material, string recipe)
    {
        var install = Installs.Require(game);
        using var sdb = SdbFile.Open(install.Sdb("dx11"));
        using var catalog = new RpackCatalog();
        await catalog.LoadAsync(install.Rpacks(), install.Assets, TestContext.Current.CancellationToken);
        var (m, problem) = ViewerSurface.Resolve(sdb, material);
        Assert.Null(problem);
        var s = MaterialPreview.Compose(m, null, n => ViewerSurface.Open(catalog, n, true, out var t) is null
            ? t!.Decode(ViewerSurface.PreviewLevel(t) ?? t.TopLevel!.Value, rebuildNormalZ: false) : null);
        Assert.Equal(recipe, s.Recipe);
        var px = s.Image!.Bgra;
        // the index map's regions come out as different gradient colours (DLTB's NPC gradients are grey ramps; their
        // hue is in the UV1 dye layer), where the viewer drew one flat grey before
        var colours = Enumerable.Range(0, px.Length / 4).Select(i => (px[i * 4], px[i * 4 + 1], px[i * 4 + 2])).Distinct().Count();
        Assert.True(colours > 16, $"{colours} colours");
        if (game == "dl2") Assert.Contains(Enumerable.Range(0, px.Length / 4), i => Math.Abs(px[i * 4] - px[i * 4 + 2]) > 8);
    }
}
