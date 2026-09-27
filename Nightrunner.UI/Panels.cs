using System.Windows.Controls;
using Nightrunner.UI.Views;

namespace Nightrunner.UI;

/// <summary>Where a window sits when it is opened fresh.</summary>
public enum PanelPlacement
{
    /// <summary>A tab in the middle — the thing being worked on.</summary>
    Document,

    /// <summary>Docked to the right, like the Inspector.</summary>
    Right,

    /// <summary>Docked along the bottom, like the Log.</summary>
    Bottom,
}

/// <summary>
/// One dockable window. Everything the shell needs to offer it in the Windows menu and build it on demand.
/// </summary>
/// <param name="Id">Stable id — also AvalonDock's ContentId, so a saved layout can find it again.</param>
/// <param name="Title">What the tab says.</param>
/// <param name="Create">Builds the view. Called when the window is opened, not before.</param>
public sealed record PanelDef(string Id, string Title, Func<PanelContext, UserControl> Create)
{
    public PanelPlacement Placement { get; init; } = PanelPlacement.Document;

    /// <summary>Opened by the built-in "everything" layout; the saved layouts decide for themselves.</summary>
    public bool OpenByDefault { get; init; } = true;

    /// <summary>Width (Right) or height (Bottom) the pane asks for when it is first docked.</summary>
    public double Extent { get; init; } = 460;
}

/// <summary>
/// How a panel reaches another one. The selection bus only talks to the Inspector; this is for the cases where
/// the answer belongs in a different window — "show this texture in Textures", "open Texture usage".
/// </summary>
public interface IPanelHost
{
    /// <summary>Open the panel if it is closed, then bring it to the front.</summary>
    void Open(string panelId);

    /// <summary>Open it and hand it something to show. A panel that does not understand the payload ignores it.</summary>
    void Reveal(string panelId, object payload);
}

/// <summary>What a panel is handed when it is built: the open game, the shared selection, and the shell.</summary>
public sealed record PanelContext(Workspace Workspace, Selection Selection, IPanelHost Host);

/// <summary>Every window the app can show. Adding an area is one row here plus its view.</summary>
public static class Panels
{
    public static readonly PanelDef[] All =
    [
        new("raw", "Raw", c => new RawExplorerView(c)),
        new("textures", "Textures", c => new TexturesView(c)),
        new("materials", "Materials", c => new MaterialsView(c)) { OpenByDefault = false },
        new("meshes", "Meshes", c => new MeshesView(c)) { OpenByDefault = false },
        new("models", "Models", c => new ModelsView(c)) { OpenByDefault = false },
        new("prefabs", "Prefabs", c => new PrefabsView(c)) { OpenByDefault = false },
        new("animations", "Animations", c => new AnimationsView(c)) { OpenByDefault = false },
        new("audio", "Audio", c => new AudioExplorerView(c)) { OpenByDefault = false },
        new("viewport", "Viewport", c => new ViewportView(c)) { OpenByDefault = false },
        new("projects", "Projects", c => new ProjectsView(c)) { OpenByDefault = false },
        new("build", "Build", c => new BuildView(c)) { OpenByDefault = false },
        new("mods", "Mods", c => new ModsView(c)) { OpenByDefault = false },
        new("inspector", "Inspector", c => new InspectorView(c))
        {
            Placement = PanelPlacement.Right,
            Extent = 480,
        },
        new("log", "Log", _ => new LogView())
        {
            Placement = PanelPlacement.Bottom,
            OpenByDefault = false,
            Extent = 220,
        },
    ];

    public static PanelDef? ById(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
