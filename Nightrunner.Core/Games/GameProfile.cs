namespace Nightrunner.Core.Games;

/// <summary>The folders a game keeps its editable content in. Values are per game; some games have none.</summary>
public enum GameFolder
{
    /// <summary>`.rpack` containers and the shader database.</summary>
    Assets,

    /// <summary>Wwise banks and streams.</summary>
    Audio,

    /// <summary>`dataN.pak` archives — scripts and `.model` definitions.</summary>
    Data,
}

/// <summary>
/// What differs between the games: where they install, what the executable is called, and where each
/// <see cref="GameFolder"/> lives under the root. Ported and extended from nightrunner/games.py.
/// </summary>
/// <remarks>
/// Templates are root-relative and use `{data}` for the data folder in use (`ph_ft` for DLTB, `ph` for DL2).
/// A folder a game does not have is simply absent from <see cref="Folders"/>.
/// </remarks>
public sealed record GameProfile(
    string Id,
    string Name,
    string SteamDir,
    string[] DataDirs,                       // candidates under the root, preferred first
    string[] ExeTemplates,                   // root-relative, `{data}` allowed; first match wins
    string[] Markers,                        // root-relative paths only this game has
    IReadOnlyDictionary<GameFolder, string> Folders,
    bool Supported = true)                   // false = the layout is a slot, nothing is implemented yet
{
    public string Short => Id.ToUpperInvariant();

    public string SdbName(string api) => $"runtime_{api}.sdb";

    /// <summary>
    /// NightrunnerProxy's game module for this game (<c>bin\x64\Nightrunner\&lt;module&gt;.dll</c>), or null when the runtime
    /// has none: then nothing boots it (its <c>Proxy/DotNetBootstrap.ixx</c> starts .NET for the DLTB executable only) and
    /// runtime modding stays off. Only DLTB has one (NightrunnerProxy rc1).
    /// </summary>
    public string? RuntimeModule { get; init; }

    /// <summary>The Chrome Engine 6 layout that DLTB and DL2 share; only the data folder differs.</summary>
    public static IReadOnlyDictionary<GameFolder, string> ChromeFolders { get; } =
        new Dictionary<GameFolder, string>
        {
            [GameFolder.Assets] = "{data}/work/data_platform/pc/assets",
            [GameFolder.Audio] = "{data}/work/data/audio",
            [GameFolder.Data] = "{data}/source",
        };

    public static readonly GameProfile Dltb = new(
        "dltb", "Dying Light: The Beast", "Dying Light The Beast", ["ph_ft"],
        ["{data}/work/bin/x64/DyingLightGame_TheBeast_x64_rwdi.exe"],
        ["ph_ft/work/data_platform/pc/assets/dlc_frontier", "ph_ft/work/data_platform/pc/assets/menu_level_ft"],
        ChromeFolders)
    { RuntimeModule = "DLTB" };

    public static readonly GameProfile Dl2 = new(
        "dl2", "Dying Light 2", "Dying Light 2", ["ph", "ph_ft"],
        ["{data}/work/bin/x64/DyingLightGame_x64_rwdi.exe"],
        ["DevTools", "ph/dlc_opera", "ph_ft/dlc_opera", "ph/source/data_devtools0.pak",
         "ph_ft/source/data_devtools0.pak"],
        ChromeFolders);

    /// <summary>
    /// Dying Light 1 — a different engine generation: `DW/Data*.pak`, no `work/data_platform` tree. The slot
    /// exists so a root can be stored and validated down to the executable; its folders are not defined yet.
    /// </summary>
    public static readonly GameProfile Dl1 = new(
        "dl1", "Dying Light", "Dying Light", ["DW"],
        ["DyingLightGame.exe", "DevTools/DyingLightPlayer.exe"],
        ["DW/Data0.pak", "DW/Data1.pak"],
        new Dictionary<GameFolder, string>(),
        Supported: false);

    public static readonly GameProfile[] All = [Dltb, Dl2, Dl1];

    public static GameProfile? ById(string? id) =>
        id is null ? null : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
