using Nightrunner.Core.Cast;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;
using Nightrunner.Core.Sdb;
using Nightrunner.Core.Texture;
using File = System.IO.File;

namespace Nightrunner.Core.Export;

/// <summary>What an asset export writes besides the scene.</summary>
public sealed record AssetExportOptions(bool Gltf = true, bool Textures = true, bool Materials = true, bool Skins = true);

/// <summary>
/// Mesh and model exports, one folder each:
/// <code>
/// &lt;mesh&gt;/                          &lt;model&gt;/
///   &lt;mesh&gt;.cast  .mesh.json          &lt;model&gt;.model                (the pak member as stored)
///   &lt;mesh&gt;.glb                         &lt;model&gt;.cast  .cast.json  .glb   (single scene, merged skeleton)
///   &lt;mesh&gt;.skn                         meshes/&lt;mesh&gt;/…              (each drawn mesh as on the left, no textures)
///   textures/&lt;tex&gt;.dds  .png          textures/…  materials/…
///   materials/&lt;mat&gt;.mat
/// </code>
/// Textures: every texture a material of the mesh (its full table, skin-only materials included) binds in the SDB,
/// as stored (DDS, byte-identical to the prototype) and decoded (PNG). Materials: DevTools <c>.mat</c> text rebuilt
/// from the SDB (<see cref="MatWriter"/>). A texture or material that cannot be written is skipped and logged; the
/// scene files are the export, and their failure fails it.
/// </summary>
public static class AssetExport
{
    /// <summary>A new folder <c>root/name</c>, or <c>name_2</c>, <c>name_3</c>… when it exists.</summary>
    public static string UniqueFolder(string root, string name)
    {
        string path = Path.Combine(root, name);
        for (int i = 2; Directory.Exists(path) || File.Exists(path); i++) path = Path.Combine(root, $"{name}_{i}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string ExportMesh(RpackCatalog catalog, SdbFile? sdb, int gid, string outRoot, AssetExportOptions options,
                                    Action<string>? progress = null, CancellationToken ct = default)
    {
        string name = catalog.Name(gid).Trim();
        var (entry, index) = catalog.Split(gid);
        var model = MeshDecoder.Decode(entry.Pack!, index);
        string dir = UniqueFolder(outRoot, RawExporter.SafeName(name));
        var counts = new Counts();
        WriteMesh(catalog, model, name, dir, options, counts, progress, ct);
        if (options.Textures || options.Materials)
            Extras(catalog, sdb, model.FullMaterialTable(), dir, options, counts, progress, ct);
        return $"{name} -> {dir}: {counts}";
    }

    public static string ExportModel(ModelCatalog models, ModelEntry entry, RpackCatalog catalog, SdbFile? sdb,
                                     Func<int, MeshModel> decode, Func<int, MeshModel, int>? skinOf, string outRoot,
                                     AssetExportOptions options, Action<string>? progress = null, CancellationToken ct = default)
    {
        var doc = models.Load(entry);
        var res = ModelResolver.Resolve(doc, catalog, sdb, decode, skinOf);
        string stem = Path.GetFileNameWithoutExtension(entry.Basename);
        string dir = UniqueFolder(outRoot, RawExporter.SafeName(stem));
        File.WriteAllBytes(Path.Combine(dir, entry.Basename), models.Pak(entry.Pak).Read(entry.Member));
        var counts = new Counts();

        progress?.Invoke("single cast");
        var single = ModelCast.Write(res, catalog, decode, dir, stem, options.Textures, progress, ct, sdb);
        counts.Files++;
        if (options.Gltf) Gltf.Save(single.Cast, Path.ChangeExtension(single.CastPath, ".glb"), dir);

        var materials = new List<string>();
        foreach (var m in res.Slots.SelectMany(s => s.Meshes).Where(m => m.Drawn && m.Found && m.Error is null))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(m.Entry.Name);
            var mesh = decode(m.Gids[0]);
            string meshDir = Path.Combine(dir, "meshes", RawExporter.SafeName(m.Entry.MeshName));
            Directory.CreateDirectory(meshDir);
            WriteMesh(catalog, mesh, catalog.Name(m.Gids[0]).Trim(), meshDir, options, counts, progress, ct);
            materials.AddRange(mesh.FullMaterialTable());
            materials.AddRange(m.Submeshes.Select(s => s.BaseMaterial));
        }
        if (options.Textures || options.Materials)
            Extras(catalog, sdb, materials, dir, options, counts, progress, ct,
                   res.Slots.SelectMany(s => s.Meshes).Where(m => m.Drawn).SelectMany(m => m.Submeshes).SelectMany(s => s.Textures));
        return $"{entry.Basename} -> {dir}: {counts}, {single.Report["rest_overrides"]!.AsArray().Count} rest overrides";
    }

    private sealed class Counts
    {
        public int Files, Textures, Materials, Skipped;
        public override string ToString() =>
            $"{Files} scene file(s), {Textures} texture(s), {Materials} material(s)" + (Skipped > 0 ? $", {Skipped} skipped" : "");
    }

    private static void WriteMesh(RpackCatalog catalog, MeshModel model, string name, string dir, AssetExportOptions options,
                                  Counts counts, Action<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Invoke($"{name}: cast");
        string safe = RawExporter.SafeName(name);
        var (report, _) = CastExport.WriteFiles(model, name, Path.Combine(dir, $"{safe}.cast"), null);
        counts.Files++;
        if (options.Gltf)
        {
            Gltf.Save(CastExport.Build(model, name).Cast, Path.Combine(dir, $"{safe}.glb"));
            counts.Files++;
        }
        if (options.Skins && model.SkinRaw is { } raw && MeshSkins.Decode(raw) is { Error: null, Skins.Count: > 0 } skins)
            File.WriteAllText(Path.Combine(dir, $"{safe}.skn"), SknWriter.Write(skins, model, null));
        foreach (var s in report.Skipped) Log.Warn("export", $"{name}: {s}");
    }

    /// <summary>Textures (DDS + PNG) and .mat text for these materials, into dir/textures and dir/materials.</summary>
    private static void Extras(RpackCatalog catalog, SdbFile? sdb, IEnumerable<string> materialNames, string dir,
                               AssetExportOptions options, Counts counts, Action<string>? progress, CancellationToken ct,
                               IEnumerable<ResolvedTexture>? extraTextures = null)
    {
        var names = materialNames.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var textures = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in extraTextures ?? []) textures.TryAdd(t.Texture, t.Gids);
        foreach (var mat in names)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var t in ModelResolver.Textures(catalog, sdb, mat, [], out _)) textures.TryAdd(t.Texture, t.Gids);
            if (!options.Materials || sdb is null) continue;
            var hits = sdb.FindMaterial(mat);
            if (hits.Count != 1)
            {
                Log.Warn("export", $"{mat}: {(hits.Count == 0 ? "not in the SDB" : $"{hits.Count} SDB entries")}; no .mat written");
                counts.Skipped++;
                continue;
            }
            try
            {
                string text = MatWriter.Write(sdb, sdb.Material(hits[0]));
                Directory.CreateDirectory(Path.Combine(dir, "materials"));
                File.WriteAllText(Path.Combine(dir, "materials", RawExporter.SafeName(mat.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) ? mat : mat + ".mat")), text);
                counts.Materials++;
            }
            catch (SdbFormatException e)
            {
                Log.Warn("export", $"{mat}: {e.Message}");
                counts.Skipped++;
            }
        }
        if (!options.Textures) return;
        foreach (var (name, gids) in textures.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (gids.Length == 0)
            {
                Log.Warn("export", $"texture {name}: not in any loaded pack");
                counts.Skipped++;
                continue;
            }
            progress?.Invoke(name);
            try
            {
                var (entry, index) = catalog.Split(gids[0]);
                var tex = TextureResource.Open(entry.Pack!, index);
                string folder = Path.Combine(dir, "textures");
                Directory.CreateDirectory(folder);
                string stem = RawExporter.SafeName(Path.GetFileNameWithoutExtension(name));
                if ((tex.PayloadProblem ?? DdsWriter.Refusal(tex.Header)) is { } why)
                    throw new ImgcException(why);
                using (var fs = File.Create(Path.Combine(folder, stem + ".dds"))) DdsWriter.Write(tex, fs);
                if (tex.TopLevel is { } top && TextureDecoder.Reason(tex.Header.Format) is null)
                    PngWriter.Write(tex.Decode(top), Path.Combine(folder, stem + ".png"));
                counts.Textures++;
            }
            catch (Exception e) when (e is ImgcException or IOException or RpackFormatException)
            {
                Log.Warn("export", $"texture {name}: {e.Message}");
                counts.Skipped++;
            }
        }
    }
}
