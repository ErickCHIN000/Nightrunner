using Nightrunner.Core.Mesh;
using Nightrunner.Core.Rpack;
using Nightrunner.Core.Sdb;
using Nightrunner.Core.Texture;
using Nightrunner.UI.Viewport;

namespace Nightrunner.UI;

/// <summary>
/// A decoded mesh → what the viewport draws: one part per submesh of the chosen LOD, positions as stored (no entity
/// transform — the same as the prototype's viewer), plus each material's albedo and normal map.
/// </summary>
/// <remarks>
/// Textures come from the SDB: a mesh material names an SDB material (<see cref="ViewerSurface.Resolve"/>), whose
/// bindings go through the preview recipes (<see cref="MaterialPreview"/>): the plain <c>dif_0_tex</c> × <c>dif_0_val</c>,
/// or a composed image (eyes, hair, opacity, gradient maps). Albedo goes to the GPU as a DDS of the stored blocks.
/// Normal maps are decoded on the CPU with Z rebuilt: the viewer's shader samples a normal map as unsigned XYZ, and the
/// stored BC5_SNORM / BC4 maps are signed two-channel data. That is a limit of the renderer, not of the texture.
/// A surface without colour stays grey; why is in its <see cref="SceneMaterial.Notes"/>, which the viewport logs on
/// every load (cached or not).
/// </remarks>
public static class MeshScenes
{
    public const string AlbedoParameter = ViewerSurface.AlbedoParameter, NormalParameter = ViewerSurface.NormalParameter;
    public const float ViewerHairCutoff = 0.08f;

    public sealed record Resolved(SdbMaterial? Material, string? Problem);

    /// <summary>The SDB material a mesh material name resolves to (<see cref="ViewerSurface.Resolve"/>).</summary>
    public static Resolved Resolve(SdbFile? sdb, string materialName)
    {
        var (m, problem) = ViewerSurface.Resolve(sdb, materialName);
        return new(m, problem);
    }

    /// <summary>
    /// A texture for the GPU. Albedo: the stored blocks as a DDS when the GPU can sample the format, else the
    /// decoded preview level. Normal: always decoded with Z rebuilt (see the remarks). Null with a note when neither works.
    /// </summary>
    public static SceneTexture? Load(RpackCatalog catalog, string name, bool normal, List<string> notes)
    {
        if (ViewerSurface.Open(catalog, name, normal, out var tex) is { } why)
        {
            notes.Add($"{name}: {why}");
            return null;
        }
        if (!normal && ViewerSurface.GpuSamples(tex!.Header)) return new SceneTexture(name, DdsWriter.ToDds(tex), null, 0, 0);
        var img = tex!.Decode(ViewerSurface.PreviewLevel(tex) ?? tex.TopLevel!.Value, rebuildNormalZ: normal);
        return new SceneTexture(name, null, img.Bgra, img.Width, img.Height);
    }

    /// <summary>
    /// Surfaces for every slot the parts use, under a skin: <paramref name="map"/> (from
    /// <see cref="MeshSkins.MaterialsFor"/>) turns a slot into a full-table material, whose name resolves through the
    /// SDB. Surfaces already built (same name) come from <paramref name="cache"/>; materials load in parallel. Each
    /// surface's notes go to <paramref name="notes"/>, prefixed by the material name.
    /// </summary>
    public static Dictionary<int, SceneMaterial> Materials(
        MeshModel m, IEnumerable<int> slots, int[] map, SdbFile? sdb, RpackCatalog catalog, List<string> notes,
        Nightrunner.Core.ByteBudgetCache<string, SceneMaterial> cache,
        Nightrunner.Core.ByteBudgetCache<string, SceneTexture?> textures)
    {
        var table = m.FullMaterialTable();
        var byName = slots.Distinct().Where(s => s < map.Length && map[s] < table.Length)
            .ToDictionary(s => s, s => table[map[s]]);
        var built = new System.Collections.Concurrent.ConcurrentDictionary<string, SceneMaterial>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(byName.Values.Distinct(StringComparer.OrdinalIgnoreCase), name =>
        {
            var r = Resolve(sdb, name);
            built[name] = Surface(r.Material, name, null, catalog, cache, textures, r.Problem);
        });
        foreach (var name in byName.Values.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            notes.AddRange(built[name].Notes.Select(n => $"{name}: {n}"));
        return byName.ToDictionary(kv => kv.Key, kv => built[kv.Value]);
    }

    private static string Key(string material, IReadOnlyDictionary<string, string>? overrides) =>
        overrides is null || overrides.Count == 0
            ? material.ToLowerInvariant()
            : material.ToLowerInvariant() + "#" + string.Join(";", overrides.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>
    /// The surface of one material (with a <c>.model</c>'s texture overrides): the preview recipes
    /// (<see cref="MaterialPreview"/>) — eyes composed from their layers, hair alpha-tested, eye shadow / wet eye /
    /// forearm hair blended, gradient maps looked up, non-rendering materials hidden — else the plain <c>dif_0_tex</c>
    /// sampled as stored and tinted by <c>dif_0_val</c>. Built once per session and material (<paramref name="cache"/>);
    /// textures are shared (<paramref name="textures"/>). Why a surface has no colour, or what it leaves out, is kept
    /// in its <see cref="SceneMaterial.Notes"/> (the SDB lookup problem included) for the caller to report.
    /// </summary>
    public static SceneMaterial Surface(SdbMaterial? material, string name, IReadOnlyDictionary<string, string>? overrides,
                                        RpackCatalog catalog,
                                        Nightrunner.Core.ByteBudgetCache<string, SceneMaterial> cache,
                                        Nightrunner.Core.ByteBudgetCache<string, SceneTexture?> textures,
                                        string? problem = null)
    {
        string key = Key(material?.Name ?? name, overrides);
        if (cache.TryGetValue(key, out var hit)) return hit;
        var mine = new List<string>();
        var failures = FailuresBy.GetValue(catalog, _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        if (material is null) mine.Add(problem ?? "not in SDB");
        var layers = new Dictionary<string, DecodedImage?>(StringComparer.OrdinalIgnoreCase);
        var surface = MaterialPreview.Compose(material, overrides, t =>
        {
            if (!layers.TryGetValue(t, out var img)) layers[t] = img = Decode(catalog, t, mine);
            return img;
        });
        mine.AddRange(surface.Warnings.Where(w => material is not null || w != "material not in SDB"));
        SceneTexture? Get(string? tex, bool normal)
        {
            if (tex is null) return null;
            // A texture that failed is cached as null; its reason is kept per key so every surface using it reports it.
            var t = textures.GetOrAdd((normal ? "n:" : "a:") + tex, _ =>
            {
                var l = new List<string>();
                var loaded = Load(catalog, tex, normal, l);
                if (l.Count > 0) lock (failures) failures[(normal ? "n:" : "a:") + tex] = l[0];
                return loaded;
            });
            if (t is null)
                lock (failures) mine.Add(failures.GetValueOrDefault((normal ? "n:" : "a:") + tex) ?? $"{tex}: not loaded");
            return t;
        }
        SceneTexture? albedo;
        if (surface.Image is { } img)
        {
            if (surface.Alpha == AlphaMode.Mask)
            {
                // Dithered hair: the game dithers coverage per pixel and TAA accumulates the overlapping cards, so a
                // strand shows wherever there is some coverage. The prototype's hard 0.25 cutoff drops most of a brow
                // (94% of its card area is below it); the viewer alpha-tests at ViewerHairCutoff instead.
                byte cut = (byte)Math.Round(ViewerHairCutoff * 255);
                for (int i = 3; i < img.Bgra.Length; i += 4) img.Bgra[i] = img.Bgra[i] >= cut ? (byte)255 : (byte)0;
            }
            albedo = new SceneTexture($"{name} ({surface.Recipe})", null, img.Bgra, img.Width, img.Height);
        }
        else albedo = Get(surface.PlainTexture, false);
        var result = new SceneMaterial(albedo, Get(surface.NormalTexture, true), surface.Alpha, surface.Hidden)
        {
            Tint = albedo is null ? null : surface.Tint,
            Notes = [.. mine.Distinct()],
        };
        return cache.GetOrAdd(key, result);
    }

    /// <summary>Why a texture cached as missing could not be loaded, per catalog (a few hundred names at most).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RpackCatalog, Dictionary<string, string>> FailuresBy = new();

    /// <summary>A texture layer for composing: its largest surface no wider than 2048, decoded to BGRA.</summary>
    private static DecodedImage? Decode(RpackCatalog catalog, string name, List<string> notes)
    {
        if (ViewerSurface.Open(catalog, name, normal: true, out var tex) is { } why)   // "normal": the decoder must take it
        {
            notes.Add($"{name}: {why}");
            return null;
        }
        try
        {
            return tex!.Decode(ViewerSurface.PreviewLevel(tex) ?? tex.TopLevel!.Value, rebuildNormalZ: false);
        }
        catch (Exception e) when (e is ImgcException or RpackFormatException)
        {
            notes.Add($"{name}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// A mesh's skin against <paramref name="skeleton"/>: each entity's bone (by name) and inverse bind (fallback: the
    /// inverse of its composed global when the stored one is singular or non-finite, as the prototype does).
    /// </summary>
    public static SceneSkin Skin(MeshModel m, Nightrunner.Core.Model.ModelSkeleton skeleton)
    {
        var map = skeleton.Map(m);
        var g = m.EntityGlobals();
        var inv = new double[m.Entities.Length][];
        foreach (var e in m.Entities)
        {
            var ib = new double[16];
            for (int i = 0; i < 12; i++) ib[i] = e.InvBind[i];
            ib[15] = 1;
            double det = ib[0] * (ib[5] * ib[10] - ib[6] * ib[9]) - ib[1] * (ib[4] * ib[10] - ib[6] * ib[8]) + ib[2] * (ib[4] * ib[9] - ib[5] * ib[8]);
            inv[e.Index] = ib.All(double.IsFinite) && Math.Abs(det) >= 1e-8 ? ib : Nightrunner.Core.Cast.ModelCast.Invert(g[e.Index]);
        }
        return new SceneSkin(map, inv);
    }

    /// <summary>A skeleton for the Bones overlay: names, parents and rest positions.</summary>
    public static SceneSkeleton Skeleton(Nightrunner.Core.Model.ModelSkeleton s) => Skeleton(s, s.Rest);

    /// <summary>The overlay skeleton at given bone globals (a pose).</summary>
    public static SceneSkeleton Skeleton(Nightrunner.Core.Model.ModelSkeleton s, IReadOnlyList<double[]> globals) => new(
        [.. s.Names], [.. s.Parents], globals.SelectMany(m => new[] { (float)m[3], (float)m[7], (float)m[11] }).ToArray());

    /// <summary>A part moved by a 4×4 row-major transform (column vectors); its frame follows the 3×3, renormalised. Skin data is dropped.</summary>
    public static ScenePart Transformed(ScenePart p, double[] m)
    {
        static float[] Map(float[] v, double[] m, bool translate, bool normalise)
        {
            var r = new float[v.Length];
            for (int i = 0; i + 2 < v.Length; i += 3)
            {
                double x = v[i], y = v[i + 1], z = v[i + 2];
                double rx = m[0] * x + m[1] * y + m[2] * z, ry = m[4] * x + m[5] * y + m[6] * z, rz = m[8] * x + m[9] * y + m[10] * z;
                if (translate) { rx += m[3]; ry += m[7]; rz += m[11]; }
                if (normalise)
                {
                    double len = Math.Sqrt(rx * rx + ry * ry + rz * rz);
                    if (len > 1e-12) { rx /= len; ry /= len; rz /= len; }
                }
                r[i] = (float)rx; r[i + 1] = (float)ry; r[i + 2] = (float)rz;
            }
            return r;
        }
        return p with
        {
            Positions = Map(p.Positions, m, true, false), Normals = Map(p.Normals, m, false, true),
            Tangents = Map(p.Tangents, m, false, true), Bitangents = Map(p.Bitangents, m, false, true),
            Joints = null, Weights = null,
        };
    }

    /// <summary>One part per submesh of entries at <paramref name="lod"/>, each with its own compacted vertex set.</summary>
    /// <remarks>
    /// Unskinned geometry is stored in its owner entity's space and drawn at that entity's global (as the engine and
    /// <c>ModelCast</c> place it): a door modelled along +X under a child entity rotated to −Z, a hood under its hinge.
    /// The prototype's viewer draws raw positions (DLTB 642, DL2 1,015 meshes differ; see docs/porting.md, Divergences).
    /// </remarks>
    public static List<ScenePart> Parts(MeshModel m, int lod = 0, int mesh = 0)
    {
        var parts = new List<ScenePart>();
        double[][]? globals = null;
        foreach (var e in m.GeometryEntries)
        {
            if (e.Element != lod || e.Vertices is not { } v) continue;
            double[]? owner = null;
            if (!v.Skinned && e.OwnerEntity is int o && o >= 0 && o < m.Entities.Length)
            {
                globals ??= m.EntityGlobals();
                if (!IsIdentity(globals[o])) owner = globals[o];
            }
            foreach (var s in e.Submeshes)
            {
                if (s.Indices.Length < 3) continue;
                var map = new Dictionary<int, int>();
                var idx = new int[s.Indices.Length - s.Indices.Length % 3];
                for (int i = 0; i < idx.Length; i++)
                {
                    int src = s.Indices[i];
                    if (src >= v.Count) { idx = []; break; }       // decoder already warned; draw nothing rather than garbage
                    if (!map.TryGetValue(src, out int dst)) map[src] = dst = map.Count;
                    idx[i] = dst;
                }
                if (idx.Length == 0) continue;
                int n = map.Count;
                float[] pos = new float[n * 3], nrm = new float[n * 3], tan = new float[n * 3], bit = new float[n * 3], uv = new float[n * 2];
                bool skinned = v.Skinned && s.Palette.Length > 0;
                int[]? joints = skinned ? new int[n * 4] : null;
                float[]? weights = skinned ? new float[n * 4] : null;
                foreach (var (src, dst) in map)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        pos[dst * 3 + k] = v.Positions[src * 3 + k];
                        nrm[dst * 3 + k] = v.Normals[src * 3 + k];
                        tan[dst * 3 + k] = v.Tangents[src * 3 + k];
                    }
                    float sgn = v.TangentSign[src];
                    float nx = nrm[dst * 3], ny = nrm[dst * 3 + 1], nz = nrm[dst * 3 + 2];
                    float tx = tan[dst * 3], ty = tan[dst * 3 + 1], tz = tan[dst * 3 + 2];
                    bit[dst * 3] = sgn * (ny * tz - nz * ty);
                    bit[dst * 3 + 1] = sgn * (nz * tx - nx * tz);
                    bit[dst * 3 + 2] = sgn * (nx * ty - ny * tx);
                    uv[dst * 2] = v.Uv0[src * 2];
                    uv[dst * 2 + 1] = v.Uv0[src * 2 + 1];
                    if (skinned)
                        for (int k = 0; k < 4; k++)
                        {
                            float w = v.Weights![src * 4 + k];
                            int j = v.Joints![src * 4 + k];
                            if (w <= 0 || j >= s.Palette.Length) continue;
                            joints![dst * 4 + k] = s.Palette[j];
                            weights![dst * 4 + k] = w;
                        }
                }
                if (owner is not null)
                {
                    Apply(owner, pos, true);
                    Apply(owner, nrm, false);
                    Apply(owner, tan, false);
                    Apply(owner, bit, false);
                }
                parts.Add(new ScenePart(mesh, e.Index, s.Index, s.MaterialSlot, pos, nrm, tan, bit, uv, idx) { Joints = joints, Weights = weights });
            }
        }
        return parts;
    }

    private static bool IsIdentity(double[] g)
    {
        for (int k = 0; k < 16; k++)
            if (Math.Abs(g[k] - (k % 5 == 0 ? 1 : 0)) > 1e-9) return false;
        return true;
    }

    /// <summary>Row-major 4×4 on xyz triples: points take the translation; directions are rotated and renormalised.</summary>
    private static void Apply(double[] g, float[] xyz, bool point)
    {
        for (int i = 0; i + 2 < xyz.Length; i += 3)
        {
            double x = xyz[i], y = xyz[i + 1], z = xyz[i + 2];
            double nx = g[0] * x + g[1] * y + g[2] * z, ny = g[4] * x + g[5] * y + g[6] * z, nz = g[8] * x + g[9] * y + g[10] * z;
            if (point) { nx += g[3]; ny += g[7]; nz += g[11]; }
            else if (Math.Sqrt(nx * nx + ny * ny + nz * nz) is > 1e-12 and var len) { nx /= len; ny /= len; nz /= len; }
            xyz[i] = (float)nx; xyz[i + 1] = (float)ny; xyz[i + 2] = (float)nz;
        }
    }
}
