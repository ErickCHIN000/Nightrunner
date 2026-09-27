using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Nightrunner.Core.Export;
using Nightrunner.Core.Rpack;

namespace Nightrunner.UI.Views;

/// <summary>One mesh row: a name and its pack. Nothing is decoded until the mesh is selected.</summary>
public sealed record MeshRow(int Gid, string Name, string PackLabel);

/// <summary>Data-virtualised mesh rows (the Raw/Textures trick): a row is built when it is shown.</summary>
public sealed class MeshRowList(RpackCatalog catalog, int[] gids) : IList, IReadOnlyList<MeshRow>
{
    public int[] Gids { get; } = gids;
    public int Count => Gids.Length;
    public MeshRow this[int index] => new(Gids[index], catalog.Name(Gids[index]), catalog.Split(Gids[index]).Entry.Label);

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    public IEnumerator<MeshRow> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public int IndexOf(object? value) => value is MeshRow r ? Array.IndexOf(Gids, r.Gid) : -1;
    public bool Contains(object? value) => IndexOf(value) >= 0;
    bool IList.IsFixedSize => true;
    bool IList.IsReadOnly => true;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;

    void ICollection.CopyTo(Array array, int index)
    {
        for (int i = 0; i < Count; i++) array.SetValue(this[i], index + i);
    }

    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
}

/// <summary>Meshes: every 0x10 resource of the open game. Selecting one publishes it to the Viewport and Inspector.</summary>
public partial class MeshesView : UserControl
{
    private readonly Workspace _ws;
    private readonly Selection _selection;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _searchCts;
    private bool _ready;
    private int _packFilter = -1;
    private int _packCount = -1;

    private RpackCatalog Catalog => _ws.Catalog;

    public MeshesView(PanelContext ctx)
    {
        _ws = ctx.Workspace;
        _selection = ctx.Selection;
        InitializeComponent();
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(140) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunSearchAsync();
        };
        _ws.Opened += OnWorkspaceOpened;
        Catalog.PackIndexed += OnPackIndexed;
        Loaded += (_, _) =>
        {
            if (_ready)
            {
                BuildPackFilter();
                return;
            }
            _ready = true;
            Rows_SizeChanged(Rows, null!);
            BuildPackFilter();
            _ = RunSearchAsync();
        };
    }

    private void OnWorkspaceOpened()
    {
        _searchCts?.Cancel();
        Rows.ItemsSource = null;
        _selection.ClearFrom("Meshes");
        Catalog.PackIndexed -= OnPackIndexed;
        Catalog.PackIndexed += OnPackIndexed;
        _packFilter = -1;
        _packCount = -1;
        BuildPackFilter();
        if (_ready) _ = RunSearchAsync();
    }

    private void OnPackIndexed(PackEntry entry) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_debounce.IsEnabled) _debounce.Start();
        });

    public void FocusSearch()
    {
        Search.Focus();
        Search.SelectAll();
    }

    public void Detach()
    {
        _ws.Opened -= OnWorkspaceOpened;
        Catalog.PackIndexed -= OnPackIndexed;
        _selection.ClearFrom("Meshes");
    }

    /// <summary>A mesh name: search for it and select it.</summary>
    public void Reveal(object payload)
    {
        if (payload is not string name) return;
        Search.Text = name;
        _debounce.Stop();
        _ = SelectFirst(name);
    }

    private async Task SelectFirst(string name)
    {
        await RunSearchAsync();
        if (Rows.ItemsSource is not MeshRowList list) return;
        for (int i = 0; i < list.Count; i++)
        {
            if (!string.Equals(list[i].Name.Trim(), name, StringComparison.OrdinalIgnoreCase)) continue;
            Rows.SelectedIndex = i;
            Rows.ScrollIntoView(Rows.SelectedItem);
            return;
        }
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void PackFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _packFilter = (PackFilter.SelectedItem as PackChoice)?.Id ?? -1;
        _ = RunSearchAsync();
    }

    private sealed record PackChoice(int Id, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>Packs that hold meshes only; rebuilt as packs finish indexing, keeping the selection.</summary>
    private void BuildPackFilter()
    {
        var packs = Catalog.IndexedPacks;
        if (packs.Length == _packCount) return;
        _packCount = packs.Length;
        var items = new List<PackChoice> { new(-1, "all packs") };
        items.AddRange(packs.Where(p => p.TypeCounts.GetValueOrDefault((byte)0x10) > 0).Select(p => new PackChoice(p.Id, p.Label)));
        bool was = _ready;
        _ready = false;
        PackFilter.ItemsSource = items;
        PackFilter.SelectedItem = items.FirstOrDefault(i => i.Id == _packFilter) ?? items[0];
        _packFilter = ((PackChoice)PackFilter.SelectedItem).Id;
        _ready = was;
    }

    private async Task RunSearchAsync()
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var catalog = Catalog;
        string text = Search.Text;
        int[]? packs = _packFilter >= 0 ? [_packFilter] : null;
        SearchResult result;
        try
        {
            result = await Task.Run(() => catalog.Search(text, 0x10, packs, cts.Token), cts.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        if (cts.IsCancellationRequested || !ReferenceEquals(catalog, Catalog)) return;
        Rows.ItemsSource = new MeshRowList(catalog, result.Gids);
        BuildPackFilter();
        Status.Text = $"{result.Count:N0} meshes" +
                      (packs is not null ? $" · {(PackFilter.SelectedItem as PackChoice)?.Text}" : "") +
                      $" · {result.Elapsed.TotalMilliseconds:0.#} ms";
    }

    private void Rows_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Rows.View is not GridView grid) return;
        double others = grid.Columns.Where(c => c != ColName).Sum(c => double.IsNaN(c.Width) ? c.ActualWidth : c.Width);
        ColName.Width = Math.Max(140, Rows.ActualWidth - others - 40);
    }

    private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Rows.SelectedItem is MeshRow row) _selection.Set("Meshes", new Selected.Mesh(Catalog, row.Gid));
        else _selection.ClearFrom("Meshes");
    }

    private MeshRow[] SelectedRows() => Rows.SelectedItems.Cast<MeshRow>().ToArray();

    private void ExportRaw_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows();
        if (rows.Length == 0) return;
        var folder = Shell.PickFolder(this, "Export the raw mesh parts into...");
        if (folder is null) return;
        var catalog = Catalog;
        var gids = rows.Select(r => r.Gid).ToArray();
        string what = $"export {rows.Length} mesh(es)";
        Status.Text = $"{what}: queued";
        Jobs.Run(what, "export", ctx =>
        {
            var r = RawExporter.ExportResources(catalog, gids, folder, Jobs.Bytes(ctx, what), ctx.Token, what);
            if (r.Errors.Count > 0) throw new System.IO.IOException($"{r.Summary}; first: {r.Errors[0]}");
            return r.Summary;
        }, log: false);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows();
        if (rows.Length == 0) return;
        var folder = Shell.PickFolder(this, "Export the selected meshes into...");
        if (folder is null) return;
        var catalog = Catalog;
        var sdbTask = _ws.Sdb.EnsureAsync();
        foreach (var row in rows)
            Jobs.Run($"export {row.Name.Trim()}", "export", ctx =>
                AssetExport.ExportMesh(catalog, sdbTask.GetAwaiter().GetResult()?.File, row.Gid, folder, new AssetExportOptions(),
                                       t => ctx.Report(null, t), ctx.Token));
        Status.Text = $"export {rows.Length} mesh(es): queued";
    }

    private void AddToProject_Click(object sender, RoutedEventArgs e)
    {
        if (_ws.Project is not { } project)
        {
            Status.Text = "open a project first";
            return;
        }
        var catalog = Catalog;
        var origins = _ws.Origins;
        foreach (var row in SelectedRows())
            Jobs.Run($"add {row.Name.Trim()} to {project.Name}", "project", _ =>
            {
                // the template is the stock copy, never a mod's
                int gid = Nightrunner.Core.Games.StockCopy.Resource(catalog, row.Gid, origins);
                return $"-> {Nightrunner.Core.Project.ProjectAssets.AddMesh(project, catalog, gid).Folder}";
            });
    }

    private void CopyName_Click(object sender, RoutedEventArgs e)
    {
        var names = SelectedRows().Select(r => r.Name).ToArray();
        if (names.Length > 0) Clipboard.SetText(string.Join(Environment.NewLine, names));
    }
}
