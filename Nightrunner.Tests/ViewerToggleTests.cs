using System.IO.Compression;
using System.Text.Json;
using Nightrunner.Core;
using Nightrunner.Core.Anim;
using Nightrunner.Core.Games;
using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Tests;

/// <summary>
/// The viewport's Mods toggle (stock view: stock copies, mod-only content kept, name-keyed caches scoped by view) and
/// Follow toggle (a kept model's fit, the toggles' defaults).
/// </summary>
public class ViewerToggleTests
{
    [Fact]
    public void ScopedCacheKeysNeverMeetButShareTheBudget()
    {
        var all = new ByteBudgetCache<string, byte[]>(300, b => b.LongLength, StringComparer.OrdinalIgnoreCase);
        var stock = all.Scope(k => "stock|" + k);
        string? asked = null;
        var modTex = all.GetOrAdd("a:tex", new byte[100]);
        var stockTex = stock.GetOrAdd("A:TEX", k => { asked = k; return new byte[100]; });
        Assert.Equal("A:TEX", asked);                              // the factory sees the key as asked
        Assert.NotSame(modTex, stockTex);                          // same name, other view: another entry
        Assert.Same(stockTex, stock.GetOrAdd("a:tex", new byte[1]));
        Assert.Same(modTex, all.GetOrAdd("a:tex", new byte[1]));
        Assert.Equal(200, stock.Bytes);                            // one store: totals are shared
        Assert.Equal(2, all.Count);
        all.GetOrAdd("b", new byte[100]);
        stock.GetOrAdd("c", new byte[100]);                        // over budget: the least recently used goes, whichever scope
        Assert.Equal(300, all.Bytes);
        Assert.Equal(1, stock.Evictions);
        Assert.False(stock.TryGetValue("b", out _));
        Assert.True(all.TryGetValue("b", out _));
    }

    [Fact]
    public async Task StockViewTakesStockCopiesAndKeepsModOnlyContent()
    {
        using var tmp = new TempDir();
        var g = new FakeInstall(tmp, "engine_pc.rpack");
        RpackWriter.Write(FakeInstall.Put(Path.Combine(g.Assets, "common_meshes_pc.rpack")),
                          [Synth.Mesh("player_head", seed: 1), Synth.Texture("player_head_dif", seed: 2)]);
        RpackWriter.Write(FakeInstall.Put(Path.Combine(g.Mods, "m", "packs", "m_pc.rpack")),
                          [Synth.Mesh("player_head", seed: 3), Synth.Texture("player_head_dif", seed: 4), Synth.Mesh("leon_hair", seed: 5)]);
        g.Mod("m", """{ "id": "m", "items": [ { "kind": "rpack", "file": "packs/m_pc.rpack" } ] }""");
        var rc = RuntimeContent.Read(g.Install);
        var origins = new PackOrigins(g.Install);
        using var catalog = new RpackCatalog();
        await catalog.LoadAsync(g.Install.Rpacks(rc), g.Install.Assets, TestContext.Current.CancellationToken);
        string PackOf(int gid) => Path.GetFileName(catalog.Split(gid).Entry.Path);

        int head = catalog.Lookup("player_head", 0x10)[0];
        Assert.Equal("m_pc.rpack", PackOf(head));                                       // Mods on: what the game loads
        Assert.Equal("common_meshes_pc.rpack", PackOf(StockCopy.TryResource(catalog, head, origins)!.Value));
        int stockHead = StockCopy.TryResource(catalog, head, origins)!.Value;
        Assert.Equal(stockHead, StockCopy.TryResource(catalog, stockHead, origins));
        Assert.Null(StockCopy.TryResource(catalog, catalog.Lookup("leon_hair", 0x10)[0], origins));   // only a mod has it
        var view = StockCopy.Catalog(catalog, origins);
        Assert.Equal([stockHead], view.Lookup("player_head", 0x10));
        Assert.Equal("common_meshes_pc.rpack", PackOf(view.Lookup("player_head_dif", 0x20)[0]));

        // .model: the stock subset shows the stock member winning again, and leaves the mod-only one out
        string Pak(string path, params string[] members)
        {
            FakeInstall.Put(path);
            File.Delete(path);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var m in members)
                using (var w = new StreamWriter(zip.CreateEntry(m).Open())) w.Write("{}");
            return path;
        }
        string data0 = Pak(Path.Combine(g.Source, "data0.pak"), "models/player.model");
        string mod = Pak(Path.Combine(g.Mods, "m", "paks", "m.pak"), "player.model", "leon.model");
        using var models = new ModelCatalog([data0, mod], p => !origins.IsStock(p));
        Assert.False(models.Models.Single(m => m.Pak == data0).Wins);
        var stock = models.Subset(origins.IsStock);
        Assert.Equal([data0], stock.PakPaths);
        var player = Assert.Single(stock.Models);
        Assert.True(player.Wins);
        Assert.Equal("player.model", stock.Find("player.model")!.Basename);
        Assert.Null(stock.Find("leon.model"));
        stock.Dispose();                                                                // shares the paks: they stay open
        Assert.Equal(2, models.Pak(mod).Models().Count());
        Assert.NotNull(models.Find("leon.model"));
    }

    [Fact]
    public void KeptModelFitIsReported()
    {
        static uint[] H(IEnumerable<string> names) => names.Select(Anm2Hash.H41).ToArray();
        static IEnumerable<string> Bones(string prefix, int n) => Enumerable.Range(0, n).Select(i => $"{prefix}{i}");
        var fpp = new AnimRig("player_fpp_skeleton.msh", H(Bones("b", 92)).ToHashSet(), ["player_fpp.model"]);
        var pick = AnimRigs.Of(fpp).Pick(H(Bones("b", 115)), "m_fpp_stick_idle_relaxed", "anims_player", null);
        Assert.Equal(92, pick.Bound);
        Assert.True(AnimRigs.PoorFit(74, pick));       // DL2: the worker binds 74 of 115, the FPP rig 92
        Assert.False(AnimRigs.PoorFit(90, pick));      // within the rig pick's tie range
        Assert.False(AnimRigs.PoorFit(92, pick));
        var gate = new RigPick(null, 0, 5, 5);         // an object's clip with no rig: under half its tracks is poor
        Assert.True(AnimRigs.PoorFit(2, gate));
        Assert.False(AnimRigs.PoorFit(3, gate));
    }

    [Fact]
    public void TogglesDefaultToStockAndFollowAndRoundTrip()
    {
        var s = new GameSettings();
        Assert.False(s.ViewportMods);
        Assert.True(s.FollowRig);
        var old = JsonSerializer.Deserialize<GameSettings>("""{ "roots": {} }""")!;     // a settings file from before the toggles
        Assert.False(old.ViewportMods);
        Assert.True(old.FollowRig);
        s.ViewportMods = true;
        s.FollowRig = false;
        var back = JsonSerializer.Deserialize<GameSettings>(JsonSerializer.Serialize(s))!;
        Assert.True(back.ViewportMods);
        Assert.False(back.FollowRig);
    }
}
