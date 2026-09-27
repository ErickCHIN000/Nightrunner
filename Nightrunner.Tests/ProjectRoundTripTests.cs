using Nightrunner.Core.Anim;
using Nightrunner.Core.Cast;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;
using Nightrunner.Core.Project;
using Nightrunner.Core.Rpack;
using CastMesh = Nightrunner.Core.Cast.Mesh;
using CastModel = Nightrunner.Core.Cast.Model;
using File = System.IO.File;

namespace Nightrunner.Tests;

/// <summary>
/// The whole mod loop on DLTB: a mesh added to a project, its Cast edited, cloned under a new name; a mesh with skins
/// edited in place; the player model pointed at the clone; build → the rpack and the PAK are reopened and re-parsed:
/// the edit is there, the clone carries its own embedded name, skins resolve as before, the model draws the clone.
/// </summary>
public class ProjectRoundTripTests
{
    private static void MoveFirstVertex(string castPath, float dy)
    {
        var cast = CastFile.Load(castPath);
        var mesh = cast.Roots()[0].ChildrenOfType<CastModel>().Single().ChildrenOfType<CastMesh>().First();
        var vp = mesh.VertexPositionBuffer()!.ToArray();
        vp[1] += dy;
        mesh.SetVertexPositionBuffer(vp);
        cast.Save(castPath);
    }

    private static string[] SkinTargets(MeshModel m)
    {
        var skins = MeshSkins.Decode(m.SkinRaw!);
        var table = m.FullMaterialTable();
        return skins.Skins.Select(s => s.NameStr + ":" + string.Join(",", skins.MaterialsFor(s.Index, m.Materials.Length).Select(i => table[i]))).ToArray();
    }

    [Fact]
    public async Task ClipEditedInCastBuildsAsStreamPair()
    {
        var install = Installs.Require("dltb");
        using var catalog = new RpackCatalog();
        await catalog.LoadAsync(install.Rpacks(), install.Assets, TestContext.Current.CancellationToken);
        using var tmp = new TempDir();
        var project = ModProject.Create(tmp.File("mod"), "mod", "dltb");
        var (se, si) = catalog.Split(catalog.Lookup("sh2_player_tpp_phx_skeleton", 0x10)[0]);
        var skel = ModelSkeleton.FromMesh(MeshDecoder.Decode(se.Pack!, si), "skeleton");
        // a player clip that ships in both forms, so the stream copy (searched first by the engine) is the template
        var name = AnimCatalog.Build(catalog).Sequences.Where(q => q.Bank == "anims_player" && q.Record.Name.StartsWith("tpp_", StringComparison.Ordinal))
            .Select(q => q.Record.Anm2Name).Distinct()
            .First(n => AnimCatalog.Clip(catalog, n) is { } w && catalog.Lookup(n, Anm2Resource.TypeAnimation).Length > 1 &&
                        Anm2Resource.Decode(w.Pack, w.Index) is { IsPoseWeights: false } c &&
                        c.TrackHashes.Contains(Anm2Hash.H41("pelvis")) && c.TrackHashes.Contains(Anm2Hash.H41("head")));
        var item = ProjectAssets.AddAnim(project, catalog, name, 30, skel);
        Assert.Contains("_stream", item.SourcePack);

        // raise the pelvis 10 cm on every key, in the .cast (the newest scene wins)
        string castPath = Directory.GetFiles(item.Folder, "*.cast").Single();
        var cast = CastFile.Load(castPath);
        var curve = cast.Roots()[0].ChildrenOfType<Nightrunner.Core.Cast.Animation>().Single().ChildrenOfType<Curve>()
            .Single(c => c.NodeName() == "pelvis" && c.KeyPropertyName() == "ty");
        curve.SetFloatKeyValueBuffer(((float[])curve.KeyValueBuffer()!).Select(v => v + 0.1f).ToArray());
        cast.Save(castPath);
        File.SetLastWriteTimeUtc(castPath, DateTime.UtcNow.AddMinutes(1));

        var result = ProjectBuild.Build(project, new BuildEnv(install, catalog, null, _ => throw new InvalidOperationException(),
                                                             "assets_2_pc.rpack", "data9.pak"), ct: TestContext.Current.CancellationToken);
        Assert.True(result.Verified, result.Verdict);
        Assert.Null(result.RpackPath);                                       // clips go to their own pack
        using var pack = RpackFile.Open(result.AnimsRpackPath!);
        int i = Assert.Single(Enumerable.Range(0, pack.Count));
        Assert.Equal(name, pack.Name(i));
        Anm2Resource.Read(pack, i, out bool streamPair);
        Assert.True(streamPair);
        var built = Anm2Resource.Decode(pack, i);
        var where = AnimCatalog.Clip(catalog, name)!.Value;
        var original = Anm2Resource.Decode(where.Pack, where.Index);
        Assert.Equal(original.KeyCount, built.KeyCount);
        int p0 = Array.IndexOf(original.TrackHashes, Anm2Hash.H41("pelvis")), p1 = Array.IndexOf(built.TrackHashes, Anm2Hash.H41("pelvis"));
        int h0 = Array.IndexOf(original.TrackHashes, Anm2Hash.H41("head")), h1 = Array.IndexOf(built.TrackHashes, Anm2Hash.H41("head"));
        for (int k = 0; k < original.KeyCount; k++)
        {
            Assert.Equal(original.Translation(k, p0).Y + 0.1f, built.Translation(k, p1).Y, 3);
            Assert.Equal(original.Translation(k, h0).Y, built.Translation(k, h1).Y, 3);
        }

        // the same build in the plain form: one 0x40 part with the shipped plain copy's words, the same clip bytes
        var streamBytes = Anm2Resource.Read(pack, i);
        var plain = ProjectBuild.Build(project, new BuildEnv(install, catalog, null, _ => throw new InvalidOperationException(),
                                                            "assets_3_pc.rpack", "data9.pak", PlainClips: true), ct: TestContext.Current.CancellationToken);
        Assert.True(plain.Verified, plain.Verdict);
        using var plainPack = RpackFile.Open(plain.AnimsRpackPath!);
        Assert.Equal(streamBytes, Anm2Resource.Read(plainPack, 0, out bool pair));
        Assert.False(pair);
        Assert.Equal(0x01, plainPack.Logicals[0].Flags);
    }

    [Fact]
    public async Task CastToMeshToModelToPackAndPak()
    {
        var install = Installs.Require("dltb");
        using var catalog = new RpackCatalog();
        await catalog.LoadAsync(install.Rpacks(), install.Assets, TestContext.Current.CancellationToken);
        using var models = new ModelCatalog(install.Paks());
        using var tmp = new TempDir();
        var project = ModProject.Create(tmp.File("mod"), "mod", "dltb");

        int head = catalog.Lookup("sh2_npc_crane", 0x10)[0];
        int sedan = catalog.Lookup("veh_sedan_a", 0x10)[0];
        var clone = ProjectAssets.AddMesh(project, catalog, head, "nr_test_head");
        var skinned = ProjectAssets.AddMesh(project, catalog, sedan);
        MoveFirstVertex(clone.ScenePath, 0.01f);
        MoveFirstVertex(skinned.ScenePath, 0.02f);
        var item = ProjectAssets.AddModel(project, models, models.Find("player_tpp_skeleton.model")!);
        var doc = item.Load();
        ModelEdit.SetSlotMesh(doc, "HEAD", "nr_test_head", item.Stash);
        item.Save(doc);

        var result = ProjectBuild.Build(project, new BuildEnv(install, catalog, models, _ => throw new InvalidOperationException(),
                                                             "assets_2_pc.rpack", "data9.pak"), ct: TestContext.Current.CancellationToken);
        Assert.True(result.Verified, result.Verdict);

        using var pack = RpackFile.Open(result.RpackPath!);
        Assert.Equal(2, pack.Count);
        var (headEntry, headIndex) = catalog.Split(head);
        var (sedanEntry, sedanIndex) = catalog.Split(sedan);
        var original = MeshDecoder.Decode(headEntry.Pack!, headIndex);
        var built = MeshDecoder.Decode(pack, Enumerable.Range(0, pack.Count).Single(i => pack.Name(i) == "nr_test_head"));
        Assert.Equal("nr_test_head.msh", MeshModel.Text(built.EmbeddedName));
        var v0 = original.GeometryEntries[0].Vertices!;
        var v1 = built.GeometryEntries[0].Vertices!;
        Assert.Equal(v0.Count, v1.Count);
        int moved = Enumerable.Range(0, v0.Count).Count(i => Math.Abs(v0.Positions[i * 3 + 1] - v1.Positions[i * 3 + 1]) > 1e-6);
        Assert.True(moved >= 1, "the edited vertex moved");

        var sedanBuilt = MeshDecoder.Decode(pack, Enumerable.Range(0, pack.Count).Single(i => pack.Name(i) == "veh_sedan_a"));
        Assert.Equal(SkinTargets(MeshDecoder.Decode(sedanEntry.Pack!, sedanIndex)), SkinTargets(sedanBuilt));   // skins intact

        using var pak = new ModelCatalog([result.PakPath!]);
        var player = pak.Load(pak.Find("player_tpp_skeleton.model")!);
        var slot = player.Slots.Single(s => s.Name == "HEAD");
        Assert.Equal("nr_test_head.msh", Assert.Single(slot.Meshes).Name);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(result.PakPath!))
            Assert.NotNull(zip.GetEntry("player_outfit_slots.scr"));                                  // player models ship no-gear

        // the built pack beside the stock ones: the model now resolves HEAD to the clone in the mod pack
        using var withMod = new RpackCatalog();
        await withMod.LoadAsync([.. install.Rpacks(), result.RpackPath!], install.Assets, TestContext.Current.CancellationToken);
        var res = ModelResolver.Resolve(player, withMod, null, g => { var (e, i) = withMod.Split(g); return MeshDecoder.Decode(e.Pack!, i); });
        var drawn = res.Slots.Single(s => s.Slot.Name == "HEAD").Meshes.Single(m => m.Drawn);
        Assert.True(drawn.Found);
        Assert.Equal(result.RpackPath, withMod.Split(drawn.Gids[0]).Entry.Path);
    }

    [Fact]
    public void Dl2GeometryIsRefusedByName()
    {
        using var tmp = new TempDir();
        var project = ModProject.Create(tmp.File("dl2mod"), "dl2mod", "dl2");
        string dir = Path.Combine(project.Folder, ProjectAssets.MeshFolder, "box");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "box.mesh.json"), """{"source": {"pack": "common_meshes_pc.rpack", "index": 0, "name": "box"}}""");
        File.WriteAllBytes(Path.Combine(dir, "box.cast"), []);
        using var catalog = new RpackCatalog();
        var e = Assert.Throws<ProjectException>(() => ProjectBuild.Build(project, new BuildEnv(null, catalog, null, _ => throw new InvalidOperationException()),
                                                                                         ct: TestContext.Current.CancellationToken));
        Assert.Equal("DL2 mesh and model building is not supported (DLTB only)", e.Message);
    }
}
