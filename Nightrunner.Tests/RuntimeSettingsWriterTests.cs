using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nightrunner.Core.Games;

namespace Nightrunner.Tests;

/// <summary>
/// Switching a mod on or off in NightrunnerProxy's <c>nightrunner.json</c> — the one file this tool writes in a game
/// folder. Synthetic installs only; every written file is read back through a port of the runtime's own reader
/// (<see cref="Proxy"/>) and its plan compared with what the Mods window shows (<see cref="RuntimeContent"/>).
/// </summary>
public class RuntimeSettingsWriterTests
{
    /// <summary>The installed DLTB file as of 2026-09-27: LF, two-space indent, one line per entry, final newline, no BOM.</summary>
    private const string Installed =
        "{\n" +
        "  \"schema\": \"nightrunner/settings@1\",\n" +
        "  \"console\": true,\n" +
        "  \"logLevel\": \"info\",\n" +
        "  \"mods\": [\n" +
        "    { \"id\": \"sample-modpack\", \"enabled\": true, \"order\": 1 },\n" +
        "    { \"id\": \"pause-menu\", \"enabled\": false, \"order\": 2 },\n" +
        "    { \"id\": \"player-skin-sdb\", \"enabled\": false, \"order\": 3 }\n" +
        "  ]\n" +
        "}\n";

    private static readonly Func<string, IReadOnlyList<int>> NotRunning = _ => [];

    private static FakeInstall Game(TempDir tmp, string? settings, params string[] ids)
    {
        var g = new FakeInstall(tmp).Proxy();
        foreach (var id in ids)
            g.Mod(id, $$"""{ "id": "{{id}}", "items": [ { "kind": "rpack", "file": "{{id}}_pc.rpack" } ] }""", $"{id}_pc.rpack");
        if (settings is not null) File.WriteAllBytes(g.Settings, Encoding.UTF8.GetBytes(settings));
        return g;
    }

    private static RuntimeSwitch Switch(FakeInstall g, string id, bool on, Func<string, IReadOnlyList<int>>? running = null) =>
        RuntimeSettingsWriter.SetEnabled(g.Install, id, on, runtimeModding: true, running ?? NotRunning);

    /// <summary>The runtime's plan from the file on disk equals the mods the window shows as loading, in the same order.</summary>
    private static void AssertSamePlan(FakeInstall g)
    {
        var rc = RuntimeContent.Read(g.Install);
        var valid = rc.Mods.Where(m => m.Valid).Select(m => m.Id).ToList();
        var runtime = Proxy.Plan(valid, Proxy.Load(g.Settings));
        Assert.Equal(runtime, rc.Mods.Where(m => m.Valid && m.Enabled).Select(m => m.Id));
        Assert.Equal(runtime, rc.Items.Where(i => i.Loads).Select(i => i.Source).Distinct());
    }

    [Fact]
    public void OnThenOffGivesTheSameBytes()
    {
        using var tmp = new TempDir();
        var g = Game(tmp, Installed, "sample-modpack", "pause-menu", "player-skin-sdb");
        AssertSamePlan(g);
        Assert.Equal(["sample-modpack"], RuntimeContent.Read(g.Install).Mods.Where(m => m.Enabled).Select(m => m.Id));

        var on = Switch(g, "pause-menu", true);
        Assert.Equal(RuntimeSwitchState.Written, on.State);
        Assert.Equal("applies at next game start", on.Message);
        string written = File.ReadAllText(g.Settings);
        Assert.Equal(Installed.Replace("\"pause-menu\", \"enabled\": false", "\"pause-menu\", \"enabled\": true"), written);
        Assert.Equal(Installed, File.ReadAllText(g.Settings + ".bak"));
        Assert.False(File.Exists(g.Settings + ".tmp"));
        Assert.Equal(["sample-modpack", "pause-menu"], RuntimeContent.Read(g.Install).Mods.Where(m => m.Enabled).Select(m => m.Id));
        AssertSamePlan(g);

        Assert.Equal(RuntimeSwitchState.Unchanged, Switch(g, "pause-menu", true).State);
        Assert.Equal(written, File.ReadAllText(g.Settings));

        Assert.True(Switch(g, "pause-menu", false).Written);
        Assert.Equal(Encoding.UTF8.GetBytes(Installed), File.ReadAllBytes(g.Settings));
        Assert.Equal(written, File.ReadAllText(g.Settings + ".bak"));
        AssertSamePlan(g);
    }

    /// <summary>A mod not listed is on and last; switching it off lists it at that effective order, not first.</summary>
    [Fact]
    public void AnUnlistedModIsAddedAtItsEffectiveOrder()
    {
        using var tmp = new TempDir();
        var g = Game(tmp, """
            {
              "schema": "nightrunner/settings@1",
              "mods": [
                { "id": "a", "order": 4 },
                { "id": "gone", "enabled": false, "order": 7 }
              ]
            }
            """, "a", "m", "z");
        var before = RuntimeContent.Read(g.Install).Mods;
        Assert.Equal(["a", "m", "z"], before.Select(m => m.Id));
        Assert.Equal(8, before.Single(m => m.Id == "z").Order);   // one past the highest listed order, "gone" included

        Assert.Equal(RuntimeSwitchState.Unchanged, Switch(g, "m", true).State);   // unlisted is already on
        Assert.True(Switch(g, "z", false).Written);
        var text = File.ReadAllText(g.Settings);
        Assert.EndsWith("{ \"id\": \"gone\", \"enabled\": false, \"order\": 7 },\r\n    { \"id\": \"z\", \"enabled\": false, \"order\": 8 }\r\n  ]\r\n}", text);   // no final newline, as given
        var after = RuntimeContent.Read(g.Install).Mods;
        Assert.Equal(["a", "m"], after.Where(m => m.Enabled).Select(m => m.Id));
        Assert.True(after.Single(m => m.Id == "z").Listed);
        AssertSamePlan(g);

        // and back on: listed at 8, so still after every listed mod, but now ahead of the unlisted m: unlisted mods sort
        // after every listed one, the runtime's rule
        Assert.True(Switch(g, "z", true).Written);
        Assert.Equal(["a", "z", "m"], RuntimeContent.Read(g.Install).Mods.Where(m => m.Enabled).Select(m => m.Id));
        AssertSamePlan(g);
    }

    /// <summary>No nightrunner.json: one is created with RuntimeSettings' defaults (console off) and the mod's entry; no backup.</summary>
    [Fact]
    public void AMissingFileIsCreatedWithTheRuntimeDefaults()
    {
        using var tmp = new TempDir();
        var g = Game(tmp, null, "a", "b");
        Assert.True(Switch(g, "b", false).Written);
        var root = JsonNode.Parse(File.ReadAllText(g.Settings))!.AsObject();
        Assert.Equal(["schema", "console", "logLevel", "mods"], root.Select(p => p.Key));
        Assert.Equal("nightrunner/settings@1", (string?)root["schema"]);
        Assert.False((bool)root["console"]!);
        var read = RuntimeSettingsFile.Load(g.Settings);
        Assert.False(read.IsBroken);
        Assert.False(read.Console);
        Assert.Equal("info", (string?)root["logLevel"]);
        Assert.Equal("""[{"id":"b","enabled":false,"order":0}]""", root["mods"]!.ToJsonString());
        Assert.False(File.Exists(g.Settings + ".bak"));
        Assert.Equal(["a"], RuntimeContent.Read(g.Install).Mods.Where(m => m.Enabled).Select(m => m.Id));
        AssertSamePlan(g);
    }

    /// <summary>Keys the writer does not know, other entries, their spelling and order, and console/logLevel all survive.</summary>
    [Fact]
    public void UnknownKeysAndOtherEntriesSurvive()
    {
        using var tmp = new TempDir();
        const string original = """
            {
              "LogLevel": "debug",
              "future": { "list": [1, 2.50, "x"], "flag": null },
              "Mods": [
                { "note": "keep me", "Id": " b ", "Enabled": true, "order": 2, "extra": [ "é", "a+b" ] },
                { "id": "a", "order": 1 },
                { "id": "b", "enabled": false, "order": 9 }
              ],
              "console": false,
              "schema": "nightrunner/settings@1"
            }
            """;
        var g = Game(tmp, original, "a", "b");
        Assert.True(Switch(g, "b", false).Written);

        var was = JsonNode.Parse(original)!.AsObject();
        var now = JsonNode.Parse(File.ReadAllText(g.Settings))!.AsObject();
        Assert.Equal(was.Select(p => p.Key), now.Select(p => p.Key));
        var first = now["Mods"]![0]!.AsObject();
        Assert.Equal(["note", "Id", "Enabled", "order", "extra"], first.Select(p => p.Key));
        Assert.False((bool)first["Enabled"]!);   // the runtime's first match, spelled as it was
        first["Enabled"] = true;
        Assert.True(JsonNode.DeepEquals(was, now));   // nothing else changed
        Assert.Contains("\"é\", \"a+b\"", File.ReadAllText(g.Settings));
        Assert.Equal(["a"], RuntimeContent.Read(g.Install).Mods.Where(m => m.Enabled).Select(m => m.Id));
        AssertSamePlan(g);
    }

    [Fact]
    public void RefusedWhileTheGameRuns()
    {
        using var tmp = new TempDir();
        var g = Game(tmp, Installed, "sample-modpack", "pause-menu");
        string? asked = null;
        var r = Switch(g, "pause-menu", true, bin => { asked = bin; return [4242]; });
        Assert.Equal(RuntimeSwitchState.Refused, r.State);
        Assert.Equal("game running (pid 4242)", r.Message);
        Assert.Equal(g.Bin, asked);
        Assert.Equal(Installed, File.ReadAllText(g.Settings));
        Assert.False(File.Exists(g.Settings + ".bak"));
    }

    [Fact]
    public void RefusedWithoutRuntimeModdingOrTheRuntime()
    {
        using var tmp = new TempDir();
        var g = Game(tmp, Installed, "pause-menu");
        Assert.Equal("runtime modding off",
                     RuntimeSettingsWriter.SetEnabled(g.Install, "pause-menu", true, runtimeModding: false, NotRunning).Message);
        File.Delete(Path.Combine(g.Bin, "dxgi.dll"));
        Assert.Equal("runtime not installed", Switch(g, "pause-menu", true).Message);
        Assert.Equal(Installed, File.ReadAllText(g.Settings));
    }

    /// <summary>
    /// A file the runtime treats as broken (it loads no mods), or one with comments, is left alone — the null entry and the
    /// entry with no id included — and so is one that cannot be written.
    /// </summary>
    [Fact]
    public void RefusedWhenTheFileCannotBeEditedFaithfully()
    {
        using var tmp = new TempDir();
        var g = Game(tmp, null, "a");
        foreach (var (text, why) in new[]
                 {
                     ("""{ "mods": [ { "id": "a", "enabled": "yes" } ] }""", "nightrunner.json is not valid at $.mods[0].enabled, line 1"),
                     ("""{ "mods": { "id": "a" } }""", "nightrunner.json is not valid at $.mods"),
                     ("""{ "mods": [ null, { "id": "a" } ] }""", "nightrunner.json has a null entry at mods[0]"),
                     ("""{ "mods": [ { "id": "a" }, { "enabled": false } ] }""", "nightrunner.json has an entry with no id at mods[1]"),
                     ("""{ "mods": [ { "id": " " } ] }""", "nightrunner.json has an entry with no id at mods[0]"),
                     ("""{ "schema": "nightrunner/settings@2" }""", "nightrunner.json has schema 'nightrunner/settings@2'"),
                     ("""{ "logLevel": "verbose" }""", "nightrunner.json has logLevel 'verbose'"),
                     ("null", "nightrunner.json is null, not an object"),
                     ("", "nightrunner.json is empty"),
                     (" \r\n", "nightrunner.json is empty"),
                     ("{ // mine\n \"mods\": [] }", "nightrunner.json has comments (would be lost)"),
                 })
        {
            File.WriteAllText(g.Settings, text);
            var r = Switch(g, "a", false);
            Assert.Equal(RuntimeSwitchState.Refused, r.State);
            Assert.StartsWith(why, r.Message);
            Assert.Equal(text, File.ReadAllText(g.Settings));
            Assert.False(File.Exists(g.Settings + ".bak"));
            // the pure edit refuses too (it used to dereference a null entry)
            var content = RuntimeContent.Read(g.Install);
            Assert.Throws<FormatException>(() => RuntimeSettingsWriter.Switch(Encoding.UTF8.GetBytes(text), content, "a", false));
        }

        File.WriteAllText(g.Settings, Installed.Replace("pause-menu", "a"));
        File.SetAttributes(g.Settings, FileAttributes.ReadOnly);
        try
        {
            var r = Switch(g, "a", true);
            Assert.Equal("no write access", r.Message);
            Assert.False(File.Exists(g.Settings + ".tmp"));
        }
        finally { File.SetAttributes(g.Settings, FileAttributes.Normal); }
    }

    /// <summary>An invalid mod can be switched off and on; it stays invalid and never loads.</summary>
    [Fact]
    public void AnInvalidModCanBeSwitched()
    {
        using var tmp = new TempDir();
        var g = Game(tmp, Installed, "sample-modpack");
        g.Mod("broken", """{ "id": "broken", "items": [ { "kind": "rpack", "file": "gone_pc.rpack" } ] }""");
        Assert.True(Switch(g, "broken", false).Written);
        var mod = RuntimeContent.Read(g.Install).Mods.Single(m => m.Id == "broken");
        Assert.False(mod.Valid);
        Assert.False(mod.Enabled);
        Assert.Equal(4, mod.Order);
        Assert.True(Switch(g, "broken", true).Written);
        mod = RuntimeContent.Read(g.Install).Mods.Single(m => m.Id == "broken");
        Assert.True(mod.Enabled);
        Assert.False(mod.Loads);
        Assert.Contains("does not exist", mod.Invalid);
        AssertSamePlan(g);
    }

    /// <summary>The real process check finds this test process under its own executable's folder.</summary>
    [Fact]
    public void ProcessesUnderFindsThisProcess()
    {
        string exe = Environment.ProcessPath!;
        Assert.Contains(Environment.ProcessId, RuntimeSettingsWriter.ProcessesUnder(Path.GetDirectoryName(exe)!));
        using var tmp = new TempDir();
        Assert.Empty(RuntimeSettingsWriter.ProcessesUnder(tmp.Path));
    }

    /// <summary>A game with no runtime module: refused by name before anything is read.</summary>
    [Fact]
    public void RefusedForAGameWithoutARuntimeModule()
    {
        using var tmp = new TempDir();
        var g = Game(tmp, Installed, "pause-menu");
        var dl2 = new GameInstall(g.Root, GameProfile.Dl2);
        var r = RuntimeSettingsWriter.SetEnabled(dl2, "pause-menu", true, runtimeModding: true, NotRunning);
        Assert.Equal(RuntimeSwitchState.Refused, r.State);
        Assert.Equal("no runtime module for dl2", r.Message);
        Assert.Equal(Installed, File.ReadAllText(g.Settings));
    }

    /// <summary>A mod listed twice: the runtime uses the first entry (with a warning), and so does the switch.</summary>
    [Fact]
    public void AModListedTwiceSwitchesItsFirstEntry()
    {
        using var tmp = new TempDir();
        const string text = "{\n  \"mods\": [\n    { \"id\": \"a\", \"enabled\": false, \"order\": 1 },\n    { \"id\": \" A \", \"enabled\": true, \"order\": 9 }\n  ]\n}\n";
        var g = Game(tmp, text, "a", "b");
        var rc = RuntimeContent.Read(g.Install);
        Assert.False(rc.Mods.Single(m => m.Id == "a").Enabled);
        Assert.Contains("nightrunner.json: mods[1] lists 'A' again; the first entry is used", rc.Problems);
        Assert.True(Switch(g, "a", true).Written);
        Assert.Equal(text.Replace("\"enabled\": false", "\"enabled\": true"), File.ReadAllText(g.Settings));
        AssertSamePlan(g);
    }

    /// <summary>An order at int.MaxValue: an appended entry stays at int.MaxValue instead of wrapping to int.MinValue.</summary>
    [Fact]
    public void AnAppendedOrderDoesNotOverflow()
    {
        using var tmp = new TempDir();
        var g = Game(tmp, $$"""{ "mods": [ { "id": "a", "order": {{int.MaxValue}} } ] }""", "a", "b", "c");
        Assert.Equal(int.MaxValue, RuntimeContent.Read(g.Install).Mods.Single(m => m.Id == "b").Order);
        Assert.Equal(["a", "b", "c"], RuntimeContent.Read(g.Install).Mods.Select(m => m.Id));
        Assert.True(Switch(g, "c", false).Written);
        Assert.Equal(int.MaxValue, RuntimeSettingsFile.Load(g.Settings).Find("c")!.Order);
        Assert.Equal(["a", "b"], RuntimeContent.Read(g.Install).Mods.Where(m => m.Enabled).Select(m => m.Id));
        AssertSamePlan(g);
    }

    /// <summary>
    /// NightrunnerProxy's reader and planner (rc1), ported from <c>Core/Content/ModJson.cs</c>, <c>RuntimeSettings.Load</c>
    /// and <c>LoadPlanBuilder.Build</c> (not the app's own <see cref="RuntimeSettingsFile"/>), so the written file is
    /// checked by the runtime's rules.
    /// </summary>
    private static class Proxy
    {
        private sealed class SettingsJson
        {
            public string? Schema { get; set; }
            public bool? Console { get; set; }
            public string? LogLevel { get; set; }
            public List<ModEntryJson?>? Mods { get; set; }
        }

        private sealed class ModEntryJson
        {
            public string? Id { get; set; }
            public bool? Enabled { get; set; }
            public int? Order { get; set; }
        }

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>
        /// RuntimeSettings.Load: (id, enabled, order) per entry, first entry per id; a file it treats as broken (it would
        /// load no mods) is a test failure here.
        /// </summary>
        public static List<(string Id, bool Enabled, int Order)> Load(string path)
        {
            if (!File.Exists(path)) return [];
            string text = File.ReadAllText(path);
            Assert.NotEqual(0, text.Trim().Length);
            var json = JsonSerializer.Deserialize<SettingsJson>(text, Options);
            Assert.NotNull(json);
            if (json.Schema is { } schema) Assert.Equal("nightrunner/settings@1", schema.Trim(), ignoreCase: true);
            Assert.Contains(json.LogLevel?.Trim().ToLowerInvariant() ?? "info", new[] { "debug", "info", "warn", "error", "fatal" });
            var list = new List<(string Id, bool Enabled, int Order)>();
            foreach (var e in json.Mods ?? [])
            {
                Assert.NotNull(e);
                Assert.False(string.IsNullOrWhiteSpace(e.Id));
                string id = e.Id!.Trim();
                if (list.Any(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add((id, e.Enabled ?? true, e.Order ?? 0));
            }
            return list;
        }

        /// <summary>LoadPlanBuilder.Build: listed mods keep their choice and come first by (order, id); unlisted ones are on, after them, by id.</summary>
        public static List<string> Plan(IEnumerable<string> mods, List<(string Id, bool Enabled, int Order)> settings)
        {
            var enabled = new List<(bool Listed, int Order, string Id)>();
            foreach (var mod in mods)
            {
                var choice = settings.FirstOrDefault(s => string.Equals(s.Id, mod, StringComparison.OrdinalIgnoreCase));
                if (choice.Id is not null)
                {
                    if (!choice.Enabled) continue;
                    enabled.Add((true, choice.Order, mod));
                    continue;
                }
                enabled.Add((false, 0, mod));
            }
            return enabled.OrderBy(e => e.Listed ? 0 : 1).ThenBy(e => e.Order).ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                          .Select(e => e.Id).ToList();
        }
    }
}
