using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;
using CastMesh = Nightrunner.Core.Cast.Mesh;
using CastModel = Nightrunner.Core.Cast.Model;

namespace Nightrunner.Core.Cast;

/// <summary>A split the report or the edit makes impossible as a whole (<c>SplitError</c> raised out of the prototype).</summary>
public sealed class ModelSplitException(string message) : Exception(message);

/// <summary>Which rest pose the edited file was baked with, so the splitter undoes exactly that.</summary>
public enum ModelSplitRule
{
    /// <summary><c>nightrunner.model_cast/2</c> (<see cref="ModelCast"/>): <see cref="ModelSkeleton.Merge"/> with part rigs.</summary>
    PartRigs,
    /// <summary><c>nightrunner.model_cast/1</c> (the prototype): every rest pose from the bone's first supplier.</summary>
    FirstSupplier,
}

/// <summary>What the encoder check gives back (see <see cref="ModelSplitOptions.Check"/>).</summary>
public sealed record ModelSplitCheckResult(IReadOnlyDictionary<byte, byte[]> Parts, IReadOnlyList<string> Warnings,
                                           IReadOnlyList<string?> Entries);

/// <summary>Options of <see cref="ModelSplit.Split"/>, as the keyword arguments of <c>split_model_cast</c>.</summary>
public sealed record ModelSplitOptions
{
    /// <summary>Position tolerance of the snap to an original vertex (engine units).</summary>
    public double Tol { get; init; } = 1e-4;
    /// <summary>UV0 tolerance of the snap.</summary>
    public double UvTol { get; init; } = 1e-4;
    /// <summary>Edited object name → target <c>mesh_map</c> name (whole replacement; several objects merge), or "" to skip it.</summary>
    public IReadOnlyDictionary<string, string>? Assign { get; init; }
    /// <summary><c>mesh_map</c> names written as one vertex + one degenerate triangle (a submesh cannot be removed).</summary>
    public IReadOnlyCollection<string>? Hide { get; init; }
    /// <summary>An assigned or hidden submesh also replaces the other LODs of its entity drawing the same material slot.</summary>
    public bool Lods { get; init; } = true;
    /// <summary>Edited objects to give a back side (every vertex again with the normal negated, every triangle reversed).</summary>
    public IReadOnlyCollection<string>? DoubleSided { get; init; }
    /// <summary>
    /// The encoder check (<c>mesh.codec.rebuild_from_files</c>, ignoring bone changes): original parts, logical name,
    /// the written <c>model.cast</c>, the written sidecar → rebuilt parts. Throw to refuse. Null skips the check.
    /// </summary>
    public Func<IReadOnlyDictionary<byte, byte[]>, string, string, JsonObject?, ModelSplitCheckResult>? Check { get; init; }
    /// <summary>
    /// The preset skeleton by (pack label, mesh name) when the report names it without a logical index (the
    /// <c>/2</c> report writes <c>name</c> and <c>pack</c> only).
    /// </summary>
    public Func<string, string, MeshModel?>? SkeletonByName { get; init; }
    /// <summary>The sidecar's <c>source.pack</c> for a pack label (default: the report's <c>pack_path</c>, else the label).</summary>
    public Func<string, string>? PackPath { get; init; }
}

/// <summary>One submesh line of the split report: every field in the prototype's key order.</summary>
public sealed class ModelSplitSubmesh
{
    public OrderedDictionary<string, object?> Fields { get; } = new(StringComparer.Ordinal);
    public string Name => (string)Fields["name"]!;
    public string? Status => Fields.GetValueOrDefault("status") as string;
    public bool Applied => Status == "applied";
    public bool Refused => Status?.StartsWith("REFUSED", StringComparison.Ordinal) == true;
    /// <summary>An integer statistic (vertices, faces, snapped, moved, new, duplicates, …); 0 when absent.</summary>
    public int Stat(string key) => Fields.GetValueOrDefault(key) is int i ? i : 0;
    /// <summary>A flag (in_place, replaced, hidden, kept_template); false when absent.</summary>
    public bool Flag(string key) => Fields.GetValueOrDefault(key) is true;
}

/// <summary>Outcome of the encoder check of one mesh folder.</summary>
public sealed record ModelSplitCheck(bool Ok, IReadOnlyList<string?> Entries, IReadOnlyList<string> Warnings, bool Unchanged, string? Error);

/// <summary>One source mesh: its folder and what happened to each of its submeshes.</summary>
public sealed class ModelSplitMesh
{
    public required string Mesh { get; init; }
    public required string LogicalName { get; init; }
    public string? Dir { get; internal set; }
    public List<ModelSplitSubmesh> Submeshes { get; } = [];
    public List<string> Problems { get; } = [];
    public ModelSplitCheck? Check { get; internal set; }
}

/// <summary>Result of <see cref="ModelSplit.Split"/> (the prototype's result dict, also written as <c>split_report.json</c>).</summary>
public sealed class ModelSplitResult
{
    public required string Edited { get; init; }
    public string? Model { get; init; }
    public required string Format { get; init; }
    public ModelSplitRule Rule { get; init; }
    public List<ModelSplitMesh> Meshes { get; } = [];
    public List<string> Problems { get; } = [];
    public List<string> UnmatchedMeshes { get; } = [];
    public string ReportPath { get; internal set; } = "";

    /// <summary>The report document (dump_json key order; <c>format</c> and <c>rest_rule</c> are additions).</summary>
    public OrderedDictionary<string, object?> ToJson()
    {
        var meshes = Meshes.Select(m =>
        {
            var d = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["mesh"] = m.Mesh, ["logical_name"] = m.LogicalName, ["dir"] = m.Dir,
                ["submeshes"] = m.Submeshes.Select(s => (object?)s.Fields).ToList(),
                ["problems"] = m.Problems.Cast<object?>().ToList(),
                ["check"] = m.Check is not { } c ? null : c.Ok
                    ? new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["ok"] = true, ["entries"] = c.Entries.Cast<object?>().ToList(),
                        ["warnings"] = c.Warnings.Cast<object?>().ToList(), ["unchanged"] = c.Unchanged,
                    }
                    : new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["ok"] = false, ["error"] = c.Error },
            };
            return (object?)d;
        }).ToList();
        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["edited"] = Edited, ["model"] = Model, ["format"] = Format, ["rest_rule"] = RuleName(Rule),
            ["meshes"] = meshes, ["problems"] = Problems.Cast<object?>().ToList(),
            ["unmatched_meshes"] = UnmatchedMeshes.Cast<object?>().ToList(),
        };
    }

    public static string RuleName(ModelSplitRule rule) => rule == ModelSplitRule.PartRigs ? "part_rigs" : "first_supplier";
}

/// <summary>
/// An edited single-file model scene + its <c>.cast.json</c> report → one folder per source mesh (<c>model.cast</c>,
/// <c>mesh.json</c>, the raw parts) and <c>split_report.json</c>. Port of <c>cast/split.py</c> (<c>split_model_cast</c>).
/// </summary>
/// <remarks>
/// For every source mesh: its own per-mesh Cast (<see cref="CastExport.Build"/>) is the template; the merged skeleton is
/// rebuilt as the file was baked (<see cref="ModelSplitRule"/>: <c>/2</c> with <see cref="ModelSkeleton.Merge"/>,
/// <c>/1</c> with the prototype's first-supplier rest) and checked against the report's bone list; each edited vertex is
/// moved back with the inverse of <c>Σ w · Rest[b] · inv_bind_part[b]</c> (<see cref="ModelCast.Rebind"/>), snapped to the
/// original records within <see cref="ModelSplitOptions.Tol"/>, and written into the template's LOD-0 node. Refused per
/// submesh (named in the report, the rest still written): weights on a bone the source mesh does not have, a skinned
/// object without weights. Bone edits are ignored: the per-mesh skeleton is always the native one.
/// <para/>
/// Not ported: <c>rpx=</c> (installing into an extracted tree; the project builder places the folders) and
/// <c>pack_paths=</c> (the <c>source</c> delegate resolves packs). Divergences (docs/porting.md): an all-zero weight row
/// (singular skin blend) or a face index past the vertex count refuses that submesh; the prototype aborts the split with
/// a numpy error.
/// </remarks>
public static partial class ModelSplit
{
    public const string FormatV1 = "nightrunner.model_cast/1";
    public const string ReportFile = "split_report.json";
    internal const double MoveRadius = 0.5;   // a moved vertex is matched to a same-UV original at most this far away

    /// <summary>Part type → file name in a mesh folder (<c>PART_FILES</c>).</summary>
    public static readonly IReadOnlyList<(byte Type, string File)> PartFiles =
    [
        (0x10, "image.bin"), (0x11, "fixups.bin"), (0x12, "skin.bin"), (0xF0, "vertex.bin"), (0xF1, "index.bin"), (0xF3, "cloth.bin"),
    ];

    [GeneratedRegex(@"\.\d{3}$")]
    private static partial Regex SuffixRegex();

    [GeneratedRegex("[<>:\"/\\\\|?*\\x00-\\x1f]")]
    private static partial Regex UnsafeRegex();

    internal static string StripSuffix(string name) => SuffixRegex().Replace(name, "", 1);

    /// <summary>The rest-pose rule of a report format (<c>beastpack.</c> spellings accepted); null when not a model scene report.</summary>
    public static ModelSplitRule? RuleOf(string? format)
    {
        string f = format is not null && format.StartsWith("beastpack.", StringComparison.Ordinal) ? "nightrunner." + format[10..] : format ?? "";
        return f switch
        {
            ModelCast.Format => ModelSplitRule.PartRigs,
            FormatV1 => ModelSplitRule.FirstSupplier,
            _ => null,
        };
    }

    /// <summary>
    /// Split <paramref name="editedScenePath"/> (.cast / .glb / .gltf) with its export <paramref name="report"/> into
    /// <paramref name="outDir"/>. <paramref name="source"/> gives a part's raw parts, decoded mesh and logical name by the
    /// report's (pack label, logical index).
    /// </summary>
    public static ModelSplitResult Split(string editedScenePath, JsonObject report, string outDir,
                                         Func<string, int, (IReadOnlyDictionary<byte, byte[]> Parts, MeshModel Mesh, string LogicalName)> source,
                                         ModelSplitOptions? options = null)
    {
        options ??= new ModelSplitOptions();
        string? format = report["format"] is JsonValue fv && fv.TryGetValue<string>(out var fs) ? fs : null;
        var rule = RuleOf(format) ?? throw new ModelSplitException(
            $"the report is not a nightrunner single-Cast export report (format {(report["format"] is null ? "None" : PyRepr(report["format"]!.ToJsonString().Trim('"')))}); " +
            "re-export the model with the current explorer");
        Directory.CreateDirectory(outDir);
        var result = new ModelSplitResult { Edited = editedScenePath, Model = Str(report["model"]), Format = format!, Rule = rule };

        var cache = new Dictionary<(string, int), (IReadOnlyDictionary<byte, byte[]> Parts, MeshModel Mesh, string LogicalName)>();
        (IReadOnlyDictionary<byte, byte[]> Parts, MeshModel Mesh, string LogicalName) Source(string pack, int index)
        {
            if (!cache.TryGetValue((pack, index), out var s)) cache[(pack, index)] = s = source(pack, index);
            return s;
        }

        // ---- merged skeleton, rebuilt exactly as exported -----------------------------------------------------------
        var partReps = (report["parts"] as JsonArray ?? throw new ModelSplitException("the report has no parts")).Select(p => (JsonObject)p!).ToList();
        var models = partReps.Select(pr => Source(Str(pr["pack"]) ?? "", Int(pr["index"])).Mesh).ToList();
        var labeled = partReps.Select((pr, i) => (Str(pr["mesh"]) ?? "", models[i])).ToList();
        var sk = report["skeleton"] as JsonObject ?? [];
        bool fromPart = sk["from_part"] is JsonValue fp && fp.TryGetValue<bool>(out var b) && b;
        MeshModel? skeleton = null;
        string skeletonLabel;
        if (fromPart)
        {
            int first = rule == ModelSplitRule.FirstSupplier
                ? partReps.FindIndex(pr => Str(pr["pack"]) == Str(sk["pack"]) && Int(pr["index"]) == Int(sk["index"]))
                : Math.Max(models.FindIndex(m => m.Skinned), 0);
            if (first < 0 || first >= models.Count || (rule == ModelSplitRule.PartRigs && labeled[first].Item1 != Str(sk["name"])))
                throw new ModelSplitException("the report's skeleton part is not among its parts");
            skeleton = models[first];
            skeletonLabel = labeled[first].Item1;
        }
        else if (Str(sk["pack"]) is { } skPack)
        {
            string name = Str(sk["name"]) ?? "";
            skeleton = sk["index"] is not null ? Source(skPack, Int(sk["index"])).Mesh
                : options.SkeletonByName?.Invoke(skPack, name)
                  ?? throw new ModelSplitException($"skeleton {name}: the report gives no logical index and it could not be found by name");
            skeletonLabel = rule == ModelSplitRule.FirstSupplier ? name : Str(sk["name"]) ?? "skeleton";
        }
        else skeletonLabel = "skeleton";
        var skel = ModelSkeleton.Merge(skeleton, skeletonLabel, labeled, partRigs: rule == ModelSplitRule.PartRigs);
        var emaps = models.Select(skel.Map).ToList();
        var bones = report["bones"] as JsonArray;
        if (bones is null || bones.Count != skel.Names.Count || skel.Names.Where((n, i) => CastExport.CastText(n) != Str(bones[i])).Any())
            throw new ModelSplitException("the merged skeleton no longer matches the export (packs changed since the export?)");

        // ---- the edited scene ------------------------------------------------------------------------------------------
        var (ebones, emeshes) = EditedMeshes(Gltf.Load(editedScenePath));
        var assign = options.Assign ?? new Dictionary<string, string>();
        var hideSet = new HashSet<string>(options.Hide ?? [], StringComparer.Ordinal);
        var two = new HashSet<string>(options.DoubleSided ?? [], StringComparer.Ordinal);
        for (int i = 0; i < emeshes.Count; i++)
            if (two.Contains(emeshes[i].Name) || two.Contains(StripSuffix(emeshes[i].Name))) emeshes[i] = DoubleSidedEm(emeshes[i]);
        var byName = new Dictionary<string, Em>(StringComparer.Ordinal);
        var grouped = new OrderedDictionary<string, List<Em>>(StringComparer.Ordinal);
        foreach (var m in emeshes)
        {
            string key = assign.ContainsKey(m.Name) ? m.Name : StripSuffix(m.Name);
            if (assign.TryGetValue(key, out var target))
            {
                m.Used = true;
                if (target.Length > 0)
                {
                    if (!grouped.TryGetValue(target, out var list)) grouped[target] = list = [];
                    list.Add(m);
                }
                continue;
            }
            byName.TryAdd(m.Name, m);
        }
        foreach (var m in emeshes)
        {
            if (m.Used) continue;
            byName.TryAdd(StripSuffix(m.Name), m);
            if (hideSet.Contains(StripSuffix(m.Name))) m.Used = true;       // the original object of a hidden submesh
        }
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (target, ems) in grouped)
        {
            if (hideSet.Contains(target)) throw new ModelSplitException($"{target}: both assigned and hidden");
            var merged = MergeEms(target, ems);
            merged.Material = null;      // a replacement keeps the target's game material (textures via rttiValues)
            byName[target] = merged;
            replaced.Add(target);
        }
        var meshMap = (report["mesh_map"] as JsonArray ?? []).Select(x => (JsonObject)x!).ToList();
        var known = meshMap.Select(x => Str(x["name"]) ?? "").ToHashSet(StringComparer.Ordinal);
        var bad = grouped.Keys.Concat(hideSet).Distinct().Where(x => !known.Contains(x)).Order(StringComparer.Ordinal).ToList();
        if (bad.Count > 0) throw new ModelSplitException("unknown target submesh: " + string.Join(", ", bad.Take(5)));

        // ---- per source mesh -------------------------------------------------------------------------------------------
        for (int pi = 0; pi < models.Count; pi++)
        {
            var model = models[pi];
            var pr = partReps[pi];
            string packLabel = Str(pr["pack"]) ?? "";
            var src = Source(packLabel, Int(pr["index"]));
            var entry = new ModelSplitMesh { Mesh = Str(pr["mesh"]) ?? "", LogicalName = model.Name };
            result.Meshes.Add(entry);
            var ents = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in model.Entities) ents[MeshModel.Text(e.Name).ToLowerInvariant()] = e.Index;
            var (rebind, _) = ModelCast.Rebind(model, skel, emaps[pi]);
            var (template, crep) = CastExport.Build(model, model.Name);
            var tmodel = template.Roots()[0].ChildOfType<CastModel>()!;
            var tmesh = new Dictionary<string, CastMesh>(StringComparer.Ordinal);
            foreach (var x in tmodel.Meshes()) tmesh[x.Name() ?? ""] = x;
            bool okAny = false;
            var jobs = new List<(ModelSplitSubmesh Sub, Em Em, Target Mm)>();
            foreach (var mmj in meshMap.Where(x => Int(x["part"]) == pi))
            {
                var mm = new Target(Str(mmj["name"]) ?? "", Int(mmj["entry"]), Int(mmj["submesh"]), Str(mmj["material"]));
                var sub = new ModelSplitSubmesh();
                sub.Fields["name"] = mm.Name;
                sub.Fields["entry"] = mm.Entry;
                sub.Fields["submesh"] = mm.Submesh;
                entry.Submeshes.Add(sub);
                Em? em;
                bool whole;
                if (hideSet.Contains(mm.Name))
                {
                    (em, whole) = (HiddenEm(mm.Name, ebones, ents), true);
                    sub.Fields["hidden"] = true;
                }
                else (em, whole) = (byName.GetValueOrDefault(mm.Name), replaced.Contains(mm.Name));
                if (em is null)
                {
                    sub.Fields["status"] = "not in the edited Cast (kept unchanged)";
                    continue;
                }
                em.Used = true;
                if (whole) sub.Fields["replaced"] = !sub.Flag("hidden");
                jobs.Add((sub, em, mm));
                if (whole && options.Lods)
                    foreach (var (e2, s2) in LodTwins(model, mm.Entry, mm.Submesh))
                    {
                        string name2 = $"{mm.Name}>lod e{e2}.s{s2}";
                        var sub2 = new ModelSplitSubmesh();
                        sub2.Fields["name"] = name2;
                        sub2.Fields["entry"] = e2;
                        sub2.Fields["submesh"] = s2;
                        sub2.Fields["lod_of"] = mm.Name;
                        sub2.Fields["hidden"] = sub.Flag("hidden");
                        entry.Submeshes.Add(sub2);
                        jobs.Add((sub2, em.With(name2), mm with { Entry = e2, Submesh = s2 }));
                    }
            }
            foreach (var (sub, em, mm) in jobs)
            {
                if (!tmesh.TryGetValue($"{CastExport.CastText(model.Name)}.e{mm.Entry}.s{mm.Submesh}", out var node))
                {
                    sub.Fields["status"] = "no template mesh (skipped at export)";
                    continue;
                }
                try
                {
                    var stats = Apply(node, em, ebones, ents, rebind, model, mm, options.Tol, options.UvTol, tmodel);
                    foreach (var (k, v) in stats) sub.Fields[k] = v;
                    sub.Fields["status"] = "applied";
                    okAny = true;
                }
                catch (SplitRefusal exc)
                {
                    sub.Fields["status"] = $"REFUSED: {exc.Message}";
                    entry.Problems.Add($"{mm.Name}: {exc.Message}");
                }
            }
            string stem = entry.Mesh.EndsWith(".msh", StringComparison.Ordinal) ? entry.Mesh[..^4] : entry.Mesh;
            string d = Path.Combine(outDir, UnsafeRegex().Replace(stem, "_").Trim(' ', '.'));
            Directory.CreateDirectory(d);
            entry.Dir = d;
            string castPath = Path.Combine(d, "model.cast");
            template.Save(castPath);
            string packPath = options.PackPath?.Invoke(packLabel) ?? Str(pr["pack_path"]) ?? packLabel;
            var side = MeshSidecar.Build(model, cast: MeshSidecar.CastInfo("model.cast", crep),
                                         source: new MeshSidecarSource(packPath, Int(pr["index"]), src.LogicalName));
            string sidePath = Path.Combine(d, "mesh.json");
            MeshSidecar.Write(side, sidePath);
            foreach (var (t, file) in PartFiles)
                if (src.Parts.TryGetValue(t, out var data)) System.IO.File.WriteAllBytes(Path.Combine(d, file), data);
            if (options.Check is { } check && okAny)
            {
                try
                {
                    var sideJson = JsonNode.Parse(MeshSidecar.ToJson(side)) as JsonObject;
                    var rb = check(src.Parts, src.LogicalName, castPath, sideJson);
                    bool Same(byte t) => rb.Parts.GetValueOrDefault(t) is var x && src.Parts.GetValueOrDefault(t) is var y
                                         && (x is null ? y is null : y is not null && x.AsSpan().SequenceEqual(y));
                    entry.Check = new ModelSplitCheck(true, rb.Entries, rb.Warnings.Take(20).ToList(),
                                                      Same(0x10) && Same(0xF0) && Same(0xF1), null);
                }
                catch (Exception exc) when (exc is not OperationCanceledException)
                {
                    entry.Check = new ModelSplitCheck(false, [], [], false, $"{exc.GetType().Name}: {exc.Message}");
                    entry.Problems.Add($"encoder check failed: {exc.Message}");
                }
            }
        }
        result.UnmatchedMeshes.AddRange(emeshes.Where(m => !m.Used).Select(m => m.Name));
        if (result.UnmatchedMeshes.Count > 0)
            result.Problems.Add("objects with no target (assign or skip them): " + string.Join(", ", result.UnmatchedMeshes.Take(10)));
        result.ReportPath = Path.Combine(outDir, ReportFile);
        MeshSidecar.Write(result.ToJson(), result.ReportPath);
        return result;
    }

    /// <summary>
    /// Per-mesh edit (e.g. a glTF round trip through Blender) → the mesh's own template Cast with the edited geometry
    /// applied as the model splitter does, with an identity rebind (the file is in the mesh's bind space). Template meshes
    /// the edit lacks are removed (the encoder then judges submesh / LOD removal); edited meshes the template does not
    /// know are appended as they are (new submeshes). Port of <c>normalize_mesh_scene</c>; fits
    /// <see cref="MeshBuildOptions.GltfNormalizer"/>. Throws <see cref="ModelSplitException"/> to refuse.
    /// </summary>
    public static (CastFile Cast, IReadOnlyList<string> Notes) NormalizeMeshScene(CastFile edited, MeshModel model,
                                                                                 double tol = 1e-4, double uvTol = 1e-4)
    {
        var (template, _) = CastExport.Build(model, model.Name);
        var tm = template.Roots()[0].ChildOfType<CastModel>()!;
        var (ebones, emeshes) = EditedMeshes(edited);
        var ents = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in model.Entities) ents[MeshModel.Text(e.Name).ToLowerInvariant()] = e.Index;
        double[] identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        var rebind = Enumerable.Range(0, Math.Max(model.Entities.Length, 1)).Select(_ => identity).ToArray();
        var byName = new OrderedDictionary<string, Em>(StringComparer.Ordinal);
        foreach (var m in emeshes) byName.TryAdd(StripSuffix(m.Name), m);
        var notes = new List<string>();
        foreach (var node in tm.Meshes())
        {
            string name = node.Name() ?? "";
            if (!byName.Remove(name, out var em))
            {
                tm.RemoveChild(node);
                notes.Add($"{name}: not in the edit (removed)");
                continue;
            }
            var mm = new Target(name, (int)(node.Property("bp_entry")?.IntegerAt(0) ?? 0), 0, null);
            try
            {
                var st = Apply(node, em, ebones, ents, rebind, model, mm, tol, uvTol, tm);
                int dups = st.GetValueOrDefault("duplicates") is int d ? d : 0;
                notes.Add($"{name}: snapped {st["snapped"]}, moved {st["moved"]}, new {st["new"]}{(dups != 0 ? $", duplicates {dups}" : "")}");
            }
            catch (SplitRefusal exc)
            {
                throw new ModelSplitException($"{name}: {exc.Message}");
            }
        }
        foreach (var (name, em) in byName)
        {
            notes.Add($"{name}: not in the original mesh — kept as a new submesh");
            AppendMesh(tm, em, ebones, ents);
        }
        return (template, notes);
    }

    /// <summary><c>_append_mesh</c>: an edited object the template does not know, as a new mesh node.</summary>
    private static void AppendMesh(CastModel tm, Em em, List<string> ebones, Dictionary<string, int> ents)
    {
        var node = tm.CreateMesh();
        node.SetName(em.Name);
        node.SetVertexPositionBuffer(F32(em.Pos));
        if (em.Nrm is not null) node.SetVertexNormalBuffer(F32(em.Nrm));
        var layers = new[] { em.Uv0, em.Uv1 }.Where(x => x is not null).ToList();
        for (int i = 0; i < layers.Count; i++) node.SetVertexUVLayerBuffer(i, F32(layers[i]!));
        node.SetUVLayerCount(layers.Count);
        if (em.Faces.Length == 0 || em.Faces.Any(f => f < 0 || f > uint.MaxValue))
            throw new ModelSplitException($"{em.Name}: no faces, or a face index out of range");
        node.SetFaceBuffer(U32(em.Faces));
        if (em.Weights is not null && em.Bones is not null)
        {
            var (wb, wv, _) = Top4(em.Bones, em.Weights, em.K, em.N);
            var mapped = new uint[wb.Length];
            for (int i = 0; i < wb.Length; i++)
            {
                long b = wb[i];
                int m = b >= 0 && b < ebones.Count && ents.TryGetValue(ebones[(int)b].ToLowerInvariant(), out int e) ? e : -1;
                if (m < 0 && wv[i] > 0) throw new ModelSplitException($"{em.Name}: weights on bones the mesh does not have");
                mapped[i] = wv[i] > 0 ? (uint)m : 0;
            }
            node.SetMaximumWeightInfluence(CastExport.MaxInfluences);
            node.SetVertexWeightBoneBuffer(mapped);
            node.SetVertexWeightValueBuffer(F32(wv));
        }
        if (!string.IsNullOrEmpty(em.Material)) AssignMaterial(node, em.Material, tm, new OrderedDictionary<string, object?>());
    }

    // ---- report helpers ------------------------------------------------------------------------------------------------

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int Int(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<int>(out var i)
            ? i : throw new ModelSplitException($"the report has a non-integer where an index belongs ({n?.ToJsonString() ?? "null"})");

    /// <summary>Python's <c>repr</c> of a string (quotes and backslashes only).</summary>
    internal static string PyRepr(string s)
    {
        char q = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var t = s.Replace("\\", "\\\\");
        if (q == '\'') t = t.Replace("'", "\\'");
        return q + t + q;
    }

    // ---- the edited scene ----------------------------------------------------------------------------------------------

    /// <summary>A submesh of the export report (<c>mesh_map</c> row) the edit lands on.</summary>
    internal sealed record Target(string Name, int Entry, int Submesh, string? Material);

    /// <summary>One edited object (the prototype's <c>em</c> dict): flat double buffers, faces, weights (<see cref="K"/> lanes).</summary>
    internal sealed class Em
    {
        public string Name = "";
        public double[] Pos = [];
        public double[]? Nrm, Uv0, Uv1;
        public long[] Faces = [];
        public long[]? Bones;
        public double[]? Weights;
        public int K;
        public string? Material;
        public bool Used;
        public int? LooseDropped;
        public int N => Pos.Length / 3;

        public Em With(string name)
        {
            var c = (Em)MemberwiseClone();
            c.Name = name;
            return c;
        }
    }

    /// <summary><c>_edited_meshes</c>: every Model under the first root; bone indices offset per model.</summary>
    internal static (List<string> Bones, List<Em> Meshes) EditedMeshes(CastFile cast)
    {
        var roots = cast.Roots();
        if (roots.Count == 0) throw new ModelSplitException("the Cast has no root node");
        var models = roots[0].ChildrenOfType<CastModel>();
        if (models.Count == 0) throw new ModelSplitException("the Cast has no Model node");
        var bones = new List<string>();
        var meshes = new List<Em>();
        foreach (var mdl in models)
        {
            int baseIndex = bones.Count;
            if (mdl.Skeleton() is { } sk) bones.AddRange(sk.Bones().Select(x => x.Name() ?? ""));
            var mats = new Dictionary<ulong, string?>();
            foreach (var x in mdl.Materials()) mats[x.Hash] = x.Name();
            foreach (var m in mdl.Meshes())
            {
                var pos = Doubles(m, "vp") ?? [];
                int n = pos.Length / 3;
                int layers = m.UVLayerCount();
                int mi = m.MaximumWeightInfluence();
                var wb = m.Property("wb") is { ValueCount: > 0 } pb ? pb : null;
                var wv = m.Property("wv") is { ValueCount: > 0 } pv ? pv : null;
                var em = new Em
                {
                    Name = m.Name() ?? "",
                    Pos = pos,
                    Nrm = Doubles(m, "vn"),
                    Uv0 = layers > 0 ? Doubles(m, "u0") : null,
                    Uv1 = layers > 1 ? Doubles(m, "u1") : null,
                    Faces = Longs(m.Property("f")) ?? [],
                    K = mi,
                    Material = m.Property("m") is { ValueCount: > 0 } pm && pm.Type <= CastPropertyType.Long ? mats.GetValueOrDefault(pm.IntegerAt(0)) : null,
                };
                if (mi > 0 && wb is not null)
                {
                    var bl = Longs(wb)!;
                    if (bl.Length != n * mi) throw new ModelSplitException($"{em.Name}: {bl.Length} weight bones for {n} vertices × {mi}");
                    for (int i = 0; i < bl.Length; i++) bl[i] += baseIndex;
                    em.Bones = bl;
                }
                if (mi > 0 && wv is not null)
                {
                    em.Weights = wv.ToDoubleArray();
                    if (em.Weights.Length != n * mi) throw new ModelSplitException($"{em.Name}: {em.Weights.Length} weights for {n} vertices × {mi}");
                }
                if (em.Nrm is not null && em.Nrm.Length != pos.Length) throw new ModelSplitException($"{em.Name}: {em.Nrm.Length / 3} normals for {n} vertices");
                meshes.Add(em);
            }
        }
        return (bones, meshes);
    }

    private static double[]? Doubles(CastNode node, string key) => node.Property(key)?.ToDoubleArray();

    private static long[]? Longs(CastProperty? p)
    {
        if (p is null) return null;
        var r = new long[p.ValueCount];
        for (int i = 0; i < r.Length; i++) r[i] = p.Type <= CastPropertyType.Long ? (long)p.IntegerAt(i) : (long)p.NumberAt(i);
        return r;
    }

    /// <summary><c>_merge_ems</c>: several edited objects → one submesh (concatenated; weight lanes padded to the widest).</summary>
    internal static Em MergeEms(string name, List<Em> ems)
    {
        if (ems.Count == 1) return ems[0].With(name);
        var o = new Em { Name = name, Material = ems[0].Material };
        o.Pos = ems.SelectMany(e => e.Pos).ToArray();   // positions are never null here
        o.Nrm = ems.Any(e => e.Nrm is null) ? null : ems.SelectMany(e => e.Nrm!).ToArray();
        if (ems.Any(e => e.Uv0 is null)) throw new ModelSplitException($"{name}: an object has no uv0");
        o.Uv0 = ems.SelectMany(e => e.Uv0!).ToArray();
        o.Uv1 = ems.Any(e => e.Uv1 is null) ? null : ems.SelectMany(e => e.Uv1!).ToArray();
        var faces = new List<long>();
        long baseIndex = 0;
        foreach (var e in ems)
        {
            faces.AddRange(e.Faces.Select(f => f + baseIndex));
            baseIndex += e.N;
        }
        o.Faces = faces.ToArray();
        if (ems.All(e => e.Weights is null)) return o;
        if (ems.Any(e => e.Weights is null || e.Bones is null)) throw new ModelSplitException($"{name}: some objects are skinned, some are not");
        int k = ems.Max(e => e.K);
        var bones = new List<long>();
        var weights = new List<double>();
        foreach (var e in ems)
            for (int i = 0; i < e.N; i++)
                for (int j = 0; j < k; j++)
                {
                    bones.Add(j < e.K ? e.Bones![i * e.K + j] : 0);
                    weights.Add(j < e.K ? e.Weights![i * e.K + j] : 0);
                }
        (o.Bones, o.Weights, o.K) = (bones.ToArray(), weights.ToArray(), k);
        return o;
    }

    /// <summary><c>_double_sided</c>: every vertex again with the normal negated, every triangle again reversed.</summary>
    internal static Em DoubleSidedEm(Em em)
    {
        int n = em.N;
        var o = em.With(em.Name);
        static T[]? Twice<T>(T[]? a) => a is null ? null : [.. a, .. a];
        o.Pos = Twice(em.Pos)!;
        o.Nrm = em.Nrm is null ? null : [.. em.Nrm, .. em.Nrm.Select(x => -x)];
        o.Uv0 = Twice(em.Uv0);
        o.Uv1 = Twice(em.Uv1);
        o.Bones = Twice(em.Bones);
        o.Weights = Twice(em.Weights);
        var f = new long[em.Faces.Length / 3 * 6];
        int t = em.Faces.Length / 3;
        for (int i = 0; i < t; i++)
        {
            f[i * 3] = em.Faces[i * 3];
            f[i * 3 + 1] = em.Faces[i * 3 + 1];
            f[i * 3 + 2] = em.Faces[i * 3 + 2];
            f[(t + i) * 3] = em.Faces[i * 3 + 2] + n;
            f[(t + i) * 3 + 1] = em.Faces[i * 3 + 1] + n;
            f[(t + i) * 3 + 2] = em.Faces[i * 3] + n;
        }
        o.Faces = f;
        return o;
    }

    /// <summary><c>_hidden_em</c>: one vertex + one degenerate triangle, weighted to the first scene bone the mesh has.</summary>
    internal static Em HiddenEm(string name, List<string> ebones, Dictionary<string, int> ents)
    {
        int bone = ebones.FindIndex(x => ents.ContainsKey(x.ToLowerInvariant()));
        return new Em
        {
            Name = name, Pos = [0, 0, 0], Nrm = [0, 1, 0], Uv0 = [0, 0], Faces = [0, 0, 0],
            Bones = bone < 0 ? null : [bone], Weights = bone < 0 ? null : [1.0], K = 1,
        };
    }

    /// <summary><c>_lod_twins</c>: (entry, submesh) of the other LOD levels of the same entity drawing the same material slot.</summary>
    internal static List<(int Entry, int Submesh)> LodTwins(MeshModel model, int entry, int submesh)
    {
        var ges = model.GeometryEntries;
        var outList = new List<(int, int)>();
        if (entry < 0 || entry >= ges.Length || submesh < 0 || submesh >= ges[entry].Submeshes.Length) return outList;
        var ge = ges[entry];
        int slot = ge.Submeshes[submesh].MaterialSlot;
        foreach (var g in ges)
        {
            if (g.Index == entry || g.OwnerEntity != ge.OwnerEntity || g.Element == ge.Element) continue;
            foreach (var sm in g.Submeshes)
                if (sm.MaterialSlot == slot)
                {
                    outList.Add((g.Index, sm.Index));
                    break;
                }
        }
        return outList;
    }
}
