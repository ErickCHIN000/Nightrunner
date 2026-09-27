using System.Collections.Concurrent;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Core.Model;

/// <summary>
/// "Used by" for materials: which meshes carry a material in their class-11 table (skin-only entries included) and
/// which <c>.model</c> documents name it (materialsData or materialsResources). Built once per session by a
/// cancellable background scan that reads only the image and fixups parts of each mesh.
/// </summary>
public sealed class MaterialUsage
{
    private readonly Dictionary<string, int[]> _meshes;
    private readonly Dictionary<string, ModelEntry[]> _models;

    public int MeshesScanned { get; }
    public int ModelsScanned { get; }
    public TimeSpan Elapsed { get; }
    public IReadOnlyList<string> Errors { get; }

    private MaterialUsage(Dictionary<string, int[]> meshes, Dictionary<string, ModelEntry[]> models, int meshCount,
                          int modelCount, TimeSpan elapsed, List<string> errors)
    {
        _meshes = meshes;
        _models = models;
        MeshesScanned = meshCount;
        ModelsScanned = modelCount;
        Elapsed = elapsed;
        Errors = errors;
    }

    /// <summary>Mesh gids whose material table names the material (case-insensitive, ".mat" optional).</summary>
    public int[] MeshesOf(string material) => _meshes.GetValueOrDefault(Key(material)) ?? [];

    public ModelEntry[] ModelsOf(string material) => _models.GetValueOrDefault(Key(material)) ?? [];

    private static string Key(string name)
    {
        var k = name.Trim().ToLowerInvariant();
        return k.EndsWith(".mat") ? k[..^4] : k;
    }

    public static MaterialUsage Scan(RpackCatalog catalog, ModelCatalog? models, CancellationToken ct = default,
                                     IProgress<int>? progress = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var meshes = new ConcurrentDictionary<string, ConcurrentBag<int>>();
        var errors = new ConcurrentBag<string>();
        int scanned = 0;
        foreach (var entry in catalog.IndexedPacks)
        {
            var pack = entry.Pack!;
            var idx = Enumerable.Range(0, pack.Count).Where(i => pack.Logicals[i].Type == 0x10).ToArray();
            Parallel.ForEach(idx, new ParallelOptions { CancellationToken = ct }, i =>
            {
                try
                {
                    foreach (var name in MeshDecoder.MaterialTable(pack, i).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                        meshes.GetOrAdd(Key(name), _ => []).Add(entry.Base + i);
                }
                catch (Exception e) when (e is MeshFormatException or MeshUnsupportedException or RpackFormatException)
                {
                    errors.Add($"{pack.Name(i)}: {e.Message}");
                }
                if (Interlocked.Increment(ref scanned) % 2000 == 0) progress?.Report(scanned);
            });
        }
        var byModel = new Dictionary<string, List<ModelEntry>>();
        int modelCount = 0;
        foreach (var m in models?.Models ?? [])
        {
            ct.ThrowIfCancellationRequested();
            ModelDocument doc;
            try { doc = models!.Load(m); }
            catch (ModelFormatException e) { errors.Add(e.Message); continue; }
            modelCount++;
            var names = doc.Slots.SelectMany(s => s.Meshes).SelectMany(r =>
                r.MaterialsData.Select(d => d.Name).Concat(r.MaterialsResources.SelectMany(g => g.Resources).Select(x => x.Name)));
            foreach (var n in names.Where(n => n.Length > 0).Select(Key).Distinct())
            {
                if (!byModel.TryGetValue(n, out var list)) byModel[n] = list = [];
                list.Add(m);
            }
        }
        return new MaterialUsage(meshes.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().Order().ToArray()),
                                 byModel.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()), scanned, modelCount,
                                 sw.Elapsed, [.. errors]);
    }
}
