using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Nightrunner.Core.Cast;
using Nightrunner.Core.Mesh.ClassReader;

namespace Nightrunner.Core.Mesh;

/// <summary>One submesh of a rebuilt geometry entry.</summary>
public sealed class SubmeshPlan
{
    public int Index { get; init; }
    public ResolvedCastMesh? Mesh { get; init; }
    /// <summary>Null until a new material gets its slot (<see cref="MeshImagePatch"/>).</summary>
    public int? MaterialSlot { get; set; }
    public required string MaterialName { get; init; }
    /// <summary>u16 indices relative to the new window.</summary>
    public required ushort[] Indices { get; init; }
    public required ushort[] Palette { get; init; }
    public bool PaletteChanged { get; init; }
    public int? OriginalIndexCount { get; init; }
    public int Moved { get; init; }
    public int Edited { get; init; }
    public int NewVertices { get; init; }

    public int IndexCount => Indices.Length;
}

/// <summary>One geometry entry: its new window records and submeshes. <see cref="Path"/>: in-place | rebuild | untouched.</summary>
public sealed class EntryPlan
{
    public required GeometryEntry Entry { get; init; }
    public required List<SubmeshPlan> Submeshes { get; init; }
    public required string Path { get; init; }
    /// <summary>New window records; null when the entry has no decodable vertex window.</summary>
    public byte[]? Records { get; init; }
    public long[]? RawIds { get; init; }
    public bool Changed { get; init; }
    public bool LayoutChanged { get; init; }

    public int VertexCount => Records is null ? 0 : Records.Length / Vertex.Stride(Entry.Format);
    public int IndexCount => Submeshes.Sum(s => s.IndexCount);
}

public sealed class RebuildPlan
{
    public required List<EntryPlan> Entries { get; init; }
    public required List<string> NewMaterials { get; init; }
    public required List<string> BoneChanges { get; init; }
    public (byte[] Before, byte[] After)? IdentityRename { get; init; }
    public bool LayoutChanged { get; init; }
    public List<string> Warnings { get; init; } = [];

    /// <summary><c>RebuildPlan.report()</c>, same keys and order.</summary>
    public JsonObject Report()
    {
        var ents = new JsonArray();
        foreach (var ep in Entries)
        {
            var e = ep.Entry;
            var subs = new JsonArray();
            foreach (var s in ep.Submeshes)
                subs.Add(new JsonObject
                {
                    ["submesh"] = s.Index, ["mesh"] = s.Mesh?.Name, ["material"] = s.MaterialName, ["material_slot"] = s.MaterialSlot,
                    ["index_count"] = new JsonArray(s.OriginalIndexCount, s.IndexCount),
                    ["palette"] = new JsonArray(s.OriginalIndexCount is null ? null : e.Submeshes[s.Index].Palette.Length, s.Palette.Length),
                    ["palette_changed"] = s.PaletteChanged, ["moved"] = s.Moved, ["edited"] = s.Edited, ["new_vertices"] = s.NewVertices,
                    ["matched_by"] = s.Mesh?.MatchedBy, ["material_matched_by"] = s.Mesh?.MaterialMatchedBy,
                    ["derived"] = new JsonArray((s.Mesh?.Derived ?? []).Select(x => (JsonNode?)x).ToArray()),
                });
            ents.Add(new JsonObject
            {
                ["entry"] = e.Index, ["path"] = ep.Path, ["format"] = e.Format,
                ["vertex_count"] = new JsonArray(e.VertexCount, ep.VertexCount), ["index_count"] = new JsonArray(e.IndexCount, ep.IndexCount),
                ["submesh_count"] = new JsonArray(e.Submeshes.Length, ep.Submeshes.Count), ["submeshes"] = subs,
            });
        }
        return new JsonObject
        {
            ["entries"] = ents,
            ["new_materials"] = Strings(NewMaterials),
            ["bone_changes"] = Strings(BoneChanges),
            ["identity_rename"] = IdentityRename is { } r ? new JsonArray(MeshModel.Text(r.Before), MeshModel.Text(r.After)) : null,
            ["layout_changed"] = LayoutChanged,
            ["warnings"] = Strings(Warnings),
        };
    }

    internal static JsonArray Strings(IEnumerable<string> s) => new(s.Select(x => (JsonNode?)x).ToArray());
}

/// <summary>New part bytes plus the plan and report. <see cref="Cloth"/>: the rebuilt part 0xF3, null when it is carried verbatim.</summary>
public sealed record RebuildResult(byte[] Image, byte[] Fixups, byte[]? Vertex, byte[]? Index, byte[]? Skin, RebuildPlan Plan, JsonObject Report,
                                   byte[]? Cloth = null);

/// <summary>
/// Decoded mesh + resolved scene → new 0x10 / 0x11 / 0xF0 / 0xF1 part bytes (and 0x12 when a new material shifts the skin
/// materials). Port of <c>mesh/rebuild.py</c>.
/// </summary>
/// <remarks>
/// Per geometry entry one of two paths:
/// <list type="bullet">
/// <item><b>in place</b>: every scene mesh of the entry carries unique <c>bp_vertex_id</c>s — the original window is kept (order,
/// unreferenced vertices, raw holes); only vertices whose float attributes differ are re-encoded; faces map back
/// through the ids.</item>
/// <item><b>rebuild</b>: ids missing (a Blender round trip) or duplicated — the window becomes the concatenation of the
/// meshes' vertices in submesh order; a vertex still identified by id, or recovered by an exact position (+ uv0,
/// normal) match, re-encodes from its raw record; new vertices take <see cref="Vertex.HoleDefaults"/>.</item>
/// </list>
/// Buffers: unchanged counts keep the original bases and sizes (bit-identical when nothing was edited); otherwise the
/// census layout rule (<see cref="PlanNewLayout"/>). The image is patched through <see cref="MeshImagePatch"/>.
/// <para/>
/// Bounds (census, 25,398 geometry-owning entities): skinned formats 6/8 store the exact AABB of the owned raw positions →
/// rewritten exactly when the geometry changed; static formats 0/3 often store a superset → grow-only.
/// </remarks>
public static class MeshRebuild
{
    public const int MaxWindowVertices = 65535;   // u16 indices (corpus max 65,529)
    /// <summary>Prefix of every cloth refusal (<see cref="ClothRebuild"/>): a new vertex in a cloth entry, a vertex in several submeshes.</summary>
    public const string ClothRefusal = ClothRebuild.RefusalPrefix;

    /// <summary>DLTB only: the writer knows DLTB object offsets and material-name shapes. A DL2 image is refused by name.</summary>
    public static void RefuseLayout(MeshModel model)
    {
        if (model.Layout != MeshLayout.Dltb.Name)
            throw new MeshBuildException($"{CastImport.Repr(model.Name)}: {model.Layout.ToUpperInvariant()} mesh writing is not supported (read-only: "
                                         + "decode, view and Cast export work; the mesh encoder only knows the Dying Light: The Beast layout). "
                                         + "Keep the original raw parts for this resource.");
    }

    // ---- plan -----------------------------------------------------------------------------------------------------

    private readonly record struct Key3(uint A, uint B, uint C);
    private readonly record struct Key8(uint A, uint B, uint C, uint D, uint E, uint F, uint G, uint H);

    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    /// <summary>
    /// Best-effort original-id recovery for scenes without <c>bp_vertex_id</c>: exact f32 (position, uv0, normal) match; else
    /// exact position with the closest (uv0, normal); else −1 (a new vertex).
    /// </summary>
    internal static long[] RecoverIds(VertexData orig, float[] positions, float[] uv0, float[] normals)
    {
        int n = positions.Length / 3;
        var outIds = new long[n];
        Array.Fill(outIds, -1L);
        if (orig.Count == 0 || n == 0) return outIds;
        Key8 Full(float[] p, float[] u, float[] nr, int i) =>
            new(Bits(p[i * 3]), Bits(p[i * 3 + 1]), Bits(p[i * 3 + 2]), Bits(u[i * 2]), Bits(u[i * 2 + 1]), Bits(nr[i * 3]), Bits(nr[i * 3 + 1]), Bits(nr[i * 3 + 2]));
        Key3 Pos(float[] p, int i) => new(Bits(p[i * 3]), Bits(p[i * 3 + 1]), Bits(p[i * 3 + 2]));
        var full = new Dictionary<Key8, int>();
        var byPos = new Dictionary<Key3, List<int>>();
        for (int i = 0; i < orig.Count; i++)
        {
            full.TryAdd(Full(orig.Positions, orig.Uv0, orig.Normals, i), i);
            var k = Pos(orig.Positions, i);
            if (!byPos.TryGetValue(k, out var l)) byPos[k] = l = [];
            l.Add(i);
        }
        for (int k = 0; k < n; k++)
        {
            if (full.TryGetValue(Full(positions, uv0, normals, k), out int hit)) { outIds[k] = hit; continue; }
            if (!byPos.TryGetValue(Pos(positions, k), out var cands)) continue;
            if (cands.Count == 1) { outIds[k] = cands[0]; continue; }
            int best = -1;
            double bestScore = 0;
            foreach (int c in cands)
            {
                double du = double.NaN;
                bool nan = false;
                for (int j = 0; j < 2; j++)
                {
                    double d = Math.Abs((double)orig.Uv0[c * 2 + j] - uv0[k * 2 + j]);
                    if (double.IsNaN(d)) nan = true;
                    else if (double.IsNaN(du) || d > du) du = d;
                }
                du = nan ? 1e9 : NanToNum(du, 1e9);
                double dot = (double)orig.Normals[c * 3] * normals[k * 3] + (double)orig.Normals[c * 3 + 1] * normals[k * 3 + 1]
                             + (double)orig.Normals[c * 3 + 2] * normals[k * 3 + 2];
                double dn = 1.0 - NanToNum(dot, -1e9);
                double score = du * 1e3 + dn;
                if (best < 0 || score < bestScore) { best = c; bestScore = score; }
            }
            outIds[k] = best;
        }
        return outIds;
    }

    /// <summary>np.nan_to_num: NaN → <paramref name="nan"/>, ±inf → ±double.MaxValue.</summary>
    private static double NanToNum(double x, double nan) =>
        double.IsNaN(x) ? nan : double.IsPositiveInfinity(x) ? double.MaxValue : double.IsNegativeInfinity(x) ? -double.MaxValue : x;

    private static (ushort[] Palette, bool Changed) PlanPalette(Submesh? subOrig, ResolvedCastMesh mesh, bool skinned, int entryIndex)
    {
        if (!skinned) return ([], false);
        var w = mesh.Weights!;
        var b = mesh.Bones!;
        var orig = subOrig?.Palette ?? [];
        var origSet = orig.Select(x => (long)x).ToHashSet();
        bool allIn = true;
        for (int i = 0; i < w.Length && allIn; i++) if (w[i] > 0) allIn = origSet.Contains(b[i]);
        if (orig.Length > 0 && allIn) return (orig, false);
        // keep the original order (existing joint bytes stay valid), append new bones in first-use order
        var pal = orig.Select(x => (long)x).ToList();
        var seen = new HashSet<long>(origSet);
        for (int i = 0; i < w.Length; i++)
            if (w[i] > 0 && seen.Add(b[i])) pal.Add(b[i]);
        if (pal.Count == 0) pal.Add(0);
        if (pal.Count > MeshGraph.MaxPalette)
            throw new MeshBuildException($"entry {entryIndex} submesh {mesh.Submesh} ({CastImport.Repr(mesh.Name)}): {pal.Count} bones in one submesh "
                                         + $"exceed the {MeshGraph.MaxPalette}-entry palette (joint bytes are u8); split the submesh");
        return (pal.Select(x => (ushort)x).ToArray(), true);
    }

    /// <summary>
    /// Joint bytes: palette index of each active bone (the raw joint kept while it still names that bone, so unchanged rows
    /// stay bit-identical with duplicate palette entries); inactive lanes keep the raw byte while it indexes the palette,
    /// else 0. <paramref name="rawJoints"/>: 4 per mesh vertex (or null); <paramref name="hasRaw"/>: per mesh vertex.
    /// </summary>
    private static byte[] Joints(ResolvedCastMesh mesh, ushort[] palette, byte[]? rawJoints, bool[] hasRaw)
    {
        int n = mesh.VertexCount;
        var first = new Dictionary<long, int>();
        for (int i = 0; i < palette.Length; i++) first.TryAdd(palette[i], i);
        var w = mesh.Weights!;
        var bones = mesh.Bones!;
        var j = new long[n * 4];
        for (int i = 0; i < n * 4; i++)
            if (w[i] > 0)
            {
                if (!first.TryGetValue(bones[i], out int k)) throw new MeshBuildException($"mesh {CastImport.Repr(mesh.Name)}: bone outside the submesh palette (internal)");
                j[i] = k;
            }
        if (rawJoints is not null && hasRaw.Any(x => x))
            for (int v = 0; v < n; v++)
            {
                if (!hasRaw[v]) continue;
                for (int l = 0; l < 4; l++)
                {
                    int i = v * 4 + l;
                    int rj = rawJoints[i];
                    bool inPal = rj < palette.Length;
                    long named = inPal ? palette[rj] : -1;
                    bool active = w[i] > 0;
                    if ((!active && inPal) || named == bones[i]) j[i] = rj;
                }
            }
        var r = new byte[n * 4];
        for (int i = 0; i < r.Length; i++) r[i] = (byte)j[i];
        return r;
    }

    private static bool RowsEqual(float[] a, int ia, float[] b, int ib, int w)
    {
        for (int k = 0; k < w; k++) if (!(a[ia * w + k] == b[ib * w + k])) return false;
        return true;
    }

    /// <summary>Structured-record equality as numpy compares records: float fields by value (NaN ≠ NaN, −0 = +0), the rest by bytes.</summary>
    private static bool RecordEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int fmt)
    {
        var o = Vertex.OffsetsOf(fmt);
        int stride = Vertex.Stride(fmt);
        int posEnd = fmt == 0 ? 6 : 12;
        for (int f = 0; f < posEnd; f += fmt == 0 ? 2 : 4)
            if (!(fmt == 0 ? HalfAt(a, f) == HalfAt(b, f) : BinaryPrimitives.ReadSingleLittleEndian(a[f..]) == BinaryPrimitives.ReadSingleLittleEndian(b[f..])))
                return false;
        int uvEnd = o.Uv1 >= 0 ? o.Uv1 + 4 : o.Uv0 + 4;           // uv1 follows uv0 directly where present
        for (int h = o.Uv0; h < uvEnd; h += 2) if (!(HalfAt(a, h) == HalfAt(b, h))) return false;
        for (int i = posEnd; i < stride; i++)
            if ((i < o.Uv0 || i >= uvEnd) && a[i] != b[i]) return false;
        return true;
    }

    private static float HalfAt(ReadOnlySpan<byte> r, int at) => Vertex.HalfToFloat(BinaryPrimitives.ReadUInt16LittleEndian(r[at..]));

    public static EntryPlan PlanEntry(MeshModel model, GeometryEntry e, List<ResolvedCastMesh> meshes, List<string> warnings)
    {
        if (e.Vertices is null)
        {
            if (meshes.Count > 0)
                throw new MeshBuildException($"entry {e.Index}: vertex format {e.Format} is not decodable / no vertex buffer; its meshes cannot be rebuilt");
            return new EntryPlan
            {
                Entry = e, Path = "untouched", Changed = false, LayoutChanged = false,
                Submeshes = e.Submeshes.Select(s => new SubmeshPlan
                {
                    Index = s.Index, MaterialSlot = s.MaterialSlot, MaterialName = model.MaterialName(s.MaterialSlot),
                    Indices = (ushort[])s.Indices.Clone(), Palette = s.Palette, OriginalIndexCount = s.IndexCount,
                }).ToList(),
            };
        }
        if (meshes.Count == 0)
        {
            if (e.Submeshes.Length == 0)
                return new EntryPlan
                {
                    Entry = e, Submeshes = [], Path = "untouched", Records = (byte[])e.Vertices.Raw.Clone(),
                    RawIds = Enumerable.Range(0, e.VertexCount).Select(i => (long)i).ToArray(),
                };
            throw new MeshBuildException($"entry {e.Index} (LOD {e.Element} of entity {(e.OwnerEntity?.ToString() ?? "None")}) has no meshes in the Cast: "
                                         + "removing a geometry entry is not supported (keep at least submesh 0 of every entry)");
        }
        var subs = meshes.OrderBy(m => m.Submesh).ToList();
        var want = subs.Select(m => m.Submesh).ToList();
        if (!want.SequenceEqual(Enumerable.Range(0, subs.Count)))
            throw new MeshBuildException($"entry {e.Index}: the Cast provides submeshes [{string.Join(", ", want)}]; they must be 0..{subs.Count - 1} "
                                         + "without gaps (renumber the mesh names / bp_submesh)");
        int fmt = e.Format, stride = Vertex.Stride(fmt);
        bool skinned = Vertex.IsSkinned(fmt);
        var orig = e.Vertices;
        var origRaw = orig.Raw;
        int nOrig = orig.Count;
        var o = Vertex.OffsetsOf(fmt);
        bool inPlace = subs.All(m => m.VertexIds is not null && m.VertexIds.Distinct().Count() == m.VertexIds.Length);
        var holes = Vertex.HoleDefaults(fmt, origRaw);
        var plans = new List<SubmeshPlan>();
        byte[] records;
        long[] rawIds;
        string path;

        byte RawJoint(long id, int lane) => origRaw[id * stride + o.Joints + lane];

        if (inPlace)
        {
            // ---- keep the window; overwrite edited vertices ------------------------------------------------------
            var v = VertexFloats.Of(orig);
            var owner = new int[nOrig];
            Array.Fill(owner, -1);
            var edited = new bool[nOrig];
            var newJoints = skinned ? (byte[])orig.Joints!.Clone() : null;
            foreach (var m in subs)
            {
                var ids = m.VertexIds!;
                int n = ids.Length;
                var subOrig = m.Submesh < e.Submeshes.Length ? e.Submeshes[m.Submesh] : null;
                var (pal, palChanged) = PlanPalette(subOrig, m, skinned, e.Index);
                var dPos = new bool[n];
                var anyD = new bool[n];
                byte[]? j = null;
                if (skinned)
                {
                    var rj = new byte[n * 4];
                    for (int i = 0; i < n; i++) for (int l = 0; l < 4; l++) rj[i * 4 + l] = RawJoint(ids[i], l);
                    j = Joints(m, pal, rj, Enumerable.Repeat(true, n).ToArray());
                }
                for (int i = 0; i < n; i++)
                {
                    int id = (int)ids[i];
                    dPos[i] = !RowsEqual(orig.Positions, id, m.Positions, i, 3);
                    bool dUv0 = !RowsEqual(orig.Uv0, id, m.Uv0, i, 2);
                    bool dUv1 = m.Uv1 is not null && orig.Uv1 is not null && !RowsEqual(orig.Uv1, id, m.Uv1, i, 2);
                    bool dFrame = !RowsEqual(orig.Normals, id, m.Normals, i, 3) || !RowsEqual(orig.Tangents, id, m.Tangents, i, 3)
                                  || orig.TangentSign[id] != m.TangentSign[i];
                    bool dSkin = false;
                    if (skinned)
                    {
                        dSkin = !RowsEqual(orig.Weights!, id, m.Weights!, i, 4);
                        for (int l = 0; l < 4 && !dSkin; l++) dSkin = RawJoint(id, l) != j![i * 4 + l];
                    }
                    anyD[i] = dPos[i] || dUv0 || dUv1 || dFrame || dSkin;
                }
                // conflicts: a window vertex written by two meshes with different values
                for (int i = 0; i < n; i++)
                {
                    int prev = owner[ids[i]];
                    if (prev >= 0 && prev != m.Submesh && anyD[i] && edited[ids[i]])
                        throw new MeshBuildException($"entry {e.Index}: window vertex {ids[i]} is edited differently by submesh {prev} and submesh "
                                                     + $"{m.Submesh} ({CastImport.Repr(m.Name)})");
                }
                for (int i = 0; i < n; i++)
                {
                    long id = ids[i];
                    if (anyD[i])
                    {
                        Array.Copy(m.Positions, i * 3, v.Positions, id * 3, 3);
                        Array.Copy(m.Uv0, i * 2, v.Uv0, id * 2, 2);
                        if (m.Uv1 is not null && v.Uv1 is not null) Array.Copy(m.Uv1, i * 2, v.Uv1, id * 2, 2);
                        Array.Copy(m.Normals, i * 3, v.Normals, id * 3, 3);
                        Array.Copy(m.Tangents, i * 3, v.Tangents, id * 3, 3);
                        v.TangentSign[id] = m.TangentSign[i];
                        if (skinned) Array.Copy(m.Weights!, i * 4, v.Weights!, id * 4, 4);
                        owner[id] = m.Submesh;
                        edited[id] = true;
                    }
                    if (skinned) Array.Copy(j!, i * 4, newJoints!, id * 4, 4);
                }
                var faces = m.Faces;
                var idx = new ushort[faces.Length];
                for (int i = 0; i < faces.Length; i++) idx[i] = (ushort)ids[faces[i]];
                plans.Add(new SubmeshPlan
                {
                    Index = m.Submesh, Mesh = m, MaterialSlot = m.MaterialSlot, MaterialName = m.MaterialName, Indices = idx, Palette = pal,
                    PaletteChanged = palChanged, OriginalIndexCount = subOrig?.IndexCount,
                    Moved = dPos.Count(x => x), Edited = anyD.Count(x => x), NewVertices = 0,
                });
            }
            if (skinned) v.Joints = newJoints;
            rawIds = Enumerable.Range(0, nOrig).Select(i => (long)i).ToArray();
            records = Vertex.EncodeBlock(v, origRaw, rawIds, holes);
            path = "in-place";
        }
        else
        {
            // ---- rebuild the window from the scene meshes ------------------------------------------------------
            var chunks = new List<byte[]>();
            var idChunks = new List<long[]>();
            int baseV = 0;
            foreach (var m in subs)
            {
                int n = m.VertexCount;
                var subOrig = m.Submesh < e.Submeshes.Length ? e.Submeshes[m.Submesh] : null;
                var (pal, palChanged) = PlanPalette(subOrig, m, skinned, e.Index);
                long[] ids;
                if (m.VertexIds is not null)
                {
                    ids = (long[])m.VertexIds.Clone();
                    if (ids.Distinct().Count() != ids.Length)
                        warnings.Add($"entry {e.Index} submesh {m.Submesh}: duplicate bp_vertex_id values; window rebuilt");
                }
                else
                {
                    ids = RecoverIds(orig, m.Positions, m.Uv0, m.Normals);
                    if (n > 0)
                        warnings.Add($"entry {e.Index} submesh {m.Submesh} ({CastImport.Repr(m.Name)}): no bp_vertex_id; "
                                     + $"{ids.Count(x => x >= 0)}/{n} vertices recovered by exact position match");
                }
                var has = ids.Select(x => x >= 0).ToArray();
                var uv1 = m.Uv1;
                if (uv1 is null && o.Uv1 >= 0)
                {
                    uv1 = (float[])m.Uv0.Clone();                   // policy: uv1 = uv0 (census majority) …
                    if (orig.Uv1 is not null)
                        for (int i = 0; i < n; i++)
                            if (has[i]) { uv1[i * 2] = orig.Uv1[ids[i] * 2]; uv1[i * 2 + 1] = orig.Uv1[ids[i] * 2 + 1]; }   // … except where the raw record is known
                }
                byte[]? joints = null;
                if (skinned)
                {
                    byte[]? rj = null;
                    if (nOrig > 0)
                    {
                        rj = new byte[n * 4];
                        for (int i = 0; i < n; i++) for (int l = 0; l < 4; l++) rj[i * 4 + l] = RawJoint(Math.Max(ids[i], 0), l);
                    }
                    joints = Joints(m, pal, rj, has);
                }
                var fv = new VertexFloats(fmt, m.Positions, m.Uv0, uv1, m.Normals, m.Tangents, m.TangentSign, m.Weights, joints);
                var rec = Vertex.EncodeBlock(fv, origRaw, ids, holes);
                chunks.Add(rec);
                idChunks.Add(ids);
                int moved = 0, editedN = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!has[i]) continue;
                    if (!RowsEqual(orig.Positions, (int)ids[i], m.Positions, i, 3)) moved++;
                    if (!RecordEqual(rec.AsSpan(i * stride, stride), origRaw.AsSpan((int)ids[i] * stride, stride), fmt)) editedN++;
                }
                var faces = m.Faces;
                var idx = new long[faces.Length];
                for (int i = 0; i < faces.Length; i++) idx[i] = faces[i] + baseV;
                plans.Add(new SubmeshPlan
                {
                    Index = m.Submesh, Mesh = m, MaterialSlot = m.MaterialSlot, MaterialName = m.MaterialName,
                    Indices = idx.Select(x => (ushort)x).ToArray(), Palette = pal, PaletteChanged = palChanged,
                    OriginalIndexCount = subOrig?.IndexCount, Moved = moved, Edited = editedN, NewVertices = has.Count(x => !x),
                });
                baseV += n;
            }
            if (baseV > MaxWindowVertices)
                throw new MeshBuildException($"entry {e.Index}: {baseV} vertices exceed the u16 index range ({MaxWindowVertices}); split the geometry");
            int newTotal = plans.Sum(p => p.NewVertices);
            if (newTotal > 0)
            {
                string note = fmt == 8 ? " (format 8: their 40-byte extension is all zero — 42/42 shipped format-8 entries contain such rows, "
                                         + "the field's meaning is unknown)" : "";
                warnings.Add($"entry {e.Index}: {newTotal} new vertices take the entry's hole policy{note}");
            }
            records = chunks.SelectMany(c => c).ToArray();
            rawIds = idChunks.SelectMany(c => c).ToArray();
            path = "rebuild";
        }

        int vcount = records.Length / stride;
        bool layoutChanged = vcount != e.VertexCount || plans.Count != e.Submeshes.Length
                             || plans.Any(p => p.Index < e.Submeshes.Length && p.IndexCount != e.Submeshes[p.Index].IndexCount);
        bool changed = layoutChanged || !records.AsSpan().SequenceEqual(origRaw)
                       || plans.Any(p => p.Index < e.Submeshes.Length && !p.Indices.AsSpan().SequenceEqual(e.Submeshes[p.Index].Indices))
                       || plans.Any(p => p.PaletteChanged);
        return new EntryPlan
        {
            Entry = e, Submeshes = plans, Path = path, Records = records, RawIds = rawIds, Changed = changed, LayoutChanged = layoutChanged,
        };
    }

    public static RebuildPlan MakePlan(MeshModel model, ResolvedCastImport imp, string logicalName)
    {
        var warnings = new List<string>(imp.Warnings);
        var byEntry = imp.ByEntry();
        var plans = model.GeometryEntries.Select(e => PlanEntry(model, e, byEntry.GetValueOrDefault(e.Index) ?? [], warnings)).ToList();
        var extra = byEntry.Keys.Except(model.GeometryEntries.Select(e => e.Index)).Order().ToList();
        if (extra.Count > 0)
            throw new MeshBuildException($"Cast meshes refer to geometry entries [{string.Join(", ", extra)}] that the mesh does not have; adding "
                                         + "geometry entries (LOD levels) is not supported");
        var exp = MeshIdentity.ExpectedEmbeddedName(logicalName);
        (byte[], byte[])? rename = exp.AsSpan().SequenceEqual(model.EmbeddedName) ? null : (model.EmbeddedName, exp);
        return new RebuildPlan
        {
            Entries = plans, NewMaterials = [.. imp.NewMaterials], BoneChanges = [.. imp.BoneChanges], IdentityRename = rename,
            LayoutChanged = plans.Any(p => p.LayoutChanged), Warnings = warnings,
        };
    }

    // ---- buffers --------------------------------------------------------------------------------------------------

    /// <summary>
    /// (vertex_base, index_base) per entry for freshly laid out buffers + total sizes (census rule): vertex windows in entry
    /// order, each zero-padded to a multiple of 160 bytes; index bases 4-aligned; the index part padded to 16 when
    /// <paramref name="padIndexPart"/> (every mesh of the on-demand packs; engine_pc stores exact sizes).
    /// </summary>
    public static (List<(long Vertex, long Index)> Bases, long VertexSize, long IndexSize) PlanNewLayout(
        IEnumerable<(int Format, int VertexCount, int IndexCount)> entries, bool padIndexPart = true)
    {
        var bases = new List<(long, long)>();
        long vcur = 0, icur = 0;
        foreach (var (fmt, nv, ni) in entries)
        {
            long vb = vcur, ib = Vertex.AlignTo(icur, Vertex.IndexBaseAlign);
            bases.Add((vb, ib));
            vcur = Vertex.AlignTo(vb + (long)nv * Vertex.Stride(fmt), Vertex.BlockAlign);
            icur = ib + ni * 2L;
        }
        return (bases, vcur, padIndexPart ? Vertex.AlignTo(icur, Vertex.IndexBufferAlign) : icur);
    }

    private static (byte[] Vertex, byte[] Index, List<(long Vertex, long Index)> Bases) AssembleBuffers(MeshModel model, RebuildPlan plan, bool padIndexPart)
    {
        List<(long Vertex, long Index)> bases;
        byte[] vout, iout;
        if (!plan.LayoutChanged)
        {
            bases = plan.Entries.Select(p => (p.Entry.VertexBase, p.Entry.IndexBase)).ToList();
            vout = (byte[])(model.VertexBuffer ?? []).Clone();
            iout = (byte[])(model.IndexBuffer ?? []).Clone();
        }
        else
        {
            foreach (var p in plan.Entries)
                if (p.Records is null && p.Entry.VertexCount > 0)
                    throw new MeshBuildException($"entry {p.Entry.Index}: vertex format {p.Entry.Format} is not decodable; its vertex window cannot be "
                                                 + "carried into a new buffer layout");
            var (b, vs, isz) = PlanNewLayout(plan.Entries.Select(p => (p.Entry.Format, p.VertexCount, p.IndexCount)), padIndexPart);
            bases = b;
            vout = new byte[vs];
            iout = new byte[isz];
        }
        for (int k = 0; k < plan.Entries.Count; k++)
        {
            var p = plan.Entries[k];
            var (vb, ib) = bases[k];
            if (p.Records is { Length: > 0 } rec)
            {
                if (vb + rec.Length > vout.Length) throw new MeshBuildException($"entry {p.Entry.Index}: vertex window exceeds the buffer (internal)");
                rec.CopyTo(vout, vb);
            }
            long cur = ib;
            foreach (var s in p.Submeshes)
            {
                if (cur + s.IndexCount * 2L > iout.Length) throw new MeshBuildException($"entry {p.Entry.Index}: index list exceeds the buffer (internal)");
                for (int i = 0; i < s.Indices.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(iout.AsSpan((int)cur + i * 2), s.Indices[i]);
                cur += s.IndexCount * 2L;
            }
        }
        return (vout, iout, bases);
    }

    // ---- top level ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Model (decoded from the raw parts) + resolved scene → new parts. Refuses rather than approximates; see the class
    /// remarks for the policies.
    /// </summary>
    public static RebuildResult Rebuild(MeshModel model, ResolvedCastImport imp, string logicalName, bool padIndexPart = true, bool ignoreBoneChanges = false)
    {
        RefuseLayout(model);
        var plan = MakePlan(model, imp, logicalName);
        if (plan.BoneChanges.Count > 0 && !ignoreBoneChanges)
            throw new MeshBuildException("bone transforms differ from the sidecar (" + string.Join("; ", plan.BoneChanges.Take(5))
                                         + (plan.BoneChanges.Count > 5 ? "; …" : "") + "): rewriting entity local/inverse-bind matrices from a Cast is "
                                         + "not supported (Cast bones carry no bind matrices); restore the skeleton, or keep the native skeleton "
                                         + "(ignore bone changes) and replace the raw parts");
        if (plan.BoneChanges.Count > 0)
            plan.Warnings.Add($"{plan.BoneChanges.Count} bone transforms differ from the sidecar; native skeleton kept");
        // cloth (part 0xF3): carried exactly through kept / reordered / deleted / duplicated vertices, refused for new ones
        ClothData? cloth = null;
        ClothRebuild.Outcome? clothOutcome = null;
        if (model.ClothRaw is not null)
        {
            cloth = ClothData.Decode(model)!;
            clothOutcome = ClothRebuild.Apply(model, cloth, plan);
            plan.Warnings.AddRange(clothOutcome.Warnings);
        }
        bool hasBuffers = model.VertexBuffer is not null || plan.Entries.Any(p => p.Records is not null);
        byte[]? vbytes = null, ibytes = null;
        var bases = model.GeometryEntries.Select(e => (e.VertexBase, e.IndexBase)).ToList();
        if (hasBuffers) (vbytes, ibytes, bases) = AssembleBuffers(model, plan, padIndexPart);
        var patched = MeshImagePatch.Apply(model, plan, bases);
        byte[] image = patched.Image;
        byte[]? clothPart = null;
        if (cloth is not null && clothOutcome!.Changed) (clothPart, image) = ClothRebuild.Write(cloth, image, patched.Fixups);
        var report = plan.Report();
        foreach (var (k, v) in patched.Report) report[k] = v?.DeepClone();
        report["vertex_size"] = new JsonArray(model.VertexBuffer?.Length ?? 0, vbytes?.Length);
        report["index_size"] = new JsonArray(model.IndexBuffer?.Length ?? 0, ibytes?.Length);
        report["image_size"] = new JsonArray(model.Image.Data.Length, patched.Image.Length);
        if (cloth is not null)
            report["cloth"] = new JsonObject
            {
                ["changed"] = clothOutcome!.Changed, ["remapped_entries"] = clothOutcome.RemappedEntries,
                ["size"] = new JsonArray(model.ClothRaw!.Length, (clothPart ?? model.ClothRaw).Length),
            };
        return new RebuildResult(image, patched.Fixups, vbytes, ibytes, patched.Skin, plan, report, clothPart);
    }

    /// <summary>Self-check: decode the produced parts and compare with the plan (counts, palettes, positions of every scene mesh).</summary>
    public static List<string> Verify(RebuildResult result, MeshModel model, string name)
    {
        var problems = new List<string>();
        var m2 = MeshDecoder.Decode(name, result.Image, result.Fixups, result.Vertex, result.Index, result.Skin ?? model.SkinRaw,
                                    result.Cloth ?? model.ClothRaw);
        var oldW = model.Warnings.ToHashSet();
        foreach (var w in m2.Warnings) if (!oldW.Contains(w)) problems.Add($"decode warning: {w}");
        if (m2.ClothRaw is not null)
        {
            // the cloth must still decode, and every render-mesh relation the shipped parts hold must still hold
            try
            {
                var oldProblems = ClothData.Decode(model)!.Check(model).ToHashSet();
                foreach (var p in ClothData.Decode(m2)!.Check(m2)) if (!oldProblems.Contains(p)) problems.Add(p);
            }
            catch (MeshFormatException e) { problems.Add(e.Message); }
        }
        if (m2.GeometryEntries.Length != result.Plan.Entries.Count)
        {
            problems.Add("geometry entry count changed");
            return problems;
        }
        for (int k = 0; k < m2.GeometryEntries.Length; k++)
        {
            var ep = result.Plan.Entries[k];
            var e2 = m2.GeometryEntries[k];
            int expectCount = ep.Records is null ? ep.Entry.VertexCount : ep.VertexCount;
            if (e2.VertexCount != expectCount || e2.Submeshes.Length != ep.Submeshes.Count)
            {
                problems.Add($"entry {e2.Index}: counts {e2.VertexCount}/{e2.Submeshes.Length} != plan {expectCount}/{ep.Submeshes.Count}");
                continue;
            }
            for (int si = 0; si < ep.Submeshes.Count; si++)
            {
                var sp = ep.Submeshes[si];
                var s2 = e2.Submeshes[si];
                if (s2.IndexCount != sp.IndexCount) problems.Add($"entry {e2.Index} submesh {s2.Index}: index count {s2.IndexCount} != {sp.IndexCount}");
                if (!s2.Palette.AsSpan().SequenceEqual(sp.Palette)) problems.Add($"entry {e2.Index} submesh {s2.Index}: palette mismatch");
                if (sp.Mesh is { } mesh && e2.Vertices is { } v2 && s2.IndexCount > 0)
                {
                    float tol = e2.Format == 0 ? 1e-2f : 0f;
                    bool ok = true;
                    for (int i = 0; i < s2.Indices.Length && ok && i < mesh.Faces.Length; i++)
                        for (int c = 0; c < 3 && ok; c++)
                        {
                            float want = mesh.Positions[mesh.Faces[i] * 3 + c];
                            if (!float.IsFinite(want)) continue;
                            float got = v2.Positions[s2.Indices[i] * 3 + c];
                            ok = Math.Abs((double)got - want) <= tol;
                        }
                    if (!ok) problems.Add($"entry {e2.Index} submesh {s2.Index}: positions differ after re-decode");
                }
            }
        }
        return problems;
    }
}
