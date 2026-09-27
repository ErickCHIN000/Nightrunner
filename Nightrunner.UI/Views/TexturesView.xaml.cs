using System.Collections;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Nightrunner.Core.Export;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Project;
using Nightrunner.Core.Rpack;
using Nightrunner.Core.Texture;

namespace Nightrunner.UI.Views;

/// <summary>One texture row. The IMGC header is read the first time the row is shown, not before.</summary>
public sealed class TextureRow(RpackCatalog catalog, int gid)
{
    private TextureResource? _texture;
    private string? _error;

    public int Gid => gid;

    public TextureResource? Texture
    {
        get
        {
            if (_texture is null && _error is null)
            {
                var (entry, index) = catalog.Split(gid);
                try
                {
                    _texture = TextureResource.Open(entry.Pack!, index);
                }
                catch (Exception e)
                {
                    _error = e.Message;
                }
            }
            return _texture;
        }
    }

    public string Error => _error ?? "";
    public string Name => catalog.Name(Gid);
    public string PackLabel => catalog.Split(Gid).Entry.Label;

    public string FormatText => Texture?.Header.FormatName ?? "unreadable";

    public string SizeText => Texture is { } t
        ? t.Header.Depth > 1
            ? $"{t.Header.Width}x{t.Header.Height}x{t.Header.Depth}"
            : $"{t.Header.Width}x{t.Header.Height}"
        : "";

    public string MipText => Texture?.Header.MipCount.ToString() ?? "";
}

/// <summary>Data-virtualised list of textures — the same trick the Raw list uses.</summary>
public sealed class TextureRowList(RpackCatalog catalog, int[] gids) : IList, IReadOnlyList<TextureRow>
{
    private readonly Dictionary<int, TextureRow> _cache = [];

    public int[] Gids { get; } = gids;
    public int Count => Gids.Length;

    public TextureRow this[int index]
    {
        get
        {
            if (_cache.TryGetValue(index, out var row)) return row;
            if (_cache.Count > 20_000) _cache.Clear();
            return _cache[index] = new TextureRow(catalog, Gids[index]);
        }
    }

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    public IEnumerator<TextureRow> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public int IndexOf(object? value) => value is TextureRow r ? Array.IndexOf(Gids, r.Gid) : -1;
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

/// <summary>Textures: every 0x20 resource of the open game, with its IMGC header and a decoded preview.</summary>
public partial class TexturesView : UserControl
{
    private readonly Workspace _ws;
    private readonly Selection _selection;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _searchCts;
    private bool _ready;
    private string _formatFilter = "";
    private int _packFilter = -1;          // -1 = every pack
    private int _packCount = -1;
    private TextureRow? _current;

    private RpackCatalog Catalog => _ws.Catalog;

    public TexturesView(PanelContext ctx)
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
        _ws.Opened += OnWorkspaceOpened;
        Catalog.PackIndexed += OnPackIndexed;
        Loaded += (_, _) =>
        {
            if (_ready)
            {
                BuildPackFilter();      // shown again: the pack list may have changed while hidden
                return;
            }
            _ready = true;
            Rows_SizeChanged(Rows, null!);
            BuildFormatFilter();
            BuildPackFilter();
            _ = RunSearchAsync();
        };
        // no Unloaded handler — see RawExplorerView.Detach
    }

    private void OnWorkspaceOpened()
    {
        _packCount = -1;
        _searchCts?.Cancel();
        Rows.ItemsSource = null;
        _selection.ClearFrom("Textures");
        _current = null;
        Catalog.PackIndexed -= OnPackIndexed;
        Catalog.PackIndexed += OnPackIndexed;
        _packFilter = -1;
        _packCount = -1;
        BuildFormatFilter();
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

    /// <summary>Stop following the workspace — called when this window is closed for good.</summary>
    public void Detach()
    {
        _ws.Opened -= OnWorkspaceOpened;
        Catalog.PackIndexed -= OnPackIndexed;
        _selection.ClearFrom("Textures");
    }

    // ---- list ----------------------------------------------------------------------------------------------

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void FormatFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _formatFilter = (FormatFilter.SelectedItem as string) ?? "";
        _ = RunSearchAsync();
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

    /// <summary>Rebuild the pack list as packs finish indexing, keeping whatever is selected.</summary>
    private void BuildPackFilter()
    {
        var packs = Catalog.IndexedPacks;
        if (packs.Length == _packCount) return;
        _packCount = packs.Length;

        var items = new List<PackChoice> { new(-1, "all packs") };
        items.AddRange(packs.Select(p => new PackChoice(p.Id, p.Label)));
        bool was = _ready;
        _ready = false;
        PackFilter.ItemsSource = items;
        PackFilter.SelectedItem = items.FirstOrDefault(i => i.Id == _packFilter) ?? items[0];
        _packFilter = ((PackChoice)PackFilter.SelectedItem).Id;
        _ready = was;
    }

    private void BuildFormatFilter()
    {
        var items = new List<string> { "all formats" };
        items.AddRange(ImgcFormats.All.Where(f => f.Corpus > 0).OrderByDescending(f => f.Corpus).Select(f => f.Name));
        string keep = _formatFilter;
        bool was = _ready;
        _ready = false;
        FormatFilter.ItemsSource = items;
        FormatFilter.SelectedItem = items.Contains(keep) ? keep : items[0];
        _formatFilter = (string)FormatFilter.SelectedItem;
        _ready = was;
    }

    private async Task RunSearchAsync()
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var catalog = Catalog;
        string text = Search.Text;
        string format = _formatFilter is "all formats" or "" ? "" : _formatFilter;
        int[]? packs = _packFilter >= 0 ? [_packFilter] : null;

        SearchResult result;
        try
        {
            result = await Task.Run(() => catalog.Search(text, 0x20, packs, cts.Token), cts.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        if (cts.IsCancellationRequested || !ReferenceEquals(catalog, Catalog)) return;

        int[] gids = result.Gids;
        if (format.Length > 0)
        {
            // the format lives in the IMGC header, so this pass reads one 96-byte part per texture
            try
            {
                gids = await Task.Run(() => FilterByFormat(catalog, result.Gids, format, cts.Token), cts.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            if (cts.IsCancellationRequested) return;
        }

        Rows.ItemsSource = new TextureRowList(catalog, gids);
        BuildPackFilter();
        Status.Text = $"{gids.Length:N0} textures" +
                      (format.Length > 0 ? $" · {format}" : "") +
                      (packs is not null ? $" · {(PackFilter.SelectedItem as PackChoice)?.Text}" : "") +
                      $" · {result.Elapsed.TotalMilliseconds:0.#} ms";
    }

    private static int[] FilterByFormat(RpackCatalog catalog, int[] gids, string format, CancellationToken ct)
    {
        var keep = new List<int>(gids.Length / 4 + 16);
        foreach (int gid in gids)
        {
            ct.ThrowIfCancellationRequested();
            var (entry, index) = catalog.Split(gid);
            try
            {
                if (TextureResource.Open(entry.Pack!, index).Header.FormatName == format) keep.Add(gid);
            }
            catch (Exception e) when (e is ImgcException or RpackFormatException)
            {
            }
        }
        return keep.ToArray();
    }

    private void Rows_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Rows.View is not GridView grid) return;
        double others = grid.Columns.Where(c => c != ColName)
            .Sum(c => double.IsNaN(c.Width) ? c.ActualWidth : c.Width);
        ColName.Width = Math.Max(140, Rows.ActualWidth - others - 40);
    }

    // ---- context menu --------------------------------------------------------------------------------------

    private TextureRow[] Selected() => Rows.SelectedItems.Cast<TextureRow>().ToArray();

    /// <summary>Write the decoded surface that is on screen as a PNG.</summary>
    private void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } row || row.Texture is not { } tex) return;
        if (tex.TopLevel is not { } top || !tex.HasBitmap ||
            TextureDecoder.Reason(tex.Header.Format) is not null)
        {
            Log.Warn("texture", $"{row.Name}: nothing to export ({tex.PayloadProblem ?? "unsupported format"})");
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export the decoded texture",
            Filter = "PNG (*.png)|*.png",
            FileName = RawExporter.SafeName(System.IO.Path.GetFileNameWithoutExtension(row.Name)) + ".png",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        string path = dlg.FileName, name = row.Name;
        Jobs.Run($"export PNG {name}", "texture", _ =>
        {
            var img = tex.Decode(top);
            var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, img.Bgra, img.Stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var file = System.IO.File.Create(path);
            encoder.Save(file);
            return $"{img.Width}x{img.Height} {tex.Header.FormatName}, {Format.Size(file.Length)} -> {path}";
        });
    }

    /// <summary>
    /// Write the stored surfaces as DDS (DX10 header, blocks as stored): one texture to a file, several into a folder.
    /// </summary>
    private void ExportDds_Click(object sender, RoutedEventArgs e)
    {
        var rows = Selected();
        if (rows.Length == 0) return;
        string? file = null, folder = null;
        if (rows.Length == 1)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export DDS",
                Filter = "DDS (*.dds)|*.dds",
                FileName = RawExporter.SafeName(System.IO.Path.GetFileNameWithoutExtension(rows[0].Name)) + ".dds",
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            file = dlg.FileName;
        }
        else if ((folder = Shell.PickFolder(this, "Export DDS into...")) is null) return;

        Jobs.Run($"export DDS {rows.Length} texture(s) -> {file ?? folder}", "texture", ctx =>
        {
            int written = 0;
            long bytes = 0;
            for (int i = 0; i < rows.Length && !ctx.Token.IsCancellationRequested; i++)
            {
                var row = rows[i];
                ctx.Step(i, rows.Length, row.Name);
                if (row.Texture is not { } tex || (tex.PayloadProblem ?? DdsWriter.Refusal(tex.Header)) is not null)
                {
                    Log.Warn("texture", $"{row.Name}: {row.Texture?.PayloadProblem ?? (row.Texture is null ? row.Error : DdsWriter.Refusal(row.Texture.Header))}");
                    continue;
                }
                string path = file ?? System.IO.Path.Combine(folder!,
                    RawExporter.SafeName(System.IO.Path.GetFileNameWithoutExtension(row.Name)) + ".dds");
                try
                {
                    using var fs = System.IO.File.Create(path);
                    DdsWriter.Write(tex, fs);
                    bytes += fs.Length;
                    written++;
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ImgcException)
                {
                    Log.Warn("texture", $"{row.Name}: {ex.Message}");
                }
            }
            return $"{written} of {rows.Length} written, {Format.Size(bytes)}";
        });
    }

    /// <summary>Write the texture's own bytes (IMGC header + bitmap) so it can be rebuilt later.</summary>
    private void ExportRaw_Click(object sender, RoutedEventArgs e)
    {
        var rows = Selected();
        if (rows.Length == 0) return;
        var folder = Shell.PickFolder(this, "Export the raw texture parts into...");
        if (folder is null) return;
        Export(rows, folder, $"export {rows.Length} texture(s)");
    }

    /// <summary>
    /// Put the selected textures in the project as editable PNGs: <c>&lt;project&gt;/textures/&lt;exact resource
    /// name&gt;</c>, each with a sidecar recording the original IMGC header and container words so the Build
    /// window can write it back in its own format.
    /// </summary>
    private void AddToProject_Click(object sender, RoutedEventArgs e) => AddToProject(Selected());

    private void AddToProject(TextureRow[] rows)
    {
        if (rows.Length == 0) return;
        if (_ws.Project is not { } project)
        {
            Log.Warn("texture", "no project is open - open or create one in the Projects window first");
            Status.Text = "no project open";
            Status.Foreground = Skin.Brush("Warn");
            return;
        }

        var folder = System.IO.Path.Combine(project.Folder, TextureAsset.Folder);
        using var op = Log.Start("texture", $"add {rows.Length} texture(s) to project {project.Name}");
        int written = 0, skipped = 0;
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            foreach (var picked in rows)
            {
                // the template is the stock copy, never a mod's
                var row = picked;
                try
                {
                    int gid = Nightrunner.Core.Games.StockCopy.Resource(Catalog, picked.Gid, _ws.Origins);
                    if (gid != picked.Gid) row = new TextureRow(Catalog, gid);
                }
                catch (ProjectException ex)
                {
                    Log.Warn("texture", ex.Message);
                    skipped++;
                    continue;
                }
                if (row.Texture is not { } tex)
                {
                    Log.Warn("texture", $"{row.Name}: {row.Error}");
                    skipped++;
                    continue;
                }
                if (tex.TopLevel is not { } top || !tex.HasBitmap)
                {
                    Log.Warn("texture", $"{row.Name}: {tex.PayloadProblem ?? "no bitmap"}");
                    skipped++;
                    continue;
                }
                if (TextureDecoder.Reason(tex.Header.Format) is { } why)
                {
                    Log.Warn("texture", $"{row.Name}: {why}");
                    skipped++;
                    continue;
                }

                var (entry, index) = Catalog.Split(row.Gid);
                if (TextureAsset.IsHdr(tex.Header.Format))
                {
                    // HDR: the editable source is the whole texture as linear floats, every mip kept
                    var dds = System.IO.Path.Combine(folder, TextureAsset.HdrName(row.Name));
                    System.IO.File.WriteAllBytes(dds, DdsWriter.ToFloatDds(tex));
                    TextureAsset.From(entry.Pack!, index, tex, entry.Label, _ws.GameId).Save(dds + TextureAsset.Extension);
                    written++;
                    Log.Info("texture", $"{row.Name}: {tex.Header} -> {dds}");
                    continue;
                }
                var png = System.IO.Path.Combine(folder, TextureAsset.PngName(row.Name));
                var image = tex.Decode(top, rebuildNormalZ: false);
                var bmp = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null,
                                              image.Bgra, image.Stride);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using (var file = System.IO.File.Create(png)) encoder.Save(file);

                TextureAsset.From(entry.Pack!, index, tex, entry.Label, _ws.GameId)
                    .Save(png + TextureAsset.Extension);
                written++;
                Log.Info("texture", $"{row.Name}: {image.Width}x{image.Height} {tex.Header.FormatName} -> {png}");
            }
            op.Result = $"{written} file(s) in {folder}" + (skipped > 0 ? $", {skipped} skipped" : "");
            if (skipped > 0) op.Level = LogLevel.Warn;
            if (written > 0) _ws.NotifyProjectContentChanged();
            Status.Text = $"added {written} texture(s) to {project.Name}";
            Status.Foreground = Skin.Brush("FgDim");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ImgcException)
        {
            op.Failed(ex.Message);
            Status.Text = ex.Message;
            Status.Foreground = Skin.Brush("Error");
        }
    }

    private void Export(TextureRow[] rows, string folder, string what)
    {
        var catalog = Catalog;
        var gids = rows.Select(r => r.Gid).ToArray();
        Status.Text = $"{what}: queued";
        Jobs.Run(what, "export", ctx =>
        {
            var r = RawExporter.ExportResources(catalog, gids, folder, Jobs.Bytes(ctx, what), ctx.Token, what);
            if (r.Errors.Count > 0) throw new System.IO.IOException($"{r.Summary}; first: {r.Errors[0]}");
            return r.Summary;
        }, log: false);
    }

    private void CopyName_Click(object sender, RoutedEventArgs e)
    {
        var names = Selected().Select(r => r.Name).ToArray();
        if (names.Length > 0) Clipboard.SetText(string.Join(Environment.NewLine, names));
    }

    // ---- what other windows ask for -------------------------------------------------------------------------

    /// <summary>
    /// Show or act on something another window found: a resource name to look up, or a set of names to put in
    /// the project (which is how the Materials window hands over a material's whole texture set).
    /// </summary>
    public void Reveal(object payload)
    {
        switch (payload)
        {
            case string name:
                Search.Text = name;
                _debounce.Stop();
                _ = ShowFirst(name);
                break;
            case AddTexturesRequest request:
                AddNamed(request);
                break;
            case int gid:
                Search.Text = Catalog.Name(gid);
                _debounce.Stop();
                _ = ShowGid(gid);
                break;
        }
    }

    private async Task ShowFirst(string name)
    {
        await RunSearchAsync();
        if (Rows.ItemsSource is not TextureRowList list) return;
        for (int i = 0; i < list.Count; i++)
        {
            if (!string.Equals(list[i].Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            Rows.SelectedIndex = i;
            Rows.ScrollIntoView(list[i]);
            return;
        }
    }

    private async Task ShowGid(int gid)
    {
        await RunSearchAsync();
        if (Rows.ItemsSource is not TextureRowList list) return;
        int i = Array.IndexOf(list.Gids, gid);
        if (i < 0) return;
        Rows.SelectedIndex = i;
        Rows.ScrollIntoView(list[i]);
    }

    /// <summary>Look the names up in the catalog, then add exactly what the packs actually provide.</summary>
    private void AddNamed(AddTexturesRequest request)
    {
        var catalog = Catalog;
        var gids = new List<int>(request.Names.Length);
        var missing = new List<string>();
        foreach (var name in request.Names)
        {
            var hits = catalog.Lookup(name, 0x20);
            if (hits.Length == 0) missing.Add(name);
            else gids.Add(hits[0]);          // the first pack that provides it is the one the game loads
        }
        foreach (var name in missing)
            Log.Warn("texture", $"{request.Because}: no loaded pack provides '{name}'");

        if (gids.Count == 0)
        {
            Status.Text = $"{request.Because}: none of its {request.Names.Length} texture(s) are in a loaded pack";
            Status.Foreground = Skin.Brush("Warn");
            return;
        }
        Log.Info("texture", $"{request.Because}: {gids.Count} of {request.Names.Length} texture(s) found");
        AddToProject([.. gids.Select(g => new TextureRow(catalog, g))]);
    }

    // ---- selection -----------------------------------------------------------------------------------------

    /// <summary>The Inspector shows the picture; this window is the list.</summary>
    private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _current = Rows.SelectedItem as TextureRow;
        if (_current is { } row) _selection.Set("Textures", new Selected.Texture(Catalog, row.Gid));
        else _selection.ClearFrom("Textures");
    }
}
