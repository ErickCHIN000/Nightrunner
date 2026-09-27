using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;
using Nightrunner.Core.Texture;
using CastMaterial = Nightrunner.Core.Cast.Material;
using CastMesh = Nightrunner.Core.Cast.Mesh;
using CastModel = Nightrunner.Core.Cast.Model;

namespace Nightrunner.Core.Cast;

/// <summary>
/// One-file export of a resolved <c>.model</c> (port of <c>gui/modelcast.py</c>): a single Cast holding the merged
/// skeleton, every slot's drawn mesh (LOD 0) skinned to it, and materials whose file slots point at PNGs next to it.
/// <code>
/// &lt;out&gt;/&lt;model&gt;.cast
/// &lt;out&gt;/textures/&lt;material&gt;_albedo.png   the material's dif_0_tex (after model overrides)
/// &lt;out&gt;/textures/&lt;texture&gt;.png          its normal map, Z rebuilt for two-channel maps
/// &lt;out&gt;/&lt;model&gt;.cast.json              what went where: skeleton, rest overrides, parts, materials, problems
/// </code>
/// Differences from the prototype (docs/porting.md, Divergences): the rest pose is <see cref="ModelSkeleton.Merge"/>'s
/// (a part's own rig supplies the bones it owns), not the preset skeleton's for every bone, so heads keep their shape;
/// the drawn (first) entry of a slot is exported, not the <c>selected</c> one. Albedos follow the prototype's
/// material-aware preview (<see cref="Nightrunner.Core.Sdb.MaterialPreview"/>) when an SDB is given. The report format is
/// <see cref="Format"/> (<c>/2</c>): a <c>/1</c> file was baked with the old rest rule; the splitter undoes each file with its own rule.
/// </summary>
public static class ModelCast
{
    public const string Format = "nightrunner.model_cast/2";
    public const int TextureDim = 2048;
    private static readonly string[] SkipPrefixes = ["auto_shadow_caster", "shadowcaster", "shadow_caster", "null"];

    public sealed record Result(string CastPath, JsonObject Report, CastFile Cast);

    private sealed record Part(ResolvedSlot Slot, ResolvedMesh Mesh, MeshModel Model, string Label, int Gid);

    private sealed record Sub(string Name, string MaterialKey, float[] Pos, float[] Nrm, float[] Tan, float[] Uv0,
                              float[]? Uv1, uint[] Faces, uint[] Bones, float[] Weights, string Slot, string Source,
                              int Entry, int Submesh, int MaterialSlot, uint[] VertexIds);

    private sealed record Mat(string Base, SortedDictionary<string, string> Override, IReadOnlyList<ResolvedTexture> Textures);

    /// <summary>Safe file/node name: letters, digits and <c>._-</c>; anything else <c>_</c>; truncated.</summary>
    public static string Safe(string? name, int limit = 60)
    {
        var s = new string((string.IsNullOrEmpty(name) ? "_" : name).Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray());
        return s.Length > limit ? s[..limit] : s.Length == 0 ? "_" : s;
    }

    /// <summary>Write <c>&lt;outDir&gt;/&lt;stem&gt;.cast</c>, its textures and report. The caller writes other formats from <see cref="Result.Cast"/>.</summary>
    public static Result Write(ResolvedModel res, RpackCatalog catalog, Func<int, MeshModel> decode, string outDir, string stem,
                               bool textures = true, Action<string>? progress = null, CancellationToken ct = default,
                               Nightrunner.Core.Sdb.SdbFile? sdb = null)
    {
        Directory.CreateDirectory(outDir);
        var problems = new List<string>();

        // ---- parts: every slot's drawn entry --------------------------------------------------------------------
        var parts = new List<Part>();
        foreach (var slot in res.Slots)
            foreach (var m in slot.Meshes.Where(m => m.Drawn))
            {
                if (!m.Found) { problems.Add($"{slot.Slot.Name}: mesh {m.Entry.Name} missing"); continue; }
                if (m.Error is not null) { problems.Add($"{m.Entry.Name}: {m.Error}"); continue; }
                ct.ThrowIfCancellationRequested();
                progress?.Invoke($"decode {m.Entry.Name}");
                parts.Add(new Part(slot, m, decode(m.Gids[0]), catalog.Split(m.Gids[0]).Entry.Label, m.Gids[0]));
            }
        if (parts.Count == 0) throw new ModelFormatException($"{res.Name}: no mesh of this model is in the loaded packs");

        MeshModel? skeletonMesh = null;
        string skeletonLabel = res.Skeleton ?? "skeleton";
        var skeletonInfo = new JsonObject();
        if (res.SkeletonGids.Length > 0)
        {
            skeletonMesh = decode(res.SkeletonGids[0]);
            skeletonInfo["name"] = res.Skeleton;
            var (skEntry, skIndex) = catalog.Split(res.SkeletonGids[0]);
            skeletonInfo["pack"] = skEntry.Label;
            skeletonInfo["index"] = skIndex;
        }
        else
        {
            if (res.Skeleton is { Length: > 0 }) problems.Add($"skeleton {res.Skeleton} not in any loaded pack; parts provide the bones");
            var first = parts.FirstOrDefault(p => p.Model.Skinned) ?? parts[0];
            skeletonMesh = first.Model;
            skeletonLabel = first.Mesh.Entry.Name;
            skeletonInfo["name"] = first.Mesh.Entry.Name;
            skeletonInfo["from_part"] = true;
            skeletonInfo["note"] = "no preset skeleton: taken from the first skinned part";
        }
        var skel = ModelSkeleton.Merge(skeletonMesh, skeletonLabel, parts.Select(p => (p.Mesh.Entry.Name, p.Model)).ToList());

        // ---- geometry --------------------------------------------------------------------------------------------
        var subs = new List<Sub>();
        var materials = new Dictionary<string, Mat>();
        var partReports = new JsonArray();
        var meshMap = new JsonArray();
        for (int pi = 0; pi < parts.Count; pi++)
        {
            var (slot, m, model, label, gid) = parts[pi];
            var emap = skel.Map(model);
            var (rebind, worst) = Rebind(model, skel, emap);
            var bySub = m.Submeshes.Where(s => s.Entry is not null).GroupBy(s => (s.Entry!.Value, s.Submesh!.Value))
                .ToDictionary(g => g.Key, g => g.First());
            var skipped = new JsonArray();
            int count = 0;
            string mstem = Safe(m.Entry.MeshName, 40);
            foreach (var ge in model.GeometryEntries.Where(e => e.Element == 0))
            {
                var v = ge.Vertices;
                foreach (var s in ge.Submeshes)
                {
                    string key = $"e{ge.Index}.s{s.Index}";
                    var sub = bySub.GetValueOrDefault((ge.Index, s.Index));
                    string baseMat = sub?.BaseMaterial is { Length: > 0 } b ? b : model.MaterialName(s.MaterialSlot);
                    if (SkipPrefixes.Any(p => baseMat.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        skipped.Add($"{key}: {baseMat}");
                        continue;
                    }
                    if (v is null || s.IndexCount == 0 || s.Indices.Length == 0)
                    {
                        skipped.Add($"{key}: no decodable vertices");
                        continue;
                    }
                    subs.Add(Geometry(slot, m, model, ge, s, v, emap, rebind, mstem, key, baseMat, sub, materials, meshMap, pi));
                    count++;
                }
            }
            partReports.Add(new JsonObject
            {
                ["slot"] = slot.Slot.Name, ["mesh"] = m.Entry.Name, ["pack"] = label, ["index"] = catalog.Split(gid).Index,
                ["logical_name"] = catalog.Name(gid), ["rebind_max_delta"] = Math.Round(worst, 6),
                ["max_shift_mm"] = Math.Round(skel.MaxShift(model), 3), ["submeshes"] = count, ["skipped"] = skipped,
            });
        }

        // ---- materials / textures -------------------------------------------------------------------------------
        var matReports = new JsonObject();
        var files = new Dictionary<string, (string? Albedo, string? Normal, string Recipe, string Alpha, float Cutoff)>();
        var layers = new Dictionary<string, DecodedImage?>(StringComparer.OrdinalIgnoreCase);
        var normals = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, md) in materials)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke($"material {md.Base}");
            var warnings = new JsonArray();
            string? albedo = null, normal = null;
            string recipe = "none", alphaMode = "opaque";
            float cutoff = 0.5f;
            if (textures)
            {
                string rel = $"textures/{Safe(key.Replace(".mat", "").Replace('#', '_'), 80)}_albedo.png";
                // the prototype's material-aware preview: eyes composed, hair alpha (hardened at its cutoff), opacity alpha
                var hits = sdb?.FindMaterial(md.Base);
                var surface = Nightrunner.Core.Sdb.MaterialPreview.Compose(hits is { Count: 1 } ? sdb!.Material(hits[0]) : null,
                    md.Override, t => layers.TryGetValue(t, out var l) ? l : layers[t] = Layer(catalog, t));
                foreach (var w in surface.Warnings) warnings.Add(w);
                recipe = surface.Recipe;
                alphaMode = surface.Alpha.ToString().ToLowerInvariant();
                cutoff = surface.Alpha == Nightrunner.Core.Sdb.AlphaMode.Mask ? surface.Cutoff : 0.5f;
                if (surface.Image is { } img)
                {
                    if (surface.Alpha == Nightrunner.Core.Sdb.AlphaMode.Mask)
                        for (int i = 3; i < img.Bgra.Length; i += 4) img.Bgra[i] = img.Bgra[i] >= surface.Cutoff * 255 ? (byte)255 : (byte)0;
                    Directory.CreateDirectory(Path.Combine(outDir, "textures"));
                    PngWriter.Write(img, Path.Combine(outDir, rel));
                    albedo = rel;
                }
                else if (md.Textures.FirstOrDefault(t => t.Param.Equals("dif_0_tex", StringComparison.OrdinalIgnoreCase)) is { } dif)
                {
                    if (Png(catalog, dif, Path.Combine(outDir, rel), normalMap: false, opaque: true) is { } why) warnings.Add($"albedo {dif.Texture}: {why}");
                    else albedo = rel;
                }
                if (md.Textures.FirstOrDefault(t => t.Param.Equals("nrm_0_tex", StringComparison.OrdinalIgnoreCase)) is { } nrm
                    && !nrm.Texture.StartsWith("default_", StringComparison.OrdinalIgnoreCase))
                {
                    string nrel = $"textures/{Safe(Path.GetFileNameWithoutExtension(nrm.Texture), 80)}.png";
                    if (!normals.TryGetValue(nrm.Texture, out var why))
                        normals[nrm.Texture] = why = Png(catalog, nrm, Path.Combine(outDir, nrel), normalMap: true);
                    if (why is null) normal = nrel;
                    else warnings.Add($"normal {nrm.Texture}: {why}");
                }
            }
            files[key] = (albedo, normal, recipe, alphaMode, cutoff);
            var ov = new JsonObject();
            foreach (var (p, t) in md.Override) ov[p] = t;
            matReports[key] = new JsonObject
            {
                ["base"] = md.Base, ["override"] = ov, ["albedo"] = albedo, ["normal"] = normal,
                ["recipe"] = recipe, ["alpha_mode"] = alphaMode, ["cutoff"] = cutoff,
                ["warnings"] = warnings,
                ["all_textures"] = new JsonArray(md.Textures.Select(t => t.Texture).Where(t => t.Length > 0)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(t => (JsonNode)t!).ToArray()),
            };
        }

        // ---- the Cast -------------------------------------------------------------------------------------------
        ct.ThrowIfCancellationRequested();
        progress?.Invoke("writing cast");
        var cast = new CastFile();
        var root = cast.CreateRoot();
        var meta = root.CreateMetadata();
        meta.SetUpAxis("y");
        meta.SetSoftware(CastExport.Software);
        var mdl = root.CreateModel();
        mdl.SetName(CastExport.CastText(stem));
        Str(mdl, "bp_model", res.Name);
        Str(mdl, "bp_variant", "Default");
        Str(mdl, "bp_format", Format);
        WriteSkeleton(mdl, skel);
        var matNodes = new Dictionary<string, CastMaterial>();
        foreach (var (key, md) in materials)
        {
            var mn = mdl.CreateMaterial();
            mn.SetName(CastExport.CastText(key));
            mn.SetType("pbr");
            var (albedo, normal, recipe, alphaMode, cutoff) = files[key];
            foreach (var (slotName, rel) in new[] { ("albedo", albedo), ("normal", normal) })
            {
                if (rel is null) continue;
                var f = mn.CreateFile();
                f.SetPath(rel);
                mn.SetSlot(slotName, f.Hash);
            }
            Str(mn, "bp_material", CastExport.CastText(md.Base));
            Str(mn, "bp_alpha_mode", alphaMode);
            mn.CreateProperty("bp_alpha_cutoff", CastPropertyType.Float).SetValues([cutoff]);
            Str(mn, "bp_recipe", recipe);
            Str(mn, "bp_textures", PyJson(md.Textures.Select(t => t.Texture).Where(t => t.Length > 0)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()));
            matNodes[key] = mn;
        }
        foreach (var s in subs)
        {
            var me = mdl.CreateMesh();
            me.SetName(CastExport.CastText(s.Name));
            me.SetMaterial(matNodes[s.MaterialKey].Hash);
            me.SetUVLayerCount(s.Uv1 is not null ? 2 : 1);
            me.SetColorLayerCount(0);
            me.SetVertexPositionBuffer(s.Pos);
            me.SetVertexNormalBuffer(s.Nrm);
            me.SetVertexTangentBuffer(s.Tan);
            me.SetVertexUVLayerBuffer(0, s.Uv0);
            if (s.Uv1 is not null) me.SetVertexUVLayerBuffer(1, s.Uv1);
            me.SetFaceBuffer(s.Faces);
            me.SetMaximumWeightInfluence(CastExport.MaxInfluences);
            me.SetSkinningMethod("linear");
            me.SetVertexWeightBoneBuffer(s.Bones);
            me.SetVertexWeightValueBuffer(s.Weights);
            Str(me, "bp_slot", CastExport.CastText(s.Slot));
            Str(me, "bp_source_mesh", CastExport.CastText(s.Source));
            me.CreateProperty("bp_entry", CastPropertyType.Integer).SetValues([(uint)s.Entry]);
            me.CreateProperty("bp_submesh", CastPropertyType.Integer).SetValues([(uint)s.Submesh]);
            me.CreateProperty("bp_material_slot", CastPropertyType.Integer).SetValues([(uint)s.MaterialSlot]);
            me.CreateProperty("bp_vertex_id", CastPropertyType.Integer).SetValues(s.VertexIds);
        }
        string path = Path.Combine(outDir, $"{Safe(stem, 80)}.cast");
        string tmp = path + ".partial";
        cast.Save(tmp);
        System.IO.File.Move(tmp, path, overwrite: true);

        var report = new JsonObject
        {
            ["format"] = Format,
            ["cast"] = Path.GetFileName(path),
            ["model"] = res.Name,
            ["variant"] = "Default",
            ["skeleton"] = skeletonInfo,
            ["rest_overrides"] = new JsonArray(skel.Overrides.Select(o => (JsonNode)new JsonObject
            {
                ["bone"] = o.Bone, ["part"] = o.Part, ["mm"] = Math.Round(o.Millimetres, 4), ["deg"] = Math.Round(o.Degrees, 4),
            }).ToArray()),
            ["parts"] = partReports,
            ["materials"] = matReports,
            ["problems"] = new JsonArray(problems.Select(p => (JsonNode)p!).ToArray()),
            ["meshes"] = subs.Count,
            ["mesh_map"] = meshMap,
            ["bones"] = new JsonArray(skel.Names.Select(n => (JsonNode)CastExport.CastText(n)!).ToArray()),
        };
        skeletonInfo["bones"] = skeletonMesh?.Entities.Length ?? 0;
        skeletonInfo["merged_bones"] = skel.Names.Count;
        skeletonInfo["rest_rule"] = "skeleton rest; a bone whose skinned part bind differs by > 0.5 mm / 0.5 deg takes the rig owner's bind";
        System.IO.File.WriteAllText(Path.Combine(outDir, $"{Path.GetFileName(path)}.json"),
                          report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return new Result(path, report, cast);
    }

    private static Sub Geometry(ResolvedSlot slot, ResolvedMesh m, MeshModel model, GeometryEntry ge, Submesh s, VertexData v,
                                int[] emap, double[][] rebind, string mstem, string key, string baseMat, ResolvedSubmesh? sub,
                                Dictionary<string, Mat> materials, JsonArray meshMap, int partIndex)
    {
        // np.unique(indices, return_inverse=True)
        var mark = new int[v.Count];
        foreach (var x in s.Indices) mark[x] = 1;
        var used = new List<uint>();
        for (int i = 0; i < mark.Length; i++)
            if (mark[i] != 0) { mark[i] = used.Count; used.Add((uint)i); }
        var faces = s.Indices.Select(x => (uint)mark[x]).ToArray();
        int n = used.Count;
        var pos = new float[n * 3];
        var nrm = new float[n * 3];
        var tan = new float[n * 3];
        var bones = new uint[n * 4];
        var weights = new float[n * 4];
        if (v.Skinned && s.Palette.Length > 0)
        {
            for (int i = 0; i < n; i++)
            {
                int src = (int)used[i];
                var blend = new double[16];
                double total = 0;
                for (int k = 0; k < 4; k++) total += v.Weights![src * 4 + k];
                total = Math.Max(total, 1e-12);
                for (int k = 0; k < 4; k++)
                {
                    float w = v.Weights![src * 4 + k];
                    int ent = s.Palette[Math.Min((int)v.Joints![src * 4 + k], s.Palette.Length - 1)];
                    bool active = w > 0;
                    if (active)
                    {
                        var mat = rebind[active ? ent : 0];
                        for (int j = 0; j < 16; j++) blend[j] += w / total * mat[j];
                    }
                    bones[i * 4 + k] = active ? (uint)emap[ent] : 0;
                    weights[i * 4 + k] = w;
                }
                Transform(blend, v.Positions, src, pos, i, translate: true, normalise: false);
                Transform(blend, v.Normals, src, nrm, i, translate: false, normalise: true);
                Transform(blend, v.Tangents, src, tan, i, translate: false, normalise: true);
            }
        }
        else
        {
            int owner = ge.OwnerEntity ?? 0;
            uint bone = emap.Length > 0 ? (uint)emap[owner] : 0;
            for (int i = 0; i < n; i++)
            {
                int src = (int)used[i];
                for (int c = 0; c < 3; c++)
                {
                    pos[i * 3 + c] = v.Positions[src * 3 + c];
                    nrm[i * 3 + c] = v.Normals[src * 3 + c];
                    tan[i * 3 + c] = v.Tangents[src * 3 + c];
                }
                bones[i * 4] = bone;
                weights[i * 4] = 1f;
            }
        }
        var ov = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in sub?.Textures ?? [])
            if (t.Param.Length > 0 && t.Texture.Length > 0 && t.Source == "model override") ov[t.Param] = t.Texture;
        string mkey = ov.Count == 0 ? baseMat : $"{baseMat}#{Sha1Prefix(PyJson(ov.Select(kv => new[] { kv.Key, kv.Value }).ToList()))}";
        materials.TryAdd(mkey, new Mat(baseMat, ov, sub?.Textures ?? []));
        string name = $"{Safe(slot.Slot.Name is { Length: > 0 } sn ? sn : "slot", 20)}.{mstem}.{key}";
        meshMap.Add(new JsonObject { ["name"] = name, ["part"] = partIndex, ["entry"] = ge.Index, ["submesh"] = s.Index, ["material"] = mkey });
        return new Sub(name, mkey, pos, nrm, tan, Gather(v.Uv0, used, 2), v.Uv1 is null ? null : Gather(v.Uv1, used, 2),
                       faces, bones, weights, slot.Slot.Name, m.Entry.Name, ge.Index, s.Index, s.MaterialSlot, used.ToArray());
    }

    private static void Transform(double[] m, float[] src, int i, float[] dst, int o, bool translate, bool normalise)
    {
        double x = src[i * 3], y = src[i * 3 + 1], z = src[i * 3 + 2];
        double rx = m[0] * x + m[1] * y + m[2] * z, ry = m[4] * x + m[5] * y + m[6] * z, rz = m[8] * x + m[9] * y + m[10] * z;
        if (translate) { rx += m[3]; ry += m[7]; rz += m[11]; }
        if (normalise)
        {
            double len = Math.Max(Math.Sqrt(rx * rx + ry * ry + rz * rz), 1e-12);
            rx /= len; ry /= len; rz /= len;
        }
        dst[o * 3] = (float)rx;
        dst[o * 3 + 1] = (float)ry;
        dst[o * 3 + 2] = (float)rz;
    }

    private static float[] Gather(float[] src, List<uint> used, int width)
    {
        var r = new float[used.Count * width];
        for (int i = 0; i < used.Count; i++)
            for (int c = 0; c < width; c++) r[i * width + c] = src[used[i] * width + c];
        return r;
    }

    /// <summary>
    /// Per part entity: Rest[mapped] · inv_bind_part (fallback inv(G_part)) as a 4×4 row-major — what the engine's
    /// skinning gives at the merged rest pose — and max |M − I| over entities.
    /// </summary>
    public static (double[][] Matrices, double Worst) Rebind(MeshModel model, ModelSkeleton skel, int[] emap)
    {
        var g = model.EntityGlobals();
        var result = new double[model.Entities.Length][];
        double worst = 0;
        foreach (var e in model.Entities)
        {
            double[] ib = M4(e.InvBind);
            if (ib.Any(x => !double.IsFinite(x)) || Math.Abs(Det3(ib)) < 1e-8) ib = Invert(g[e.Index]);
            var m = Mul(skel.Rest[emap[e.Index]], ib);
            result[e.Index] = m;
            for (int i = 0; i < 16; i++) worst = Math.Max(worst, Math.Abs(m[i] - (i % 5 == 0 ? 1 : 0)));
        }
        return (result, worst);
    }

    /// <summary>The skeleton as Cast bones: local TRS from the rest globals, world TRS, scale 1, <c>bp_source</c>.</summary>
    public static void WriteSkeleton(CastModel mdl, ModelSkeleton skel)
    {
        var node = mdl.CreateSkeleton();
        for (int i = 0; i < skel.Names.Count; i++)
        {
            var g = skel.Rest[i];
            int p = skel.Parents[i];
            var local = p < 0 ? g : Mul(Invert(skel.Rest[p]), g);
            var b = node.CreateBone();
            b.SetName(CastExport.CastText(skel.Names[i]));
            b.SetParentIndex(p);
            b.SetSegmentScaleCompensate(false);
            b.SetLocalPosition([(float)local[3], (float)local[7], (float)local[11]]);
            b.SetLocalRotation(Quaternion(local));
            b.SetWorldPosition([(float)g[3], (float)g[7], (float)g[11]]);
            b.SetWorldRotation(Quaternion(g));
            b.SetScale([1f, 1f, 1f]);
            Str(b, "bp_source", skel.Source[i]);
        }
    }

    private static float[] Quaternion(double[] m4)
    {
        var r = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) r[i, j] = m4[i * 4 + j];
        return CastExport.QuaternionXyzw(CastExport.PolarRotation(r)).Select(x => (float)x).ToArray();
    }

    /// <summary>Decode a texture's largest level no bigger than <see cref="TextureDim"/> to PNG; null, or why not.</summary>
    /// <summary>A texture layer for the preview recipes: its largest surface no wider than <see cref="TextureDim"/>.</summary>
    private static DecodedImage? Layer(RpackCatalog catalog, string name)
    {
        var gids = catalog.Lookup(name, ModelResolver.TextureType);
        if (gids.Length == 0) return null;
        try
        {
            var (entry, index) = catalog.Split(gids[0]);
            var tex = TextureResource.Open(entry.Pack!, index);
            if (tex.PayloadProblem is not null || TextureDecoder.Reason(tex.Header.Format) is not null) return null;
            ImgcLevel? level = tex.Levels.Where(l => l.Face == 0 && Math.Max(l.Width, l.Height) <= TextureDim).Cast<ImgcLevel?>().FirstOrDefault() ?? tex.TopLevel;
            return level is { } lv ? tex.Decode(lv, rebuildNormalZ: false) : null;
        }
        catch (Exception e) when (e is ImgcException or IOException or RpackFormatException)
        {
            return null;
        }
    }

    private static string? Png(RpackCatalog catalog, ResolvedTexture t, string path, bool normalMap, bool opaque = false)
    {
        if (t.Gids.Length == 0) return "not in any loaded pack";
        try
        {
            var (entry, index) = catalog.Split(t.Gids[0]);
            var tex = TextureResource.Open(entry.Pack!, index);
            if (tex.PayloadProblem is { } p) return p;
            if (TextureDecoder.Reason(tex.Header.Format) is { } r) return r;
            ImgcLevel? level = tex.Levels.Where(l => l.Face == 0 && Math.Max(l.Width, l.Height) <= TextureDim).Cast<ImgcLevel?>().FirstOrDefault() ?? tex.TopLevel;
            if (level is null) return "no surface";
            var img = tex.Decode(level.Value, rebuildNormalZ: normalMap);
            if (normalMap || opaque)          // the plain recipe draws dif_0_tex opaque (its alpha is not coverage)
                for (int i = 3; i < img.Bgra.Length; i += 4) img.Bgra[i] = 255;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            PngWriter.Write(img, path);
            return null;
        }
        catch (Exception e) when (e is ImgcException or IOException or RpackFormatException)
        {
            return e.Message;
        }
    }

    private static void Str(CastNode node, string name, string value) =>
        node.CreateProperty(name, CastPropertyType.String).SetString(value);

    /// <summary>Python's <c>json.dumps</c> of a list of strings / string pairs (default separators, ASCII-escaped).</summary>
    private static string PyJson(IEnumerable<object> items)
    {
        var sb = new StringBuilder("[");
        bool first = true;
        foreach (var item in items)
        {
            if (!first) sb.Append(", ");
            first = false;
            if (item is string[] pair) sb.Append('[').Append(PyString(pair[0])).Append(", ").Append(PyString(pair[1])).Append(']');
            else sb.Append(PyString((string)item));
        }
        return sb.Append(']').ToString();
    }

    private static string PyJson(List<string> items) => PyJson(items.Cast<object>());
    private static string PyJson(List<string[]> items) => PyJson(items.Cast<object>());

    private static string PyString(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
            sb.Append(c switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\b' => "\\b", '\f' => "\\f",
                _ when c < 0x20 || c > 0x7E => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        return sb.Append('"').ToString();
    }

    private static string Sha1Prefix(string text) =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..6];

    private static double[] M4(float[] m34)
    {
        var r = new double[16];
        for (int i = 0; i < 12; i++) r[i] = m34[i];
        r[15] = 1;
        return r;
    }

    private static double Det3(double[] m) =>
        m[0] * (m[5] * m[10] - m[6] * m[9]) - m[1] * (m[4] * m[10] - m[6] * m[8]) + m[2] * (m[4] * m[9] - m[5] * m[8]);

    public static double[] Mul(double[] a, double[] b)
    {
        var r = new double[16];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                double s = 0;
                for (int k = 0; k < 4; k++) s += a[i * 4 + k] * b[k * 4 + j];
                r[i * 4 + j] = s;
            }
        return r;
    }

    /// <summary>General 4×4 inverse (Gauss-Jordan, partial pivoting), as <c>np.linalg.inv</c>.</summary>
    public static double[] Invert(double[] m)
    {
        var a = new double[4, 8];
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++) a[i, j] = m[i * 4 + j];
            a[i, 4 + i] = 1;
        }
        for (int c = 0; c < 4; c++)
        {
            int piv = c;
            for (int r = c + 1; r < 4; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[piv, c])) piv = r;
            if (Math.Abs(a[piv, c]) < 1e-300) throw new ArithmeticException("singular matrix");
            if (piv != c) for (int j = 0; j < 8; j++) (a[c, j], a[piv, j]) = (a[piv, j], a[c, j]);
            double d = a[c, c];
            for (int j = 0; j < 8; j++) a[c, j] /= d;
            for (int r = 0; r < 4; r++)
            {
                if (r == c) continue;
                double f = a[r, c];
                if (f == 0) continue;
                for (int j = 0; j < 8; j++) a[r, j] -= f * a[c, j];
            }
        }
        var inv = new double[16];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++) inv[i * 4 + j] = a[i, 4 + j];
        return inv;
    }
}
