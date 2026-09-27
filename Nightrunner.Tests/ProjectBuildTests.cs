using System.Text.Json;
using Nightrunner.Core.Project;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Tests;

/// <summary>
/// The build folder belongs to the project (regression: opening A, then B, then building B wrote into A\build,
/// because the window kept A's output path).
/// </summary>
public class ProjectBuildTests
{
    private static ModProject WithTexture(string folder, string name)
    {
        var p = ModProject.Create(folder, name, "dltb");
        var dir = Path.Combine(p.Folder, TextureAsset.Folder);
        File.WriteAllBytes(Path.Combine(dir, name + "_dif.png"), [0]);   // content comes from the test loader
        new TextureAsset { Resource = name + "_dif.png", Format = 38, FormatName = "RGBA8", Width = 4, Height = 4, Mips = 1 }
            .Save(Path.Combine(dir, name + "_dif.png" + TextureAsset.Extension));
        return p;
    }

    private static (byte[], int, int) Pixels(string _) => (Enumerable.Repeat((byte)0x80, 4 * 4 * 4).ToArray(), 4, 4);

    [Fact]
    public void OpeningAThenBBuildsIntoB()
    {
        using var tmp = new TempDir();
        var a = WithTexture(tmp.File("A"), "a");
        var b = WithTexture(tmp.File("B"), "b");

        ModProject current = a;          // what Workspace.Project holds: A is opened first ...
        current = ModProject.Open(b.Folder);   // ... then B
        var result = ProjectBuilder.BuildTextures(current, null, Pixels, TestContext.Current.CancellationToken);

        Assert.StartsWith(Path.Combine(b.Folder, "build") + Path.DirectorySeparatorChar, result.PackPath);
        Assert.True(File.Exists(result.PackPath));
        Assert.False(Directory.Exists(Path.Combine(a.Folder, "build")));
        Assert.True(result.Verified, result.Verdict);
        using var pack = RpackFile.Open(result.PackPath);
        Assert.Equal(["b_dif.png"], Enumerable.Range(0, pack.Count).Select(pack.Name));
    }

    private static TextureAsset Tex(ModProject p, string name, byte format, string formatName, int w, int h, int mips, string source = "", ulong split = 0)
    {
        var dir = Path.Combine(p.Folder, TextureAsset.Folder);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), [0]);
        var asset = new TextureAsset
        {
            Resource = name + ".png", Format = format, FormatName = formatName, Width = w, Height = h, Mips = mips, SourcePack = source,
            MipSplit = split, StatsBase64 = Convert.ToBase64String(Enumerable.Repeat((byte)0x3F, 48).ToArray()),
        };
        asset.Save(Path.Combine(dir, name + ".png" + TextureAsset.Extension));
        return asset;
    }

    private static Func<string, (byte[], int, int)> Gradient(int w, int h) => _ =>
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { px[i * 4] = 10; px[i * 4 + 1] = (byte)(i % 200); px[i * 4 + 2] = 250; px[i * 4 + 3] = 255; }
        return (px, w, h);
    };

    [Fact]
    public async Task TexturePackField08FollowsTheSourcePacks()
    {
        using var tmp = new TempDir();
        var assets = tmp.File("assets");
        Directory.CreateDirectory(assets);
        var clip = ProjectBuildItemTests.Plain("c", ProjectBuildItemTests.BoneClip());
        RpackWriter.Write(Path.Combine(assets, "zero_pc.rpack"), [clip], 0);
        RpackWriter.Write(Path.Combine(assets, "ondemand_pc.rpack"), [clip], 0x1000);
        var catalog = tmp.Track(new RpackCatalog());
        await catalog.LoadAsync([Path.Combine(assets, "zero_pc.rpack"), Path.Combine(assets, "ondemand_pc.rpack")], assets,
                                TestContext.Current.CancellationToken);
        uint Field08(string name, RpackCatalog? c, params string[] sources)
        {
            var p = ModProject.Create(tmp.File(name), name, "dltb");
            for (int i = 0; i < sources.Length; i++) Tex(p, $"t{i}", 38, "RGBA8", 4, 4, 1, sources[i]);
            var r = ProjectBuilder.BuildTextures(p, null, Pixels, TestContext.Current.CancellationToken, c);
            Assert.True(r.Verified, r.Verdict);
            using var pack = RpackFile.Open(r.PackPath);
            return pack.Header.Field08;
        }
        Assert.Equal(0u, Field08("a", catalog, "zero_pc.rpack", "zero_pc.rpack"));      // the sources agree
        Assert.Equal(0x1000u, Field08("b", catalog, "ondemand_pc.rpack"));
        Assert.Equal(0x1000u, Field08("c", catalog, "zero_pc.rpack", "ondemand_pc.rpack"));   // they disagree
        Assert.Equal(0x1000u, Field08("d", null, "zero_pc.rpack"));                        // unknown source
    }

    [Fact]
    public void ResizedTextureGetsNewStatsAndMipSplit()
    {
        using var tmp = new TempDir();
        var p = ModProject.Create(tmp.File("p"), "p", "dltb");
        // RGBA8 4x4 -> 8x8: statistics by the prototype's PNG import (stored min/max, source mean)
        Tex(p, "rgba", 38, "RGBA8", 4, 4, 1);
        var r = ProjectBuilder.BuildTextures(p, "a_pc.rpack", Gradient(8, 8), TestContext.Current.CancellationToken);
        Assert.True(r.Verified, r.Verdict);
        using (var pack = RpackFile.Open(r.PackPath))
        {
            var (min, max, mean) = Nightrunner.Core.Texture.TextureResource.Open(pack, 0).Header.Stats();
            Assert.Equal([250 / 255f, 0, 10 / 255f, 1], min);
            Assert.Equal([250 / 255f, 63 / 255f, 10 / 255f, 1], max);
            Assert.Equal(31.5 / 255, mean[1], 5);
            Assert.Equal(1, mean[3], 5);
        }

        // BC1 64x32 (7 mips) with the stock split -> 128x64 (8 mips): recomputed by the same rule
        var p2 = ModProject.Create(tmp.File("p2"), "p2", "dltb");
        Assert.Equal(0x10000168_00000400UL, ProjectBuilder.StockMipSplit(59, 64, 32, 1, 7, 1));
        Tex(p2, "bc1", 59, "BC1", 64, 32, 7, split: 0x10000168_00000400UL);
        r = ProjectBuilder.BuildTextures(p2, "b_pc.rpack", Gradient(128, 64), TestContext.Current.CancellationToken);
        Assert.True(r.Verified, r.Verdict);
        using (var pack = RpackFile.Open(r.PackPath))
            Assert.Equal(0x20000168_00001400UL, Nightrunner.Core.Texture.TextureResource.Open(pack, 0).Header.MipSplit);
        Assert.Contains(r.Skipped, s => s.Contains("header statistics kept from the original (BC1 statistics are not derivable)"));

        // a split the rule does not reproduce is refused, not carried stale
        var p3 = ModProject.Create(tmp.File("p3"), "p3", "dltb");
        Tex(p3, "bc1", 59, "BC1", 64, 32, 7, split: 0x10000168_00000401UL);
        Tex(p3, "ok", 38, "RGBA8", 4, 4, 1);
        r = ProjectBuilder.BuildTextures(p3, "c_pc.rpack", s => s.Contains("bc1") ? Gradient(128, 64)(s) : Pixels(s), TestContext.Current.CancellationToken);
        Assert.Contains(r.Skipped, s => s.StartsWith("bc1.png: 64x32 BC1 7 mips -> 128x64 BC1 8 mips: its mip split 0x1000016800000401 does not follow the stock rule", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildFolderIsStoredPerProject()
    {
        using var tmp = new TempDir();
        var a = ModProject.Create(tmp.File("A"), "A", null);
        var b = ModProject.Create(tmp.File("B"), "B", null);
        Assert.Equal(Path.Combine(a.Folder, "build"), a.BuildFolder);

        a.SetBuildFolder(Path.Combine(a.Folder, "out", "packs"));      // inside: stored relative
        var outside = tmp.File("elsewhere");
        b.SetBuildFolder(outside);                                     // outside: stored absolute
        Assert.Equal(Path.Combine("out", "packs"), ModProject.Open(a.Folder).Manifest.BuildFolder);
        Assert.Equal(Path.Combine(a.Folder, "out", "packs"), ModProject.Open(a.Folder).BuildFolder);
        Assert.Equal(Path.GetFullPath(outside), ModProject.Open(b.Folder).BuildFolder);

        a.SetBuildFolder(Path.Combine(a.Folder, "build"));             // back to the default: field dropped
        Assert.DoesNotContain("buildFolder", File.ReadAllText(a.ManifestPath));
    }

    [Fact]
    public void OlderManifestsAndUnknownFieldsSurvive()
    {
        using var tmp = new TempDir();
        var dir = tmp.File("old");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ModProject.ManifestName),
            """{ "schema": "nightrunner/project@1", "name": "old", "game": "dltb", "notes": "", "future": { "x": 1 } }""");
        var p = ModProject.Open(dir);
        Assert.Equal(Path.Combine(p.Folder, "build"), p.BuildFolder);
        p.Save();
        using var doc = JsonDocument.Parse(File.ReadAllText(p.ManifestPath));
        Assert.Equal(1, doc.RootElement.GetProperty("future").GetProperty("x").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("buildFolder", out _));
    }
}
