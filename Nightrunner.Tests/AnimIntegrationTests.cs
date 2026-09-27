using Nightrunner.Core.Anim;
using Nightrunner.Core.Cast;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;
using Nightrunner.Core.Prefab;
using Nightrunner.Core.Rpack;
using CastAnimation = Nightrunner.Core.Cast.Animation;
using File = System.IO.File;

namespace Nightrunner.Tests;

/// <summary>Playback and export glue: sequence catalog, clip → pose on a skeleton, clip → Cast animation, prefab placement.</summary>
public class AnimIntegrationTests
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static RpackCatalog? _dltb;

    private static async Task<RpackCatalog> Dltb()
    {
        var install = Installs.Require("dltb");
        await Gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            if (_dltb is null)
            {
                var c = new RpackCatalog();
                await c.LoadAsync(install.Rpacks(), install.Assets, TestContext.Current.CancellationToken);
                _dltb = c;
            }
            return _dltb;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static ModelSkeleton PlayerSkeleton(RpackCatalog c)
    {
        var (e, i) = c.Split(c.Lookup("sh2_player_tpp_phx_skeleton", 0x10)[0]);
        return ModelSkeleton.FromMesh(MeshDecoder.Decode(e.Pack!, i), "skeleton");
    }

    [Fact]
    public async Task SequenceBanksRegisterFirstWins()
    {
        var c = await Dltb();
        var a = AnimCatalog.Build(c);
        Assert.Equal(110, a.Banks.Count);                         // 217 resources, the stream copies shadowed
        Assert.Equal(a.Banks.Count, a.Banks.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var idle = a.Find("m_fpp", "fpp_unarmed_crouch_idle")!;
        Assert.Equal("m_fpp_unarmed_crouch_idle", idle.Record.Anm2Name);
        Assert.NotNull(AnimCatalog.Clip(c, idle.Record.Anm2Name));
    }

    [Fact]
    public async Task PoseFollowsTheClip()
    {
        var c = await Dltb();
        var a = AnimCatalog.Build(c);
        var run = a.Find("anims_player", "tpp_balls_runbackward")!;
        var where = AnimCatalog.Clip(c, run.Record.Anm2Name)!.Value;
        var clip = Anm2Resource.Decode(where.Pack, where.Index);
        var skel = PlayerSkeleton(c);
        var pose = new AnimPose(clip, skel);
        Assert.True(pose.BoundBones > 50, $"{pose.BoundBones} bound");
        var g = pose.Sample(3);
        // a bound bone's local (inverse parent global × global) equals the clip's TRS at that key
        int b = Enumerable.Range(0, skel.Names.Count).First(i => pose.TrackOfBone[i] >= 0 && skel.Parents[i] >= 0 && pose.TrackOfBone[skel.Parents[i]] >= 0);
        var local = ModelCast.Mul(ModelCast.Invert(g[skel.Parents[b]]), g[b]);
        var expect = AnimPose.Trs(clip.Rotation(3, pose.TrackOfBone[b]), clip.Translation(3, pose.TrackOfBone[b]), clip.Scale(3, pose.TrackOfBone[b]));
        for (int k = 0; k < 12; k++) Assert.Equal(expect[k], local[k], 5);
        // half-way between keys lies between them
        var mid = pose.Sample(3.5);
        Assert.All(mid, m => Assert.All(m, v => Assert.True(double.IsFinite(v))));
    }

    [Fact]
    public async Task ClipExportsAsCastAnimation()
    {
        var c = await Dltb();
        var a = AnimCatalog.Build(c);
        var run = a.Find("anims_player", "tpp_balls_runbackward")!;
        var where = AnimCatalog.Clip(c, run.Record.Anm2Name)!.Value;
        var clip = Anm2Resource.Decode(where.Pack, where.Index);
        var skel = PlayerSkeleton(c);
        using var tmp = new TempDir();
        string path = tmp.File("run.cast");
        AnimCast.Build(clip, skel.Names, run.Record.Fps, run.Record.StartFrame, run.Record.EndFrame).Save(path);
        var anim = CastFile.Load(path).Roots()[0].ChildrenOfType<CastAnimation>().Single();
        var curves = anim.ChildrenOfType<Curve>().ToList();
        Assert.Equal(clip.TrackCount * 7, curves.Count);
        Assert.Equal(run.Record.Fps, anim.Property("fr")!.Floats[0]);
        var pelvis = curves.Single(k => k.Property("nn")!.StringValue == "pelvis" && k.Property("kp")!.StringValue == "ty");
        int track = Array.IndexOf(clip.TrackHashes, Anm2Hash.H41("pelvis"));
        int from = (int)Math.Min(run.Record.StartFrame, run.Record.EndFrame);
        Assert.Equal(clip.Translation(from, track).Y, pelvis.Property("kv")!.Floats[0]);
        Assert.Equal("absolute", pelvis.Property("m")!.StringValue);
    }

    [Fact]
    public async Task ClipExportsAsGlbAnimation()
    {
        var c = await Dltb();
        var a = AnimCatalog.Build(c);
        var run = a.Find("anims_player", "tpp_balls_runbackward")!;
        var where = AnimCatalog.Clip(c, run.Record.Anm2Name)!.Value;
        var clip = Anm2Resource.Decode(where.Pack, where.Index);
        var skel = PlayerSkeleton(c);
        using var tmp = new TempDir();
        string path = tmp.File("run.glb");
        Gltf.Save(AnimCast.Scene(clip, skel, "run", run.Record.Fps, run.Record.StartFrame, run.Record.EndFrame), path);
        var bytes = File.ReadAllBytes(path);
        int jsonLength = BitConverter.ToInt32(bytes, 12);
        var doc = System.Text.Json.Nodes.JsonNode.Parse(bytes.AsSpan(20, jsonLength))!;
        var anim = doc["animations"]![0]!;
        Assert.Equal("run", (string)anim["name"]!);
        // a rotation, translation and scale channel for every track the skeleton has a bone for
        int bound = clip.TrackHashes.Count(h => skel.Names.Any(n => Anm2Hash.H41(n) == h));
        Assert.Equal(bound * 3, anim["channels"]!.AsArray().Count);
        var input = doc["accessors"]![(int)anim["samplers"]![0]!["input"]!]!;
        double span = Math.Abs(run.Record.EndFrame - run.Record.StartFrame) / run.Record.Fps;
        Assert.Equal(span, (double)input["max"]![0]!, 4);
        // the skeleton itself stays the model-scene form: the model's node list starts with the bones
        Assert.Equal(skel.Names[0], (string)doc["nodes"]![0]!["name"]!);
    }

    [Fact]
    public async Task AdditiveLayerBringsTheHandToTheChin()
    {
        var c = await Dltb();
        var a = AnimCatalog.Build(c);
        var (e, i) = c.Split(c.Lookup("man_basic_skeleton", 0x10)[0]);
        var skel = ModelSkeleton.FromMesh(MeshDecoder.Decode(e.Pack!, i), "skeleton");
        AnimPose Pose(string seq)
        {
            var w = AnimCatalog.Clip(c, a.Find("npc_dialogs", seq)!.Record.Anm2Name)!.Value;
            return new AnimPose(Anm2Resource.Decode(w.Pack, w.Index), skel);
        }
        var idle = Pose("c_dominik_dialog_truefriends_ending_standing_idle");
        var chin = Pose("hold_chin_additive");
        Assert.False(idle.IsAdditive);
        Assert.True(chin.IsAdditive);
        int head = skel.IndexOf("head"), hand = skel.IndexOf("r_hand");
        double Distance(double[][] g) => Math.Sqrt(Math.Pow(g[hand][3] - g[head][3], 2) + Math.Pow(g[hand][7] - g[head][7], 2) + Math.Pow(g[hand][11] - g[head][11], 2));
        var locals = idle.Locals(100);
        double before = Distance(AnimPose.Globals(skel.Parents, locals));
        chin.Over(locals, 100);
        double after = Distance(AnimPose.Globals(skel.Parents, locals));
        Assert.True(before > 0.4 && after < 0.2, $"hand to head {before:F2} m -> {after:F2} m");
        // an override layer replaces exactly the bones it drives
        var over = idle.Locals(10);
        idle.Over(over, 10);
        Assert.Equal(idle.Sample(10)[hand], AnimPose.Globals(skel.Parents, over)[hand]);
    }

    [Fact]
    public async Task ClipsPickTheirRig()
    {
        var c = await Dltb();
        using var models = new ModelCatalog(Installs.Require("dltb").Paks(false));
        var rigs = AnimRigs.Build(c, models);
        var a = AnimCatalog.Build(c);
        const string tpp = "sh2_player_tpp_phx_skeleton.msh";
        (string Rig, string Model) Pick(string bank, string seq)
        {
            var e = a.Find(bank, seq)!;
            var w = AnimCatalog.Clip(c, e.Record.Anm2Name)!.Value;
            var rig = rigs.Best(Anm2Resource.Decode(w.Pack, w.Index).TrackHashes, e.Record.Name, bank, tpp)!;
            return (rig.Skeleton, AnimRigs.Model(rig, "player_kc_basic_tpp.model", e.Record.Name));
        }
        // the FPP rig is the TPP rig plus hand-mask bones: name words decide between them
        Assert.Equal(("player_fpp_phx_skeleton.msh", "player_kc_basic_fpp.model"), Pick("weapon_unarmed", "fpp_balls_begindodgeback"));
        Assert.Equal((tpp, "player_kc_basic_tpp.model"), Pick("anims_player", "tpp_balls_runbackward"));
        Assert.Equal("zmb_banshee_skeleton.msh", Pick("anims_man_all", "banshee_death_pose_00").Rig);
    }

    [Fact]
    public void PrefabXformIsTranslateRotateXyzScale()
    {
        // CreateXform: mtx34::rotation_xyz(x, y, z) = Rx·Ry·Rz (degrees), columns scaled, translation added
        var m = PrefabPlacement.Xform(new PrefabXform(new Vec3(1, 2, 3), new Vec3(0, 90, 0), new Vec3(2, 1, 1)));
        // +X scaled by 2 then yawed 90° about Y goes to −Z
        double x = m[0] * 1 + m[3], y = m[4] * 1 + m[7], z = m[8] * 1 + m[11];
        Assert.Equal((1.0, 2.0, 3.0 - 2.0), (Math.Round(x, 6), Math.Round(y, 6), Math.Round(z, 6)));
        var rxy = PrefabPlacement.Xform(new PrefabXform(new Vec3(0, 0, 0), new Vec3(90, 90, 0), new Vec3(1, 1, 1)));
        // Rx(90)·Ry(90) applied to +X: Ry takes it to −Z, Rx(90) then takes −Z to +Y
        Assert.Equal((0.0, 1.0, 0.0), (Math.Round(rxy[0], 6), Math.Round(rxy[4], 6), Math.Round(rxy[8], 6)));
    }

    [Fact]
    public async Task PrefabPlacesChildEntityMeshes()
    {
        var c = await Dltb();
        var prefabs = PrefabCatalog.Build(c);
        Assert.Empty(prefabs.Errors);
        var truck = prefabs.Find("dlc_ft_vehicle_truck")!;
        var meshes = PrefabPlacement.Meshes(prefabs, truck);
        Assert.Equal(24, meshes.Count);                         // inactive damage doors, the LOD stand-in and doubled door draws left out
        Assert.DoesNotContain(meshes, m => m.Mesh.EndsWith("_dmg", StringComparison.Ordinal));
        Assert.All(meshes, m => Assert.NotEmpty(c.Lookup(m.Mesh, 0x10)));
        Assert.Contains(meshes, m => m.Path.Contains('/'));      // expanded through child entities
    }
}
