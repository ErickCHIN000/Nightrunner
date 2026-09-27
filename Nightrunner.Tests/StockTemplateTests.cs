using System.IO.Compression;
using Nightrunner.Core.Games;
using Nightrunner.Core.Model;
using Nightrunner.Core.Project;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Tests;

/// <summary>Stock vs modded packs, and "Add to project" taking its template from the stock copy.</summary>
public class StockTemplateTests
{
    [Fact]
    public void OriginsTellStockFromMods()
    {
        using var tmp = new TempDir();
        var g = new FakeInstall(tmp);
        var project = ModProject.Create(tmp.File("proj"), "proj", "dltb");
        project.SetOutputNames("mymod_pc.rpack", "data5.pak");
        FakeInstall.Text(Path.Combine(project.BuildFolder, "proj.build.json"), """{ "outputs": { "rpack": { "path": "older_pc.rpack" } } }""");
        var built = PackOrigins.BuiltNames([project.Folder, tmp.File("not-a-project")]);
        Assert.Equal(["data5.pak", "mymod_pc.rpack", "older_pc.rpack"], built.Order(StringComparer.OrdinalIgnoreCase));

        var o = new PackOrigins(g.Install, built);
        Assert.Equal(PackOrigin.Stock, o.Of(Path.Combine(g.Assets, "common_meshes_pc.rpack")));
        Assert.Equal(PackOrigin.Stock, o.Of(Path.Combine(g.Source, "data1.pak")));
        Assert.Equal(PackOrigin.Runtime, o.Of(Path.Combine(g.Mods, "m", "packs", "x_pc.rpack")));
        Assert.Equal(PackOrigin.Runtime, o.Of(Path.Combine(g.Mods, "m", "paks", "x.pak")));
        Assert.Equal(PackOrigin.Build, o.Of(Path.Combine(g.Assets, "mymod_pc.rpack")));
        Assert.Equal(PackOrigin.Build, o.Of(Path.Combine(g.Source, "data5.pak")));
        Assert.Equal(PackOrigin.Slot, o.Of(Path.Combine(g.Assets, "assets_2_pc.rpack")));
        Assert.Equal(PackOrigin.Slot, o.Of(Path.Combine(g.Source, "data2.pak")));
    }

    [Fact]
    public async Task AddToProjectTemplatesComeFromStock()
    {
        using var tmp = new TempDir();
        var g = new FakeInstall(tmp, "engine_pc.rpack");
        RpackWriter.Write(FakeInstall.Put(Path.Combine(g.Assets, "common_meshes_pc.rpack")),
                          [Synth.Mesh("shared", seed: 1), Synth.Mesh("only_stock", seed: 2), Synth.Texture("tex_a", seed: 3)]);
        RpackWriter.Write(FakeInstall.Put(Path.Combine(g.Assets, "assets_2_pc.rpack")),        // an installed build, sorts first
                          [Synth.Mesh("shared", seed: 4), Synth.Mesh("only_mod", seed: 5)]);
        RpackWriter.Write(FakeInstall.Put(Path.Combine(g.Mods, "m", "packs", "m_pc.rpack")), [Synth.Texture("tex_a", seed: 6)]);
        g.Mod("m", """{ "id": "m", "items": [ { "kind": "rpack", "file": "packs/m_pc.rpack" } ] }""");

        var rc = RuntimeContent.Read(g.Install);
        var origins = new PackOrigins(g.Install);
        using var catalog = new RpackCatalog();
        await catalog.LoadAsync(g.Install.Rpacks(rc), g.Install.Assets, TestContext.Current.CancellationToken);
        string PackOf(int gid) => Path.GetFileName(catalog.Split(gid).Entry.Path);

        int shared = catalog.Lookup("shared", 0x10)[0];
        Assert.Equal("assets_2_pc.rpack", PackOf(shared));                         // what a plain lookup takes
        Assert.Equal("common_meshes_pc.rpack", PackOf(StockCopy.Resource(catalog, shared, origins)));
        int tex = catalog.Lookup("tex_a", 0x20)[0];
        Assert.Equal("m_pc.rpack", PackOf(tex));                                   // the runtime's copy wins in game...
        Assert.Equal("common_meshes_pc.rpack", PackOf(StockCopy.Resource(catalog, tex, origins)));   // ...but is no template
        int stockOnly = catalog.Lookup("only_stock", 0x10)[0];
        Assert.Equal(stockOnly, StockCopy.Resource(catalog, stockOnly, origins));
        var e = Assert.Throws<ProjectException>(() => StockCopy.Resource(catalog, catalog.Lookup("only_mod", 0x10)[0], origins));
        Assert.Equal("only_mod is only in assets_2_pc.rpack (mod slot): no stock copy to start from", e.Message);

        // the stock view: same gids, mods left out, disposing it leaves the catalog's packs open
        var view = StockCopy.Catalog(catalog, origins);
        Assert.Equal([StockCopy.Resource(catalog, shared, origins)], view.Lookup("shared", 0x10));
        Assert.Empty(view.Lookup("only_mod"));
        Assert.Equal("shared", view.Name(view.Lookup("shared")[0]));
        view.Dispose();
        Assert.Equal("shared", catalog.Name(shared));
        await Assert.ThrowsAsync<InvalidOperationException>(() => view.LoadAsync(["x.rpack"], ct: TestContext.Current.CancellationToken));

        // .model: the stock pak's member, never the mod's
        string Pak(string path, params string[] members)
        {
            FakeInstall.Put(path);
            File.Delete(path);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var m in members)
                using (var w = new StreamWriter(zip.CreateEntry(m).Open())) w.Write("{}");
            return path;
        }
        Pak(Path.Combine(g.Source, "data0.pak"), "models/thing.model");
        string mod = Pak(Path.Combine(g.Mods, "m", "paks", "m.pak"), "thing.model", "modonly.model");
        using var models = new ModelCatalog([Path.Combine(g.Source, "data0.pak"), mod], p => !origins.IsStock(p));
        var winner = models.Find("thing.model")!;
        Assert.True(winner.Custom);
        Assert.Equal("data0.pak", Path.GetFileName(StockCopy.Model(models, winner, origins).Pak));
        var m2 = Assert.Throws<ProjectException>(() => StockCopy.Model(models, models.Find("modonly.model")!, origins));
        Assert.Equal("modonly.model is only in m.pak (runtime mod): no stock copy to start from", m2.Message);
    }
}
