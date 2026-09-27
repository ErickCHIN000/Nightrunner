using System.Diagnostics;
using System.Text;

namespace Nightrunner.Core.Sdb;

/// <summary>Materials matching a query, and how long finding them took.</summary>
public sealed record SdbSearchResult(int[] Materials, TimeSpan Elapsed);

/// <summary>
/// Everything the panels need about one open database, computed once and then never changed.
/// </summary>
/// <remarks>
/// Built entirely on a worker and published by reference assignment, so readers need no lock: a panel that holds
/// a snapshot holds a consistent one, and switching dx11↔dx12 or games simply hands out a different instance.
///
/// Names live as one lowered byte blob with start/length arrays, the same shape the pack catalog searches
/// (<see cref="Rpack.RpackFile.NameBlobLower"/>) — matching is a span scan that allocates nothing per row.
/// </remarks>
public sealed class SdbIndex
{
    public SdbFile File { get; }

    /// <summary>Lowered material names, concatenated; the search scans this and never allocates strings.</summary>
    private readonly byte[] _lower;
    private readonly int[] _start;
    private readonly int[] _length;

    /// <summary>Per material: the preset its route names, or "" when it has no route.</summary>
    public string[] Preset { get; }

    /// <summary>Preset name → the materials using it.</summary>
    public IReadOnlyDictionary<string, int[]> MaterialsByPreset { get; }

    /// <summary>How many materials a route named a preset for. One shipped material is a nameless sentinel.</summary>
    public int Routed { get; }

    public TimeSpan BuildTime { get; }

    public int Count => _start.Length;

    public string Name(int material) => File.MaterialName(material);

    private SdbIndex(SdbFile file, byte[] lower, int[] start, int[] length, string[] preset,
                     Dictionary<string, int[]> byPreset, int routed, TimeSpan buildTime)
    {
        Routed = routed;
        File = file;
        _lower = lower;
        _start = start;
        _length = length;
        Preset = preset;
        MaterialsByPreset = byPreset;
        BuildTime = buildTime;
    }

    /// <summary>
    /// Read every material name and route once. Cheap enough to do in the same task that opens the file —
    /// one pass over the routes with a token cache, which is where the preset column comes from.
    /// </summary>
    public static SdbIndex Build(SdbFile file, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        int n = file.MaterialCount;

        var start = new int[n];
        var length = new int[n];
        var lower = new byte[file.Table(SdbFormat.TableMaterialNames).Bytes];
        int at = 0;
        for (int i = 0; i < n; i++)
        {
            if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            var name = file.Record(SdbFormat.TableMaterialNames, i);
            start[i] = at;
            length[i] = name.Length;
            for (int k = 0; k < name.Length; k++)
            {
                byte b = name[k];
                lower[at + k] = b is >= 0x41 and <= 0x5A ? (byte)(b + 0x20) : b;
            }
            at += name.Length;
        }

        var preset = new string[n];
        Array.Fill(preset, "");
        var seen = new bool[n];              // not "preset is empty": one shipped material has a nameless route
        int routed = 0;
        var tokens = new Dictionary<int, string>();
        var byPreset = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int r = 0; r < file.RouteCount; r++)
        {
            if ((r & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            var row = file.RouteRow(r);
            if ((uint)row.Material >= (uint)n || seen[row.Material]) continue;
            seen[row.Material] = true;
            routed++;
            if (!tokens.TryGetValue(row.Tokens, out var name))
            {
                string all = file.Text(SdbFormat.TableTokens, row.Tokens);
                int semi = all.IndexOf(';');
                tokens[row.Tokens] = name = semi < 0 ? all : all[..semi];
            }
            preset[row.Material] = name;
            if (!byPreset.TryGetValue(name, out var list)) byPreset[name] = list = [];
            list.Add(row.Material);
        }

        return new SdbIndex(file, lower, start, length, preset,
                            byPreset.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal),
                            routed, sw.Elapsed);
    }

    // ---- search ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Materials whose name contains every whitespace-separated word of <paramref name="text"/>, or the exact
    /// indices named by `#123` words. Nothing is capped.
    /// </summary>
    public SdbSearchResult Search(string text, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var (words, indices, malformed) = ParseQuery(text);
        if (malformed) return new SdbSearchResult([], sw.Elapsed);

        if (indices.Length > 0)
        {
            var picked = new List<int>(indices.Length);
            foreach (int i in indices)
                if ((uint)i < (uint)Count && Matches(i, words))
                    picked.Add(i);
            picked.Sort();
            return new SdbSearchResult([.. picked], sw.Elapsed);
        }

        if (words.Length == 0)
        {
            var all = new int[Count];
            for (int i = 0; i < all.Length; i++) all[i] = i;
            return new SdbSearchResult(all, sw.Elapsed);
        }

        var hits = new List<int>(Math.Min(Count, 1024));
        for (int i = 0; i < Count; i++)
        {
            if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            if (Matches(i, words)) hits.Add(i);
        }
        return new SdbSearchResult([.. hits], sw.Elapsed);
    }

    private bool Matches(int material, byte[][] words)
    {
        if (words.Length == 0) return true;
        var name = _lower.AsSpan(_start[material], _length[material]);
        foreach (var w in words)
            if (name.IndexOf(w) < 0) return false;
        return true;
    }

    /// <summary>(search words as lowered bytes, material indices from `#123` tokens, a malformed `#` seen).</summary>
    public static (byte[][] Words, int[] Indices, bool Malformed) ParseQuery(string text)
    {
        List<byte[]> words = [];
        List<int> indices = [];
        bool bad = false;
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token[0] == '#')
            {
                if (token.Length > 1 && int.TryParse(token.AsSpan(1), out int i)) indices.Add(i);
                else bad = true;
            }
            else
            {
                words.Add(Rpack.RpackFile.EngineFold(Encoding.UTF8.GetBytes(token)));
            }
        }
        return ([.. words], [.. indices], bad);
    }
}
