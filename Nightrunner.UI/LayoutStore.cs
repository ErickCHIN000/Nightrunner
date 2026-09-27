using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nightrunner.Core.Logging;

namespace Nightrunner.UI;

/// <summary>
/// One node of the document area: either a pane holding tabs, or a split holding more nodes.
/// </summary>
/// <remarks>
/// This is what makes a saved view reproduce a side-by-side arrangement rather than collapsing everything into
/// one tab strip. A leaf carries its panel ids; a split carries an orientation and its children, each with the
/// weight the splitter left it at.
/// </remarks>
public sealed class DocNode
{
    /// <summary>"Horizontal" or "Vertical" for a split; null for a pane.</summary>
    [JsonPropertyName("orientation")] public string? Orientation { get; set; }

    [JsonPropertyName("children")] public List<DocNode>? Children { get; set; }

    /// <summary>Panel ids in this pane, in tab order.</summary>
    [JsonPropertyName("panels")] public List<string>? Panels { get; set; }

    /// <summary>Which tab of this pane is in front.</summary>
    [JsonPropertyName("active")] public string? Active { get; set; }

    /// <summary>How much of the split this node takes.</summary>
    [JsonPropertyName("size")] public double Size { get; set; } = 1;

    /// <summary>True for a proportional size (a `*` length), false for pixels.</summary>
    [JsonPropertyName("star")] public bool Star { get; set; } = true;

    [JsonIgnore] public bool IsSplit => Children is { Count: > 0 };

    [JsonIgnore]
    public IEnumerable<string> AllPanels => IsSplit
        ? Children!.SelectMany(c => c.AllPanels)
        : Panels ?? [];

    public DocNode Copy() => new()
    {
        Orientation = Orientation,
        Children = Children?.Select(c => c.Copy()).ToList(),
        Panels = Panels is null ? null : [.. Panels],
        Active = Active,
        Size = Size,
        Star = Star,
    };

    public static DocNode Pane(params string[] panels) => new() { Panels = [.. panels], Active = panels.FirstOrDefault() };
}

/// <summary>
/// A named arrangement of windows: how the document area is split, what is docked right and along the bottom,
/// and how big the side panes are.
/// </summary>
public sealed class WorkspaceView
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>The document area. Null falls back to <see cref="Documents"/> as one pane.</summary>
    [JsonPropertyName("layout")] public DocNode? Layout { get; set; }

    /// <summary>Flat list used by the built-ins (and by views saved before splits were captured).</summary>
    [JsonPropertyName("documents")] public List<string> Documents { get; set; } = [];

    [JsonPropertyName("right")] public List<string> Right { get; set; } = [];
    [JsonPropertyName("bottom")] public List<string> Bottom { get; set; } = [];

    [JsonPropertyName("rightWidth")] public double RightWidth { get; set; } = 480;
    [JsonPropertyName("bottomHeight")] public double BottomHeight { get; set; } = 220;

    /// <summary>Which document is in front when the view has no split of its own.</summary>
    [JsonPropertyName("active")] public string? Active { get; set; }

    [JsonIgnore] public bool BuiltIn { get; init; }

    /// <summary>The document area as a tree, whichever way this view stored it.</summary>
    [JsonIgnore]
    public DocNode DocumentTree => Layout ?? new DocNode { Panels = [.. Documents], Active = Active };

    [JsonIgnore]
    public IEnumerable<string> AllPanels => DocumentTree.AllPanels.Concat(Right).Concat(Bottom);

    public WorkspaceView Copy(string newName) => new()
    {
        Name = newName,
        Layout = Layout?.Copy(),
        Documents = [.. Documents],
        Right = [.. Right],
        Bottom = [.. Bottom],
        RightWidth = RightWidth,
        BottomHeight = BottomHeight,
        Active = Active,
    };
}

/// <summary>
/// The workspace views a user can switch between: a few that ship with the app, plus whatever they save, kept
/// beside the other settings.
/// </summary>
/// <remarks>
/// Deliberately not AvalonDock's layout XML — version 5 ships no public serializer — but the description is now
/// structural: splits, tab order, which tab is in front, and the side-pane sizes all come back.
/// </remarks>
public sealed class LayoutStore
{
    public static readonly WorkspaceView[] BuiltIn =
    [
        new() { Name = "Explore", Documents = ["raw"], Right = ["inspector"], BuiltIn = true },
        new() { Name = "Textures", Documents = ["textures"], Right = ["inspector"], BuiltIn = true },
        new()
        {
            Name = "Modding",
            Layout = new DocNode
            {
                Orientation = "Horizontal",
                Children =
                [
                    new DocNode { Panels = ["projects", "build", "mods"], Active = "projects", Size = 1 },
                    new DocNode { Panels = ["textures"], Active = "textures", Size = 1.2 },
                ],
            },
            Right = ["inspector"],
            Bottom = ["log"],
            BuiltIn = true,
        },
        new()
        {
            Name = "Materials",
            Layout = new DocNode
            {
                Orientation = "Horizontal",
                Children =
                [
                    new DocNode { Panels = ["materials"], Active = "materials", Size = 1 },
                    new DocNode { Panels = ["textures"], Active = "textures", Size = 1 },
                ],
            },
            Right = ["inspector"],
            RightWidth = 640,
            Bottom = ["log"],
            BuiltIn = true,
        },
        new()
        {
            Name = "Meshes",
            Layout = new DocNode
            {
                Orientation = "Horizontal",
                Children =
                [
                    new DocNode { Panels = ["meshes"], Active = "meshes", Size = 1 },
                    new DocNode { Panels = ["viewport"], Active = "viewport", Size = 1.6 },
                ],
            },
            Right = ["inspector"],
            RightWidth = 520,
            Bottom = ["log"],
            BuiltIn = true,
        },
        new()
        {
            Name = "Models",
            Layout = new DocNode
            {
                Orientation = "Horizontal",
                Children =
                [
                    new DocNode { Panels = ["models"], Active = "models", Size = 1 },
                    new DocNode { Panels = ["viewport"], Active = "viewport", Size = 1.6 },
                ],
            },
            Right = ["inspector"],
            RightWidth = 560,
            Bottom = ["log"],
            BuiltIn = true,
        },
        new()
        {
            Name = "Animations",
            Layout = new DocNode
            {
                Orientation = "Horizontal",
                Children =
                [
                    new DocNode { Panels = ["animations"], Active = "animations", Size = 1 },
                    new DocNode { Panels = ["viewport"], Active = "viewport", Size = 1.6 },
                ],
            },
            Right = ["inspector"],
            RightWidth = 520,
            Bottom = ["log"],
            BuiltIn = true,
        },
        new()
        {
            Name = "Prefabs",
            Layout = new DocNode
            {
                Orientation = "Horizontal",
                Children =
                [
                    new DocNode { Panels = ["prefabs"], Active = "prefabs", Size = 1 },
                    new DocNode { Panels = ["viewport"], Active = "viewport", Size = 1.6 },
                ],
            },
            Right = ["inspector"],
            RightWidth = 560,
            Bottom = ["log"],
            BuiltIn = true,
        },
        new() { Name = "Mods", Documents = ["mods", "raw"], Right = ["inspector"], Bottom = ["log"], BuiltIn = true },
        new()
        {
            Name = "Everything",
            Documents = ["raw", "textures", "materials", "meshes", "models", "prefabs", "animations", "viewport", "projects", "build", "mods"],
            Right = ["inspector"],
            Bottom = ["log"],
            BuiltIn = true,
        },
    ];

    private static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nightrunner", "layouts.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class Stored
    {
        [JsonPropertyName("views")] public List<WorkspaceView> Views { get; set; } = [];
        [JsonPropertyName("default")] public string? Default { get; set; }
    }

    private Stored _stored = new();

    public LayoutStore() => Load();

    /// <summary>The view applied when a game is opened; the first built-in unless the user set one.</summary>
    public string DefaultName
    {
        get => _stored.Default is { } name && Find(name) is not null ? name : BuiltIn[0].Name;
        set
        {
            _stored.Default = value;
            Save();
            Log.Info("layout", $"default workspace view is now '{value}'");
        }
    }

    /// <summary>Built-ins first, then the saved ones.</summary>
    public IEnumerable<WorkspaceView> All => BuiltIn.Concat(_stored.Views);

    public WorkspaceView? Find(string name) =>
        All.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool IsBuiltIn(string name) =>
        BuiltIn.Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Store a view under its name, replacing a saved one that already has it.</summary>
    public void Put(WorkspaceView view)
    {
        if (IsBuiltIn(view.Name))
            throw new InvalidOperationException($"'{view.Name}' is a built-in view — save it under another name");
        _stored.Views.RemoveAll(v => string.Equals(v.Name, view.Name, StringComparison.OrdinalIgnoreCase));
        _stored.Views.Add(view);
        Save();
        Log.Info("layout", $"saved workspace view '{view.Name}': {Describe(view)}");
    }

    private static string Describe(WorkspaceView view)
    {
        var tree = view.DocumentTree;
        string docs = tree.IsSplit
            ? $"{tree.Children!.Count} document panes ({string.Join(" | ", tree.Children.Select(c => string.Join(",", c.AllPanels)))})"
            : $"{tree.AllPanels.Count()} document(s)";
        return $"{docs}, {view.Right.Count} right, {view.Bottom.Count} bottom";
    }

    public void Delete(string name)
    {
        int removed = _stored.Views.RemoveAll(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return;
        if (string.Equals(_stored.Default, name, StringComparison.OrdinalIgnoreCase)) _stored.Default = null;
        Save();
        Log.Info("layout", $"deleted workspace view '{name}'");
    }

    /// <summary>Copy any view — built-in or saved — under a new name.</summary>
    public WorkspaceView? Copy(string name, string newName)
    {
        if (Find(name) is not { } view) return null;
        var copy = view.Copy(newName);
        Put(copy);
        return copy;
    }

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                _stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath), Json) ?? new Stored();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            _stored = new Stored();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_stored, Json), new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("layout", $"could not save {FilePath}: {e.Message}");
        }
    }
}
