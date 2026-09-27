using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Nightrunner.Core.Cast;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Project;
using CastModel = Nightrunner.Core.Cast.Model;
using CastMesh = Nightrunner.Core.Cast.Mesh;
using File = System.IO.File;

namespace Nightrunner.UI.Views.Inspectors;

/// <summary>
/// A project's model scene: every object of the edited scene and the exported submesh it replaces. Objects named like
/// the export (<c>&lt;slot&gt;.&lt;mesh&gt;.eN.sM</c>, Blender <c>.NNN</c> tolerated) map themselves; any other object must be
/// assigned to a target or skipped before the build. Saved to <c>split.json</c> (<c>assign</c>).
/// </summary>
public sealed partial class ProjectSceneInspector : UserControl
{
    private const string Auto = "(auto)", Skip = "(skip)";
    private readonly StackPanel _rows = new();
    private readonly TextBlock _info = new() { Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 6, 0, 0) };
    private SceneItem? _item;
    private readonly Dictionary<string, ComboBox> _choices = [];

    [GeneratedRegex(@"\.e\d+\.s\d+(\.\d{3})?$")]
    private static partial Regex ExportName();

    public ProjectSceneInspector()
    {
        _info.SetResourceReference(StyleProperty, "Dim");
        _status.SetResourceReference(StyleProperty, "Dim");
        var save = new Button { Content = "Save", Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        save.Click += (_, _) => Save();
        var root = new DockPanel();
        DockPanel.SetDock(_info, Dock.Top);
        DockPanel.SetDock(save, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_info);
        root.Children.Add(save);
        root.Children.Add(_status);
        root.Children.Add(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    public ProjectSceneInspector With(Selected.ProjectScene what)
    {
        _item = what.Item;
        Load();
        return this;
    }

    private void Load()
    {
        _rows.Children.Clear();
        _choices.Clear();
        if (_item is null) return;
        try
        {
            var report = JsonNode.Parse(File.ReadAllText(_item.ReportPath))!.AsObject();
            var targets = (report["mesh_map"] as JsonArray ?? []).OfType<JsonObject>().Select(m => (string?)m["name"] ?? "").ToList();
            var cast = Gltf.Load(_item.ScenePath);
            var objects = cast.Roots().SelectMany(r => r.ChildrenOfType<CastModel>()).SelectMany(m => m.ChildrenOfType<CastMesh>())
                .Select(m => m.Property("n")?.StringValue ?? "").Where(n => n.Length > 0).ToList();
            var assign = _item.Options["assign"] as JsonObject ?? [];
            int open = 0;
            foreach (var o in objects)
            {
                bool auto = ExportName().IsMatch(o);
                var combo = new ComboBox { ItemsSource = new[] { Auto, Skip }.Concat(targets).ToList(), MinWidth = 260 };
                string? set = (string?)assign[o] ?? (string?)assign[Regex.Replace(o, @"\.\d{3}$", "")];
                combo.SelectedItem = set is null ? Auto : set.Length == 0 ? Skip : set;
                if (!auto && set is null) open++;
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var name = new TextBlock { Text = o, Width = 220, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = o };
                if (!auto && set is null) name.Foreground = Skin.Brush("Warn");
                DockPanel.SetDock(name, Dock.Left);
                row.Children.Add(name);
                row.Children.Add(combo);
                _rows.Children.Add(row);
                _choices[o] = combo;
            }
            _info.Text = $"{Path.GetFileName(_item.ScenePath)}  ·  {objects.Count} objects  ·  {targets.Count} targets" +
                         (open > 0 ? $"  ·  {open} unassigned" : "");
            _status.Text = "";
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or CastFormatException or GltfException)
        {
            _status.Text = e.Message;
        }
    }

    private void Save()
    {
        if (_item is null) return;
        var assign = new JsonObject();
        foreach (var (o, combo) in _choices)
            if (combo.SelectedItem is string s && s != Auto) assign[o] = s == Skip ? "" : s;
        _item.Options["assign"] = assign;
        File.WriteAllText(Path.Combine(_item.Folder, ProjectAssets.SplitSettings), _item.Options.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Log.Info("project", $"saved assign for {Path.GetFileName(_item.Folder)}: {assign.Count} object(s)");
        Load();
    }
}
