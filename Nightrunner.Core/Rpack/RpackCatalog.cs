using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Nightrunner.Core.Rpack;

/// <summary>One pack in the catalog: its file, where its resources start in the global id space, or why it failed.</summary>
public sealed class PackEntry(int id, string path, string label, long fileSize)
{
    public int Id { get; } = id;
    public string Path { get; } = path;

    /// <summary>Path relative to the assets folder (or the bare file name).</summary>
    public string Label { get; } = label;

    public long FileSize { get; } = fileSize;

    /// <summary>Global id of logical 0. Valid once <see cref="Pack"/> is set.</summary>
    public int Base { get; internal set; }

    public int Count { get; internal set; }
    public RpackFile? Pack { get; internal set; }
    public string? Error { get; internal set; }

    /// <summary>Resource count per logical type.</summary>
    public IReadOnlyDictionary<byte, int> TypeCounts { get; internal set; } = new Dictionary<byte, int>();

    public bool IsIndexed => Pack is not null;
    public override string ToString() => Label;
}

/// <summary>A search hit: the global id plus the pack and logical index it resolves to.</summary>
public readonly record struct Hit(int Gid, PackEntry Entry, int Index);

/// <summary>
/// Lazily-built index of every logical resource in every loaded pack, addressed by a global id
/// (gid = pack.Base + logicalIndex). Ported from nightrunner/gui/catalog.py.
/// </summary>
/// <remarks>
/// Packs are opened (mmap + table parse only) on the thread pool; <see cref="PackIndexed"/> fires per pack, from
/// a worker thread. Search is pure byte work over each pack's pre-lowered name blob and is safe to run on a
/// worker while more packs are still arriving — it only sees what has been indexed so far.
/// </remarks>
public sealed class RpackCatalog : IDisposable
{
    private readonly object _lock = new();
    private readonly List<PackEntry> _packs = [];
    private readonly List<PackEntry> _ordered = [];   // indexed packs, ascending Base
    private int _total;
    private bool _closed;
    private bool _view;   // a Subset: shares its packs with the catalog it came from and never loads or disposes any

    /// <summary>Fires once per pack, on a worker thread, whether it indexed or failed.</summary>
    public event Action<PackEntry>? PackIndexed;

    /// <summary>Fires after each pack with (indexed or failed, queued total).</summary>
    public event Action<int, int>? Progress;

    public int ResourceCount { get { lock (_lock) return _total; } }

    public IReadOnlyList<PackEntry> Packs { get { lock (_lock) return _packs.ToArray(); } }

    /// <summary>Snapshot of the packs that have finished indexing, in load order.</summary>
    public PackEntry[] IndexedPacks
    {
        get
        {
            lock (_lock) return _ordered.OrderBy(p => p.Id).ToArray();
        }
    }

    /// <summary>
    /// A read-only view of the packs indexed so far that <paramref name="include"/> accepts: same entries, same global
    /// ids, so a gid from either resolves the same. Lookups and searches see only the included packs. The view
    /// neither loads nor disposes packs; it is valid while this catalog is.
    /// </summary>
    public RpackCatalog Subset(Func<PackEntry, bool> include)
    {
        var view = new RpackCatalog { _view = true };
        lock (_lock)
        {
            view._packs.AddRange(_packs.Where(include));
            view._ordered.AddRange(_ordered.Where(include));
            view._total = _total;
        }
        return view;
    }

    /// <summary>Queue packs for background indexing. Duplicate paths are ignored.</summary>
    public Task LoadAsync(IEnumerable<string> paths, string? assetsRoot = null, CancellationToken ct = default)
    {
        if (_view) throw new InvalidOperationException("a catalog subset cannot load packs");
        List<PackEntry> fresh = [];
        lock (_lock)
        {
            var known = _packs.Select(p => p.Path.ToLowerInvariant()).ToHashSet();
            foreach (var path in paths)
            {
                var full = System.IO.Path.GetFullPath(path);
                if (!known.Add(full.ToLowerInvariant())) continue;
                long size = 0;
                try { size = new FileInfo(full).Length; } catch (IOException) { }
                var e = new PackEntry(_packs.Count, full, RelativeLabel(full, assetsRoot), size);
                _packs.Add(e);
                fresh.Add(e);
            }
        }
        if (fresh.Count == 0) return Task.CompletedTask;

        int total = fresh.Count;
        int done = 0;
        return Task.Run(() => Parallel.ForEach(
            fresh,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
            e =>
            {
                if (_closed || ct.IsCancellationRequested) return;
                Index(e);
                PackIndexed?.Invoke(e);
                Progress?.Invoke(Interlocked.Increment(ref done), total);
            }), ct);
    }

    private static string RelativeLabel(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return System.IO.Path.GetFileName(path);
        var rel = System.IO.Path.GetRelativePath(root, path);
        return rel.StartsWith("..") ? System.IO.Path.GetFileName(path) : rel.Replace('\\', '/');
    }

    private void Index(PackEntry e)
    {
        RpackFile pack;
        try
        {
            pack = RpackFile.Open(e.Path);
        }
        catch (Exception ex)
        {
            e.Error = $"{ex.GetType().Name}: {ex.Message}";
            return;
        }
        var counts = new Dictionary<byte, int>();
        foreach (var lg in pack.Logicals)
        {
            counts.TryGetValue(lg.Type, out int n);
            counts[lg.Type] = n + 1;
        }
        lock (_lock)
        {
            if (_closed)
            {
                pack.Dispose();
                return;
            }
            e.Pack = pack;
            e.Base = _total;
            e.Count = pack.Count;
            e.TypeCounts = counts.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value);
            _total += pack.Count;
            _ordered.Add(e);
        }
    }

    // ---- addressing ----------------------------------------------------------------------------------------

    /// <summary>The pack and logical index a global id resolves to.</summary>
    public (PackEntry Entry, int Index) Split(int gid)
    {
        lock (_lock)
        {
            int lo = 0, hi = _ordered.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                var e = _ordered[mid];
                if (gid < e.Base) hi = mid - 1;
                else if (gid >= e.Base + e.Count) lo = mid + 1;
                else return (e, gid - e.Base);
            }
        }
        throw new ArgumentOutOfRangeException(nameof(gid), $"global id {gid} is not in any indexed pack");
    }

    public string Name(int gid)
    {
        var (e, i) = Split(gid);
        return e.Pack!.Name(i);
    }

    public byte Type(int gid)
    {
        var (e, i) = Split(gid);
        return e.Pack!.Logicals[i].Type;
    }

    /// <summary>
    /// Global ids of every resource with exactly this name, optionally of one type. Unlike
    /// <see cref="Search"/> this is an exact match, which is what a name from outside the packs — an SDB texture
    /// binding, a .model reference — needs in order to find what provides it.
    /// </summary>
    public int[] Lookup(string name, byte? type = null)
    {
        if (string.IsNullOrEmpty(name)) return [];
        var want = RpackFile.EngineFold(Encoding.UTF8.GetBytes(name));
        List<int> hits = [];
        foreach (var e in IndexedPacks)
        {
            var pack = e.Pack!;
            foreach (int i in pack.IndicesOf(want))
                if (type is not { } t || pack.Logicals[i].Type == t) hits.Add(e.Base + i);
        }
        return [.. hits];
    }

    /// <summary>Resource counts per logical type over every indexed pack.</summary>
    public Dictionary<byte, int> TypeCounts()
    {
        var all = new Dictionary<byte, int>();
        foreach (var e in IndexedPacks)
            foreach (var (t, n) in e.TypeCounts)
            {
                all.TryGetValue(t, out int have);
                all[t] = have + n;
            }
        return all.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    // ---- search --------------------------------------------------------------------------------------------

    /// <summary>
    /// Global ids matching every whitespace-separated word of <paramref name="text"/> (case-insensitive
    /// substring, engine folding), optionally restricted to a type and a set of packs. A `#123` word matches the
    /// logical index 123. Results are in pack load order, then logical order. Nothing is capped.
    /// </summary>
    public SearchResult Search(string text, byte? type = null, IReadOnlyCollection<int>? packIds = null,
                               CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var (words, indices, malformed) = ParseQuery(text);
        var packs = IndexedPacks;
        if (packIds is { Count: > 0 })
        {
            var want = packIds.ToHashSet();
            packs = packs.Where(p => want.Contains(p.Id)).ToArray();
        }
        if (malformed) return new SearchResult([], sw.Elapsed, packs.Length);

        var parts = new ConcurrentDictionary<int, int[]>();
        Parallel.ForEach(packs, new ParallelOptions { CancellationToken = ct }, e =>
            parts[e.Id] = MatchPack(e, words, indices, type, ct));

        int n = 0;
        foreach (var e in packs) n += parts[e.Id].Length;
        var gids = new int[n];
        int at = 0;
        foreach (var e in packs)
        {
            var hits = parts[e.Id];
            Array.Copy(hits, 0, gids, at, hits.Length);
            at += hits.Length;
        }
        return new SearchResult(gids, sw.Elapsed, packs.Length);
    }

    private static int[] MatchPack(PackEntry e, byte[][] words, int[] indices, byte? type, CancellationToken ct)
    {
        var pack = e.Pack!;
        var logicals = pack.Logicals;
        var hits = new List<int>(indices.Length > 0 ? indices.Length : Math.Min(logicals.Length, 1024));
        if (indices.Length > 0)
        {
            foreach (int i in indices)
            {
                if ((uint)i >= (uint)logicals.Length) continue;
                if (type is { } t && logicals[i].Type != t) continue;
                if (!Matches(pack, i, words)) continue;
                hits.Add(e.Base + i);
            }
            hits.Sort();
            return hits.ToArray();
        }
        for (int i = 0; i < logicals.Length; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            if (type is { } t && logicals[i].Type != t) continue;
            if (!Matches(pack, i, words)) continue;
            hits.Add(e.Base + i);
        }
        return hits.ToArray();
    }

    private static bool Matches(RpackFile pack, int index, byte[][] words)
    {
        if (words.Length == 0) return true;
        var name = pack.NameBytesLower(index);
        foreach (var w in words)
            if (name.IndexOf(w) < 0) return false;
        return true;
    }

    /// <summary>(search words as lowered bytes, logical indices from `#123` tokens, a malformed `#` token seen).</summary>
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
                words.Add(RpackFile.EngineFold(Encoding.UTF8.GetBytes(token)));
            }
        }
        return (words.ToArray(), indices.ToArray(), bad);
    }

    public void Dispose()
    {
        if (_view) return;
        PackEntry[] packs;
        lock (_lock)
        {
            _closed = true;
            packs = _packs.ToArray();
        }
        foreach (var e in packs)
        {
            e.Pack?.Dispose();
            e.Pack = null;
        }
    }
}

/// <summary>Global ids of a search, how long it took, and how many packs it covered.</summary>
public sealed record SearchResult(int[] Gids, TimeSpan Elapsed, int PackCount)
{
    public int Count => Gids.Length;
}
