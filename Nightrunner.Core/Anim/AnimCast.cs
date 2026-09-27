using Nightrunner.Core.Cast;
using Nightrunner.Core.Model;
using CastAnimation = Nightrunner.Core.Cast.Animation;

namespace Nightrunner.Core.Anim;

/// <summary>
/// A SeqTrack as a Cast animation: one curve per animated channel of every track — <c>rq</c> (quaternion xyzw),
/// <c>tx ty tz</c>, <c>sx sy sz</c> — keyed on every clip frame of the track's range (re-based to 0), mode
/// <c>absolute</c>, at the track's fps. Values are the clip's parent-local TRS in native coordinates (Y up), the same
/// space as the skeleton of a Nightrunner mesh or model Cast, so the animation applies to that armature by bone name.
/// Tracks are named by the bones they bind to; a track no given name matches keeps its hash (<c>0x%08X</c>).
/// </summary>
public static class AnimCast
{
    public static CastFile Build(Anm2Clip clip, IReadOnlyList<string> boneNames, float fps, float startFrame, float endFrame,
                                 bool looping = false)
    {
        var names = new Dictionary<uint, string>();
        foreach (var n in boneNames.Concat(Anm2Hash.SpecialTracks)) names.TryAdd(Anm2Hash.H41(n), n);
        int from = (int)Math.Clamp(Math.Floor(Math.Min(startFrame, endFrame)), 0, clip.FrameBound);
        int to = (int)Math.Clamp(Math.Ceiling(Math.Max(startFrame, endFrame)), 0, clip.FrameBound);
        if (to <= from) (from, to) = (0, clip.FrameBound);
        int n0 = to - from + 1;
        var keys = Enumerable.Range(0, n0).Select(i => (uint)i).ToArray();

        var cast = new CastFile();
        var root = cast.CreateRoot();
        var meta = root.CreateMetadata();
        meta.SetUpAxis("y");
        meta.SetSoftware(CastExport.Software);
        CastAnimation anim = root.CreateAnimation();
        anim.SetFramerate(fps > 0 ? fps : 30);
        anim.SetLooping(looping);
        for (int t = 0; t < clip.TrackCount; t++)
        {
            string name = names.GetValueOrDefault(clip.TrackHashes[t]) ?? $"0x{clip.TrackHashes[t]:X8}";
            var rq = new float[n0 * 4];
            var tr = new float[3][];
            var sc = new float[3][];
            for (int c = 0; c < 3; c++) { tr[c] = new float[n0]; sc[c] = new float[n0]; }
            for (int i = 0; i < n0; i++)
            {
                var q = clip.Rotation(from + i, t);
                rq[i * 4] = q.X; rq[i * 4 + 1] = q.Y; rq[i * 4 + 2] = q.Z; rq[i * 4 + 3] = q.W;
                var p = clip.Translation(from + i, t);
                var s = clip.Scale(from + i, t);
                tr[0][i] = p.X; tr[1][i] = p.Y; tr[2][i] = p.Z;
                sc[0][i] = s.X; sc[1][i] = s.Y; sc[2][i] = s.Z;
            }
            Curve(anim, name, "rq", keys, rq, vec4: true);
            for (int c = 0; c < 3; c++)
            {
                Curve(anim, name, "t" + "xyz"[c], keys, tr[c], vec4: false);
                Curve(anim, name, "s" + "xyz"[c], keys, sc[c], vec4: false);
            }
        }
        return cast;
    }

    /// <summary>
    /// The same animation with the skeleton it plays on as a Cast model (<see cref="ModelCast.WriteSkeleton"/>), both
    /// named <paramref name="name"/> — what a .glb needs, since glTF channels target nodes.
    /// </summary>
    public static CastFile Scene(Anm2Clip clip, ModelSkeleton skeleton, string name, float fps, float startFrame, float endFrame,
                                 bool looping = false)
    {
        var cast = Build(clip, skeleton.Names, fps, startFrame, endFrame, looping);
        var root = (Root)cast.Roots()[0];
        var mdl = root.CreateModel();
        mdl.SetName(name);
        ModelCast.WriteSkeleton(mdl, skeleton);
        root.ChildOfType<CastAnimation>()!.SetName(name);
        return cast;
    }

    private static void Curve(CastAnimation anim, string node, string property, uint[] keys, float[] values, bool vec4)
    {
        var c = anim.CreateCurve();
        c.SetNodeName(node);
        c.SetKeyPropertyName(property);
        c.SetKeyFrameBuffer(keys);
        if (vec4) c.SetVec4KeyValueBuffer(values);
        else c.SetFloatKeyValueBuffer(values);
        c.SetMode("absolute");
    }
}
