using System.Text;
using Nightrunner.Core.Mesh;

namespace Nightrunner.Core.Cast;

/// <summary>
/// One Cast mesh node: which geometry entry / submesh it came from and the original vertex id (index into the entry's
/// vertex window) of each Cast vertex. Port of <c>cast/export.py::MeshMapping</c>.
/// </summary>
public sealed record CastMeshMapping(string Name, int Entry, int Submesh, uint[] VertexIds, int VertexCount, int FaceCount);

/// <summary>Port of <c>cast/export.py::ExportReport</c>.</summary>
public sealed class CastExportReport
{
    public string Path { get; set; } = "";
    /// <summary>Node counts in the prototype's order: root, meta, modl, skel, bone, matl, mesh.</summary>
    public OrderedDictionary<string, int> Nodes { get; } = new(StringComparer.Ordinal);
    public List<CastMeshMapping> Meshes { get; } = [];
    public List<string> Skipped { get; } = [];
    /// <summary>max |R_polar − M3x3| over the bones' local frames (0 = pure rotations).</summary>
    public double BoneFrameResidual { get; set; }

    /// <summary><c>to_json()</c>: path, nodes, skipped, bone_frame_residual, meshes (name, entry, submesh, vertices, faces).</summary>
    public OrderedDictionary<string, object?> ToJson() => new(StringComparer.Ordinal)
    {
        ["path"] = Path,
        ["nodes"] = new OrderedDictionary<string, object?>(Nodes.Select(kv => KeyValuePair.Create(kv.Key, (object?)kv.Value)), StringComparer.Ordinal),
        ["skipped"] = Skipped.Cast<object?>().ToList(),
        ["bone_frame_residual"] = BoneFrameResidual,
        ["meshes"] = Meshes.Select(m => (object?)new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = m.Name, ["entry"] = m.Entry, ["submesh"] = m.Submesh, ["vertices"] = m.VertexCount, ["faces"] = m.FaceCount,
        }).ToList(),
    };
}

/// <summary>
/// Mesh model → per-mesh Cast scene. Port of <c>cast/export.py</c> (<c>build_cast</c>, <c>export_cast</c>, the rotation
/// helpers and <c>cast_text</c>); the file is byte-identical to the prototype's when the float maths agree to the last
/// float32 bit (see the remarks).
/// </summary>
/// <remarks>
/// <code>
/// Root
///   Metadata   up 'y', software 'nightrunner 0.1.0' (native engine coordinates)
///   Model (name)
///     Skeleton   Bone per entity (class-4 order): name, parent, local pos + polar rotation of the local 3×3, world
///                pos/rot from the composed globals, scale 1; bp_entity, bp_flags, bp_type, bp_geometry_count
///     Material   one per used material slot: name, type 'pbr', bp_material_slot
///     Mesh       per (entry, submesh) '&lt;name&gt;.e&lt;entry&gt;.s&lt;sub&gt;': vp/vn/vt, u0 (+u1), f compacted to the used vertices,
///                wb/wv (mi 4) when skinned; bp_format, bp_entry, bp_submesh, bp_material_slot, bp_owner_entity,
///                bp_vertex_base, bp_vertex_count, bp_index_base, bp_raw_00/12/14/17/34, bp_vertex_id, bp_tangent_sign
/// </code>
/// The polar rotation (3×3 SVD by one-sided Jacobi) and the quaternion (4×4 symmetric Jacobi eigenvectors) are computed
/// in double as the prototype does with LAPACK; they agree to ~1e-16, which rounds to the same float32 except at
/// rounding boundaries. Float buffers go through the prototype's float32 → double → float32 path, which quiets a
/// signalling NaN (bit 22 set); the exact bits stay in the sidecar's raw vertex window.
/// <para/>
/// Divergence: the prototype writes <c>bp_owner_entity = -1</c> into an unsigned <c>i</c> property for an entry no entity
/// owns, which castlib refuses (such a mesh cannot be exported there). Here it is written as 0xFFFFFFFF, the Cast
/// convention for −1 (<see cref="Bone.SetParentIndex"/>); the importer does not read the property.
/// </remarks>
public static class CastExport
{
    public const string Software = "nightrunner 0.1.0";
    public const int MaxInfluences = 4;
    public const uint NoOwner = 0xFFFFFFFF;

    // ---- text ----------------------------------------------------------------------------------------------------

    /// <summary>Cast strings are UTF-8: invalid bytes become U+FFFD (the exact bytes stay in the sidecar's *_hex).</summary>
    public static string CastText(byte[] raw) => Encoding.UTF8.GetString(raw);

    /// <summary>A string's lone surrogates become U+FFFD (as the prototype's surrogateescape → replace).</summary>
    public static string CastText(string s) => Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(s));

    // ---- rotations -----------------------------------------------------------------------------------------------

    /// <summary>Closest proper rotation to a 3×3 (SVD polar factor U·Vᵀ, det forced positive by negating U's last column).</summary>
    public static double[,] PolarRotation(double[,] m)
    {
        if (m.GetLength(0) != 3 || m.GetLength(1) != 3) throw new ArgumentException("polar rotation needs a 3×3 matrix", nameof(m));
        foreach (var x in m)
            if (!double.IsFinite(x)) throw new MeshUnsupportedException("polar rotation of a non-finite 3×3 (SVD does not converge)");
        var (u, _, v) = Svd3(m);
        var r = MulT(u, v);
        if (Det3(r) < 0)
        {
            for (int i = 0; i < 3; i++) u[i, 2] = -u[i, 2];
            r = MulT(u, v);
        }
        return r;
    }

    /// <summary>Proper rotation matrix → (x, y, z, w) with w ≥ 0 (symmetric-eigenvector method, robust for any rotation).</summary>
    public static double[] QuaternionXyzw(double[,] r)
    {
        if (r.GetLength(0) != 3 || r.GetLength(1) != 3) throw new ArgumentException("quaternion needs a 3×3 matrix", nameof(r));
        var k = new double[4, 4]
        {
            { r[0, 0] - r[1, 1] - r[2, 2], r[1, 0] + r[0, 1], r[2, 0] + r[0, 2], r[2, 1] - r[1, 2] },
            { r[1, 0] + r[0, 1], r[1, 1] - r[0, 0] - r[2, 2], r[2, 1] + r[1, 2], r[0, 2] - r[2, 0] },
            { r[2, 0] + r[0, 2], r[2, 1] + r[1, 2], r[2, 2] - r[0, 0] - r[1, 1], r[1, 0] - r[0, 1] },
            { r[2, 1] - r[1, 2], r[0, 2] - r[2, 0], r[1, 0] - r[0, 1], r[0, 0] + r[1, 1] + r[2, 2] },
        };
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++) k[i, j] /= 3.0;
        var (values, vectors) = SymmetricEigen(k);
        int best = 0;
        for (int j = 1; j < 4; j++) if (values[j] > values[best]) best = j;
        var q = new double[4];
        double n = 0;
        for (int i = 0; i < 4; i++) { q[i] = vectors[i, best]; n += q[i] * q[i]; }
        n = Math.Sqrt(n);
        for (int i = 0; i < 4; i++) q[i] /= n;
        if (q[3] < 0) for (int i = 0; i < 4; i++) q[i] = -q[i];
        return q;
    }

    // ---- build -----------------------------------------------------------------------------------------------------

    /// <summary>The Cast scene of <paramref name="model"/>, named <paramref name="name"/> (the prototype uses the resource name).</summary>
    public static (CastFile Cast, CastExportReport Report) Build(MeshModel model, string name)
    {
        var cast = new CastFile();
        var root = cast.CreateRoot();
        var meta = root.CreateMetadata();
        meta.SetUpAxis("y");
        meta.SetSoftware(Software);
        var mdl = root.CreateModel();
        string modelName = CastText(name);
        mdl.SetName(modelName);
        var rep = new CastExportReport();
        var counts = rep.Nodes;
        foreach (var k in new[] { "root", "meta", "modl" }) counts[k] = 1;
        foreach (var k in new[] { "skel", "bone", "matl", "mesh" }) counts[k] = 0;

        // ---- skeleton ----------------------------------------------------------------------------------------------
        var globals = model.EntityGlobals();
        var skel = mdl.CreateSkeleton();
        counts["skel"] = 1;
        double residual = 0;
        foreach (var ent in model.Entities)
        {
            var bone = skel.CreateBone();
            bone.SetName(CastText(ent.Name));
            bone.SetParentIndex(ent.Parent);
            bone.SetSegmentScaleCompensate(false);
            var l3 = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++) l3[i, j] = ent.Local[i * 4 + j];
            var rl = PolarRotation(l3);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++) residual = Math.Max(residual, Math.Abs(rl[i, j] - l3[i, j]));
            bone.SetLocalPosition([F(ent.Local[3]), F(ent.Local[7]), F(ent.Local[11])]);
            bone.SetLocalRotation(Floats(QuaternionXyzw(rl)));
            bone.SetScale([1f, 1f, 1f]);
            var g = globals[ent.Index];
            bone.SetWorldPosition([F(g[3]), F(g[7]), F(g[11])]);
            var g3 = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++) g3[i, j] = g[i * 4 + j];
            bone.SetWorldRotation(Floats(QuaternionXyzw(PolarRotation(g3))));
            Prop(bone, "bp_entity", (uint)ent.Index);
            Prop(bone, "bp_flags", ent.Flags);
            Prop(bone, "bp_type", (uint)ent.Type);
            Prop(bone, "bp_geometry_count", (uint)ent.GeometryCount);
            counts["bone"]++;
        }
        rep.BoneFrameResidual = residual;

        // ---- materials ---------------------------------------------------------------------------------------------
        var usedSlots = model.GeometryEntries.SelectMany(e => e.Submeshes).Select(s => s.MaterialSlot).Distinct().Order().ToList();
        var matNodes = new Dictionary<int, Material>();
        foreach (int slot in usedSlots)
        {
            var m = mdl.CreateMaterial();
            m.SetName(CastText(model.MaterialName(slot)));
            m.SetType("pbr");
            Prop(m, "bp_material_slot", (uint)slot);
            matNodes[slot] = m;
            counts["matl"]++;
        }

        // ---- meshes ------------------------------------------------------------------------------------------------
        foreach (var e in model.GeometryEntries)
        {
            var v = e.Vertices;
            foreach (var s in e.Submeshes)
            {
                string meshName = $"{modelName}.e{e.Index}.s{s.Index}";
                if (v is null)
                {
                    rep.Skipped.Add($"{meshName}: vertex format {e.Format} not decodable / no vertex buffer");
                    continue;
                }
                if (s.IndexCount == 0)
                {
                    rep.Skipped.Add($"{meshName}: empty submesh (0 indices)");
                    continue;
                }
                var idx = s.Indices;
                if (idx.Length == 0)   // the prototype fails here with a bare ValueError (max of an empty face list)
                    throw new MeshFormatException($"{meshName}: {s.IndexCount} indices declared but no index part");
                int maxIndex = 0;
                foreach (var x in idx) if (x > maxIndex) maxIndex = x;
                if (maxIndex >= v.Count) throw new MeshFormatException($"{meshName}: index {maxIndex} >= vertex count {v.Count}");

                // np.unique(return_inverse): ascending used ids, faces remapped into them
                var mark = new int[v.Count];
                foreach (var x in idx) mark[x] = 1;
                var used = new List<uint>();
                for (int i = 0; i < mark.Length; i++)
                    if (mark[i] != 0) { mark[i] = used.Count; used.Add((uint)i); }
                var faces = new uint[idx.Length];
                for (int i = 0; i < idx.Length; i++) faces[i] = (uint)mark[idx[i]];
                int n = used.Count;

                var mesh = mdl.CreateMesh();
                mesh.SetName(meshName);
                mesh.SetMaterial(matNodes[s.MaterialSlot].Hash);
                mesh.SetUVLayerCount(v.Uv1 is not null ? 2 : 1);
                mesh.SetColorLayerCount(0);
                mesh.SetVertexPositionBuffer(Gather(v.Positions, used, 3));
                mesh.SetVertexNormalBuffer(Gather(v.Normals, used, 3));
                mesh.SetVertexTangentBuffer(Gather(v.Tangents, used, 3));
                mesh.SetVertexUVLayerBuffer(0, Gather(v.Uv0, used, 2));
                if (v.Uv1 is not null) mesh.SetVertexUVLayerBuffer(1, Gather(v.Uv1, used, 2));
                mesh.SetFaceBuffer(faces);
                if (v.Skinned)
                {
                    var joints = v.Joints!;
                    var weights = v.Weights!;
                    var pal = s.Palette;
                    var bones = new uint[n * 4];
                    var wv = new float[n * 4];
                    for (int i = 0; i < n; i++)
                        for (int k = 0; k < 4; k++)
                        {
                            int src = (int)used[i] * 4 + k;
                            float w = weights[src];
                            wv[i * 4 + k] = F(w);
                            if (!(w > 0)) continue;
                            if (pal.Length == 0) throw new MeshFormatException($"{meshName}: skinned vertices with an empty palette");
                            if (joints[src] >= pal.Length) throw new MeshFormatException($"{meshName}: joint index outside the {pal.Length}-entry palette");
                            bones[i * 4 + k] = pal[joints[src]];
                        }
                    mesh.SetMaximumWeightInfluence(MaxInfluences);
                    mesh.SetSkinningMethod("linear");
                    mesh.SetVertexWeightBoneBuffer(bones);
                    mesh.SetVertexWeightValueBuffer(wv);
                }
                mesh.CreateProperty("bp_format", CastPropertyType.Byte).SetValues([checked((byte)e.Format)]);
                Prop(mesh, "bp_entry", (uint)e.Index);
                Prop(mesh, "bp_submesh", (uint)s.Index);
                Prop(mesh, "bp_material_slot", (uint)s.MaterialSlot);
                Prop(mesh, "bp_owner_entity", e.OwnerEntity is { } o ? (uint)o : NoOwner);
                Prop(mesh, "bp_vertex_base", checked((uint)e.VertexBase));
                Prop(mesh, "bp_vertex_count", (uint)e.VertexCount);
                Prop(mesh, "bp_index_base", checked((uint)s.IndexBase));
                mesh.CreateProperty("bp_raw_00", CastPropertyType.String).SetString(Convert.ToHexStringLower(e.Raw00));
                Prop(mesh, "bp_raw_12", (uint)e.Raw12);
                Prop(mesh, "bp_raw_14", (uint)e.Raw14);
                Prop(mesh, "bp_raw_17", (uint)e.Raw17);
                mesh.CreateProperty("bp_raw_34", CastPropertyType.String).SetString(Convert.ToHexStringLower(e.Raw34));
                mesh.CreateProperty("bp_vertex_id", CastPropertyType.Integer).SetValues(used.ToArray());
                var sign = new float[n];
                for (int i = 0; i < n; i++) sign[i] = v.TangentSign[used[i]];
                mesh.CreateProperty("bp_tangent_sign", CastPropertyType.Float).SetValues(sign);
                counts["mesh"]++;
                rep.Meshes.Add(new CastMeshMapping(meshName, e.Index, s.Index, used.ToArray(), n, faces.Length / 3));
            }
        }
        return (cast, rep);
    }

    /// <summary>
    /// Writes the Cast file atomically (<c>&lt;path&gt;.tmp</c>, then replace), as <c>export_cast</c>. glTF/GLB output
    /// (<c>cast/gltf.py</c>) is not ported and is refused.
    /// </summary>
    public static CastExportReport Write(MeshModel model, string name, string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".gltf" or ".glb") throw new MeshUnsupportedException($"{ext} export is not ported (Cast only)");
        var (cast, rep) = Build(model, name);
        string full = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        string tmp = full + ".tmp";
        try
        {
            cast.Save(tmp);
            System.IO.File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
        }
        rep.Path = full;
        return rep;
    }

    /// <summary>
    /// <c>&lt;castPath&gt;</c> plus the <c>&lt;stem&gt;.mesh.json</c> sidecar beside it, exactly as the prototype's
    /// <c>gui/meshdata.py::export_cast_files</c> (fresh hash counter per file). Returns the report and the sidecar path.
    /// </summary>
    public static (CastExportReport Report, string? SidecarPath) WriteFiles(MeshModel model, string name, string castPath,
                                                                          MeshSidecarSource? source = null, bool sidecar = true)
    {
        var rep = Write(model, name, castPath);
        if (!sidecar) return (rep, null);
        var doc = MeshSidecar.Build(model, cast: MeshSidecar.CastInfo(System.IO.Path.GetFileName(rep.Path), rep), source: source);
        string sp = MeshSidecar.PathFor(rep.Path);
        MeshSidecar.Write(doc, sp);
        return (rep, sp);
    }

    /// <summary>Node identifier census of a Cast (fourcc strings), port of <c>cast_node_counts</c>.</summary>
    public static Dictionary<string, int> NodeCounts(CastFile cast)
    {
        var outd = new Dictionary<string, int>(StringComparer.Ordinal);
        void Walk(CastNode n)
        {
            Span<byte> b = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b, n.Identifier);
            string key = Encoding.ASCII.GetString(b);
            outd[key] = outd.GetValueOrDefault(key) + 1;
            foreach (var c in n.ChildNodes) Walk(c);
        }
        foreach (var r in cast.Roots()) Walk(r);
        return outd;
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static void Prop(CastNode node, string name, uint value) =>
        node.CreateProperty(name, CastPropertyType.Integer).SetValues([value]);

    /// <summary>float32 as the prototype writes it: float32 → Python float → struct 'f' (quiets a signalling NaN).</summary>
    internal static float F(float x) =>
        float.IsNaN(x) ? BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(x) | 0x00400000u) : x;

    private static float F(double x) => (float)x;

    private static float[] Floats(double[] a) => Array.ConvertAll(a, x => (float)x);

    private static float[] Gather(float[] src, List<uint> used, int width)
    {
        var r = new float[used.Count * width];
        for (int i = 0; i < used.Count; i++)
            for (int k = 0; k < width; k++) r[i * width + k] = F(src[(int)used[i] * width + k]);
        return r;
    }

    private static double[,] MulT(double[,] a, double[,] b)   // a · bᵀ
    {
        var r = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                r[i, j] = a[i, 0] * b[j, 0] + a[i, 1] * b[j, 1] + a[i, 2] * b[j, 2];
        return r;
    }

    private static double Det3(double[,] m) =>
        m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
        - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
        + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

    /// <summary>
    /// 3×3 SVD M = U·diag(s)·Vᵀ by one-sided (Hestenes) Jacobi; singular values descending as LAPACK returns them. Rank
    /// deficiency completes U to an orthonormal basis.
    /// </summary>
    private static (double[,] U, double[] S, double[,] V) Svd3(double[,] m)
    {
        var a = (double[,])m.Clone();
        var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int sweep = 0; sweep < 64; sweep++)
        {
            bool rotated = false;
            for (int p = 0; p < 2; p++)
                for (int q = p + 1; q < 3; q++)
                {
                    double alpha = 0, beta = 0, gamma = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        alpha += a[i, p] * a[i, p];
                        beta += a[i, q] * a[i, q];
                        gamma += a[i, p] * a[i, q];
                    }
                    if (gamma == 0 || Math.Abs(gamma) <= 4e-16 * Math.Sqrt(alpha * beta)) continue;
                    rotated = true;
                    double zeta = (beta - alpha) / (2 * gamma);
                    double t = Math.Sign(zeta == 0 ? 1 : zeta) / (Math.Abs(zeta) + Math.Sqrt(1 + zeta * zeta));
                    double c = 1 / Math.Sqrt(1 + t * t), s = c * t;
                    for (int i = 0; i < 3; i++)
                    {
                        double ap = a[i, p], aq = a[i, q];
                        a[i, p] = c * ap - s * aq;
                        a[i, q] = s * ap + c * aq;
                        double vp = v[i, p], vq = v[i, q];
                        v[i, p] = c * vp - s * vq;
                        v[i, q] = s * vp + c * vq;
                    }
                }
            if (!rotated) break;
        }
        var sv = new double[3];
        for (int j = 0; j < 3; j++) sv[j] = Math.Sqrt(a[0, j] * a[0, j] + a[1, j] * a[1, j] + a[2, j] * a[2, j]);
        int[] order = [0, 1, 2];
        Array.Sort(order, (x, y) => sv[y].CompareTo(sv[x]) is var c0 && c0 != 0 ? c0 : x.CompareTo(y));
        var u = new double[3, 3];
        var vs = new double[3, 3];
        var ss = new double[3];
        double tiny = (sv[order[0]] == 0 ? 1 : sv[order[0]]) * 1e-300;
        var valid = new bool[3];
        for (int j = 0; j < 3; j++)
        {
            int o = order[j];
            ss[j] = sv[o];
            for (int i = 0; i < 3; i++) vs[i, j] = v[i, o];
            if (sv[o] > tiny)
            {
                for (int i = 0; i < 3; i++) u[i, j] = a[i, o] / sv[o];
                valid[j] = true;
            }
        }
        CompleteBasis(u, valid);
        return (u, ss, vs);
    }

    /// <summary>Fills the invalid columns of <paramref name="u"/> so the columns are orthonormal (valid columns first).</summary>
    private static void CompleteBasis(double[,] u, bool[] valid)
    {
        for (int j = 0; j < 3; j++)
        {
            if (valid[j]) continue;
            // Gram–Schmidt the standard axes against the columns already set, keep the longest remainder
            double[] best = [0, 0, 0];
            double bestLen = -1;
            for (int axis = 0; axis < 3; axis++)
            {
                double[] c = [0, 0, 0];
                c[axis] = 1;
                for (int k = 0; k < 3; k++)
                {
                    if (!valid[k]) continue;
                    double d = c[0] * u[0, k] + c[1] * u[1, k] + c[2] * u[2, k];
                    for (int i = 0; i < 3; i++) c[i] -= d * u[i, k];
                }
                double len = Math.Sqrt(c[0] * c[0] + c[1] * c[1] + c[2] * c[2]);
                if (len > bestLen + 1e-12) { bestLen = len; best = [c[0] / len, c[1] / len, c[2] / len]; }
            }
            for (int i = 0; i < 3; i++) u[i, j] = best[i];
            valid[j] = true;
        }
    }

    /// <summary>Cyclic Jacobi eigen-decomposition of a symmetric n×n matrix: eigenvalues and column eigenvectors.</summary>
    private static (double[] Values, double[,] Vectors) SymmetricEigen(double[,] matrix)
    {
        int n = matrix.GetLength(0);
        var a = (double[,])matrix.Clone();
        var v = new double[n, n];
        for (int i = 0; i < n; i++) v[i, i] = 1;
        for (int sweep = 0; sweep < 100; sweep++)
        {
            double off = 0;
            for (int p = 0; p < n; p++)
                for (int q = p + 1; q < n; q++) off += a[p, q] * a[p, q];
            if (off == 0) break;
            for (int p = 0; p < n - 1; p++)
                for (int q = p + 1; q < n; q++)
                {
                    double apq = a[p, q];
                    if (apq == 0) continue;
                    double app = a[p, p], aqq = a[q, q];
                    double g = 100 * Math.Abs(apq);
                    if (sweep > 3 && Math.Abs(app) + g == Math.Abs(app) && Math.Abs(aqq) + g == Math.Abs(aqq))
                    {
                        a[p, q] = a[q, p] = 0;
                        continue;
                    }
                    double theta = (aqq - app) / (2 * apq);
                    double t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < n; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = c * akp - s * akq;
                        a[k, q] = s * akp + c * akq;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk;
                        a[q, k] = s * apk + c * aqk;
                    }
                    a[p, q] = a[q, p] = 0;
                    for (int k = 0; k < n; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq;
                        v[k, q] = s * vkp + c * vkq;
                    }
                }
        }
        var values = new double[n];
        for (int i = 0; i < n; i++) values[i] = a[i, i];
        return (values, v);
    }
}
