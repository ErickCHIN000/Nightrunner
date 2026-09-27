using System.Collections.Concurrent;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Core.Anim;

/// <summary>
/// Every mesh of the open game by bone: the h41 hash of each entity name → the meshes carrying it. For clips that
/// bind no character rig (a gate, a chest, a cable, a screwdriver): their tracks name the object's own entities, so
/// the mesh binding the most of them is the one to play them on. Built from the image and fixups parts only
/// (no vertex data); DL2's 33,728 meshes index in a few seconds.
/// </summary>
public sealed class AnimObjects
{
    private readonly Dictionary<uint, int[]> _byBone;
    private readonly ConcurrentDictionary<int, int> _entities;

    public int Meshes => _entities.Count;

    private AnimObjects(Dictionary<uint, int[]> byBone, ConcurrentDictionary<int, int> entities)
    {
        _byBone = byBone;
        _entities = entities;
    }

    public static AnimObjects Build(RpackCatalog catalog, CancellationToken ct = default)
    {
        var pairs = new ConcurrentBag<(uint Hash, int Gid)>();
        var entities = new ConcurrentDictionary<int, int>();
        var work = catalog.IndexedPacks.SelectMany(e => Enumerable.Range(0, e.Pack!.Count)
            .Where(i => e.Pack!.Logicals[i].Type == 0x10).Select(i => (e, i))).ToList();
        Parallel.ForEach(work, new ParallelOptions { CancellationToken = ct }, w =>
        {
            var (e, i) = w;
            MeshModel mesh;
            try
            {
                var p = MeshDecoder.ReadParts(e.Pack!, i);
                mesh = MeshDecoder.Decode(p.Name, p.Image!, p.Fixups!);
            }
            catch (Exception ex) when (ex is MeshFormatException or MeshUnsupportedException or RpackFormatException or ArgumentException or IndexOutOfRangeException) { return; }
            int gid = e.Base + i;
            entities[gid] = mesh.Entities.Length;
            foreach (var ent in mesh.Entities.DistinctBy(x => x.NameStr)) pairs.Add((Anm2Hash.H41(ent.Name), gid));
        });
        var byBone = pairs.GroupBy(p => p.Hash).ToDictionary(g => g.Key, g => g.Select(p => p.Gid).Order().ToArray());
        return new AnimObjects(byBone, entities);
    }

    /// <summary>
    /// The mesh binding the most of <paramref name="tracks"/>: among equal counts the one sharing the most name words
    /// with <paramref name="clip"/>, then the one with the fewest entities, then the first registered.
    /// Null when no mesh binds any track. <paramref name="include"/> limits the meshes considered (the viewer's stock
    /// view leaves the mods' meshes out); <paramref name="catalog"/> names them.
    /// </summary>
    public (int Gid, int Bound)? Best(uint[] tracks, string clip, RpackCatalog catalog, Func<int, bool>? include = null)
    {
        var count = new Dictionary<int, int>();
        foreach (uint t in tracks.Distinct())
            if (_byBone.TryGetValue(t, out var gids))
                foreach (int g in gids)
                    if (include is null || include(g)) count[g] = count.GetValueOrDefault(g) + 1;
        if (count.Count == 0) return null;
        int best = count.Values.Max();
        var words = Words(clip).ToHashSet();
        var pick = count.Where(kv => kv.Value == best)
            .Select(kv => (Gid: kv.Key, Name: catalog.Name(kv.Key)))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.MinBy(x => x.Gid))
            .OrderByDescending(x => Words(x.Name).Count(words.Contains))
            .ThenBy(x => _entities.GetValueOrDefault(x.Gid))
            .ThenBy(x => x.Gid)
            .First();
        return (pick.Gid, best);
    }

    private static readonly HashSet<string> Noise = ["obj", "syncaction", "interaction", "anim", "a", "b", "c", "lod", "lod0", "lod1"];

    private static IEnumerable<string> Words(string name) =>
        name.ToLowerInvariant().Split(['_', '.', '/', '\\', ' '], StringSplitOptions.RemoveEmptyEntries).Where(w => !Noise.Contains(w) && !char.IsDigit(w[0]));
}
