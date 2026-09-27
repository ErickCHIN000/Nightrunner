using System.Text;
using System.Text.Json.Nodes;
using Nightrunner.Core.Cast;

namespace Nightrunner.Core.Mesh;

/// <summary>A mesh build refused by name (the prototype's BuildError / UnsupportedError / FormatError).</summary>
public sealed class MeshBuildException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record MeshBuildOptions
{
    /// <summary>Bone transforms that differ from the sidecar only warn (the native skeleton is kept), as the project build passes.</summary>
    public bool IgnoreBoneChanges { get; init; }
    /// <summary>Pad a re-laid-out index part to 16 bytes (the on-demand packs, field08 bit 12); engine_pc stores exact sizes.</summary>
    public bool PadIndexPart { get; init; } = true;
    /// <summary>Decode the produced parts and compare them with the plan before returning (the prototype's self-check).</summary>
    public bool VerifyResult { get; init; } = true;
    /// <summary>
    /// glTF edits: snaps the loaded scene back onto the original mesh and returns the scene to read plus notes that become
    /// warnings. Default (null): <see cref="ModelSplit.NormalizeMeshScene"/>, the prototype's <c>normalize_mesh_scene</c>.
    /// </summary>
    public Func<CastFile, MeshModel, (CastFile Cast, IReadOnlyList<string> Notes)>? GltfNormalizer { get; init; }
}

/// <summary>
/// Every part of the resource by type — regenerated 0x10/0x11/0xF0/0xF1 (and 0x12 when a new material remapped the skins),
/// the rest carried verbatim — the build warnings and the report (<c>rebuild.py</c>'s report plus the patch counters).
/// </summary>
public sealed record MeshBuildResult(IReadOnlyDictionary<byte, byte[]> Parts, IReadOnlyList<string> Warnings, JsonObject Report);

/// <summary>
/// Mesh writing entry point: original parts + edited scene + sidecar → new part bytes. Port of <c>mesh/codec.py</c>
/// (<c>build</c>, <c>rebuild_from_files</c>, <c>load_edit_scene</c>). DLTB only; every refusal is a <see cref="MeshBuildException"/>.
/// An unedited scene reproduces every part byte for byte, apart from the embedded-name rewrite when the logical name
/// differs from the source.
/// </summary>
public static class MeshBuild
{
    public static readonly byte[] RegeneratedTypes = [MeshDecoder.PartImage, MeshDecoder.PartFixups, MeshDecoder.PartVertex, MeshDecoder.PartIndex];

    /// <param name="originalParts">The target resource's parts by type, as read from the pack.</param>
    /// <param name="logicalName">The output resource name (may differ from the source: identity rename).</param>
    /// <param name="scenePath">The edited scene: .cast, .glb or .gltf.</param>
    /// <param name="sidecar">The <c>mesh.json</c> written at export, or null (then derived from the original parts).</param>
    public static MeshBuildResult Build(IReadOnlyDictionary<byte, byte[]> originalParts, string logicalName, string scenePath,
                                        JsonObject? sidecar, MeshBuildOptions? options = null)
    {
        options ??= new MeshBuildOptions();
        try
        {
            return BuildCore(originalParts, logicalName, scenePath, sidecar, options);
        }
        catch (Exception e) when (e is MeshFormatException or MeshUnsupportedException or CastFormatException or ModelSplitException
                                      or IOException or UnauthorizedAccessException or OverflowException)
        {
            throw new MeshBuildException($"{CastImport.Repr(logicalName)}: {e.Message}", e);
        }
    }

    private static MeshBuildResult BuildCore(IReadOnlyDictionary<byte, byte[]> parts, string logicalName, string scenePath, JsonObject? sidecar,
                                             MeshBuildOptions options)
    {
        foreach (var t in new[] { MeshDecoder.PartImage, MeshDecoder.PartFixups })
            if (!parts.ContainsKey(t)) throw new MeshBuildException($"{CastImport.Repr(logicalName)}: missing part 0x{t:X2}");
        if (sidecar is not null && !CastImport.SchemaMatches(sidecar["schema"]))
            throw new MeshBuildException($"sidecar schema {CastImport.Repr(sidecar["schema"]?.ToString())} is not {CastImport.SidecarSchema}");
        // decode under the name the parts were exported as: a renamed / cloned mesh keeps the source's scene node names
        // (<source>.eN.sM); the new logical name only goes into the rebuilt parts
        string decodeName = sidecar?["name"] is JsonValue nv && nv.TryGetValue<string>(out var sn) && sn.Length > 0 ? sn : logicalName;
        var model = MeshDecoder.Decode(decodeName, parts[MeshDecoder.PartImage], parts[MeshDecoder.PartFixups],
                                       parts.GetValueOrDefault(MeshDecoder.PartVertex), parts.GetValueOrDefault(MeshDecoder.PartIndex),
                                       parts.GetValueOrDefault(MeshDecoder.PartSkin), parts.GetValueOrDefault(MeshDecoder.PartCloth));
        MeshRebuild.RefuseLayout(model);   // DL2 meshes are read-only: refused before the scene is even read
        sidecar ??= SidecarFromModel(model);
        if (!System.IO.File.Exists(scenePath)) throw new MeshBuildException($"{scenePath}: scene missing");
        var scene = LoadEditScene(scenePath, model, options);
        var imp = CastImport.Resolve(scene, sidecar);
        var result = MeshRebuild.Rebuild(model, imp, logicalName, options.PadIndexPart, options.IgnoreBoneChanges);
        if (options.VerifyResult)
        {
            var problems = MeshRebuild.Verify(result, model, decodeName);
            if (problems.Count > 0)
                throw new MeshBuildException($"{CastImport.Repr(logicalName)}: rebuilt parts fail the self-check: " + string.Join("; ", problems.Take(5)));
        }
        var outParts = new Dictionary<byte, byte[]>(parts);
        void Put(byte t, byte[]? data)
        {
            if (data is not null && parts.ContainsKey(t)) outParts[t] = data;
        }
        Put(MeshDecoder.PartImage, result.Image);
        Put(MeshDecoder.PartFixups, result.Fixups);
        Put(MeshDecoder.PartVertex, result.Vertex);
        Put(MeshDecoder.PartIndex, result.Index);
        Put(MeshDecoder.PartSkin, result.Skin);
        Put(MeshDecoder.PartCloth, result.Cloth);
        var warnings = (result.Report["warnings"] as JsonArray ?? []).Select(w => w!.GetValue<string>()).ToList();
        return new MeshBuildResult(outParts, warnings, result.Report);
    }

    /// <summary>
    /// The scene to rebuild from: a .cast as it is; a glTF edit first snapped back onto the original mesh
    /// (<see cref="MeshBuildOptions.GltfNormalizer"/>; Blender's glTF round trip moves every value by float noise and splits
    /// a few vertices).
    /// </summary>
    public static CastImportScene LoadEditScene(string scenePath, MeshModel model, MeshBuildOptions? options = null)
    {
        string ext = Path.GetExtension(scenePath).ToLowerInvariant();
        if (ext is not (".gltf" or ".glb")) return CastImport.Read(scenePath);
        CastFile cast;
        try { cast = Gltf.Load(scenePath); }
        catch (Exception e) when (e is not OutOfMemoryException and not MeshBuildException)
        {
            throw new MeshFormatException($"{scenePath}: cannot read scene: {e.Message}");
        }
        var norm = options?.GltfNormalizer ?? ((c, m) => ModelSplit.NormalizeMeshScene(c, m));
        (cast, var notes) = norm(cast, model);
        var scene = CastImport.Read(scenePath, cast);
        scene.Warnings.AddRange(notes);
        return scene;
    }

    /// <summary>
    /// The sidecar fields the importer reads (schema, name, entities, material names, geometry entries), derived from the
    /// model itself — for a build without the <c>mesh.json</c> written at export.
    /// </summary>
    public static JsonObject SidecarFromModel(MeshModel model)
    {
        var nameBytes = Encoding.UTF8.GetBytes(model.Name);
        return new JsonObject
        {
            ["schema"] = MeshSidecar.Schema,
            ["name"] = MeshModel.Text(nameBytes),
            ["name_hex"] = Convert.ToHexStringLower(nameBytes),
            ["entities"] = new JsonArray(model.Entities.Select(en => (JsonNode?)new JsonObject
            {
                ["index"] = en.Index, ["name"] = MeshModel.Text(en.Name), ["name_hex"] = Convert.ToHexStringLower(en.Name), ["parent"] = en.Parent,
                ["local_3x4"] = new JsonArray(en.Local.Select(x => (JsonNode?)(double)x).ToArray()),
            }).ToArray()),
            ["materials"] = new JsonObject
            {
                ["count"] = model.Materials.Length, ["capacity"] = model.MaterialCapacity,
                ["entries"] = new JsonArray(model.Materials.Select(m => (JsonNode?)new JsonObject
                {
                    ["index"] = m.Index, ["name"] = MeshModel.Text(m.Name), ["name_hex"] = Convert.ToHexStringLower(m.Name),
                }).ToArray()),
            },
            ["geometry_entries"] = new JsonArray(model.GeometryEntries.Select(e => (JsonNode?)new JsonObject
            {
                ["index"] = e.Index, ["format"] = e.Format, ["vertex_count"] = e.VertexCount,
                ["submeshes"] = new JsonArray(e.Submeshes.Select(s => (JsonNode?)new JsonObject
                {
                    ["index"] = s.Index, ["material_slot"] = s.MaterialSlot, ["index_count"] = s.IndexCount,
                    ["palette"] = new JsonArray(s.Palette.Select(x => (JsonNode?)(int)x).ToArray()),
                }).ToArray()),
            }).ToArray()),
        };
    }
}
