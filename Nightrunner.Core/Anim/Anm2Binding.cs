using System.Text;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;

namespace Nightrunner.Core.Anim;

/// <summary>
/// Track → bone binding. Tracks carry the h41 hash of the bone name (list A); the engine binds each skeleton bone by
/// a linear search of list A (<c>SimpleSampler::AddBone</c> 0x1802bb630), and a miss is 0xFFFF.
/// </summary>
public static class Anm2Hash
{
    /// <summary><c>h = h·41 + c</c> from 0, A–Z lowered, c added as a signed char, u32 wrap.</summary>
    public static uint H41(ReadOnlySpan<byte> name)
    {
        uint h = 0;
        foreach (byte b in name)
        {
            int c = b is >= (byte)'A' and <= (byte)'Z' ? b + 32 : b;
            h = unchecked(h * 41 + (uint)(sbyte)(byte)c);
        }
        return h;
    }

    public static uint H41(string name) => H41(Encoding.Latin1.GetBytes(name));

    /// <summary>
    /// <c>h = h·41 + c</c> without lowering: the face-pose hash (<c>face::impl::GetPoseIndex</c>, the
    /// <c>wrinkles{n}</c> names of <c>FacialExpressionManager::ProcessTemplateFile</c>) and list B of pose-weight clips.
    /// </summary>
    public static uint H41Cased(string name)
    {
        uint h = 0;
        foreach (byte b in Encoding.Latin1.GetBytes(name)) h = unchecked(h * 41 + (uint)(sbyte)b);
        return h;
    }

    /// <summary>Tracks that are not skeleton bones (root motion, attach points), for labelling misses.</summary>
    public static readonly string[] SpecialTracks =
    [
        "OffsetHelper", "Holder", "Bone_root", "ActionCenter", "propsholder1", "propsholder2", "propsholder3",
        "r_hand_attach_01", "l_hand_attach_01", "refcamera", "eyecamera",
    ];

    /// <summary>The identity track seen in 19,826 clips; its name is unknown.</summary>
    public const uint UnknownIdentityTrack = 0xFC3A1E27;
}

/// <summary>List A bound to a skeleton: bone index per track (−1 = miss) and the misses by hash, named when known.</summary>
public sealed record Anm2Binding(int[] TrackToBone, (int Track, uint Hash, string? Name)[] Misses)
{
    public int Bound => TrackToBone.Length - Misses.Length;

    public static Anm2Binding Bind(IReadOnlyList<uint> trackHashes, IReadOnlyList<string> boneNames)
    {
        // First bone wins on a (theoretical) hash collision, as a linear search from bone 0 would.
        var byHash = new Dictionary<uint, int>(boneNames.Count);
        for (int i = 0; i < boneNames.Count; i++) byHash.TryAdd(Anm2Hash.H41(boneNames[i]), i);
        var special = Anm2Hash.SpecialTracks.ToDictionary(Anm2Hash.H41, s => s);
        var map = new int[trackHashes.Count];
        var misses = new List<(int, uint, string?)>();
        for (int t = 0; t < map.Length; t++)
        {
            if (byHash.TryGetValue(trackHashes[t], out int b)) map[t] = b;
            else
            {
                map[t] = -1;
                misses.Add((t, trackHashes[t], special.GetValueOrDefault(trackHashes[t])));
            }
        }
        return new Anm2Binding(map, misses.ToArray());
    }

    /// <summary>Bind against a mesh's entities (a skeleton mesh such as <c>sh2_player_tpp_phx_skeleton</c>).</summary>
    public static Anm2Binding Bind(IReadOnlyList<uint> trackHashes, MeshModel skeleton) =>
        Bind(trackHashes, skeleton.Entities.Select(e => e.NameStr).ToArray());

    /// <summary>Bind against a merged model skeleton.</summary>
    public static Anm2Binding Bind(IReadOnlyList<uint> trackHashes, ModelSkeleton skeleton) =>
        Bind(trackHashes, skeleton.Names);
}
