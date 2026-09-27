using System.Buffers.Binary;
using System.Text;
using System.Xml;

namespace Nightrunner.Core.Audio;

public sealed class AudioNameIndex
{
    private const int MaxPinheadBytes = 32 * 1024 * 1024;
    private const int MaxHircBytes = 64 * 1024 * 1024;
    private readonly Dictionary<uint, AudioName> _byMedia;
    private readonly Dictionary<(int Archive, string Bank, uint Media), AudioName> _byBank;
    private readonly Dictionary<uint, string[]> _banksByMedia;
    private readonly NameStats _stats;
    private readonly string[] _warnings;

    private AudioNameIndex(Dictionary<uint, AudioName> byMedia,
                           Dictionary<(int Archive, string Bank, uint Media), AudioName> byBank,
                           Dictionary<uint, string[]> banksByMedia,
                           int eventCount, int bankCount, NameStats stats, string[] warnings)
    {
        _byMedia = byMedia;
        _byBank = byBank;
        _banksByMedia = banksByMedia;
        EventCount = eventCount;
        BankCount = bankCount;
        _stats = stats;
        _warnings = warnings;
    }

    public int EventCount { get; }
    public int BankCount { get; }
    public int MediaCount => _byMedia.Count;
    public int NamedEntryCount { get; private set; }
    public IReadOnlyList<string> Warnings => _warnings;
    public string Diagnostics => $"matched events {_stats.Events:N0}, play actions {_stats.Actions:N0}, source objects {_stats.Sources:N0}, parents {_stats.Parents:N0}, target hits {_stats.Hits:N0}";

    private sealed class NameStats
    {
        public int Events, Actions, Sources, Parents, Hits;
    }

    public string NameFor(AespEntry entry) => Get(entry)?.Label ?? "—";

    public IReadOnlyList<string> EventsFor(AespEntry entry) => Get(entry)?.Events ?? [];

    public IReadOnlyList<string> BanksFor(AespEntry entry)
    {
        if (entry.BankName is { } bank) return [bank];
        return entry.WemId <= uint.MaxValue && _banksByMedia.TryGetValue((uint)entry.WemId, out var names) ? names : [];
    }

    public bool Matches(AespEntry entry, string word) =>
        Get(entry) is { } name && name.Events.Any(e => e.Contains(word, StringComparison.OrdinalIgnoreCase));

    private AudioName? Get(AespEntry entry)
    {
        if (entry.WemId > uint.MaxValue) return null;
        uint id = (uint)entry.WemId;
        if (entry.BankName is { } bank)
            return _byBank.GetValueOrDefault((entry.ArchiveId, bank.ToLowerInvariant(), id));
        return _byMedia.GetValueOrDefault(id);
    }

    public static AudioNameIndex Build(AespCatalog catalog, CancellationToken ct = default)
    {
        Dictionary<uint, string> eventNames = [];
        HashSet<uint> ambiguousEvents = [];
        List<string> warnings = [];
        foreach (var asset in catalog.NameAssets.Where(a => a.Name.Equals("wwisepinhead", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            try { ReadPinhead(catalog.Archives[asset.ArchiveId].Source.Path, asset, eventNames, ambiguousEvents, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException)
            {
                warnings.Add($"{catalog.Archives[asset.ArchiveId].Source.RelativePath}: {ex.Message}");
            }
        }
        Dictionary<uint, HashSet<string>> byMedia = [];
        Dictionary<(int Archive, string Bank, uint Media), HashSet<string>> byBank = [];
        Dictionary<uint, HashSet<string>> banksByMedia = [];
        NameStats stats = new();
        int bankCount = 0;
        foreach (var asset in catalog.NameAssets.Where(a => !a.Name.Equals("wwisepinhead", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var scan = ReadBank(catalog.Archives[asset.ArchiveId].Source.Path, asset, eventNames, stats, ct);
                foreach (uint mediaId in scan.SourceIds)
                {
                    if (!banksByMedia.TryGetValue(mediaId, out var banks))
                        banksByMedia[mediaId] = banks = new(StringComparer.OrdinalIgnoreCase);
                    banks.Add(asset.Name);
                }
                foreach (var (mediaId, events) in scan.NamedLinks)
                {
                    if (!byMedia.TryGetValue(mediaId, out var global)) byMedia[mediaId] = global = [];
                    var key = (asset.ArchiveId, asset.Name.ToLowerInvariant(), mediaId);
                    if (!byBank.TryGetValue(key, out var local)) byBank[key] = local = [];
                    global.UnionWith(events);
                    local.UnionWith(events);
                }
                bankCount++;
            }
            catch (Exception ex) when (ex is IOException or OverflowException or XmlException or ArgumentException)
            {
                warnings.Add($"{catalog.Archives[asset.ArchiveId].Source.RelativePath}/{asset.Name}: {ex.Message}");
            }
        }
        // Prefer names tied to fewer media IDs when choosing a row label.
        Dictionary<string, int> fanout = new(StringComparer.OrdinalIgnoreCase);
        foreach (var events in byMedia.Values)
            foreach (string name in events)
                fanout[name] = fanout.GetValueOrDefault(name) + 1;
        var result = new AudioNameIndex(byMedia.ToDictionary(k => k.Key, k => new AudioName(k.Value, fanout)),
                                        byBank.ToDictionary(k => k.Key, k => new AudioName(k.Value, fanout)),
                                        banksByMedia.ToDictionary(k => k.Key,
                                            k => k.Value.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray()),
                                        eventNames.Count, bankCount, stats, [.. warnings]);
        result.NamedEntryCount = catalog.Entries.Count(e => result.Get(e) is not null);
        return result;
    }

    public static uint WwiseHash(string name)
    {
        // Wwise IDs use FNV-1; FNV-1a's XOR-first order gives different IDs.
        uint hash = 2166136261;
        foreach (byte value in Encoding.UTF8.GetBytes(name.ToLowerInvariant()))
            hash = unchecked((hash * 16777619) ^ value);
        return hash;
    }

    private static void ReadPinhead(string path, AespAsset asset, Dictionary<uint, string> names,
                                    HashSet<uint> ambiguous, CancellationToken ct)
    {
        if (asset.Size > MaxPinheadBytes || asset.Size < 0) throw new AespFormatException("pinhead is too large");
        byte[] bytes = ReadAt(path, asset.Offset, (int)asset.Size);
        using var input = new MemoryStream(bytes, writable: false);
        using var xml = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxPinheadBytes,
        });
        while (xml.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (xml.NodeType != XmlNodeType.Element || xml.Name != "Event") continue;
            string? name = xml.GetAttribute("name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            uint id = WwiseHash(name);
            if (ambiguous.Contains(id)) continue;
            if (names.TryGetValue(id, out string? existing) &&
                !existing.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                // A colliding hash cannot identify either event reliably.
                names.Remove(id);
                ambiguous.Add(id);
            }
            else names.TryAdd(id, name);
        }
    }

    private sealed record BankScan(Dictionary<uint, HashSet<string>> NamedLinks, uint[] SourceIds);

    private static BankScan ReadBank(string path, AespAsset asset,
        Dictionary<uint, string> eventNames, NameStats stats, CancellationToken ct)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long end = checked(asset.Offset + asset.Size);
        long hircOffset = 0;
        uint hircSize = 0;
        uint version = 0;
        Span<byte> header = stackalloc byte[8];
        Span<byte> number = stackalloc byte[4];
        for (long pos = asset.Offset; pos < end;)
        {
            if (end - pos < 8) throw new AespFormatException("bank chunk header is truncated");
            BankMediaReader.ReadExactlyAt(file.SafeFileHandle, header, pos);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            long payload = pos + 8;
            if (size > end - payload) throw new AespFormatException("bank chunk exceeds entry");
            if (header[..4].SequenceEqual("BKHD"u8) && size >= 4)
            {
                BankMediaReader.ReadExactlyAt(file.SafeFileHandle, number, payload);
                version = BinaryPrimitives.ReadUInt32LittleEndian(number);
            }
            if (header[..4].SequenceEqual("HIRC"u8)) (hircOffset, hircSize) = (payload, size);
            pos = payload + size;
        }
        if (version != 150) throw new AespFormatException($"unsupported Wwise bank version {version}");
        if (hircSize == 0) return new([], []);
        if (hircSize < 4 || hircSize > MaxHircBytes) throw new AespFormatException("invalid HIRC size");
        byte[] bytesHirc = new byte[checked((int)hircSize)];
        BankMediaReader.ReadExactlyAt(file.SafeFileHandle, bytesHirc, hircOffset);
        ReadOnlySpan<byte> hirc = bytesHirc;
        uint count = U32(hirc);
        if (count > (hirc.Length - 4) / 9) throw new AespFormatException("invalid HIRC object count");
        Dictionary<uint, uint[]> events = [];
        Dictionary<uint, uint> actions = [];
        Dictionary<uint, uint[]> sources = [];
        Dictionary<uint, uint> parents = [];
        HashSet<uint> objectIds = [];
        int cursor = 4;
        for (uint i = 0; i < count; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            if (hirc.Length - cursor < 5) throw new AespFormatException("truncated HIRC object");
            byte type = hirc[cursor];
            uint size = U32(hirc[(cursor + 1)..]);
            cursor += 5;
            if (size < 4 || size > hirc.Length - cursor) throw new AespFormatException("invalid HIRC object size");
            var item = hirc.Slice(cursor, (int)size);
            uint id = U32(item);
            objectIds.Add(id);
            if (type == 4 && eventNames.ContainsKey(id) && TryEvent(item, out var actionIds))
                events[id] = actionIds;
            else if (type == 3 && item.Length >= 11)
            {
                ushort actionType = BinaryPrimitives.ReadUInt16LittleEndian(item[4..]);
                if (actionType is 0x0403 or 0x0503 && item[10] == 0)
                    actions[id] = U32(item[6..]);
            }
            else if (type == 2 && item.Length >= 17)
            {
                sources[id] = [U32(item[9..])];
                if (item.Length >= 18)
                {
                    int baseOffset = 18;
                    bool hasBase = true;
                    if ((U32(item[4..]) & 0xF) == 2)
                    {
                        if (item.Length - baseOffset < 4) hasBase = false;
                        else
                        {
                            uint pluginSize = U32(item[baseOffset..]);
                            if (pluginSize > item.Length - baseOffset - 4) hasBase = false;
                            else baseOffset += checked((int)pluginSize + 4);
                        }
                    }
                    if (hasBase && TryParent(item, baseOffset, out uint parent)) parents[id] = parent;
                }
            }
            else if (type == 11 && TryMusicTrack(item, out var mediaIds, out uint trackParent))
            {
                sources[id] = mediaIds;
                parents[id] = trackParent;
            }
            else if (type is 5 or 6 or 7 or 9 or 10 or 12 or 13 &&
                     TryParent(item, type is 10 or 12 or 13 ? 5 : 4, out uint parent))
                parents[id] = parent;
            cursor += (int)size;
        }

        stats.Events += events.Count;
        stats.Actions += actions.Count;
        stats.Sources += sources.Count;
        stats.Parents += parents.Count;
        // Match named events to play-action targets, then follow sources up the HIRC tree.
        Dictionary<uint, HashSet<string>> targetNames = [];
        foreach (var (eventId, actionIds) in events)
        {
            string name = eventNames[eventId];
            foreach (uint actionId in actionIds)
            {
                if (!actions.TryGetValue(actionId, out uint target)) continue;
                if (!targetNames.TryGetValue(target, out var aliases)) targetNames[target] = aliases = [];
                aliases.Add(name);
            }
        }

        Dictionary<uint, HashSet<string>> links = [];
        foreach (var (sourceObject, media) in sources)
        {
            HashSet<uint> visited = [];
            uint current = sourceObject;
            for (int depth = 0; depth < 64 && current != 0 && visited.Add(current); depth++)
            {
                if (targetNames.TryGetValue(current, out var names))
                {
                    stats.Hits++;
                    foreach (uint mediaId in media)
                    {
                        if (mediaId == 0) continue;
                        if (!links.TryGetValue(mediaId, out var aliases)) links[mediaId] = aliases = [];
                        aliases.UnionWith(names);
                    }
                }
                if (!parents.TryGetValue(current, out uint parent) || !objectIds.Contains(parent)) break;
                current = parent;
            }
        }
        return new(links, [.. sources.Values.SelectMany(ids => ids).Where(id => id != 0).Distinct()]);
    }

    private static bool TryParent(ReadOnlySpan<byte> item, int offset, out uint parent)
    {
        parent = 0;
        if (item.Length - offset < 2) return false;
        // The v150 parent field follows variable-length FX and metadata lists.
        int fxCount = item[offset + 1];
        offset += 2;
        if (fxCount > 0) offset += 1 + fxCount * 6;
        if (item.Length - offset < 2) return false;
        int metadataCount = item[offset + 1];
        offset += 2 + metadataCount * 6;
        if (item.Length - offset < 8) return false;
        parent = U32(item[(offset + 4)..]);
        return true;
    }

    private static bool TryEvent(ReadOnlySpan<byte> item, out uint[] ids)
    {
        ids = [];
        uint count = 0;
        int shift = 0, pos = 4;
        while (pos < item.Length && shift < 35)
        {
            byte value = item[pos++];
            count |= (uint)(value & 0x7F) << shift;
            if ((value & 0x80) == 0) break;
            shift += 7;
        }
        if (count > 100_000 || count > (item.Length - pos) / 4 || pos + count * 4 != item.Length) return false;
        ids = new uint[count];
        for (int i = 0; i < ids.Length; i++) ids[i] = U32(item[(pos + i * 4)..]);
        return true;
    }

    private static bool TryMusicTrack(ReadOnlySpan<byte> item, out uint[] ids, out uint parent)
    {
        ids = [];
        parent = 0;
        if (item.Length < 9) return false;
        uint count = U32(item[5..]);
        if (count > 10_000) return false;
        List<uint> found = [];
        int pos = 9;
        for (uint i = 0; i < count; i++)
        {
            if (item.Length - pos < 14) return false;
            uint plugin = U32(item[pos..]);
            uint source = U32(item[(pos + 5)..]);
            pos += 14;
            // These source records carry length-prefixed plug-in data.
            if ((plugin & 0xF) == 2)
            {
                if (item.Length - pos < 4) return false;
                uint length = U32(item[pos..]);
                if (length > item.Length - pos - 4) return false;
                pos += checked((int)length + 4);
            }
            found.Add(source);
        }
        ids = [.. found];
        if (item.Length - pos < 4) return true;
        uint playlistCount = U32(item[pos..]);
        pos += 4;
        if (playlistCount > 100_000 || playlistCount > (item.Length - pos) / 44) return true;
        pos += checked((int)playlistCount * 44);
        if (playlistCount > 0)
        {
            if (item.Length - pos < 4) return true;
            pos += 4;
        }
        if (item.Length - pos < 4) return true;
        uint automationCount = U32(item[pos..]);
        pos += 4;
        if (automationCount > 10_000) return true;
        for (uint i = 0; i < automationCount; i++)
        {
            if (item.Length - pos < 12) return true;
            uint points = U32(item[(pos + 8)..]);
            pos += 12;
            if (points > (item.Length - pos) / 12) return true;
            pos += checked((int)points * 12);
        }
        TryParent(item, pos, out parent);
        return true;
    }

    private static byte[] ReadAt(string path, long offset, int size)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] data = new byte[size];
        BankMediaReader.ReadExactlyAt(file.SafeFileHandle, data, offset);
        return data;
    }

    private static uint U32(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt32LittleEndian(value);

    private sealed class AudioName
    {
        public AudioName(HashSet<string> events, Dictionary<string, int> fanout)
        {
            Events = [.. events.OrderBy(e => fanout.GetValueOrDefault(e))
                               .ThenBy(e => e.Length)
                               .ThenBy(e => e, StringComparer.OrdinalIgnoreCase)];
            Label = Events[0] + (Events.Length > 1 ? $" (+{Events.Length - 1})" : "");
        }

        public string[] Events { get; }
        public string Label { get; }
    }
}
