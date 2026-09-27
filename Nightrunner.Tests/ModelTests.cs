using System.IO.Compression;
using System.Text.Json.Nodes;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;
using Nightrunner.Core.Sdb;

namespace Nightrunner.Tests;

/// <summary><c>.model</c> documents: parse, pak override order, the material join, "used by" and the merged skeleton.</summary>
public class ModelTests
{
    private const string Doc = """
        {"version": 6, "preset": {"skeletonName": "rig.msh"}, "data": {"properties": [{"name": "p", "value": 1}]},
         "futureField": {"kept": true},
         "slots": [
          {"slotUid": 7, "name": "BODY", "meshResources": {"resources": [
            {"name": "body_a.msh", "selected": false,
             "materialsData": [{"number": 0, "name": "skin.mat"}, {"number": 1, "name": "cloth.mat"}],
             "materialsResources": [
               {"number": 0, "resources": [{"name": "skin_alt.mat", "selected": false},
                                            {"name": "skin_red.mat", "selected": true,
                                             "rttiValues": [{"name": "dif_0_tex", "type": 7, "val_str": "red_dif.png"},
                                                            {"name": "gloss", "type": 2, "val_float": 0.5}]}]},
               {"number": 1, "resources": [{"name": "cloth.mat"}]}]},
            {"name": "body_b.msh", "selected": true}]}},
          {"name": "EMPTY", "meshResources": {"resources": []}, "clothResources": [{"name": "c"}]}
         ]}
        """;

    private static string Pak(string folder, string file, params (string Member, string Json)[] members)
    {
        string path = Path.Combine(folder, file);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (m, json) in members)
        {
            using var w = new StreamWriter(zip.CreateEntry(m).Open());
            w.Write(json);
        }
        return path;
    }

    private static ModelDocument Parse(string json = Doc) => ModelDocument.From(JsonNode.Parse(json)!.AsObject(), "t.model");

    [Fact]
    public void ParsesSlotsEntriesAndMaterials()
    {
        var doc = Parse();
        Assert.Equal("rig.msh", doc.Skeleton);
        Assert.Equal(2, doc.Slots.Count);
        var body = doc.Slots[0];
        Assert.Equal("BODY", body.Name);
        Assert.Equal("body_a", body.Drawn!.MeshName);          // the game draws the first entry ...
        Assert.Equal("body_b.msh", body.Chosen!.Name);         // ... not the selected one
        Assert.True(doc.Slots[1].HasCloth);
        Assert.Null(doc.Slots[1].Drawn);
        Assert.NotNull(doc.Json["futureField"]);               // unknown fields stay in the document
        Assert.Throws<ModelFormatException>(() => Parse("""{"version": 5, "slots": []}"""));
        Assert.Throws<ModelFormatException>(() => Parse("""{"version": 6, "slots": {}}"""));
    }

    [Fact]
    public void JoinFollowsNumberThenSelected()
    {
        var mesh = Parse().Slots[0].Meshes[0];
        var (number, choice, alternatives, error) = ModelResolver.Join(mesh, "SKIN.mat");
        Assert.Equal(0, number);
        Assert.Equal("skin_red.mat", choice!.Name);
        Assert.Equal(["skin_alt.mat"], alternatives);
        Assert.Null(error);
        Assert.Equal("cloth.mat", ModelResolver.Join(mesh, "cloth.mat").Choice!.Name);
        Assert.Null(ModelResolver.Join(mesh, "other.mat").Choice);
        Assert.Equal([("dif_0_tex", 7, "red_dif.png"), ("gloss", 2, "0.5")],
                     choice.RttiValues.Select(r => (r.Name, r.Type ?? 0, r.Value)));
    }

    [Fact]
    public void RttiTextureAddsUnboundParameter()
    {
        using var catalog = new RpackCatalog();
        var rtti = Parse().Slots[0].Meshes[0].MaterialsResources[0].Chosen!.RttiValues;
        var tex = ModelResolver.Textures(catalog, null, "skin_red.mat", rtti, out bool inSdb);
        Assert.False(inSdb);
        var t = Assert.Single(tex);
        Assert.Equal(("dif_0_tex", "red_dif.png", "model override (unbound)"), (t.Param, t.Texture, t.Source));
    }

    [Fact]
    public void LaterPakOverridesByBasename()
    {
        string dir = Directory.CreateTempSubdirectory("nr-pak-").FullName;
        try
        {
            string a = Pak(dir, "data0.pak", ("models/a/thing.model", Doc), ("models/a/only0.model", Doc), ("readme.txt", "x"));
            string b = Pak(dir, "data1.pak", ("models/b/THING.model", Doc.Replace("BODY", "BODY1")));
            using var catalog = new ModelCatalog([a, b]);
            Assert.Empty(catalog.Errors);
            Assert.Equal(3, catalog.Models.Count);
            var old = catalog.Models.Single(m => m.Name == "models/a/thing.model");
            Assert.False(old.Wins);
            Assert.Equal(["data1.pak"], old.OverriddenBy);
            var winner = catalog.Find("thing.model")!;
            Assert.Equal(b, winner.Pak);
            Assert.Equal("BODY1", catalog.Load(winner).Slots[0].Name);
            Assert.True(catalog.Find("only0.model")!.Wins);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- installed game ---------------------------------------------------------------------------------------

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (RpackCatalog Rpacks, ModelCatalog Models)? _dltb;

    private static async Task<(RpackCatalog Rpacks, ModelCatalog Models)> Dltb()
    {
        var install = Installs.Require("dltb");
        await Gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            if (_dltb is { } d) return d;
            var rpacks = new RpackCatalog();
            await rpacks.LoadAsync(install.Rpacks(), install.Assets, TestContext.Current.CancellationToken);
            _dltb = (rpacks, new ModelCatalog(install.Paks()));
            return _dltb.Value;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static MeshModel Decode(RpackCatalog c, int gid)
    {
        var (e, i) = c.Split(gid);
        return MeshDecoder.Decode(e.Pack!, i);
    }

    [Fact]
    public async Task EveryStockModelParses()
    {
        var (_, models) = await Dltb();
        Assert.Empty(models.Errors);
        Assert.Equal(815, models.Models.Count);        // the prototype's census (pak/model_json.py): 815, all parse
        int slots = 0;
        foreach (var e in models.Models) slots += models.Load(e).Slots.Count;
        Assert.Equal(7220, slots);
    }

    [Fact]
    public async Task PlayerModelResolvesEverySlotWithTextures()
    {
        var install = Installs.Require("dltb");
        var (rpacks, models) = await Dltb();
        using var sdb = SdbFile.Open(install.Sdb("dx11"));
        var entry = models.Find("player_kc_basic_tpp.model")!;
        var res = ModelResolver.Resolve(models.Load(entry), rpacks, sdb, g => Decode(rpacks, g));
        Assert.NotEmpty(res.SkeletonGids);
        Assert.Equal((15, 14), (res.Slots.Count, res.Slots.Count(s => s.Meshes.Count > 0)));
        foreach (var s in res.Slots.Where(s => s.Meshes.Count > 0))
        {
            var drawn = Assert.Single(s.Meshes, m => m.Drawn);
            Assert.True(drawn.Found, s.Slot.Name);
            Assert.Null(drawn.Error);
            Assert.All(drawn.Submeshes.SelectMany(sm => sm.Textures), t => Assert.NotEmpty(t.Gids));
            Assert.Contains(drawn.Submeshes, sm => sm.Textures.Count > 0);
        }
    }

    /// <summary>
    /// The rest-pose rule (docs/porting.md, Divergences): the rig owner keeps its own bind — Crane's head stays put while
    /// its beard, bound to a slightly different face rig, moves by ~1.3 mm; the body stays at the skeleton's rest.
    /// </summary>
    [Fact]
    public async Task MergedRestKeepsTheHeadInPlace()
    {
        var (rpacks, models) = await Dltb();
        var doc = models.Load(models.Find("player_kc_basic_tpp.model")!);
        var res = ModelResolver.Resolve(doc, rpacks, null, g => Decode(rpacks, g));
        var parts = res.Slots.SelectMany(s => s.Meshes.Where(m => m.Drawn && m.Found))
            .Select(m => (m.Entry.Name, Decode(rpacks, m.Gids[0]))).ToList();
        var skel = ModelSkeleton.Merge(Decode(rpacks, res.SkeletonGids[0]), doc.Skeleton!, parts);
        double Shift(string mesh) => skel.MaxShift(parts.Single(p => p.Name.Contains(mesh, StringComparison.OrdinalIgnoreCase)).Item2);
        Assert.True(Shift("sh2_npc_crane.msh") < 0.05);
        Assert.InRange(Shift("sh2_npc_ft_crane_beard_a.msh"), 1.0, 1.6);
        Assert.True(Shift("eyebrow") < 0.05);
        Assert.Contains(skel.Overrides, o => o.Part == "sh2_npc_crane.msh");
    }

    [Fact]
    public async Task UsedByFindsMeshesAndModels()
    {
        var (rpacks, models) = await Dltb();
        var doc = models.Load(models.Find("player_kc_basic_tpp.model")!);
        var head = doc.Slots.SelectMany(s => s.Meshes).Single(m => m.Name == "sh2_npc_crane.msh");
        string material = head.MaterialsData[0].Name;
        var usage = MaterialUsage.Scan(rpacks, models, TestContext.Current.CancellationToken);
        Assert.Empty(usage.Errors);
        Assert.Contains(usage.MeshesOf(material), g => rpacks.Name(g).Equals("sh2_npc_crane", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(usage.ModelsOf(material), m => m.Basename == "player_kc_basic_tpp.model");
        Assert.Equal(usage.MeshesOf(material), usage.MeshesOf(material.ToUpperInvariant().Replace(".MAT", "")));
    }
}
