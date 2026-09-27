using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Nightrunner.Core.Export;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Sdb;

namespace Nightrunner.UI.Views;

/// <summary>One material row: a name and the preset its route names. Both come straight out of the index.</summary>
public sealed class MaterialRow(SdbIndex index, int material)
{
    public int Index => material;
    public string Name => index.Name(material);
    public string Preset => index.Preset[material];
    public string IndexText => material.ToString("N0");
}

/// <summary>Data-virtualised list of materials — the same trick the Raw and Textures lists use.</summary>
public sealed class MaterialRowList(SdbIndex index, int[] materials) : IList, IReadOnlyList<MaterialRow>
{
    private readonly Dictionary<int, MaterialRow> _cache = [];

    public int[] Materials { get; } = materials;
    public int Count => Materials.Length;

    public MaterialRow this[int i]
    {
        get
        {
            if (_cache.TryGetValue(i, out var row)) return row;
            if (_cache.Count > 20_000) _cache.Clear();
            return _cache[i] = new MaterialRow(index, Materials[i]);
        }
    }

    object? IList.this[int i]
    {
        get => this[i];
        set => throw new NotSupportedException();
    }

    public IEnumerator<MaterialRow> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public int IndexOf(object? value) => value is MaterialRow r ? Array.IndexOf(Materials, r.Index) : -1;
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

/// <summary>
/// Materials: every material the game's shader database names, with the preset it is built from.
/// </summary>
/// <remarks>
/// The list is this window's job; the detail — parameters, variants, the textures it draws with — belongs to the
/// Inspector, the same split the other panels use. Read-only: there is no SDB writer.
/// </remarks>
public partial class MaterialsView : UserControl
{
    private const string Source = "Materials";

    private readonly Workspace _ws;
    private readonly Selection _selection;
    private readonly IPanelHost _host;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _searchCts;
    private SdbIndex? _index;
    private bool _ready;
    private string _preset = "";

    private SdbService Sdb => _ws.Sdb;

    public MaterialsView(PanelContext ctx)
    {
        _ws = ctx.Workspace;
        _selection = ctx.Selection;
        _host = ctx.Host;
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
        _ws.Opened += OnWorkspaceOpened;
        Sdb.Changed += OnSdbChanged;
        Loaded += (_, _) =>
        {
            if (_ready) return;
            _ready = true;
            Rows_SizeChanged(Rows, null!);
            _ = OpenAsync();
        };
        // no Unloaded handler — the dock unloads a panel that is merely hidden; see RawExplorerView.Detach
    }

    /// <summary>Stop following the workspace — called when this window is closed for good.</summary>
    public void Detach()
    {
        _ws.Opened -= OnWorkspaceOpened;
        Sdb.Changed -= OnSdbChanged;
        _searchCts?.Cancel();
        _selection.ClearFrom(Source);
    }

    public void FocusSearch()
    {
        Search.Focus();
        Search.SelectAll();
    }

    /// <summary>A material name from another window: search for it and select the exact match.</summary>
    public void Reveal(object payload)
    {
        if (payload is not string name) return;
        _ = RevealAsync(name);
    }

    private async Task RevealAsync(string name)
    {
        if (_index is null) await OpenAsync();
        Search.Text = name;
        _debounce.Stop();
        await RunSearchAsync();
        if (Rows.ItemsSource is not MaterialRowList list || _index is not { } index) return;
        var hits = index.File.FindMaterial(name);
        if (hits.Count == 0) return;
        int i = Array.IndexOf(list.Materials, hits[0]);
        if (i < 0) return;
        Rows.SelectedIndex = i;
        Rows.ScrollIntoView(Rows.SelectedItem);
    }

    private void OnWorkspaceOpened()
    {
        _searchCts?.Cancel();
        _index = null;
        Rows.ItemsSource = null;
        _selection.ClearFrom(Source);
        _preset = "";
        if (_ready) _ = OpenAsync();
    }

    private void OnSdbChanged()
    {
        if (!_ready) return;
        if (Sdb.Index is { } index && !ReferenceEquals(index, _index)) _ = OpenAsync();
        else if (Sdb.Index is null) ShowNote(Sdb.Error ?? Waiting());
    }

    private string Waiting() =>
        Sdb.Available ? $"reading {System.IO.Path.GetFileName(Sdb.Path)}..." : NoDatabase();

    private string NoDatabase() =>
        _ws.Install is null
            ? "Open a game to read its material database."
            : $"{_ws.Title} ships no shader database, so it has no materials to list.";

    /// <summary>Open the database (the first window to ask pays for it) and show what it holds.</summary>
    private async Task OpenAsync()
    {
        if (!Sdb.Available)
        {
            ShowNote(NoDatabase());
            return;
        }
        ShowNote(Waiting());
        var index = await Sdb.EnsureAsync();
        if (index is null)
        {
            ShowNote(Sdb.Error ?? "the database could not be read");
            return;
        }
        if (!ReferenceEquals(index, Sdb.Index)) return;    // a newer open won

        _index = index;
        BuildPresetFilter(index);
        await RunSearchAsync();
    }

    private void ShowNote(string text)
    {
        Note.Text = text;
        Note.Visibility = Visibility.Visible;
        Rows.ItemsSource = null;
        Status.Text = "";
    }

    // ---- list ------------------------------------------------------------------------------------------------

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void PresetFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _preset = (PresetFilter.SelectedItem as PresetChoice)?.Name ?? "";
        _ = RunSearchAsync();
    }

    private sealed record PresetChoice(string Name, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>Presets that are actually in use, most-used first — 780-odd of them, so the order matters.</summary>
    private void BuildPresetFilter(SdbIndex index)
    {
        var items = new List<PresetChoice> { new("", "all presets") };
        items.AddRange(index.MaterialsByPreset
            .Where(p => p.Key.Length > 0)
            .OrderByDescending(p => p.Value.Length)
            .Select(p => new PresetChoice(p.Key, $"{p.Key}  ({p.Value.Length:N0})")));
        bool was = _ready;
        _ready = false;
        PresetFilter.ItemsSource = items;
        PresetFilter.SelectedItem = items.FirstOrDefault(i => i.Name == _preset) ?? items[0];
        _preset = ((PresetChoice)PresetFilter.SelectedItem).Name;
        _ready = was;
    }

    private async Task RunSearchAsync()
    {
        if (_index is not { } index) return;
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        string text = Search.Text;
        string preset = _preset;

        SdbSearchResult result;
        try
        {
            result = await Task.Run(() => index.Search(text, cts.Token), cts.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        if (cts.IsCancellationRequested || !ReferenceEquals(index, _index)) return;

        var materials = result.Materials;
        if (preset.Length > 0)
        {
            var want = index.MaterialsByPreset.GetValueOrDefault(preset, []).ToHashSet();
            materials = [.. materials.Where(want.Contains)];
        }

        Note.Visibility = Visibility.Collapsed;
        Rows.ItemsSource = new MaterialRowList(index, materials);
        Status.Text = $"{materials.Length:N0} of {index.Count:N0} materials" +
                      (preset.Length > 0 ? $" · {preset}" : "") +
                      $" · {index.File.Layout.Name} {Sdb.Api}" +
                      $" · {result.Elapsed.TotalMilliseconds:0.#} ms";
    }

    private void Rows_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Rows.View is not GridView grid) return;
        double others = grid.Columns.Where(c => c != ColName)
            .Sum(c => double.IsNaN(c.Width) ? c.ActualWidth : c.Width);
        ColName.Width = Math.Max(160, Rows.ActualWidth - others - 40);
    }

    // ---- selection and actions -------------------------------------------------------------------------------

    private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_index is { } index && Rows.SelectedItem is MaterialRow row)
            _selection.Set(Source, new Selected.Material(index, row.Index));
        else
            _selection.ClearFrom(Source);
    }

    private MaterialRow[] Selected() => Rows.SelectedItems.Cast<MaterialRow>().ToArray();

    /// <summary>
    /// Put the whole texture set of the selected materials in the project. This is what the database is for:
    /// the exact names a material draws with, rather than a guess from the resource list.
    /// </summary>
    private void AddTextures_Click(object sender, RoutedEventArgs e)
    {
        if (_index is not { } index) return;
        var rows = Selected();
        if (rows.Length == 0) return;
        var names = TextureNames(index, rows);
        if (names.Length == 0)
        {
            Status.Text = "those materials bind no textures";
            Status.Foreground = Skin.Brush("Warn");
            return;
        }
        _host.Reveal("textures", new AddTexturesRequest(names,
            rows.Length == 1 ? rows[0].Name : $"{rows.Length} materials"));
    }

    private void ExportMat_Click(object sender, RoutedEventArgs e) => ExportMat(Selected().Select(r => r.Index).ToArray());

    private void ExportAllMat_Click(object sender, RoutedEventArgs e) =>
        ExportMat(Rows.ItemsSource is MaterialRowList list ? list.Select(r => r.Index).ToArray() : []);

    /// <summary>.mat text per material (<see cref="MatWriter"/>), on the job queue; a material it refuses is logged and skipped.</summary>
    private void ExportMat(int[] materials)
    {
        if (_index is not { } index || materials.Length == 0) return;
        var folder = Shell.PickFolder(this, $"Export {materials.Length:N0} .mat file(s) into...");
        if (folder is null) return;
        var sdb = index.File;
        string what = $"export {materials.Length:N0} .mat";
        Status.Text = $"{what}: queued";
        Jobs.Run(what, "export", ctx =>
        {
            int written = 0, skipped = 0;
            for (int i = 0; i < materials.Length; i++)
            {
                ctx.Token.ThrowIfCancellationRequested();
                var mat = sdb.Material(materials[i]);
                if (i % 64 == 0) ctx.Step(i, materials.Length, mat.Name);
                try
                {
                    string name = mat.Name.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) ? mat.Name : mat.Name + ".mat";
                    System.IO.File.WriteAllText(System.IO.Path.Combine(folder, RawExporter.SafeName(name)), MatWriter.Write(sdb, mat));
                    written++;
                }
                catch (SdbFormatException x)
                {
                    Log.Warn("export", $"{mat.Name}: {x.Message}");
                    skipped++;
                }
            }
            return $"{written:N0} .mat written" + (skipped > 0 ? $", {skipped:N0} refused (see log)" : "") + $" -> {folder}";
        });
    }

    private void CopyName_Click(object sender, RoutedEventArgs e)
    {
        var names = Selected().Select(r => r.Name).ToArray();
        if (names.Length > 0) Clipboard.SetText(string.Join(Environment.NewLine, names));
    }

    private void CopyTextures_Click(object sender, RoutedEventArgs e)
    {
        if (_index is not { } index) return;
        var names = TextureNames(index, Selected());
        if (names.Length > 0) Clipboard.SetText(string.Join(Environment.NewLine, names));
    }

    private string[] TextureNames(SdbIndex index, MaterialRow[] rows)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            try
            {
                foreach (var t in index.File.Material(row.Index).Textures) names.Add(t);
            }
            catch (SdbFormatException ex)
            {
                Core.Logging.Log.Warn("sdb", $"{row.Name}: {ex.Message}");
            }
        }
        return [.. names];
    }
}

/// <summary>Ask the Textures window to add these exact resource names to the open project.</summary>
public sealed record AddTexturesRequest(string[] Names, string Because);
