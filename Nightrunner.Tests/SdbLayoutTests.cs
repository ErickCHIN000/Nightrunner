// Port of nightrunner-main/tests/test_sdb_layout.py: the SDB table layout is selected by the inner MDB version word,
// DLTB (0x24012001) or DL2 (0x23062801). Synthetic databases are built in either layout by Synth.Sdb; the real-file
// checks read the installed runtime_dx11/dx12.sdb.
//
// Not ported:
//   LayoutTests.check_resolves: validate_all() totals and stats() — SdbFile has no whole-database validation or
//     stats call (tools/SdbCheck does that outside Core); the table order the stats listed is checked on Tables.
using Nightrunner.Core.Sdb;

namespace Nightrunner.Tests;

public class SdbLayoutTests
{
    private static SdbFile CheckResolves(TempDir tmp, SdbLayout layout)
    {
        var s = tmp.OpenSdb(Synth.Sdb(layout));
        Assert.Same(layout, s.Layout);
        Assert.Equal(layout.InnerVersion, s.InnerVersion);
        Assert.Equal(1, s.MaterialCount);
        Assert.Equal("test_mat.mat", s.MaterialName(0));
        Assert.Equal(3, s.Tables[0x62].Count);
        Assert.Equal(2, s.Tables[0x28].Count);
        Assert.Equal(Synth.SdbProgramBlob, s.Program(2, 0));
        Assert.Empty(s.Program(0, 0));

        var m = s.Material("TEST_MAT");
        var r = Assert.Single(m.Routes);
        Assert.Equal("opaque", r.Preset);
        Assert.Equal([("dif_0_tex", "string", "override_dif.png"), ("roughness", "float", "0.25")],
                     r.Parameters.Select(p => (p.Name, p.TypeName, p.ValueText)));
        Assert.All(r.Parameters, p => Assert.True(p.Declared));
        var v = Assert.Single(r.Variants);
        Assert.Equal((1, 1, 1), (v.Shader, v.RenderPassCount, v.TextureArray));
        Assert.Equal([("override_dif.png", true), ("noise.dds", false)], v.Bindings.Select(b => (b.Texture, b.Overridden)));
        Assert.Equal(["noise.dds", "override_dif.png"], m.Textures);
        Assert.False(m.NonRendering);

        var p = s.Preset(0);
        Assert.True(p.Complete);
        Assert.Equal("opaque", p.Name);
        Assert.Equal(0x1122334455667788ul, p.Key);
        Assert.Equal([("dif_0_tex", "string"), ("roughness", "float")], p.Parameters.Select(x => (x.Name, x.TypeName)));
        Assert.Equal("0.5", p.Parameters[1].DefaultText);
        Assert.Equal(7u, Assert.Single(p.Groups).Key);

        Assert.Equal(layout.Order.Select(t => t.Key), s.Tables.Keys);
        return s;
    }

    [Fact]
    public void DltbLayout()
    {
        using var tmp = new TempDir();
        var s = CheckResolves(tmp, SdbFormat.Dltb);
        Assert.Equal(39, s.Tables.Count);
        Assert.Equal(44, s.Tables[0x62].Width);
    }

    [Fact]
    public void Dl2Layout()
    {
        using var tmp = new TempDir();
        var s = CheckResolves(tmp, SdbFormat.Dl2);
        Assert.Equal(38, s.Tables.Count);
        Assert.False(s.Tables.ContainsKey(0x48));
        Assert.Equal((43, 25), (s.Tables[0x62].Width, s.Tables[0x28].Width));
        Assert.Throws<SdbFormatException>(() => s.Table(0x48));
    }

    [Fact]
    public void LayoutConstants()
    {
        Assert.Equal(SdbFormat.InnerVersionDltb, SdbFormat.Dltb.InnerVersion);
        Assert.Equal(SdbFormat.InnerVersionDl2, SdbFormat.Dl2.InnerVersion);
        Assert.Same(SdbFormat.Dltb, SdbFormat.LayoutFor(0x24012001));
        Assert.Same(SdbFormat.Dl2, SdbFormat.LayoutFor(0x23062801));
        Assert.Null(SdbFormat.LayoutFor(0x12345678));
        var dltb = SdbFormat.Dltb.Order.ToArray();
        var dl2 = SdbFormat.Dl2.Order.ToArray();
        Assert.Equal(dltb.Length - 1, dl2.Length);
        var diff = dltb.Where(t => t.Key != 0x48).Zip(dl2)
                       .Where(p => p.First.Width != p.Second.Width)
                       .ToDictionary(p => p.First.Key, p => (p.First.Width, p.Second.Width));
        Assert.Equal(new Dictionary<int, (int, int)> { [0x62] = (44, 43), [0x28] = (27, 25) }, diff);
        Assert.Equal(dltb.Where(t => t.Key != 0x48).Select(t => (t.Key, t.Shape)), dl2.Select(t => (t.Key, t.Shape)));
    }

    /// <summary>A DL2 stream labelled with the DLTB version word throws, and vice versa: never mis-resolved.</summary>
    [Fact]
    public void LayoutFollowsVersionWordNotBytes()
    {
        using var tmp = new TempDir();
        Assert.Throws<SdbFormatException>(() => tmp.OpenSdb(Synth.Sdb(SdbFormat.Dl2, SdbFormat.InnerVersionDltb), "dl2_as_dltb.sdb"));
        Assert.Throws<SdbFormatException>(() => tmp.OpenSdb(Synth.Sdb(SdbFormat.Dltb, SdbFormat.InnerVersionDl2), "dltb_as_dl2.sdb"));
    }

    [Fact]
    public void UnknownVersion()
    {
        using var tmp = new TempDir();
        var ex = Assert.Throws<SdbFormatException>(() => tmp.OpenSdb(Synth.Sdb(SdbFormat.Dl2, 0x12345678)));
        Assert.Matches("0x24012001.*0x23062801.*0x12345678", ex.Message);
    }

    [Fact]
    public void BadOuterFraming()
    {
        using var tmp = new TempDir();
        var good = Synth.Sdb(SdbFormat.Dltb);
        Assert.Throws<SdbFormatException>(() => tmp.OpenSdb(Synth.Patch(good, 0, "MDBX"u8), "magic.sdb"));
        Assert.Throws<SdbFormatException>(() => tmp.OpenSdb([.. good, 0], "long.sdb"));           // 16 + A + B != size
        Assert.Throws<SdbFormatException>(() => tmp.OpenSdb(good[..40], "short.sdb"));
    }

    // ---- real databases (install-backed) ------------------------------------------------------------------

    private static List<string> InstalledSdbs(string gameId)
    {
        var gi = Installs.Require(gameId);
        var found = new[] { "dx11", "dx12" }.Select(gi.Sdb).Where(File.Exists).ToList();
        if (found.Count == 0) Assert.Skip($"no runtime_dx11/dx12.sdb in the {gameId} install");
        return found;
    }

    [Fact]
    public void RealDl2OpenAndResolveSample()
    {
        foreach (var path in InstalledSdbs("dl2"))
        {
            using var s = SdbFile.Open(path);
            Assert.Same(SdbFormat.Dl2, s.Layout);
            Assert.True(s.Tables[0xB2].Count > 20000, path);             // 25,760 dx11 / 25,741 dx12 (2026-09-16)
            Assert.Equal(523, s.Tables[0xAA].Count);
            int n = s.Tables[0xB2].Count;
            var texs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < n; i += Math.Max(1, n / 300))
            {
                var m = s.Material(i);
                var r = Assert.Single(m.Routes);
                Assert.NotEmpty(r.PresetIndices);
                texs.UnionWith(m.Textures);
            }
            Assert.Contains(texs, t => t.EndsWith(".dds") || t.EndsWith(".png"));
        }
    }

    [Fact]
    public void RealDltbOpens()
    {
        foreach (var path in InstalledSdbs("dltb"))
        {
            using var s = SdbFile.Open(path);
            Assert.Same(SdbFormat.Dltb, s.Layout);
            Assert.Equal(39, s.Tables.Count);
            Assert.True(s.MaterialCount > 20000, path);                 // 26,670 dx11 / 26,665 dx12 (2026-09-22)
            Assert.Equal(791, s.PresetCount);
            int n = s.MaterialCount;
            for (int i = 0; i < n; i += Math.Max(1, n / 100))
                Assert.Single(s.Material(i).Routes);
        }
    }
}
