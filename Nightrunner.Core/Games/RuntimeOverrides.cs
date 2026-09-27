using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Core.Games;

/// <summary>
/// One stock resource a runtime mod item replaces: a resource of its rpack that a stock pack also has (same engine-folded
/// name and type), or a <c>.model</c> / <c>.scr</c> member of its pak that shadows a stock pak's.
/// </summary>
/// <param name="File">The item's file, relative to the mod folder.</param>
/// <param name="Type">The resource type (rpacks), null for pak members.</param>
/// <param name="Stock">The stock pack or pak that has it (the first one, in the stock order given).</param>
public sealed record RuntimeOverride(RuntimeKind Kind, string File, string Name, byte? Type, string Stock);

/// <summary>
/// What a runtime mod replaces in the stock game, computed from the files: the mod's rpacks are opened and every
/// resource name looked up in the stock packs; the mod's paks are opened and their <c>.model</c> / <c>.scr</c> members
/// compared with the stock paks'. Whether the mod's copy wins is the load order's business (see
/// <see cref="RuntimeContent.Rpacks"/>); this lists what it collides with. Reads only.
/// </summary>
/// <remarks>
/// A <c>.model</c> is matched by basename (the rule <see cref="ModelCatalog"/> lists overrides by); a <c>.scr</c> by its
/// member path. Items the runtime would not load are still listed: what a disabled mod would override is useful to see.
/// </remarks>
public static class RuntimeOverrides
{
    /// <summary>Script members compared by path.</summary>
    public const string ScriptSuffix = ".scr";

    /// <summary>The <c>.model</c> basenames and <c>.scr</c> paths of the stock paks, each with the first pak that has it.</summary>
    public sealed class StockMembers
    {
        internal readonly Dictionary<string, string> Models = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> Scripts = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Errors { get; } = [];

        public static StockMembers Read(IEnumerable<string> stockPaks)
        {
            var s = new StockMembers();
            foreach (var pak in stockPaks)
            {
                try
                {
                    using var ix = PakIndex.Open(pak);
                    foreach (var m in ix.Members) s.Add(m, Path.GetFileName(pak));
                }
                catch (Exception e) when (e is ModelFormatException or IOException or UnauthorizedAccessException)
                {
                    s.Errors.Add(e.Message);
                }
            }
            return s;
        }

        private void Add(PakMember m, string pak)
        {
            if (IsModel(m.Name)) Models.TryAdd(m.Basename, pak);
            else if (m.Name.EndsWith(ScriptSuffix, StringComparison.OrdinalIgnoreCase)) Scripts.TryAdd(Key(m.Name), pak);
        }
    }

    private static bool IsModel(string name) => PakIndex.ModelSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    private static string Key(string member) => member.Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// Every stock resource or member <paramref name="mod"/>'s items collide with. <paramref name="stockPacks"/> are the
    /// indexed stock packs in lookup order; <paramref name="problems"/> gets a line for each file that cannot be read.
    /// </summary>
    public static List<RuntimeOverride> Of(IEnumerable<RuntimeItem> items, IReadOnlyList<PackEntry> stockPacks, StockMembers stockPaks,
                                           List<string>? problems = null)
    {
        var list = new List<RuntimeOverride>();
        foreach (var item in items)
        {
            if (item.Path is not { } path || !File.Exists(path)) continue;
            try
            {
                if (item.Kind == RuntimeKind.Rpack) Rpack(item, path, stockPacks, list);
                else if (item.Kind == RuntimeKind.Pak) Pak(item, path, stockPaks, list);
            }
            catch (Exception e) when (e is RpackFormatException or ModelFormatException or IOException or UnauthorizedAccessException)
            {
                problems?.Add($"{item.Name}: {e.Message}");
            }
        }
        return list;
    }

    private static void Rpack(RuntimeItem item, string path, IReadOnlyList<PackEntry> stock, List<RuntimeOverride> into)
    {
        using var pack = RpackFile.Open(path);
        for (int i = 0; i < pack.Count; i++)
        {
            var folded = pack.NameBytesLower(i);
            byte type = pack.Logicals[i].Type;
            foreach (var e in stock)
            {
                if (e.Pack is not { } sp) continue;
                if (!sp.IndicesOf(folded).Any(k => sp.Logicals[k].Type == type)) continue;
                into.Add(new RuntimeOverride(RuntimeKind.Rpack, item.Name, pack.Name(i).Trim(), type, e.Label));
                break;
            }
        }
    }

    private static void Pak(RuntimeItem item, string path, StockMembers stock, List<RuntimeOverride> into)
    {
        using var ix = PakIndex.Open(path);
        foreach (var m in ix.Members)
        {
            string? hit = IsModel(m.Name) ? stock.Models.GetValueOrDefault(m.Basename)
                : m.Name.EndsWith(ScriptSuffix, StringComparison.OrdinalIgnoreCase) ? stock.Scripts.GetValueOrDefault(Key(m.Name))
                : null;
            if (hit is not null) into.Add(new RuntimeOverride(RuntimeKind.Pak, item.Name, Key(m.Name), null, hit));
        }
    }
}
