using Nightrunner.Core.Mesh;
using Nightrunner.Core.Rpack;
using Nightrunner.Core.Sdb;

namespace Nightrunner.Core.Model;

public sealed record ResolvedModel(string Name, ModelDocument Doc, string? Skeleton, int[] SkeletonGids,
                                   IReadOnlyList<ResolvedSlot> Slots, IReadOnlyList<string> Notes);

public sealed record ResolvedSlot(ModelSlot Slot, IReadOnlyList<ResolvedMesh> Meshes, string? Problem);

/// <summary>
/// One mesh entry of a slot. <see cref="Drawn"/> marks the entry the game draws (the first — the engine ignores
/// <c>selected</c>, RT); <see cref="Chosen"/> is the prototype's pick (selected, else first).
/// </summary>
public sealed record ResolvedMesh(ModelMesh Entry, bool Drawn, bool Chosen, int[] Gids, int Skin, string? Error,
                                  IReadOnlyList<ResolvedSubmesh> Submeshes, IReadOnlyList<string> Problems)
{
    public bool Found => Gids.Length > 0;
}

/// <summary>
/// A submesh and the material it ends up with: embedded name → (skin) → materialsData.number →
/// materialsResources[number] selected/first → base .mat + rttiValues → SDB bindings → textures.
/// </summary>
public sealed record ResolvedSubmesh(int? Entry, int? Submesh, int? Slot, int Triangles, string Embedded,
                                     string? SkinMaterial, int? Number, string BaseMaterial, string MaterialSource,
                                     IReadOnlyList<RttiValue> Rtti, string? JoinError, bool InSdb,
                                     IReadOnlyList<ResolvedTexture> Textures);

/// <summary>A bound texture; <see cref="Original"/> is set when an rttiValue (type 7) replaced it.</summary>
public sealed record ResolvedTexture(string Param, string Texture, string? Original, string Source, int[] Gids);

/// <summary>
/// Full resolution of a <c>.model</c> (port of <c>gui/modelresolve.py</c>): skeleton → slots → meshes → submeshes →
/// materials → SDB → textures. Pure and worker-safe; errors are recorded on the row, never thrown.
/// </summary>
public static class ModelResolver
{
    public const byte MeshType = 0x10, TextureType = 0x20;

    /// <param name="decode">Mesh decode by gid (the caller's cache).</param>
    /// <param name="skinOf">The skin a mesh (by gid) is drawn with; −1 = its own materials.</param>
    public static ResolvedModel Resolve(ModelDocument doc, RpackCatalog catalog, SdbFile? sdb,
                                        Func<int, MeshModel> decode, Func<int, MeshModel, int>? skinOf = null)
    {
        var notes = new List<string>();
        int[] skel = doc.Skeleton is { Length: > 0 } s ? Lookup(catalog, MeshKey(s), MeshType) : [];
        var sdbCache = new Dictionary<string, SdbTextures>(StringComparer.OrdinalIgnoreCase);
        var slots = new List<ResolvedSlot>();
        foreach (var slot in doc.Slots)
        {
            string? problem = slot.Meshes.Count(m => m.Selected) switch
            {
                > 1 => "several entries are 'selected'",
                0 when slot.Meshes.Count > 0 => null,
                _ => null,
            };
            if (slot.Meshes.Count > 1) problem = (problem is null ? "" : problem + "; ") + "more than one entry: the game draws the first";
            var meshes = slot.Meshes.Select(m => ResolveMesh(m, m == slot.Drawn, m == slot.Chosen, catalog, sdb, decode, skinOf, sdbCache)).ToList();
            slots.Add(new ResolvedSlot(slot, meshes, problem));
        }
        return new ResolvedModel(doc.Name, doc, doc.Skeleton, skel, slots, notes);
    }

    private static string MeshKey(string name) => name.EndsWith(".msh", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    public static int[] Lookup(RpackCatalog catalog, string name, byte type)
    {
        if (string.IsNullOrEmpty(name)) return [];
        try { return catalog.Lookup(name, type); }
        catch (ObjectDisposedException) { return []; }
    }

    private static ResolvedMesh ResolveMesh(ModelMesh r, bool drawn, bool chosen, RpackCatalog catalog, SdbFile? sdb,
                                            Func<int, MeshModel> decode, Func<int, MeshModel, int>? skinOf,
                                            Dictionary<string, SdbTextures> sdbCache)
    {
        var gids = Lookup(catalog, r.MeshName, MeshType);
        var problems = new List<string>();
        string? error = null;
        MeshModel? mesh = null;
        int skin = -1;
        if (gids.Length > 0)
        {
            try
            {
                mesh = decode(gids[0]);
                skin = skinOf?.Invoke(gids[0], mesh) ?? -1;
            }
            catch (Exception e) when (e is MeshFormatException or MeshUnsupportedException or RpackFormatException)
            {
                error = $"mesh decode failed: {e.Message}";
            }
        }

        // submesh rows: from the decoded mesh, else what the model says the mesh embeds
        var rows = new List<(int? Entry, int? Sub, int? Slot, int Tris, string Material, string? SkinMaterial)>();
        if (mesh is not null)
        {
            string[] table = mesh.FullMaterialTable();
            int[]? map = null;
            if (skin >= 0 && mesh.SkinRaw is { } raw && MeshSkins.Decode(raw) is { Error: null } skins)
                map = skins.MaterialsFor(skin, mesh.Materials.Length);
            foreach (var e in mesh.GeometryEntries)
                foreach (var sm in e.Submeshes)
                {
                    string embedded = mesh.MaterialName(sm.MaterialSlot);
                    string? viaSkin = map is not null && sm.MaterialSlot < map.Length && map[sm.MaterialSlot] < table.Length &&
                                      !table[map[sm.MaterialSlot]].Equals(embedded, StringComparison.OrdinalIgnoreCase)
                        ? table[map[sm.MaterialSlot]] : null;
                    rows.Add((e.Index, sm.Index, sm.MaterialSlot, sm.TriangleCount, embedded, viaSkin));
                }
        }
        else
            rows.AddRange(r.MaterialsData.Select(md => ((int?)null, (int?)null, (int?)null, 0, md.Name, (string?)null)));

        var subs = new List<ResolvedSubmesh>();
        foreach (var row in rows)
        {
            string lookup = row.SkinMaterial ?? row.Material;
            var (number, choice, alternatives, joinError) = Join(r, lookup);
            string baseMat = choice?.Name ?? lookup;
            string source = choice is null ? (row.SkinMaterial is not null ? "skin" : "mesh default")
                : row.SkinMaterial is not null ? "skin"
                : choice.Name.Equals(lookup, StringComparison.OrdinalIgnoreCase) ? "model" : "model (remap)";
            var rtti = choice?.RttiValues ?? [];
            var tex = Textures(catalog, sdb, baseMat, rtti, sdbCache, out bool inSdb);
            subs.Add(new ResolvedSubmesh(row.Entry, row.Sub, row.Slot, row.Tris, row.Material, row.SkinMaterial, number,
                                         baseMat, source, rtti, joinError, inSdb, tex));
        }
        if (mesh is not null)
        {
            var seen = rows.Select(x => x.Material).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unused = r.MaterialsData.Select(md => md.Name)
                .Where(n => n.Length > 0 && !seen.Contains(n) && !n.StartsWith("auto_shadow_caster", StringComparison.OrdinalIgnoreCase)
                            && !n.StartsWith("shadowcaster", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (unused.Count > 0) problems.Add($"materialsData names not used by any submesh: {string.Join(", ", unused)}");
        }
        return new ResolvedMesh(r, drawn, chosen, gids, skin, error, subs, problems);
    }

    /// <summary>The join rule (B): name → materialsData.number → materialsResources[number] → selected/first.</summary>
    public static (int? Number, ModelMaterialResource? Choice, IReadOnlyList<string> Alternatives, string? Error)
        Join(ModelMesh r, string material)
    {
        var hits = r.MaterialsData.Where(md => md.Name.Equals(material, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0) return (null, null, [], null);
        if (hits.Count > 1) return (null, null, [], $"'{material}' listed {hits.Count} times in materialsData");
        int? num = hits[0].Number;
        var groups = r.MaterialsResources.Where(g => g.Number == num).ToList();
        if (groups.Count == 0) return (num, null, [], $"no materialsResources group for number {num}");
        string? error = groups.Count > 1 ? $"materialsResources number {num} is duplicated; first group used" : null;
        var choice = groups[0].Chosen;
        if (choice is null) return (num, null, [], $"materialsResources group {num} is empty");
        if (groups[0].Resources.Count(x => x.Selected) > 1) error = "several resources are 'selected'; first used";
        return (num, choice, groups[0].Resources.Where(x => x != choice).Select(x => x.Name).ToList(), error);
    }

    private sealed record SdbTextures(bool Found, List<(string Param, string Texture, string Source)> Bindings);

    /// <summary>SDB bindings of a material (first texture per parameter over every shader variant), then rttiValues.</summary>
    public static IReadOnlyList<ResolvedTexture> Textures(RpackCatalog catalog, SdbFile? sdb, string material,
                                                           IReadOnlyList<RttiValue> rtti, out bool inSdb)
        => Textures(catalog, sdb, material, rtti, new Dictionary<string, SdbTextures>(StringComparer.OrdinalIgnoreCase), out inSdb);

    private static IReadOnlyList<ResolvedTexture> Textures(RpackCatalog catalog, SdbFile? sdb, string material,
                                                            IReadOnlyList<RttiValue> rtti,
                                                            Dictionary<string, SdbTextures> cache, out bool inSdb)
    {
        if (!cache.TryGetValue(material, out var info))
        {
            var bindings = new List<(string, string, string)>();
            bool found = false;
            if (sdb is not null && material.Length > 0)
            {
                var hits = sdb.FindMaterial(material);
                if (hits.Count == 1)
                {
                    found = true;
                    var m = sdb.Material(hits[0]);
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var b in m.Routes.SelectMany(x => x.Variants).SelectMany(v => v.Bindings))
                    {
                        if (string.IsNullOrEmpty(b.Texture)) continue;
                        string p = b.Parameter is { Length: > 0 } pp ? pp : $"binding_{b.Slot}";
                        if (seen.Add(p)) bindings.Add((p, b.Texture, b.Overridden ? "sdb override" : "sdb shader default"));
                    }
                }
            }
            cache[material] = info = new SdbTextures(found, bindings);
        }
        inSdb = info.Found;
        var rows = info.Bindings.Select(b => new ResolvedTexture(b.Param, b.Texture, null, b.Source, [])).ToList();
        foreach (var o in rtti.Where(o => o.Type == 7))
        {
            int i = rows.FindIndex(r => r.Param.Equals(o.Name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) rows[i] = new ResolvedTexture(rows[i].Param, o.Value, rows[i].Texture, "model override", []);
            else rows.Add(new ResolvedTexture(o.Name, o.Value, null, "model override (unbound)", []));
        }
        return rows.Select(r => r with { Gids = Lookup(catalog, r.Texture, TextureType) }).ToList();
    }
}
