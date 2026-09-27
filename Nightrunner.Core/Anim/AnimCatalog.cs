using Nightrunner.Core.Logging;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Core.Anim;

/// <summary>One SeqTrack of a registered sequence bank.</summary>
public sealed record SeqEntry(string Bank, string Pack, SeqRecord Record, AnimBank Source);

/// <summary>Where a clip resolves: the pack and logical index, and the pack's catalog label.</summary>
public readonly record struct ClipRef(RpackFile Pack, int Index, string Label);

/// <summary>
/// The sequence banks of the open game as the engine sees them: every AnimationScr (0x42 + 0x43) of every loaded
/// pack, keyed by bank name, the <b>first registration winning</b> (the engine logs and drops a duplicate) in
/// registration order — the catalog's pack order, or with the runtime's orders (<see cref="AnimPackOrder"/>)
/// the early mod packs first and the late ones after the stock packs. Clips resolve by name the same way, preferring
/// the streamable form (0x44 + 0x45) the engine prefers among the copies of the first pack group that has one.
/// </summary>
public sealed class AnimCatalog
{
    public IReadOnlyList<string> Banks { get; }
    public IReadOnlyList<SeqEntry> Sequences { get; }
    /// <summary>Banks another pack registered first: (bank, the losing pack).</summary>
    public IReadOnlyList<(string Bank, string Pack)> Shadowed { get; }
    public IReadOnlyList<string> Errors { get; }
    /// <summary>The packs in registration order (mod packs the runtime skips left out).</summary>
    public IReadOnlyList<PackEntry> Packs { get; }

    private readonly Dictionary<string, List<SeqEntry>> _byBank;
    private readonly RpackCatalog _catalog;
    private readonly Dictionary<int, int> _rank;          // pack id → registration rank
    private readonly Func<string, bool>? _isCustom;
    private readonly int _firstStock;                     // rank of the first stock pack

    private AnimCatalog(List<string> banks, List<SeqEntry> seqs, List<(string, string)> shadowed, List<string> errors,
                        Dictionary<string, List<SeqEntry>> byBank, RpackCatalog catalog, IReadOnlyList<PackEntry> packs,
                        Func<string, bool>? isCustom)
    {
        Banks = banks;
        Sequences = seqs;
        Shadowed = shadowed;
        Errors = errors;
        _byBank = byBank;
        _catalog = catalog;
        Packs = packs;
        _isCustom = isCustom;
        _rank = new Dictionary<int, int>(packs.Count);
        for (int r = 0; r < packs.Count; r++) _rank[packs[r].Id] = r;
        _firstStock = isCustom is null ? 0 : Enumerable.Range(0, packs.Count).FirstOrDefault(r => !isCustom(packs[r].Path), packs.Count);
    }

    /// <param name="isCustom">Which pack paths are the runtime's mod packs (with <paramref name="order"/>).</param>
    /// <param name="order">The runtime's orders; null keeps the catalog's pack order.</param>
    public static AnimCatalog Build(RpackCatalog catalog, Func<string, bool>? isCustom = null, AnimPackOrder? order = null)
    {
        var banks = new List<string>();
        var seqs = new List<SeqEntry>();
        var shadowed = new List<(string, string)>();
        var errors = new List<string>();
        var byBank = new Dictionary<string, List<SeqEntry>>(StringComparer.OrdinalIgnoreCase);
        var packs = isCustom is null ? catalog.IndexedPacks : AnimPackOrder.Arrange(catalog.IndexedPacks, isCustom, order);
        foreach (var entry in packs)
        {
            var pack = entry.Pack!;
            foreach (int i in AnimBankPacks.OfType(pack, AnimBankPacks.TypeScr))
            {
                string name = SeqRef.NormalizeBank(pack.Name(i).Trim());
                if (byBank.ContainsKey(name))
                {
                    shadowed.Add((name, entry.Label));
                    continue;
                }
                try
                {
                    var bank = AnimBankPacks.ReadBank(pack, i);
                    var list = bank.Records.Select(r => new SeqEntry(name, entry.Label, r, bank)).ToList();
                    byBank[name] = list;
                    banks.Add(name);
                    seqs.AddRange(list);
                }
                catch (AnimBankFormatException e)
                {
                    errors.Add($"{entry.Label}: {name}: {e.Message}");
                }
            }
        }
        foreach (var e in errors) Log.Warn("anim", e);
        return new AnimCatalog(banks, seqs, shadowed, errors, byBank, catalog, packs, isCustom);
    }

    /// <summary>A SeqTrack by bank and name (engine order: <c>_stricmp</c>), or null.</summary>
    public SeqEntry? Find(string bank, string seq) =>
        _byBank.TryGetValue(SeqRef.NormalizeBank(bank), out var list)
            ? list.FirstOrDefault(s => s.Record.Name.Equals(seq, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>
    /// The clip the game plays under <paramref name="anm2Name"/>: among the copies in registered packs, those of the
    /// earliest registration group (early mod packs, stock packs, late mod packs), the streamable one
    /// first, else the earliest. Null when no registered pack ships it.
    /// </summary>
    public ClipRef? ClipOf(string anm2Name)
    {
        if (anm2Name.Length == 0) return null;
        var hits = new List<(int Rank, int Group, RpackFile Pack, int Index, string Label)>();
        foreach (int gid in _catalog.Lookup(anm2Name, Anm2Resource.TypeAnimation))
        {
            var (entry, index) = _catalog.Split(gid);
            if (!_rank.TryGetValue(entry.Id, out int rank)) continue;
            hits.Add((rank, Group(entry), entry.Pack!, index, entry.Label));
        }
        if (hits.Count == 0) return null;
        int first = hits.Min(h => h.Group);
        var group = hits.Where(h => h.Group == first).OrderBy(h => h.Rank).ToList();
        var pick = group.FirstOrDefault(h => IsStream(h.Pack, h.Index));
        if (pick.Pack is null) pick = group[0];
        return new ClipRef(pick.Pack, pick.Index, pick.Label);
    }

    /// <summary>0 for a mod pack registered before the stock packs, 1 stock, 2 after.</summary>
    private int Group(PackEntry entry)
    {
        if (_isCustom is null || !_isCustom(entry.Path)) return 1;
        return _rank[entry.Id] < _firstStock ? 0 : 2;
    }

    private static bool IsStream(RpackFile pack, int index)
    {
        var lg = pack.Logicals[index];
        return lg.PartCount == 2 && pack.PartType((int)lg.FirstPart) == Anm2Resource.PartHeader;
    }

    /// <summary>The clip a SeqTrack plays in catalog pack order: the streamable resource (0x44 + 0x45) if any, else the static one (0x40).</summary>
    public static (RpackFile Pack, int Index)? Clip(RpackCatalog catalog, string anm2Name)
    {
        if (anm2Name.Length == 0) return null;
        // both copies are logical type 0x40; the stream copy is the one whose parts are 0x44 + 0x45
        (RpackFile Pack, int Index)? plain = null;
        foreach (int gid in catalog.Lookup(anm2Name, Anm2Resource.TypeAnimation))
        {
            var (entry, index) = catalog.Split(gid);
            var pack = entry.Pack!;
            if (IsStream(pack, index)) return (pack, index);
            plain ??= (pack, index);
        }
        return plain;
    }
}
