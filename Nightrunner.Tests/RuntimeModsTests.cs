using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Nightrunner.Core.Games;
using Nightrunner.Core.Project;
using Nightrunner.Core.Rpack;
using File = System.IO.File;

namespace Nightrunner.Tests;

/// <summary>
/// The Mods window's model and the runtime layout Settings shows: every mod folder with its manifest, switch, items and
/// problems; the installed runtime path by path; what a mod overrides in the stock game; and a build laid out as a
/// NightrunnerProxy mod folder, read back as the runtime would. Synthetic installs in a temp folder.
/// </summary>
public class RuntimeModsTests
{
    [Fact]
    public void EveryModFolderIsListedWithItsManifestSwitchAndProblems()
    {
        using var tmp = new TempDir();
        var g = new FakeInstall(tmp);
        FakeInstall.Text(Path.Combine(g.Bin, "Nightrunner", RuntimeContent.SettingsName),
                         """{ "mods": [ { "id": "b", "order": 2 }, { "id": "off", "enabled": false, "order": 1 }, { "id": "bad", "order": 0 } ] }""");
        g.Mod("a", """
            { "id": "a", "name": "A mod", "version": "1.2", "author": "me", "description": "d",
              "items": [ { "kind": "rpack", "file": "packs/anim_pc.rpack", "at": "before:common_anims_pc" },
                         { "kind": "sdb", "file": "s.sdb", "order": 1 },
                         { "kind": "audio", "file": "x.bnk", "order": 2 } ] }
            """, "packs/anim_pc.rpack", "s.sdb", "x.bnk");
        g.Mod("b", """{ "id": "b", "items": [ { "kind": "pak", "file": "b.pak" } ] }""", "b.pak");
        g.Mod("off", """{ "id": "off", "items": [ { "kind": "rpack", "file": "o_pc.rpack" } ] }""", "o_pc.rpack");
        g.Mod("empty", """{ "id": "empty" }""");
        g.Mod("bad", """{ "id": "bad", "name": "Bad", "items": [ { "kind": "rpack", "file": "gone_pc.rpack" } ] }""");
        g.Mod("zdupe", """{ "id": "B", "items": [] }""");
        g.Mod("junk", "{ not json");

        var rc = RuntimeContent.Read(g.Install);
        // valid mods in plan order (order, id; unlisted last), then the invalid ones by folder
        Assert.Equal(["off", "b", "a", "empty", "bad", "junk", "B"], rc.Mods.Select(m => m.Id));
        var a = rc.Mods.Single(m => m.Id == "a");
        Assert.Equal(("A mod", "1.2", "me", "d"), (a.Name, a.Version, a.Author, a.Description));
        Assert.False(a.Listed);
        Assert.True(a.Valid && a.Enabled && a.Loads);
        Assert.Equal(3, a.Order);   // one past the highest listed order
        Assert.Equal(["before:common_anims_pc", "mmcreate", "after-builtins"], a.Items.Select(i => i.At));
        Assert.Equal(["audio is not loaded by the runtime"], a.Items.Where(i => !i.Loads).Select(i => i.Problem));
        Assert.Contains(a.Problems, p => p.Contains("x.bnk: audio is not loaded"));

        var off = rc.Mods.Single(m => m.Id == "off");
        Assert.True(off.Listed && off.Valid);
        Assert.False(off.Enabled || off.Loads);
        Assert.Equal("disabled in nightrunner.json", Assert.Single(off.Items).Problem);
        Assert.Empty(off.Problems);   // switched off is not a problem

        Assert.Contains("no items", Assert.Single(rc.Mods.Single(m => m.Id == "empty").Problems));
        var bad = rc.Mods.Single(m => m.Id == "bad");
        Assert.False(bad.Valid);
        Assert.Equal("Bad", bad.Name);                                    // named even though its items are refused
        Assert.Equal("file 'gone_pc.rpack' does not exist", bad.Invalid);
        Assert.True(bad.Listed);
        Assert.Empty(bad.Items);
        Assert.Contains("unreadable", rc.Mods.Single(m => m.Id == "junk").Invalid);
        Assert.Equal("id 'B' is taken (skipped)", rc.Mods.Single(m => m.Folder.EndsWith("zdupe")).Invalid);
        Assert.Equal(2, rc.Mods.Count(m => m.Valid && m.Loads));        // a, b
    }

    [Fact]
    public void RuntimeIsReportedPathByPath()
    {
        using var tmp = new TempDir();
        var g = new FakeInstall(tmp);
        Dictionary<string, string> States() => RuntimeModding.Describe(g.Install).Paths.ToDictionary(p => p.Label, p => p.State);

        var none = RuntimeModding.Describe(g.Install);
        Assert.Equal("none", none.Layout);
        Assert.Equal("absent", States()["dll"]);
        Assert.Equal("absent", States()["folder"]);
        Assert.Equal(["dll", "folder", "core", "config", "api", "module", "deps", "mods", "settings", "log", "bootlog"],
                     none.Paths.Select(p => p.Label));

        FakeInstall.Put(Path.Combine(g.Bin, "dxgi.dll"), [.. "MZ\0\0"u8, .. Encoding.Unicode.GetBytes(@"Nightrunner\Core.dll")]);
        var half = RuntimeModding.Describe(g.Install);
        Assert.Equal("proxy", half.Layout);
        Assert.Equal(("ok", "missing", "missing", "missing", "missing", "absent"),
                     (States()["dll"], States()["core"], States()["config"], States()["api"], States()["module"], States()["deps"]));
        Assert.True(half.Paths.Single(p => p.Label == "core").Bad);
        g.Proxy();
        g.Mod("m", """{ "id": "m", "items": [] }""");
        var proxy = RuntimeModding.Describe(g.Install);
        Assert.True(proxy.Check.Found);
        Assert.Equal(("ok", "ok", "ok", "absent"), (States()["core"], States()["module"], States()["mods"], States()["settings"]));
        Assert.Equal(Path.Combine(g.Bin, "Nightrunner", "mods"), proxy.Paths.Single(p => p.Label == "mods").Path);
        Assert.Equal(Path.Combine(g.Bin, "Nightrunner", "logs", "nightrunner.log"), proxy.Paths.Single(p => p.Label == "log").Path);
    }

    [Fact]
    public async Task OverridesAreTheStockNamesAModCollidesWith()
    {
        using var tmp = new TempDir();
        static ResourceSpec Res(string name, byte type) => new(Encoding.UTF8.GetBytes(name), type, 0, [new PartSpec(type, [1, 2, 3, 4], 4, 0, 0)]);
        string stockPath = tmp.File("stock_pc.rpack"), modPath = tmp.File("mod_pc.rpack");
        RpackWriter.Write(stockPath, [Res("Tex_A.png", 0x20), Res("mesh_m", 0x10), Res("other", 0x20)]);
        RpackWriter.Write(modPath, [Res("tex_a.png", 0x20), Res("mesh_m", 0x20), Res("new_one", 0x10)]);
        using var catalog = new RpackCatalog();
        await catalog.LoadAsync([stockPath], tmp.Path, TestContext.Current.CancellationToken);

        void Zip(string path, params string[] members)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var m in members) using (var s = zip.CreateEntry(m).Open()) s.Write("{}"u8);
        }
        string data0 = tmp.File("data0.pak"), modPak = tmp.File("mod.pak");
        Zip(data0, "models/ft/player/x.model", "scripts/s.scr", "scripts/t.scr");
        Zip(modPak, "x.model", "scripts/s.scr", "other/t.scr", "gui/menu.gui");
        var stockPaks = RuntimeOverrides.StockMembers.Read([data0]);

        RuntimeItem Item(RuntimeKind kind, string path) =>
            new(kind, Path.GetFileName(path), path, true, 0, RuntimePhase.AfterBuiltins, null, "mod");
        var problems = new List<string>();
        var list = RuntimeOverrides.Of([Item(RuntimeKind.Rpack, modPath), Item(RuntimeKind.Pak, modPak),
                                        Item(RuntimeKind.Rpack, tmp.File("missing_pc.rpack"))], catalog.IndexedPacks, stockPaks, problems);
        Assert.Equal([
            (RuntimeKind.Rpack, "tex_a.png", (byte?)0x20, "stock_pc.rpack"),   // engine folding: case does not matter
            (RuntimeKind.Pak, "x.model", null, "data0.pak"),                    // .model by basename
            (RuntimeKind.Pak, "scripts/s.scr", null, "data0.pak"),              // .scr by path
        ], list.Select(o => (o.Kind, o.Name, o.Type, o.Stock)));
        Assert.Empty(problems);
    }

    // ---- a build as a NightrunnerProxy mod folder ------------------------------------------------------------------

    [Fact]
    public void BuildWritesAModFolderTheRuntimeLoads()
    {
        using var tmp = new TempDir();
        var catalog = ProjectBuildItemTests.Catalog(tmp, ("anims_pc.rpack", [ProjectBuildItemTests.Plain("clip_a", ProjectBuildItemTests.BoneClip())]));
        var project = ModProject.Create(tmp.File("mod"), "My Mod!", "dltb");
        ProjectBuildItemTests.TextureItem(project, "tex_dif");
        ProjectBuildItemTests.AnimItem(project, "clip_a", "anims_pc.rpack", 0, ProjectBuildItemTests.BoneClip());
        string id = ProjectBuild.ModId(project.Name);
        Assert.Equal("my-mod", id);
        var env = ProjectBuildItemTests.Env(catalog) with { LoadPng = ProjectBuildItemTests.Pixels, ModId = id };
        var result = ProjectBuild.Build(project, env, ct: TestContext.Current.CancellationToken);
        Assert.True(result.Verified, result.Verdict);

        // <build>\<id>\: mod.json and the files in the sample modpack's layout; the build's own outputs stay beside it
        string folder = Path.Combine(project.BuildFolder, id);
        Assert.Equal(folder, result.ModFolder);
        Assert.True(File.Exists(result.RpackPath) && File.Exists(result.AnimsRpackPath));
        Assert.Equal(["mod.json", "packs/mod_anims_pc.rpack", "packs/mod_pc.rpack"],
                     Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Order());
        Assert.Equal(File.ReadAllBytes(result.RpackPath!), File.ReadAllBytes(Path.Combine(folder, "packs", "mod_pc.rpack")));
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "mod.json")))!;
        Assert.Equal(id, (string?)manifest["id"]);
        Assert.Equal("My Mod!", (string?)manifest["name"]);
        Assert.Equal([("rpack", "packs/mod_anims_pc.rpack", "before:common_anims_pc", 0), ("rpack", "packs/mod_pc.rpack", "after-builtins", 1)],
                     manifest["items"]!.AsArray().Select(i => ((string)i!["kind"]!, (string)i["file"]!, (string)i["at"]!, (int)i["order"]!)));
        var report = JsonNode.Parse(File.ReadAllText(Path.Combine(project.BuildFolder, "My Mod!.build.json")))!;
        Assert.Equal(id, (string?)report["mod"]!["id"]);
        Assert.Equal("ph_ft/work/bin/x64/Nightrunner/mods/my-mod", (string?)report["mod"]!["install"]);
        Assert.False((bool)report["mod"]!["animsAt"]!["verified"]!);   // the clip override through before: is not verified in game
        Assert.Contains(result.Warnings, w => w.Contains("unverified in game"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("clips override only"));

        // the folder copied into a runtime's mods folder reads back as one valid mod: clips before common_anims_pc, the rest after the built-ins
        var g = new FakeInstall(tmp);
        foreach (var f in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            FakeInstall.Put(Path.Combine(g.Mods, id, Path.GetRelativePath(folder, f)), File.ReadAllBytes(f));
        var rc = RuntimeContent.Read(g.Install);
        var mod = Assert.Single(rc.Mods);
        Assert.True(mod.Valid && mod.Loads, string.Join("; ", rc.Problems));
        Assert.Empty(rc.Problems);
        Assert.Equal([(RuntimePhase.BeforePack, "common_anims_pc", true), (RuntimePhase.AfterBuiltins, null, true)],
                     mod.Items.Select(i => (i.Phase, i.BeforePack, i.Loads)));
        string M(string f) => $"ph_ft/work/bin/x64/Nightrunner/mods/my-mod/packs/{f}";
        string S(string f) => $"ph_ft/work/data_platform/pc/assets/{f}";
        Assert.Equal([S("engine_pc.rpack"), S("lang_speech_en_pc.rpack"), M("mod_pc.rpack"), M("mod_anims_pc.rpack"),
                      S("common_anims_pc.rpack"), S("common_meshes_pc.rpack"), S("dlc/level_pc.rpack")],
                     g.Install.Rpacks(rc).Select(g.Rel));

        // what the Build tab lists for this layout
        var outputs = ProjectBuild.Outputs(ProjectAssets.Scan(project), true, "mod_pc.rpack", "data9.pak", null, id);
        Assert.Equal([("anims", "mod_anims_pc.rpack", "ph_ft/work/bin/x64/Nightrunner/mods/my-mod/packs", 0, "before:common_anims_pc"),
                      ("rpack", "mod_pc.rpack", "ph_ft/work/bin/x64/Nightrunner/mods/my-mod/packs", 1, "after-builtins")],
                     outputs.Select(o => (o.Kind, o.File, o.Destination, o.Order ?? -99, o.At)));
    }

    /// <summary>
    /// What <c>before:common_anims_pc</c> can override. In the measured DLTB boot (NightrunnerProxy LoadPack log, 2026-09-26)
    /// the real packs before it are the boot packs (engine, lang_speech_en; before LoadResources) and gui_common,
    /// common_textures_0, common_meshes, common_prefabs. Those four ship no clip; the boot packs do (engine 2, lang_speech_en
    /// 11,084 measured 2026-09-27), and their names register before any mod pack — so the build names such clips.
    /// </summary>
    [Fact]
    public async Task OnlyBootPacksShipClipsBeforeCommonAnims()
    {
        var install = Installs.Require("dltb");
        string[] before = ["engine_pc", "lang_speech_en_pc", "gui_common_pc", "common_textures_0_pc", "common_meshes_pc", "common_prefabs_pc"];
        var paths = install.Rpacks().Where(p => before.Contains(Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)).ToArray();
        Assert.Equal(before.Length, paths.Length);
        Assert.Contains(install.Rpacks(), p => Path.GetFileName(p).Equals("common_anims_pc.rpack", StringComparison.OrdinalIgnoreCase));
        using var catalog = new RpackCatalog();
        await catalog.LoadAsync(paths, install.Assets, TestContext.Current.CancellationToken);
        foreach (var e in catalog.IndexedPacks)
        {
            int clips = e.TypeCounts.GetValueOrDefault(Nightrunner.Core.Anim.Anm2Resource.TypeAnimation);
            bool boot = RuntimeContent.IsBootPack("dltb", Path.GetFileName(e.Path));
            if (boot) Assert.True(clips > 0, $"{e.Label}: {clips} clips");
            else Assert.True(clips == 0, $"{e.Label}: {clips} clips");
        }
    }

    [Fact]
    public void ModFolderBuildNamesClipsABootPackAlsoShips()
    {
        using var tmp = new TempDir();
        var catalog = ProjectBuildItemTests.Catalog(tmp, ("anims_pc.rpack", [ProjectBuildItemTests.Plain("clip_a", ProjectBuildItemTests.BoneClip())]),
                                                     ("lang_speech_en_pc.rpack", [ProjectBuildItemTests.Plain("clip_a", ProjectBuildItemTests.BoneClip())]));
        var project = ModProject.Create(tmp.File("mod"), "mod", "dltb");
        ProjectBuildItemTests.AnimItem(project, "clip_a", "anims_pc.rpack", 0, ProjectBuildItemTests.BoneClip());
        var result = ProjectBuild.Build(project, ProjectBuildItemTests.Env(catalog) with { ModId = "mod" }, ct: TestContext.Current.CancellationToken);
        Assert.True(result.Verified, result.Verdict);
        Assert.Contains(result.Warnings, w => w.StartsWith("clip_a: also in lang_speech_en_pc.rpack", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData(" x")]
    public void BadModIdsAreRefusedByName(string id)
    {
        Assert.NotNull(ProjectBuild.ModIdRefusal(id));
        using var tmp = new TempDir();
        var catalog = ProjectBuildItemTests.Catalog(tmp, ("anims_pc.rpack", [ProjectBuildItemTests.Plain("clip_a", ProjectBuildItemTests.BoneClip())]));
        var project = ModProject.Create(tmp.File("mod"), "mod", "dltb");
        ProjectBuildItemTests.AnimItem(project, "clip_a", "anims_pc.rpack", 0, ProjectBuildItemTests.BoneClip());
        var e = Assert.Throws<ProjectException>(() => ProjectBuild.Build(project, ProjectBuildItemTests.Env(catalog) with { ModId = id },
                                                                          ct: TestContext.Current.CancellationToken));
        Assert.Contains("is not a mod id", e.Message);
    }
}
