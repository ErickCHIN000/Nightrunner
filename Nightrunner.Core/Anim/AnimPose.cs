using System.Numerics;
using Nightrunner.Core.Model;

namespace Nightrunner.Core.Anim;

/// <summary>
/// A clip played on a skeleton: tracks bound to bones by name hash (<see cref="Anm2Binding"/>), bone globals sampled
/// at any (fractional) frame. Clip values are absolute parent-local TRS, <c>M = T·R·S</c> with column vectors, the same
/// convention as the skeleton's 4×4 row-major rest globals; bones the clip does not animate keep their rest local.
/// Keys are blended as the engine does: nlerp with a sign fix for rotation, lerp for translation and scale.
/// </summary>
/// <summary>A clip evaluated on a skeleton: bone locals at a frame, or laid over another pose's locals.</summary>
public interface IClipPose
{
    Anm2Clip Clip { get; }
    ModelSkeleton Skeleton { get; }
    int BoundBones { get; }
    double[][] Locals(double frame);
    void Over(double[][] locals, double frame);
}

public sealed class AnimPose : IClipPose
{
    public Anm2Clip Clip { get; }
    public ModelSkeleton Skeleton { get; }
    public Anm2Binding Binding { get; }
    /// <summary>Track animating each bone, or −1.</summary>
    public int[] TrackOfBone { get; }
    private readonly double[][] _restLocal;

    public AnimPose(Anm2Clip clip, ModelSkeleton skeleton)
    {
        Clip = clip;
        Skeleton = skeleton;
        Binding = Anm2Binding.Bind(clip.TrackHashes, skeleton);
        TrackOfBone = Enumerable.Repeat(-1, skeleton.Names.Count).ToArray();
        for (int t = 0; t < Binding.TrackToBone.Length; t++)
            if (Binding.TrackToBone[t] is >= 0 and var b && TrackOfBone[b] < 0) TrackOfBone[b] = t;
        _restLocal = new double[skeleton.Names.Count][];
        for (int b = 0; b < skeleton.Names.Count; b++)
            _restLocal[b] = skeleton.Parents[b] < 0 ? skeleton.Rest[b] : Cast.ModelCast.Mul(Cast.ModelCast.Invert(skeleton.Rest[skeleton.Parents[b]]), skeleton.Rest[b]);
    }

    /// <summary>Bones the clip animates.</summary>
    public int BoundBones => TrackOfBone.Count(t => t >= 0);

    /// <summary>Bone globals (4×4 row-major) at <paramref name="frame"/> in clip frames (0..FrameBound), clamped.</summary>
    public double[][] Sample(double frame) => Globals(Skeleton.Parents, Locals(frame));

    /// <summary>Bone locals at <paramref name="frame"/>: the clip's TRS for bound bones, the rest local for the others.</summary>
    public double[][] Locals(double frame)
    {
        var (k0, k1, f) = Keys(frame);
        var locals = new double[Skeleton.Names.Count][];
        for (int b = 0; b < locals.Length; b++)
            locals[b] = TrackOfBone[b] is >= 0 and var t ? Local(t, k0, k1, f) : _restLocal[b];
        return locals;
    }

    /// <summary>Globals from locals (parents precede children, as in every skeleton here).</summary>
    public static double[][] Globals(IReadOnlyList<int> parents, double[][] locals)
    {
        var globals = new double[locals.Length][];
        for (int b = 0; b < locals.Length; b++)
            globals[b] = parents[b] < 0 ? locals[b] : Cast.ModelCast.Mul(globals[parents[b]], locals[b]);
        return globals;
    }

    /// <summary>
    /// Whether the clip stores deltas rather than poses: over 90% of its tracks (OffsetHelper aside) have zero
    /// translation at frame 0. Measured on DLTB: 9 of 23,547 bone clips, all named <c>*additive*</c> / <c>*_add</c>.
    /// </summary>
    public bool IsAdditive => _additive ??= DetectAdditive(Clip);
    private bool? _additive;

    public static bool DetectAdditive(Anm2Clip clip)
    {
        uint offset = Anm2Hash.H41("OffsetHelper");
        var tracks = Enumerable.Range(0, clip.TrackCount).Where(t => clip.TrackHashes[t] != offset).ToList();
        if (clip.IsPoseWeights || tracks.Count == 0) return false;
        return tracks.Count(t => clip.Translation(0, t).Length() < 1e-4) > 0.9 * tracks.Count;
    }

    /// <summary>
    /// Lay this clip over <paramref name="locals"/> (another pose's locals, changed in place): the bones it drives
    /// take its pose; an additive clip instead composes its delta after the base local (<c>L = L_base · L_delta</c>,
    /// the delta in the bone's own frame — the engine's order is not proven). Other bones keep the base.
    /// </summary>
    public void Over(double[][] locals, double frame)
    {
        var (k0, k1, f) = Keys(frame);
        bool additive = IsAdditive;
        for (int b = 0; b < locals.Length && b < TrackOfBone.Length; b++)
            if (TrackOfBone[b] is >= 0 and var t)
                locals[b] = additive ? Cast.ModelCast.Mul(locals[b], Local(t, k0, k1, f)) : Local(t, k0, k1, f);
    }

    private (int K0, int K1, float F) Keys(double frame)
    {
        frame = Math.Clamp(frame, 0, Clip.FrameBound);
        int k0 = (int)Math.Floor(frame);
        return (k0, Math.Min(k0 + 1, Clip.FrameBound), (float)(frame - k0));
    }

    private double[] Local(int track, int k0, int k1, float f)
    {
        var q0 = Clip.Rotation(k0, track);
        var q1 = Clip.Rotation(k1, track);
        if (Quaternion.Dot(q0, q1) < 0) q1 = -q1;
        var q = Quaternion.Normalize(Quaternion.Lerp(q0, q1, f));
        var t = Vector3.Lerp(Clip.Translation(k0, track), Clip.Translation(k1, track), f);
        var s = Vector3.Lerp(Clip.Scale(k0, track), Clip.Scale(k1, track), f);
        return Trs(q, t, s);
    }

    /// <summary><c>T·R·S</c> as a 4×4 row-major matrix for column vectors; quaternion xyzw.</summary>
    public static double[] Trs(Quaternion q, Vector3 t, Vector3 s)
    {
        double x = q.X, y = q.Y, z = q.Z, w = q.W;
        return
        [
            (1 - 2 * (y * y + z * z)) * s.X, 2 * (x * y - z * w) * s.Y, 2 * (x * z + y * w) * s.Z, t.X,
            2 * (x * y + z * w) * s.X, (1 - 2 * (x * x + z * z)) * s.Y, 2 * (y * z - x * w) * s.Z, t.Y,
            2 * (x * z - y * w) * s.X, 2 * (y * z + x * w) * s.Y, (1 - 2 * (x * x + y * y)) * s.Z, t.Z,
            0, 0, 0, 1,
        ];
    }
}
