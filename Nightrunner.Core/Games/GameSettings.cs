using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nightrunner.Core.Games;

/// <summary>
/// The roots the user typed in, remembered between runs, with the runtime modding switches, the recent projects and the
/// viewport's toggles. Detection still runs for games with no remembered root.
/// </summary>
public sealed class GameSettings
{
    /// <summary>Game id → install root.</summary>
    [JsonPropertyName("roots")]
    public Dictionary<string, string> Roots { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Game id → is Nightrunner Runtime Modding (NightrunnerProxy) installed and in use. Off means its mods are
    /// hidden everywhere in the app.
    /// </summary>
    [JsonPropertyName("runtimeModding")]
    public Dictionary<string, bool> RuntimeModding { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The viewport's Mods toggle: on draws what the game loads (mods first); off, the default, resolves every name
    /// against the stock packs and paks only.
    /// </summary>
    [JsonPropertyName("viewportMods")]
    public bool ViewportMods { get; set; }

    /// <summary>The viewport's Follow rig toggle: on (the default) a played clip loads its rig's model; off keeps the shown one.</summary>
    [JsonPropertyName("followRig")]
    public bool FollowRig { get; set; } = true;

    /// <summary>Project folders that were opened, most recent first.</summary>
    [JsonPropertyName("recentProjects")]
    public List<string> RecentProjects { get; set; } = [];

    [JsonIgnore]
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nightrunner", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static GameSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<GameSettings>(File.ReadAllText(FilePath), Json) ?? new GameSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // a corrupt or unreadable settings file must never stop the app starting
        }
        return new GameSettings();
    }

    public void Save()
    {
        // the dock, runtime and build checks (NIGHTRUNNER_DOCKCHECK, NIGHTRUNNER_RUNTIMECHECK, NIGHTRUNNER_BUILDCHECK,
        // NIGHTRUNNER_PROJECTSCHECK) never
        // write the user's settings
        if (Environment.GetEnvironmentVariable("NIGHTRUNNER_DOCKCHECK") is { Length: > 0 } ||
            Environment.GetEnvironmentVariable("NIGHTRUNNER_RUNTIMECHECK") is { Length: > 0 } ||
            Environment.GetEnvironmentVariable("NIGHTRUNNER_BUILDCHECK") is { Length: > 0 } ||
            Environment.GetEnvironmentVariable("NIGHTRUNNER_PROJECTSCHECK") is { Length: > 0 }) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public string? Root(string gameId) =>
        Roots.TryGetValue(gameId, out var r) && !string.IsNullOrWhiteSpace(r) ? r : null;

    public void SetRoot(string gameId, string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) Roots.Remove(gameId);
        else Roots[gameId] = root.Trim();
    }

    /// <summary>On only for a game NightrunnerProxy has a module for (<see cref="GameProfile.RuntimeModule"/>), whatever is stored.</summary>
    public bool RuntimeModdingFor(string? gameId) =>
        GameProfile.ById(gameId)?.RuntimeModule is not null && RuntimeModding.TryGetValue(gameId!, out var on) && on;

    public void SetRuntimeModding(string gameId, bool on) => RuntimeModding[gameId] = on;

    private const int MaxRecentProjects = 12;

    /// <summary>Put a project folder at the top of the recent list.</summary>
    public void TouchProject(string folder)
    {
        RecentProjects.RemoveAll(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, folder);
        if (RecentProjects.Count > MaxRecentProjects)
            RecentProjects.RemoveRange(MaxRecentProjects, RecentProjects.Count - MaxRecentProjects);
    }

    public void ForgetProject(string folder) =>
        RecentProjects.RemoveAll(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase));
}
