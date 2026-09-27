using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nightrunner.Core.Project;

public sealed class ProjectException(string message) : Exception(message);

/// <summary>What a project folder records about itself. Everything else is the folder's own contents.</summary>
public sealed class ProjectManifest
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = ModProject.CurrentSchema;

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Which game this project targets ("dltb", "dl2", "dl1"), or null when it is game-agnostic.</summary>
    [JsonPropertyName("game")]
    public string? Game { get; set; }

    [JsonPropertyName("created")]
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("modified")]
    public DateTimeOffset Modified { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "";

    /// <summary>
    /// Where Build writes, when the user picked a folder other than <c>&lt;project&gt;\build</c>: relative to the project
    /// folder when it lies inside it, else absolute. Absent means the default, so older manifests read unchanged.
    /// </summary>
    [JsonPropertyName("buildFolder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuildFolder { get; set; }

    /// <summary>The .rpack name the last build used; Build prefills it. Absent until a build names one.</summary>
    [JsonPropertyName("rpackName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RpackName { get; set; }

    /// <summary>The .pak name the last build used; Build prefills it.</summary>
    [JsonPropertyName("pakName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PakName { get; set; }

    /// <summary>Build every clip in the plain form (one 0x40 part), also when its template is a stream pair.</summary>
    [JsonPropertyName("plainClips")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PlainClips { get; set; }

    /// <summary>Fields this version does not know, kept so a save never drops what a newer version wrote.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One thing in a project folder: a family folder, or a loose file.</summary>
public sealed record ProjectEntry(string Name, string FullPath, bool IsDirectory, long Bytes, int Files,
                                  DateTimeOffset Modified);

/// <summary>What a project folder holds right now.</summary>
public sealed record ProjectContents(IReadOnlyList<ProjectEntry> Entries, int Files, long Bytes);

/// <summary>
/// A mod project: a folder on disk that a modder exports resources into, edits, and later builds a pack from.
/// </summary>
/// <remarks>
/// The folder is the project — this class only owns the manifest beside it and the reads/writes that keep it
/// honest. Nothing here builds anything; that is the Build area's job. Contents are whatever is on disk, so a
/// modder can drop files in with Explorer and the panel picks them up on the next scan.
/// </remarks>
public sealed class ModProject
{
    public const string ManifestName = "project.nrproj";
    public const string CurrentSchema = "nightrunner/project@1";

    /// <summary>Folders a new project starts with. "textures" is where edited PNGs live (TextureAsset.Folder).</summary>
    public static readonly string[] StandardFolders =
        ["textures", "mesh", "model", "anim", "sdb", "audio", "other"];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Folder { get; }
    public ProjectManifest Manifest { get; }

    public string ManifestPath => Path.Combine(Folder, ManifestName);

    public const string DefaultBuildFolder = "build";

    /// <summary>
    /// Where this project's build goes: the folder stored in the manifest, else <c>&lt;project&gt;\build</c>. Always
    /// derived from this project — never from whatever a window last showed.
    /// </summary>
    public string BuildFolder => Manifest.BuildFolder is { Length: > 0 } f
        ? Path.GetFullPath(Path.IsPathRooted(f) ? f : Path.Combine(Folder, f))
        : Path.Combine(Folder, DefaultBuildFolder);

    /// <summary>Store a build folder (null or the default clears the field), then save the manifest.</summary>
    public void SetBuildFolder(string? folder)
    {
        string? stored = null;
        if (!string.IsNullOrWhiteSpace(folder))
        {
            var full = Path.GetFullPath(folder);
            if (!full.Equals(Path.Combine(Folder, DefaultBuildFolder), StringComparison.OrdinalIgnoreCase))
                stored = Contains(full) ? Path.GetRelativePath(Folder, full) : full;
        }
        Manifest.BuildFolder = stored;
        Save();
    }

    /// <summary>Remember the output names a build used, then save the manifest when they changed.</summary>
    public void SetOutputNames(string? rpack, string? pak)
    {
        if (Manifest.RpackName == rpack && Manifest.PakName == pak) return;
        Manifest.RpackName = rpack;
        Manifest.PakName = pak;
        Save();
    }

    /// <summary>Remember the plain-clips build option, then save the manifest when it changed.</summary>
    public void SetPlainClips(bool on)
    {
        if (Manifest.PlainClips == on) return;
        Manifest.PlainClips = on;
        Save();
    }

    public string Name => Manifest.Name;

    private ModProject(string folder, ProjectManifest manifest)
    {
        Folder = folder;
        Manifest = manifest;
    }

    public static bool IsProject(string folder) => File.Exists(Path.Combine(folder, ManifestName));

    /// <summary>Make a new project folder (it may already exist, but must not already be a project).</summary>
    public static ModProject Create(string folder, string name, string? gameId)
    {
        folder = Path.GetFullPath(folder);
        if (IsProject(folder))
            throw new ProjectException($"{folder} is already a project");
        Directory.CreateDirectory(folder);
        foreach (var sub in StandardFolders) Directory.CreateDirectory(Path.Combine(folder, sub));

        var project = new ModProject(folder, new ProjectManifest
        {
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(folder) : name.Trim(),
            Game = gameId,
        });
        project.Save();
        return project;
    }

    /// <summary>Open a project by its folder or by its manifest file.</summary>
    public static ModProject Open(string path)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(path) && Path.GetFileName(path).Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
            path = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(path))
            throw new ProjectException($"{path} does not exist");
        var manifestPath = Path.Combine(path, ManifestName);
        if (!File.Exists(manifestPath))
            throw new ProjectException($"{path} has no {ManifestName}");

        ProjectManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(manifestPath), Json)
                       ?? throw new ProjectException($"{manifestPath} is empty");
        }
        catch (JsonException e)
        {
            throw new ProjectException($"{manifestPath}: {e.Message}");
        }
        if (manifest.Name.Length == 0) manifest.Name = Path.GetFileName(path);
        return new ModProject(path, manifest);
    }

    public void Save()
    {
        Manifest.Modified = DateTimeOffset.Now;
        Directory.CreateDirectory(Folder);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(Manifest, Json));
    }

    /// <summary>Top-level contents: one entry per folder (with its recursive totals) or loose file.</summary>
    public ProjectContents Scan()
    {
        var entries = new List<ProjectEntry>();
        int files = 0;
        long bytes = 0;
        if (!Directory.Exists(Folder)) return new ProjectContents(entries, 0, 0);

        foreach (var dir in Directory.EnumerateDirectories(Folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var (count, size) = Measure(dir);
            files += count;
            bytes += size;
            entries.Add(new ProjectEntry(Path.GetFileName(dir), dir, true, size, count,
                                         Directory.GetLastWriteTime(dir)));
        }
        foreach (var file in Directory.EnumerateFiles(Folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(file).Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
            var info = new FileInfo(file);
            files++;
            bytes += info.Length;
            entries.Add(new ProjectEntry(info.Name, file, false, info.Length, 1, info.LastWriteTime));
        }
        return new ProjectContents(entries, files, bytes);
    }

    private static (int Files, long Bytes) Measure(string dir)
    {
        int n = 0;
        long size = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                n++;
                try { size += new FileInfo(f).Length; } catch (IOException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return (n, size);
    }

    /// <summary>Copy files into one of the project's folders. Returns how many were copied.</summary>
    public int Import(IEnumerable<string> paths, string? subFolder = null, bool overwrite = false)
    {
        string target = subFolder is null ? Folder : Path.Combine(Folder, subFolder);
        Directory.CreateDirectory(target);
        int copied = 0;
        foreach (var path in paths)
        {
            var dest = Path.Combine(target, Path.GetFileName(path));
            if (File.Exists(path))
            {
                if (!overwrite && File.Exists(dest)) continue;
                File.Copy(path, dest, overwrite);
                copied++;
            }
            else if (Directory.Exists(path))
            {
                copied += CopyTree(path, dest, overwrite);
            }
        }
        if (copied > 0) Save();
        return copied;
    }

    private static int CopyTree(string from, string to, bool overwrite)
    {
        Directory.CreateDirectory(to);
        int n = 0;
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            if (!overwrite && File.Exists(dest)) continue;
            File.Copy(file, dest, overwrite);
            n++;
        }
        return n;
    }

    /// <summary>A path is inside this project (used before anything is deleted).</summary>
    public bool Contains(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(Folder);
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => $"{Name} — {Folder}";
}
