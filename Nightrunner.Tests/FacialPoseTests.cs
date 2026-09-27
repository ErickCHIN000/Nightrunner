using System.Buffers.Binary;
using System.Numerics;
using Nightrunner.Core.Anim;
using Nightrunner.Core.Cast;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Mesh.ClassReader;
using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;
using Record = Nightrunner.Core.Mesh.ClassReader.Record;

namespace Nightrunner.Tests;

/// <summary>Face rigs of solid-head meshes and pose-weight (facial / lip-sync) clips mixed on them.</summary>
public class FacialPoseTests
{
    // ---- synthetic ------------------------------------------------------------------------------------------

    private static Quaternion Axis(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    /// <summary>A rig of neutral + the given poses and the given bones, all elements used at every detail.</summary>
    private static FaceRig Rig(FacePose[] poses, params FaceBone[] bones) =>
        new() { Poses = [new FacePose("neutral_pose", Anm2Hash.H41Cased("neutral_pose"), 1), .. poses], Bones = bones };

    private static FacePose Pose(string name, uint kind = 7) => new(name, Anm2Hash.H41Cased(name), kind);

    private static FaceBone Bone(string name, params FacePoseElement[] e) =>
        new(Anm2Hash.H41(name), [(uint)e.Length, (uint)e.Length, 1, 1, 1], e);

    /// <summary>A pose-weight clip for <paramref name="rig"/>: keys × weights (poses then wrinkles; missing = 0).</summary>
    private static Anm2Clip Clip(FaceRig rig, params double[][] keys)
    {
        int n = rig.WeightCount + FacialPose.WrinkleCount, tracks = (n + 8) / 9, s = 9 * tracks;
        var values = new double[keys.Length * s];
        for (int k = 0; k < keys.Length; k++) keys[k].CopyTo(values, k * s);
        var listB = rig.Poses.Skip(1).Select(p => p.Hash)
            .Concat(Enumerable.Range(0, FacialPose.WrinkleCount).Select(i => Anm2Hash.H41Cased($"wrinkles{i}"))).ToArray();
        return new Anm2Clip
        {
            TrackHashes = Enumerable.Range(0, tracks).Select(t => Anm2Hash.H41(t.ToString())).ToArray(),
            PoseHashes = listB, FrameBound = (ushort)(keys.Length - 1), Values = values,
        };
    }

    private static ModelSkeleton Skeleton(params (string Name, int Parent, double[] Global)[] bones)
    {
        var s = new ModelSkeleton();
        foreach (var (n, p, r) in bones)
        {
            s.Names.Add(n);
            s.Parents.Add(p);
            s.Rest.Add(r);
            s.Source.Add("synth");
        }
        return s;
    }

    private static void Near(Quaternion a, Quaternion b, float tol = 1e-5f)
    {
        if (Quaternion.Dot(a, b) < 0) b = -b;
        Assert.True((a - b).Length() < tol, $"{a} vs {b}");
    }

    private static void Near(Vector3 a, Vector3 b, float tol = 1e-5f) => Assert.True((a - b).Length() < tol, $"{a} vs {b}");

    [Fact]
    public void PoseHashKeepsCase()
    {
        // stored pose hashes and list B use h41 without lowering: cheek_raise_L and _R differ by 'R' − 'L'
        Assert.Equal(0x108E3716u, Anm2Hash.H41Cased("cheek_raise_L"));
        Assert.Equal(0x108E371Cu, Anm2Hash.H41Cased("cheek_raise_R"));
        Assert.Equal(0x914ED777u, Anm2Hash.H41Cased("wrinkles0"));
        Assert.Equal(Anm2Hash.H41("lip_flare__"), Anm2Hash.H41Cased("lip_flare__"));
    }

    [Fact]
    public void MixIsBaseTimesWeightedDeltas()
    {
        var baseQ = Axis(Vector3.UnitY, 30);
        var e1 = Axis(Vector3.UnitX, 90);
        var e2 = Axis(Vector3.UnitZ, 40);
        var bone = Bone("jaw",
            new FacePoseElement(0, baseQ, new Vector3(0, 1, 0)),
            new FacePoseElement(1, e1, new Vector3(0, 0, 0.1f)),
            new FacePoseElement(2, e2, new Vector3(0.2f, 0, 0)));
        var w = new float[2 + FacialPose.WrinkleCount];

        var (q, t) = FacialPose.Mix(bone, w);                       // no weight: the base
        Near(baseQ, q);
        Near(new Vector3(0, 1, 0), t);

        w[0] = 1;                                                    // full pose 1: base ⊗ e1, base.t + base.q · e1.t
        (q, t) = FacialPose.Mix(bone, w);
        Near(baseQ * e1, q);
        Near(new Vector3(0, 1, 0) + Vector3.Transform(new Vector3(0, 0, 0.1f), baseQ), t);

        w[1] = 1;                                                    // elements compose in order
        (q, t) = FacialPose.Mix(bone, w);
        Near(baseQ * e1 * e2, q);

        w[0] = 0.5f;
        w[1] = 0.01f;                                                // at the threshold: dropped
        (q, t) = FacialPose.Mix(bone, w);
        var half = Quaternion.Normalize(new Quaternion(0.5f * e1.X, 0.5f * e1.Y, 0.5f * e1.Z, 0.5f * e1.W + 0.5f));
        Near(baseQ * half, q);
        Near(new Vector3(0, 1, 0) + Vector3.Transform(new Vector3(0, 0, 0.05f), baseQ), t);
    }

    [Fact]
    public void WeightsClampDropAndCorrect()
    {
        var rig = Rig([Pose("a"), Pose("b"), Pose("cs_a_b", 8), Pose("blink", 6)], Bone("x", new FacePoseElement(0, Quaternion.Identity, Vector3.Zero)));
        // weights: a, b, cs_a_b, blink, then wrinkles0..
        var clip = Clip(rig, [1.5, 0.005, 0.7, 0.4, 0.3], [0.5, 0.8, 0.7, -1, 0.0005]);
        var fp = new FacialPose(clip, rig, Skeleton(("x", -1, AnimPose.Trs(Quaternion.Identity, Vector3.Zero, Vector3.One))),
                                [new CorrectiveShape("cs_a_b", ["a", "b"])]);
        var w0 = fp.Weights(0);
        Assert.Equal(1f, w0[0]);                                     // clamped
        Assert.Equal(0f, w0[1]);                                     // ≤ 0.01 dropped
        Assert.Equal(0f, w0[2]);                                     // corrective = a · b, the clip's value ignored
        Assert.Equal(0.4f, w0[3], 6);
        Assert.Equal(0.3f, w0[4], 6);                                // wrinkles0
        var w1 = fp.Weights(1);
        Assert.Equal(0.4f, w1[2], 6);
        Assert.Equal(0f, w1[3]);                                     // negative clamps to 0
        Assert.Equal(0f, w1[4]);                                     // wrinkle ≤ 0.001
        var mid = fp.Weights(0.5);                                   // linear between keys, then clamped
        Assert.Equal(1f, mid[0], 6);
        Assert.Equal((0.005f + 0.8f) / 2 * 1f, mid[1], 5);

        Assert.Equal(FacialPose.GroupEyelids, fp.Groups[3]);
        fp.GroupWeights[FacialPose.GroupEyelids] = 0;                // lip sync: the engine never drives eyelids from the clip
        Assert.Equal(0f, fp.Weights(0)[3]);
    }

    [Fact]
    public void SampleChainsFaceBonesUnderTheirParents()
    {
        var e1 = Axis(Vector3.UnitX, 20);
        var rig = Rig([Pose("open")],
            Bone("jaw", new FacePoseElement(0, Quaternion.Identity, new Vector3(0, 0.1f, 0)), new FacePoseElement(1, e1, new Vector3(0, -0.01f, 0))));
        var head = AnimPose.Trs(Quaternion.Identity, new Vector3(0, 1.6f, 0), Vector3.One);
        var jawRest = AnimPose.Trs(Quaternion.Identity, new Vector3(0, 1.7f, 0), Vector3.One);
        var skel = Skeleton(("head", -1, head), ("jaw", 0, jawRest), ("tip", 1, AnimPose.Trs(Quaternion.Identity, new Vector3(0, 1.7f, 0.1f), Vector3.One)));
        var fp = new FacialPose(Clip(rig, [0], [1]), rig, skel);
        Assert.Equal(1, fp.BoundBones);
        var g0 = fp.Sample(0);
        for (int k = 0; k < 16; k++) Assert.Equal(jawRest[k], g0[1][k], 5);
        var g1 = fp.Sample(1);
        var expect = ModelCast.Mul(head, AnimPose.Trs(e1, new Vector3(0, 0.09f, 0), Vector3.One));
        for (int k = 0; k < 16; k++) Assert.Equal(expect[k], g1[1][k], 5);
        // the child follows its parent's new transform with its rest local
        var tip = ModelCast.Mul(g1[1], ModelCast.Mul(ModelCast.Invert(jawRest), skel.Rest[2]));
        for (int k = 0; k < 16; k++) Assert.Equal(tip[k], g1[2][k], 5);
    }

    [Fact]
    public void RefusesClipsThatAreNotThisHeads()
    {
        var rig = Rig([Pose("a"), Pose("b")], Bone("x", new FacePoseElement(0, Quaternion.Identity, Vector3.Zero)));
        var skel = Skeleton(("x", -1, AnimPose.Trs(Quaternion.Identity, Vector3.Zero, Vector3.One)));
        var good = Clip(rig, [0]);
        Assert.Null(FacialPose.Mismatch(good, rig));

        var swapped = new Anm2Clip { TrackHashes = good.TrackHashes, PoseHashes = [good.PoseHashes[1], good.PoseHashes[0], .. good.PoseHashes[2..]], FrameBound = 0, Values = good.Values };
        Assert.Throws<Anm2UnsupportedException>(() => new FacialPose(swapped, rig, skel));
        var shortTracks = new Anm2Clip { TrackHashes = good.TrackHashes[..1], PoseHashes = good.PoseHashes, FrameBound = 0, Values = new double[9] };
        Assert.Contains("tracks", FacialPose.Mismatch(shortTracks, rig));
        var bones = new Anm2Clip { TrackHashes = [Anm2Hash.H41("pelvis")], FrameBound = 0, Values = new double[9] };
        Assert.Contains("list B", FacialPose.Mismatch(bones, rig));
        var other = Rig([Pose("a"), Pose("b"), Pose("c")], rig.Bones);
        Assert.NotNull(FacialPose.Mismatch(good, other));
        Assert.Throws<Anm2UnsupportedException>(() => new FacialPose(good, rig, skel, [new CorrectiveShape("a", ["zz"])]));
    }

    [Fact]
    public void CorrectivesParseAndApplyToV2Only()
    {
        var json = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(
            """{"correctives":[{"pose_name":"cs_a_b","used_poses":["a","b"]}]}""")!;
        var shapes = CorrectiveShape.Parse(json);
        Assert.Equal("cs_a_b", Assert.Single(shapes).Pose);
        Assert.Equal(["a", "b"], shapes[0].Inputs);
        var v1 = Rig([.. Enumerable.Range(1, FacialPose.V1Poses - 1).Select(i => Pose($"p{i}"))]);
        var v2 = Rig([.. Enumerable.Range(1, FacialPose.V2Poses - 1).Select(i => Pose($"p{i}"))]);
        Assert.Empty(CorrectiveShape.ForRig(v1, shapes));
        Assert.Same(shapes, CorrectiveShape.ForRig(v2, shapes));
        Assert.Throws<ModelFormatException>(() => CorrectiveShape.Parse(new System.Text.Json.Nodes.JsonObject()));
    }

    /// <summary>A face-data image as meshes store it: class 16 → 17 (poses) / 18 (bones) → 20 (elements).</summary>
    private static Image FaceImage(int poseStride, string[] poses, uint[] kinds, (uint Hash, uint[] Counts, (int Pose, float[] Q, float[] T)[] Elems)[] bones)
    {
        var buf = new List<byte>();
        var recs = new List<Record>();
        int Obj(uint cls, int count, int size)
        {
            while (buf.Count % 16 != 0) buf.Add(0);
            int off = buf.Count;
            buf.AddRange(new byte[size]);
            recs.Add(new Record((uint)off, (0xB0u << 24) | cls, (uint)count));
            return off;
        }
        int fd = Obj(16, 1, 0x28);
        int pt = Obj(17, poses.Length, poses.Length * poseStride);
        int bt = Obj(18, bones.Length, bones.Length * 0x20);
        var elems = bones.Select(b => Obj(20, b.Elems.Length, b.Elems.Length * 0x20)).ToArray();
        var names = poses.Select(p => Obj(0, 1, p.Length + 1)).ToArray();
        while (buf.Count % 16 != 0) buf.Add(0);
        var b = buf.ToArray();
        for (int i = 0; i < poses.Length; i++) System.Text.Encoding.ASCII.GetBytes(poses[i]).CopyTo(b, names[i]);
        var slots = new List<Slot>();
        void Ptr(int at, int target)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at), (ulong)target + 1);
            slots.Add(new Slot((uint)at, 0));
        }
        void U32(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
        Ptr(fd, pt);
        Ptr(fd + 8, bt);
        U32(fd + 0x18, (uint)poses.Length);
        U32(fd + 0x1C, (uint)bones.Length);
        for (int p = 0; p < poses.Length; p++)
        {
            Ptr(pt + p * poseStride, names[p]);
            U32(pt + p * poseStride + 8, Anm2Hash.H41Cased(poses[p]));
            U32(pt + p * poseStride + 12, kinds[p]);
        }
        for (int i = 0; i < bones.Length; i++)
        {
            int o = bt + i * 0x20;
            Ptr(o, elems[i]);
            for (int d = 0; d < 5; d++) U32(o + 8 + 4 * d, bones[i].Counts[d]);
            U32(o + 0x1C, bones[i].Hash);
            for (int j = 0; j < bones[i].Elems.Length; j++)
            {
                var (pose, q, t) = bones[i].Elems[j];
                int e = elems[i] + j * 0x20;
                for (int k = 0; k < 4; k++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(e + 4 * k), q[k]);
                for (int k = 0; k < 3; k++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(e + 0x10 + 4 * k), t[k]);
                U32(e + 0x1C, (uint)pose);
            }
        }
        var fx = new Fixups((uint)b.Length, (uint)recs.Count, recs, slots.OrderBy(s => s.Offset).ToList());
        return new Image(b, fx);
    }

    [Fact]
    public void ReadsFaceDataFromTheImage()
    {
        float[] id = [0, 0, 0, 1], zero = [0, 0, 0];
        var img = FaceImage(0x60, ["neutral_pose", "blink", "jaw_open"], [1, 6, 7],
            [(Anm2Hash.H41("jaw_bone"), [2, 2, 1, 1, 1], [(0, id, [0, 1, 0]), (2, [0.5f, 0, 0, 0.5f], [0, -0.01f, 0])])]);
        var rig = FaceRig.FromImage(img)!;
        Assert.Equal(["neutral_pose", "blink", "jaw_open"], rig.Poses.Select(p => p.Name));
        Assert.Equal(6u, rig.Poses[1].Kind);
        Assert.Equal(2, rig.WeightCount);
        var bone = Assert.Single(rig.Bones);
        Assert.Equal(Anm2Hash.H41("jaw_bone"), bone.Hash);
        Assert.Equal(new Vector3(0, 1, 0), bone.Base.Translation);
        Assert.Equal(2, bone.Elements[1].Pose);
        Assert.Empty(rig.ExtraBones);
        // a detail count that is not the element count, or an element naming a pose past the table, is malformed
        Assert.Throws<MeshFormatException>(() => FaceRig.FromImage(FaceImage(0x60, ["neutral_pose"], [1],
            [(1u, [3, 2, 1, 1, 1], [(0, id, zero), (0, id, zero)])])));
        Assert.Throws<MeshFormatException>(() => FaceRig.FromImage(FaceImage(0x60, ["neutral_pose"], [1],
            [(1u, [2, 2, 1, 1, 1], [(0, id, zero), (5, id, zero)])])));
        Assert.Throws<MeshFormatException>(() => FaceRig.FromImage(FaceImage(0x58, ["neutral_pose", "blink"], [1, 6], [])));
    }

    // ---- install-backed ----------------------------------------------------------------------------------------

    private static Anm2Clip InstallClip(string game, string name)
    {
        var install = Installs.Require(game);
        foreach (var path in install.Rpacks())
        {
            using var pack = RpackFile.Open(path);
            for (int i = 0; i < pack.Count; i++)
                if (pack.Logicals[i].Type == Anm2Resource.TypeAnimation && pack.Name(i).Trim() == name)
                    return Anm2Resource.Decode(pack, i);
        }
        Assert.Skip($"{name} not in the {game} install");
        return null!;
    }

    private static (FaceRig Rig, ModelSkeleton Skeleton, MeshModel Mesh) Head(string game, string name)
    {
        var (mesh, _) = MeshFixtures.Load(game, name);
        return (FaceRig.FromMesh(mesh)!, ModelSkeleton.FromMesh(mesh, name), mesh);
    }

    [Fact]
    public void DltbHeadRigIsTheBindPose()
    {
        var (rig, skel, mesh) = Head("dltb", "sh_man_a");
        Assert.Equal(FacialPose.V1Poses, rig.Poses.Length);
        Assert.Equal("neutral_pose", rig.Poses[0].Name);
        Assert.Equal(128, rig.Bones.Length);
        Assert.Equal(39, rig.ExtraBones.Length);
        Assert.All(rig.Poses, p => Assert.Equal(Anm2Hash.H41Cased(p.Name), p.Hash));
        // every face bone is a mesh bone and its base element is that bone's bind local
        var byHash = mesh.Entities.ToDictionary(e => Anm2Hash.H41(e.NameStr), e => e);
        foreach (var b in rig.Bones)
        {
            var local = byHash[b.Hash].Local4x4();
            var fromRig = AnimPose.Trs(b.Base.Rotation, b.Base.Translation, Vector3.One);
            for (int k = 0; k < 12; k++) Assert.Equal(local[k], fromRig[k], 4);
        }
        Assert.Null(FaceRig.FromMesh(MeshFixtures.Load("dltb", "alarm_lamp_a").Model));
    }

    [Fact]
    public void DltbTemplateClipOpensTheJaw()
    {
        var (rig, skel, _) = Head("dltb", "sh_man_a");
        var clip = InstallClip("dltb", "solid_head_template_v1");     // one pose per frame: frame 120 is jaw_opening__
        var fp = new FacialPose(clip, rig, skel);
        Assert.Equal(rig.Bones.Length, fp.BoundBones);
        var w = fp.Weights(120);
        Assert.Equal("jaw_opening__", rig.Poses[Array.IndexOf(w, w[..rig.WeightCount].Max()) + 1].Name);
        int chin = skel.IndexOf("c_centerchin_bone");
        var g = fp.Sample(120);
        Assert.True(skel.Rest[chin][7] - g[chin][7] > 0.01, "chin drops over 1 cm");
        var rest = fp.Sample(0);                                       // frame 0 holds no weight: the bind pose
        for (int b = 0; b < skel.Names.Count; b++)
            for (int k = 0; k < 12; k++) Assert.True(Math.Abs(skel.Rest[b][k] - rest[b][k]) < 1e-5);
    }

    [Fact]
    public void DltbV2HeadPlaysLipSyncWithCorrectives()
    {
        var install = Installs.Require("dltb");
        var (rig, skel, _) = Head("dltb", "sh2_npc_ft_olivia");
        Assert.Equal(FacialPose.V2Poses, rig.Poses.Length);
        using var paks = new ModelCatalog(install.Paks(false));
        var shapes = CorrectiveShape.ForRig(rig, CorrectiveShape.Load(paks));
        Assert.Equal(81, shapes.Count);
        var clip = InstallClip("dltb", "dlg_dlcftq1oliviafirstmeetaltdlg_dlc_ft_sq_olivia_034");
        var fp = new FacialPose(clip, rig, skel, shapes);
        Assert.Equal(258, fp.BoundBones);
        var names = rig.Poses.Skip(1).Select(p => p.Name).ToList();
        for (int f = 0; f <= clip.FrameBound; f += 37)
        {
            var w = fp.Weights(f);
            foreach (var c in shapes.Take(5))
                Assert.Equal(c.Inputs.Aggregate(1f, (p, i) => p * w[names.IndexOf(i)]), w[names.IndexOf(c.Pose)], 5);
            Assert.All(fp.Sample(f), m => Assert.All(m, v => Assert.True(double.IsFinite(v))));
        }
        // a v1 lip-sync clip is refused on a v2 head
        Assert.NotNull(FacialPose.Mismatch(InstallClip("dltb", "brk_asur_m_b_panic_05"), rig));
    }

    [Fact]
    public void Dl2HeadPlaysLipSync()
    {
        var (rig, skel, _) = Head("dl2", "sh_basemesh");
        Assert.Equal(FacialPose.V1Poses, rig.Poses.Length);
        var clip = InstallClip("dl2", "brk_bdt_m_b_surrender_19");
        var fp = new FacialPose(clip, rig, skel);
        Assert.Equal(rig.Bones.Length, fp.BoundBones);
        double moved = 0;
        for (int f = 0; f <= clip.FrameBound; f++)
        {
            var g = fp.Sample(f);
            for (int b = 0; b < g.Length; b++)
                moved = Math.Max(moved, Math.Abs(g[b][7] - skel.Rest[b][7]) + Math.Abs(g[b][11] - skel.Rest[b][11]));
        }
        Assert.True(moved > 0.002, $"face moves {moved * 1000:F1} mm");
    }
}
