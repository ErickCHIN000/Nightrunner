using System.Text.Json;

namespace Nightrunner.Core.Games;

/// <summary>What a runtime content item is.</summary>
public enum RuntimeKind { Rpack, Pak, Sdb, Audio, Map }

/// <summary>When an item loads, relative to the engine's own packs.</summary>
public enum RuntimePhase
{
    /// <summary>At the first <c>LoadResources</c>, after the boot packs (mod.json's default, <c>after-builtins</c>).</summary>
    AfterBuiltins,

    /// <summary>Immediately before a named pack the engine loads (mod.json <c>"at": "before:&lt;pack&gt;"</c>).</summary>
    BeforePack,
}

/// <summary>One file a mod lists, resolved to disk, and whether the runtime would load it.</summary>
/// <param name="Path">The resolved file, or null when nothing on disk matches the entry.</param>
/// <param name="Order">The mod's order in <c>nightrunner.json</c>.</param>
/// <param name="Source">The mod id that lists it.</param>
/// <param name="Problem">Why it is off or unresolved, when it is.</param>
public sealed record RuntimeItem(RuntimeKind Kind, string Name, string? Path, bool Enabled, int Order,
                                 RuntimePhase Phase, string? BeforePack, string Source, string? Problem = null)
{
    public bool Loads => Enabled && Path is not null;

    /// <summary>
    /// When it loads, in the runtime's own words: mod.json's <c>after-builtins</c> / <c>before:&lt;pack&gt;</c> (pack name
    /// normalised, <see cref="RuntimeContent.NormalizePackName"/>); an sdb merges at <c>MmCreate</c> whatever it asks for
    /// (NightrunnerProxy <c>DltbContentHost</c>). A merged sdb is the game's business: the app's materials read the stock sdb.
    /// </summary>
    public string At => Kind == RuntimeKind.Sdb ? "mmcreate"
        : Phase == RuntimePhase.BeforePack ? $"before:{BeforePack}" : "after-builtins";
}

/// <summary>
/// One folder of NightrunnerProxy's <c>mods</c> that has a <c>mod.json</c>, as the runtime sees it: its manifest fields,
/// its switch and order in <c>nightrunner.json</c> (<paramref name="Listed"/> false: not listed, so on and last), and
/// its items. An invalid mod (unreadable manifest, a broken item, a taken id) has no items and says why.
/// <paramref name="Order"/> is its order in <c>nightrunner.json</c>, or for a mod not listed the order it would be listed
/// at (<see cref="RuntimeSettingsFile.NextOrder"/>). <paramref name="Blocked"/>: why the runtime loads no mod at all (a
/// broken <c>nightrunner.json</c>); then its switch is unknown, shown off.
/// </summary>
public sealed record RuntimeMod(string Id, string Folder, string? Name, string? Version, string? Author, string? Description,
                                bool Listed, bool Enabled, int Order, IReadOnlyList<RuntimeItem> Items, IReadOnlyList<string> Problems,
                                string? Invalid = null, string? Blocked = null)
{
    public bool Valid => Invalid is null;

    /// <summary>At least one of its items would load (the runtime being installed is a separate check).</summary>
    public bool Loads => Items.Any(i => i.Loads);

    public string Title => string.IsNullOrWhiteSpace(Name) ? Id : Name!;
}

/// <summary>
/// What Nightrunner Runtime Modding (NightrunnerProxy) loads for an install, read the way the runtime reads it, and
/// the order it loads in. Nothing here writes anything.
/// </summary>
/// <remarks>
/// <para><b>mods</b> (NightrunnerProxy rc1, <c>Core/Content</c>): every <c>bin\x64\Nightrunner\mods\&lt;folder&gt;\mod.json</c>,
/// folders by name, a later folder with a taken id skipped; listed mods by (order, id), then the mods not listed in
/// <c>nightrunner.json</c> (on by default) by id; items by (order, file); a broken item drops its whole mod. Items load at
/// the first <c>LoadResources</c> (paks, then rpacks) or just before a named pack; sdb items at <c>MmCreate</c>; audio and
/// map items are accepted but not loaded.</para>
/// <para><b>nightrunner.json</b> (<see cref="RuntimeSettingsFile"/>): missing is the defaults; there but broken, the runtime
/// loads no mods at all (<see cref="NoMods"/>), and nothing here loads. Only a game with a runtime module
/// (<see cref="GameProfile.RuntimeModule"/>) loads anything.</para>
/// <para>Winners (measured on DLTB, see <see cref="Rpacks"/>): rpack names register first-wins, so an earlier load
/// wins; a pak mount goes to the front of the archive group, so a later mount wins.</para>
/// </remarks>
public sealed class RuntimeContent
{
    public const string RuntimeFolderName = "Nightrunner";
    public const string ModsFolderName = "mods";
    public const string SettingsName = RuntimeSettingsFile.FileName;
    public const string ManifestName = "mod.json";

    public static RuntimeContent Empty { get; } = new([], [], [], [], RuntimeSettingsFile.Defaults, null);

    /// <summary>Every item the mods list, loadable or not, in plan order.</summary>
    public IReadOnlyList<RuntimeItem> Items { get; }

    /// <summary>Entries that are off, unresolved or dropped, and why (one line each); <see cref="NoMods"/> first.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>Folders whose changes change what the runtime loads (existing ones only).</summary>
    public IReadOnlyList<string> WatchFolders { get; }

    /// <summary>Every mod folder with a <c>mod.json</c>, valid or not, in plan order (invalid ones last, by folder).</summary>
    public IReadOnlyList<RuntimeMod> Mods { get; }

    /// <summary><c>nightrunner.json</c> as the runtime reads it.</summary>
    public RuntimeSettingsFile Settings { get; }

    /// <summary>
    /// Why the runtime loads nothing at all, or null: <c>runtime loads no mods (nightrunner.json ...)</c> for a broken
    /// settings file, <c>no runtime module for &lt;game&gt;</c> for a game NightrunnerProxy has no module for.
    /// </summary>
    public string? NoMods { get; }

    private RuntimeContent(IReadOnlyList<RuntimeItem> items, IReadOnlyList<string> problems, IReadOnlyList<string> watch,
                           IReadOnlyList<RuntimeMod> mods, RuntimeSettingsFile settings, string? noMods)
    {
        Items = items;
        Problems = problems;
        WatchFolders = watch;
        Mods = mods;
        Settings = settings;
        NoMods = noMods;
    }

    /// <summary>What <see cref="NoMods"/> says for a game without a runtime module.</summary>
    public static string NoModule(string gameId) => $"no runtime module for {gameId}";

    public IEnumerable<RuntimeItem> Loaded(RuntimeKind kind) => Items.Where(i => i.Loads && i.Kind == kind);

    // ---- load order ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The engine's own packs that load before the first <c>LoadResources</c>, measured on DLTB (NightrunnerProxy
    /// LoadPack log, 2026-09-26: <c>engine_pc</c> and <c>lang_speech_en_pc</c>; the other boot calls are empty-path
    /// probes). Not measured for DL2, so none are assumed there.
    /// </summary>
    public static bool IsBootPack(string gameId, string fileName) =>
        gameId.Equals(GameProfile.Dltb.Id, StringComparison.OrdinalIgnoreCase) &&
        (fileName.Equals("engine_pc.rpack", StringComparison.OrdinalIgnoreCase) ||
         (fileName.StartsWith("lang_speech_", StringComparison.OrdinalIgnoreCase) &&
          fileName.EndsWith("_pc.rpack", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The runtime's rpacks placed among the stock ones in the order a name lookup must see them: the pack whose
    /// copy the game uses comes first. Rpack names register first-wins at load — a mod pack loaded at
    /// <c>LoadResources</c> shows its meshes and textures over the stock packs loaded after it (sample modpack and
    /// the pickup meshes, in game), and the runtime's anim override relies on it. So the order is the load order:
    /// items loading before a boot pack, the boot packs, the after-built-ins items (mods in plan order), the other
    /// <c>before:&lt;pack&gt;</c> items, then the rest of the stock packs.
    /// </summary>
    /// <remarks>
    /// Not modelled, because it is not known: the stock packs the engine loads between <c>LoadResources</c> and a
    /// <c>before:</c> pack, and the stock packs' order among themselves (path order, as before). Two mod packs
    /// colliding on one name have not been tried in game; this follows the same first-wins rule.
    /// </remarks>
    public string[] Rpacks(IReadOnlyList<string> stock, string gameId)
    {
        var rpacks = Loaded(RuntimeKind.Rpack).ToList();
        bool IsBoot(string p) => IsBootPack(gameId, Path.GetFileName(p));
        bool BeforeBoot(RuntimeItem i) => i.Phase == RuntimePhase.BeforePack && IsBootPack(gameId, i.BeforePack + ".rpack");
        var order = new List<string>();
        order.AddRange(rpacks.Where(BeforeBoot).Select(i => i.Path!));
        order.AddRange(stock.Where(IsBoot));
        order.AddRange(rpacks.Where(i => i.Phase == RuntimePhase.AfterBuiltins).Select(i => i.Path!));
        order.AddRange(rpacks.Where(i => i.Phase == RuntimePhase.BeforePack && !BeforeBoot(i)).Select(i => i.Path!));
        order.AddRange(stock);
        return Distinct(order);
    }

    /// <summary>
    /// The runtime's paks after the stock <c>dataN.pak</c>s, in mount order. A mount goes to the front of the
    /// archive group, so the later one wins a member name (what <see cref="Model.ModelCatalog"/> assumes).
    /// </summary>
    public string[] Paks(IReadOnlyList<string> stock)
    {
        var paks = Loaded(RuntimeKind.Pak).ToList();
        var order = new List<string>(stock);
        order.AddRange(paks.Where(i => i.Phase == RuntimePhase.AfterBuiltins).Select(i => i.Path!));
        order.AddRange(paks.Where(i => i.Phase == RuntimePhase.BeforePack).Select(i => i.Path!));
        return Distinct(order);
    }

    private static string[] Distinct(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return paths.Where(p => seen.Add(Path.GetFullPath(p))).ToArray();
    }

    // ---- reading --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Read NightrunnerProxy's mods folder and <c>nightrunner.json</c>. Never throws; problems are listed. For a game with
    /// no runtime module nothing is read: nothing would load it.
    /// </summary>
    public static RuntimeContent Read(GameInstall install)
    {
        if (install.Profile.RuntimeModule is null)
        {
            string none = NoModule(install.Id);
            return new RuntimeContent([], [none], [], [], RuntimeSettingsFile.Defaults, none);
        }

        var items = new List<RuntimeItem>();
        var problems = new List<string>();
        var mods = new List<RuntimeMod>();
        var stockStems = install.Rpacks(runtime: null).Select(p => Path.GetFileNameWithoutExtension(p))
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settings = RuntimeSettingsFile.Load(Path.Combine(install.Paths.RuntimeFolder, SettingsName));
        string? noMods = settings.IsBroken ? $"runtime loads no mods ({settings.Problem})" : null;
        if (noMods is not null) problems.Add(noMods);
        problems.AddRange(settings.Warnings);
        ReadMods(install.Paths, stockStems, settings, noMods, items, problems, mods);

        // bin\x64 too: the runtime folder may not exist yet
        var watch = new List<string> { install.Paths.Bin, install.Paths.RuntimeFolder };
        return new RuntimeContent(items, problems, watch.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                                  mods, settings, noMods);
    }

    // ---- mods folder --------------------------------------------------------------------------------------------

    private sealed record Mod(string Id, string Folder, string? Name, string? Version, string? Author, string? Description,
                              List<(RuntimeKind Kind, string File, string Path, RuntimePhase Phase, string? Before, int Order)> Items);

    private static void ReadMods(GamePaths paths, HashSet<string> stockStems, RuntimeSettingsFile settings, string? noMods,
                                 List<RuntimeItem> items, List<string> problems, List<RuntimeMod> mods)
    {
        string modsFolder = Path.Combine(paths.RuntimeFolder, ModsFolderName);
        if (!Directory.Exists(modsFolder)) return;

        // mod.json files, by folder name; a later folder with a taken id is skipped (ModCatalog.Discover)
        var found = new List<Mod>();
        var invalid = new List<(string Id, string Folder, Mod? Parsed, string Why)>();
        var folders = Directory.GetDirectories(modsFolder);
        Array.Sort(folders, StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            string manifest = Path.Combine(folder, ManifestName);
            if (!File.Exists(manifest)) continue;
            string name = Path.GetFileName(folder);
            string? why;
            try
            {
                var mod = ReadMod(folder, manifest, out why, out var header);
                if (mod is not null && found.Any(m => m.Id.Equals(mod.Id, StringComparison.OrdinalIgnoreCase)))
                    why = $"id '{mod.Id}' is taken (skipped)";
                if (why is null) found.Add(mod!);
                else invalid.Add((mod?.Id ?? header?.Id ?? name, folder, mod ?? header, why));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                why = $"{ManifestName} unreadable ({e.Message})";
                invalid.Add((name, folder, null, why));
            }
            if (why is not null) problems.Add($"mods/{name}: {why}");
        }

        // nightrunner.json: listed mods keep their switch and order and come first (order, id); unlisted ones are on and
        // follow, by id (LoadPlanBuilder). A broken file: nothing is on.
        RuntimeSettingsEntry? Setting(string id) => noMods is null ? settings.Find(id) : null;
        var plan = found.Select(m =>
            {
                var s = Setting(m.Id);
                return (Mod: m, Listed: s is not null, Enabled: noMods is null && (s?.Enabled ?? true), Order: s?.Order ?? settings.NextOrder);
            })
            .OrderBy(x => x.Listed ? 0 : 1).ThenBy(x => x.Order).ThenBy(x => x.Mod.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var (mod, listed, enabled, order) in plan)
        {
            var own = new List<RuntimeItem>();
            var ownProblems = new List<string>();
            if (noMods is not null) ownProblems.Add(noMods);
            foreach (var it in mod.Items.OrderBy(i => i.Order).ThenBy(i => i.File, StringComparer.OrdinalIgnoreCase))
            {
                bool sdb = it.Kind == RuntimeKind.Sdb;
                string? problem =
                    noMods is not null ? noMods
                    : !enabled ? $"disabled in {SettingsName}"
                    : it.Kind is RuntimeKind.Audio or RuntimeKind.Map ? $"{it.Kind.ToString().ToLowerInvariant()} is not loaded by the runtime"
                    : !sdb && it.Before is { } b && !stockStems.Contains(b) ? $"before:{b} never comes: no such pack"
                    : null;
                if (problem is not null && enabled)
                {
                    problems.Add($"{mod.Id}/{it.File}: {problem}");
                    ownProblems.Add($"{it.File}: {problem}");
                }
                // DltbContentHost.Execute warns and loads it at MmCreate anyway
                if (problem is null && sdb && it.Phase == RuntimePhase.BeforePack)
                {
                    problems.Add($"{mod.Id}/{it.File}: sdb loads at mmcreate, not before:{it.Before}");
                    ownProblems.Add($"{it.File}: sdb loads at mmcreate, not before:{it.Before}");
                }
                own.Add(new RuntimeItem(it.Kind, it.File, it.Path, problem is null, order, it.Phase, it.Before,
                                        mod.Id, problem));
            }
            if (mod.Items.Count == 0) ownProblems.Add("no items, nothing to load");
            items.AddRange(own);
            mods.Add(new RuntimeMod(mod.Id, mod.Folder, mod.Name, mod.Version, mod.Author, mod.Description, listed, enabled, order,
                                    own, ownProblems, Blocked: noMods));
        }
        foreach (var (id, folder, parsed, why) in invalid)
        {
            var s = Setting(id);
            mods.Add(new RuntimeMod(id, folder, parsed?.Name, parsed?.Version, parsed?.Author, parsed?.Description,
                                    s is not null, noMods is null && (s?.Enabled ?? true), s?.Order ?? settings.NextOrder, [],
                                    noMods is null ? [why] : [noMods, why], why, noMods));
        }
    }

    /// <summary>
    /// The manifest as <c>ModCatalog.TryRead</c> / <c>TryReadItem</c> check it, with the runtime's deserializer: a value of
    /// the wrong type or an <c>items</c> that is not a list makes the manifest unreadable. <paramref name="header"/> keeps
    /// the id, name, version, author and description of a manifest whose items are refused, so an invalid mod can still be
    /// named.
    /// </summary>
    private static Mod? ReadMod(string folder, string manifest, out string? why, out Mod? header)
    {
        why = null;
        header = null;
        ModWire? json;
        try
        {
            json = JsonSerializer.Deserialize<ModWire>(File.ReadAllText(manifest), RuntimeSettingsFile.Options);
        }
        catch (JsonException e)
        {
            var where = e.Path is { Length: > 0 } p ? $" at {p}" : "";
            var line = e.LineNumber is { } n ? $", line {n + 1}" : "";
            why = $"{ManifestName} unreadable (not valid{where}{line})";
            return null;
        }
        catch (NotSupportedException e)
        {
            why = $"{ManifestName} unreadable ({e.Message})";
            return null;
        }
        if (json is null) { why = $"{ManifestName} is empty"; return null; }
        if (string.IsNullOrWhiteSpace(json.Id)) { why = $"{ManifestName} has no id"; return null; }
        string id = json.Id.Trim();
        header = new Mod(id, folder, json.Name, json.Version, json.Author, json.Description, []);
        var list = new List<(RuntimeKind, string, string, RuntimePhase, string?, int)>();
        int index = -1;
        foreach (var raw in json.Items ?? [])
        {
            index++;
            // a broken item makes the whole mod untrustworthy: the runtime skips the mod
            if (raw is null) { why = $"items[{index}] is null"; return null; }
            RuntimeKind? kind = raw.Kind?.Trim().ToLowerInvariant() switch
            {
                "rpack" => RuntimeKind.Rpack, "pak" => RuntimeKind.Pak, "sdb" => RuntimeKind.Sdb,
                "audio" => RuntimeKind.Audio, "map" => RuntimeKind.Map, _ => null,
            };
            if (kind is null) { why = $"kind '{raw.Kind}' is not rpack/pak/sdb/audio/map"; return null; }
            if (string.IsNullOrWhiteSpace(raw.File)) { why = "an item has no file"; return null; }
            if (!TryParseAt(raw.At, out var phase, out var before))
            {
                why = $"at '{raw.At}' is not after-builtins or before:<pack>";
                return null;
            }
            string rel = raw.File.Trim().Replace('/', Path.DirectorySeparatorChar);
            if (!TryResolve(folder, rel, out var full))
            {
                why = $"file '{raw.File}' must be relative and stay inside the mod folder";
                return null;
            }
            if (!File.Exists(full)) { why = $"file '{raw.File}' does not exist"; return null; }
            list.Add((kind.Value, rel, full, phase, before, raw.Order ?? 0));
        }
        return header with { Items = list };
    }

    /// <summary><c>ModCatalog.TryResolve</c>: rooted paths and anything a <c>..</c> walks out of the mod folder are refused.</summary>
    private static bool TryResolve(string folder, string file, out string absolute)
    {
        absolute = "";
        if (Path.IsPathRooted(file) || file.Contains(':')) return false;
        string root = Path.GetFullPath(folder);
        string candidate = Path.GetFullPath(Path.Combine(root, file));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        absolute = candidate;
        return true;
    }

    /// <summary>
    /// <c>LoadPoint.TryParse</c>: nothing or <c>after-builtins</c>, or <c>before:&lt;pack&gt;</c> with the pack name
    /// normalised (<see cref="NormalizePackName"/>); a name that normalises to nothing is refused.
    /// </summary>
    public static bool TryParseAt(string? text, out RuntimePhase phase, out string? before)
    {
        phase = RuntimePhase.AfterBuiltins;
        before = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        string value = text.Trim();
        if (value.Equals("after-builtins", StringComparison.OrdinalIgnoreCase)) return true;
        const string prefix = "before:";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        string name = NormalizePackName(value[prefix.Length..]);
        if (name.Length == 0) return false;
        phase = RuntimePhase.BeforePack;
        before = name;
        return true;
    }

    /// <summary>
    /// <c>LoadPoint.NormalizePackName</c>: no folder (either slash), no trailing <c>.rpack</c>, no surrounding whitespace,
    /// lower case (invariant); "" for null or a name that is only a folder or an extension.
    /// </summary>
    public static string NormalizePackName(string? name)
    {
        if (name is null) return "";
        string value = name.Trim();
        int start = value.LastIndexOfAny(['\\', '/']) + 1;
        value = value[start..].Trim();
        const string extension = ".rpack";
        if (value.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) value = value[..^extension.Length].TrimEnd();
        return value.ToLowerInvariant();
    }

    // ---- the runtime's wire shape of mod.json (ModJson.cs: ModJson, ModItemJson) -----------------------------------

    private sealed class ModWire
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Author { get; set; }
        public string? Description { get; set; }
        public List<ItemWire?>? Items { get; set; }
    }

    private sealed class ItemWire
    {
        public string? Kind { get; set; }
        public string? File { get; set; }
        public string? At { get; set; }
        public int? Order { get; set; }
    }
}
