using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nightrunner.Core.Mesh;

namespace Nightrunner.Core.Cast;

/// <summary>A skeleton bone as read from a scene.</summary>
public sealed record CastImportBone(int Index, string Name, int Parent, double[]? LocalPos, double[]? LocalRot, double[]? Scale, int? Entity);

/// <summary>A scene mesh as read (flat arrays; <see cref="Faces"/> 3 per triangle, weights/bones <see cref="Influences"/> per vertex).</summary>
public sealed class CastImportMesh
{
    public required string Name { get; init; }
    public int NodeIndex { get; init; }
    public required float[] Positions { get; init; }
    public float[]? Normals { get; init; }
    /// <summary>Null: not in the scene (derived from the UVs on resolve).</summary>
    public float[]? Tangents { get; init; }
    public sbyte[]? TangentSign { get; init; }
    public float[]? Uv0 { get; init; }
    public float[]? Uv1 { get; init; }
    public required long[] Faces { get; init; }
    public float[]? Weights { get; init; }
    public long[]? Bones { get; init; }
    public int Influences { get; init; }
    public ulong? MaterialHash { get; init; }
    /// <summary>The <c>bp_*</c> custom properties.</summary>
    public required Dictionary<string, CastProperty> Props { get; init; }

    public int VertexCount => Positions.Length / 3;
}

/// <summary>A parsed scene: bones, materials (node hash → name), meshes.</summary>
public sealed class CastImportScene
{
    public required string Path { get; init; }
    public string? Software { get; init; }
    public string? UpAxis { get; init; }
    public string? ModelName { get; init; }
    public required List<CastImportBone> Bones { get; init; }
    public required Dictionary<ulong, string> Materials { get; init; }
    public required Dictionary<ulong, int> MaterialSlots { get; init; }
    public required List<CastImportMesh> Meshes { get; init; }
    public List<string> Warnings { get; } = [];
}

/// <summary>One scene mesh matched to a (geometry entry, submesh), with normalised attributes.</summary>
public sealed class ResolvedCastMesh
{
    public required CastImportMesh Src { get; init; }
    public int Entry { get; init; }
    public int Submesh { get; init; }
    /// <summary><c>bp</c> | <c>name</c>.</summary>
    public required string MatchedBy { get; init; }
    public int Format { get; init; }
    public required string MaterialName { get; init; }
    /// <summary>Null: a new material (the writer adds it to the table or refuses).</summary>
    public int? MaterialSlot { get; init; }
    /// <summary><c>bp</c> | <c>exact</c> | <c>fold</c> | <c>new</c>.</summary>
    public required string MaterialMatchedBy { get; init; }
    public required float[] Positions { get; init; }
    public required float[] Normals { get; init; }
    public required float[] Tangents { get; init; }
    public required sbyte[] TangentSign { get; init; }
    public required float[] Uv0 { get; init; }
    public float[]? Uv1 { get; init; }
    public required long[] Faces { get; init; }
    /// <summary>4 lanes per vertex (the scene's lane order kept when it has at most 4, else active lanes first).</summary>
    public float[]? Weights { get; init; }
    /// <summary>4 entity indices per vertex (0 on inactive lanes).</summary>
    public long[]? Bones { get; init; }
    /// <summary>Original window index of each vertex (<c>bp_vertex_id</c>), or null.</summary>
    public long[]? VertexIds { get; init; }
    public List<string> Derived { get; } = [];

    public int VertexCount => Positions.Length / 3;
    public string Name => Src.Name;
}

/// <summary>The scene resolved against the sidecar.</summary>
public sealed class ResolvedCastImport
{
    public required CastImportScene Scene { get; init; }
    public required JsonObject Sidecar { get; init; }
    public required List<ResolvedCastMesh> Meshes { get; init; }
    /// <summary>Scene bone index → entity index; null without a skeleton.</summary>
    public long[]? EntityMap { get; init; }
    /// <summary><c>identity</c> | <c>name</c> | <c>bp_entity</c> | <c>none</c>.</summary>
    public required string BonesMatchedBy { get; init; }
    public required List<string> BoneChanges { get; init; }
    public required List<string> NewMaterials { get; init; }
    public required List<string> Warnings { get; init; }

    public Dictionary<int, List<ResolvedCastMesh>> ByEntry()
    {
        var d = new Dictionary<int, List<ResolvedCastMesh>>();
        foreach (var m in Meshes)
        {
            if (!d.TryGetValue(m.Entry, out var l)) d[m.Entry] = l = [];
            l.Add(m);
        }
        foreach (var l in d.Values) l.Sort((a, b) => a.Submesh.CompareTo(b.Submesh));
        return d;
    }
}

/// <summary>
/// Scene (+ <c>mesh.json</c> sidecar) → resolved edit description for the mesh writer. Port of <c>cast/import_.py</c>.
/// </summary>
/// <remarks>
/// Accepted: a scene written by the exporter (<c>bp_*</c> properties, meshes named <c>&lt;mesh&gt;.e&lt;entry&gt;.s&lt;sub&gt;</c>), and
/// the same scene after the official Blender plugin's round trip (custom properties dropped, no tangent buffer, vertices
/// split on UV seams, names possibly suffixed <c>.001</c>). Coordinates are always native engine space: a Metadata up axis
/// other than <c>y</c> only warns.
/// <para/>
/// Matching per mesh: <c>bp_entry</c>/<c>bp_submesh</c> → the name suffix → refusal naming the mesh. Bones: identical name
/// list → identity; a unique-name permutation or <c>bp_entity</c> → mapped; anything else is refused (adding or removing
/// bones is not supported). Materials: <c>bp_material_slot</c> naming the same material → the slot; exact name; case-
/// insensitive name (warning); else a NEW material name.
/// </remarks>
public static partial class CastImport
{
    public const int MaxInfluences = 4;
    public const double BonePosTol = 1e-4, BoneRotTol = 1e-3;
    public const string SidecarSchema = "nightrunner.mesh/1";

    [GeneratedRegex(@"\.e(\d+)\.s(\d+)(?:\.\d{3})?$")]
    public static partial Regex MeshNameRe();

    // ---- read -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Parses a .cast / .gltf / .glb (or an in-memory <paramref name="cast"/>, <paramref name="path"/> then only labels it)
    /// into plain arrays. Throws <see cref="MeshFormatException"/> for a malformed scene.
    /// </summary>
    public static CastImportScene Read(string path, CastFile? cast = null)
    {
        if (cast is null)
        {
            try { cast = Gltf.Load(path); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                throw new MeshFormatException($"{path}: cannot read scene: {e.Message}");
            }
        }
        var roots = cast.Roots();
        if (roots.Count == 0) throw new MeshFormatException($"{path}: no root node");
        var warnings = new List<string>();
        var root = roots[0];
        if (roots.Count > 1) warnings.Add($"{roots.Count} root nodes; only the first is read");
        var meta = root.ChildOfType<Metadata>();
        string? software = meta?.Software(), up = meta?.UpAxis();
        if (up is not null && up.ToLowerInvariant() != "y")
            warnings.Add($"Metadata up axis is {Repr(up)}: coordinates are still read as native engine space (the official "
                         + "Blender plugin applies no axis conversion on import or export)");
        var models = root.ChildrenOfType<Model>();
        if (models.Count == 0) throw new MeshFormatException($"{path}: no Model node");
        if (models.Count > 1) warnings.Add($"{models.Count} Model nodes; only the first is read");
        var mdl = models[0];

        var bones = new List<CastImportBone>();
        if (mdl.Skeleton() is { } skel)
        {
            var list = skel.Bones();
            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i];
                var ent = b.Property("bp_entity");
                bones.Add(new CastImportBone(i, b.Name() ?? "", b.ParentIndex(), D(b.LocalPosition()), D(b.LocalRotation()), D(b.Scale()),
                                           ent is { ValueCount: > 0 } ? (int)ent.NumberAt(0) : null));
            }
        }

        var materials = new Dictionary<ulong, string>();
        var slots = new Dictionary<ulong, int>();
        foreach (var m in mdl.Materials())
        {
            materials[m.Hash] = m.Name() ?? "";
            if (m.Property("bp_material_slot") is { ValueCount: > 0 } s) slots[m.Hash] = (int)s.NumberAt(0);
        }

        var meshes = new List<CastImportMesh>();
        var nodes = mdl.Meshes();
        for (int k = 0; k < nodes.Count; k++)
        {
            var m = nodes[k];
            string name = m.Name() ?? $"mesh_{k}";
            var pos = Floats(m, "vp", 3, path, name) ?? throw new MeshFormatException($"{path}: mesh {Repr(name)} has no vertex position buffer");
            int n = pos.Length / 3;
            long[] faces = [];
            if (m.Property("f") is { } fp)
            {
                if (fp.ValueCount % 3 != 0) throw new MeshFormatException($"buffer of {fp.ValueCount} values is not a multiple of 3");
                faces = new long[fp.ValueCount];
                for (int i = 0; i < faces.Length; i++) faces[i] = (long)fp.IntegerAt(i);
            }
            if (faces.Length > 0 && faces.Max() is var fmax && fmax >= n)
                throw new MeshFormatException($"{path}: mesh {Repr(name)}: face index {fmax} >= {n} vertices");
            int mi = m.MaximumWeightInfluence();
            float[]? wv = null;
            long[]? wb = null;
            if (mi > 0)
            {
                var wbp = m.Property("wb");
                wv = Floats(m, "wv", mi, path, name);
                if (wbp is not null)
                {
                    if (wbp.ValueCount % mi != 0) throw new MeshFormatException($"buffer of {wbp.ValueCount} values is not a multiple of {mi}");
                    wb = new long[wbp.ValueCount];
                    for (int i = 0; i < wb.Length; i++) wb[i] = (long)wbp.IntegerAt(i);
                }
                if (wb is null || wv is null || wb.Length / mi != n || wv.Length / mi != n)
                    throw new MeshFormatException($"{path}: mesh {Repr(name)}: weight buffers do not match {n} vertices × {mi} influences");
            }
            var props = new Dictionary<string, CastProperty>(StringComparer.Ordinal);
            foreach (var (pn, p) in m.Properties)
                if (pn.StartsWith("bp_", StringComparison.Ordinal)) props[pn] = p;
            sbyte[]? sign = null;
            if (props.TryGetValue("bp_tangent_sign", out var sp))
            {
                if (sp.ValueCount != n) warnings.Add($"mesh {Repr(name)}: bp_tangent_sign has {sp.ValueCount} values for {n} vertices; ignored");
                else
                {
                    sign = new sbyte[n];
                    for (int i = 0; i < n; i++) sign[i] = (sbyte)(sp.NumberAt(i) < 0 ? -1 : 1);
                }
            }
            var mesh = new CastImportMesh
            {
                Name = name, NodeIndex = k, Positions = pos,
                Normals = Floats(m, "vn", 3, path, name), Tangents = Floats(m, "vt", 3, path, name), TangentSign = sign,
                Uv0 = Floats(m, "u0", 2, path, name), Uv1 = Floats(m, "u1", 2, path, name),
                Faces = faces, Weights = wv, Bones = wb, Influences = mi,
                MaterialHash = m.Property("m") is { ValueCount: > 0 } mh ? mh.IntegerAt(0) : null, Props = props,
            };
            foreach (var (label, a, w) in new (string, float[]?, int)[] { ("normals", mesh.Normals, 3), ("tangents", mesh.Tangents, 3), ("uv0", mesh.Uv0, 2), ("uv1", mesh.Uv1, 2) })
                if (a is not null && a.Length / w != n)
                    throw new MeshFormatException($"{path}: mesh {Repr(name)}: {label} has {a.Length / w} rows for {n} vertices");
            meshes.Add(mesh);
        }
        var scene = new CastImportScene
        {
            Path = path, Software = software, UpAxis = up, ModelName = mdl.Name(), Bones = bones, Materials = materials,
            MaterialSlots = slots, Meshes = meshes,
        };
        scene.Warnings.AddRange(warnings);
        return scene;
    }

    private static double[]? D(float[]? a) => a is null ? null : Array.ConvertAll(a, x => (double)x);

    private static float[]? Floats(CastNode node, string key, int cols, string path, string mesh)
    {
        if (node.Property(key) is not { } p) return null;
        var a = p.ToSingleArray();
        if (cols > 1 && a.Length % cols != 0) throw new MeshFormatException($"{path}: mesh {Repr(mesh)}: buffer of {a.Length} values is not a multiple of {cols}");
        return a;
    }

    // ---- resolve ----------------------------------------------------------------------------------------------------

    /// <summary>The sidecar's name for an entity/material record, as the scene carries it (UTF-8 with U+FFFD).</summary>
    private static string SideName(JsonNode? e)
    {
        var o = e!.AsObject();
        if (o["name_hex"]?.GetValue<string>() is { Length: > 0 } hx) return Encoding.UTF8.GetString(Convert.FromHexString(hx));
        return o["name"]?.GetValue<string>() ?? "";
    }

    private static JsonArray Arr(JsonObject o, string key) => o[key] as JsonArray ?? [];

    /// <summary>A JSON number, whatever CLR type backs the node (parsed documents and built ones alike).</summary>
    internal static double Num(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<uint>(out var u)) return u;
            if (v.TryGetValue<float>(out var f)) return f;
            if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        }
        throw new MeshFormatException($"sidecar: {n?.ToJsonString() ?? "null"} is not a number");
    }

    /// <summary>True for <c>nightrunner.mesh/1</c> and its pre-rename <c>beastpack.</c> spelling.</summary>
    public static bool SchemaMatches(JsonNode? value)
    {
        string s = value is JsonValue v && v.TryGetValue<string>(out var str) ? str : "";
        if (s.StartsWith("beastpack.", StringComparison.Ordinal)) s = "nightrunner." + s["beastpack.".Length..];
        return s == SidecarSchema;
    }

    private static (long[]? Map, string How, List<string> Changes) ResolveBones(CastImportScene scene, JsonObject sidecar, List<string> warnings)
    {
        var ents = Arr(sidecar, "entities");
        var names = ents.Select(SideName).ToList();
        if (scene.Bones.Count == 0) return (null, "none", []);
        var castNames = scene.Bones.Select(b => b.Name).ToList();
        if (castNames.Count != names.Count)
            throw new MeshUnsupportedException($"Cast has {castNames.Count} bones, the sidecar has {names.Count} entities: adding or "
                                               + "removing bones is not supported (restore the skeleton from the exported Cast)");
        long[] emap;
        string how;
        if (castNames.SequenceEqual(names, StringComparer.Ordinal))
        {
            emap = Enumerable.Range(0, names.Count).Select(i => (long)i).ToArray();
            how = "identity";
        }
        else if (names.Distinct(StringComparer.Ordinal).Count() == names.Count
                 && castNames.Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            var idx = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < names.Count; i++) idx[names[i]] = i;
            emap = castNames.Select(n => (long)idx[n]).ToArray();
            how = "name";
            warnings.Add("Cast bones are re-ordered relative to the sidecar entities; matched by name");
        }
        else if (scene.Bones.All(b => b.Entity is not null)
                 && scene.Bones.Select(b => b.Entity!.Value).Order().SequenceEqual(Enumerable.Range(0, names.Count)))
        {
            emap = scene.Bones.Select(b => (long)b.Entity!.Value).ToArray();
            how = "bp_entity";
            var bad = scene.Bones.Where(b => b.Name != names[b.Entity!.Value]).ToList();
            if (bad.Count > 0)
                warnings.Add($"{bad.Count} bones renamed (matched by bp_entity), e.g. {Repr(bad[0].Name)} → entity {Repr(names[bad[0].Entity!.Value])}");
        }
        else
        {
            var missing = names.Except(castNames, StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(5);
            var extra = castNames.Except(names, StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(5);
            throw new MeshUnsupportedException($"Cast bone names do not match the sidecar entities (missing {ReprList(missing)}, unexpected "
                                               + $"{ReprList(extra)}); renaming/adding/removing bones is not supported");
        }

        var changes = new List<string>();
        foreach (var b in scene.Bones)
        {
            var e = ents[(int)emap[b.Index]]!.AsObject();
            int expParent = (int)Num(e["parent"]);
            long gotParent = b.Parent < 0 || b.Parent >= emap.Length ? -1 : emap[b.Parent];
            if (expParent != gotParent)
            {
                changes.Add($"{Repr(b.Name)}: parent entity {gotParent} != {expParent}");
                continue;
            }
            var l = Arr(e, "local_3x4").Select(x => Num(x)).ToArray();
            if (b.LocalPos is { } lp)
            {
                double dmax = 0;
                for (int i = 0; i < 3; i++) dmax = Max(dmax, Math.Abs(lp[i] - l[i * 4 + 3]));
                if (dmax > BonePosTol)
                {
                    changes.Add($"{Repr(b.Name)}: local position moved by {G3(dmax)}");
                    continue;
                }
            }
            if (b.LocalRot is { } q1)
            {
                var r3 = new double[3, 3];
                for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) r3[i, j] = l[i * 4 + j];
                var q0 = CastExport.QuaternionXyzw(CastExport.PolarRotation(r3));
                double dm = 0, dp = 0;
                for (int i = 0; i < 4; i++) { dm = Max(dm, Math.Abs(q1[i] - q0[i])); dp = Max(dp, Math.Abs(q1[i] + q0[i])); }
                double d = Math.Min(dm, dp);
                if (d > BoneRotTol || double.IsNaN(d))
                {
                    changes.Add($"{Repr(b.Name)}: local rotation differs by {G3(d)}");
                    continue;
                }
            }
            if (b.Scale is { } s)
            {
                double dmax = 0;
                foreach (var x in s) dmax = Max(dmax, Math.Abs(x - 1.0));
                if (dmax > 1e-3)
                    changes.Add($"{Repr(b.Name)}: scale [{string.Join(", ", s.Select(x => Math.Round(x, 4).ToString("0.0###", CultureInfo.InvariantCulture)))}] != 1");
            }
        }
        return (emap, how, changes);
    }

    /// <summary>np.max semantics: NaN propagates.</summary>
    private static double Max(double a, double b) => double.IsNaN(a) || double.IsNaN(b) ? double.NaN : Math.Max(a, b);

    private static (string Name, int? Slot, string How) ResolveMaterial(CastImportMesh mesh, CastImportScene scene, JsonObject sidecar, List<string> warnings)
    {
        var names = Arr(sidecar["materials"] as JsonObject ?? [], "entries").Select(SideName).ToList();
        int? slotProp = mesh.Props.TryGetValue("bp_material_slot", out var sp) && sp.ValueCount > 0 ? (int)sp.NumberAt(0) : null;
        string? name = mesh.MaterialHash is { } h ? scene.Materials.GetValueOrDefault(h) : null;
        if (name is null && mesh.MaterialHash is { } hh)
            throw new MeshFormatException($"mesh {Repr(mesh.Name)}: material hash 0x{hh:x} names no Material node");
        if (name is null)
        {
            if (slotProp is { } s0 && s0 >= 0 && s0 < names.Count) return (names[s0], s0, "bp");
            throw new MeshBuildException($"mesh {Repr(mesh.Name)} has no Material and no bp_material_slot: assign a material");
        }
        if (slotProp is { } s && s >= 0 && s < names.Count && names[s] == name) return (name, s, "bp");
        int exact = names.IndexOf(name);
        if (exact >= 0) return (name, exact, "exact");
        int fold = names.FindIndex(n => n.ToLowerInvariant() == name.ToLowerInvariant());
        if (fold >= 0)
        {
            warnings.Add($"mesh {Repr(mesh.Name)}: material {Repr(name)} matched slot {fold} ({Repr(names[fold])}) case-insensitively");
            return (names[fold], fold, "fold");
        }
        return (name, null, "new");
    }

    private static (int Entry, int Submesh, string How) MatchMesh(CastImportMesh mesh, JsonObject sidecar)
    {
        int count = Arr(sidecar, "geometry_entries").Count;
        if (mesh.Props.TryGetValue("bp_entry", out var be) && be.ValueCount > 0 && mesh.Props.TryGetValue("bp_submesh", out var bs) && bs.ValueCount > 0)
        {
            int e = (int)be.NumberAt(0), s = (int)bs.NumberAt(0);
            if (e < 0 || e >= count) throw new MeshBuildException($"mesh {Repr(mesh.Name)}: bp_entry {e} outside the sidecar's {count} geometry entries");
            return (e, s, "bp");
        }
        var m = MeshNameRe().Match(mesh.Name);
        if (!m.Success)
            throw new MeshBuildException($"mesh {Repr(mesh.Name)}: no bp_entry/bp_submesh properties and the name does not end in "
                                         + "'.e<entry>.s<submesh>' — cannot tell which geometry entry/submesh it replaces");
        int en = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), sn = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (en < 0 || en >= count)
            throw new MeshBuildException($"mesh {Repr(mesh.Name)}: entry {en} (from the name) outside the sidecar's {count} geometry entries");
        return (en, sn, "name");
    }

    /// <summary>Matches the scene against the sidecar and normalises every mesh's attributes; never modifies the scene.</summary>
    public static ResolvedCastImport Resolve(CastImportScene scene, JsonObject sidecar, bool allowNewSubmeshes = true)
    {
        if (!SchemaMatches(sidecar["schema"]))
            throw new MeshFormatException($"sidecar schema {Repr(sidecar["schema"]?.ToString())} is not {SidecarSchema}");
        var warnings = new List<string>(scene.Warnings);
        var (emap, bonesHow, boneChanges) = ResolveBones(scene, sidecar, warnings);
        var entries = Arr(sidecar, "geometry_entries");
        var resolved = new List<ResolvedCastMesh>();
        var seen = new Dictionary<(int, int), string>();
        var newMaterials = new List<string>();
        foreach (var mesh in scene.Meshes)
        {
            var (e, s, how) = MatchMesh(mesh, sidecar);
            if (seen.TryGetValue((e, s), out var prev))
                throw new MeshBuildException($"meshes {Repr(prev)} and {Repr(mesh.Name)} both map to entry {e} submesh {s}");
            seen[(e, s)] = mesh.Name;
            var ent = entries[e]!.AsObject();
            int nsub = Arr(ent, "submeshes").Count;
            if (s >= nsub && !allowNewSubmeshes)
                throw new MeshBuildException($"mesh {Repr(mesh.Name)}: submesh {s} beyond entry {e}'s {nsub} submeshes");
            int fmt = (int)Num(ent["format"]);
            var (matName, slot, matHow) = ResolveMaterial(mesh, scene, sidecar, warnings);
            if (slot is null && !newMaterials.Contains(matName)) newMaterials.Add(matName);
            int n = mesh.VertexCount;
            var derived = new List<string>();
            if (mesh.Uv0 is null) throw new MeshBuildException($"mesh {Repr(mesh.Name)}: no UV layer 0 (every vertex format stores uv0)");
            if (mesh.Normals is null) throw new MeshBuildException($"mesh {Repr(mesh.Name)}: no vertex normal buffer");
            // non-finite geometry would be encoded silently; non-finite UVs stay allowed (shipped data carries them)
            foreach (var (label, arr) in new (string, float[]?)[] { ("position", mesh.Positions), ("normal", mesh.Normals), ("tangent", mesh.Tangents), ("weight", mesh.Weights) })
            {
                if (arr is null || n == 0) continue;
                int w = arr.Length / n;
                for (int i = 0; i < arr.Length; i++)
                    if (!float.IsFinite(arr[i]))
                        throw new MeshBuildException($"mesh {Repr(mesh.Name)}: vertex {i / w} has a non-finite {label}");
            }
            var faces = mesh.Faces;
            var tangents = mesh.Tangents;
            var sign = mesh.TangentSign;
            if (tangents is null || sign is null)
            {
                var (tUv, sUv) = Vertex.TangentsFromUv(mesh.Positions, mesh.Normals, mesh.Uv0, faces);
                if (tangents is null)
                {
                    tangents = tUv;
                    derived.Add("tangents (from UV derivatives)");
                }
                if (sign is null)
                {
                    sign = sUv;
                    derived.Add("tangent handedness (from UV derivatives)");
                }
            }
            float[]? weights = null;
            long[]? bones = null;
            if (Vertex.IsSkinned(fmt))
            {
                if (mesh.Weights is null || mesh.Bones is null)
                    throw new MeshBuildException($"mesh {Repr(mesh.Name)}: vertex format {fmt} is skinned but the Cast mesh has no weights");
                if (emap is null)
                    throw new MeshBuildException($"mesh {Repr(mesh.Name)}: skinned mesh but the Cast has no Skeleton to map bones to entities");
                (weights, bones) = Lanes(mesh, emap, n);
            }
            else if (mesh.Weights is not null)
                warnings.Add($"mesh {Repr(mesh.Name)}: weights ignored (vertex format {fmt} is not skinned)");
            long[]? ids = null;
            if (mesh.Props.TryGetValue("bp_vertex_id", out var idp))
            {
                if (idp.ValueCount == n)
                {
                    ids = new long[n];
                    for (int i = 0; i < n; i++) ids[i] = (long)idp.NumberAt(i);
                    long vcount = (long)Num(ent["vertex_count"]);
                    if (Math.Min(0, ids.Length > 0 ? ids.Min() : 0) < 0 || Math.Max(0, ids.Length > 0 ? ids.Max() : 0) >= vcount)
                    {
                        warnings.Add($"mesh {Repr(mesh.Name)}: bp_vertex_id outside entry {e}'s {vcount} vertices; ignored");
                        ids = null;
                    }
                }
                else warnings.Add($"mesh {Repr(mesh.Name)}: bp_vertex_id has {idp.ValueCount} values for {n} vertices; ignored");
            }
            var rm = new ResolvedCastMesh
            {
                Src = mesh, Entry = e, Submesh = s, MatchedBy = how, Format = fmt, MaterialName = matName, MaterialSlot = slot,
                MaterialMatchedBy = matHow, Positions = mesh.Positions, Normals = mesh.Normals, Tangents = tangents, TangentSign = sign,
                Uv0 = mesh.Uv0, Uv1 = mesh.Uv1, Faces = faces, Weights = weights, Bones = bones, VertexIds = ids,
            };
            rm.Derived.AddRange(derived);
            resolved.Add(rm);
        }
        return new ResolvedCastImport
        {
            Scene = scene, Sidecar = sidecar, Meshes = resolved, EntityMap = emap, BonesMatchedBy = bonesHow,
            BoneChanges = boneChanges, NewMaterials = newMaterials, Warnings = warnings,
        };
    }

    /// <summary>
    /// Scene weights → 4 lanes of (weight, entity). The scene's lane order is kept when it has at most 4 lanes (2,904
    /// shipped rows have a zero lane before an active one and must re-encode bit-exactly); wider buffers are compacted
    /// active-first (stable). New geometry may only use bones the mesh already has: a weight on any other bone is refused.
    /// </summary>
    private static (float[] Weights, long[] Bones) Lanes(CastImportMesh mesh, long[] emap, int n)
    {
        int k = mesh.Influences;
        var w = mesh.Weights!;
        var b = mesh.Bones!;
        int worst = 0, worstCount = 0;
        for (int i = 0; i < n; i++)
        {
            int act = 0;
            for (int j = 0; j < k; j++) if (w[i * k + j] > 0) act++;
            if (act > worstCount) (worst, worstCount) = (i, act);
        }
        if (worstCount > MaxInfluences)
            throw new MeshBuildException($"mesh {Repr(mesh.Name)}: vertex {worst} has {worstCount} non-zero weights; the vertex formats carry at most {MaxInfluences}");
        for (int i = 0; i < n; i++)
        {
            bool any = false;
            for (int j = 0; j < k && !any; j++) any = w[i * k + j] > 0;
            if (!any) throw new MeshBuildException($"mesh {Repr(mesh.Name)}: vertex {i} has no bone weights (every shipped skinned row sums to 255)");
        }
        for (int i = 0; i < n; i++)
            for (int j = 0; j < k; j++)
                if (w[i * k + j] > 0 && (b[i * k + j] < 0 || b[i * k + j] >= emap.Length))
                    throw new MeshBuildException($"mesh {Repr(mesh.Name)}: vertex {i} is weighted to bone {b[i * k + j]}, which the mesh does not have "
                                                 + $"(bone index outside the {emap.Length}-bone skeleton); new geometry may only use bones the mesh already has");
        var w4 = new float[n * MaxInfluences];
        var b4 = new long[n * MaxInfluences];
        int lanes = Math.Min(MaxInfluences, k);
        Span<int> order = stackalloc int[k];
        for (int i = 0; i < n; i++)
        {
            int o = 0;
            if (k <= MaxInfluences) for (int j = 0; j < k; j++) order[o++] = j;
            else
            {
                for (int j = 0; j < k; j++) if (w[i * k + j] > 0) order[o++] = j;
                for (int j = 0; j < k; j++) if (!(w[i * k + j] > 0)) order[o++] = j;
            }
            for (int j = 0; j < lanes; j++)
            {
                float wv = w[i * k + order[j]];
                w4[i * MaxInfluences + j] = wv;
                b4[i * MaxInfluences + j] = wv > 0 ? emap[Math.Clamp(b[i * k + order[j]], 0, emap.Length - 1)] : 0;
            }
        }
        return (w4, b4);
    }

    /// <summary>The sidecar written at export, or a <see cref="MeshBuildException"/> naming the file.</summary>
    public static JsonObject LoadSidecar(string path)
    {
        JsonObject side;
        try { side = JsonNode.Parse(System.IO.File.ReadAllText(path))?.AsObject() ?? throw new MeshFormatException("empty document"); }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or IOException)
        {
            throw new MeshFormatException($"{path}: cannot read sidecar: {e.Message}");
        }
        if (!SchemaMatches(side["schema"]))
            throw new MeshFormatException($"{path}: schema {Repr(side["schema"]?.ToString())} is not {SidecarSchema}");
        return side;
    }

    // ---- Python-style text ------------------------------------------------------------------------------------------

    internal static string Repr(string? s) => s is null ? "None" : "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    internal static string ReprList(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(Repr)) + "]";

    private static string G3(double d) => d.ToString("G3", CultureInfo.InvariantCulture);
}
