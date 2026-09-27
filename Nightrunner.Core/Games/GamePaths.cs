namespace Nightrunner.Core.Games;

/// <summary>One folder or file a validation looked for.</summary>
/// <param name="Required">A missing required entry means the root is not usable.</param>
public sealed record PathCheck(string Label, string? Path, bool Exists, bool Required)
{
    public string State => Path is null ? "n/a" : Exists ? "ok" : Required ? "missing" : "absent";
}

/// <summary>What a root turned out to be: the executable and the content folders.</summary>
public sealed record PathReport(GameProfile Profile, string Root, string DataName, string? Exe,
                                IReadOnlyList<PathCheck> Checks)
{
    public bool ExeFound => Exe is not null;

    /// <summary>Every required entry is present (and the profile has some).</summary>
    public bool Ok => ExeFound && Checks.Where(c => c.Required).All(c => c.Exists)
                      && Checks.Any(c => c.Required);

    public IEnumerable<PathCheck> Missing => Checks.Where(c => c.Required && !c.Exists);

    public string Summary => !ExeFound
        ? "no game executable under this root"
        : Ok ? "ok" : $"{Missing.Count()} folder(s) missing";
}

/// <summary>
/// Every path a game exposes, resolved from a root. Nothing here touches the filesystem except the checks —
/// the properties are where a folder *would* be.
/// </summary>
public sealed class GamePaths(GameProfile profile, string root, string dataName)
{
    public GameProfile Profile { get; } = profile;
    public string Root { get; } = root;
    public string DataName { get; } = dataName;

    public string Data => Path.Combine(Root, DataName);

    /// <summary>The full path of a folder, or null when this game does not define it.</summary>
    public string? Folder(GameFolder which) =>
        Profile.Folders.TryGetValue(which, out var template) ? Resolve(template) : null;

    public string? Assets => Folder(GameFolder.Assets);
    public string? Audio => Folder(GameFolder.Audio);
    public string? Source => Folder(GameFolder.Data);

    public string? Exe()
    {
        foreach (var t in Profile.ExeTemplates)
        {
            var p = Resolve(t);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// The executable's folder (<c>{data}/work/bin/x64</c>), where NightrunnerProxy's DLL and its
    /// <see cref="RuntimeFolder"/> live. Where it would be when the executable is missing.
    /// </summary>
    public string Bin => Path.GetDirectoryName(Exe() ?? (Profile.ExeTemplates.Length > 0 ? Resolve(Profile.ExeTemplates[0]) : Path.Combine(Data, "x")))!;

    /// <summary>NightrunnerProxy's own folder beside the executable: <c>Core.dll</c>, <c>nightrunner.json</c>, <c>mods</c>.</summary>
    public string RuntimeFolder => Path.Combine(Bin, RuntimeContent.RuntimeFolderName);

    public string Resolve(string template) =>
        Path.GetFullPath(Path.Combine(Root, template.Replace("{data}", DataName).Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// True when a path lives in NightrunnerProxy's folder (its mods). With Nightrunner Runtime Modding off, nothing
    /// under it is shown: without the runtime the game cannot load any of it.
    /// </summary>
    public bool IsCustom(string path)
    {
        var full = Path.GetFullPath(path);
        string folder = RuntimeFolder;
        return full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               full.Equals(folder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Locate the executable, then the content folders. NightrunnerProxy's folder is reported by
    /// <see cref="RuntimeModding.Describe"/>.
    /// </summary>
    public PathReport Validate()
    {
        List<PathCheck> checks = [];
        foreach (var (folder, label, required) in Content)
            Add(checks, folder, label, required);
        return new PathReport(Profile, Root, DataName, Exe(), checks);
    }

    private void Add(List<PathCheck> into, GameFolder which, string label, bool required)
    {
        var path = Folder(which);
        into.Add(new PathCheck(label, path, path is not null && Directory.Exists(path), required && path is not null));
    }

    private static readonly (GameFolder Folder, string Label, bool Required)[] Content =
    [
        (GameFolder.Assets, "assets", true),
        (GameFolder.Audio, "audio", true),
        (GameFolder.Data, "data", true),
    ];
}
