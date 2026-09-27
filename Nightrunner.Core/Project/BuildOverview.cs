using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;
using File = System.IO.File;

namespace Nightrunner.Core.Project;

/// <summary>
/// One item of a project as the build sees it, read without building: what it outputs (resource name, form, which
/// output file), what it is built from (template and the source file the build reads), whether that file changed since
/// the export, the edits pending, and what the build would refuse that is visible up front.
/// </summary>
public sealed class BuildItemInfo
{
    /// <summary>texture | mesh | scene | model | anim | prefab.</summary>
    public required string Kind { get; init; }

    /// <summary>The output resource name (scene: its folder; model: the document's file name).</summary>
    public required string Name { get; init; }

    /// <summary>The item's folder (texture: the textures folder).</summary>
    public required string Folder { get; init; }

    /// <summary>How the build's report and warnings name it: <c>mesh/&lt;folder&gt;</c> etc.; a texture: its resource name.</summary>
    public string Key { get; init; } = "";

    /// <summary>The file the build reads (scenes and clips: <see cref="ProjectAssets.EditedScene"/>).</summary>
    public string? Source { get; init; }

    /// <summary>The source changed since Add to project wrote it; null when that is not known.</summary>
    public bool? Changed { get; init; }

    /// <summary><c>pack:index</c> of the template resource; a model: <c>pak:member</c>; a scene: the model it came from.</summary>
    public string Template { get; init; } = "";

    /// <summary>Resource type and form: <c>0x20 BC1</c>, <c>0x10</c>, <c>stream 0x44+0x45</c>, <c>plain 0x40</c>, ...</summary>
    public string Form { get; init; } = "";

    /// <summary>The output it lands in: rpack | anims | pak (the <see cref="BuildOutput.Kind"/>).</summary>
    public string Pack { get; init; } = "";

    public string Edits { get; init; } = "";

    /// <summary>What the build refuses about this item, found without building, or null.</summary>
    public string? Refusal { get; init; }

    /// <summary>The scan record: <see cref="TextureAsset"/>, <see cref="MeshItem"/>, <see cref="SceneItem"/>, ...</summary>
    public object? Item { get; init; }

    /// <summary>A model to show for it: a scene's model, a model override's member.</summary>
    public string? Model { get; init; }

    /// <summary>Output resources known before the build, as <c>&lt;pack&gt;/&lt;name&gt;</c> (a scene's meshes come from the report).</summary>
    public IReadOnlyList<string> Outputs { get; init; } = [];

    /// <summary>
    /// Numbers read with the item, by name (absent: not known): a mesh's <c>vertices</c> and <c>submeshes</c> (its
    /// sidecar); a scene's <c>parts</c>, <c>exported</c> submeshes, objects <c>assigned</c> / <c>skipped</c>, <c>hidden</c>,
    /// <c>renamed</c> (split.json); a model's <c>off</c>, <c>swap</c>, <c>added</c>, <c>rtti</c> against the stock member
    /// (without the game: <c>stashed</c> only); a clip's <c>stream</c> (template has the stream pair) and <c>forced</c>
    /// (plain clips on); a prefab's <c>edits</c>.
    /// </summary>
    public IReadOnlyDictionary<string, int> Counts { get; init; } = new Dictionary<string, int>();
}

/// <summary>
/// What one build (or check) report says about one item: <paramref name="Result"/> (built … / ok … / skipped / refused /
/// invalid / —), its warnings, and each of its output resources with its diff state.
/// </summary>
public sealed record BuildItemState(string Result, IReadOnlyList<string> Warnings, IReadOnlyList<KeyValuePair<string, string>> Resources)
{
    private static readonly string[] Order = ["added", "changed", "removed", "unknown", "unchanged"];

    /// <summary>The strongest diff state of its resources (added, changed, unknown, unchanged), or "".</summary>
    public string Diff => Resources.Count == 0 ? ""
        : Order.FirstOrDefault(s => Resources.Any(r => r.Value == s)) ?? Resources[0].Value;

    public static readonly BuildItemState None = new("", [], []);
}

/// <summary>A report laid over the items: one state per item (same order), what no item claims, and the verdict.</summary>
public sealed record BuildReportView(IReadOnlyList<BuildItemState> Items, IReadOnlyList<string> Other, IReadOnlyList<string> Removed,
                                     string Verdict, bool Verified, bool Check, string? Refused);

public static partial class ProjectBuild
{
    /// <summary>
    /// Every item of the project, one entry each, in the order textures, meshes, scenes, models, clips, prefab edits,
    /// then folders the scan could not read. <paramref name="catalog"/> (the open game, optional) resolves templates
    /// and clip forms; <paramref name="models"/> (optional) gives a model override's original to count its edits. Reads
    /// files only; nothing is built or written.
    /// </summary>
    public static List<BuildItemInfo> Overview(ModProject project, RpackCatalog? catalog, ModelCatalog? models, bool plainClips,
                                               string? gameId = null)
    {
        string game = project.Manifest.Game ?? gameId ?? "dltb";
        string? geometryRefusal = game == "dltb" ? null : $"{game.ToUpperInvariant()} mesh and model building is not supported (DLTB only)";
        string? animRefusal = game == "dltb" ? null : $"{game.ToUpperInvariant()} animation building is not supported (DLTB only)";
        var list = new List<BuildItemInfo>();

        // ---- textures ----------------------------------------------------------------------------------------------
        var (ready, orphans) = TextureAsset.Scan(project.Folder);
        var sources = ready.GroupBy(r => r.Asset.Resource, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (source, a) in ready)
        {
            string? refusal = sources[a.Resource] > 1
                ? $"{sources[a.Resource]} source files describe it ({Path.GetFileName(source)} is one); keep one"
                : TextureSource.Refusal(source, a);
            list.Add(new BuildItemInfo
            {
                Kind = "texture", Name = a.Resource, Folder = Path.GetDirectoryName(source)!, Key = a.Resource, Source = source,
                Changed = Newer(source, source + TextureAsset.Extension), Template = $"{a.SourcePack}:{a.SourceIndex}",
                Form = $"0x20 {a.FormatName} {a.Width}x{a.Height}",
                Pack = "rpack", Edits = TextureEdits(source, a), Refusal = refusal, Item = a, Outputs = [$"rpack/{a.Resource}"],
            });
        }
        foreach (var orphan in orphans)
            list.Add(new BuildItemInfo
            {
                Kind = "texture", Name = Path.GetFileName(orphan), Folder = Path.GetDirectoryName(orphan)!, Key = Path.GetFileName(orphan),
                Source = orphan, Refusal = $"no {TextureAsset.Extension} beside it: skipped (add it from the Textures window)",
            });

        var c = ProjectAssets.Scan(project);

        // ---- meshes ------------------------------------------------------------------------------------------------
        foreach (var m in c.Meshes)
        {
            string key = $"{ProjectAssets.MeshFolder}/{Path.GetFileName(m.Folder)}";
            string? sidecar = Directory.GetFiles(m.Folder, "*.mesh.json").FirstOrDefault();
            var geometry = (m.Sidecar["geometry_entries"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var meshCounts = new Dictionary<string, int>();
            if (geometry.Count > 0)
            {
                meshCounts["vertices"] = geometry.Sum(g => (int?)g["vertex_count"] ?? 0);
                meshCounts["submeshes"] = geometry.Sum(g => (g["submeshes"] as JsonArray)?.Count ?? 0);
            }
            list.Add(new BuildItemInfo
            {
                Kind = "mesh", Name = m.OutputName, Folder = m.Folder, Key = key, Source = m.ScenePath,
                Changed = sidecar is null ? null : Newer(m.ScenePath, sidecar), Template = $"{m.SourcePack}:{m.SourceIndex}",
                Form = "0x10", Pack = "rpack",
                Edits = m.OutputName.Equals(m.SourceName.Trim(), StringComparison.OrdinalIgnoreCase) ? "" : $"clone of {m.SourceName.Trim()}",
                Refusal = geometryRefusal ?? (catalog is null ? null : TemplateRefusal(catalog, m.SourcePack, m.SourceIndex, key)),
                Item = m, Outputs = [$"rpack/{m.OutputName}"], Counts = meshCounts,
            });
        }

        // ---- model scenes ------------------------------------------------------------------------------------------
        foreach (var s in c.Scenes)
        {
            string key = $"{ProjectAssets.SceneFolder}/{Path.GetFileName(s.Folder)}";
            JsonObject? report = null;
            try { report = JsonNode.Parse(File.ReadAllText(s.ReportPath)) as JsonObject; }
            catch (Exception e) when (e is JsonException or IOException) { }
            var parts = (report?["parts"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            string? refusal = geometryRefusal;
            if (refusal is null && catalog is not null)
                foreach (var part in parts)
                    if (TemplateRefusal(catalog, (string?)part["pack"] ?? "", (int?)part["index"] ?? -1, key) is { } bad) { refusal = bad; break; }
            list.Add(new BuildItemInfo
            {
                Kind = "scene", Name = Path.GetFileName(s.Folder), Folder = s.Folder, Key = key, Source = s.ScenePath,
                Changed = ProjectAssets.ChangedScenes(s.Folder, s.Options).Count > 0, Template = (string?)report?["model"] ?? "",
                Form = $"0x10 ×{parts.Count}", Pack = "rpack", Edits = SceneEdits(s.Options), Refusal = refusal, Item = s,
                Model = (string?)report?["model"], Counts = SceneCounts(s.Options, report),
            });
        }

        // ---- .model overrides --------------------------------------------------------------------------------------
        foreach (var m in c.Models)
        {
            var outputs = ModelEdit.MemberPaths(m.Member, m.PathMode).Select(p => $"pak/{MemberKey(p)}").ToList();
            if (m.NoGear) outputs.AddRange(ModelEdit.MemberPaths(ModelEdit.OutfitScript, m.PathMode).Select(p => $"pak/{MemberKey(p)}"));
            var (changed, edits, modelCounts) = ModelEdits(m, models);
            list.Add(new BuildItemInfo
            {
                Kind = "model", Name = Path.GetFileName(m.DocPath), Folder = m.Folder,
                Key = $"{ProjectAssets.ModelFolder}/{Path.GetFileName(m.Folder)}", Source = m.DocPath, Changed = changed,
                Template = $"{m.Pak}:{m.Member}", Form = m.NoGear ? $"model {m.PathMode} + scr" : $"model {m.PathMode}",
                Pack = "pak", Edits = edits, Refusal = geometryRefusal, Item = m, Model = m.Member, Outputs = outputs, Counts = modelCounts,
            });
        }

        // ---- clips -------------------------------------------------------------------------------------------------
        foreach (var a in c.Anims)
        {
            string key = $"{ProjectAssets.AnimFolder}/{Path.GetFileName(a.Folder)}";
            string form = "0x40";
            string? refusal = animRefusal;
            var animCounts = new Dictionary<string, int>();
            if (catalog is not null)
            {
                try
                {
                    var (pack, index) = TemplateOf(catalog, a.SourcePack, a.SourceIndex, key, Anim.Anm2Resource.TypeAnimation);
                    var lg = pack.Logicals[index];
                    bool pair = Enumerable.Range((int)lg.FirstPart, lg.PartCount).Any(p => pack.PartType(p) == Anim.Anm2Resource.PartHeader);
                    form = !pair ? "plain 0x40" : plainClips ? "plain 0x40 (forced)" : "stream 0x44+0x45";
                    animCounts["stream"] = pair ? 1 : 0;
                    animCounts["forced"] = pair && plainClips ? 1 : 0;
                }
                catch (ProjectException e) { refusal ??= e.Message; }
            }
            list.Add(new BuildItemInfo
            {
                Kind = "anim", Name = a.OutputName, Folder = a.Folder, Key = key, Source = a.ScenePath,
                Changed = ProjectAssets.ChangedScenes(a.Folder, a.Settings).Count > 0, Template = $"{a.SourcePack}:{a.SourceIndex}",
                Form = $"{form} {a.Fps.ToString("0.##", CultureInfo.InvariantCulture)} fps", Pack = "anims", Refusal = refusal, Item = a,
                Outputs = [$"anims/{a.OutputName}"], Counts = animCounts,
            });
        }

        // ---- prefab edits ------------------------------------------------------------------------------------------
        foreach (var pf in c.Prefabs)
        {
            string key = $"{ProjectAssets.PrefabFolder}/{Path.GetFileName(pf.Folder)}";
            string? refusal = game != "dltb" ? $"{key}: {game.ToUpperInvariant()} prefabs are not edited (DLTB only)"
                : c.Prefabs.Count > 1 ? $"edits of {c.Prefabs.Count} packs' Prefabs resources: one per build (they share the name 'Prefabs')"
                : catalog is null ? null : TemplateRefusal(catalog, pf.SourcePack, pf.SourceIndex, key, Prefab.PrefabContainer.ResourceType);
            list.Add(new BuildItemInfo
            {
                Kind = "prefab", Name = pf.SourceName, Folder = pf.Folder, Key = key, Source = pf.SettingsPath,
                Template = $"{pf.SourcePack}:{pf.SourceIndex}", Form = $"0x{Prefab.PrefabContainer.ResourceType:X2}", Pack = "rpack",
                Edits = $"{pf.Edits.Count} edits", Refusal = refusal, Item = pf, Outputs = [$"rpack/{pf.SourceName}"],
                Counts = new Dictionary<string, int> { ["edits"] = pf.Edits.Count },
            });
        }

        // ---- folders the scan could not read -----------------------------------------------------------------------
        foreach (var problem in c.Problems)
        {
            int colon = problem.IndexOf(": ", StringComparison.Ordinal);
            string where = colon > 0 ? problem[..colon] : "";
            list.Add(new BuildItemInfo
            {
                Kind = where.Split('/')[0], Name = where.Contains('/') ? where[(where.IndexOf('/') + 1)..] : where,
                Folder = Path.Combine(project.Folder, where), Key = where, Refusal = colon > 0 ? problem[(colon + 2)..] : problem,
            });
        }
        return list;
    }

    /// <summary><paramref name="file"/> was written more than two seconds after <paramref name="reference"/> (null: either is missing).</summary>
    private static bool? Newer(string file, string reference) =>
        File.Exists(file) && File.Exists(reference) ? (File.GetLastWriteTimeUtc(file) - File.GetLastWriteTimeUtc(reference)).TotalSeconds > 2 : null;

    /// <summary>A texture's pending size and format change: the PNG's own size (its IHDR) against the original's.</summary>
    private static string TextureEdits(string source, TextureAsset a)
    {
        var edits = new List<string>();
        if (Path.GetExtension(source).Equals(".png", StringComparison.OrdinalIgnoreCase) && PngSize(source) is { } size && (size.Width != a.Width || size.Height != a.Height))
            edits.Add($"→{size.Width}x{size.Height}");
        if (a.BuildFormat is not null && a.EffectiveFormat != a.Format) edits.Add($"→{a.EffectiveFormatName}");
        return string.Join(" · ", edits);
    }

    /// <summary>Width and height from a PNG's IHDR, or null when the file is not a PNG.</summary>
    public static (int Width, int Height)? PngSize(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> head = stackalloc byte[24];
            if (f.Read(head) != 24 || head[12] != (byte)'I' || head[13] != (byte)'H' || head[14] != (byte)'D' || head[15] != (byte)'R') return null;
            return (BinaryPrimitives.ReadInt32BigEndian(head[16..]), BinaryPrimitives.ReadInt32BigEndian(head[20..]));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A scene's split settings: objects assigned (or skipped), hidden, renamed, made double-sided.</summary>
    private static string SceneEdits(JsonObject options)
    {
        static int Count(JsonNode? n) => n switch { JsonObject o => o.Count, JsonArray a => a.Count, _ => 0 };
        var parts = new List<string>();
        foreach (var (key, label) in new[] { ("assign", "assign"), ("hide", "hide"), ("renames", "rename"), ("double_sided", "2-sided") })
            if (Count(options[key]) is > 0 and var n) parts.Add($"{n} {label}");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// A scene's split settings and export report as numbers: parts and exported submeshes (the report), objects assigned
    /// to a submesh and skipped (<c>assign</c>: a target, or empty), hidden submeshes, renames.
    /// </summary>
    private static Dictionary<string, int> SceneCounts(JsonObject options, JsonObject? report)
    {
        var counts = new Dictionary<string, int>();
        if (report is not null)
        {
            counts["parts"] = (report["parts"] as JsonArray)?.Count ?? 0;
            counts["exported"] = (report["mesh_map"] as JsonArray)?.Count ?? 0;
        }
        var assign = (options["assign"] as JsonObject ?? []).Select(kv => kv.Value is JsonValue v && v.TryGetValue(out string? s) ? s : null).ToList();
        counts["assigned"] = assign.Count(s => s is { Length: > 0 });
        counts["skipped"] = assign.Count(s => s is { Length: 0 });
        counts["hidden"] = options["hide"] switch { JsonArray a => a.Count, JsonObject o => o.Count, _ => 0 };
        counts["renamed"] = options["renames"] switch { JsonArray a => a.Count, JsonObject o => o.Count, _ => 0 };
        return counts;
    }

    /// <summary>
    /// A model override against the member it was added from (the PAK named in its settings): whether the document
    /// differs from it, and slots turned off, drawn meshes swapped, slots added and rttiValues set. Without the open
    /// game's PAKs: the stashed (turned-off) slots only.
    /// </summary>
    private static (bool? Changed, string Edits, Dictionary<string, int> Counts) ModelEdits(ModelItem m, ModelCatalog? models)
    {
        JsonObject edited;
        try { edited = m.Load(); }
        catch (Exception e) when (e is JsonException or IOException) { return (null, "unreadable", []); }
        JsonObject? original = null;
        bool? changed = null;
        if (models?.PakPaths.FirstOrDefault(p => Path.GetFileName(p).Equals(m.Pak, StringComparison.OrdinalIgnoreCase)) is { } pakPath)
        {
            try
            {
                var pak = models.Pak(pakPath);
                var bytes = pak.Read(pak.Find(m.Member));
                changed = !File.ReadAllBytes(m.DocPath).AsSpan().SequenceEqual(bytes);
                original = JsonNode.Parse(bytes) as JsonObject;
            }
            catch (Exception e) when (e is ModelFormatException or JsonException or IOException) { }
        }
        if (original is null)
            return (changed, m.Stash.Count > 0 ? $"{m.Stash.Count} off" : "", new Dictionary<string, int> { ["stashed"] = m.Stash.Count });

        static List<JsonObject> SlotList(JsonObject doc) => (doc["slots"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        static JsonObject? Drawn(JsonObject slot) => (slot["meshResources"]?["resources"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault();
        static IEnumerable<string> Rtti(JsonObject doc) =>
            SlotList(doc).SelectMany(s => Drawn(s) is not { } d ? [] :
                (d["materialsResources"] as JsonArray ?? []).OfType<JsonObject>().SelectMany(g =>
                    (g["resources"] as JsonArray ?? []).OfType<JsonObject>().SelectMany(e => (e["rttiValues"] as JsonArray ?? []).Select(v =>
                        $"{ModelDocument.Str(s["name"])}|{ModelDocument.Str(d["name"])}|{ModelDocument.Int(g["number"])}|{v?.ToJsonString()}"))));
        var before = SlotList(original).GroupBy(s => ModelDocument.Str(s["name"]) ?? "").ToDictionary(g => g.Key, g => g.First());
        int off = 0, swapped = 0, added = 0;
        foreach (var slot in SlotList(edited))
        {
            string name = ModelDocument.Str(slot["name"]) ?? "";
            if (!before.TryGetValue(name, out var was)) { added++; continue; }
            JsonObject? now = Drawn(slot), then = Drawn(was);
            if (now is null && then is not null) off++;
            else if (now is not null && then is not null
                     && !string.Equals(ModelDocument.Str(now["name"]), ModelDocument.Str(then["name"]), StringComparison.OrdinalIgnoreCase)) swapped++;
        }
        var stock = Rtti(original).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        int rtti = 0;
        foreach (var r in Rtti(edited))
            if (stock.TryGetValue(r, out int left) && left > 0) stock[r] = left - 1;
            else rtti++;
        var parts = new List<string>();
        if (off > 0) parts.Add($"{off} off");
        if (swapped > 0) parts.Add($"{swapped} swap");
        if (added > 0) parts.Add($"{added} new slot");
        if (rtti > 0) parts.Add($"{rtti} rtti");
        if (m.NoGear) parts.Add("no gear");
        var counts = new Dictionary<string, int> { ["off"] = off, ["swap"] = swapped, ["added"] = added, ["rtti"] = rtti, ["stashed"] = m.Stash.Count };
        return (changed, string.Join(" · ", parts), counts);
    }

    /// <summary>
    /// Lay a build or check report over <paramref name="items"/> (<see cref="Overview"/>): each item's result, the
    /// warnings that name it (by its output name, its folder key, or — for a scene — its meshes and scene file), and
    /// the diff state of each of its output resources. Warnings no item claims go to <see cref="BuildReportView.Other"/>.
    /// </summary>
    public static BuildReportView Attribute(IReadOnlyList<BuildItemInfo> items, JsonObject report)
    {
        int n = items.Count;
        bool check = (bool?)report["check"] ?? false, verified = (bool?)report["verified"] ?? false;
        string? refused = (string?)report["refused"];
        string done = check ? "ok" : verified ? "built" : "invalid";
        var results = new string?[n];
        var warnings = Enumerable.Range(0, n).Select(_ => new List<string>()).ToArray();
        var outputs = items.Select(i => i.Outputs.ToList()).ToArray();
        var sceneMeshes = new int[n];

        var prefixes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            prefixes.TryAdd(items[i].Key, i);
            prefixes.TryAdd(items[i].Name, i);
            if (items[i].Kind == "scene" && items[i].Source is { } scene) prefixes.TryAdd(Path.GetFileName(scene), i);
        }
        int Find(Func<BuildItemInfo, bool> match)
        {
            for (int i = 0; i < n; i++) if (match(items[i])) return i;
            return -1;
        }

        foreach (var entry in (report["items"] as JsonArray ?? []).OfType<JsonObject>())
        {
            string kind = (string?)entry["kind"] ?? "", name = (string?)entry["name"] ?? "", from = (string?)entry["from"] ?? "";
            int i;
            switch (kind)
            {
                case "texture":
                    i = Find(x => x.Kind == "texture" && x.Name == name);
                    if (i >= 0)
                    {
                        string line = (string?)entry["result"] ?? "";
                        // "<name>: 512x512 BC1, 10 mips, 174,800 bytes" -> "built 512x512 BC1, 10 mips"
                        string what = line.StartsWith(name + ": ", StringComparison.Ordinal) ? line[(name.Length + 2)..] : "";
                        if (what.EndsWith(" bytes", StringComparison.Ordinal) && what.LastIndexOf(", ", StringComparison.Ordinal) is > 0 and var cut) what = what[..cut];
                        results[i] = what.Length > 0 ? $"{done} {what}" : done;
                    }
                    break;
                case "mesh" when from.StartsWith(ProjectAssets.SceneFolder + "/", StringComparison.Ordinal):
                    i = Find(x => x.Kind == "scene" && x.Key == from);
                    if (i >= 0)
                    {
                        outputs[i].Add($"rpack/{name}");
                        prefixes.TryAdd(name, i);
                        sceneMeshes[i]++;
                    }
                    break;
                case "mesh":
                    i = Find(x => x.Kind == "mesh" && x.Key == from);
                    if (i >= 0)
                        results[i] = (entry["report"]?["entries"] as JsonArray ?? []).OfType<JsonObject>().Select(e => (string?)e["path"]).Distinct().ToList()
                            is { Count: > 0 } paths ? $"{done} {string.Join("/", paths)}" : done;
                    break;
                case "scene":
                    i = Find(x => x.Kind == "scene" && x.Name == name);
                    if (i >= 0) results[i] ??= "split";
                    break;
                case "anim":
                    i = Find(x => x.Kind == "anim" && x.Name == name);
                    if (i >= 0) results[i] = $"{done} {(string?)entry["form"]}, {(int?)entry["tracks"]} tracks, {(int?)entry["keys"]:N0} keys";
                    break;
                case "prefab":
                    i = Find(x => x.Kind == "prefab" && (x.Key == from || x.Name == name));
                    if (i >= 0) results[i] = $"{done}, {(int?)entry["edits"]} edits";
                    break;
                case "model":
                    i = Find(x => x.Kind == "model" && x.Item is ModelItem m && m.Member == (string?)entry["member"]);
                    if (i >= 0) results[i] = done;
                    break;
            }
        }
        for (int i = 0; i < n; i++)
            if (items[i].Kind == "scene" && results[i] is not null)
                results[i] = sceneMeshes[i] > 0 ? $"{done} {sceneMeshes[i]} mesh(es)" : "split, no change";

        int Claim(string text)
        {
            int best = -1, length = -1;
            foreach (var (prefix, i) in prefixes)
                if (prefix.Length > length && text.StartsWith(prefix + ":", StringComparison.Ordinal)) (best, length) = (i, prefix.Length);
            return best;
        }
        var other = new List<string>();
        foreach (var w in (report["warnings"] as JsonArray ?? []).Select(x => (string?)x ?? ""))
        {
            int i = Claim(w);
            if (i >= 0) warnings[i].Add(w);
            else other.Add(w);
        }
        if (refused is not null)
        {
            int i = Claim(refused);
            if (i >= 0)
            {
                results[i] = "refused";
                warnings[i].Insert(0, refused);
            }
            else other.Insert(0, refused);
        }

        var diff = report["diff"] as JsonObject;
        var states = new List<BuildItemState>(n);
        for (int i = 0; i < n; i++)
        {
            string result = results[i] ?? (refused is not null ? "—" : warnings[i].Count > 0 && items[i].Kind == "texture" ? "skipped" : "—");
            bool built = results[i] is { } r && (r.StartsWith(done, StringComparison.Ordinal) || r.StartsWith("split", StringComparison.Ordinal));
            var resources = !built ? [] : outputs[i].Select(k => new KeyValuePair<string, string>(k, (string?)diff?[k] ?? (diff is null ? "unknown" : "—")))
                                                   .ToList();
            states.Add(new BuildItemState(result, warnings[i], resources));
        }
        var removed = (diff ?? []).Where(kv => (string?)kv.Value == "removed").Select(kv => kv.Key).ToList();
        return new BuildReportView(states, other, removed, (string?)report["verdict"] ?? "", verified, check, refused);
    }
}
