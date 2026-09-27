using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nightrunner.Core.Model;
using Nightrunner.Core.Project;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Core.Games;

/// <summary>Where a pack or pak came from, as far as the files on disk can tell.</summary>
public enum PackOrigin
{
    /// <summary>Shipped with the game, as far as can be told.</summary>
    Stock,

    /// <summary>Runtime content: in NightrunnerProxy's folder (its mods).</summary>
    Runtime,

    /// <summary>A Nightrunner build output in a stock folder: a known project's manifest or build.json names it.</summary>
    Build,

    /// <summary>
    /// Named like Nightrunner's (and the prototype's) default output slots — <c>assets_N_pc.rpack</c>, or a
    /// <c>dataN.pak</c> past the shipped <c>data0</c>/<c>data1</c> — though no known project claims it.
    /// </summary>
    Slot,
}

/// <summary>
/// Tells stock packs from modded ones: by location first (NightrunnerProxy's folder), then by
/// what Nightrunner projects recorded writing (manifest output names, <c>&lt;project&gt;.build.json</c>), then by
/// Nightrunner's default output names. Anything not <see cref="PackOrigin.Stock"/> is a mod and never a template.
/// </summary>
public sealed partial class PackOrigins(GameInstall? install, IEnumerable<string>? builtNames = null)
{
    private readonly HashSet<string> _built = (builtNames ?? []).Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n))
        .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

    /// <summary>The pak names the games ship (both DLTB and DL2: <c>data0.pak</c>, <c>data1.pak</c>).</summary>
    public static readonly string[] ShippedPaks = ["data0.pak", "data1.pak"];

    public PackOrigin Of(string path)
    {
        string full = Path.GetFullPath(path);
        string name = Path.GetFileName(full);
        if (install is not null && install.Paths.IsCustom(full)) return PackOrigin.Runtime;
        if (_built.Contains(name)) return PackOrigin.Build;
        if (IsSlotName(name)) return PackOrigin.Slot;
        return PackOrigin.Stock;
    }

    public bool IsStock(string path) => Of(path) == PackOrigin.Stock;

    /// <summary>A word for a modded pack's origin, for refusals and labels.</summary>
    public static string Describe(PackOrigin origin) => origin switch
    {
        PackOrigin.Runtime => "runtime mod",
        PackOrigin.Build => "Nightrunner build",
        PackOrigin.Slot => "mod slot",
        _ => "stock",
    };

    /// <summary><c>assets_N_pc.rpack</c> or a <c>dataN.pak</c> other than the shipped ones.</summary>
    public static bool IsSlotName(string name) =>
        SlotRpack().IsMatch(name) || (DataPak().IsMatch(name) && !ShippedPaks.Contains(name, StringComparer.OrdinalIgnoreCase));

    [GeneratedRegex(@"^assets_\d+_pc\.rpack$", RegexOptions.IgnoreCase)]
    private static partial Regex SlotRpack();

    [GeneratedRegex(@"^data\d+\.pak$", RegexOptions.IgnoreCase)]
    private static partial Regex DataPak();

    /// <summary>
    /// The .rpack / .pak file names Nightrunner projects recorded writing: each project's manifest output names and
    /// the outputs of every <c>*.build.json</c> in its build folder. Unreadable projects are skipped.
    /// </summary>
    public static IReadOnlyList<string> BuiltNames(IEnumerable<string> projectFolders)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in projectFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!ModProject.IsProject(folder)) continue;
                var project = ModProject.Open(folder);
                if (project.Manifest.RpackName is { Length: > 0 } r) names.Add(r);
                if (project.Manifest.PakName is { Length: > 0 } p) names.Add(p);
                if (!Directory.Exists(project.BuildFolder)) continue;
                foreach (var report in Directory.EnumerateFiles(project.BuildFolder, "*.build.json"))
                {
                    if (JsonNode.Parse(File.ReadAllText(report)) is not JsonObject j || j["outputs"] is not JsonObject outputs) continue;
                    foreach (var kind in new[] { "rpack", "pak" })
                        if ((string?)outputs[kind]?["path"] is { Length: > 0 } n) names.Add(Path.GetFileName(n));
                }
            }
            catch (Exception e) when (e is ProjectException or IOException or UnauthorizedAccessException
                                         or System.Text.Json.JsonException or InvalidOperationException)
            {
            }
        }
        return [.. names];
    }
}

/// <summary>
/// "Add to project" takes its template from the stock copy: the resource or <c>.model</c> the game ships,
/// never a mod's copy of it. When only modded copies exist the add is refused by name.
/// </summary>
public static class StockCopy
{
    /// <summary>
    /// The stock copy of the resource <paramref name="gid"/> names: itself when its pack is stock, else the first
    /// stock pack's resource of the same name and type (the one a lookup without mods would take).
    /// </summary>
    public static int Resource(RpackCatalog catalog, int gid, PackOrigins origins)
    {
        if (TryResource(catalog, gid, origins) is int stock) return stock;
        var (entry, _) = catalog.Split(gid);
        throw new ProjectException($"{catalog.Name(gid).Trim()} is only in {entry.Label} ({PackOrigins.Describe(origins.Of(entry.Path))}): no stock copy to start from");
    }

    /// <summary>
    /// <see cref="Resource"/>, or null when only modded packs have it (the viewer's Mods off then draws the mod's copy).
    /// </summary>
    public static int? TryResource(RpackCatalog catalog, int gid, PackOrigins origins)
    {
        var (entry, _) = catalog.Split(gid);
        if (origins.IsStock(entry.Path)) return gid;
        foreach (int hit in catalog.Lookup(catalog.Name(gid), catalog.Type(gid)))
            if (origins.IsStock(catalog.Split(hit).Entry.Path)) return hit;
        return null;
    }

    /// <summary>
    /// The stock copy of a <c>.model</c>: itself when its pak is stock, else the winning stock provider of the same
    /// basename (the last stock pak that has it).
    /// </summary>
    public static ModelEntry Model(ModelCatalog models, ModelEntry entry, PackOrigins origins)
    {
        var origin = origins.Of(entry.Pak);
        if (origin == PackOrigin.Stock) return entry;
        return models.Models.LastOrDefault(m => m.Basename == entry.Basename && origins.IsStock(m.Pak))
               ?? throw new ProjectException($"{entry.Basename} is only in {Path.GetFileName(entry.Pak)} ({PackOrigins.Describe(origin)}): no stock copy to start from");
    }

    /// <summary>
    /// The catalog without modded packs, for templates that resolve names themselves (a model scene, a clip). Its
    /// global ids are the full catalog's.
    /// </summary>
    public static RpackCatalog Catalog(RpackCatalog catalog, PackOrigins origins) => catalog.Subset(e => origins.IsStock(e.Path));

    /// <summary>Refuse a pack by name when it is not stock (prefab edits name their pack directly).</summary>
    public static void RequireStock(PackEntry pack, PackOrigins origins)
    {
        var origin = origins.Of(pack.Path);
        if (origin != PackOrigin.Stock)
            throw new ProjectException($"{pack.Label} is a {PackOrigins.Describe(origin)}: edit the stock pack instead");
    }
}
