using System.Numerics;
using System.Text.Json.Nodes;
using Nightrunner.Core.Model;

namespace Nightrunner.Core.Anim;

/// <summary>
/// A corrective shape of <c>solid_head_corrective_shapes.json</c>: pose <see cref="Pose"/> takes the product of the
/// weights of <see cref="Inputs"/> (<c>FacialExpressionManager::LoadCorrectiveShapeConfig</c>).
/// </summary>
public sealed record CorrectiveShape(string Pose, string[] Inputs)
{
    /// <summary>The data-pak member the engine reads for the v2 template.</summary>
    public const string PakMember = "characters/solidhead/solid_head_corrective_shapes.json";

    /// <summary><c>{"correctives": [{"pose_name": "...", "used_poses": ["...", ...]}, ...]}</c>.</summary>
    public static CorrectiveShape[] Parse(JsonObject root)
    {
        if (root["correctives"] is not JsonArray list)
            throw new ModelFormatException("corrective shapes: 'correctives' root member is not an array");
        return list.Select((n, i) =>
        {
            string pose = (string?)n?["pose_name"] ?? throw new ModelFormatException($"corrective {i}: no pose_name");
            var used = n!["used_poses"] as JsonArray ?? throw new ModelFormatException($"corrective {i}: no used_poses");
            return new CorrectiveShape(pose, used.Select(u => (string?)u ?? throw new ModelFormatException($"corrective {i}: null pose")).ToArray());
        }).ToArray();
    }

    /// <summary>The shapes from the data paks (a later pak overriding an earlier one), or null when no pak has them.</summary>
    public static CorrectiveShape[]? Load(ModelCatalog paks)
    {
        foreach (var path in paks.PakPaths.Reverse())
        {
            PakIndex pak;
            try { pak = paks.Pak(path); }
            catch (KeyNotFoundException) { continue; }
            var m = pak.Members.FirstOrDefault(x => x.Name.Replace('\\', '/').Equals(PakMember, StringComparison.OrdinalIgnoreCase));
            if (m is not null) return Parse(pak.LoadJson(m));
        }
        return null;
    }

    /// <summary>
    /// The shapes the engine applies to <paramref name="rig"/>: all of them for the v2 template
    /// (<see cref="FacialPose.V2Poses"/> poses), none for v1 (its template names no config).
    /// </summary>
    public static IReadOnlyList<CorrectiveShape> ForRig(FaceRig rig, IReadOnlyList<CorrectiveShape>? shapes) =>
        rig.Poses.Length == FacialPose.V2Poses && shapes is not null ? shapes : [];
}

/// <summary>
/// A pose-weight clip (facial expression or lip sync) played on a solid-head face rig, as
/// <c>CSolidHeadController::UpdateMix</c> mixes one clip at full weight and the highest detail.
/// </summary>
/// <remarks>
/// <para><b>Weights.</b> List B of the clip is the rig's poses 1.. (sorted by case-kept h41) followed by
/// <c>wrinkles0..15</c>; weight <c>i</c> is stream <c>i</c> in track-major order (track <c>i / 9</c>, component
/// <c>i % 9</c>), sampled linearly between keys (<c>CAnimPoseWeightSampler::SampleWeights</c>). Each is clamped to
/// [0, 1] and scaled by its group's weight (face; eyelids = the first two kind-6 and kind-10 poses; eyeballs = kinds
/// 2..5; wrinkles); a bone weight at or below 0.01 is dropped. Corrective shapes (v2 template) then set their pose to
/// the product of their inputs, in file order. The 16 wrinkle weights drive normal-map blends
/// (<c>CoSkinnedMesh::UpdateFaceWrinkles</c>), not bones; <see cref="Weights"/> returns them.</para>
/// <para><b>Mix rule.</b> Per face bone, over its pose elements 1..: with <c>w</c> the weight of the element's pose,
/// <c>q ← q ⊗ (w·e.xyz, w·e.w + 1 − w)</c> and <c>t ← t + w·e.t</c> (elements with <c>w ≤ 0.01</c> skipped); then
/// <c>q</c> is normalised and the bone's parent-local transform is <c>R = base.q ⊗ q</c>,
/// <c>T = base.t + base.q · t</c>, no scale. The base (element 0) is the bind local. Bones the rig does not drive keep
/// their rest local; globals follow <see cref="AnimPose.Sample"/> (4×4 row-major, parents first).</para>
/// <para><b>Refused</b> (<see cref="Anm2UnsupportedException"/>): a clip without list B, a list B that is not the
/// rig's poses plus the 16 wrinkles, a track count other than ⌈weights / 9⌉ (the engine's own check), and a corrective
/// naming a pose the rig lacks. Not modelled: the engine's procedural blink and eye look-at, the blend between up to
/// four face and lip-sync slots, lower detail levels (fewer elements, correctives off below detail 4).</para>
/// </remarks>
public sealed class FacialPose : IClipPose
{
    /// <summary>Pose counts (neutral included) of the two templates the engine knows.</summary>
    public const int V1Poses = 105, V2Poses = 192;
    public const int WrinkleCount = 16;
    /// <summary>Weights at or below this are dropped (<c>UpdateMix</c>, 0x3C23D70A).</summary>
    public const float Threshold = 0.01f;
    public const int GroupFace = 0, GroupEyelids = 1, GroupEyeballs = 2, GroupWrinkles = 3;

    public Anm2Clip Clip { get; }
    public FaceRig Rig { get; }
    public ModelSkeleton Skeleton { get; }
    /// <summary>Skeleton bone per face bone, or −1.</summary>
    public int[] BoneOfFaceBone { get; }
    /// <summary>Group per weight (<see cref="GroupFace"/>...).</summary>
    public int[] Groups { get; }
    /// <summary>Weight per group; the engine sets eyelids and eyeballs from the eye sources (never for lip sync).</summary>
    public float[] GroupWeights { get; } = [1, 1, 1, 1];

    private readonly (int Target, int[] Inputs)[] _correctives;
    private readonly double[][] _restLocal;
    private readonly int[] _faceBoneOfBone;

    public FacialPose(Anm2Clip clip, FaceRig rig, ModelSkeleton skeleton, IReadOnlyList<CorrectiveShape>? correctives = null)
    {
        if (Mismatch(clip, rig) is { } why) throw new Anm2UnsupportedException(why);
        Clip = clip;
        Rig = rig;
        Skeleton = skeleton;
        int n = clip.PoseHashes.Length, poses = rig.WeightCount;
        Groups = new int[n];
        int lids = 0, lidsAlt = 0;
        for (int i = 0; i < n; i++)
            Groups[i] = i >= poses ? GroupWrinkles : rig.Poses[i + 1].Kind switch
            {
                >= 2 and <= 5 => GroupEyeballs,
                6 when lids++ < 2 => GroupEyelids,
                10 when lidsAlt++ < 2 => GroupEyelids,
                _ => GroupFace,
            };
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int p = 1; p < rig.Poses.Length; p++) index.TryAdd(rig.Poses[p].Name, p - 1);
        int Resolve(string name) => index.TryGetValue(name, out int i) ? i
            : throw new Anm2UnsupportedException($"corrective shape pose '{name}' is not a pose of this head");
        _correctives = (correctives ?? []).Select(c => (Resolve(c.Pose), c.Inputs.Select(Resolve).ToArray())).ToArray();

        var byHash = new Dictionary<uint, int>();
        for (int b = 0; b < skeleton.Names.Count; b++) byHash.TryAdd(Anm2Hash.H41(skeleton.Names[b]), b);
        BoneOfFaceBone = rig.Bones.Select(fb => byHash.TryGetValue(fb.Hash, out int b) ? b : -1).ToArray();
        _faceBoneOfBone = Enumerable.Repeat(-1, skeleton.Names.Count).ToArray();
        for (int f = 0; f < BoneOfFaceBone.Length; f++)
            if (BoneOfFaceBone[f] is >= 0 and var b && _faceBoneOfBone[b] < 0) _faceBoneOfBone[b] = f;
        _restLocal = new double[skeleton.Names.Count][];
        for (int b = 0; b < skeleton.Names.Count; b++)
            _restLocal[b] = skeleton.Parents[b] < 0 ? skeleton.Rest[b] : Cast.ModelCast.Mul(Cast.ModelCast.Invert(skeleton.Rest[skeleton.Parents[b]]), skeleton.Rest[b]);
    }

    /// <summary>Face bones bound to a skeleton bone.</summary>
    public int BoundBones => BoneOfFaceBone.Count(b => b >= 0);

    /// <summary>Why <paramref name="clip"/> cannot play on <paramref name="rig"/>, or null when it can.</summary>
    public static string? Mismatch(Anm2Clip clip, FaceRig rig)
    {
        if (!clip.IsPoseWeights) return "not a pose-weight clip (no list B)";
        int n = clip.PoseHashes.Length, poses = rig.WeightCount;
        if (clip.TrackCount != (n + 8) / 9)
            return $"pose-weight clip with {clip.TrackCount} tracks for {n} weights (the engine needs {(n + 8) / 9})";
        if (n != poses + WrinkleCount)
            return $"clip weights {n} poses of a {poses + WrinkleCount}-weight head ({rig.Poses.Length}-pose rig)";
        for (int i = 0; i < poses; i++)
            if (clip.PoseHashes[i] != rig.Poses[i + 1].Hash)
                return $"clip pose {i} (0x{clip.PoseHashes[i]:X8}) is not head pose '{rig.Poses[i + 1].Name}'";
        for (int k = 0; k < WrinkleCount; k++)
            if (clip.PoseHashes[poses + k] != Anm2Hash.H41Cased($"wrinkles{k}"))
                return $"clip weight {poses + k} is not wrinkles{k}";
        return null;
    }

    /// <summary>The mixed weight of every list-B entry at <paramref name="frame"/> (clip frames, clamped).</summary>
    public float[] Weights(double frame)
    {
        frame = Math.Clamp(frame, 0, Clip.FrameBound);
        int k0 = (int)Math.Floor(frame), k1 = Math.Min(k0 + 1, Clip.FrameBound);
        float t = (float)(frame - k0);
        int n = Clip.PoseHashes.Length, s = Clip.StreamCount, poses = Rig.WeightCount;
        var w = new float[n];
        for (int i = 0; i < n; i++)
        {
            float v0 = (float)Clip.Values[k0 * s + i], v1 = (float)Clip.Values[k1 * s + i];
            float v = Math.Clamp(v0 * (1 - t) + v1 * t, 0f, 1f) * GroupWeights[Groups[i]];
            // one slot of CAnimWeightedSum: Calculate() = v²/v = v, 0 at or below 0.001
            w[i] = i < poses ? (v > Threshold ? v : 0) : (v > 0.001f ? v : 0);
        }
        foreach (var (target, inputs) in _correctives)
        {
            float p = 1;
            foreach (int i in inputs) p *= w[i];
            w[target] = p;
        }
        return w;
    }

    /// <summary>Parent-local rotation and translation of every face bone at <paramref name="frame"/>.</summary>
    public (Quaternion Rotation, Vector3 Translation)[] FaceLocals(double frame)
    {
        var w = Weights(frame);
        var result = new (Quaternion, Vector3)[Rig.Bones.Length];
        for (int f = 0; f < Rig.Bones.Length; f++) result[f] = Mix(Rig.Bones[f], w);
        return result;
    }

    /// <summary>One face bone's local from the weights (weight of pose p at <c>w[p − 1]</c>).</summary>
    public static (Quaternion Rotation, Vector3 Translation) Mix(FaceBone bone, ReadOnlySpan<float> w)
    {
        var q = Quaternion.Identity;
        var t = Vector3.Zero;
        int n = (int)bone.DetailCounts[0];
        for (int j = 1; j < n; j++)
        {
            var e = bone.Elements[j];
            float x = w[e.Pose - 1];
            if (x <= Threshold) continue;
            t += x * e.Translation;
            q *= new Quaternion(x * e.Rotation.X, x * e.Rotation.Y, x * e.Rotation.Z, x * e.Rotation.W + (1 - x));
        }
        float len = q.Length();
        if (len > 1e-6f) q *= 1 / len;
        var b = bone.Base;
        return (b.Rotation * q, b.Translation + Vector3.Transform(t, b.Rotation));
    }

    /// <summary>Bone locals at <paramref name="frame"/>: the face bones mixed, every other bone at rest.</summary>
    public double[][] Locals(double frame)
    {
        var locals = (double[][])_restLocal.Clone();
        Over(locals, frame);
        return locals;
    }

    /// <summary>
    /// Put the mixed face bones into <paramref name="locals"/> (a body pose's locals, changed in place): the engine's
    /// face mix replaces those bones' locals, whatever the body clip set.
    /// </summary>
    public void Over(double[][] locals, double frame)
    {
        var face = FaceLocals(frame);
        for (int f = 0; f < face.Length; f++)
            if (BoneOfFaceBone[f] is >= 0 and var b && b < locals.Length)
                locals[b] = AnimPose.Trs(face[f].Rotation, face[f].Translation, Vector3.One);
    }

    /// <summary>Bone globals (4×4 row-major) at <paramref name="frame"/>, indexed by skeleton bone.</summary>
    public double[][] Sample(double frame)
    {
        var locals = FaceLocals(frame);
        var globals = new double[Skeleton.Names.Count][];
        for (int b = 0; b < globals.Length; b++)
        {
            int f = _faceBoneOfBone[b];
            double[] local = f < 0 ? _restLocal[b] : AnimPose.Trs(locals[f].Rotation, locals[f].Translation, Vector3.One);
            int p = Skeleton.Parents[b];
            globals[b] = p < 0 ? local : Cast.ModelCast.Mul(globals[p], local);
        }
        return globals;
    }
}
