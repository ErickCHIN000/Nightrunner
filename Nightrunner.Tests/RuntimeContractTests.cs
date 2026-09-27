using Nightrunner.Core.Anim;
using Nightrunner.Core.Games;

namespace Nightrunner.Tests;

/// <summary>
/// NightrunnerProxy rc1's reading rules, as the app models them: nightrunner.json missing / broken / valid
/// (<see cref="RuntimeSettingsFile"/>), the plan order, and mod.json items. Synthetic installs in a temp folder.
/// </summary>
public class RuntimeContractTests
{
    private static FakeInstall Game(TempDir tmp, string? settings, params string[] ids)
    {
        var g = new FakeInstall(tmp);
        foreach (var id in ids)
            g.Mod(id, $$"""{ "id": "{{id}}", "items": [ { "kind": "rpack", "file": "{{id}}_pc.rpack" }, { "kind": "pak", "file": "{{id}}.pak" } ] }""",
                  $"{id}_pc.rpack", $"{id}.pak");
        if (settings is not null) FakeInstall.Text(g.Settings, settings);
        return g;
    }

    private static List<string> Loading(RuntimeContent rc) => rc.Items.Where(i => i.Loads).Select(i => i.Source).Distinct().ToList();

    // ---- nightrunner.json ------------------------------------------------------------------------------------------

    [Fact]
    public void AMissingFileIsTheDefaults()
    {
        var s = RuntimeSettingsFile.Load(Path.Combine(Path.GetTempPath(), "nightrunner-none", "nightrunner.json"));
        Assert.False(s.IsBroken);
        Assert.False(s.Exists);
        Assert.False(s.Console);
        Assert.Equal("info", s.LogLevel);
        Assert.Empty(s.Mods);

        using var tmp = new TempDir();
        var rc = RuntimeContent.Read(Game(tmp, null, "b", "a").Install);
        Assert.Null(rc.NoMods);
        Assert.Equal(["a", "b"], Loading(rc));
    }

    [Theory]
    [InlineData("", "is empty")]
    [InlineData("  \r\n ", "is empty")]
    [InlineData("""{ "schema": "nightrunner/settings@2" }""", "has schema 'nightrunner/settings@2'")]
    [InlineData("""{ "schema": "" }""", "has schema ''")]
    [InlineData("""{ "logLevel": "verbose" }""", "has logLevel 'verbose'")]
    [InlineData("""{ "logLevel": "3" }""", "has logLevel '3'")]
    [InlineData("""{ "mods": [ null, { "id": "a", "enabled": false } ] }""", "has a null entry at mods[0]")]
    [InlineData("""{ "mods": [ { "id": "a", "enabled": false }, { "name": "b" } ] }""", "has an entry with no id at mods[1]")]
    [InlineData("""{ "mods": [ { "id": "  " } ] }""", "has an entry with no id at mods[0]")]
    [InlineData("""{ "mods": [ { "id": "a", "enabled": "false" } ] }""", "is not valid at $.mods[0].enabled, line 1")]
    [InlineData("""{ "mods": [ { "id": "a" "enabled": false } ] }""", "is not valid")]
    [InlineData("null", "is null, not an object")]
    [InlineData("[]", "is not valid at $")]
    [InlineData("""{ "console": "yes" }""", "is not valid at $.console")]
    [InlineData("""{ "logLevel": 3 }""", "is not valid at $.logLevel")]
    [InlineData("""{ "mods": { "id": "a" } }""", "is not valid at $.mods")]
    [InlineData("""{ "mods": [ 1 ] }""", "is not valid at $.mods[0]")]
    [InlineData("""{ "mods": [ { "id": "a", "order": 1.5 } ] }""", "is not valid at $.mods[0].order")]
    [InlineData("""{ "mods": [ { "id": 7 } ] }""", "is not valid at $.mods[0].id")]
    [InlineData("""{ "mods": [ { "id": "a" }""", "is not valid")]
    public void ABrokenFileLoadsNoMods(string text, string why)
    {
        var s = RuntimeSettingsFile.Parse(text);
        Assert.True(s.IsBroken);
        Assert.StartsWith($"nightrunner.json {why}", s.Problem);
        Assert.False(s.Console);
        Assert.Empty(s.Plan(["a", "b"]));

        using var tmp = new TempDir();
        var g = Game(tmp, text, "a", "b");
        var rc = RuntimeContent.Read(g.Install);
        Assert.Equal($"runtime loads no mods ({s.Problem})", rc.NoMods);
        Assert.Equal(rc.NoMods, rc.Problems[0]);
        Assert.Empty(Loading(rc));
        Assert.All(rc.Mods, m => Assert.Equal(rc.NoMods, m.Blocked));
        Assert.All(rc.Mods, m => Assert.False(m.Enabled || m.Loads));
        Assert.All(rc.Mods, m => Assert.Contains(rc.NoMods, m.Problems));
        Assert.Equal(["a", "b"], rc.Mods.Select(m => m.Id));   // still listed, by id

        // nothing of the mods reaches the catalogs
        Assert.DoesNotContain(g.Install.Rpacks(rc), g.Install.Paths.IsCustom);
        Assert.Equal(g.Install.Paks(runtime: null), g.Install.Paks(rc));
        Assert.Equal(0, g.Install.CustomRpackCount());
        Assert.Null(AnimPackOrder.FromRuntime(rc).OrderOf("a_pc.rpack"));
    }

    [Theory]
    [InlineData("""{ }""", false, "info", "a,b,c")]
    [InlineData("""{ "schema": " NIGHTRUNNER/settings@1 ", "mods": [] }""", false, "info", "a,b,c")]
    [InlineData("""{ "Mods": [ { "Id": "b", "Enabled": true, "Order": -5 }, { "id": "a", "order": 3 } ] }""", false, "info", "b,a,c")]
    [InlineData("""{ "mods": [ { "id": "b", "enabled": false } ] }""", false, "info", "a,c")]
    [InlineData("""{ "console": true, "logLevel": " ERROR " }""", true, "error", "a,b,c")]
    [InlineData("""{ "logLevel": null, "console": null, "mods": null, "future": { "x": 1 } }""", false, "info", "a,b,c")]
    [InlineData("{ // a comment\n \"mods\": [ { \"id\": \"x-not-installed\", \"enabled\": false }, ], }", false, "info", "a,b,c")]
    public void AValidFileIsObeyed(string text, bool console, string level, string expected)
    {
        var s = RuntimeSettingsFile.Parse(text);
        Assert.False(s.IsBroken, s.Problem);
        Assert.Equal(console, s.Console);
        Assert.Equal(level, s.LogLevel);

        using var tmp = new TempDir();
        var rc = RuntimeContent.Read(Game(tmp, text, "a", "b", "c").Install);
        Assert.Null(rc.NoMods);
        Assert.Equal(expected.Split(','), Loading(rc));
        Assert.Equal(expected.Split(','), s.Plan(["c", "a", "b"]));
    }

    [Fact]
    public void AModListedTwiceKeepsItsFirstEntryWithAWarning()
    {
        var s = RuntimeSettingsFile.Parse("""{ "mods": [ { "id": "a", "enabled": false }, { "id": " A ", "enabled": true }, { "id": "c", "order": -1 } ] }""");
        Assert.False(s.IsBroken);
        Assert.Equal(2, s.Mods.Count);
        Assert.False(s.Find("A")!.Enabled);
        Assert.Equal(["nightrunner.json: mods[1] lists 'A' again; the first entry is used"], s.Warnings);
        Assert.Equal(["c", "b"], s.Plan(["a", "b", "c"]));   // a off, c listed, b unlisted last
    }

    /// <summary>Unlisted mods come after every listed one, even after an order of int.MaxValue (the old max + 1 wrapped).</summary>
    [Fact]
    public void UnlistedModsSortLastWithoutOverflow()
    {
        string text = $$"""{ "mods": [ { "id": "z", "order": {{int.MaxValue}} }, { "id": "y", "order": {{int.MinValue}} } ] }""";
        Assert.Equal(["y", "z", "a", "b"], RuntimeSettingsFile.Parse(text).Plan(["b", "a", "y", "z"]));
        Assert.Equal(int.MaxValue, RuntimeSettingsFile.Parse(text).NextOrder);

        using var tmp = new TempDir();
        var rc = RuntimeContent.Read(Game(tmp, text, "a", "b", "y", "z").Install);
        Assert.Equal(["y", "z", "a", "b"], Loading(rc));
        Assert.Equal(["y", "z", "a", "b"], rc.Mods.Select(m => m.Id));
        Assert.Equal(int.MaxValue, rc.Mods.Single(m => m.Id == "a").Order);
    }

    /// <summary>Listed ties break on the id; a listed mod with a high order still comes before an unlisted one.</summary>
    [Fact]
    public void ListedModsComeFirstThenUnlistedById()
    {
        const string text = """{ "mods": [ { "id": "m", "order": 100 }, { "id": "k", "order": 100 }, { "id": "x", "order": 0 } ] }""";
        using var tmp = new TempDir();
        var rc = RuntimeContent.Read(Game(tmp, text, "a", "k", "m", "x", "b").Install);
        Assert.Equal(["x", "k", "m", "a", "b"], Loading(rc));
    }

    // ---- mod.json ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{ "id": "m", "items": { "kind": "rpack", "file": "m_pc.rpack" } }""", "mod.json unreadable (not valid at $.items")]
    [InlineData("""{ "id": "m", "items": "m_pc.rpack" }""", "mod.json unreadable (not valid at $.items")]
    [InlineData("""{ "id": "m", "items": [ 1 ] }""", "mod.json unreadable (not valid at $.items[0]")]
    [InlineData("""{ "id": "m", "items": [ { "kind": "rpack", "file": "m_pc.rpack", "order": "1" } ] }""", "mod.json unreadable (not valid at $.items[0].order")]
    [InlineData("""{ "id": 5 }""", "mod.json unreadable (not valid at $.id")]
    [InlineData("""{ "id": "m", "items": [ null ] }""", "items[0] is null")]
    [InlineData("""{ "id": "m", "items": [ { "kind": "rpack", "file": "m_pc.rpack", "at": "before: data/.rpack" } ] }""", "at 'before: data/.rpack' is not after-builtins or before:<pack>")]
    [InlineData("""{ "id": "m", "items": [ { "kind": "rpack", "file": "m_pc.rpack", "at": "before:" } ] }""", "at 'before:' is not after-builtins or before:<pack>")]
    [InlineData("""{ "id": "m", "items": [ { "kind": "texture", "file": "m_pc.rpack" } ] }""", "kind 'texture' is not rpack/pak/sdb/audio/map")]
    [InlineData("""{ "id": "m", "items": [ { "kind": "rpack", "file": "../m_pc.rpack" } ] }""", "file '../m_pc.rpack' must be relative and stay inside the mod folder")]
    [InlineData("""{ "id": " ", "items": [] }""", "mod.json has no id")]
    [InlineData("null", "mod.json is empty")]
    public void ABadManifestMakesTheModInvalid(string json, string why)
    {
        using var tmp = new TempDir();
        var g = new FakeInstall(tmp);
        g.Mod("m", json, "m_pc.rpack");
        var mod = Assert.Single(RuntimeContent.Read(g.Install).Mods);
        Assert.False(mod.Valid);
        Assert.StartsWith(why, mod.Invalid);
        Assert.False(mod.Loads);
    }

    [Fact]
    public void ItemsNullIsAValidModWithNothingToLoad()
    {
        using var tmp = new TempDir();
        var g = new FakeInstall(tmp);
        g.Mod("m", """{ "id": "m", "items": null }""");
        var mod = Assert.Single(RuntimeContent.Read(g.Install).Mods);
        Assert.True(mod.Valid);
        Assert.Equal(["no items, nothing to load"], mod.Problems);
    }

    [Theory]
    [InlineData("before:common_anims_pc", "common_anims_pc")]
    [InlineData("  BEFORE: data/Common_Anims_PC.rpack ", "common_anims_pc")]
    [InlineData(@"before:assets\sub\common_anims_pc.RPACK", "common_anims_pc")]
    public void BeforeNamesAreNormalisedLikeTheRuntime(string at, string pack)
    {
        Assert.True(RuntimeContent.TryParseAt(at, out var phase, out var before));
        Assert.Equal((RuntimePhase.BeforePack, pack), (phase, before));
        using var tmp = new TempDir();
        var g = new FakeInstall(tmp);
        g.Mod("m", $$"""{ "id": "m", "items": [ { "kind": "rpack", "file": "m_pc.rpack", "at": "{{at.Replace("\\", "\\\\")}}" } ] }""", "m_pc.rpack");
        var item = Assert.Single(RuntimeContent.Read(g.Install).Items);
        Assert.True(item.Loads, item.Problem);
        Assert.Equal($"before:{pack}", item.At);
        Assert.Equal("", RuntimeContent.NormalizePackName(" folder/ "));
        Assert.Equal("", RuntimeContent.NormalizePackName(null));
    }

    [Fact]
    public void SdbLoadsAtMmCreateAudioAndMapAreSkipped()
    {
        using var tmp = new TempDir();
        var g = new FakeInstall(tmp);
        g.Mod("m", """
            { "id": "m", "items": [
              { "kind": "sdb", "file": "a.sdb", "at": "before:no_such_pack" },
              { "kind": "sdb", "file": "b.sdb", "at": "before:common_anims_pc" },
              { "kind": "sdb", "file": "c.sdb" },
              { "kind": "audio", "file": "x.bnk" },
              { "kind": "map", "file": "y.map" } ] }
            """, "a.sdb", "b.sdb", "c.sdb", "x.bnk", "y.map");
        var rc = RuntimeContent.Read(g.Install);
        var items = rc.Items.ToDictionary(i => i.Name);
        Assert.All(new[] { "a.sdb", "b.sdb", "c.sdb" }, n => Assert.True(items[n].Loads, items[n].Problem));
        Assert.All(new[] { "a.sdb", "b.sdb", "c.sdb" }, n => Assert.Equal("mmcreate", items[n].At));
        Assert.Contains("m/a.sdb: sdb loads at mmcreate, not before:no_such_pack", rc.Problems);
        Assert.Contains("b.sdb: sdb loads at mmcreate, not before:common_anims_pc", rc.Mods.Single().Problems);
        Assert.DoesNotContain(rc.Problems, p => p.Contains("c.sdb"));
        Assert.Equal("audio is not loaded by the runtime", items["x.bnk"].Problem);
        Assert.Equal("map is not loaded by the runtime", items["y.map"].Problem);
        Assert.False(items["x.bnk"].Loads || items["y.map"].Loads);
        Assert.True(rc.Mods.Single().Valid);   // accepted, only skipped
        Assert.DoesNotContain(g.Install.Rpacks(rc), g.Install.Paths.IsCustom);   // no sdb or audio reaches the pack catalog
    }
}
