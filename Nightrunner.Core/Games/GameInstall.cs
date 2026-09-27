using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Nightrunner.Core.Games;

/// <summary>An install root plus its profile and resolved paths. Nothing here ever writes to an install.</summary>
public sealed partial class GameInstall
{
    public string Root { get; }
    public GameProfile Profile { get; }
    public GamePaths Paths { get; }

    public GameInstall(string root, GameProfile? profile = null)
    {
        Root = NormalizeRoot(root);
        Profile = profile ?? DetectProfile(Root) ?? GameProfile.Dltb;
        Paths = new GamePaths(Profile, Root, DataName);
    }

    public string Id => Profile.Id;
    public string Name => Profile.Name;
    public bool Supported => Profile.Supported;

    /// <summary>The data folder in use: the first candidate with assets, else the first that exists, else the first.</summary>
    public string DataName
    {
        get
        {
            var probe = Profile.Folders.GetValueOrDefault(GameFolder.Assets);
            foreach (var d in Profile.DataDirs)
                if (probe is not null && Directory.Exists(Expand(Root, probe, d))) return d;
            foreach (var d in Profile.DataDirs)
                if (Directory.Exists(Path.Combine(Root, d))) return d;
            return Profile.DataDirs[0];
        }
    }

    public string Data => Path.Combine(Root, DataName);
    public string? Assets => Paths.Assets;
    public string? Audio => Paths.Audio;
    public string? Source => Paths.Source;
    public string? Exe() => Paths.Exe();

    /// <summary>A root is usable when its assets folder is there; games without one need their executable.</summary>
    public bool IsValid => Assets is { } a ? Directory.Exists(a) : Exe() is not null;

    public PathReport Validate() => Paths.Validate();

    /// <summary>
    /// Every stock .rpack under the assets folder, recursively, sorted by relative path; with
    /// <paramref name="includeCustom"/>, what Nightrunner Runtime Modding loads as well, in lookup order (see
    /// <see cref="Rpacks(RuntimeContent?)"/>).
    /// </summary>
    public string[] Rpacks(bool includeCustom = false) => Rpacks(includeCustom ? RuntimeContent.Read(this) : null);

    /// <summary>
    /// The stock packs (NightrunnerProxy's folder left out), sorted by relative path, and with a
    /// <paramref name="runtime"/> snapshot the packs it loads placed where the game's first-wins name registration
    /// puts them (<see cref="RuntimeContent.Rpacks"/>): name lookups take the first hit, which is then the copy the
    /// game uses. Only what the runtime would load is listed: a disabled or invalid mod is left out.
    /// </summary>
    public string[] Rpacks(RuntimeContent? runtime)
    {
        if (Assets is not { } assets || !Directory.Exists(assets)) return [];
        var stock = Directory.EnumerateFiles(assets, "*.rpack", SearchOption.AllDirectories)
            .Where(p => !Paths.IsCustom(p))
            .OrderBy(p => Path.GetRelativePath(assets, p), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return runtime is null ? stock : runtime.Rpacks(stock, Id);
    }

    /// <summary>How many .rpack files runtime modding would add.</summary>
    public int CustomRpackCount() => RuntimeContent.Read(this).Loaded(RuntimeKind.Rpack).Count();

    /// <summary>
    /// dataN.pak archives in the data folder, numeric order (data0 first) — later ones override earlier ones member by
    /// member, as the prototype lists them. With <paramref name="includeCustom"/> (runtime modding on), the paks the
    /// runtime mounts follow, in mount order (<see cref="RuntimeContent.Paks"/>).
    /// </summary>
    public string[] Paks(bool includeCustom = false) => Paks(includeCustom ? RuntimeContent.Read(this) : null);

    /// <summary>The stock dataN.paks, then (with a <paramref name="runtime"/> snapshot) the paks it mounts.</summary>
    public string[] Paks(RuntimeContent? runtime)
    {
        var dir = Source ?? (Directory.Exists(Data) ? Data : null);
        if (dir is null || !Directory.Exists(dir)) return [];
        var stock = Directory.EnumerateFiles(dir, "*.pak")
            .Where(p => !Paths.IsCustom(p))
            .Select(p => (path: p, m: DataPakRegex().Match(Path.GetFileName(p))))
            .Where(x => x.m.Success)
            .OrderBy(x => int.Parse(x.m.Groups[1].Value))
            .Select(x => x.path)
            .ToArray();
        return runtime is null ? stock : runtime.Paks(stock);
    }

    public string Sdb(string api = "dx11") =>
        Path.Combine(Assets ?? Data, Profile.SdbName(api));

    public override string ToString() => $"{Name} — {Root}";

    // ---- roots ---------------------------------------------------------------------------------------------

    /// <summary>The picked folder, or its parent when the user picked the data folder itself (.../ph).</summary>
    public static string NormalizeRoot(string path)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var name = Path.GetFileName(p);
        foreach (var pr in GameProfile.All)
        {
            if (!pr.DataDirs.Any(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase))) continue;
            var assets = pr.Folders.GetValueOrDefault(GameFolder.Assets);
            if (assets is not null && Directory.Exists(Expand(p, assets, name)))
                return Path.GetDirectoryName(p) ?? p;
        }
        return p;
    }

    /// <summary>Best profile for a root folder, or null when nothing matches.</summary>
    public static GameProfile? DetectProfile(string root)
    {
        root = NormalizeRoot(root);
        GameProfile? best = null;
        int bestScore = 0;
        foreach (var pr in GameProfile.All)
        {
            int score = 0;
            var assets = pr.Folders.GetValueOrDefault(GameFolder.Assets);
            var datas = pr.DataDirs
                .Where(d => assets is not null
                    ? Directory.Exists(Expand(root, assets, d))
                    : Directory.Exists(Path.Combine(root, d)))
                .ToArray();
            if (datas.Length > 0) score += datas[0] == pr.DataDirs[0] ? 2 : 1;
            if (pr.DataDirs.Any(d => pr.ExeTemplates.Any(t => File.Exists(Expand(root, t, d))))) score += 8;
            if (pr.Markers.Any(m => Path.Exists(Expand(root, m, pr.DataDirs[0])))) score += 4;
            if (string.Equals(Path.GetFileName(root), pr.SteamDir, StringComparison.OrdinalIgnoreCase)) score += 3;
            if (score > bestScore) (best, bestScore) = (pr, score);
        }
        return best;
    }

    private static string Expand(string root, string template, string dataName) =>
        Path.Combine(root, template.Replace("{data}", dataName).Replace('/', Path.DirectorySeparatorChar));

    // ---- lookup --------------------------------------------------------------------------------------------

    /// <summary>
    /// Every game found: remembered roots (settings) first, then an explicit root, then
    /// `$NIGHTRUNNER_GAME_ROOT`, then the Steam libraries.
    /// </summary>
    public static Dictionary<string, GameInstall> FindInstalls(
        string? explicitRoot = null, IReadOnlyDictionary<string, string>? savedRoots = null)
    {
        var found = new Dictionary<string, GameInstall>();
        foreach (var (id, root) in savedRoots ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(root) || GameProfile.ById(id) is not { } pr) continue;
            var gi = new GameInstall(root, pr);
            if (gi.IsValid) found.TryAdd(pr.Id, gi);
        }
        foreach (var cand in new[] { explicitRoot, EnvRoot() })
        {
            if (string.IsNullOrWhiteSpace(cand) || !Directory.Exists(cand)) continue;
            var gi = new GameInstall(cand);
            if (gi.IsValid) found.TryAdd(gi.Id, gi);
        }
        List<string>? libs = null;
        foreach (var pr in GameProfile.All)
        {
            if (found.ContainsKey(pr.Id)) continue;
            libs ??= SteamLibraries();
            foreach (var lib in libs)
            {
                var gi = new GameInstall(Path.Combine(lib, "steamapps", "common", pr.SteamDir), pr);
                if (gi.IsValid)
                {
                    found[pr.Id] = gi;
                    break;
                }
            }
        }
        return found;
    }

    /// <summary>Where a game is, if it can be found at all: Steam libraries, by profile.</summary>
    public static GameInstall? Detect(GameProfile profile)
    {
        foreach (var lib in SteamLibraries())
        {
            var gi = new GameInstall(Path.Combine(lib, "steamapps", "common", profile.SteamDir), profile);
            if (gi.IsValid) return gi;
        }
        return null;
    }

    /// <summary>An explicit root wins, then the remembered roots, then the environment and Steam (DLTB first).</summary>
    public static GameInstall? Find(string? explicitRoot = null, string? gameId = null,
                                    IReadOnlyDictionary<string, string>? savedRoots = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot) && Directory.Exists(explicitRoot))
        {
            var gi = new GameInstall(explicitRoot);
            if (gi.IsValid && (gameId is null || gi.Id == gameId)) return gi;
        }
        var all = FindInstalls(savedRoots: savedRoots);
        if (gameId is not null) return all.GetValueOrDefault(gameId);
        return all.GetValueOrDefault(GameProfile.Dltb.Id) ?? all.Values.FirstOrDefault();
    }

    private static string? EnvRoot() =>
        Environment.GetEnvironmentVariable("NIGHTRUNNER_GAME_ROOT")
        ?? Environment.GetEnvironmentVariable("BEASTPACK_GAME_ROOT");

    /// <summary>Steam library folders (registry SteamPath, the default locations, plus libraryfolders.vdf).</summary>
    public static List<string> SteamLibraries()
    {
        List<string> roots = [];
        if (OperatingSystem.IsWindows())
        {
            foreach (var (hive, key) in new (RegistryKey, string)[]
                     {
                         (Registry.CurrentUser, @"Software\Valve\Steam"),
                         (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
                     })
            {
                try
                {
                    using var k = hive.OpenSubKey(key);
                    foreach (var name in new[] { "SteamPath", "InstallPath" })
                        if (k?.GetValue(name) is string s && s.Length > 0) roots.Add(s);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        roots.Add(@"C:\Program Files (x86)\Steam");
        roots.Add(@"C:\Program Files\Steam");

        List<string> libs = [];
        foreach (var r in roots)
        {
            libs.Add(r);
            var vdf = Path.Combine(r, "steamapps", "libraryfolders.vdf");
            try
            {
                if (!File.Exists(vdf)) continue;
                foreach (Match m in VdfPathRegex().Matches(File.ReadAllText(vdf)))
                    libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch (IOException) { }
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return libs.Where(p => seen.Add(p)).ToList();
    }

    [GeneratedRegex(@"^data(\d+)\.pak$", RegexOptions.IgnoreCase)]
    private static partial Regex DataPakRegex();

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex VdfPathRegex();
}
