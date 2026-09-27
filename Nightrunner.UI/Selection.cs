using Nightrunner.Core.Project;
using Nightrunner.Core.Rpack;
using Nightrunner.Core.Sdb;

namespace Nightrunner.UI;

/// <summary>What a panel has selected. The Inspector shows whichever of these arrives last.</summary>
public abstract record Selected
{
    /// <summary>A pack: its header and storage table.</summary>
    public sealed record Pack(PackEntry Entry) : Selected;

    /// <summary>One resource: its parts, and the bytes of whichever part is picked.</summary>
    public sealed record Resource(RpackCatalog Catalog, int Gid) : Selected;

    /// <summary>Several resources: just the totals.</summary>
    public sealed record Resources(RpackCatalog Catalog, int[] Gids) : Selected;

    /// <summary>A texture: the decoded image and its IMGC header.</summary>
    public sealed record Texture(RpackCatalog Catalog, int Gid) : Selected;

    /// <summary>A mesh: the Viewport draws it, the Inspector shows its entries, materials and skins.</summary>
    public sealed record Mesh(RpackCatalog Catalog, int Gid) : Selected;

    /// <summary>A <c>.model</c>: the Viewport assembles it, the Inspector shows slots → meshes → materials → textures.</summary>
    public sealed record Model(Nightrunner.Core.Model.ModelCatalog Models, Nightrunner.Core.Model.ModelEntry Entry) : Selected;

    /// <summary>A SeqTrack of a sequence bank: the Viewport plays its clip, the Inspector shows its record and events.</summary>
    public sealed record Sequence(Nightrunner.Core.Anim.AnimCatalog Animations, Nightrunner.Core.Anim.SeqEntry Entry) : Selected;

    /// <summary>
    /// A binary prefab: the Inspector shows its components, child entities, fields and presets; the Viewport draws it,
    /// with its class preset <paramref name="Presets"/> (<c>group;preset</c>) applied when one is picked.
    /// </summary>
    public sealed record Prefab(Nightrunner.Core.Prefab.PrefabCatalog Prefabs, Nightrunner.Core.Prefab.PrefabEntry Entry,
                                string? Presets = null) : Selected;

    /// <summary>A project: name, path, notes.</summary>
    public sealed record Project(ModProject Value) : Selected;

    /// <summary>A project's <c>.model</c> override: the Inspector edits its slots, entries and material overrides.</summary>
    public sealed record ProjectModel(ModProject Owner, ModelItem Item) : Selected;

    /// <summary>A project's model scene: the Inspector maps its objects onto the exported submeshes.</summary>
    public sealed record ProjectScene(ModProject Owner, SceneItem Item) : Selected;

    /// <summary>
    /// A project item as the Build window sees it: output, form, template, source, edits, and what the last build and
    /// the last check said about it.
    /// </summary>
    public sealed record BuildItem(ModProject Owner, BuildItemInfo Info, BuildItemState? Last, BuildItemState? Check) : Selected;

    /// <summary>
    /// A material: its preset, the values it overrides, and the textures it draws with. Carries the index it was
    /// resolved against, so the Inspector never reads a database that was swapped out underneath it.
    /// </summary>
    public sealed record Material(SdbIndex Index, int MaterialIndex) : Selected;

    /// <summary>
    /// A runtime mod: its manifest, switch, items, what it overrides (null: not computed yet), whether it loads, and the
    /// runtime check it was read with.
    /// </summary>
    public sealed record Mod(Nightrunner.Core.Games.RuntimeMod Value, Nightrunner.Core.Games.RuntimeCheck? Runtime,
                             IReadOnlyList<Nightrunner.Core.Games.RuntimeOverride>? Overrides, string Loads) : Selected;
}

/// <summary>
/// The one selection the Inspector follows. Panels publish; the Inspector subscribes. Nothing else couples the
/// two, so a new panel only has to call <see cref="Set"/> to get an inspector.
/// </summary>
public sealed class Selection
{
    public Selected? Current { get; private set; }

    /// <summary>The panel id that published the current selection, so the Inspector can say where it came from.</summary>
    public string Source { get; private set; } = "";

    public event Action<Selected?, string>? Changed;

    public void Set(string source, Selected? what)
    {
        Current = what;
        Source = source;
        Changed?.Invoke(what, source);
    }

    /// <summary>Drop the selection when its panel closes or its game goes away.</summary>
    public void ClearFrom(string source)
    {
        if (Source == source) Set(source, null);
    }
}
