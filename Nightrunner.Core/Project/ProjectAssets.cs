using System.Text.Json;
using System.Text.Json.Nodes;
using Nightrunner.Core.Cast;
using Nightrunner.Core.Export;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;
using File = System.IO.File;

namespace Nightrunner.Core.Project;

/// <summary>
/// A mesh the project rebuilds: <c>mesh/&lt;output name&gt;/</c> holding the scene the modder edits
/// (<c>&lt;name&gt;.glb</c>/<c>.gltf</c> win over <c>&lt;name&gt;.cast</c>, as the prototype's codec) and the export sidecar
/// <c>&lt;name&gt;.mesh.json</c>, whose <c>source</c> names the template resource (pack label, logical index, name). The
/// folder name is the output resource name: a different name clones the template under that name.
/// </summary>
public sealed record MeshItem(string Folder, string OutputName, string ScenePath, JsonObject Sidecar,
                              string SourcePack, int SourceIndex, string SourceName);

/// <summary>
/// A whole model edited as one scene: <c>scene/&lt;name&gt;/</c> with the edited scene, its export report
/// (<c>&lt;stem&gt;.cast.json</c>) and optional <c>split.json</c> (<c>assign</c>, <c>hide</c>, <c>renames</c>, <c>tol</c>,
/// <c>scene</c>, <c>exported</c>). Which file is built: <see cref="ProjectAssets.EditedScene"/>.
/// </summary>
public sealed record SceneItem(string Folder, string ScenePath, string ReportPath, JsonObject Options);

/// <summary>
/// A <c>.model</c> override: <c>model/&lt;stem&gt;/&lt;basename&gt;</c> (the edited document) and <c>model.json</c> —
/// <c>member</c> (original PAK path), <c>pak</c>, <c>paths</c> (root | original | both), <c>noGear</c>, <c>stash</c>.
/// </summary>
public sealed record ModelItem(string Folder, string DocPath, string Member, string Pak, string PathMode, bool NoGear,
                               JsonObject Settings)
{
    public JsonObject Stash => Settings["stash"] as JsonObject ?? (JsonObject)(Settings["stash"] = new JsonObject());

    public JsonObject Load() => JsonNode.Parse(File.ReadAllText(DocPath))!.AsObject();

    /// <summary>Save the edited document (prototype JSON form) and the settings.</summary>
    public void Save(JsonObject doc)
    {
        File.WriteAllText(DocPath, PyJson.Dumps(doc));
        File.WriteAllText(Path.Combine(Folder, ProjectAssets.ModelSettings), Settings.ToJsonString(ProjectAssets.Indented));
    }
}

/// <summary>
/// A clip the project replaces: <c>anim/&lt;clip&gt;/</c> with the edited scene (<see cref="ProjectAssets.EditedScene"/>)
/// and <c>anim.json</c> — <c>source</c> (the template resource: pack label, logical index, name; the stream copy when one
/// exists), <c>fps</c> (keys are resampled at it), <c>sequence</c> (where it was added from, informational), and
/// optionally <c>scene</c> and <c>exported</c>.
/// </summary>
public sealed record AnimItem(string Folder, string OutputName, string ScenePath, JsonObject Settings,
                              string SourcePack, int SourceIndex, double Fps);

/// <summary>
/// Edits of one pack's <c>Prefabs</c> resource: <c>prefab/&lt;pack&gt;/prefab.json</c> — <c>source</c> (pack label,
/// logical index, name) and <c>edits</c> (<see cref="Prefab.PrefabEditOp"/> in order). The build applies them to the
/// template from the open game and writes the whole edited resource into the project rpack under the same name.
/// </summary>
public sealed record PrefabItem(string Folder, string SettingsPath, string SourcePack, int SourceIndex, string SourceName,
                                IReadOnlyList<Prefab.PrefabEditOp> Edits);

public static class ProjectAssets
{
    public const string MeshFolder = "mesh", SceneFolder = "scene", ModelFolder = "model", AnimFolder = "anim";
    public const string AnimSettings = "anim.json";
    public const string PrefabFolder = "prefab", PrefabSettings = "prefab.json";
    public const string ModelSettings = "model.json", SplitSettings = "split.json";

    /// <summary>
    /// Settings keys of scene and anim items: <c>scene</c> names the file to build; <c>exported</c> records the size and
    /// write time of each scene file Add to project wrote, so an edit is told apart from the export.
    /// </summary>
    public const string SceneKey = "scene", ExportedKey = "exported";
    internal static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly string[] SceneExtensions = [".glb", ".gltf", ".cast"];

    public sealed record Contents(IReadOnlyList<MeshItem> Meshes, IReadOnlyList<SceneItem> Scenes,
                                  IReadOnlyList<ModelItem> Models, IReadOnlyList<string> Problems)
    {
        public IReadOnlyList<AnimItem> Anims { get; init; } = [];
        public IReadOnlyList<PrefabItem> Prefabs { get; init; } = [];

        /// <summary>Items only the full build (<see cref="ProjectBuild.Build"/>) writes: everything but textures.</summary>
        public int FullBuildItems => Meshes.Count + Scenes.Count + Models.Count + Anims.Count + Prefabs.Count;
    }

    public static Contents Scan(ModProject project)
    {
        var meshes = new List<MeshItem>();
        var scenes = new List<SceneItem>();
        var models = new List<ModelItem>();
        var problems = new List<string>();
        foreach (var dir in Folders(project, MeshFolder))
        {
            string name = Path.GetFileName(dir);
            var sidecars = Directory.GetFiles(dir, "*.mesh.json");
            var scene = SceneExtensions.Select(ext => Directory.GetFiles(dir, "*" + ext).FirstOrDefault()).FirstOrDefault(f => f is not null);
            if (sidecars.Length != 1 || scene is null)
            {
                problems.Add($"mesh/{name}: needs one .mesh.json and a .cast/.glb/.gltf");
                continue;
            }
            try
            {
                var sidecar = JsonNode.Parse(File.ReadAllText(sidecars[0]))!.AsObject();
                var src = sidecar["source"] as JsonObject ?? throw new ProjectException("sidecar has no source");
                meshes.Add(new MeshItem(dir, name, scene, sidecar, (string?)src["pack"] ?? "", (int?)src["index"] ?? -1,
                                        (string?)src["name"] ?? name));
            }
            catch (Exception e) when (e is JsonException or ProjectException or InvalidOperationException or FormatException)
            {
                problems.Add($"mesh/{name}: {e.Message}");
            }
        }
        foreach (var dir in Folders(project, SceneFolder))
        {
            string name = Path.GetFileName(dir);
            var report = Directory.GetFiles(dir, "*.cast.json").FirstOrDefault();
            string? stem = report is null ? null : Path.GetFileName(report)[..^".cast.json".Length];
            if (report is null || !SceneExtensions.Any(ext => Directory.GetFiles(dir, "*" + ext).Length > 0))
            {
                problems.Add($"scene/{name}: needs a .cast.json report and a scene");
                continue;
            }
            try
            {
                string opts = Path.Combine(dir, SplitSettings);
                var options = File.Exists(opts) ? JsonNode.Parse(File.ReadAllText(opts))!.AsObject() : new JsonObject();
                scenes.Add(new SceneItem(dir, EditedScene(dir, options, stem + ".cast", SplitSettings), report, options));
            }
            catch (Exception e) when (e is JsonException or ProjectException or InvalidOperationException or FormatException)
            {
                problems.Add($"scene/{name}: {e.Message}");
            }
        }
        foreach (var dir in Folders(project, ModelFolder))
        {
            string name = Path.GetFileName(dir);
            string settings = Path.Combine(dir, ModelSettings);
            var doc = Directory.GetFiles(dir, "*.model").FirstOrDefault();
            if (!File.Exists(settings) || doc is null)
            {
                problems.Add($"model/{name}: needs model.json and a .model");
                continue;
            }
            var s = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
            models.Add(new ModelItem(dir, doc, (string?)s["member"] ?? Path.GetFileName(doc), (string?)s["pak"] ?? "data0.pak",
                                     (string?)s["paths"] ?? "root", (bool?)s["noGear"] ?? false, s));
        }
        var anims = new List<AnimItem>();
        foreach (var dir in Folders(project, AnimFolder))
        {
            string name = Path.GetFileName(dir);
            string settings = Path.Combine(dir, AnimSettings);
            if (!File.Exists(settings) || !SceneExtensions.Any(ext => Directory.GetFiles(dir, "*" + ext).Length > 0))
            {
                problems.Add($"anim/{name}: needs anim.json and a .cast/.glb/.gltf");
                continue;
            }
            try
            {
                var s = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
                var src = s["source"] as JsonObject ?? throw new ProjectException("anim.json has no source");
                string scene = EditedScene(dir, s, name + ".cast", AnimSettings);
                anims.Add(new AnimItem(dir, (string?)src["name"] ?? name, scene, s, (string?)src["pack"] ?? "", (int?)src["index"] ?? -1,
                                       (double?)s["fps"] ?? 30));
            }
            catch (Exception e) when (e is JsonException or ProjectException or InvalidOperationException or FormatException)
            {
                problems.Add($"anim/{name}: {e.Message}");
            }
        }
        var prefabs = new List<PrefabItem>();
        foreach (var dir in Folders(project, PrefabFolder))
        {
            string name = Path.GetFileName(dir);
            string settings = Path.Combine(dir, PrefabSettings);
            if (!File.Exists(settings))
            {
                problems.Add($"prefab/{name}: needs {PrefabSettings}");
                continue;
            }
            try { prefabs.Add(ReadPrefabItem(dir, settings)); }
            catch (Exception e) when (e is JsonException or ProjectException or InvalidOperationException or FormatException)
            {
                problems.Add($"prefab/{name}: {e.Message}");
            }
        }
        return new Contents(meshes, scenes, models, problems) { Anims = anims, Prefabs = prefabs };
    }

    /// <summary>
    /// Put a clip in the project for editing: the whole clip (every key, not only one SeqTrack's range) as
    /// <c>&lt;clip&gt;.cast</c>, plus <c>&lt;clip&gt;.glb</c> with <paramref name="skeleton"/> when that binds any track. The
    /// template is the clip's stream copy (0x44 + 0x45, the layer the engine searches first) when a pack has one.
    /// </summary>
    public static AnimItem AddAnim(ModProject project, RpackCatalog catalog, string clipName, double fps, ModelSkeleton? skeleton,
                                   string? sequence = null)
    {
        var where = Anim.AnimCatalog.Clip(catalog, clipName) ?? throw new ProjectException($"clip {clipName} is in no loaded pack");
        string dir = Path.Combine(project.Folder, AnimFolder, RawExporter.SafeName(clipName));
        if (Directory.Exists(dir)) throw new ProjectException($"anim/{Path.GetFileName(dir)} already exists");
        var clip = Anim.Anm2Resource.Decode(where.Pack, where.Index);
        if (clip.IsPoseWeights) throw new ProjectException($"{clipName}: facial pose weights are not edited as bone animation");
        Directory.CreateDirectory(dir);
        string safe = RawExporter.SafeName(clipName);
        var names = (IReadOnlyList<string>?)skeleton?.Names ?? [];
        Anim.AnimCast.Build(clip, names, (float)fps, 0, clip.FrameBound).Save(Path.Combine(dir, safe + ".cast"));
        if (skeleton is not null && clip.TrackHashes.Any(h => names.Any(n => Anim.Anm2Hash.H41(n) == h)))
            Gltf.Save(Anim.AnimCast.Scene(clip, skeleton, clipName, (float)fps, 0, clip.FrameBound), Path.Combine(dir, safe + ".glb"));
        var label = catalog.Packs.First(p => ReferenceEquals(p.Pack, where.Pack)).Label;
        var settings = new JsonObject
        {
            ["source"] = new JsonObject { ["pack"] = label, ["index"] = where.Index, ["name"] = clipName },
            ["fps"] = fps, ["sequence"] = sequence, [ExportedKey] = Exported(dir),
        };
        File.WriteAllText(Path.Combine(dir, AnimSettings), settings.ToJsonString(Indented));
        project.Save();
        return Scan(project).Anims.First(m => m.Folder.Equals(dir, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The scene file a scene or anim item builds — the file the modder edited, not merely the newest one:
    /// <list type="number">
    /// <item><c>scene</c> in the item's settings names it (it must exist);</item>
    /// <item>else the one scene file (.cast/.glb/.gltf) changed since Add to project: a size or write time other than
    /// <c>exported</c> records, or a file it does not list. Items added before that record: a file written more than
    /// two seconds after the oldest scene file (the export writes its files together);</item>
    /// <item>none changed: the export's own <paramref name="exportName"/> (the lossless Cast, which keeps prop and root
    /// tracks a .glb drops).</item>
    /// </list>
    /// More than one changed is ambiguous and refused by name: set <c>scene</c> in <paramref name="settingsFile"/>.
    /// </summary>
    public static string EditedScene(string dir, JsonObject settings, string exportName, string settingsFile)
    {
        if (settings[SceneKey] is JsonNode named)
        {
            string file = (string?)named ?? "";
            string path = Path.Combine(dir, file);
            if (file != Path.GetFileName(file) || !SceneExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()) || !File.Exists(path))
                throw new ProjectException($"{settingsFile} names scene '{file}', which is not a .cast/.glb/.gltf in the folder");
            return path;
        }
        var candidates = SceneFiles(dir);
        var changed = ChangedScenes(dir, settings);
        if (changed.Count == 1) return changed[0];
        if (changed.Count > 1)
            throw new ProjectException($"{string.Join(", ", changed.Select(Path.GetFileName))} all changed since the export: " +
                                       $"name the one to build as \"{SceneKey}\" in {settingsFile}");
        string export = Path.Combine(dir, exportName);
        if (File.Exists(export)) return export;
        if (candidates.Count == 1) return candidates[0];
        throw new ProjectException($"no {exportName} and nothing changed since the export: name the scene to build as \"{SceneKey}\" in {settingsFile}");
    }

    private static List<string> SceneFiles(string dir) =>
        SceneExtensions.SelectMany(ext => Directory.GetFiles(dir, "*" + ext)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The scene files of a scene or anim item changed since Add to project: a size or write time other than
    /// <see cref="ExportedKey"/> records, or a file it does not list; items added before that record: a file written more
    /// than two seconds after the oldest scene file. <see cref="EditedScene"/> builds the one changed file.
    /// </summary>
    public static List<string> ChangedScenes(string dir, JsonObject settings)
    {
        var candidates = SceneFiles(dir);
        if (settings[ExportedKey] is JsonObject record)
            return candidates.Where(f => record[Path.GetFileName(f)] is not JsonObject e || (long?)e["bytes"] != new FileInfo(f).Length
                                         || !DateTime.TryParse((string?)e["modified"], null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
                                         || Math.Abs((File.GetLastWriteTimeUtc(f) - t.ToUniversalTime()).TotalSeconds) > 2).ToList();
        if (candidates.Count == 0) return [];
        var oldest = candidates.Min(File.GetLastWriteTimeUtc);
        return candidates.Where(f => (File.GetLastWriteTimeUtc(f) - oldest).TotalSeconds > 2).ToList();
    }

    /// <summary>Size and write time of every scene file in a folder, for <see cref="ExportedKey"/>.</summary>
    public static JsonObject Exported(string dir)
    {
        var record = new JsonObject();
        foreach (var f in SceneExtensions.SelectMany(ext => Directory.GetFiles(dir, "*" + ext)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            record[Path.GetFileName(f)] = new JsonObject
            {
                ["bytes"] = new FileInfo(f).Length, ["modified"] = File.GetLastWriteTimeUtc(f).ToString("o"),
            };
        return record;
    }

    private static IEnumerable<string> Folders(ModProject project, string family)
    {
        string root = Path.Combine(project.Folder, family);
        return Directory.Exists(root) ? Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase) : [];
    }

    /// <summary>
    /// Put a mesh in the project for editing: its per-mesh Cast, glb and sidecar into <c>mesh/&lt;name&gt;/</c>. The
    /// sidecar's source records the pack by its label (relative to the assets folder), so the project is portable.
    /// </summary>
    public static MeshItem AddMesh(ModProject project, RpackCatalog catalog, int gid, string? outputName = null)
    {
        var (entry, index) = catalog.Split(gid);
        string name = catalog.Name(gid).Trim();
        string output = string.IsNullOrWhiteSpace(outputName) ? name : outputName.Trim();
        string dir = Path.Combine(project.Folder, MeshFolder, RawExporter.SafeName(output));
        if (Directory.Exists(dir)) throw new ProjectException($"mesh/{Path.GetFileName(dir)} already exists");
        Directory.CreateDirectory(dir);
        var model = MeshDecoder.Decode(entry.Pack!, index);
        string safe = RawExporter.SafeName(name);
        CastExport.WriteFiles(model, name, Path.Combine(dir, safe + ".cast"), new MeshSidecarSource(entry.Label, index, name));
        project.Save();
        return Scan(project).Meshes.First(m => m.Folder.Equals(dir, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Put a <c>.model</c> in the project: the member as stored plus its settings (player models: no gear).</summary>
    public static ModelItem AddModel(ModProject project, ModelCatalog models, ModelEntry entry)
    {
        string stem = Path.GetFileNameWithoutExtension(entry.Basename);
        string dir = Path.Combine(project.Folder, ModelFolder, RawExporter.SafeName(stem));
        if (Directory.Exists(dir)) throw new ProjectException($"model/{Path.GetFileName(dir)} already exists");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, entry.Basename), models.Pak(entry.Pak).Read(entry.Member));
        var settings = new JsonObject
        {
            ["member"] = entry.Name, ["pak"] = Path.GetFileName(entry.Pak), ["paths"] = "root",
            ["noGear"] = ModelEdit.IsPlayer(entry.Name), ["stash"] = new JsonObject(),
        };
        File.WriteAllText(Path.Combine(dir, ModelSettings), settings.ToJsonString(Indented));
        project.Save();
        return Scan(project).Models.First(m => m.Folder.Equals(dir, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Record prefab edits in the project: appended to <c>prefab/&lt;pack&gt;/prefab.json</c> of the pack whose
    /// <c>Prefabs</c> resource they edit (created on first use). Before anything is written the whole edit list is
    /// applied to the template and validated, so a refused edit never reaches the project.
    /// </summary>
    public static PrefabItem AddPrefabEdits(ModProject project, RpackCatalog catalog, string packLabel, IReadOnlyList<Prefab.PrefabEditOp> ops)
    {
        var entry = catalog.Packs.FirstOrDefault(p => p.Label.Equals(packLabel, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ProjectException($"pack {packLabel} is not in the open game");
        var pack = entry.Pack ?? throw new ProjectException($"pack {packLabel} is not indexed");
        int index = Prefab.PrefabContainer.ResourcesIn(pack) is [var only] ? only
            : throw new ProjectException($"{packLabel}: expected one Prefabs resource");
        string dir = Path.Combine(project.Folder, PrefabFolder, RawExporter.SafeName(Path.GetFileNameWithoutExtension(packLabel)));
        string settings = Path.Combine(dir, PrefabSettings);
        var existing = File.Exists(settings) ? ReadPrefabItem(dir, settings) : null;
        if (existing is not null && (existing.SourceIndex != index || !existing.SourcePack.Equals(entry.Label, StringComparison.OrdinalIgnoreCase)))
            throw new ProjectException($"prefab/{Path.GetFileName(dir)} edits {existing.SourcePack}:{existing.SourceIndex}");
        var all = (existing?.Edits ?? []).Concat(ops).ToList();
        try { Prefab.PrefabEdits.Apply(Prefab.PrefabContainer.Read(pack, index), all); }
        catch (Prefab.PrefabFormatException e) { throw new ProjectException(e.Message); }
        Directory.CreateDirectory(dir);
        var doc = new JsonObject
        {
            ["source"] = new JsonObject { ["pack"] = entry.Label, ["index"] = index, ["name"] = pack.Name(index) },
            ["edits"] = new JsonArray(all.Select(o => (JsonNode)EditJson(o)).ToArray()),
        };
        File.WriteAllText(settings, doc.ToJsonString(Indented));
        project.Save();
        return ReadPrefabItem(dir, settings);
    }

    private static PrefabItem ReadPrefabItem(string dir, string settings)
    {
        var s = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        var src = s["source"] as JsonObject ?? throw new ProjectException($"{PrefabSettings} has no source");
        var edits = (s["edits"] as JsonArray ?? []).Select(n => EditOp(n?.AsObject() ?? throw new ProjectException("null edit"))).ToList();
        return new PrefabItem(dir, settings, (string?)src["pack"] ?? "", (int?)src["index"] ?? -1, (string?)src["name"] ?? "Prefabs", edits);
    }

    public static JsonObject EditJson(Prefab.PrefabEditOp o)
    {
        var j = new JsonObject { ["op"] = o.Op, ["prefab"] = o.Prefab };
        if (o.Op != "rename") j["pcid"] = o.Pcid;
        if (o.Field is not null) j["field"] = o.Field;
        if (o.Text is not null) j["text"] = o.Text;
        if (o.Value is { } v) j["value"] = v;
        if (o.Active is { } a) j["active"] = a;
        static JsonArray V(Prefab.Vec3 x) => [x.X, x.Y, x.Z];
        if (o.Translate is { } t) j["translate"] = V(t);
        if (o.Rotate is { } r) j["rotate"] = V(r);
        if (o.Scale is { } sc) j["scale"] = V(sc);
        return j;
    }

    public static Prefab.PrefabEditOp EditOp(JsonObject j)
    {
        static Prefab.Vec3? V(JsonNode? n) => n is JsonArray { Count: 3 } a ? new Prefab.Vec3((float)a[0]!, (float)a[1]!, (float)a[2]!) : null;
        return new Prefab.PrefabEditOp((string?)j["op"] ?? throw new ProjectException("edit without op"),
                                       (string?)j["prefab"] ?? throw new ProjectException("edit without prefab"), (uint?)j["pcid"] ?? 0)
        {
            Field = (string?)j["field"], Text = (string?)j["text"], Value = (double?)j["value"], Active = (bool?)j["active"],
            Translate = V(j["translate"]), Rotate = V(j["rotate"]), Scale = V(j["scale"]),
        };
    }

    /// <summary>
    /// Put a whole model in the project as one scene (<see cref="ModelCast"/>): the modder edits it and saves the
    /// result next to it; at build it is split back per mesh.
    /// </summary>
    public static SceneItem AddScene(ModProject project, ModelCatalog models, ModelEntry entry, RpackCatalog catalog,
                                     Func<int, MeshModel> decode, Func<int, MeshModel, int>? skinOf = null)
    {
        string stem = Path.GetFileNameWithoutExtension(entry.Basename);
        string dir = Path.Combine(project.Folder, SceneFolder, RawExporter.SafeName(stem));
        if (Directory.Exists(dir)) throw new ProjectException($"scene/{Path.GetFileName(dir)} already exists");
        var res = ModelResolver.Resolve(models.Load(entry), catalog, null, decode, skinOf);
        var written = ModelCast.Write(res, catalog, decode, dir, stem);
        Gltf.Save(written.Cast, Path.ChangeExtension(written.CastPath, ".glb"), dir);
        File.WriteAllText(Path.Combine(dir, SplitSettings), new JsonObject { [ExportedKey] = Exported(dir) }.ToJsonString(Indented));
        project.Save();
        return Scan(project).Scenes.First(s => s.Folder.Equals(dir, StringComparison.OrdinalIgnoreCase));
    }
}
