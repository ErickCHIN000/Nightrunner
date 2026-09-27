using System.Text.Json.Nodes;
using Nightrunner.Core.Project;
using Nightrunner.Core.Rpack;
using static Nightrunner.Tests.ProjectBuildItemTests;
using File = System.IO.File;

namespace Nightrunner.Tests;

/// <summary>
/// The Build tab's model on synthetic packs and projects (no install): one overview row per item with its output, form
/// and template; per-resource hashes and the diff against the last build; Check writing nothing; report attribution.
/// </summary>
public class BuildOverviewTests
{
    private static (TempDir Tmp, RpackCatalog Catalog, ModProject Project) Setup()
    {
        var tmp = new TempDir();
        var catalog = Catalog(tmp, ("anims_pc.rpack", [Plain("clip_plain", BoneClip())]), ("anims_stream_pc.rpack", [Stream("clip_stream", BoneClip(9))]));
        var project = ModProject.Create(tmp.File("mod"), "mod", "dltb");
        TextureItem(project, "tex_dif");
        AnimItem(project, "clip_plain", "anims_pc.rpack", 0, BoneClip());
        AnimItem(project, "clip_stream", "anims_stream_pc.rpack", 0, BoneClip(9));
        return (tmp, catalog, project);
    }

    private static (byte[], int, int) Grey(byte v) => (Enumerable.Repeat(v, 4 * 4 * 4).ToArray(), 4, 4);

    [Fact]
    public void OverviewListsEveryItemWithItsOutputFormAndTemplate()
    {
        var (tmp, catalog, project) = Setup();
        using var cleanup = tmp;
        // a mesh whose template is not in the open game: refused by name before any build
        string mesh = Path.Combine(project.Folder, ProjectAssets.MeshFolder, "body");
        Directory.CreateDirectory(mesh);
        File.WriteAllBytes(Path.Combine(mesh, "body.cast"), []);
        File.WriteAllText(Path.Combine(mesh, "body.mesh.json"), """{"source":{"pack":"meshes_pc.rpack","index":3,"name":"body_src"}}""");

        var items = ProjectBuild.Overview(project, catalog, null, plainClips: false);
        Assert.Equal(["texture", "mesh", "anim", "anim"], items.Select(i => i.Kind));
        var tex = items[0];
        Assert.Equal(("tex_dif.png", "0x20 RGBA8 4x4", "rpack"), (tex.Name, tex.Form, tex.Pack));
        Assert.Equal(["rpack/tex_dif.png"], tex.Outputs);
        var body = items[1];
        Assert.Equal(("meshes_pc.rpack:3", "clone of body_src"), (body.Template, body.Edits));
        Assert.Equal("mesh/body: pack meshes_pc.rpack is not in the open game", body.Refusal);
        var plain = items.Single(i => i.Name == "clip_plain");
        Assert.Equal(("plain 0x40 30 fps", "anims", "anims_pc.rpack:0", false), (plain.Form, plain.Pack, plain.Template, plain.Changed));
        Assert.Null(plain.Refusal);
        Assert.Equal("stream 0x44+0x45 30 fps", items.Single(i => i.Name == "clip_stream").Form);
        Assert.Equal("plain 0x40 (forced) 30 fps", ProjectBuild.Overview(project, catalog, null, plainClips: true).Single(i => i.Name == "clip_stream").Form);
    }

    [Fact]
    public void BuildRecordsHashesAndTheDiffAgainstTheLastBuild()
    {
        var (tmp, catalog, project) = Setup();
        using var cleanup = tmp;
        var env = Env(catalog) with { LoadPng = _ => Grey(0x80) };
        JsonObject Diff() => ProjectBuild.LastReport(project)!["diff"]!.AsObject();

        Assert.True(ProjectBuild.Build(project, env, ct: TestContext.Current.CancellationToken).Verified);
        var hashes = ProjectBuild.LastReport(project)!["hashes"]!;
        Assert.Equal(["tex_dif.png"], hashes["rpack"]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(["clip_plain", "clip_stream"], hashes["anims"]!.AsObject().Select(kv => kv.Key).Order());
        Assert.All(Diff(), kv => Assert.Equal("added", (string?)kv.Value));

        Assert.True(ProjectBuild.Build(project, env, ct: TestContext.Current.CancellationToken).Verified);
        Assert.All(Diff(), kv => Assert.Equal("unchanged", (string?)kv.Value));

        Assert.True(ProjectBuild.Build(project, env with { LoadPng = _ => Grey(0x20) }, ct: TestContext.Current.CancellationToken).Verified);
        Assert.Equal("changed", (string?)Diff()["rpack/tex_dif.png"]);
        Assert.Equal("unchanged", (string?)Diff()["anims/clip_plain"]);

        // a clip taken out of the project is removed from the build
        Directory.Delete(Path.Combine(project.Folder, ProjectAssets.AnimFolder, "clip_plain"), true);
        Assert.True(ProjectBuild.Build(project, env, ct: TestContext.Current.CancellationToken).Verified);
        Assert.Equal("removed", (string?)Diff()["anims/clip_plain"]);

        // a report from before hashes: every resource "unknown"
        string json = Path.Combine(project.BuildFolder, "mod.build.json");
        var old = JsonNode.Parse(File.ReadAllText(json))!.AsObject();
        old.Remove("hashes");
        old.Remove("diff");
        File.WriteAllText(json, old.ToJsonString());
        Assert.True(ProjectBuild.Build(project, env, ct: TestContext.Current.CancellationToken).Verified);
        Assert.All(Diff(), kv => Assert.Equal("unknown", (string?)kv.Value));
    }

    [Fact]
    public void CheckRunsTheBuildAndKeepsNothing()
    {
        var (tmp, catalog, project) = Setup();
        using var cleanup = tmp;
        var env = Env(catalog, runtime: true) with { LoadPng = _ => Grey(0x80) };
        string manifest = File.ReadAllText(project.ManifestPath);
        int temps = Directory.GetDirectories(Path.GetTempPath(), "nightrunner-check-*").Length;

        var first = ProjectBuild.Check(project, env, ct: TestContext.Current.CancellationToken);
        Assert.True(first.Ok, first.Verdict);
        Assert.Null(first.Refused);
        Assert.False(Directory.Exists(project.BuildFolder));                  // nothing written where Build writes
        Assert.Equal(manifest, File.ReadAllText(project.ManifestPath));       // the manifest's build folder is not the temp one
        Assert.Equal(temps, Directory.GetDirectories(Path.GetTempPath(), "nightrunner-check-*").Length);
        Assert.True((bool)first.Report["check"]!);
        Assert.Null(first.Report["install"]);
        Assert.All(first.Report["diff"]!.AsObject(), kv => Assert.Equal("added", (string?)kv.Value));   // no build yet

        Assert.True(ProjectBuild.Build(project, env, ct: TestContext.Current.CancellationToken).Verified);
        var written = Directory.GetFiles(project.BuildFolder).ToDictionary(f => f, File.GetLastWriteTimeUtc);
        var again = ProjectBuild.Check(project, env with { LoadPng = _ => Grey(0x20) }, ct: TestContext.Current.CancellationToken);
        Assert.Equal(written, Directory.GetFiles(project.BuildFolder).ToDictionary(f => f, File.GetLastWriteTimeUtc));
        Assert.Equal("changed", (string?)again.Report["diff"]!["rpack/tex_dif.png"]);
        Assert.Equal("unchanged", (string?)again.Report["diff"]!["anims/clip_stream"]);

        // per item: the check's result, and the anims pack's load-order note claimed by no item
        var items = ProjectBuild.Overview(project, catalog, null, false);
        var view = ProjectBuild.Attribute(items, again.Report);
        Assert.True(view.Check);
        var tex = view.Items[items.FindIndex(i => i.Kind == "texture")];
        Assert.StartsWith("ok 4x4 ", tex.Result, StringComparison.Ordinal);
        Assert.DoesNotContain("bytes", tex.Result, StringComparison.Ordinal);
        Assert.Equal("changed", tex.Diff);
        var clip = view.Items[items.FindIndex(i => i.Name == "clip_stream")];
        Assert.StartsWith("ok stream (0x44+0x45), 2 tracks", clip.Result, StringComparison.Ordinal);
        Assert.Equal("unchanged", clip.Diff);
    }

    [Fact]
    public void CheckNamesTheItemTheBuildRefuses()
    {
        var (tmp, catalog, project) = Setup();
        using var cleanup = tmp;
        AnimItem(project, "clip_gone", "gone_pc.rpack", 0, BoneClip());
        var r = ProjectBuild.Check(project, Env(catalog) with { LoadPng = _ => Grey(0x80) }, ct: TestContext.Current.CancellationToken);
        Assert.False(r.Ok);
        Assert.Equal("anim/clip_gone: pack gone_pc.rpack is not in the open game", r.Refused);

        var items = ProjectBuild.Overview(project, catalog, null, false);
        Assert.Equal(r.Refused, items.Single(i => i.Name == "clip_gone").Refusal);        // seen before the check, too
        var view = ProjectBuild.Attribute(items, r.Report);
        int gone = items.FindIndex(i => i.Name == "clip_gone");
        Assert.Equal("refused", view.Items[gone].Result);
        Assert.Equal([r.Refused!], view.Items[gone].Warnings);
        Assert.All(view.Items.Where((_, i) => i != gone), s => Assert.Equal("—", s.Result));
        Assert.Empty(view.Other);
    }

    [Fact]
    public void LastBuildWarningsGoToTheirItems()
    {
        var (tmp, catalog, project) = Setup();
        using var cleanup = tmp;
        TextureItem(project, "tex_bad", headerVersion: 0x12345678);
        File.WriteAllBytes(Path.Combine(project.Folder, TextureAsset.Folder, "orphan.png"), [0]);
        var r = ProjectBuild.Build(project, Env(catalog) with { LoadPng = _ => Grey(0x80) }, ct: TestContext.Current.CancellationToken);
        var items = ProjectBuild.Overview(project, catalog, null, false);
        Assert.Contains(items, i => i.Name == "orphan.png" && i.Refusal is not null);
        var view = ProjectBuild.Attribute(items, ProjectBuild.LastReport(project)!);
        Assert.Equal(r.Verified, view.Verified);
        // the anims pack's note (clips override only with runtime modding) belongs to no item
        Assert.Contains(view.Other, w => w.StartsWith("mod_anims_pc.rpack: clips override only", StringComparison.Ordinal));
        Assert.All(view.Other, w => Assert.DoesNotContain(items, i => w.StartsWith(i.Name + ":", StringComparison.Ordinal)));
    }

    /// <summary>The numbers the Projects window's kind tabs show, read with each item by the same overview the Build tab uses.</summary>
    [Fact]
    public void OverviewCountsWhatEachKindCarries()
    {
        var (tmp, catalog, project) = Setup();
        using var cleanup = tmp;
        string mesh = Path.Combine(project.Folder, ProjectAssets.MeshFolder, "body");
        Directory.CreateDirectory(mesh);
        File.WriteAllBytes(Path.Combine(mesh, "body.cast"), []);
        File.WriteAllText(Path.Combine(mesh, "body.mesh.json"), """
            {"source":{"pack":"meshes_pc.rpack","index":3,"name":"body"},
             "geometry_entries":[{"vertex_count":100,"submeshes":[{},{}]},{"vertex_count":20,"submeshes":[{}]}]}
            """);
        string scene = Path.Combine(project.Folder, ProjectAssets.SceneFolder, "hero");
        Directory.CreateDirectory(scene);
        File.WriteAllBytes(Path.Combine(scene, "hero.cast"), []);
        File.WriteAllText(Path.Combine(scene, "hero.cast.json"), """
            {"model":"models/hero.model","parts":[{"pack":"meshes_pc.rpack","index":3},{"pack":"meshes_pc.rpack","index":4}],
             "mesh_map":[{"name":"A.x.e0.s0"},{"name":"A.x.e0.s1"},{"name":"B.y.e0.s0"}]}
            """);
        File.WriteAllText(Path.Combine(scene, ProjectAssets.SplitSettings), """
            {"assign":{"mine":"A.x.e0.s0","A.x.e0.s1":"","other":"B.y.e0.s0"},"hide":["B.y.e0.s0"],"renames":{"a":"b"}}
            """);
        string model = Path.Combine(project.Folder, ProjectAssets.ModelFolder, "hero");
        Directory.CreateDirectory(model);
        File.WriteAllText(Path.Combine(model, "hero.model"), "{}");
        File.WriteAllText(Path.Combine(model, ProjectAssets.ModelSettings), """{"member":"models/hero.model","noGear":true,"stash":{"HEAD":[]}}""");
        string prefab = Path.Combine(project.Folder, ProjectAssets.PrefabFolder, "reg_pc");
        Directory.CreateDirectory(prefab);
        File.WriteAllText(Path.Combine(prefab, ProjectAssets.PrefabSettings), """
            {"source":{"pack":"reg_pc.rpack","index":1,"name":"Prefabs"},
             "edits":[{"op":"active","prefab":"a","pcid":2,"active":false},{"op":"rename","prefab":"a","text":"b"}]}
            """);

        var items = ProjectBuild.Overview(project, catalog, null, plainClips: true);
        IReadOnlyDictionary<string, int> Of(string kind, string? name = null) => items.Single(i => i.Kind == kind && (name is null || i.Name == name)).Counts;
        Assert.Equal(new Dictionary<string, int> { ["vertices"] = 120, ["submeshes"] = 3 }, Of("mesh"));
        Assert.Equal(new Dictionary<string, int> { ["parts"] = 2, ["exported"] = 3, ["assigned"] = 2, ["skipped"] = 1, ["hidden"] = 1, ["renamed"] = 1 },
                     Of("scene"));
        Assert.Equal(new Dictionary<string, int> { ["stashed"] = 1 }, Of("model"));      // no game PAKs: the stash only
        Assert.Equal(new Dictionary<string, int> { ["edits"] = 2 }, Of("prefab"));
        Assert.Equal(new Dictionary<string, int> { ["stream"] = 0, ["forced"] = 0 }, Of("anim", "clip_plain"));
        Assert.Equal(new Dictionary<string, int> { ["stream"] = 1, ["forced"] = 1 }, Of("anim", "clip_stream"));
        Assert.Equal(new Dictionary<string, int> { ["stream"] = 1, ["forced"] = 0 },
                     ProjectBuild.Overview(project, catalog, null, plainClips: false).Single(i => i.Name == "clip_stream").Counts);
        // without the open game a clip's form is not known
        Assert.Empty(ProjectBuild.Overview(project, null, null, plainClips: true).Single(i => i.Name == "clip_stream").Counts);
        // a folder the scan cannot read counts nothing
        Directory.CreateDirectory(Path.Combine(project.Folder, ProjectAssets.MeshFolder, "broken"));
        var broken = ProjectBuild.Overview(project, catalog, null, false).Single(i => i.Kind == "mesh" && i.Name == "broken");
        Assert.Empty(broken.Counts);
        Assert.NotNull(broken.Refusal);
    }
}
