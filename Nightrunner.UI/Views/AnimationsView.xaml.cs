using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Nightrunner.Core.Anim;
using Nightrunner.Core.Cast;
using Nightrunner.Core.Model;

namespace Nightrunner.UI.Views;

/// <summary>One SeqTrack row, with the rpack its clip plays from (null: not shipped). Placeholders (no clip) are listed, dimmed.</summary>
public sealed record SequenceRow(SeqEntry Entry, string? ClipPack)
{
    public bool Missing => !Entry.Record.IsPlaceholder && ClipPack is null;
    /// <summary>The clip's rpack; the bank's for a placeholder or a clip no pack ships.</summary>
    public string Pack => ClipPack ?? Entry.Pack;
    public string PackTip => ClipPack is null || ClipPack == Entry.Pack ? Entry.Pack : $"clip {ClipPack} · bank {Entry.Pack}";
    public string Name => Entry.Record.Name;
    public string Bank => Entry.Bank;
    public string Clip => Entry.Record.IsPlaceholder ? "placeholder: no clip"
        : Missing ? $"{Entry.Record.Anm2Name}: not shipped in any loaded pack" : Entry.Record.Anm2Name;
    /// <summary>The clip the search also matches, so a hit by clip name shows why it matched.</summary>
    public string ClipName => Entry.Record.IsPlaceholder ? "" : Entry.Record.Anm2Name;
    public string Fps => Entry.Record.Fps > 0 ? Entry.Record.Fps.ToString("0.##", CultureInfo.InvariantCulture) : "";
    public string Frames => Entry.Record.IsPlaceholder ? "" : $"{Entry.Record.StartFrame:0}–{Entry.Record.EndFrame:0}";
    public double Opacity => Entry.Record.IsPlaceholder || Missing ? 0.45 : 1.0;
}

/// <summary>
/// Animations: every SeqTrack of every registered sequence bank. Selecting one plays it in the Viewport on the shown
/// model (the player model when nothing is shown) and shows its record and events in the Inspector.
/// </summary>
public partial class AnimationsView : UserControl
{
    private readonly Workspace _ws;
    private readonly Selection _selection;
    private readonly DispatcherTimer _debounce;
    private List<SequenceRow> _all = [];
    private AnimCatalog? _animations;
    private bool _ready;

    public AnimationsView(PanelContext ctx)
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
            _loading = LoadAsync();
        };
    }

    public void Detach()
    {
        _ws.Opened -= OnWorkspaceOpened;
        _selection.ClearFrom("Animations");
    }

    public void FocusSearch()
    {
        Search.Focus();
        Search.SelectAll();
    }

    /// <summary>A sequence reference (<c>bank.scr@Seq</c> or <c>Seq@bank.scr</c>) or a plain name: search and select it.</summary>
    public async void Reveal(object payload)
    {
        if (payload is not string text) return;
        if (_animations is null) _loading = LoadAsync();
        // a Reveal that opens the window starts a load that the window's own (Loaded) supersedes: wait for the last one,
        // or the search ran on an empty list and nothing was picked
        for (Task load = _loading; ; load = _loading)
        {
            await load;
            if (ReferenceEquals(load, _loading)) break;
        }
        string seq = text, bank = "";
        if ((SeqRef.TryParseGraph(text) ?? SeqRef.TryParseGds(text)) is { } r) (bank, seq) = (r.Bank, r.Seq);
        Search.Text = seq;
        _debounce.Stop();
        Filter();
        if (Rows.ItemsSource is not List<SequenceRow> rows) return;
        var hit = rows.FirstOrDefault(r => r.Name.Equals(seq, StringComparison.OrdinalIgnoreCase) && (bank.Length == 0 || r.Bank.Equals(bank, StringComparison.OrdinalIgnoreCase)))
                  ?? rows.FirstOrDefault(r => r.Name.Equals(seq, StringComparison.OrdinalIgnoreCase));
        if (hit is null) return;
        Rows.SelectedItem = hit;
        Rows.ScrollIntoView(hit);
    }

    private void OnWorkspaceOpened()
    {
        _selection.ClearFrom("Animations");
        _animations = null;
        _all = [];
        Rows.ItemsSource = null;
        if (_ready) _loading = LoadAsync();
    }

    private int _loads;
    private Task _loading = Task.CompletedTask;

    private async Task LoadAsync()
    {
        int load = ++_loads;
        Status.Text = "reading sequence banks...";
        // Opened is raised before the new game's packs start loading: waiting at once took the old game's finished
        // load, found an empty catalog and left the list empty until a restart
        await Task.Yield();
        await _ws.WaitLoaded();
        var animations = await Task.Run(() => _ws.Animations);
        if (load != _loads) return;
        _animations = animations;
        if (animations is null)
        {
            Status.Text = "no packs";
            return;
        }
        var all = await Task.Run(() =>
        {
            var packOf = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            return animations.Sequences.Select(s =>
            {
                string clip = s.Record.Anm2Name;
                if (clip.Length > 0 && !packOf.TryGetValue(clip, out var pack)) packOf[clip] = pack = animations.ClipOf(clip)?.Label;
                return new SequenceRow(s, clip.Length > 0 ? packOf[clip] : null);
            }).ToList();
        });
        if (load != _loads) return;
        _all = all;
        bool was = _ready;
        _ready = false;
        BankFilter.ItemsSource = new[] { "all banks" }.Concat(animations.Banks.Order(StringComparer.OrdinalIgnoreCase)).ToList();
        BankFilter.SelectedIndex = 0;
        // packs holding a bank or a played clip, in registration order (the order that decides which copy plays)
        var used = _all.Select(r => r.Entry.Pack).Concat(_all.Select(r => r.ClipPack).OfType<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        PackFilter.ItemsSource = new[] { "all packs" }.Concat(animations.Packs.Select(p => p.Label).Where(used.Contains)).ToList();
        PackFilter.SelectedIndex = 0;
        _ready = was;
        Filter();
    }

    private void Filter()
    {
        string[] words = Search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? bank = BankFilter.SelectedIndex > 0 ? BankFilter.SelectedItem as string : null;
        string? pack = PackFilter.SelectedIndex > 0 ? PackFilter.SelectedItem as string : null;
        var rows = _all.Where(r => (bank is null || r.Bank.Equals(bank, StringComparison.OrdinalIgnoreCase)) &&
                                   (pack is null || pack.Equals(r.ClipPack, StringComparison.OrdinalIgnoreCase) ||
                                                    pack.Equals(r.Entry.Pack, StringComparison.OrdinalIgnoreCase)) &&
                                   words.All(w => r.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                                                  r.Entry.Record.Anm2Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                                                  r.Bank.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        Rows.ItemsSource = rows;
        int missing = rows.Count(r => r.Missing);
        Status.Text = $"{rows.Count:N0} sequences" + (bank is null ? $" · {_animations?.Banks.Count ?? 0} banks" : "") +
                      (pack is not null ? $" · {pack}" : "") +
                      (missing > 0 ? $" · {missing:N0} clips not shipped" : "");
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void BankFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) Filter();
    }

    private void PackFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) Filter();
    }

    /// <summary>Show only the sequences whose clip or bank comes from <paramref name="label"/> (dock check).</summary>
    public void FilterPack(string label)
    {
        if (PackFilter.ItemsSource is IEnumerable<string> items && items.FirstOrDefault(i => i.Equals(label, StringComparison.OrdinalIgnoreCase)) is { } hit)
            PackFilter.SelectedItem = hit;
    }

    public int RowCount => (Rows.ItemsSource as List<SequenceRow>)?.Count ?? 0;

    private void Rows_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Rows.View is not GridView grid) return;
        double others = grid.Columns.Where(c => c != ColName).Sum(c => double.IsNaN(c.Width) ? c.ActualWidth : c.Width);
        ColName.Width = Math.Max(140, Rows.ActualWidth - others - 40);
    }

    private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Rows.SelectedItem is SequenceRow row && _animations is { } a) _selection.Set("Animations", new Selected.Sequence(a, row.Entry));
        else _selection.ClearFrom("Animations");
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is not SequenceRow row || row.Entry.Record.IsPlaceholder || row.Missing) return;
        string ext = (sender as FrameworkElement)?.Tag as string ?? ".cast";
        var folder = Shell.PickFolder(this, "Export the animation into...");
        if (folder is null) return;
        var catalog = _ws.Catalog;
        var entry = row.Entry;
        var shown = _ws.ShownSkeleton;
        var animations = _animations;
        Jobs.Run($"export {entry.Bank}@{entry.Record.Name}{ext}", "export", _ =>
        {
            var skeleton = shown ?? PlayerSkeleton(catalog);
            var bones = (IReadOnlyList<string>?)skeleton?.Names ?? [];
            var where = animations?.ClipOf(entry.Record.Anm2Name)
                        ?? throw new AnimBankFormatException($"clip {entry.Record.Anm2Name} is in no loaded pack");
            var clip = Anm2Resource.Decode(where.Pack, where.Index);
            if (clip.IsPoseWeights) throw new AnimBankFormatException($"{entry.Record.Name}: facial pose weights, not bone tracks");
            string name = $"{entry.Bank}@{entry.Record.Name}";
            string path = System.IO.Path.Combine(folder, Nightrunner.Core.Export.RawExporter.SafeName(name) + ext);
            if (ext == ".glb")
            {
                if (skeleton is null) throw new AnimBankFormatException("no skeleton to export the animation on");
                Gltf.Save(AnimCast.Scene(clip, skeleton, name, entry.Record.Fps, entry.Record.StartFrame, entry.Record.EndFrame), path);
            }
            else AnimCast.Build(clip, bones, entry.Record.Fps, entry.Record.StartFrame, entry.Record.EndFrame).Save(path);
            int named = clip.TrackHashes.Count(h => bones.Concat(Anm2Hash.SpecialTracks).Any(b => Anm2Hash.H41(b) == h));
            return $"{clip.TrackCount} tracks ({named} named), {clip.FrameBound + 1} keys -> {path}";
        });
    }

    private void AddToProject_Click(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is not SequenceRow row || row.Entry.Record.IsPlaceholder || row.Missing) return;
        if (_ws.Project is not { } project)
        {
            Status.Text = "open a project first";
            return;
        }
        var entry = row.Entry;
        var shown = _ws.ShownSkeleton;
        Jobs.Run($"add {entry.Record.Anm2Name} to {project.Name}", "project", _ =>
        {
            // the template is the stock clip, never a mod's
            _ws.WaitLoaded().GetAwaiter().GetResult();
            var catalog = _ws.StockCatalog();
            var item = Nightrunner.Core.Project.ProjectAssets.AddAnim(project, catalog, entry.Record.Anm2Name,
                                                                      entry.Record.Fps > 0 ? entry.Record.Fps : 30,
                                                                      shown ?? PlayerSkeleton(catalog), $"{entry.Bank}@{entry.Record.Name}");
            return $"-> {item.Folder}";
        });
    }

    /// <summary>The player skeleton, for naming tracks when no skeleton is on screen.</summary>
    private static ModelSkeleton? PlayerSkeleton(Nightrunner.Core.Rpack.RpackCatalog catalog)
    {
        foreach (var name in new[] { "sh2_player_tpp_phx_skeleton", "player_tpp_skeleton", "player_skeleton" })
            if (catalog.Lookup(name, 0x10) is { Length: > 0 } g)
            {
                var (e, i) = catalog.Split(g[0]);
                return ModelSkeleton.FromMesh(Nightrunner.Core.Mesh.MeshDecoder.Decode(e.Pack!, i), name);
            }
        return null;
    }

    private void CopyName_Click(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is SequenceRow row) Clipboard.SetText($"{row.Bank}.scr@{row.Name}");
    }
}
