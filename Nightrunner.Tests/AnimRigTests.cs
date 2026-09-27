using Nightrunner.Core.Anim;
using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Tests;

/// <summary>Which rig or object a clip plays on (the runtime's pack order for clips, see AnimPackOrderRuntimeTests).</summary>
public class AnimRigTests
{
    private static uint[] H(params string[] names) => names.Select(Anm2Hash.H41).ToArray();

    private static AnimRig Rig(string skeleton, IEnumerable<string> bones, params string[] models) =>
        new(skeleton, H([.. bones]).ToHashSet(), models);

    private static IEnumerable<string> Bones(string prefix, int n) => Enumerable.Range(0, n).Select(i => $"{prefix}{i}");

    [Fact]
    public void MostTracksWinOverNameWords()
    {
        // a clip named after the man rig, binding more tracks on the banshee: the tracks decide (DL2 bsh_standup_*)
        var man = Rig("man_basic_skeleton.msh", Bones("b", 76), "man.model");
        var banshee = Rig("zmb_banshee_skeleton.msh", Bones("b", 93), "banshee.model");
        var rigs = AnimRigs.Of(man, banshee);
        var pick = rigs.Pick(H([.. Bones("b", 97)]), "bsh_standup_back_01", "anims_man_all", null);
        Assert.Same(banshee, pick.Rig);
        Assert.Equal(93, pick.Bound);
        Assert.False(pick.Object);
    }

    [Fact]
    public void NameWordsBreakNearTies()
    {
        // within 10% of the best count, the sequence's words pick the rig: a biter clip on the biter, not on the
        // player rig that binds one bone more (DLTB biter_a_idle: 64 vs 65)
        var player = Rig("sh2_player_tpp_skeleton.msh", Bones("b", 65), "player.model");
        var biter = Rig("man_zmb_biter_xl_skeleton.msh", Bones("b", 64), "biter.model");
        var rigs = AnimRigs.Of(player, biter);
        var tracks = H([.. Bones("b", 68)]);
        Assert.Same(biter, rigs.Pick(tracks, "biter_a_idle", "zombie_s1_a", null).Rig);
        // no word: the count decides
        Assert.Same(player, rigs.Pick(tracks, "idle", "zombie_s1_a", null).Rig);
        // light / medium / heavy are hit strengths in clips, not body builds
        var woman = Rig("woman_light_skeleton.msh", Bones("b", 64), "woman.model");
        Assert.Same(player, AnimRigs.Of(player, woman).Pick(tracks, "hitreaction_light", "zombie_s1_a", null).Rig);
        // past the window the count wins even against a matching word
        var far = Rig("man_zmb_biter_xl_skeleton.msh", Bones("b", 50), "biter.model");
        Assert.Same(player, AnimRigs.Of(player, far).Pick(tracks, "biter_a_idle", "zombie_s1_a", null).Rig);
    }

    [Fact]
    public void TppAndFppPlayerRigsSplitByName()
    {
        // DL2's TPP player rig is named player_skeleton: it counts as tpp; the sequence's words come before the bank's
        var tpp = Rig("player_skeleton.msh", Bones("b", 80), "player_tpp.model");
        var fpp = Rig("player_fpp_skeleton.msh", Bones("b", 80), "player_fpp.model");
        var rigs = AnimRigs.Of(tpp, fpp);
        var tracks = H([.. Bones("b", 90)]);
        Assert.Same(tpp, rigs.Pick(tracks, "m_tpp_inventory_main_pose", "m_fpp", null).Rig);
        Assert.Same(fpp, rigs.Pick(tracks, "fpp_unarmed_idle", "anims_player", null).Rig);
    }

    [Fact]
    public void ObjectClipsAndRefusalsNameTheBestRig()
    {
        var man = Rig("man_basic_skeleton.msh", ["root", "pelvis", "spine"], "man.model");
        var rigs = AnimRigs.Of(man);
        var none = rigs.Pick(H("gate_l", "gate_r", "lock"), "obj_gate_open", "anims_player", null);
        Assert.Null(none.Rig);
        Assert.True(none.Object);
        Assert.Equal("no rig binds any of its 3 tracks", none.Describe());
        // one generic bone in eight is an object's clip, named with the rig that bound it
        var few = rigs.Pick(H("root", "lid", "hinge", "a1", "a2", "a3", "a4", "a5"), "obj_chest_open", "anims_player", null);
        Assert.Same(man, few.Rig);
        Assert.True(few.Object);
        Assert.Equal("man_basic_skeleton.msh binds 1/8 tracks", few.Describe());
        // root, attach and camera tracks alone are not an object's bones
        var special = rigs.Pick(H("OffsetHelper", "Holder"), "attach_only", "anims_player", null);
        Assert.False(special.Object);
        Assert.Equal(0, special.Named);
    }

    [Fact]
    public void ModelFollowsTheShownOne()
    {
        var rig = Rig("player_skeleton.msh", Bones("b", 3), "_test_player.model", "player_a_tpp.model", "player_b_tpp.model");
        Assert.Equal("player_b_tpp.model", AnimRigs.Model(rig, "player_b_tpp.model", "x"));
        Assert.Equal("player_b_tpp.model", AnimRigs.Model(rig, "player_b_fpp.model", "x"));
        Assert.Equal("player_a_tpp.model", AnimRigs.Model(rig, null, "x"));
    }

    [Fact]
    public async Task Dl2ClipsFindTheirRigOrObject()
    {
        var install = Installs.Require("dl2");
        using var c = new RpackCatalog();
        await c.LoadAsync(install.Rpacks(), install.Assets, TestContext.Current.CancellationToken);
        using var models = new ModelCatalog(install.Paks(false));
        var rigs = AnimRigs.Build(c, models);
        var a = AnimCatalog.Build(c);
        uint[] Tracks(string bank, string seq)
        {
            var e = a.Find(bank, seq)!;
            var w = a.ClipOf(e.Record.Anm2Name)!.Value;
            Assert.Equal(AnimCatalog.Clip(c, e.Record.Anm2Name)!.Value, (w.Pack, w.Index));
            return Anm2Resource.Decode(w.Pack, w.Index).TrackHashes;
        }
        RigPick Pick(string bank, string seq) => rigs.Pick(Tracks(bank, seq), seq, bank, null);
        // the name heuristic sent these to man_basic (76 tracks) and a TPP rig; the tracks say banshee and FPP
        Assert.Equal("zmb_banshee_skeleton.msh", Pick("anims_man_all", "bsh_standup_back_01").Rig!.Skeleton);
        Assert.Equal("player_fpp_skeleton.msh", Pick("anims_player", "fpp_unarmed_slipmoveup").Rig!.Skeleton);
        Assert.Equal("player_skeleton.msh", Pick("m_fpp", "m_tpp_inventory_main_pose").Rig!.Skeleton);
        // a gate's clip binds no character rig: it plays on the gate mesh, all 5 tracks bound
        var gate = Pick("anims_player", "obj_gre_gate_locked_01");
        Assert.Null(gate.Rig);
        Assert.True(gate.Object);
        var objects = AnimObjects.Build(c, TestContext.Current.CancellationToken);
        var hit = objects.Best(Tracks("anims_player", "obj_gre_gate_locked_01"), "obj_gre_gate_locked_01", c)!.Value;
        Assert.Equal(("gre_gate_door_a_anm", 5), (c.Name(hit.Gid), hit.Bound));
        // a view leaving that mesh out (the viewer's Mods off leaves the mods' meshes out) never picks it
        var gateTracks = Tracks("anims_player", "obj_gre_gate_locked_01");
        Assert.Null(objects.Best(gateTracks, "obj_gre_gate_locked_01", c, _ => false));
        Assert.NotEqual(hit.Gid, objects.Best(gateTracks, "obj_gre_gate_locked_01", c, gid => gid != hit.Gid)?.Gid);
    }
}
