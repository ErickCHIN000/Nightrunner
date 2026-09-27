using Nightrunner.Core.Mesh;
using CastMesh = Nightrunner.Core.Cast.Mesh;
using CastModel = Nightrunner.Core.Cast.Model;

namespace Nightrunner.Core.Cast;

/// <summary>
/// The per-submesh half of the splitter (<c>_apply</c> and its helpers): un-rebind, snap to the original records,
/// identify moved / new / duplicate vertices, write the template node. Numbers follow numpy's order of operations.
/// </summary>
public static partial class ModelSplit
{
    /// <summary>A submesh the splitter refuses (the prototype's <c>SplitError</c> inside <c>_apply</c>).</summary>
    internal sealed class SplitRefusal(string message) : Exception(message);

    /// <summary>The template node's buffers (<c>_node_buffers</c>), as float64 of the stored values.</summary>
    private sealed class Orig
    {
        public required double[] Vp, Vn, U0;
        public double[]? Vt, U1, Wv, Sign;
        public long[]? Wb, Ids;
        public int Mi;
        public int N => Vp.Length / 3;
    }

    private static Orig NodeBuffers(CastMesh node)
    {
        var vp = node.Property("vp")!.ToDoubleArray();
        int mi = node.MaximumWeightInfluence();
        return new Orig
        {
            Vp = vp,
            Vn = node.Property("vn")!.ToDoubleArray(),
            Vt = node.Property("vt")?.ToDoubleArray(),
            U0 = node.Property("u0")!.ToDoubleArray(),
            U1 = node.Property("u1")?.ToDoubleArray(),
            Mi = mi,
            Wb = mi > 0 ? Longs(node.Property("wb")) : null,
            Wv = mi > 0 ? node.Property("wv")?.ToDoubleArray() : null,
            Ids = Longs(node.Property("bp_vertex_id")),
            Sign = node.Property("bp_tangent_sign")?.ToDoubleArray(),
        };
    }

    // numpy casts NaN / out-of-range floats to int64 as INT64_MIN on x86-64
    private static long NpLong(double d) => double.IsNaN(d) || d >= 9.2233720368547758e18 || d < -9.2233720368547758e18 ? long.MinValue : (long)d;

    private static (long, long, long) Cell(double[] v, int i, double cell) =>
        (NpLong(Math.Floor(v[i * 3] / cell)), NpLong(Math.Floor(v[i * 3 + 1] / cell)), NpLong(Math.Floor(v[i * 3 + 2] / cell)));

    /// <summary>max |a[i] − b[j]| over 3 components (NaN propagates, as numpy's max).</summary>
    private static double MaxAbs3(double[] a, int i, double[] b, int j) =>
        Math.Max(Math.Max(Math.Abs(a[i * 3] - b[j * 3]), Math.Abs(a[i * 3 + 1] - b[j * 3 + 1])), Math.Abs(a[i * 3 + 2] - b[j * 3 + 2]));

    private static double SumAbs3(double[] a, int i, double[] b, int j) =>
        Math.Abs(a[i * 3] - b[j * 3]) + Math.Abs(a[i * 3 + 1] - b[j * 3 + 1]) + Math.Abs(a[i * 3 + 2] - b[j * 3 + 2]);

    private static double Norm3(double[] a, int i, double[] b, int j)
    {
        double x = a[i * 3] - b[j * 3], y = a[i * 3 + 1] - b[j * 3 + 1], z = a[i * 3 + 2] - b[j * 3 + 2];
        return Math.Sqrt(x * x + y * y + z * z);
    }

    /// <summary><c>_uv_dist</c>: max per-row UV difference, ignoring components non-finite in the original.</summary>
    private static double UvDist(double[] orig, int j, double[] uv, int i)
    {
        double a = double.IsFinite(orig[j * 2]) ? Math.Abs(orig[j * 2] - uv[i * 2]) : 0;
        double b = double.IsFinite(orig[j * 2 + 1]) ? Math.Abs(orig[j * 2 + 1] - uv[i * 2 + 1]) : 0;
        return Math.Max(a, b);
    }

    private static double[] Take(double[] a, int width, bool[] keep)
    {
        var r = new List<double>(a.Length);
        for (int i = 0; i < keep.Length; i++)
            if (keep[i]) for (int c = 0; c < width; c++) r.Add(a[i * width + c]);
        return [.. r];
    }

    private static long[] Take(long[] a, int width, bool[] keep)
    {
        var r = new List<long>(a.Length);
        for (int i = 0; i < keep.Length; i++)
            if (keep[i]) for (int c = 0; c < width; c++) r.Add(a[i * width + c]);
        return [.. r];
    }

    private static long[] Take(long[] a, bool[] keep) => Take(a, 1, keep);

    /// <summary><c>_drop_loose</c>: remove vertices no triangle uses (Blender keeps them after face deletion).</summary>
    private static Em DropLoose(Em em)
    {
        int n = em.N;
        var used = new bool[n];
        foreach (var f in em.Faces)
        {
            if (f < 0 || f >= n) throw new SplitRefusal($"face index {f} outside the {n} vertices");
            used[f] = true;
        }
        if (used.All(x => x)) return em;
        var lut = new long[n];
        long count = 0;
        for (int i = 0; i < n; i++) { if (used[i]) count++; lut[i] = count - 1; }
        var o = em.With(em.Name);
        o.Pos = Take(em.Pos, 3, used);
        if (em.Nrm is { } nrm && nrm.Length == n * 3) o.Nrm = Take(nrm, 3, used);
        if (em.Uv0 is { } u0 && u0.Length == n * 2) o.Uv0 = Take(u0, 2, used);
        if (em.Uv1 is { } u1 && u1.Length == n * 2) o.Uv1 = Take(u1, 2, used);
        if (em.Bones is { } bn && bn.Length == n * em.K) o.Bones = Take(bn, em.K, used);
        if (em.Weights is { } wv && wv.Length == n * em.K) o.Weights = Take(wv, em.K, used);
        o.Faces = em.Faces.Select(f => lut[f]).ToArray();
        o.LooseDropped = used.Count(x => !x);
        return o;
    }

    /// <summary><c>_top4</c>: the 4 largest influences per vertex (stable on ties), renormalised; and the vertices pruned.</summary>
    private static (long[] Bones, double[] Weights, int Pruned) Top4(long[] bones, double[] weights, int k, int n)
    {
        const int M = CastExport.MaxInfluences;
        int kk = Math.Max(k, M);
        var ob = new long[n * M];
        var ow = new double[n * M];
        int pruned = 0;
        var order = new int[kk];
        var w = new double[kk];
        var bb = new long[kk];
        for (int i = 0; i < n; i++)
        {
            int positive = 0;
            for (int j = 0; j < kk; j++)
            {
                w[j] = j < k ? weights[i * k + j] : 0;
                bb[j] = j < k ? bones[i * k + j] : 0;
                order[j] = j;
                if (w[j] > 0) positive++;
            }
            if (positive > M) pruned++;
            // argsort(-w, kind="stable"): descending, ties keep lane order, NaN last
            var sorted = order.OrderBy(j => -w[j], NumpyOrder.Instance).ToArray();
            double s = 0;
            for (int j = 0; j < M; j++) s += w[sorted[j]];
            for (int j = 0; j < M; j++)
            {
                ob[i * M + j] = bb[sorted[j]];
                ow[i * M + j] = s > 0 ? w[sorted[j]] / Math.Max(s, 1e-12) : w[sorted[j]];
            }
        }
        return (ob, ow, pruned);
    }

    /// <summary>numpy's float sort order: NaN after everything, −0 equal to +0.</summary>
    private sealed class NumpyOrder : IComparer<double>
    {
        public static readonly NumpyOrder Instance = new();
        public int Compare(double a, double b) =>
            a < b ? -1 : a > b ? 1 : a == b ? 0 : double.IsNaN(a) ? (double.IsNaN(b) ? 0 : 1) : -1;
    }

    /// <summary><c>_apply</c>: one edited object onto one template submesh node. Returns the statistics in the prototype's key order.</summary>
    private static OrderedDictionary<string, object?> Apply(CastMesh node, Em em, List<string> ebones, Dictionary<string, int> ents,
                                                            double[][] rebind, MeshModel model, Target mm, double tol, double uvTol,
                                                            CastModel templateModel)
    {
        var orig = NodeBuffers(node);
        if (em.Uv0 is null || em.Nrm is null || em.Faces.Length == 0) throw new SplitRefusal("mesh has no UVs / normals / faces");
        // the prototype fails on these with a numpy shape error that aborts the whole split
        if (em.Faces.Length % 3 != 0) throw new SplitRefusal($"{em.Faces.Length} face indices are not whole triangles");
        if (em.Uv0.Length != em.N * 2 || em.Nrm.Length != em.N * 3) throw new SplitRefusal($"UV / normal count does not match the {em.N} vertices");
        em = DropLoose(em);
        var pos = em.Pos;
        int n = em.N;
        var ge = model.GeometryEntries[mm.Entry];
        bool skinned = ge.Vertices is { Skinned: true };
        var stats = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["vertices"] = n, ["original_vertices"] = orig.N, ["faces"] = em.Faces.Length / 3, ["loose_dropped"] = em.LooseDropped ?? 0,
        };
        string emMat = StripSuffix(em.Material ?? "");
        if (emMat.Length > 0 && emMat != mm.Material)
            // a different material than the one exported (single-Cast names may carry '#<override hash>')
            AssignMaterial(node, emMat.Split('#', 2)[0], templateModel, stats);

        var nrm = em.Nrm!;
        long[]? bones = null;
        double[]? weights = null;
        if (skinned)
        {
            if (em.Weights is null || em.Bones is null) throw new SplitRefusal("skinned mesh lost its weights");
            var (wb, wv, pruned) = Top4(em.Bones, em.Weights, em.K, n);
            stats["pruned_influences"] = pruned;
            var partBones = new long[wb.Length];
            foreach (long b in Enumerable.Range(0, wb.Length).Where(i => wv[i] > 0).Select(i => wb[i]).Distinct().Order())
            {
                string? name = b >= 0 && b < ebones.Count ? ebones[(int)b].ToLowerInvariant() : null;
                if (name is null || !ents.TryGetValue(name, out int ent))
                    throw new SplitRefusal($"weights on bone {(name is not null ? PyRepr(ebones[(int)b]) : b.ToString())} that {PyRepr(model.Name)} does not have");
                for (int i = 0; i < wb.Length; i++) if (wb[i] == b && wv[i] > 0) partBones[i] = ent;
            }
            var p2 = new double[n * 3];
            var n2 = new double[n * 3];
            for (int i = 0; i < n; i++)
            {
                var blend = new double[16];
                for (int k = 0; k < 4; k++)
                {
                    var r = rebind[partBones[i * 4 + k]];
                    double w = wv[i * 4 + k];
                    for (int c = 0; c < 16; c++) blend[c] += w * r[c];
                }
                double[] inv;
                try { inv = ModelCast.Invert(blend); }
                catch (ArithmeticException) { throw new SplitRefusal($"vertex {i}: its weights give a singular skin blend"); }
                double x = pos[i * 3], y = pos[i * 3 + 1], z = pos[i * 3 + 2];
                p2[i * 3] = inv[0] * x + inv[1] * y + inv[2] * z + inv[3];
                p2[i * 3 + 1] = inv[4] * x + inv[5] * y + inv[6] * z + inv[7];
                p2[i * 3 + 2] = inv[8] * x + inv[9] * y + inv[10] * z + inv[11];
                x = nrm[i * 3]; y = nrm[i * 3 + 1]; z = nrm[i * 3 + 2];
                double nx = inv[0] * x + inv[1] * y + inv[2] * z, ny = inv[4] * x + inv[5] * y + inv[6] * z, nz = inv[8] * x + inv[9] * y + inv[10] * z;
                double len = Math.Max(Math.Sqrt(nx * nx + ny * ny + nz * nz), 1e-12);
                n2[i * 3] = nx / len;
                n2[i * 3 + 1] = ny / len;
                n2[i * 3 + 2] = nz / len;
            }
            (pos, nrm, bones, weights) = (p2, n2, partBones, wv);
        }
        var uv0 = em.Uv0!;
        var uv1 = em.Uv1;
        var emFaces = em.Faces;

        // ---- snap ---------------------------------------------------------------------------------------------------
        int nOrig = orig.N;
        var snap = Enumerable.Repeat(-1L, n).ToArray();
        var ou0 = orig.U0;
        if (n == nOrig)
            // Blender keeps vertex order when it had no UV seam to split (the Cast already carries split vertices)
            for (int i = 0; i < n; i++)
                if (MaxAbs3(orig.Vp, i, pos, i) <= tol && UvDist(ou0, i, uv0, i) <= uvTol && MaxAbs3(orig.Vn, i, nrm, i) <= 1e-2) snap[i] = i;
        var claimed = new bool[nOrig];
        foreach (var s in snap) if (s >= 0) claimed[s] = true;
        foreach (double t in new SortedSet<double> { Math.Min(tol, 1e-6), tol })
        {
            // tight pass first: a near neighbour must not steal an original from its exact (float-noise) match
            var rest = Enumerable.Range(0, n).Where(i => snap[i] < 0).ToList();
            if (rest.Count == 0) break;
            double cell = Math.Max(t * 4, 1e-9);
            var grid = new Dictionary<(long, long, long), List<int>>();
            for (int j = 0; j < nOrig; j++)
                if (!claimed[j]) Bucket(grid, Cell(orig.Vp, j, cell), j);
            foreach (int i in rest)
            {
                var (kx, ky, kz) = Cell(pos, i, cell);
                int best = -1;
                double bd = double.PositiveInfinity;
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (!grid.TryGetValue((kx + dx, ky + dy, kz + dz), out var list)) continue;
                            foreach (int j in list)
                            {
                                if (claimed[j]) continue;
                                double d = MaxAbs3(orig.Vp, j, pos, i);
                                double du = UvDist(ou0, j, uv0, i);
                                if (d <= t && du <= uvTol)
                                {
                                    double score = d + du * 10 + SumAbs3(orig.Vn, j, nrm, i);
                                    if (score < bd) (best, bd) = (j, score);
                                }
                            }
                        }
                if (best >= 0)
                {
                    snap[i] = best;
                    claimed[best] = true;
                }
            }
        }
        var dup = Enumerable.Repeat(-1L, n).ToArray();      // extra copies of an original vertex (Blender normal splits)
        var left = Enumerable.Range(0, n).Where(i => snap[i] < 0).ToList();
        if (left.Count > 0)
        {
            double cell = Math.Max(tol * 4, 1e-9);
            var grid2 = new Dictionary<(long, long, long), List<int>>();
            for (int j = 0; j < nOrig; j++) Bucket(grid2, Cell(orig.Vp, j, cell), j);
            foreach (int i in left)
                if (grid2.TryGetValue(Cell(pos, i, cell), out var list))
                    foreach (int j in list)
                        if (MaxAbs3(orig.Vp, j, pos, i) <= tol && UvDist(ou0, j, uv0, i) <= uvTol)
                        {
                            dup[i] = j;
                            break;
                        }
        }
        // moved vertices: UVs survive a move, so an unclaimed original with the same UV0 (nearest in space) is taken
        var uvmatch = Enumerable.Repeat(-1L, n).ToArray();
        left = Enumerable.Range(0, n).Where(i => snap[i] < 0 && dup[i] < 0).ToList();
        if (left.Count > 0)
        {
            var free = Enumerable.Range(0, nOrig).Where(j => !claimed[j]).ToList();
            if (free.Count > 0)
            {
                double q = Math.Max(uvTol, 1e-9);
                static double Fin(double v) => double.IsNaN(v) ? 9e9 : double.IsPositiveInfinity(v) ? 9e9 : double.IsNegativeInfinity(v) ? -9e9 : v;
                var buckets = new Dictionary<(long, long), List<int>>();
                foreach (int j in free)
                {
                    var key = (NpLong(Math.Round(Fin(ou0[j * 2]) / q)), NpLong(Math.Round(Fin(ou0[j * 2 + 1]) / q)));
                    if (!buckets.TryGetValue(key, out var l)) buckets[key] = l = [];
                    l.Add(j);
                }
                foreach (int i in left)
                {
                    if (!buckets.TryGetValue((NpLong(Math.Round(uv0[i * 2] / q)), NpLong(Math.Round(uv0[i * 2 + 1] / q))), out var cands)) continue;
                    int bestJ = -1;
                    double bestD = double.NaN;
                    foreach (int j in cands)
                    {
                        if (claimed[j]) continue;
                        double d = Norm3(orig.Vp, j, pos, i);
                        // np.argmin: first minimum, a NaN wins
                        if (bestJ < 0 || (!double.IsNaN(bestD) && (d < bestD || double.IsNaN(d)))) (bestJ, bestD) = (j, d);
                    }
                    if (bestJ >= 0 && bestD <= MoveRadius)
                    {
                        uvmatch[i] = bestJ;
                        claimed[bestJ] = true;
                    }
                }
            }
        }
        var fold = Enumerable.Range(0, n).Select(i => (long)i).ToArray();
        if (dup.Any(x => x >= 0))
        {
            // copies of an already-claimed original (Blender split them by normal): fold them into the claimer
            var owner = new Dictionary<long, int>();
            for (int i = 0; i < n; i++) if (snap[i] >= 0) owner[snap[i]] = i;
            for (int i = 0; i < n; i++)
                if (dup[i] >= 0 && owner.TryGetValue(dup[i], out int o)) fold[i] = o;
        }
        var rest2 = Enumerable.Range(0, n).Where(i => snap[i] < 0 && uvmatch[i] < 0 && dup[i] < 0).ToList();
        if (rest2.Count > 0)
        {
            // split copies of a moved vertex: same edited position + UV as a vertex that was identified
            double cell = Math.Max(tol * 4, 1e-9);
            var g3 = new Dictionary<(long, long, long), List<int>>();
            for (int i2 = 0; i2 < n; i2++)
                if (snap[i2] >= 0 || uvmatch[i2] >= 0) Bucket(g3, Cell(pos, i2, cell), i2);
            foreach (int i in rest2)
                if (g3.TryGetValue(Cell(pos, i, cell), out var list))
                    foreach (int i2 in list)
                        if (MaxAbs3(pos, i2, pos, i) <= tol && Math.Max(Math.Abs(uv0[i2 * 2] - uv0[i * 2]), Math.Abs(uv0[i2 * 2 + 1] - uv0[i * 2 + 1])) <= uvTol)
                        {
                            fold[i] = i2;
                            break;
                        }
        }
        var faces = emFaces;
        bool folded = false;
        var drop = Enumerable.Range(0, n).Select(i => fold[i] != i).ToArray();
        if (drop.Any(x => x))
        {
            var keep = drop.Select(x => !x).ToArray();
            var lut = new long[n];
            long c = 0;
            for (int i = 0; i < n; i++) { if (keep[i]) c++; lut[i] = c - 1; }
            faces = faces.Select(f => lut[fold[f]]).ToArray();
            pos = Take(pos, 3, keep);
            nrm = Take(nrm, 3, keep);
            uv0 = Take(uv0, 2, keep);
            if (skinned)
            {
                bones = Take(bones!, 4, keep);
                weights = Take(weights!, 4, keep);
            }
            if (uv1 is not null && uv1.Length == n * 2) uv1 = Take(uv1, 2, keep);
            snap = Take(snap, keep);
            dup = Take(dup, keep);
            uvmatch = Take(uvmatch, keep);
            emFaces = faces;
            stats["folded_duplicates"] = drop.Count(x => x);
            folded = true;
            n = keep.Count(x => x);
            stats["vertices"] = n;
        }
        var exact = snap.Select(s => s >= 0).ToArray();
        var ofaces = Longs(node.Property("f"))!;
        if (exact.All(x => x) && !(!folded && n == nOrig && SameTriangles(faces.Select(f => snap[f]).ToArray(), ofaces)))
            if (UnchangedGeometry(node, orig, snap, faces, bones, weights, skinned, stats)) return stats;

        long[] ident;
        if (n == nOrig && SameTriangles(faces, ofaces)) ident = Enumerable.Range(0, n).Select(i => (long)i).ToArray();   // same order, same triangles: vertex i is original i
        else ident = CompleteByTopology(snap.Select((s, i) => s >= 0 ? s : uvmatch[i]).ToArray(), faces, ofaces);
        var has = ident.Select(x => x >= 0).ToArray();
        bool reorder = n == nOrig && has.All(x => x) && ident.Distinct().Count() == n && SameTriangles(faces.Select(f => ident[f]).ToArray(), ofaces);
        if (!reorder && n == nOrig && has.All(x => x) && faces.Length == ofaces.Length)
            // exact snaps may have picked the wrong one of several identical originals: re-pick through the triangles
            if (ResolveByTriangles(ident, faces, ofaces, orig) is { } alt) (ident, reorder) = (alt, true);
        var src = (long[])ident.Clone();
        for (int i = 0; i < n; i++)
        {
            if (has[i]) continue;
            // nearest original (by position) for new vertices
            int best = 0;
            double bd = double.NaN;
            for (int j = 0; j < nOrig; j++)
            {
                double d = Norm3(orig.Vp, j, pos, i);
                if (j == 0 || (!double.IsNaN(bd) && (d < bd || double.IsNaN(d)))) (best, bd) = (j, d);
            }
            src[i] = best;
        }
        var eod = new bool[n];
        for (int i = 0; i < n; i++)
        {
            bool isDup = !has[i] && dup[i] >= 0;
            eod[i] = exact[i] || isDup;
            if (isDup) src[i] = dup[i];
        }
        var outPos = new double[n * 3];
        var outNrm = new double[n * 3];
        var outUv0 = new double[n * 2];
        for (int i = 0; i < n; i++)
        {
            long s = src[i];
            for (int c = 0; c < 3; c++)
            {
                outPos[i * 3 + c] = eod[i] ? orig.Vp[s * 3 + c] : pos[i * 3 + c];
                outNrm[i * 3 + c] = eod[i] ? orig.Vn[s * 3 + c] : nrm[i * 3 + c];
            }
            for (int c = 0; c < 2; c++) outUv0[i * 2 + c] = eod[i] ? ou0[s * 2 + c] : uv0[i * 2 + c];
        }
        double[]? outUv1 = null;
        if (orig.U1 is { } ou1)
        {
            var eu1 = uv1 is not null && uv1.Length == n * 2 ? uv1 : null;
            outUv1 = new double[n * 2];
            for (int i = 0; i < n; i++)
                for (int c = 0; c < 2; c++)
                    outUv1[i * 2 + c] = eod[i] || eu1 is null ? ou1[src[i] * 2 + c] : eu1[i * 2 + c];
        }
        double[]? outTan = null;
        if (orig.Vt is { } ovt)
        {
            outTan = new double[n * 3];
            for (int i = 0; i < n; i++)
            {
                double tx = ovt[src[i] * 3], ty = ovt[src[i] * 3 + 1], tz = ovt[src[i] * 3 + 2];
                if (!eod[i])
                {
                    double nx = outNrm[i * 3], ny = outNrm[i * 3 + 1], nz = outNrm[i * 3 + 2];
                    double dot = tx * nx + ty * ny + tz * nz;
                    tx -= nx * dot; ty -= ny * dot; tz -= nz * dot;
                    double len = Math.Max(Math.Sqrt(tx * tx + ty * ty + tz * tz), 1e-12);
                    tx /= len; ty /= len; tz /= len;
                }
                outTan[i * 3] = tx;
                outTan[i * 3 + 1] = ty;
                outTan[i * 3 + 2] = tz;
            }
        }
        if (skinned)
        {
            var same = new bool[n];
            if (orig.Wb is { } ob && orig.Wv is { } ow && has.Any(x => x))
            {
                int omi = orig.Mi;
                for (int i = 0; i < n; i++)
                    if (has[i]) same[i] = SameWeights(ob, ow, (int)src[i], omi, bones!, weights!, i, 4);
                for (int i = 0; i < n; i++)
                    if (same[i])
                        for (int k = 0; k < 4; k++)
                        {
                            bones![i * 4 + k] = ob[src[i] * omi + k];
                            weights![i * 4 + k] = ow[src[i] * omi + k];
                        }
            }
            stats["weights_kept"] = same.Count(x => x);
        }
        stats["snapped"] = exact.Count(x => x);
        stats["moved"] = Enumerable.Range(0, n).Count(i => has[i] && !exact[i]);
        stats["new"] = has.Count(x => !x);
        stats["duplicates"] = Enumerable.Range(0, n).Count(i => !has[i] && dup[i] >= 0);
        stats["in_place"] = reorder;
        long[]? ids = null;
        long[]? facesOut = emFaces;
        if (reorder)
        {
            // same vertices and triangles: back to the template order
            var perm = new int[n];
            for (int i = 0; i < n; i++) perm[ident[i]] = i;
            outPos = Permute(outPos, 3, perm);
            outNrm = Permute(outNrm, 3, perm);
            outUv0 = Permute(outUv0, 2, perm);
            if (outUv1 is not null) outUv1 = Permute(outUv1, 2, perm);
            if (outTan is not null) outTan = Permute(outTan, 3, perm);
            if (skinned)
            {
                bones = Permute(bones!, 4, perm);
                weights = Permute(weights!, 4, perm);
            }
            src = Enumerable.Range(0, n).Select(i => (long)i).ToArray();
            ids = orig.Ids;
            facesOut = null;
        }

        // ---- write into the template node --------------------------------------------------------------------------
        node.SetVertexPositionBuffer(F32(outPos));
        node.SetVertexNormalBuffer(F32(outNrm));
        if (outTan is not null) node.SetVertexTangentBuffer(F32(outTan));
        node.SetVertexUVLayerBuffer(0, F32(outUv0));
        if (outUv1 is not null) node.SetVertexUVLayerBuffer(1, F32(outUv1));
        if (facesOut is not null) node.SetFaceBuffer(U32(facesOut));
        if (skinned)
        {
            node.SetMaximumWeightInfluence(CastExport.MaxInfluences);
            node.SetVertexWeightBoneBuffer(U32(bones!));
            node.SetVertexWeightValueBuffer(F32(weights!));
        }
        if (orig.Sign is { } sign) node.CreateProperty("bp_tangent_sign", CastPropertyType.Float).SetValues(src.Select(s => (float)sign[s]).ToArray());
        if (ids is not null) node.CreateProperty("bp_vertex_id", CastPropertyType.Integer).SetValues(ids.Select(x => checked((uint)x)).ToArray());
        else node.RemoveProperty("bp_vertex_id");
        return stats;
    }

    private static void Bucket<TKey>(Dictionary<TKey, List<int>> grid, TKey key, int value) where TKey : notnull
    {
        if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
        list.Add(value);
    }

    private static float[] F32(double[] a) => Array.ConvertAll(a, x => (float)x);

    private static uint[] U32(long[] a) => Array.ConvertAll(a, x => checked((uint)x));

    private static T[] Permute<T>(T[] a, int width, int[] perm)
    {
        var r = new T[a.Length];
        for (int i = 0; i < perm.Length; i++)
            for (int c = 0; c < width; c++) r[i * width + c] = a[perm[i] * width + c];
        return r;
    }

    /// <summary>Same influence set (bone → weight, positive weights only) within 0.6/255 per bone.</summary>
    private static bool SameWeights(long[] ab, double[] aw, int ai, int ak, long[] cb, double[] cw, int ci, int ck)
    {
        var a = new Dictionary<long, double>();
        for (int k = 0; k < ak; k++) if (aw[ai * ak + k] > 0) a[ab[ai * ak + k]] = aw[ai * ak + k];
        var c = new Dictionary<long, double>();
        for (int k = 0; k < ck; k++) if (cw[ci * ck + k] > 0) c[cb[ci * ck + k]] = cw[ci * ck + k];
        if (a.Count != c.Count || a.Keys.Any(x => !c.ContainsKey(x))) return false;
        return a.All(kv => Math.Abs(kv.Value - c[kv.Key]) <= 0.6 / 255);
    }

    /// <summary><c>_assign_material</c>: point the node at material <paramref name="name"/> (existing by exact / case-insensitive name, else new).</summary>
    private static void AssignMaterial(CastMesh node, string name, CastModel mdl, OrderedDictionary<string, object?> stats)
    {
        var mats = mdl.Materials();
        ulong? hash = node.Property("m") is { ValueCount: > 0 } p && p.Type <= CastPropertyType.Long ? p.IntegerAt(0) : null;
        var cur = mats.FirstOrDefault(m => m.Hash == hash);
        if (cur is not null && (cur.Name() ?? "") == name) return;
        string baseName = StripSuffix(name);
        var hit = mats.FirstOrDefault(m => (m.Name() ?? "") == name || (m.Name() ?? "") == baseName)
                  ?? mats.FirstOrDefault(m => (m.Name() ?? "").ToLowerInvariant() is var l && (l == name.ToLowerInvariant() || l == baseName.ToLowerInvariant()));
        if (hit is null)
        {
            hit = mdl.CreateMaterial();
            hit.SetName(baseName);
            hit.SetType("pbr");
        }
        node.SetMaterial(hit.Hash);
        stats["material"] = hit.Name();
    }

    /// <summary>
    /// Group label per original vertex by (position, UV0 with non-finite → ±9e9): the rank of its row among the
    /// sorted distinct rows, as <c>np.unique(…, return_inverse=True)</c> over a structured view.
    /// </summary>
    private static int[] Groups(Orig orig)
    {
        int n = orig.N;
        static double Fin(double v) => double.IsNaN(v) ? 9e9 : double.IsPositiveInfinity(v) ? 9e9 : double.IsNegativeInfinity(v) ? -9e9 : v;
        var rows = new double[n][];
        for (int i = 0; i < n; i++)
            rows[i] = [orig.Vp[i * 3], orig.Vp[i * 3 + 1], orig.Vp[i * 3 + 2], Fin(orig.U0[i * 2]), Fin(orig.U0[i * 2 + 1])];
        int Cmp(int a, int b)
        {
            for (int k = 0; k < 5; k++)
            {
                int c = NumpyOrder.Instance.Compare(rows[a][k], rows[b][k]);
                if (c != 0) return c;
            }
            return 0;
        }
        var order = Enumerable.Range(0, n).ToArray();
        Array.Sort(order, Cmp);
        var group = new int[n];
        int g = -1;
        for (int r = 0; r < n; r++)
        {
            if (r == 0 || Enumerable.Range(0, 5).Any(k => rows[order[r]][k] != rows[order[r - 1]][k])) g++;
            group[order[r]] = g;
        }
        return group;
    }

    /// <summary>
    /// <c>_unchanged_geometry</c>: every edited vertex sits exactly on an original and the triangles are the original
    /// ones up to vertices sharing position + UV — the template node is kept; only weight edits are carried onto every
    /// original of the edited vertex's group.
    /// </summary>
    private static bool UnchangedGeometry(CastMesh node, Orig orig, long[] snap, long[] faces, long[]? bones, double[]? weights,
                                          bool skinned, OrderedDictionary<string, object?> stats)
    {
        var ofaces = Longs(node.Property("f"))!;
        if (faces.Length != ofaces.Length) return false;
        var group = Groups(orig);
        static List<(int, int, int)> Canon(IEnumerable<int> labels)
        {
            var l = labels.ToArray();
            var rows = new List<(int, int, int)>();
            for (int i = 0; i + 2 < l.Length; i += 3)
            {
                var t = new[] { l[i], l[i + 1], l[i + 2] };
                Array.Sort(t);
                rows.Add((t[0], t[1], t[2]));
            }
            rows.Sort();
            return rows;
        }
        if (!Canon(faces.Select(f => group[snap[f]])).SequenceEqual(Canon(ofaces.Select(f => group[f])))) return false;
        stats["in_place"] = true;
        stats["kept_template"] = true;
        if (skinned && orig.Wb is not null && orig.Wv is not null)
        {
            var wb = (long[])orig.Wb.Clone();
            var wv = (double[])orig.Wv.Clone();
            int mi = orig.Mi;
            int changed = 0;
            var members = new Dictionary<int, List<int>>();
            for (int j = 0; j < group.Length; j++) Bucket(members, group[j], j);
            for (int i = 0; i < snap.Length; i++)
            {
                int j = (int)snap[i];
                // the prototype compares against the arrays it is updating (an earlier edit of the group counts)
                if (SameWeights(wb, wv, j, mi, bones!, weights!, i, 4)) continue;
                foreach (int m in members[group[j]])
                {
                    for (int k = 0; k < mi; k++)
                    {
                        // numpy assigns the 4-lane row into an mi-lane row (mi is 4 in every template)
                        wb[m * mi + k] = bones![i * 4 + k];
                        wv[m * mi + k] = weights![i * 4 + k];
                    }
                    changed++;
                }
            }
            stats["weights_kept"] = snap.Length - changed;
            if (changed > 0)
            {
                node.SetVertexWeightBoneBuffer(U32(wb));
                node.SetVertexWeightValueBuffer(F32(wv));
            }
        }
        stats["snapped"] = snap.Length;
        stats["moved"] = 0;
        stats["new"] = 0;
        return true;
    }

    /// <summary>
    /// <c>_resolve_by_triangles</c>: vertices with identical position + UV0 are interchangeable for snapping; choose among
    /// them so every edited triangle is an original triangle. Null when no consistent choice exists.
    /// </summary>
    private static long[]? ResolveByTriangles(long[] ident, long[] faces, long[] ofaces, Orig orig)
    {
        var group = Groups(orig);
        long[] Canon(long a, long b, long c)
        {
            var t = new[] { a, b, c };
            int k = 0;
            for (int r = 1; r < 3; r++) if (group[t[r]] < group[t[k]]) k = r;
            return [t[k], t[(k + 1) % 3], t[(k + 2) % 3]];
        }
        var pool = new Dictionary<(int, int, int), List<long[]>>();
        for (int i = 0; i + 2 < ofaces.Length; i += 3)
        {
            var c = Canon(ofaces[i], ofaces[i + 1], ofaces[i + 2]);
            Bucket2(pool, (group[c[0]], group[c[1]], group[c[2]]), c);
        }
        var outIds = Enumerable.Repeat(-1L, ident.Length).ToArray();
        var used = new bool[orig.N];
        for (int i = 0; i + 2 < faces.Length; i += 3)
        {
            long[] t = [faces[i], faces[i + 1], faces[i + 2]];
            int[] g = [group[ident[t[0]]], group[ident[t[1]]], group[ident[t[2]]]];
            int k = 0;
            for (int r = 1; r < 3; r++) if (g[r] < g[k]) k = r;
            long[] nt = [t[k], t[(k + 1) % 3], t[(k + 2) % 3]];
            if (!pool.TryGetValue((g[k], g[(k + 1) % 3], g[(k + 2) % 3]), out var cands) || cands.Count == 0) return null;
            int pick = cands.FindIndex(c => Enumerable.Range(0, 3).All(r =>
                (outIds[nt[r]] == -1 || outIds[nt[r]] == c[r]) && (outIds[nt[r]] == c[r] || !used[c[r]])));
            if (pick < 0) return null;
            var chosen = cands[pick];
            cands.RemoveAt(pick);
            for (int r = 0; r < 3; r++)
            {
                outIds[nt[r]] = chosen[r];
                used[chosen[r]] = true;
            }
        }
        if (outIds.Any(x => x < 0) || outIds.Distinct().Count() != outIds.Length) return null;
        return outIds;
    }

    private static void Bucket2<TKey, TValue>(Dictionary<TKey, List<TValue>> d, TKey key, TValue value) where TKey : notnull
    {
        if (!d.TryGetValue(key, out var list)) d[key] = list = [];
        list.Add(value);
    }

    /// <summary>
    /// <c>_complete_by_topology</c>: extend exact matches through the triangles — a triangle with two identified corners
    /// whose directed edge exists in the original takes the original third corner. An original claimed twice keeps only
    /// its exact claims.
    /// </summary>
    private static long[] CompleteByTopology(long[] snap, long[] faces, long[] ofaces)
    {
        var ident = (long[])snap.Clone();
        var edge = new Dictionary<(long, long), long>();
        for (int i = 0; i + 2 < ofaces.Length; i += 3)
        {
            long a = ofaces[i], b = ofaces[i + 1], c = ofaces[i + 2];
            edge[(a, b)] = c;
            edge[(b, c)] = a;
            edge[(c, a)] = b;
        }
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i + 2 < faces.Length; i += 3)
            {
                long[] ids = [ident[faces[i]], ident[faces[i + 1]], ident[faces[i + 2]]];
                if (ids.Count(x => x == -1) != 1) continue;
                int k = Array.IndexOf(ids, -1L);
                if (edge.TryGetValue((ids[(k + 1) % 3], ids[(k + 2) % 3]), out long c))
                {
                    ident[faces[i + k]] = c;
                    changed = true;
                }
            }
        }
        var dupIds = ident.Where(x => x >= 0).GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        if (dupIds.Count > 0)
            for (int i = 0; i < ident.Length; i++)
                if (ident[i] >= 0 && dupIds.Contains(ident[i]) && snap[i] < 0) ident[i] = -1;
        return ident;
    }

    /// <summary><c>_same_triangles</c>: same triangle list up to each triangle's rotation (in order, or as sorted lists).</summary>
    private static bool SameTriangles(long[] a, long[] b)
    {
        if (a.Length != b.Length || a.Length % 3 != 0) return a.Length == b.Length && a.SequenceEqual(b);
        static List<(long, long, long)> Canon(long[] t)
        {
            var r = new List<(long, long, long)>(t.Length / 3);
            for (int i = 0; i < t.Length; i += 3)
            {
                int k = 0;
                for (int j = 1; j < 3; j++) if (t[i + j] < t[i + k]) k = j;
                r.Add((t[i + k], t[i + (k + 1) % 3], t[i + (k + 2) % 3]));
            }
            return r;
        }
        var ca = Canon(a);
        var cb = Canon(b);
        if (ca.SequenceEqual(cb)) return true;
        ca.Sort();
        cb.Sort();
        return ca.SequenceEqual(cb);
    }
}
