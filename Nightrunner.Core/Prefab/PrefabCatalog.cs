using System.Collections.Concurrent;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Core.Prefab;

/// <summary>Where a catalog prefab comes from: a binary <c>Prefabs</c> resource of an rpack, or a text <c>.prefab</c> pak member.</summary>
public enum PrefabSource { Rpack, Pak }

/// <summary>
/// One prefab of one pack or pak member. <see cref="Wins"/> is false when another provider registers the same name
/// first (<see cref="ShadowedBy"/>). A text entry reads its member on first use of <see cref="Document"/> /
/// <see cref="Root"/>; a member that does not read throws <see cref="PrefabFormatException"/> there (and keeps
/// <see cref="Error"/>).
/// </summary>
public sealed class PrefabEntry
{
    private readonly Func<PrefabDocument>? _load;
    private readonly Lock _lock = new();
    private PrefabDocument? _doc;
    private readonly PrefabRoot? _root;

    /// <summary>A binary prefab: root <paramref name="Index"/> of a decoded <c>Prefabs</c> resource of <paramref name="Pack"/>.</summary>
    public PrefabEntry(string Name, string Pack, int Index, PrefabRoot Root, PrefabDocument Document)
    {
        this.Name = Name;
        this.Pack = Pack;
        this.Index = Index;
        _root = Root;
        _doc = Document;
        Source = PrefabSource.Rpack;
    }

    /// <summary>A text prefab: pak member <paramref name="member"/>, read on first use; <paramref name="index"/> 0 is the file's own prefab.</summary>
    public PrefabEntry(string name, string pak, string member, Func<PrefabDocument> load, int index = 0)
    {
        Name = name;
        Pack = pak;
        Member = member;
        Index = index;
        _load = load;
        Source = PrefabSource.Pak;
    }

    internal PrefabEntry(string name, string pak, string member, PrefabDocument loaded, int index)
    {
        Name = name;
        Pack = pak;
        Member = member;
        Index = index;
        _doc = loaded;
        Source = PrefabSource.Pak;
    }

    public string Name { get; }

    /// <summary>The rpack label (binary) or the pak file path (text).</summary>
    public string Pack { get; }

    /// <summary>Root index in its <c>Prefabs</c> resource (binary), or in its file (text: 0 = the file's prefab, then sub-prefabs).</summary>
    public int Index { get; }

    public PrefabSource Source { get; }

    /// <summary>Text prefab: the pak member path; null for binary.</summary>
    public string? Member { get; }

    public bool Wins { get; internal set; } = true;

    /// <summary>The entry that registers this name first (the one the engine uses), when this one is shadowed.</summary>
    public PrefabEntry? ShadowedBy { get; internal set; }

    /// <summary>Why the text member did not read (after a failed load), else null.</summary>
    public string? Error { get; private set; }

    public bool IsLoaded => _doc is not null;

    public PrefabDocument Document
    {
        get
        {
            if (_doc is not null) return _doc;
            lock (_lock)
            {
                if (_doc is not null) return _doc;
                if (Error is not null) throw new PrefabFormatException(Error);
                try
                {
                    var doc = _load!();
                    Loaded?.Invoke(this, doc);
                    return _doc = doc;
                }
                catch (Exception e) when (e is PrefabFormatException or ModelFormatException or InvalidDataException or IOException)
                {
                    Error = $"{Member}: {e.Message}";
                    throw new PrefabFormatException(Error);
                }
            }
        }
    }

    public PrefabRoot Root => _root ?? Document.Prefabs[Index];

    /// <summary>The root, or null with <paramref name="error"/> when the text member does not read.</summary>
    public PrefabRoot? TryRoot(out string? error)
    {
        try
        {
            error = null;
            return Root;
        }
        catch (PrefabFormatException e)
        {
            error = e.Message;
            return null;
        }
    }

    internal Action<PrefabEntry, PrefabDocument>? Loaded;

    public override string ToString() => $"{Name} ({(Source == PrefabSource.Rpack ? Pack : Member)})";
}

/// <summary>
/// Every prefab of the open game: the binary <c>Prefabs</c> resource of each loaded pack decoded (in parallel), then
/// every text <c>*.prefab</c> member of the paks (listed from the pak directories, each read on first use).
/// <para>
/// Registration, as the engine does it (prefab notes §10, read in the DLTB engine; assumed for Dying Light 2, whose
/// binary is not analysed): binary prefabs register at pack load, in pack order, and the <b>first registration
/// wins</b> (the engine's pack registration order is not known, PF4; this order is the catalog's). A text prefab is
/// only opened when a lookup misses the registry (<c>LoadPrefabFromFile</c>), so a binary prefab of the same name
/// shadows it. Between paks, the later pak's member wins, as for <c>.model</c> (shipped basenames are unique across
/// data0/data1 anyway). Every provider is kept and marked, none is hidden.
/// </para>
/// Sub-prefabs of a text file (top-level keys besides <c>__filename__</c>) are registered when their file is read.
/// </summary>
public sealed class PrefabCatalog
{
    public IReadOnlyList<PrefabEntry> Prefabs { get; }
    public IReadOnlyList<string> Errors { get; }
    private readonly Dictionary<string, PrefabEntry> _byName;
    private readonly ConcurrentDictionary<string, PrefabEntry> _subs = new(StringComparer.Ordinal);

    /// <summary>Binary and text entries (text: file-level members; sub-prefabs join <see cref="SubPrefabs"/> when read).</summary>
    public int BinaryCount { get; }
    public int TextCount => Prefabs.Count - BinaryCount;

    /// <summary>Sub-prefabs of the text files read so far.</summary>
    public IReadOnlyList<PrefabEntry> SubPrefabs => _subs.Values.ToList();

    private PrefabCatalog(List<PrefabEntry> binary, List<PrefabEntry> text, List<string> errors)
    {
        Prefabs = [.. binary, .. text];
        BinaryCount = binary.Count;
        Errors = errors;
        _byName = new Dictionary<string, PrefabEntry>(StringComparer.Ordinal);
        foreach (var p in binary)
            if (!_byName.TryAdd(p.Name, p)) Shadow(p, _byName[p.Name]);
        var textWinner = new Dictionary<string, PrefabEntry>(StringComparer.Ordinal);
        foreach (var p in text)
        {
            if (textWinner.TryGetValue(p.Name, out var earlier)) Shadow(earlier, p);   // a later pak overrides
            textWinner[p.Name] = p;
        }
        foreach (var p in text)
        {
            if (!ReferenceEquals(textWinner[p.Name], p)) continue;
            if (!_byName.TryAdd(p.Name, p)) Shadow(p, _byName[p.Name]);                 // the registry is asked first
            p.Loaded = OnLoaded;
        }
    }

    private static void Shadow(PrefabEntry loser, PrefabEntry winner)
    {
        loser.Wins = false;
        loser.ShadowedBy = winner;
    }

    private void OnLoaded(PrefabEntry file, PrefabDocument doc)
    {
        for (int i = 1; i < doc.Prefabs.Count; i++)
        {
            var name = doc.Prefabs[i].Name!;
            var sub = new PrefabEntry(name, file.Pack, file.Member!, doc, i);
            if (_byName.TryGetValue(name, out var reg)) Shadow(sub, reg);
            else if (!_subs.TryAdd(name, sub)) Shadow(sub, _subs[name]);
        }
    }

    /// <summary>Binary prefabs of the packs only (the catalog as it was before text prefabs).</summary>
    public static PrefabCatalog Build(RpackCatalog catalog) => Build(catalog, null);

    /// <summary>
    /// Binary prefabs of the packs, then the text <c>.prefab</c> members of <paramref name="paks"/> (data0 first; may be
    /// null). Only the pak directories are read here; a text member is read when its entry is first used.
    /// </summary>
    public static PrefabCatalog Build(RpackCatalog catalog, ModelCatalog? paks)
    {
        var packs = catalog.IndexedPacks.Where(e => PrefabContainer.ResourcesIn(e.Pack!).Length > 0).ToList();
        var docs = new ConcurrentDictionary<int, (PackEntry Entry, List<PrefabDocument> Docs, string? Error)>();
        Parallel.For(0, packs.Count, k =>
        {
            var entry = packs[k];
            var list = new List<PrefabDocument>();
            try
            {
                foreach (int i in PrefabContainer.ResourcesIn(entry.Pack!))
                    list.Add(PrefabDecoder.Decode(PrefabContainer.Read(entry.Pack!, i)));
                docs[k] = (entry, list, null);
            }
            catch (Exception e) when (e is PrefabFormatException or RpackFormatException or InvalidDataException)
            {
                docs[k] = (entry, list, $"{entry.Label}: {e.Message}");
            }
        });
        var binary = new List<PrefabEntry>();
        var errors = new List<string>();
        for (int k = 0; k < packs.Count; k++)
        {
            var (entry, list, error) = docs[k];
            if (error is not null) errors.Add(error);
            foreach (var doc in list)
                foreach (var root in doc.Prefabs)
                    binary.Add(new PrefabEntry(root.Name ?? $"#{root.Index}", entry.Label, root.Index, root, doc));
        }
        var text = new List<PrefabEntry>();
        foreach (var path in paks?.PakPaths ?? [])
        {
            PakIndex pak;
            try { pak = paks!.Pak(path); }
            catch (KeyNotFoundException) { continue; }   // the pak did not open (ModelCatalog.Errors says why)
            foreach (var m in pak.Members)
                if (m.Name.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    var member = m;
                    string name = NameOf(m.Name);
                    text.Add(new PrefabEntry(name, path, m.Name, () => PrefabTextReader.Read(pak.Read(member, 256 << 20), name, Path.GetFileName(path), member.Name)));
                }
        }
        foreach (var e in errors) Log.Warn("prefab", e);
        return new PrefabCatalog(binary, text, errors);
    }

    private Dictionary<string, List<PresetSet>>? _presetSets;
    private readonly Lock _presetLock = new();

    /// <summary>
    /// Preset <paramref name="preset"/> of group <paramref name="group"/> in the class presets of prefab
    /// <paramref name="className"/> (every binary resource's <c>CRttiClassPresets</c>, in catalog order; matched by
    /// key, then by name), or null. Text preset files (<c>.pre</c>) are not read.
    /// </summary>
    public Preset? Preset(string className, string group, string preset)
    {
        lock (_presetLock)
            _presetSets ??= Prefabs.Take(BinaryCount).Select(p => p.Document).Distinct()
                .SelectMany(d => d.PresetSets).Where(s => s.ClassName is not null)
                .GroupBy(s => s.ClassName!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        if (!_presetSets.TryGetValue(className, out var sets)) return null;
        foreach (var s in sets)
            foreach (var g in s.Groups)
                if (g.Key == group || g.Name == group)
                    if ((g.Presets.FirstOrDefault(p => p.Key == preset) ?? g.Presets.FirstOrDefault(p => p.Name == preset)) is { } hit)
                        return hit;
        return null;
    }

    /// <summary>The name a text member registers under: its file name, lowercase, without <c>.prefab</c> (engine lookup).</summary>
    public static string NameOf(string member)
    {
        var s = member.Replace('\\', '/');
        s = s[(s.LastIndexOf('/') + 1)..];
        if (s.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) s = s[..^7];
        return s.ToLowerInvariant();
    }

    /// <summary>
    /// The registered prefab of a name (engine lookup: lowercase, no <c>.prefab</c>/<c>.eds</c>), or null: a binary
    /// prefab, else a text member, else a sub-prefab of a text file read so far.
    /// </summary>
    public PrefabEntry? Find(string name)
    {
        string key = name.Trim();
        foreach (var ext in new[] { ".prefab", ".eds" })
            if (key.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) key = key[..^ext.Length];
        return _byName.GetValueOrDefault(key) ?? _byName.GetValueOrDefault(key.ToLowerInvariant())
               ?? _subs.GetValueOrDefault(key) ?? _subs.GetValueOrDefault(key.ToLowerInvariant());
    }
}
