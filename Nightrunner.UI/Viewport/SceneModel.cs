namespace Nightrunner.UI.Viewport;

/// <summary>
/// What the viewport draws, in plain arrays — no rendering types, so the renderer behind <see cref="MeshViewport"/>
/// can be swapped without touching the code that builds scenes.
/// </summary>
public sealed class SceneModel(string name, IReadOnlyList<ScenePart> parts,
                                IReadOnlyDictionary<(int Mesh, int Slot), SceneMaterial> materials,
                                IReadOnlyDictionary<int, SceneSkin>? skins = null)
{
    /// <summary>Per mesh: how its skinned parts follow a pose (see <see cref="SceneSkin"/>).</summary>
    public IReadOnlyDictionary<int, SceneSkin> Skins { get; } = skins ?? new Dictionary<int, SceneSkin>();
    public string Name { get; } = name;
    public IReadOnlyList<ScenePart> Parts { get; } = parts;

    /// <summary>By (mesh, material slot).</summary>
    public IReadOnlyDictionary<(int Mesh, int Slot), SceneMaterial> Materials { get; } = materials;
}

/// <summary>
/// One submesh: its own compacted vertex set (flat arrays, 3/2 floats per vertex) and a triangle list.
/// <see cref="Mesh"/> tells the meshes of an assembled model apart (0 for a single mesh).
/// </summary>
public sealed record ScenePart(
    int Mesh, int Entry, int Submesh, int Slot, float[] Positions, float[] Normals, float[] Tangents, float[] Bitangents,
    float[] Uvs, int[] Indices)
{
    /// <summary>Skinned parts: 4 influences per vertex — the mesh entity (index into its <see cref="SceneSkin"/>) and weight.</summary>
    public int[]? Joints { get; init; }
    public float[]? Weights { get; init; }
}

/// <summary>
/// A mesh's skin: per entity, the skeleton bone it follows and its inverse bind (3×4 row-major). A vertex moves by
/// <c>Σ w · Pose[Bone[e]] · InvBind[e]</c>, the engine's skinning.
/// </summary>
public sealed record SceneSkin(int[] Bone, double[][] InvBind);

/// <summary>A texture ready for the GPU: a DDS file the GPU samples as is, or top-level BGRA8 pixels.</summary>
public sealed record SceneTexture(string Name, byte[]? Dds, byte[]? Bgra, int Width, int Height);

/// <summary>
/// A surface: albedo and normal map, how the albedo's alpha is used (opaque, alpha-tested hair, blended eye shadow)
/// and whether the material draws at all (non-rendering materials such as <c>null.mat</c> are hidden).
/// </summary>
public sealed record SceneMaterial(SceneTexture? Albedo, SceneTexture? Normal,
                                   Nightrunner.Core.Sdb.AlphaMode Alpha = Nightrunner.Core.Sdb.AlphaMode.Opaque, bool Hidden = false)
{
    /// <summary>RGB the albedo is multiplied by (<c>dif_0_val</c>), or null for none.</summary>
    public float[]? Tint { get; init; }

    /// <summary>Why the surface has no colour, or what of the material it leaves out; reported on every load.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>Bones to draw over the model: names, parent indices (−1 root) and rest positions (3 floats per bone).</summary>
public sealed record SceneSkeleton(string[] Names, int[] Parents, float[] Positions);
