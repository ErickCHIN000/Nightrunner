using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Nightrunner.Core.Model;

namespace Nightrunner.Tests;

/// <summary>
/// <c>.model</c> editing (port of the slot/material helpers of <c>project.py</c>). The pinned hashes are the prototype's
/// output for the same edits on the stock DLTB player model (game document after <c>game_doc</c>, and
/// <c>empty_outfit_script</c> of the stock script), checked 2026-09-23.
/// </summary>
public class ModelEditTests
{
    private static JsonObject Doc() => JsonNode.Parse("""
        {"version": 6, "slots": [
          {"slotUid": 100, "name": "HEAD", "meshResources": {"resources": [
            {"name": "a.msh", "selected": true, "materialsData": [{"number": 0, "name": "m.mat"}],
             "materialsResources": [{"number": 0, "resources": [{"name": "m.mat", "selected": true, "rttiValues": []}]}]}]}},
          {"slotUid": 101, "name": "LEGS", "meshResources": {"resources": [{"name": "l.msh", "selected": true}]}}]}
        """)!.AsObject();

    [Fact]
    public void SwapAddsACopyFirstAndSelected()
    {
        var doc = Doc();
        var hit = ModelEdit.SetSlotMesh(doc, "HEAD", "b")!;
        var res = ModelEdit.Resources(ModelEdit.Slot(doc, "HEAD"));
        Assert.Equal(2, res.Count);
        Assert.Same(hit, res[0]);                                   // the game draws the first entry
        Assert.Equal("b.msh", (string?)hit["name"]);
        Assert.True((bool?)hit["selected"]);
        Assert.False((bool?)res[1]!["selected"]);
        Assert.NotNull(hit["materialsData"]);                       // copied from the chosen entry
        var game = ModelEdit.GameDoc(doc);
        var one = Assert.Single(ModelEdit.Resources(ModelEdit.Slot(game, "HEAD")));
        Assert.Equal("b.msh", (string?)one!["name"]);
        Assert.Equal(2, ModelEdit.Resources(ModelEdit.Slot(doc, "HEAD")).Count);   // the project keeps alternatives
    }

    [Fact]
    public void OffStashesAndOnRestores()
    {
        var doc = Doc();
        var stash = new JsonObject();
        ModelEdit.SetSlotEnabled(doc, "LEGS", false, stash);
        Assert.Empty(ModelEdit.Resources(ModelEdit.Slot(doc, "LEGS")));
        Assert.NotNull(stash["LEGS"]);
        ModelEdit.SetSlotEnabled(doc, "LEGS", true, stash);
        Assert.Equal("l.msh", (string?)ModelEdit.Resources(ModelEdit.Slot(doc, "LEGS"))[0]!["name"]);
        Assert.Null(stash["LEGS"]);
    }

    [Fact]
    public void MaterialOverrides()
    {
        var entry = ModelEdit.MeshEntry(Doc(), "HEAD", "a");
        var mat = ModelEdit.MaterialEntry(entry, "new.mat", "base.mat")!;
        ModelEdit.SetRtti(mat, "dif_0_tex", "x.png");
        ModelEdit.SetRtti(mat, "gloss", 0.5);
        ModelEdit.SetRtti(mat, "tint", new double[] { 1, 0, 0 });
        ModelEdit.SetRtti(mat, "gloss", null);
        var mesh = new ModelMesh(entry, 0);
        var (number, choice, _, _) = ModelResolver.Join(mesh, "new.mat");
        Assert.Equal(1, number);
        Assert.Equal("base.mat", choice!.Name);
        Assert.Equal([("dif_0_tex", 7), ("tint", 4)], choice.RttiValues.Select(r => (r.Name, r.Type ?? 0)));
        Assert.Throws<ModelFormatException>(() => ModelEdit.SetRtti(mat, "v", new double[] { 1, 2 }));
    }

    [Theory]
    [InlineData("root", new[] { "p.model" })]
    [InlineData("original", new[] { "models/player/p.model" })]
    [InlineData("both", new[] { "p.model", "models/player/p.model" })]
    public void MemberPaths(string mode, string[] expected) =>
        Assert.Equal(expected, ModelEdit.MemberPaths("models/player/p.model", mode));

    [Fact]
    public void SameEditsAsThePrototype()
    {
        var install = Installs.Require("dltb");
        using var models = new ModelCatalog(install.Paks());
        var e = models.Find("player_tpp_skeleton.model")!;
        var pak = models.Pak(e.Pak);
        var doc = pak.LoadJson(e.Member);
        var stash = new JsonObject();
        var names = ModelEdit.Slots(doc).OfType<JsonObject>().Select(s => (string)s["name"]!).ToList();
        ModelEdit.SetSlotMesh(doc, names[0], "n2b_head.msh", stash);
        ModelEdit.SetSlotEnabled(doc, names[1], false, stash);
        var me = ModelEdit.MaterialEntry(ModelEdit.MeshEntry(doc, names[0], "n2b_head.msh"), "n2b_skin.mat", "sh2_npc_crane.mat")!;
        ModelEdit.SetRtti(me, "dif_0_tex", "n2b_dif.png");
        ModelEdit.SetRtti(me, "gloss", 0.25);
        ModelEdit.SetRtti(me, "tint", new double[] { 1, 0.5, 0 });
        int n = 1;
        while (names.Contains($"TORSO_PART_{n}")) n++;
        ModelEdit.AddSlot(doc, $"TORSO_PART_{n}");
        Assert.Equal("8ba114cd79286973ce1060dd8c616b6caa2f9b57c8f8e38b6df27595d594a3f2", Sha(PyJson.Dumps(ModelEdit.GameDoc(doc))));
        string script = Encoding.UTF8.GetString(pak.Read(pak.Find(ModelEdit.OutfitScript)));
        Assert.Equal("18f1d85834570e83bc2c7d5eb028682aef4576245ce8e867b4f5d3b93e87ec39", Sha(ModelEdit.EmptyOutfitScript(script)));
    }

    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
