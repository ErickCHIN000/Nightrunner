using System.Text;
using System.Windows;
using System.Windows.Controls;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;
using Nightrunner.Core.Sdb;

namespace Nightrunner.UI.Views.Inspectors;

/// <summary>A texture the material draws with, and whether a loaded pack actually provides it.</summary>
public sealed record MaterialTextureRow(string Name, string Slot, string SlotTip, string Status);

/// <summary>A parameter the material sets, or one the preset declares that it leaves alone.</summary>
public sealed record MaterialParameterRow(string Name, string Value, string State, string Tip);

/// <summary>
/// A material: its preset, the textures it draws with, and the parameter values it overrides.
/// </summary>
/// <remarks>
/// Everything here is read-only and will stay that way — there is no SDB writer, so a value baked into the
/// database is a fact about the game rather than something to edit. What a modder can act on is the texture
/// list, which is why it is the first thing on screen.
/// </remarks>
public partial class MaterialInspector : UserControl
{
    private readonly Workspace _ws;
    private readonly IPanelHost _host;

    private SdbIndex? _index;
    private int _material = -1;
    private SdbMaterial? _resolved;

    public MaterialInspector(Workspace workspace, IPanelHost host)
    {
        _ws = workspace;
        _host = host;
        InitializeComponent();
        Textures.SizeChanged += (_, _) => Stretch(Textures, ColTexture);
        Parameters.SizeChanged += (_, _) => Stretch(Parameters, ColParameter);
    }

    /// <summary>Give the leftover width to the name column — the inspector is narrow and names are long.</summary>
    private static void Stretch(ListView list, GridViewColumn first)
    {
        if (list.View is not GridView grid) return;
        double others = grid.Columns.Where(c => c != first)
            .Sum(c => double.IsNaN(c.Width) ? c.ActualWidth : c.Width);
        first.Width = Math.Max(120, list.ActualWidth - others - 40);
    }

    public MaterialInspector With(Selected.Material what)
    {
        if (ReferenceEquals(_index, what.Index) && _material == what.MaterialIndex) return this;
        _index = what.Index;
        _material = what.MaterialIndex;
        Show();
        return this;
    }

    private void Show()
    {
        if (_index is not { } index) return;
        Note.Text = "";
        try
        {
            _resolved = index.File.Material(_material);
        }
        catch (SdbFormatException e)
        {
            _resolved = null;
            Info.Text = $"#{_material}\n{e.Message}";
            Textures.ItemsSource = null;
            Parameters.ItemsSource = null;
            Note.Text = "this material does not resolve";
            Note.Foreground = Skin.Brush("Error");
            return;
        }

        var material = _resolved;
        var route = material.Route;
        var variants = material.Routes.SelectMany(r => r.Variants).ToArray();

        // the long token string goes last: it scrolls, and the summary above it has to be readable without.
        var info = new StringBuilder();
        info.AppendLine(material.Name.Length > 0 ? material.Name : "(unnamed)");
        info.AppendLine($"#{material.Index}  ·  {index.File.Name}  ·  {index.File.Layout.Name}");
        info.Append($"preset   {(route?.Preset.Length > 0 ? route.Preset : "(none)")}");
        info.Append($"  ·  {variants.Length} variant(s)  ·  {material.Textures.Count} texture(s)");
        if (material.NonRendering) info.Append("  ·  NON-RENDERING (no pass draws it)");
        if (material.Routes.Count != 1) info.Append($"  ·  {material.Routes.Count} routes");
        info.AppendLine();
        info.Append($"tokens   {route?.Tokens ?? ""}");
        Info.Text = info.ToString();

        ShowTextures(material);
        ShowParameters(material);
        _usedFor = null;
        if (Lower.SelectedItem == UsedTab) _ = ShowUsage();
    }

    /// <summary>The de-duplicated texture set, each with the slot that binds it and its catalog status.</summary>
    private void ShowTextures(SdbMaterial material)
    {
        var slots = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var overridden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in material.Routes.SelectMany(r => r.Variants).SelectMany(v => v.Bindings))
        {
            if (string.IsNullOrEmpty(b.Texture)) continue;
            if (!slots.TryGetValue(b.Texture, out var names)) slots[b.Texture] = names = new(StringComparer.Ordinal);
            if (b.Parameter is { Length: > 0 }) names.Add(b.Parameter);
            if (b.Overridden) overridden.Add(b.Texture);
        }

        var rows = new List<MaterialTextureRow>(material.Textures.Count);
        foreach (var name in material.Textures)
        {
            var bound = slots.GetValueOrDefault(name) ?? [];
            string slot = bound.Count switch
            {
                0 => "",
                1 => bound.First(),
                _ => $"{bound.First()} +{bound.Count - 1}",
            };
            rows.Add(new MaterialTextureRow(name, slot,
                                            string.Join(", ", bound) +
                                            (overridden.Contains(name) ? "  (set by this material)" : "  (shader default)"),
                                            Provides(name)));
        }
        Textures.ItemsSource = rows;
        TextureHeader.Text = rows.Count == 0 ? "textures — none" : $"textures — {rows.Count}";
    }

    /// <summary>
    /// How many loaded packs provide this name. Nothing means the texture lives in a pack that is not open, so
    /// replacing it from here would do nothing — worth saying rather than leaving blank.
    /// </summary>
    private string Provides(string name)
    {
        try
        {
            int n = _ws.Catalog.Lookup(name, 0x20).Length;
            return n == 0 ? "no" : n.ToString();
        }
        catch (Exception e) when (e is ObjectDisposedException or ArgumentOutOfRangeException)
        {
            return "";
        }
    }

    private void ShowParameters(SdbMaterial material)
    {
        var rows = new List<MaterialParameterRow>();
        var set = new HashSet<int>();
        foreach (var p in material.Routes.SelectMany(r => r.Parameters))
        {
            set.Add(p.Id);
            rows.Add(new MaterialParameterRow(
                p.Name,
                p.ValueText ?? p.ValueHex ?? "",
                p.Declared ? "set" : "untyped",
                $"id {p.Id} · {(p.Declared ? p.TypeName : "the preset does not declare this id")} · " +
                $"0xD2+{p.Offset}" + (p.FlagBit31 ? " · flag31" : "")));
        }

        if (ShowDefaults.IsChecked == true && material.Route is { PresetIndices.Count: 1 } route)
        {
            foreach (var d in _index!.File.Preset(route.PresetIndices[0]).Parameters)
            {
                if (set.Contains(d.Id)) continue;
                rows.Add(new MaterialParameterRow(d.Name, d.DefaultText ?? "", "default",
                                                  $"id {d.Id} · {d.TypeName}" +
                                                  (d.Annotation.Length > 0 ? $" · {d.Annotation}" : "") +
                                                  (d.Expression.Length > 0 ? $" · {d.Expression}" : "")));
            }
        }

        Parameters.ItemsSource = rows;
        int setCount = set.Count;
        ParameterHeader.Text = $"parameters — {setCount} set" +
                               (rows.Count > setCount ? $", {rows.Count - setCount} at default" : "");
    }

    private void ShowDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (_resolved is { } material) ShowParameters(material);
    }

    // ---- actions ---------------------------------------------------------------------------------------------

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_resolved is not { } material || material.Textures.Count == 0)
        {
            Note.Text = "this material binds no textures";
            Note.Foreground = Skin.Brush("Warn");
            return;
        }
        Note.Text = $"adding {material.Textures.Count} texture(s)...";
        Note.Foreground = Skin.Brush("FgDim");
        _host.Reveal("textures", new AddTexturesRequest([.. material.Textures], material.Name));
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_resolved is not { Textures.Count: > 0 } material) return;
        Clipboard.SetText(string.Join(Environment.NewLine, material.Textures));
    }

    private void Textures_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => ShowPicked();

    private void Show_Click(object sender, RoutedEventArgs e) => ShowPicked();

    private void ShowPicked()
    {
        if (Textures.SelectedItem is not MaterialTextureRow row) return;
        _host.Reveal("textures", row.Name);
    }

    private void CopyOne_Click(object sender, RoutedEventArgs e)
    {
        if (Textures.SelectedItem is MaterialTextureRow row) Clipboard.SetText(row.Name);
    }

    // ---- used by ------------------------------------------------------------------------------------------------

    private string? _usedFor;

    private void Lower_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == Lower && Lower.SelectedItem == UsedTab) _ = ShowUsage();
    }

    /// <summary>
    /// Meshes whose material table names this material and models whose materialsData/materialsResources do. The first
    /// request starts a background scan of every mesh (material tables only) and model; it is kept for the session.
    /// </summary>
    private async Task ShowUsage()
    {
        if (_resolved is not { } material || material.Name.Length == 0 || _usedFor == material.Name) return;
        string name = material.Name;
        _usedFor = name;
        UsedMeshes.ItemsSource = UsedModels.ItemsSource = null;
        MeshesHeader.Text = "scanning meshes and models...";
        ModelsHeader.Text = "models";
        StopScan.Visibility = Visibility.Visible;
        MaterialUsage? usage;
        try
        {
            usage = await _ws.Usage();
        }
        catch (OperationCanceledException)
        {
            usage = null;
        }
        finally
        {
            StopScan.Visibility = Visibility.Collapsed;
        }
        if (_usedFor != name) return;
        if (usage is null)
        {
            MeshesHeader.Text = "scan stopped";
            _usedFor = null;
            return;
        }
        var catalog = _ws.Catalog;
        var meshes = usage.MeshesOf(name).Select(g => catalog.Name(g)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        var models = usage.ModelsOf(name).Where(m => m.Wins).Select(m => m.Name)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        UsedMeshes.ItemsSource = meshes;
        UsedModels.ItemsSource = models;
        MeshesHeader.Text = $"meshes {meshes.Count:N0}";
        ModelsHeader.Text = $"models {models.Count:N0}";
    }

    private void StopScan_Click(object sender, RoutedEventArgs e) => _ws.CancelUsage();

    private void UsedMeshes_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (UsedMeshes.SelectedItem is string name) _host.Reveal("meshes", name);
    }

    private void UsedModels_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (UsedModels.SelectedItem is string name) _host.Reveal("models", name);
    }
}
