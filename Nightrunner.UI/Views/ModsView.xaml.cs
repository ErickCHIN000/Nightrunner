using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nightrunner.Core.Games;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Rpack;

namespace Nightrunner.UI.Views;

/// <summary>One mod in the Mods window. <paramref name="lockedWhy"/>: why its switch cannot be written (null: it can).</summary>
public sealed class ModRow(RuntimeMod mod, bool runtimeFound, string? lockedWhy = null)
{
    public RuntimeMod Mod { get; } = mod;
    public string Order => Mod.Valid ? Mod.Order.ToString() : "";
    public string Id => Mod.Id;
    public string Name => Mod.Name ?? "";
    public string Version => Mod.Version ?? "";
    public string Author => Mod.Author ?? "";
    public string Folder => Mod.Folder;
    public string On => Mod.Blocked is not null ? "?" : Mod.Enabled ? "on" : "off";
    public bool Enabled => Mod.Enabled;
    public bool CanSwitch => lockedWhy is null;
    public string SwitchTip => lockedWhy ?? $"{(Mod.Enabled ? "on" : "off")} · applies at next game start";
    public string Loads => !Mod.Valid ? "no" : !Mod.Loads ? "no" : runtimeFound ? "yes" : "no runtime";
    public Brush LoadsBrush => Skin.Brush(Loads == "yes" ? "Fg" : Loads == "no runtime" ? "Warn" : "FgDim");
    public string Valid => Mod.Valid ? "ok" : "invalid";
    public Brush ValidBrush => Skin.Brush(Mod.Valid ? "FgDim" : "Error");
    public double Opacity => Mod.Valid && Mod.Enabled ? 1.0 : 0.5;

    /// <summary>Stock resources and members it overrides; null until computed.</summary>
    public IReadOnlyList<RuntimeOverride>? Overrides { get; set; }
    public string OverrideCount => Overrides is { } o ? o.Count.ToString("N0") : "";
}

public sealed record ModItemRow(string Kind, string File, string At, string Loads, Brush LoadsBrush, string Path);

public sealed record ModOverrideRow(RuntimeOverride Value)
{
    public string Type => Value.Type is { } t ? ResTypes.Label(t) : "pak member";
    public string Name => Value.Name;
    public string File => Value.File;
    public string Stock => Value.Stock;
}

/// <summary>
/// Mods: what Nightrunner Runtime Modding would load for the open game — NightrunnerProxy's mods (<c>mods\&lt;mod&gt;\mod.json</c>,
/// switched and ordered by <c>nightrunner.json</c>), each with its items, what it overrides in the stock game, and why
/// anything does not load. The "on" box writes <c>nightrunner.json</c> (<see cref="Workspace.SetModEnabled"/>), the one file
/// this app writes in a game folder; everything else here only reads.
/// </summary>
public partial class ModsView : UserControl
{
    private readonly Workspace _ws;
    private readonly Selection _selection;
    private readonly IPanelHost _host;
    private List<ModRow> _rows = [];
    private RuntimeCheck? _check;
    private bool _ready;
    private int _version;

    public ModsView(PanelContext ctx)
    {
        _ws = ctx.Workspace;
        _selection = ctx.Selection;
        _host = ctx.Host;
        InitializeComponent();
        _ws.Opened += OnWorkspaceOpened;
        _ws.ModSwitched += OnModSwitched;
        Loaded += (_, _) =>
        {
            if (_ready) return;
            _ready = true;
            _ = LoadAsync();
        };
    }

    /// <summary>Stop following the workspace — called when this window is closed for good.</summary>
    public void Detach()
    {
        _ws.Opened -= OnWorkspaceOpened;
        _ws.ModSwitched -= OnModSwitched;
        _selection.ClearFrom("Mods");
    }

    /// <summary>The rows shown, for the runtime check.</summary>
    public IReadOnlyList<ModRow> Rows => _rows;

    /// <summary>Completes when the rows and their overrides are read.</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    /// <summary>A mod id: select it.</summary>
    public void Reveal(object payload)
    {
        if (payload is not string id) return;
        if (_rows.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } row) ModList.SelectedItem = row;
    }

    private void OnWorkspaceOpened()
    {
        _selection.ClearFrom("Mods");
        if (_ready) _ = LoadAsync();
    }

    private Task LoadAsync() => Ready = Load();

    private async Task Load()
    {
        int version = ++_version;
        string? keep = (ModList.SelectedItem as ModRow)?.Id;
        ModList.ItemsSource = null;
        ItemList.ItemsSource = null;
        OverrideList.ItemsSource = null;
        ProblemList.ItemsSource = null;
        ShowNote();
        if (_ws.Install is not { } install)
        {
            Runtime.Text = "no game";
            Status.Text = "";
            _rows = [];
            return;
        }
        Status.Text = "reading...";
        var (check, content) = await Task.Run(() => (RuntimeModding.Validate(install), RuntimeContent.Read(install)));
        if (version != _version) return;
        _check = check;
        string shown = _ws.RuntimeModding ? "shown" : "hidden";
        Runtime.Text = !RuntimeModding.HasModule(install.Profile) ? $"runtime: {check.Summary}"
            : check.DllPath is null ? "runtime: not installed"
            : $"runtime: proxy · {Path.GetFileName(check.DllPath)} · " +
              (check.Found ? "ok" : "incomplete") + $" · {shown}" +
              (check.Boot is { } boot ? $" · {boot.Text}" : "") +
              (content.NoMods is { } none ? $" · {none}" : "");
        Runtime.Foreground = Skin.Brush(check.Found && check.Boot is null && content.NoMods is null ? "Fg" : "Warn");
        Runtime.ToolTip = check.Summary + (check.Boot is { } b ? $"{Environment.NewLine}{b.Text} · {b.Log}" : "");

        string? locked = !RuntimeModding.HasModule(install.Profile) ? check.Summary
            : !_ws.RuntimeModding ? "runtime modding off" : !check.Found ? "runtime not installed"
            : content.Settings.Problem;
        var rows = content.Mods.Select(m => new ModRow(m, check.Found, locked)).ToList();
        _rows = rows;
        ModList.ItemsSource = rows;
        ShowStatus(content);
        ModList.SelectedItem = rows.FirstOrDefault(r => r.Id.Equals(keep, StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault();

        // what each mod overrides: its rpacks against the stock packs, its paks' .model/.scr against the stock paks
        await _ws.WaitLoaded();
        if (version != _version) return;
        var origins = _ws.Origins;
        var stockPacks = _ws.Catalog.IndexedPacks.Where(e => origins.IsStock(e.Path)).ToList();
        var problems = new List<string>();
        await Task.Run(() =>
        {
            var stockPaks = RuntimeOverrides.StockMembers.Read(install.Paks(runtime: null).Where(origins.IsStock));
            problems.AddRange(stockPaks.Errors);
            foreach (var row in rows)
                row.Overrides = RuntimeOverrides.Of(row.Mod.Items, stockPacks, stockPaks, problems);
        });
        if (version != _version) return;
        foreach (var p in problems) Log.Warn("mods", p);
        ModList.Items.Refresh();
        ShowStatus(content);
        if (ModList.SelectedItem is ModRow selected) Show(selected);
    }

    private void ShowStatus(RuntimeContent content)
    {
        int loads = _rows.Count(r => r.Loads == "yes");
        int overrides = _rows.Sum(r => r.Overrides?.Count ?? 0);
        Status.Text = $"{content.Mods.Count} mods · {loads} load · {content.Items.Count(i => i.Loads)} items · {content.Problems.Count} problems" +
                      (_rows.Any(r => r.Overrides is null) ? "" : $" · {overrides:N0} overrides");
    }

    // ---- selection -------------------------------------------------------------------------------------------------

    private void ModList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModList.SelectedItem is ModRow row) Show(row);
    }

    private void Show(ModRow row)
    {
        bool runtimeFound = row.Loads != "no runtime";
        ItemList.ItemsSource = row.Mod.Items.Select(i =>
        {
            string loads = i.Problem ?? (i.Path is null ? "not found" : row.Mod.Valid && !runtimeFound ? "no runtime" : "yes");
            return new ModItemRow(i.Kind.ToString().ToLowerInvariant(), i.Name, i.At, loads,
                                  Skin.Brush(loads == "yes" ? "Fg" : loads == "no runtime" ? "Warn" : "FgDim"), i.Path ?? "");
        }).ToList();
        OverrideList.ItemsSource = row.Overrides?.Select(o => new ModOverrideRow(o)).ToList();
        var problems = row.Mod.Problems.ToList();
        if (_check?.Boot is { } boot) problems.Insert(0, $"runtime: {boot.Text} · {boot.Log}");
        if (_check is not { Found: true })
            problems.Insert(0, $"runtime: {_check?.Summary ?? "NightrunnerProxy is not installed"}");
        ProblemList.ItemsSource = problems;
        UpdateButtons();
        _selection.Set("Mods", new Selected.Mod(row.Mod, _check, row.Overrides, row.Loads));
    }

    private void OverrideList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var o = (OverrideList.SelectedItem as ModOverrideRow)?.Value;
        MeshesButton.IsEnabled = o is { Type: 0x10 };
        ModelsButton.IsEnabled = o is { Kind: RuntimeKind.Pak } && !o.Name.EndsWith(RuntimeOverrides.ScriptSuffix, StringComparison.OrdinalIgnoreCase);
    }

    // ---- switch ----------------------------------------------------------------------------------------------------

    /// <summary>The "on" box: write the switch; refused or unchanged, the box goes back to what the file says.</summary>
    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: ModRow row } box) return;
        box.IsEnabled = false;
        var result = await _ws.SetModEnabled(row.Id, box.IsChecked == true);
        if (!result.Written) ModList.Items.Refresh();
    }

    private void OnModSwitched(RuntimeSwitch result) => ShowNote();

    private void ShowNote()
    {
        var last = _ws.LastSwitch;
        Note.Text = last is null ? ""
            : last.State == RuntimeSwitchState.Refused ? $"{last.ModId}: {last.Message}"
            : last.Written ? $"{last.ModId} {(last.Enabled ? "on" : "off")} · {last.Message}"
            : $"{last.ModId} {last.Message}";
        Note.Foreground = Skin.Brush(last?.State == RuntimeSwitchState.Refused ? "Warn" : "Fg");
    }

    // ---- actions ---------------------------------------------------------------------------------------------------

    private void Reload_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ModList.SelectedItem is ModRow row && Directory.Exists(row.Folder)) Shell.Reveal(row.Folder);
        else if (_ws.Install is { } install)
        {
            string mods = Path.Combine(install.Paths.RuntimeFolder, RuntimeContent.ModsFolderName);
            Shell.Reveal(Directory.Exists(mods) ? mods : install.Paths.Bin);
        }
    }

    private void ShowRaw_Click(object sender, RoutedEventArgs e)
    {
        if (OverrideList.SelectedItem is ModOverrideRow row) _host.Reveal("raw", row.Name);
    }

    private void ShowMeshes_Click(object sender, RoutedEventArgs e)
    {
        if (OverrideList.SelectedItem is ModOverrideRow { Value.Type: 0x10 } row) _host.Reveal("meshes", row.Name);
    }

    private void ShowModels_Click(object sender, RoutedEventArgs e)
    {
        if (OverrideList.SelectedItem is ModOverrideRow { Value.Kind: RuntimeKind.Pak } row) _host.Reveal("models", row.Name);
    }

    /// <summary>Double-click: meshes to Meshes, models to Models, anything else to Raw.</summary>
    private void OverrideList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (OverrideList.SelectedItem is not ModOverrideRow row) return;
        if (row.Value.Type == 0x10) _host.Reveal("meshes", row.Name);
        else if (row.Value.Kind == RuntimeKind.Pak && !row.Name.EndsWith(RuntimeOverrides.ScriptSuffix, StringComparison.OrdinalIgnoreCase))
            _host.Reveal("models", row.Name);
        else _host.Reveal("raw", row.Name);
    }
}
