using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Model;
using Nightrunner.Core.Project;

namespace Nightrunner.UI.Views.Inspectors;

/// <summary>
/// A project's <c>.model</c> override: per slot on/off and the mesh it draws (any listed entry, a stashed one, a mesh
/// the project builds, or a typed name); per material of that entry the base <c>.mat</c> and the rttiValues
/// (<c>param=value; …</c> — text is a texture, a number a float, three numbers a vec3). Save writes the document; the
/// build writes one entry per slot (<see cref="ModelEdit.GameDoc"/>).
/// </summary>
public sealed class ProjectModelInspector : UserControl
{
    private readonly StackPanel _slots = new();
    private readonly StackPanel _materials = new();
    private readonly TextBlock _info = new() { Style = null, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _paths = new() { Width = 100, ItemsSource = new[] { "root", "original", "both" } };
    private readonly CheckBox _noGear = new() { Content = "No gear", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                                                ToolTip = "Ship an empty player_outfit_slots.scr so gear stops swapping slots" };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 6, 0, 0) };
    private ModProject? _project;
    private ModelItem? _item;
    private JsonObject? _doc;
    private string? _slot;

    public ProjectModelInspector()
    {
        _info.SetResourceReference(StyleProperty, "Dim");
        _status.SetResourceReference(StyleProperty, "Dim");
        var save = new Button { Content = "Save", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0) };
        save.Click += (_, _) => Save();
        var revert = new Button { Content = "Revert", Padding = new Thickness(12, 4, 12, 4) };
        revert.Click += (_, _) => Load();
        _paths.SelectionChanged += (_, _) => { if (_item is not null && _paths.SelectedItem is string m) { _item.Settings["paths"] = m; Dirty(); } };
        _noGear.Click += (_, _) => { if (_item is not null) { _item.Settings["noGear"] = _noGear.IsChecked == true; Dirty(); } };

        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(save);
        bar.Children.Add(revert);
        var pathsLabel = new Label { Content = "paths", Margin = new Thickness(12, 0, 0, 0) };
        bar.Children.Add(pathsLabel);
        bar.Children.Add(_paths);
        bar.Children.Add(_noGear);
        bar.Children.Add(new TextBlock());
        foreach (UIElement c in bar.Children) DockPanel.SetDock(c, Dock.Left);

        var root = new DockPanel();
        DockPanel.SetDock(_info, Dock.Top);
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_info);
        root.Children.Add(bar);
        root.Children.Add(_status);
        var split = new Grid();
        split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        var top = new ScrollViewer { Content = _slots, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var bottom = new ScrollViewer { Content = _materials, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(bottom, 1);
        split.Children.Add(top);
        split.Children.Add(bottom);
        root.Children.Add(split);
        Content = root;
    }

    public ProjectModelInspector With(Selected.ProjectModel what)
    {
        if (ReferenceEquals(_item, what.Item)) return this;
        _project = what.Owner;
        _item = what.Item;
        Load();
        return this;
    }

    private void Load()
    {
        if (_item is null) return;
        try
        {
            _doc = _item.Load();
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException)
        {
            _status.Text = e.Message;
            return;
        }
        _info.Text = $"{Path.GetFileName(_item.DocPath)}  ·  {_item.Member}  ·  {_item.Pak}";
        _paths.SelectedItem = _item.PathMode;
        _noGear.IsChecked = _item.NoGear;
        _slot ??= ModelEdit.Slots(_doc).OfType<JsonObject>().Select(s => (string?)s["name"]).FirstOrDefault();
        ShowSlots();
        ShowMaterials();
        _status.Text = "";
    }

    private void Dirty()
    {
        _status.Text = "unsaved";
    }

    private void Save()
    {
        if (_item is null || _doc is null) return;
        try
        {
            _item.Save(_doc);
            _status.Text = "saved";
            Log.Info("project", $"saved {_item.Member} ({Path.GetFileName(_item.Folder)})");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _status.Text = e.Message;
        }
    }

    /// <summary>Mesh names a slot can take: its entries, its stash, and every mesh the project builds.</summary>
    private List<string> Alternatives(JsonObject slot)
    {
        var names = ModelEdit.Resources(slot).OfType<JsonObject>().Select(r => (string?)r["name"] ?? "").ToList();
        if (_item!.Stash[(string?)slot["name"] ?? ""] is JsonArray stashed) names.AddRange(stashed.OfType<JsonObject>().Select(r => (string?)r["name"] ?? ""));
        if (_project is not null) names.AddRange(ProjectAssets.Scan(_project).Meshes.Select(m => m.OutputName + ".msh"));
        return names.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void ShowSlots()
    {
        _slots.Children.Clear();
        foreach (var slot in ModelEdit.Slots(_doc!).OfType<JsonObject>())
        {
            string name = (string?)slot["name"] ?? "";
            var res = ModelEdit.Resources(slot);
            var chosen = res.OfType<JsonObject>().FirstOrDefault(r => ModelDocument.Flag(r["selected"])) ?? res.OfType<JsonObject>().FirstOrDefault();
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var on = new CheckBox { IsChecked = res.Count > 0, Width = 22, VerticalAlignment = VerticalAlignment.Center };
            on.Click += (_, _) =>
            {
                ModelEdit.SetSlotEnabled(_doc!, name, on.IsChecked == true, _item!.Stash);
                Dirty();
                ShowSlots();
                ShowMaterials();
            };
            var label = new Button
            {
                Content = name, Width = 150, HorizontalContentAlignment = HorizontalAlignment.Left,
                FontWeight = name == _slot ? FontWeights.SemiBold : FontWeights.Normal, ToolTip = "Show this slot's materials",
            };
            label.Click += (_, _) => { _slot = name; ShowSlots(); ShowMaterials(); };
            var alternatives = Alternatives(slot);
            string current = (string?)chosen?["name"] ?? "";
            bool enabled = res.Count > 0 || _item!.Stash[name] is not null;
            var mesh = new ComboBox { ItemsSource = alternatives, MinWidth = 220, IsEnabled = enabled };
            mesh.SelectedItem = alternatives.FirstOrDefault(a => a.Equals(current, StringComparison.OrdinalIgnoreCase));
            var other = new TextBox { Width = 120, Margin = new Thickness(6, 0, 0, 0), IsEnabled = enabled, ToolTip = "other mesh name" };
            void Apply(string text)
            {
                text = text.Trim();
                if (text.Length == 0 || text.Equals(current, StringComparison.OrdinalIgnoreCase)) return;
                ModelEdit.SetSlotMesh(_doc!, name, text, _item!.Stash);
                Dirty();
                _slot = name;
                ShowSlots();
                ShowMaterials();
            }
            mesh.SelectionChanged += (_, _) => { if (mesh.SelectedItem is string s) Apply(s); };
            other.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Apply(other.Text); };
            DockPanel.SetDock(other, Dock.Right);
            DockPanel.SetDock(on, Dock.Left);
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(on);
            row.Children.Add(label);
            row.Children.Add(other);
            row.Children.Add(mesh);
            _slots.Children.Add(row);
        }
    }

    private void ShowMaterials()
    {
        _materials.Children.Clear();
        if (_doc is null || _slot is null) return;
        JsonObject? entry;
        try
        {
            var res = ModelEdit.Resources(ModelEdit.Slot(_doc, _slot));
            entry = res.OfType<JsonObject>().FirstOrDefault(r => ModelDocument.Flag(r["selected"])) ?? res.OfType<JsonObject>().FirstOrDefault();
        }
        catch (ModelFormatException) { return; }
        var header = new TextBlock { Text = entry is null ? $"{_slot}: off" : $"{_slot}: {(string?)entry["name"]}", Margin = new Thickness(0, 0, 0, 4) };
        header.SetResourceReference(StyleProperty, "Dim");
        _materials.Children.Add(header);
        if (entry is null) return;
        foreach (var md in (entry["materialsData"] as JsonArray ?? []).OfType<JsonObject>())
        {
            string material = (string?)md["name"] ?? "";
            var ent = ModelEdit.MaterialEntry(entry, material, create: false);
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var name = new TextBlock { Text = material, Width = 200, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = material };
            var baseBox = new TextBox { Text = (string?)ent?["name"] ?? material, Width = 200, ToolTip = "base .mat" };
            var rtti = new TextBox { Text = RttiText(ent), ToolTip = "param=value; …  (texture name, float, or x,y,z)" };
            baseBox.LostKeyboardFocus += (_, _) =>
            {
                string b = baseBox.Text.Trim();
                if (b.Length == 0 || b == ((string?)ent?["name"] ?? material)) return;
                ent = ModelEdit.MaterialEntry(entry, material, b);
                Dirty();
            };
            rtti.LostKeyboardFocus += (_, _) =>
            {
                if (rtti.Text == RttiText(ent)) return;
                try
                {
                    ent ??= ModelEdit.MaterialEntry(entry, material);
                    foreach (var old in (ent!["rttiValues"] as JsonArray ?? []).OfType<JsonObject>().Select(v => (string?)v["name"]).ToList())
                        ModelEdit.SetRtti(ent, old ?? "", null);
                    foreach (var (param, value) in ParseRtti(rtti.Text)) ModelEdit.SetRtti(ent, param, value);
                    Dirty();
                }
                catch (ModelFormatException e) { _status.Text = e.Message; }
            };
            DockPanel.SetDock(name, Dock.Left);
            DockPanel.SetDock(baseBox, Dock.Left);
            row.Children.Add(name);
            row.Children.Add(baseBox);
            row.Children.Add(rtti);
            _materials.Children.Add(row);
        }
    }

    private static string RttiText(JsonObject? ent) =>
        string.Join("; ", (ent?["rttiValues"] as JsonArray ?? []).OfType<JsonObject>().Select(v =>
            $"{(string?)v["name"]}={(v["val_str"] ?? v["val_float"] ?? v["val_vec3"]) switch
            {
                JsonArray a => string.Join(",", a.Select(x => x?.ToJsonString())),
                JsonNode n => n.GetValueKind() == System.Text.Json.JsonValueKind.String ? (string?)n : n.ToJsonString(),
                null => "",
            }}"));

    /// <summary><c>a=x.png; b=0.5; c=1,0,0</c> → (a, "x.png"), (b, 0.5), (c, [1,0,0]).</summary>
    public static IEnumerable<(string Param, object Value)> ParseRtti(string text)
    {
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) throw new ModelFormatException($"'{part}': param=value expected");
            string param = part[..eq].Trim(), value = part[(eq + 1)..].Trim();
            var nums = value.Split(',', StringSplitOptions.TrimEntries);
            if (nums.Length == 3 && nums.All(n => double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                yield return (param, nums.Select(n => double.Parse(n, CultureInfo.InvariantCulture)).ToArray().AsEnumerable());
            else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                yield return (param, d);
            else yield return (param, value);
        }
    }
}
