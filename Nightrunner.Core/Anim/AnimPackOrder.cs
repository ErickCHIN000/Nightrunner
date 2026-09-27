using Nightrunner.Core.Games;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Core.Anim;

/// <summary>
/// Where NightrunnerProxy's mod packs register animations relative to the stock packs. Clips and sequence banks
/// register first-wins, so a pack registered before the stock anim packs replaces their clips and one registered after
/// them never does; a mod pack the runtime does not load (a disabled or invalid mod) is left out.
/// </summary>
public sealed class AnimPackOrder
{
    private readonly Dictionary<string, int> _orders;     // by pack file name, case-insensitive: −1 early, +1 late

    public IReadOnlyDictionary<string, int> Orders => _orders;

    private AnimPackOrder(Dictionary<string, int> orders) => _orders = orders;

    /// <summary>
    /// Every rpack the runtime loads (<see cref="RuntimeContent"/>: NightrunnerProxy's mod.json items), by pack file
    /// name. An item counts as early (−1) when it loads just before a named pack and late (+1) when it loads at
    /// <c>LoadResources</c>: the engine's anim packs go through its own <c>LoadPack</c> before <c>LoadResources</c>, so a
    /// late clip registers after the stock one and loses (observed in game with the earlier, removed runtime). Whether a <c>before:&lt;pack&gt;</c> item really
    /// precedes the stock anim packs depends on the pack it names; that is not modelled.
    /// </summary>
    public static AnimPackOrder FromRuntime(RuntimeContent runtime)
    {
        var orders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in runtime.Loaded(RuntimeKind.Rpack))
            orders.TryAdd(Path.GetFileName(item.Path!), item.Phase == RuntimePhase.AfterBuiltins ? 1 : -1);
        return new AnimPackOrder(orders);
    }

    /// <summary>The order of the pack file <paramref name="fileName"/>; null when the runtime does not load it.</summary>
    public int? OrderOf(string fileName) => _orders.TryGetValue(fileName, out int order) ? order : null;

    /// <summary>
    /// <paramref name="packs"/> (catalog order) in animation registration order: the early mod packs, then the stock
    /// packs, then the late ones, each group in catalog order. Mod packs the runtime does not load are left out.
    /// <paramref name="order"/> null keeps the catalog order.
    /// </summary>
    public static IReadOnlyList<PackEntry> Arrange(IReadOnlyList<PackEntry> packs, Func<string, bool> isCustom, AnimPackOrder? order)
    {
        if (order is null) return packs;
        var early = new List<PackEntry>();
        var late = new List<PackEntry>();
        var stock = new List<PackEntry>();
        foreach (var p in packs)
        {
            if (!isCustom(p.Path)) { stock.Add(p); continue; }
            switch (order.OrderOf(Path.GetFileName(p.Path)))
            {
                case < 0: early.Add(p); break;
                case > 0: late.Add(p); break;
            }
        }
        return [.. early, .. stock, .. late];
    }
}
