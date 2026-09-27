using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Nightrunner.Core.Prefab;

namespace Nightrunner.UI.Views;

/// <summary>
/// One prefab row: a binary prefab (its rpack) or a text member (its pak). A name another provider registers first is
/// listed, dimmed: the engine keeps the first, and asks the registry before it opens a text file.
/// </summary>
public sealed record PrefabRow(PrefabEntry Entry)
{
    public string Name => Entry.Name;
    public string Pack => System.IO.Path.GetFileNameWithoutExtension(Entry.Pack);
    public string Components => Entry.Source == PrefabSource.Rpack || Entry.IsLoaded ? Entry.TryRoot(out _)?.Components.Count.ToString() ?? "" : "";
    public double Opacity => Entry.Wins ? 1.0 : 0.45;
    public string Tip => (Entry.Member ?? Entry.Name) + (Entry.Wins ? "" : $" (shadowed by {Entry.ShadowedBy?.ToString() ?? "an earlier registration"})");
}

/// <summary>Prefabs: every binary prefab of the game. Selecting one shows it in the Inspector and the Viewport.</summary>
public partial class PrefabsView : UserControl
{
    private readonly Workspace _ws;
    private readonly Selection _selection;
    private readonly DispatcherTimer _debounce;
    private List<PrefabRow> _all = [];
    private PrefabCatalog? _prefabs;
    private bool _ready;

    public PrefabsView(PanelContext ctx)
    {
        _ws = ctx.Workspace;
        _selection = ctx.Selection;
        InitializeComponent();
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
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
        _selection.ClearFrom("Prefabs");
    }

    public void FocusSearch()
    {
        Search.Focus();
        Search.SelectAll();
    }

    /// <summary>A prefab name: search for it and select the registered one.</summary>
    public async void Reveal(object payload)
    {
        if (payload is not string name) return;
        if (_prefabs is null) await LoadAsync();
        if (_prefabs?.Find(name) is not { } entry) return;
        Search.Text = entry.Name;
        _debounce.Stop();
        if (PackFilter.SelectedIndex != 0) PackFilter.SelectedIndex = 0;
        Filter();
        if (Rows.ItemsSource is not List<PrefabRow> rows || rows.FirstOrDefault(r => ReferenceEquals(r.Entry, entry)) is not { } hit) return;
        Rows.SelectedItem = hit;
        Rows.ScrollIntoView(hit);
    }

    private void OnWorkspaceOpened()
    {
        _selection.ClearFrom("Prefabs");
        _prefabs = null;
        _all = [];
        Rows.ItemsSource = null;
        if (_ready) _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        Status.Text = "decoding prefabs...";
        await _ws.WaitLoaded();
        var prefabs = await Task.Run(() => _ws.Prefabs);
        _prefabs = prefabs;
        if (prefabs is null)
        {
            Status.Text = "no packs";
            return;
        }
        _all = prefabs.Prefabs.Select(p => new PrefabRow(p)).OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => !r.Entry.Wins).ToList();
        bool was = _ready;
        _ready = false;
        PackFilter.ItemsSource = new[] { "all packs" }.Concat(_all.Select(r => r.Pack).Distinct().Order(StringComparer.OrdinalIgnoreCase)).ToList();
        PackFilter.SelectedIndex = 0;
        _ready = was;
        Filter();
    }

    private void Filter()
    {
        string[] words = Search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? pack = PackFilter.SelectedIndex > 0 ? PackFilter.SelectedItem as string : null;
        var rows = _all.Where(r => (pack is null || r.Pack == pack) &&
                                   words.All(w => r.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        Rows.ItemsSource = rows;
        Status.Text = $"{rows.Count:N0} prefabs" + (_prefabs is { Errors.Count: > 0 } p ? $" · {p.Errors.Count} pack(s) not decoded" : "");
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void PackFilter_Changed(object sender, SelectionChangedEventArgs e)
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
        if (Rows.SelectedItem is PrefabRow row && _prefabs is { } p) _selection.Set("Prefabs", new Selected.Prefab(p, row.Entry));
        else _selection.ClearFrom("Prefabs");
    }

    private void CopyName_Click(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is PrefabRow row) Clipboard.SetText(row.Name);
    }
}
