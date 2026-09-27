using Nightrunner.Core.Rpack;
using Nightrunner.Core.Texture;

namespace Nightrunner.Core.Sdb;

/// <summary>
/// The viewport's material decisions, without the GPU: which SDB material a mesh material name resolves to, and whether
/// a texture named by it can be shown (and if not, why). The viewport (<c>MeshScenes</c>) and the material census
/// (<c>tools/MeshCheck --materials</c>) both go through these, so the census counts what the viewport draws.
/// </summary>
public static class ViewerSurface
{
    public const string AlbedoParameter = "dif_0_tex", NormalParameter = "nrm_0_tex";

    /// <summary>The SDB material a mesh material name resolves to, or why none does.</summary>
    public static (SdbMaterial? Material, string? Problem) Resolve(SdbFile? sdb, string materialName)
    {
        if (sdb is null) return (null, "no SDB");
        var hits = sdb.FindMaterial(materialName);
        if (hits.Count != 1) return (null, hits.Count == 0 ? "not in SDB" : $"{hits.Count} SDB matches");
        return (sdb.Material(hits[0]), null);
    }

    /// <summary>First pack that provides the texture (the one the game loads), or null.</summary>
    public static int? Find(RpackCatalog catalog, string name)
    {
        var hits = catalog.Lookup(name, 0x20);
        return hits.Length == 0 ? null : hits[0];
    }

    /// <summary>2D, has a DXGI format, and a float/unorm/snorm/BC kind a shader can filter (not integer or depth).</summary>
    public static bool GpuSamples(ImgcHeader h) =>
        h.Type == TextureType.Texture2D && DdsWriter.Refusal(h) is null &&
        ImgcFormats.Get(h.Format).Kind is FormatKind.Bc or FormatKind.Unorm or FormatKind.Snorm or FormatKind.Float;

    /// <summary>
    /// The largest face-0 surface no wider than 2048: what the viewport decodes. A 4k level decoded to BGRA is 64 MB,
    /// and the viewer draws it no sharper than its 2k mip. Null when every level is wider (then the top level).
    /// </summary>
    public static ImgcLevel? PreviewLevel(TextureResource tex) =>
        tex.Levels.Where(l => l.Face == 0 && Math.Max(l.Width, l.Height) <= 2048).Cast<ImgcLevel?>().FirstOrDefault();

    /// <summary>
    /// Open a texture the way the viewport loads it: null with the texture when it can be shown (as stored blocks when
    /// the GPU samples the format and it is not a normal map, else decoded), otherwise why not.
    /// </summary>
    public static string? Open(RpackCatalog catalog, string name, bool normal, out TextureResource? texture)
    {
        texture = null;
        if (Find(catalog, name) is not { } gid) return "no loaded pack provides it";
        var (entry, index) = catalog.Split(gid);
        TextureResource tex;
        try { tex = TextureResource.Open(entry.Pack!, index); }
        catch (Exception e) when (e is ImgcException or RpackFormatException) { return e.Message; }
        if (tex.PayloadProblem is { } why) return why;
        if (tex.TopLevel is null) return tex.Header.HeaderOnly ? $"header-only, refers to '{tex.Header.Reference}'" : "no levels";
        if ((normal || !GpuSamples(tex.Header)) && TextureDecoder.Reason(tex.Header.Format) is { } refused) return refused;
        texture = tex;
        return null;
    }
}
