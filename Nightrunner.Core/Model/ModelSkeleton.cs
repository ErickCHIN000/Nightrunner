using Nightrunner.Core.Mesh;

namespace Nightrunner.Core.Model;

/// <summary>A bone whose rest pose came from a skinned part instead of the skeleton, and by how much it differed.</summary>
public sealed record BoneOverride(string Bone, string Part, double Millimetres, double Degrees);

/// <summary>
/// The bones of an assembled model and the rest pose they are drawn and exported in. Port of
/// <c>cast/assemble.py</c> <c>MergedSkeleton</c> with the rest-pose rule corrected (see docs/porting.md, Divergences).
/// </summary>
/// <remarks>
/// Bones: the skeleton mesh's first, then every part bone it lacks, matched by name (ASCII case-insensitive), parents
/// by name. Rest pose: the skeleton's. <b>Override:</b> for any bone where a skinned part's own bind — the inverse of
/// its stored inverse bind — differs from that rest by more than <see cref="PositionTolerance"/> or
/// <see cref="AngleTolerance"/>, a part supplies the rest pose instead: of the parts using the bone, the one with the most
/// skin weight on off-skeleton bones overall (the rig owner), then the most weight on the bone. Ranking by weight on
/// the single bone alone let a beard (68.5 vs 5.6 on a lip corner) move Crane's head by 1.1 mm. The
/// prototype took every rest pose from the first supplier (the preset skeleton), whose face bones are a generic
/// template, and baked the difference into the vertices — heads (e.g. <c>sh2_npc_crane</c>, bound to their own face
/// rig) came out deformed. With the override, a part's vertices already sit at the rest pose of the bones they use.
/// </remarks>
public sealed class ModelSkeleton
{
    public const double PositionTolerance = 0.0005;   // metres
    public const double AngleTolerance = 0.5;         // degrees

    public List<string> Names { get; } = [];
    public List<int> Parents { get; } = [];
    /// <summary>4×4 row-major rest globals.</summary>
    public List<double[]> Rest { get; } = [];
    /// <summary>Who supplied each bone's rest pose.</summary>
    public List<string> Source { get; } = [];
    public List<BoneOverride> Overrides { get; } = [];

    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);

    public int IndexOf(string name) => _index.TryGetValue(name, out int i) ? i : -1;

    /// <summary>One mesh's own bone table at its composed globals (what a single-mesh export writes).</summary>
    public static ModelSkeleton FromMesh(MeshModel mesh, string label)
    {
        var s = new ModelSkeleton();
        s.Add(mesh, label);
        return s;
    }

    /// <summary>
    /// Merge a skeleton (may be null) and the parts drawn with it, then apply the rest-pose override rule.
    /// <paramref name="partRigs"/> false skips the override: every rest pose from the bone's first supplier, the
    /// prototype's <c>MergedSkeleton</c> (what a <c>nightrunner.model_cast/1</c> file was baked with).
    /// </summary>
    public static ModelSkeleton Merge(MeshModel? skeleton, string skeletonLabel,
                                      IReadOnlyList<(string Label, MeshModel Mesh)> parts, bool partRigs = true)
    {
        var s = new ModelSkeleton();
        if (skeleton is not null) s.Add(skeleton, skeletonLabel);
        foreach (var (label, mesh) in parts) s.Add(mesh, label);
        if (!partRigs) return s;

        // Each part's binds, and its total skin weight on bones whose bind is off the skeleton's rest (its "own rig").
        var candidates = new Dictionary<int, List<(int Part, double Weight, double[] Bind)>>();
        var rigWeight = new double[parts.Count];
        for (int p = 0; p < parts.Count; p++)
        {
            var mesh = parts[p].Mesh;
            foreach (var (entity, w) in SkinWeights(mesh))
            {
                var e = mesh.Entities[entity];
                int bone = s.IndexOf(MeshModel.Text(e.Name));
                if (bone < 0 || w <= 0 || Inverse34(e.InvBind) is not { } bind) continue;
                var (mm, deg) = Difference(s.Rest[bone], bind);
                if (mm <= PositionTolerance * 1000 && deg <= AngleTolerance) continue;
                rigWeight[p] += w;
                if (!candidates.TryGetValue(bone, out var list)) candidates[bone] = list = [];
                list.Add((p, w, bind));
            }
        }
        // A bone off the skeleton takes the bind of the part with the most weight on it — ranked first by that part's
        // total off-skeleton weight, so the part that owns the rig (the head over its beard and eyebrows, which carry
        // more weight on a few lip bones) supplies every bone it uses.
        foreach (var (bone, list) in candidates.OrderBy(kv => kv.Key))
        {
            var pick = list.OrderByDescending(c => rigWeight[c.Part]).ThenByDescending(c => c.Weight).First();
            var (mm, deg) = Difference(s.Rest[bone], pick.Bind);
            s.Rest[bone] = pick.Bind;
            s.Source[bone] = parts[pick.Part].Label;
            s.Overrides.Add(new BoneOverride(s.Names[bone], parts[pick.Part].Label, mm, deg));
        }
        return s;
    }

    /// <summary>
    /// How far (mm) the part's LOD 0 skinned vertices move when skinned with this rest pose instead of their own bind
    /// (Σ w · Rest[bone] · InvBind): what the rest-pose rule keeps near zero for the rig owner.
    /// </summary>
    public double MaxShift(MeshModel mesh)
    {
        var map = Map(mesh);
        double worst = 0;
        foreach (var e in mesh.GeometryEntries)
        {
            if (e.Element != 0 || e.Vertices is not { Skinned: true } v) continue;
            foreach (var sm in e.Submeshes)
                foreach (var idx in sm.Indices.Distinct())
                {
                    double x = v.Positions[idx * 3], y = v.Positions[idx * 3 + 1], z = v.Positions[idx * 3 + 2], ox = 0, oy = 0, oz = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        double w = v.Weights![idx * 4 + k];
                        if (w <= 0 || v.Joints![idx * 4 + k] >= sm.Palette.Length) continue;
                        int ent = sm.Palette[v.Joints[idx * 4 + k]];
                        if (map[ent] < 0) continue;
                        double[] r = Rest[map[ent]];
                        float[] ib = mesh.Entities[ent].InvBind;
                        // (r · ib) applied to the vertex; ib is 3×4 row-major
                        double bx = ib[0] * x + ib[1] * y + ib[2] * z + ib[3];
                        double by = ib[4] * x + ib[5] * y + ib[6] * z + ib[7];
                        double bz = ib[8] * x + ib[9] * y + ib[10] * z + ib[11];
                        ox += w * (r[0] * bx + r[1] * by + r[2] * bz + r[3]);
                        oy += w * (r[4] * bx + r[5] * by + r[6] * bz + r[7]);
                        oz += w * (r[8] * bx + r[9] * by + r[10] * bz + r[11]);
                    }
                    worst = Math.Max(worst, Math.Sqrt((ox - x) * (ox - x) + (oy - y) * (oy - y) + (oz - z) * (oz - z)));
                }
        }
        return worst * 1000;
    }

    /// <summary>Part entity index → merged bone index.</summary>
    public int[] Map(MeshModel mesh) => mesh.Entities.Select(e => IndexOf(MeshModel.Text(e.Name))).ToArray();

    private void Add(MeshModel mesh, string label)
    {
        var g = mesh.EntityGlobals();
        foreach (var e in mesh.Entities) AddBone(mesh, e.Index, g, label);
    }

    private int AddBone(MeshModel mesh, int entity, double[][] globals, string label)
    {
        var e = mesh.Entities[entity];
        string name = MeshModel.Text(e.Name);
        if (_index.TryGetValue(name, out int have)) return have;
        int parent = e.Parent >= 0 ? AddBone(mesh, e.Parent, globals, label) : -1;
        int i = Names.Count;
        _index[name] = i;
        Names.Add(name);
        Parents.Add(parent);
        Rest.Add(globals[entity]);
        Source.Add(label);
        return i;
    }

    /// <summary>
    /// Σ weight per entity over the skinned LOD 0 vertices each submesh draws (raw/255, palette-mapped). LOD 0 only:
    /// counting every LOD would let a part with more LODs out-vote one with fewer.
    /// </summary>
    public static Dictionary<int, double> SkinWeights(MeshModel mesh)
    {
        var sum = new Dictionary<int, double>();
        foreach (var e in mesh.GeometryEntries)
        {
            if (e.Element != 0 || e.Vertices is not { Skinned: true } v) continue;
            foreach (var s in e.Submeshes)
            {
                var seen = new HashSet<ushort>();
                foreach (var idx in s.Indices)
                {
                    if (idx >= v.Count || !seen.Add(idx)) continue;
                    for (int k = 0; k < 4; k++)
                    {
                        float w = v.Weights![idx * 4 + k];
                        int j = v.Joints![idx * 4 + k];
                        if (w <= 0 || j >= s.Palette.Length) continue;
                        int ent = s.Palette[j];
                        sum[ent] = sum.GetValueOrDefault(ent) + w;
                    }
                }
            }
        }
        return sum;
    }

    /// <summary>Inverse of a 3×4 affine (row-major, last column translation) as a 4×4; null when singular or non-finite.</summary>
    public static double[]? Inverse34(float[] m)
    {
        if (m.Any(x => !float.IsFinite(x))) return null;
        double a = m[0], b = m[1], c = m[2], d = m[4], e = m[5], f = m[6], g = m[8], h = m[9], i = m[10];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (Math.Abs(det) < 1e-8) return null;
        double[] r =
        [
            (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det, 0,
            (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det, 0,
            (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det, 0,
            0, 0, 0, 1,
        ];
        double tx = m[3], ty = m[7], tz = m[11];
        r[3] = -(r[0] * tx + r[1] * ty + r[2] * tz);
        r[7] = -(r[4] * tx + r[5] * ty + r[6] * tz);
        r[11] = -(r[8] * tx + r[9] * ty + r[10] * tz);
        return r;
    }

    /// <summary>(position difference in mm, rotation difference in degrees) between two 4×4 frames.</summary>
    public static (double Millimetres, double Degrees) Difference(double[] a, double[] b)
    {
        double dx = a[3] - b[3], dy = a[7] - b[7], dz = a[11] - b[11];
        double mm = Math.Sqrt(dx * dx + dy * dy + dz * dz) * 1000;
        // compare column-normalised rotations: trace(Ra^T Rb) = 1 + 2 cos θ
        double[] ra = Normalised(a), rb = Normalised(b);
        double trace = 0;
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++) trace += ra[r * 3 + c] * rb[r * 3 + c];
        double cos = Math.Clamp((trace - 1) / 2, -1, 1);
        return (mm, Math.Acos(cos) * 180 / Math.PI);
    }

    private static double[] Normalised(double[] m)
    {
        var r = new double[9];
        for (int c = 0; c < 3; c++)
        {
            double len = Math.Sqrt(m[c] * m[c] + m[4 + c] * m[4 + c] + m[8 + c] * m[8 + c]);
            if (len < 1e-12) len = 1;
            for (int row = 0; row < 3; row++) r[row * 3 + c] = m[row * 4 + c] / len;
        }
        return r;
    }
}
