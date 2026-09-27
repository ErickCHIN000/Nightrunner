using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Nightrunner.Core.Anim;
using Nightrunner.Core.Project;
using Nightrunner.Core.Rpack;
using File = System.IO.File;

namespace Nightrunner.Tests;

/// <summary>
/// Full-build rules on synthetic packs and projects (no install): which items the build takes, what it refuses by name,
/// where each output goes.
/// </summary>
public class ProjectBuildItemTests
{
    // ---- synthetic clips and a pack holding them, in the stock storage words (census: every plain clip
    //      lf 0x01 [0x40 a8 f40]; every stream pair lf 0x21 [0x44 a8 f20] [0x45 a8 f29 bit 8]) ------------------------

    internal static readonly string[] Bones = ["pelvis", "spine"];

    internal static Anm2Clip BoneClip(int keys = 6)
    {
        int n = keys * Bones.Length;
        var rot = new Quaternion[n];
        var pos = new Vector3[n];
        var scl = new Vector3[n];
        for (int k = 0; k < keys; k++)
            for (int t = 0; t < Bones.Length; t++)
            {
                rot[k * Bones.Length + t] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.1f * k + t);
                pos[k * Bones.Length + t] = new Vector3(t, 0.01f * k, 0);
                scl[k * Bones.Length + t] = Vector3.One;
            }
        return Anm2Encoder.FromTrs(Bones.Select(Anm2Hash.H41).ToArray(), keys, rot, pos, scl);
    }

    internal static Anm2Clip PoseClip()
    {
        const int T = 3, keys = 8;
        var poses = Enumerable.Range(0, 9 * T - 2).Select(i => (uint)(1000 + i)).ToArray();
        var v = new double[keys * 9 * T];
        for (int k = 0; k < keys; k++)
            for (int i = 0; i < poses.Length; i++) v[k * 9 * T + i] = Math.Clamp(Math.Sin(0.2 * k + i), 0, 1);
        return Anm2Encoder.FromStreams([0x30, 0x31, 0x32], poses, keys - 1, v);
    }

    internal static ResourceSpec Plain(string name, Anm2Clip clip) =>
        new(Encoding.UTF8.GetBytes(name), Anm2Resource.TypeAnimation, 0x01,
            [new PartSpec(Anm2Resource.TypeAnimation, Anm2Encoder.Encode(clip), 8, 0x40, 0)]);

    internal static ResourceSpec Stream(string name, Anm2Clip clip)
    {
        var (h, p) = Anm2Resource.SplitStreamPair(Anm2Encoder.Encode(clip));
        return new(Encoding.UTF8.GetBytes(name), Anm2Resource.TypeAnimation, 0x21,
                   [new PartSpec(Anm2Resource.PartHeader, h, 8, 0x20, 0), new PartSpec(Anm2Resource.PartPayload, p, 8, 0x29, 0, 0x100)]);
    }

    /// <summary>One pack per spec list, under <c>assets/</c>, loaded into a catalog labelled relative to it.</summary>
    internal static RpackCatalog Catalog(TempDir tmp, params (string Pack, ResourceSpec[] Resources)[] packs)
    {
        string assets = tmp.File("assets");
        Directory.CreateDirectory(assets);
        var paths = new List<string>();
        foreach (var (name, resources) in packs)
        {
            string path = Path.Combine(assets, name);
            RpackWriter.Write(path, resources);
            paths.Add(path);
        }
        var catalog = tmp.Track(new RpackCatalog());
        catalog.LoadAsync(paths, assets, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        return catalog;
    }

    /// <summary>An anim item as Add to project writes it: <c>anim/&lt;clip&gt;/</c>, <c>anim.json</c> and the clip as .cast.</summary>
    internal static string AnimItem(ModProject project, string clipName, string pack, int index, Anm2Clip? clip)
    {
        string dir = Path.Combine(project.Folder, ProjectAssets.AnimFolder, clipName);
        Directory.CreateDirectory(dir);
        string cast = Path.Combine(dir, clipName + ".cast");
        if (clip is null) File.WriteAllBytes(cast, []);
        else AnimCast.Build(clip, Bones, 30, 0, clip.FrameBound).Save(cast);
        var settings = new JsonObject
        {
            ["source"] = new JsonObject { ["pack"] = pack, ["index"] = index, ["name"] = clipName }, ["fps"] = 30,
        };
        File.WriteAllText(Path.Combine(dir, ProjectAssets.AnimSettings), settings.ToJsonString());
        return dir;
    }

    internal static BuildEnv Env(RpackCatalog catalog, bool runtime = false, bool plainClips = false) =>
        new(null, catalog, null, _ => throw new InvalidOperationException(), "mod_pc.rpack", "data9.pak", runtime, plainClips);

    [Fact]
    public void AnimOnlyProjectTakesTheFullBuild()
    {
        using var tmp = new TempDir();
        var catalog = Catalog(tmp, ("anims_pc.rpack", [Plain("clip_a", BoneClip())]));
        var project = ModProject.Create(tmp.File("mod"), "mod", "dltb");
        AnimItem(project, "clip_a", "anims_pc.rpack", 0, BoneClip());
        var contents = ProjectAssets.Scan(project);
        Assert.Equal(1, contents.FullBuildItems);   // the Build button's test: an anim-only project is not "nothing to build"

        var result = ProjectBuild.Build(project, Env(catalog), ct: TestContext.Current.CancellationToken);
        Assert.True(result.Verified, result.Verdict);
        Assert.Null(result.RpackPath);
        using var pack = RpackFile.Open(result.AnimsRpackPath!);
        Assert.Equal(["clip_a"], Enumerable.Range(0, pack.Count).Select(pack.Name));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClipsGoToTheirOwnPackLoadedBeforeTheStockAnims(bool runtime)
    {
        using var tmp = new TempDir();
        var catalog = Catalog(tmp, ("anims_pc.rpack", [Plain("clip_plain", BoneClip())]), ("anims_stream_pc.rpack", [Stream("clip_stream", BoneClip(9))]));
        var project = ModProject.Create(tmp.File("mod"), "mod", "dltb");
        TextureItem(project, "tex_dif");
        AnimItem(project, "clip_plain", "anims_pc.rpack", 0, BoneClip());
        AnimItem(project, "clip_stream", "anims_stream_pc.rpack", 0, BoneClip(9));
        var result = ProjectBuild.Build(project, Env(catalog, runtime) with { LoadPng = Pixels }, ct: TestContext.Current.CancellationToken);
        Assert.True(result.Verified, result.Verdict);

        Assert.Equal(Path.Combine(project.BuildFolder, "mod_anims_pc.rpack"), result.AnimsRpackPath);
        using (var main = RpackFile.Open(result.RpackPath!))
            Assert.Equal(["tex_dif.png"], Enumerable.Range(0, main.Count).Select(main.Name));
        using (var anims = RpackFile.Open(result.AnimsRpackPath!))
        {
            Assert.Equal(["clip_plain", "clip_stream"], Enumerable.Range(0, anims.Count).Select(anims.Name).Order());
            Assert.Equal(0u, anims.Header.Field08);   // the stock anim packs' value: the grouped layout takes a stream pair
            int si = Enumerable.Range(0, anims.Count).Single(i => anims.Name(i) == "clip_stream");
            Anm2Resource.Read(anims, si, out bool pair);
            Assert.True(pair);
        }

        // runtime modding on: the mod folder only (the project's id), the clip pack before:common_anims_pc
        var report = JsonNode.Parse(File.ReadAllText(Path.Combine(project.BuildFolder, "mod.build.json")))!;
        Assert.Equal(runtime, Directory.Exists(Path.Combine(project.BuildFolder, "mod")));
        Assert.Equal(runtime ? "mod" : null, (string?)report["mod"]?["id"]);
        string packs = runtime ? "ph_ft/work/bin/x64/Nightrunner/mods/mod/packs" : "ph_ft/work/data_platform/pc/assets";
        Assert.Equal($"{packs}/mod_anims_pc.rpack", (string?)report["install"]!["anims"]);
        Assert.Equal($"{packs}/mod_pc.rpack", (string?)report["install"]!["rpack"]);
        Assert.Empty(Directory.GetFiles(project.BuildFolder, "*.ini"));
        if (!runtime) Assert.Contains(result.Warnings, w => w.StartsWith("mod_anims_pc.rpack: clips override only", StringComparison.Ordinal));
        else Assert.DoesNotContain(result.Warnings, w => w.StartsWith("mod_anims_pc.rpack: clips override only", StringComparison.Ordinal));

        var outputs = ProjectBuild.Outputs(ProjectAssets.Scan(project), true, "mod_pc.rpack", "data9.pak", null, runtime ? "mod" : null);
        Assert.Equal(runtime
                ? [("anims", "mod_anims_pc.rpack", packs, "before:common_anims_pc"), ("rpack", "mod_pc.rpack", packs, "after-builtins")]
                : [("rpack", "mod_pc.rpack", packs, (string?)null), ("anims", "mod_anims_pc.rpack", packs, null)],
                     outputs.Select(o => (o.Kind, o.File, o.Destination, o.At)));
    }

    [Fact]
    public void PlainClipsWritesAStreamClipInPlainFormLosslessly()
    {
        using var tmp = new TempDir();
        var clip = BoneClip(9);
        var catalog = Catalog(tmp, ("anims_pc.rpack", [Plain("clip_b", clip)]), ("anims_stream_pc.rpack", [Stream("clip_b", clip)]));
        var project = ModProject.Create(tmp.File("mod"), "mod", "dltb");
        AnimItem(project, "clip_b", "anims_stream_pc.rpack", 0, clip);

        byte[] Built(bool plain, out bool pair, out byte flags, out byte storage)
        {
            var r = ProjectBuild.Build(project, Env(catalog, plainClips: plain), ct: TestContext.Current.CancellationToken);
            Assert.True(r.Verified, r.Verdict);
            using var pack = RpackFile.Open(r.AnimsRpackPath!);
            flags = pack.Logicals[0].Flags;
            storage = pack.PartStorage((int)pack.Logicals[0].FirstPart).Flags;
            return Anm2Resource.Read(pack, 0, out pair);
        }
        var asStream = Built(false, out bool p0, out byte lf0, out _);
        var asPlain = Built(true, out bool p1, out byte lf1, out byte sf1);
        Assert.True(p0);
        Assert.Equal(0x21, lf0);
        Assert.False(p1);
        Assert.Equal(0x01, lf1);                     // the plain copy's words, not the stream pair's
        Assert.Equal(0x40, sf1);
        Assert.Equal(asStream, asPlain);             // plain = 0x44 ‖ 0x45: the same clip bytes
        var report = JsonNode.Parse(File.ReadAllText(Path.Combine(project.BuildFolder, "mod.build.json")))!;
        Assert.Equal("plain (0x40, forced)", (string?)report["items"]![0]!["form"]);

        // no plain copy in the open game: refused by name, never invented
        using var tmp2 = new TempDir();
        var streamOnly = Catalog(tmp2, ("anims_stream_pc.rpack", [Stream("clip_b", clip)]));
        var e = Assert.Throws<ProjectException>(() => ProjectBuild.Build(project, Env(streamOnly, plainClips: true), ct: TestContext.Current.CancellationToken));
        Assert.Equal("anim/clip_b: no plain (0x40) copy of clip_b to take the plain form's storage words from", e.Message);
    }

    [Theory]
    [InlineData("assets_2_pc.rpack", "assets_2_anims_pc.rpack")]
    [InlineData("mymod.rpack", "mymod_anims_pc.rpack")]
    [InlineData("mymod", "mymod_anims_pc.rpack")]
    public void AnimsPackName(string rpack, string anims) => Assert.Equal(anims, ProjectBuild.AnimsPackName(rpack));

    /// <summary>A texture item (4x4 RGBA8); the test loader supplies its pixels.</summary>
    internal static string TextureItem(ModProject project, string name, uint headerVersion = Nightrunner.Core.Texture.ImgcHeader.KnownVersion)
    {
        var dir = Path.Combine(project.Folder, TextureAsset.Folder);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), [0]);
        string sidecar = Path.Combine(dir, name + ".png" + TextureAsset.Extension);
        new TextureAsset { Resource = name + ".png", Format = 38, FormatName = "RGBA8", Width = 4, Height = 4, Mips = 1, HeaderVersion = headerVersion }
            .Save(sidecar);
        return sidecar;
    }

    internal static (byte[], int, int) Pixels(string _) => (Enumerable.Repeat((byte)0x80, 4 * 4 * 4).ToArray(), 4, 4);

    [Fact]
    public void FailedVerifyKeepsOutputsAsInvalid()
    {
        using var tmp = new TempDir();
        using var catalog = new RpackCatalog();
        var project = ModProject.Create(tmp.File("mod"), "mod", "dltb");
        var env = Env(catalog) with { LoadPng = Pixels };
        TextureItem(project, "tex_dif");
        var good = ProjectBuild.Build(project, env, ct: TestContext.Current.CancellationToken);
        Assert.True(good.Verified, good.Verdict);
        Assert.Equal(Path.Combine(project.BuildFolder, "mod_pc.rpack"), good.RpackPath);
        var goodBytes = File.ReadAllBytes(good.RpackPath!);

        // an IMGC header version the reader refuses: the pack writes, the verify fails
        TextureItem(project, "tex_dif", headerVersion: 0x12345678);
        var bad = ProjectBuild.Build(project, env, ct: TestContext.Current.CancellationToken);
        Assert.False(bad.Verified);
        Assert.Equal(good.RpackPath + ProjectBuild.Invalid, bad.RpackPath);
        Assert.True(File.Exists(bad.RpackPath));
        Assert.EndsWith("kept as mod_pc.rpack.invalid", bad.Verdict);
        Assert.Equal(goodBytes, File.ReadAllBytes(good.RpackPath!));                     // the earlier output is untouched
        Assert.Empty(Directory.GetFiles(project.BuildFolder, "*" + ProjectBuild.Unverified));
        var report = JsonNode.Parse(File.ReadAllText(Path.Combine(project.BuildFolder, "mod.build.json")))!;
        Assert.False((bool)report["verified"]!);
        Assert.Equal("mod_pc.rpack.invalid", (string?)report["outputs"]!["rpack"]!["path"]);
        Assert.Null(report["install"]!["rpack"]);

        TextureItem(project, "tex_dif");                                                  // fixed: the stale .invalid goes
        Assert.True(ProjectBuild.Build(project, env, ct: TestContext.Current.CancellationToken).Verified);
        Assert.False(File.Exists(bad.RpackPath));
    }

    private static void Touch(string path, int minutes, byte[]? append = null)
    {
        if (append is not null) File.AppendAllText(path, Encoding.ASCII.GetString(append));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(minutes));
    }

    [Fact]
    public void AnimItemBuildsTheFileThatWasEdited()
    {
        using var tmp = new TempDir();
        var project = ModProject.Create(tmp.File("mod"), "mod", "dltb");
        string dir = AnimItem(project, "clip", "anims_pc.rpack", 0, BoneClip());
        string cast = Path.Combine(dir, "clip.cast"), glb = Path.Combine(dir, "clip.glb");
        File.WriteAllBytes(glb, [1, 2, 3]);
        var at = DateTime.UtcNow.AddMinutes(-10);
        File.SetLastWriteTimeUtc(cast, at);
        File.SetLastWriteTimeUtc(glb, at.AddMilliseconds(40));   // written together by Add to project
        AnimItem Scan() => Assert.Single(ProjectAssets.Scan(project).Anims);

        // an item from before the export record: nothing edited, so the lossless .cast, not the newer .glb
        Assert.Equal(cast, Scan().ScenePath);

        // with the record: the one file that changed since the export
        var settingsPath = Path.Combine(dir, ProjectAssets.AnimSettings);
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        settings[ProjectAssets.ExportedKey] = ProjectAssets.Exported(dir);
        File.WriteAllText(settingsPath, settings.ToJsonString());
        Assert.Equal(cast, Scan().ScenePath);
        Touch(glb, 1, [4]);
        Assert.Equal(glb, Scan().ScenePath);

        // two edited files: refused by name until anim.json says which
        Touch(cast, 2);
        var contents = ProjectAssets.Scan(project);
        Assert.Empty(contents.Anims);
        Assert.Equal("anim/clip: clip.cast, clip.glb all changed since the export: name the one to build as \"scene\" in anim.json",
                     Assert.Single(contents.Problems));
        settings[ProjectAssets.SceneKey] = "clip.cast";
        File.WriteAllText(settingsPath, settings.ToJsonString());
        Assert.Equal(cast, Scan().ScenePath);
        settings[ProjectAssets.SceneKey] = "gone.glb";
        File.WriteAllText(settingsPath, settings.ToJsonString());
        Assert.Equal("anim/clip: anim.json names scene 'gone.glb', which is not a .cast/.glb/.gltf in the folder",
                     Assert.Single(ProjectAssets.Scan(project).Problems));
    }

    [Fact]
    public void SceneItemBuildsAnEditedCastEvenBesideItsGlb()
    {
        using var tmp = new TempDir();
        var project = ModProject.Create(tmp.File("mod"), "mod", "dltb");
        string dir = Path.Combine(project.Folder, ProjectAssets.SceneFolder, "player");
        Directory.CreateDirectory(dir);
        string cast = Path.Combine(dir, "player.cast"), glb = Path.Combine(dir, "player.glb");
        File.WriteAllText(Path.Combine(dir, "player.cast.json"), "{}");
        File.WriteAllBytes(cast, [1]);
        File.WriteAllBytes(glb, [2]);
        File.WriteAllText(Path.Combine(dir, ProjectAssets.SplitSettings),
                          new JsonObject { [ProjectAssets.ExportedKey] = ProjectAssets.Exported(dir) }.ToJsonString());
        Assert.Equal(cast, Assert.Single(ProjectAssets.Scan(project).Scenes).ScenePath);   // unedited: the export
        Touch(cast, 1, [3]);                                                              // edited .cast, untouched .glb
        Assert.Equal(cast, Assert.Single(ProjectAssets.Scan(project).Scenes).ScenePath);
        string blender = Path.Combine(dir, "player_edit.glb");
        File.WriteAllBytes(blender, [4]);                                                  // a new file is a change too
        Assert.Contains("player.cast, player_edit.glb all changed", Assert.Single(ProjectAssets.Scan(project).Problems));
    }

    [Fact]
    public void Dl2AnimIsRefusedByName()
    {
        using var tmp = new TempDir();
        var project = ModProject.Create(tmp.File("dl2mod"), "dl2mod", "dl2");
        AnimItem(project, "clip", "anims_pc.rpack", 0, null);
        using var catalog = new RpackCatalog();
        var e = Assert.Throws<ProjectException>(() => ProjectBuild.Build(project, Env(catalog), ct: TestContext.Current.CancellationToken));
        Assert.Equal("DL2 animation building is not supported (DLTB only)", e.Message);
    }

    [Fact]
    public void FacialClipTemplateIsRefusedByName()
    {
        using var tmp = new TempDir();
        var catalog = Catalog(tmp, ("anims_pc.rpack", [Plain("face_clip", PoseClip())]));
        var project = ModProject.Create(tmp.File("mod"), "mod", "dltb");
        AnimItem(project, "face_clip", "anims_pc.rpack", 0, BoneClip());
        var e = Assert.Throws<ProjectException>(() => ProjectBuild.Build(project, Env(catalog), ct: TestContext.Current.CancellationToken));
        Assert.Equal("anim/face_clip: anims_pc.rpack:0 is a facial pose-weight clip, not bone animation", e.Message);
        Assert.False(Directory.Exists(project.BuildFolder) && Directory.GetFiles(project.BuildFolder, "*.rpack").Length > 0);
    }
}
