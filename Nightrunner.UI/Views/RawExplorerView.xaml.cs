using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Nightrunner.Core.Export;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Project;
using Nightrunner.Core.Rpack;

namespace Nightrunner.UI.Views;

/// <summary>One pack in the left list; updated in place when its tables finish parsing.</summary>
public sealed class PackItem(PackEntry entry) : INotifyPropertyChanged
{
    public PackEntry Entry { get; } = entry;
    public int Id => Entry.Id;
    public string Label => Entry.Label;
    public string Path => Entry.Path;

    public string Summary => Entry.Error is { } err
        ? err
        : Entry.IsIndexed
            ? $"{Entry.Count:N0} res · {Format.Size(Entry.FileSize)}"
            : "indexing";

    public System.Windows.Media.Brush SummaryBrush =>
        Skin.Brush(Entry.Error is not null ? "Error" : Entry.IsIndexed ? "FgDim" : "FgFaint");

    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SummaryBrush)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Raw tab: every resource of every open pack, searchable, with its parts and their bytes.</summary>
public partial class RawExplorerView : UserControl
{
    private readonly Workspace _ws;
    private readonly Selection _selection;
    private readonly List<PackItem> _packItems = [];
    private readonly Dictionary<int, PackItem> _packById = [];
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _searchCts;
    private bool _ready;
    private bool _dirty;
    private int _typeVersion = -1;

    private RpackCatalog Catalog => _ws.Catalog;

    public RawExplorerView(PanelContext ctx)
    {
        _ws = ctx.Workspace;
        _selection = ctx.Selection;
        InitializeComponent();
        _debounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(140),
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunSearchAsync();
        };
        PackList.ItemsSource = _packItems;
        _ws.Opened += OnWorkspaceOpened;
        Catalog.PackIndexed += OnPackIndexed;
        Loaded += OnLoaded;
        // no Unloaded handler: the dock unloads a panel whenever it is hidden, and a detached view would stop
        // following the workspace. MainWindow calls Detach() when the window is actually closed.
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_ready)
        {
            SyncPacks();        // shown again after being hidden: pick up anything that changed meanwhile
            MarkDirty();
            return;
        }
        Rows_SizeChanged(Rows, null!);
        _ready = true;
        SyncPacks();
        BuildTypeFilter();
        MarkDirty();
        Search.Focus();
    }

    /// <summary>Stop following the workspace — called when this window is closed for good.</summary>
    public void Detach()
    {
        _ws.Opened -= OnWorkspaceOpened;
        Catalog.PackIndexed -= OnPackIndexed;
        _selection.ClearFrom("Raw");
    }

    /// <summary>The shell opened another game: drop everything and follow the new catalog.</summary>
    private void OnWorkspaceOpened()
    {
        _searchCts?.Cancel();
        Rows.ItemsSource = null;
        _selection.ClearFrom("Raw");
        _packItems.Clear();
        _packById.Clear();
        PackList.Items.Refresh();
        _typeVersion = -1;
        Catalog.PackIndexed -= OnPackIndexed;
        Catalog.PackIndexed += OnPackIndexed;
        SyncPacks();
        BuildTypeFilter();
        MarkDirty();
    }

    public void FocusSearch()
    {
        Search.Focus();
        Search.SelectAll();
    }

    /// <summary>A resource name: search for it (the Mods window's "Raw").</summary>
    public void Reveal(object payload)
    {
        if (payload is not string name) return;
        Search.Text = name;
        _debounce.Stop();
        _ = RunSearchAsync();
    }

    // ---- packs ---------------------------------------------------------------------------------------------

    /// <summary>Add list rows for packs the catalog knows about but the list does not.</summary>
    private void SyncPacks()
    {
        bool added = false;
        foreach (var entry in Catalog.Packs)
        {
            if (_packById.ContainsKey(entry.Id)) continue;
            var item = new PackItem(entry);
            _packItems.Add(item);
            _packById[entry.Id] = item;
            added = true;
        }
        if (added) PackList.Items.Refresh();
        PackHeader.Text = $"packs ({_packItems.Count})";
    }

    /// <summary>Raised on a worker thread, once per pack.</summary>
    private void OnPackIndexed(PackEntry entry)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            SyncPacks();
            if (_packById.TryGetValue(entry.Id, out var item)) item.Refresh();
            MarkDirty();
            UpdateStatus();
        });
    }

    private void MarkDirty()
    {
        _dirty = true;
        if (!_debounce.IsEnabled) _debounce.Start();
    }

    private void UpdateStatus()
    {
        var packs = Catalog.Packs;
        int done = packs.Count(p => p.IsIndexed || p.Error is not null);
        int failed = packs.Count(p => p.Error is not null);
        var sb = new StringBuilder();
        sb.Append($"{done}/{packs.Count} packs · {Catalog.ResourceCount:N0} resources");
        if (failed > 0) sb.Append($" · {failed} failed");
        sb.Append($" · {Format.Size(packs.Sum(p => p.FileSize))} mapped");
        Status.Text = sb.ToString();
        Status.Foreground = Skin.Brush(failed > 0 ? "Error" : done < packs.Count ? "Warn" : "FgDim");
    }

    private void AddPacks_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Add packs",
            Filter = "RPACK (*.rpack)|*.rpack|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        _ = _ws.AddPacks(dlg.FileNames);
        SyncPacks();
    }

    // ---- filtering -----------------------------------------------------------------------------------------

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void TypeFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) MarkDirty();
    }

    private void PackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        MarkDirty();
        ShowSelection();
    }

    private void ClearPackFilter_Click(object sender, RoutedEventArgs e) => PackList.UnselectAll();

    private async Task RunSearchAsync()
    {
        _dirty = false;
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        var catalog = Catalog;
        string text = Search.Text;
        byte? type = (TypeFilter.SelectedItem as TypeItem)?.Id;
        var packIds = PackList.SelectedItems.Cast<PackItem>().Select(p => p.Id).ToArray();

        SearchResult result;
        try
        {
            result = await Task.Run(() => catalog.Search(text, type, packIds, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;   // the catalog was replaced while this search ran
        }
        if (cts.IsCancellationRequested || !ReferenceEquals(catalog, Catalog)) return;

        Rows.ItemsSource = new ResourceRowList(catalog, result.Gids);
        Timing.Text = $"{result.Count:N0} rows · {result.Elapsed.TotalMilliseconds:0.#} ms";
        UpdateStatus();
        BuildTypeFilter();
        ShowSelection();
        if (_dirty) MarkDirty();
    }

    private sealed record TypeItem(byte? Id, string Text)
    {
        public override string ToString() => Text;
    }

    private void BuildTypeFilter()
    {
        var counts = Catalog.TypeCounts();
        int version = counts.Count * 397 + counts.Values.Sum();
        if (version == _typeVersion) return;
        _typeVersion = version;

        byte? keep = (TypeFilter.SelectedItem as TypeItem)?.Id;
        var items = new List<TypeItem> { new(null, "all types") };
        items.AddRange(counts.Select(kv => new TypeItem(kv.Key, $"{ResTypes.Label(kv.Key)}  ({kv.Value:N0})")));
        bool was = _ready;
        _ready = false;
        TypeFilter.ItemsSource = items;
        TypeFilter.SelectedItem = items.FirstOrDefault(i => i.Id == keep) ?? items[0];
        _ready = was;
    }

    // ---- details -------------------------------------------------------------------------------------------

    private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelection();

    /// <summary>Give the name column whatever the fixed columns leave over, so nothing scrolls sideways.</summary>
    private void Rows_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Rows.View is not GridView grid) return;
        double others = grid.Columns.Where(c => c != ColName)
            .Sum(c => double.IsNaN(c.Width) ? c.ActualWidth : c.Width);
        ColName.Width = Math.Max(160, Rows.ActualWidth - others - 40);
    }

    /// <summary>Publish what is selected; the Inspector decides how to show it.</summary>
    private void ShowSelection()
    {
        if (Rows.SelectedItems.Count > 1)
        {
            _selection.Set("Raw", new Selected.Resources(
                Catalog, Rows.SelectedItems.Cast<ResourceRow>().Select(r => r.Gid).ToArray()));
            return;
        }
        if (Rows.SelectedItem is ResourceRow row)
        {
            _selection.Set("Raw", new Selected.Resource(Catalog, row.Gid));
            return;
        }
        if (PackList.SelectedItems.Count == 1 && PackList.SelectedItem is PackItem pack)
        {
            _selection.Set("Raw", new Selected.Pack(pack.Entry));
            return;
        }
        _selection.ClearFrom("Raw");
    }

    // ---- context menus -------------------------------------------------------------------------------------

    /// <summary>Right-clicking a pack selects it first, so the menu always acts on what was clicked.</summary>
    private void PackMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src &&
            ItemsControl.ContainerFromElement(PackList, src) is ListBoxItem item && !item.IsSelected)
        {
            PackList.SelectedItems.Clear();
            item.IsSelected = true;
        }
        if (PackList.SelectedItems.Count == 0) e.Handled = true;
    }

    private PackItem[] SelectedPacks() => PackList.SelectedItems.Cast<PackItem>().ToArray();

    private void PackReveal_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in SelectedPacks().Take(3)) Shell.Reveal(p.Path);
    }

    private void PackExport_Click(object sender, RoutedEventArgs e)
    {
        var packs = SelectedPacks().Where(p => p.Entry.IsIndexed).Select(p => p.Entry).ToArray();
        if (packs.Length == 0) return;
        var folder = Shell.PickFolder(this, "Export the raw bins of these packs into...");
        if (folder is null) return;
        RunExport($"export {packs.Length} pack(s)", folder,
                  (progress, ct, label) => RawExporter.ExportPacks(packs, folder, progress, ct, label));
    }

    private void PackToProject_Click(object sender, RoutedEventArgs e)
    {
        if (RequireProject() is not { } project) return;
        var packs = SelectedPacks().Where(p => p.Entry.IsIndexed).Select(p => p.Entry).ToArray();
        if (packs.Length == 0) return;
        RunExport($"add {packs.Length} pack(s) to project {project.Name}", project.Folder,
                  (progress, ct, label) => RawExporter.ExportPacks(packs, project.Folder, progress, ct, label));
    }

    private int[] SelectedGids() => Rows.SelectedItems.Cast<ResourceRow>().Select(r => r.Gid).ToArray();

    private void RowsExport_Click(object sender, RoutedEventArgs e)
    {
        var gids = SelectedGids();
        if (gids.Length == 0) return;
        var folder = Shell.PickFolder(this, "Export the raw bins of the selected resources into...");
        if (folder is null) return;
        var catalog = Catalog;
        RunExport($"export {gids.Length} resource(s)", folder,
                  (progress, ct, label) => RawExporter.ExportResources(catalog, gids, folder, progress, ct, label));
    }

    private void RowsToProject_Click(object sender, RoutedEventArgs e)
    {
        if (RequireProject() is not { } project) return;
        var gids = SelectedGids();
        if (gids.Length == 0) return;
        var catalog = Catalog;
        RunExport($"add {gids.Length} resource(s) to project {project.Name}", project.Folder,
                  (progress, ct, label) =>
                      RawExporter.ExportResources(catalog, gids, project.Folder, progress, ct, label));
    }

    private void RowsReveal_Click(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is ResourceRow row) Shell.Reveal(row.Pack.Path);
    }

    private void RowsCopyName_Click(object sender, RoutedEventArgs e)
    {
        var names = Rows.SelectedItems.Cast<ResourceRow>().Select(r => r.Name).ToArray();
        if (names.Length > 0) Clipboard.SetText(string.Join(Environment.NewLine, names));
    }

    private ModProject? RequireProject()
    {
        if (_ws.Project is { } p) return p;
        Log.Warn("export", "no project is open - open or create one in the Projects window first");
        Status.Text = "no project open";
        Status.Foreground = Skin.Brush("Warn");
        return null;
    }

    /// <summary>Run an export off the UI thread. The exporter itself writes the start/end lines to the log.</summary>
    private void RunExport(string what, string where,
                           Func<IProgress<ExportProgress>, CancellationToken, string, ExportResult> run)
    {
        Status.Text = $"{what}: queued";
        Status.Foreground = Skin.Brush("FgDim");
        Jobs.Run(what, "export", ctx =>
        {
            var r = run(Jobs.Bytes(ctx, what), ctx.Token, what);
            if (r.Errors.Count > 0) throw new System.IO.IOException($"{r.Summary}; first: {r.Errors[0]}");
            return r.Summary;
        }, log: false);
    }

}
