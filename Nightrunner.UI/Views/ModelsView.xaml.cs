using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Nightrunner.Core.Export;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;

namespace Nightrunner.UI.Views;

/// <summary>One <c>.model</c> row. A member a later pak overrides is listed, dimmed: the game never loads it.</summary>
public sealed record ModelRow(ModelEntry Entry)
{
    public string Basename => Entry.Basename;
    public string Pak => System.IO.Path.GetFileName(Entry.Pak);
    public double Opacity => Entry.Wins ? 1.0 : 0.45;
    public string Tip => Entry.Wins ? Entry.Name : $"{Entry.Name} (overridden by {string.Join(", ", Entry.OverriddenBy.Select(System.IO.Path.GetFileName))})";
}

/// <summary>Models: every <c>.model</c> in the game's paks. Selecting one publishes it to the Viewport and Inspector.</summary>
public partial class ModelsView : UserControl
{
    private readonly Workspace _ws;
    private readonly Selection _selection;
    private readonly DispatcherTimer _debounce;
    private List<ModelRow> _all = [];
    private ModelCatalog? _models;
    private bool _ready;

    public ModelsView(PanelContext ctx)
    {
        _ws = ctx.Workspace;
        _selection = ctx.Selection;
        InitializeComponent();
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Filter();
        };
        _ws.Opened += OnWorkspaceOpened;
        Loaded += (_, _) =>
        {
            if (_ready) return;
            _ready = true;
            Rows_SizeChanged(Rows, null!);
            _ = LoadAsync();
        };
    }

    public void Detach()
    {
        _ws.Opened -= OnWorkspaceOpened;
        _selection.ClearFrom("Models");
    }

    public void FocusSearch()
    {
        Search.Focus();
        Search.SelectAll();
    }

    /// <summary>A model name: search for it and select it.</summary>
    public async void Reveal(object payload)
    {
        if (payload is not string name) return;
        if (_models is null) await LoadAsync();
        Search.Text = System.IO.Path.GetFileName(name);
        _debounce.Stop();
        Filter();
        if (Rows.ItemsSource is not List<ModelRow> rows) return;
        var hit = rows.FirstOrDefault(r => r.Entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && r.Entry.Wins)
                  ?? rows.FirstOrDefault(r => r.Basename.Equals(System.IO.Path.GetFileName(name), StringComparison.OrdinalIgnoreCase));
        if (hit is null) return;
        Rows.SelectedItem = hit;
        Rows.ScrollIntoView(hit);
    }

    private void OnWorkspaceOpened()
    {
        _selection.ClearFrom("Models");
        _models = null;
        _all = [];
        Rows.ItemsSource = null;
        if (_ready) _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        Status.Text = "reading paks...";
        var models = await Task.Run(() => _ws.Models);
        _models = models;
        if (models is null)
        {
            Status.Text = "no game";
            return;
        }
        _all = models.Models.Select(m => new ModelRow(m))
            .OrderBy(r => r.Basename, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Entry.Wins).ToList();
        bool was = _ready;
        _ready = false;
        PakFilter.ItemsSource = new[] { "all paks" }.Concat(_all.Select(r => r.Pak).Distinct(StringComparer.OrdinalIgnoreCase)).ToList();
        PakFilter.SelectedIndex = 0;
        _ready = was;
        Filter();
    }

    private void Filter()
    {
        string[] words = Search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? pak = PakFilter.SelectedIndex > 0 ? PakFilter.SelectedItem as string : null;
        var rows = _all.Where(r => (pak is null || r.Pak.Equals(pak, StringComparison.OrdinalIgnoreCase)) &&
                                   words.All(w => r.Entry.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        Rows.ItemsSource = rows;
        int overridden = rows.Count(r => !r.Entry.Wins);
        Status.Text = $"{rows.Count:N0} models" + (overridden > 0 ? $" · {overridden} overridden" : "") +
                      (_models is { Errors.Count: > 0 } m ? $" · {m.Errors.Count} errors" : "");
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void PakFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) Filter();
    }

    private void Rows_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Rows.View is not GridView grid) return;
        double others = grid.Columns.Where(c => c != ColName).Sum(c => double.IsNaN(c.Width) ? c.ActualWidth : c.Width);
        ColName.Width = Math.Max(140, Rows.ActualWidth - others - 40);
    }

    private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Rows.SelectedItems.Count == 1 && Rows.SelectedItem is ModelRow row && _models is { } m) _selection.Set("Models", new Selected.Model(m, row.Entry));
        else _selection.ClearFrom("Models");
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var rows = Rows.SelectedItems.Cast<ModelRow>().ToArray();
        if (rows.Length == 0 || _models is not { } models) return;
        var folder = Shell.PickFolder(this, "Export the selected models into...");
        if (folder is null) return;
        var catalog = _ws.Catalog;
        var sdbTask = _ws.Sdb.EnsureAsync();
        MeshModel Decode(int gid) => _ws.Mesh(gid).GetAwaiter().GetResult();
        int SkinOf(int gid, MeshModel mesh) =>
            _ws.SkinOf(catalog, gid) ?? (mesh.SkinRaw is { } raw && MeshSkins.Decode(raw) is { Error: null } k ? k.DefaultIndex : -1);
        foreach (var row in rows)
            Jobs.Run($"export {row.Basename}", "export", ctx =>
                AssetExport.ExportModel(models, row.Entry, catalog, sdbTask.GetAwaiter().GetResult()?.File, Decode, SkinOf,
                                        folder, new AssetExportOptions(), t => ctx.Report(null, t), ctx.Token));
        Status.Text = $"export {rows.Length} model(s): queued";
    }

    private void AddToProject_Click(object sender, RoutedEventArgs e)
    {
        if (_ws.Project is not { } project || _models is not { } models) { Status.Text = "open a project first"; return; }
        var origins = _ws.Origins;
        foreach (var row in Rows.SelectedItems.Cast<ModelRow>().ToArray())
            Jobs.Run($"add {row.Basename} to {project.Name}", "project", _ =>
                // the template is the stock .model, never a mod's
                $"-> {Nightrunner.Core.Project.ProjectAssets.AddModel(project, models, Nightrunner.Core.Games.StockCopy.Model(models, row.Entry, origins)).Folder}");
    }

    private void AddScene_Click(object sender, RoutedEventArgs e)
    {
        if (_ws.Project is not { } project || _models is not { } models) { Status.Text = "open a project first"; return; }
        var origins = _ws.Origins;
        MeshModel Decode(int gid) => _ws.Mesh(gid).GetAwaiter().GetResult();
        foreach (var row in Rows.SelectedItems.Cast<ModelRow>().ToArray())
            Jobs.Run($"scene {row.Basename} in {project.Name}", "project", _ =>
            {
                // the stock .model, resolved against the stock packs only; gids are the full catalog's
                _ws.WaitLoaded().GetAwaiter().GetResult();
                var entry = Nightrunner.Core.Games.StockCopy.Model(models, row.Entry, origins);
                return $"-> {Nightrunner.Core.Project.ProjectAssets.AddScene(project, models, entry, _ws.StockCatalog(), Decode).Folder}";
            });
    }

    private void CopyName_Click(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is ModelRow row) Clipboard.SetText(row.Entry.Name);
    }
}
