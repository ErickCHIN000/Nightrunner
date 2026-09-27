using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace Nightrunner.Core.Model;

public sealed class ModelFormatException(string message) : Exception(message);

/// <summary>One member of a PAK (ZIP) archive.</summary>
public sealed record PakMember(int Index, string Name, long Size, long Compressed, uint Crc)
{
    /// <summary>Last path component, lower-case — the key later paks override by.</summary>
    public string Basename => Name.Replace('\\', '/')[(Name.Replace('\\', '/').LastIndexOf('/') + 1)..].ToLowerInvariant();
}

/// <summary>
/// A <c>dataN.pak</c> (a plain ZIP archive), read-only: the central directory up front, members inflated on request.
/// Port of <c>pak/model_json.py</c> <c>PakIndex</c> (read side).
/// </summary>
public sealed class PakIndex : IDisposable
{
    public const int MaxMemberBytes = 16 << 20;
    public static readonly string[] ModelSuffixes = [".model", ".models"];

    private readonly ZipArchive _zip;
    private readonly object _lock = new();
    private readonly Dictionary<string, List<PakMember>> _byName = new(StringComparer.OrdinalIgnoreCase);

    public string Path { get; }
    public IReadOnlyList<PakMember> Members { get; }

    private PakIndex(string path, ZipArchive zip)
    {
        Path = path;
        _zip = zip;
        var members = new List<PakMember>(zip.Entries.Count);
        for (int i = 0; i < zip.Entries.Count; i++)
        {
            var e = zip.Entries[i];
            var m = new PakMember(i, e.FullName, e.Length, e.CompressedLength, e.Crc32);
            members.Add(m);
            var key = m.Name.Replace('\\', '/');
            if (!_byName.TryGetValue(key, out var list)) _byName[key] = list = [];
            list.Add(m);
        }
        Members = members;
    }

    public static PakIndex Open(string path)
    {
        try { return new PakIndex(path, ZipFile.OpenRead(path)); }
        catch (InvalidDataException e) { throw new ModelFormatException($"{path}: not a ZIP/PAK archive ({e.Message})"); }
    }

    public IEnumerable<PakMember> Models() =>
        Members.Where(m => ModelSuffixes.Any(s => m.Name.EndsWith(s, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Member by exact path (either slash, any case), else by unique basename.</summary>
    public PakMember Find(string member)
    {
        var key = member.Replace('\\', '/');
        if (!_byName.TryGetValue(key, out var hits))
        {
            string base_ = key[(key.LastIndexOf('/') + 1)..].ToLowerInvariant();
            hits = Members.Where(m => m.Basename == base_).ToList();
        }
        if (hits.Count == 0) throw new ModelFormatException($"'{member}': no such member in {System.IO.Path.GetFileName(Path)}");
        if (hits.Count > 1)
            throw new ModelFormatException($"'{member}': {hits.Count} members match in {System.IO.Path.GetFileName(Path)}");
        return hits[0];
    }

    public byte[] Read(PakMember m, int maxBytes = MaxMemberBytes)
    {
        if (m.Size > maxBytes) throw new ModelFormatException($"{m.Name}: {m.Size} bytes exceeds the {maxBytes} byte limit");
        lock (_lock)
        {
            using var s = _zip.Entries[m.Index].Open();
            using var ms = new MemoryStream((int)m.Size);
            var buf = new byte[81920];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > maxBytes) throw new ModelFormatException($"{m.Name}: inflated size exceeds the {maxBytes} byte limit");
            }
            return ms.ToArray();
        }
    }

    public JsonObject LoadJson(PakMember m)
    {
        var bytes = Read(m);
        JsonNode? node;
        try
        {
            var text = Encoding.UTF8.GetString(bytes.AsSpan(bytes.AsSpan().StartsWith("﻿"u8) ? 3 : 0));
            node = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException e) { throw new ModelFormatException($"{m.Name}: not JSON ({e.Message})"); }
        return node as JsonObject ?? throw new ModelFormatException($"{m.Name}: JSON root is not an object");
    }

    /// <summary>A <c>.model</c> document; only version 6 is known.</summary>
    public ModelDocument LoadModel(PakMember m)
    {
        if (!ModelSuffixes.Any(s => m.Name.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            throw new ModelFormatException($"{m.Name}: not a .model member");
        return ModelDocument.From(LoadJson(m), m.Name);
    }

    public void Dispose() => _zip.Dispose();
}

/// <summary>A <c>.model</c> member of one pak, and the later paks that provide the same basename.</summary>
public sealed class ModelEntry(string name, string pak, PakMember member, bool custom)
{
    public string Name { get; } = name;
    public string Pak { get; } = pak;
    public PakMember Member { get; } = member;
    public bool Custom { get; } = custom;
    public string Basename => Member.Basename;
    public List<string> OverriddenBy { get; } = [];

    /// <summary>The provider the game loads: no later pak overrides it.</summary>
    public bool Wins => OverriddenBy.Count == 0;
}

/// <summary>
/// Every <c>.model</c> of an install's paks, data0 first. A later pak providing the same basename overrides the
/// earlier member (the prototype's listing rule, RT-confirmed for root-level members of a later dataN.pak, not
/// native-verified); every provider is kept and marked, none is hidden.
/// </summary>
public sealed class ModelCatalog : IDisposable
{
    private readonly Dictionary<string, PakIndex> _paks = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _owns = true;   // false for a Subset: the paks belong to the catalog it came from

    public IReadOnlyList<string> PakPaths { get; }
    public IReadOnlyList<ModelEntry> Models { get; }
    public IReadOnlyList<string> Errors { get; }

    public ModelCatalog(IReadOnlyList<string> paks, Func<string, bool>? isCustom = null)
        : this(paks, isCustom, null)
    {
    }

    private ModelCatalog(IReadOnlyList<string> paks, Func<string, bool>? isCustom, IReadOnlyDictionary<string, PakIndex>? open)
    {
        _owns = open is null;
        var models = new List<ModelEntry>();
        var errors = new List<string>();
        var byBase = new Dictionary<string, List<ModelEntry>>(StringComparer.Ordinal);
        var read = new List<string>();
        foreach (var path in paks)
        {
            PakIndex ix;
            if (open is not null)
            {
                if (!open.TryGetValue(path, out var shared)) continue;
                ix = shared;
            }
            else
            {
                try { ix = PakIndex.Open(path); }
                catch (Exception e) when (e is ModelFormatException or IOException or UnauthorizedAccessException)
                {
                    errors.Add(e.Message);
                    continue;
                }
            }
            _paks[path] = ix;
            read.Add(path);
            foreach (var m in ix.Models())
            {
                var entry = new ModelEntry(m.Name, path, m, isCustom?.Invoke(path) ?? false);
                if (!byBase.TryGetValue(m.Basename, out var prev)) byBase[m.Basename] = prev = [];
                foreach (var p in prev) p.OverriddenBy.Add(System.IO.Path.GetFileName(path));
                prev.Add(entry);
                models.Add(entry);
            }
        }
        PakPaths = open is null ? paks : read;
        Models = models;
        Errors = errors;
    }

    /// <summary>
    /// The <c>.model</c> documents of the paks <paramref name="include"/> accepts, overrides recomputed among them (the
    /// viewer's stock view: a mod pak's <c>.model</c> neither shows nor overrides). Shares this catalog's open paks and
    /// never disposes them; valid while this catalog is.
    /// </summary>
    public ModelCatalog Subset(Func<string, bool> include) =>
        new(PakPaths.Where(include).ToList(), null, _paks);

    public PakIndex Pak(string path) => _paks[path];

    public ModelDocument Load(ModelEntry e) => _paks[e.Pak].LoadModel(e.Member);

    /// <summary>By member path (either slash) or basename (with or without .model); the last provider wins.</summary>
    public ModelEntry? Find(string name)
    {
        var key = name.Replace('\\', '/');
        var exact = Models.LastOrDefault(m => m.Name.Replace('\\', '/').Equals(key, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var base_ = key[(key.LastIndexOf('/') + 1)..].ToLowerInvariant();
        if (!base_.EndsWith(".model")) base_ += ".model";
        return Models.LastOrDefault(m => m.Basename == base_);
    }

    public void Dispose()
    {
        if (_owns)
            foreach (var p in _paks.Values) p.Dispose();
        _paks.Clear();
    }
}
