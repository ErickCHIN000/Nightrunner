using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nightrunner.Core.Games;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Project;

namespace Nightrunner.UI.Views;

/// <summary>
/// One project item on the Build tab: what it outputs, from what, the file the build reads, edits pending, and what the
/// last build and the last check said about it.
/// </summary>
public sealed class BuildItemRow(BuildItemInfo info, string projectFolder, BuildItemState? last, BuildItemState? check)
{
    public BuildItemInfo Info => info;
    public BuildItemState? Last => last;
    public BuildItemState? Check => check;

    public string Kind => info.Kind;
    public string Name => info.Name;
    public string NameTip => info.Refusal ?? Rel(info.Folder);
    public Brush NameBrush => Skin.Brush(info.Refusal is null ? "FgBright" : "FgDim");
    public string Form => info.Form;
    public string Pack => info.Pack;
    public string Template => info.Template;
    public string SourceText => info.Source is { } s ? Path.GetFileName(s) : "";
    public string SourceTip => info.Source is { } s ? Rel(s) : "";
    public string ChangedText => info.Changed switch { true => "edited", false => "export", _ => "" };
    public Brush ChangedBrush => Skin.Brush(info.Changed == true ? "FgBright" : "FgDim");
    public string Edits => info.Edits;

    public string LastText => last is null ? "" : Warned(last.Result, last.Warnings.Count);
    public string LastTip => last is null ? "" : Tip(last);
    public Brush LastBrush => Skin.Brush(Tone(last?.Result, last?.Warnings.Count ?? 0));
    public string DiffText => last?.Diff ?? "";
    public Brush DiffBrush => Skin.Brush(DiffText is "added" or "changed" ? "FgBright" : "FgDim");

    /// <summary>The check's verdict word and would-be diff; before a check, a refusal found without building.</summary>
    public string CheckText => check is null ? (info.Refusal is null ? "" : "refused")
        : Warned(check.Diff.Length > 0 ? $"{Word(check.Result)} · {check.Diff}" : Word(check.Result), check.Warnings.Count);
    public string CheckTip => check is null ? info.Refusal ?? "" : Tip(check);
    public Brush CheckBrush => Skin.Brush(check is null ? (info.Refusal is null ? "FgDim" : "Warn") : Tone(check.Result, check.Warnings.Count));

    private string Rel(string path) => Path.GetRelativePath(projectFolder, path).Replace('\\', '/');
    private static string Word(string result) => result.Split(' ', ',')[0];
    private static string Warned(string text, int warnings) => warnings > 0 ? $"{text} · {warnings} warn" : text;
    private static string Tip(BuildItemState s) => s.Warnings.Count == 0 ? s.Result : s.Result + "\n" + string.Join("\n", s.Warnings);

    private static string Tone(string? result, int warnings) => result switch
    {
        null or "" or "—" => "FgDim",
        _ when result.StartsWith("refused", StringComparison.Ordinal) || result.StartsWith("invalid", StringComparison.Ordinal) => "Error",
        _ when result.StartsWith("skipped", StringComparison.Ordinal) || warnings > 0 => "Warn",
        _ => "Fg",
    };
}

/// <summary>One output file: where it goes, its mod.json load point (blank: a stock folder), and its resources' diff.</summary>
public sealed class OutputRow(BuildOutput output, string last, string check)
{
    public string File => output.File;
    public string Destination => output.Destination;
    /// <summary>The mod.json load point (NightrunnerProxy mod folder); blank for a stock folder.</summary>
    public string OrderText => output.At ?? "";
    public string LastText => last;
    public string CheckText => check;
}

/// <summary>A kind filter entry: one kind (null: all) and how many items it has.</summary>
public sealed record KindChoice(string? Kind, int Count)
{
    public override string ToString() => $"{Kind ?? "all"} {Count}";
}

/// <summary>
/// Build: every item of the project, one row each (textures, meshes, scenes, models, clips, prefab edits), with its
/// output, template, source, edits, the last build's result and diff and the last check's; the files the build writes
/// with their destinations and load orders; Check (a dry run) and Build.
/// </summary>
public partial class BuildView : UserControl
{
    private const string Source = "Build";
    private readonly Workspace _ws;
    private readonly Selection _selection;
    private readonly IPanelHost _host;
    private List<BuildItemInfo> _items = [];
    private List<BuildItemRow> _rows = [];
    private ProjectAssets.Contents? _contents;
    private JsonObject? _last, _check;
    private DateTime? _lastWritten;
    private BuildReportView? _lastView, _checkView;
    private int _version;
    private bool _filling, _restoring;
    private ModProject? _shown;
    private (string Kind, string Key, string Name)? _picked;

    public BuildView(PanelContext ctx)
    {
        _ws = ctx.Workspace;
        _selection = ctx.Selection;
        _host = ctx.Host;
        InitializeComponent();
        _ws.ProjectChanged += ShowProject;
        _ws.ProjectContentChanged += Refresh;
        // AvalonDock reloads a tab's content each time the tab comes back: keep what is shown (rows, the check, the
        // pick) unless the project changed meanwhile
        Loaded += (_, _) =>
        {
            if (!ReferenceEquals(_shown, Project)) ShowProject();
        };
    }

    /// <summary>Stop following the workspace — called when this window is closed for good.</summary>
    public void Detach()
    {
        _ws.ProjectChanged -= ShowProject;
        _ws.ProjectContentChanged -= Refresh;
        _selection.ClearFrom(Source);
    }

    private ModProject? Project => _ws.Project;

    /// <summary>Everything on screen comes from the current project; nothing carries over from the previous one.</summary>
    private void ShowProject()
    {
        _shown = Project;
        _picked = null;
        _version++;
        _items = [];
        _rows = [];
        _contents = null;
        _last = _check = null;
        Rows.ItemsSource = null;
        Outputs.ItemsSource = null;
        if (Project is not { } p)
        {
            Title.Text = "no project open";
            Meta.Text = "";
            OutFolder.Text = PackName.Text = PakName.Text = "";
            KindFilter.ItemsSource = null;
            Status.Text = "open a project in the Projects window first";
            return;
        }
        Title.Text = p.Name;
        Meta.Text = $"{p.Manifest.Game ?? "any game"} · {p.Folder}";
        OutFolder.Text = p.BuildFolder;
        PlainClips.IsChecked = p.Manifest.PlainClips;
        PackName.Text = p.Manifest.RpackName ?? ProjectBuild.NextFreeRpack(_ws.Install?.Assets);
        try
        {
            PakName.Text = p.Manifest.PakName ?? ProjectBuild.NextFreePak(_ws.Install?.Paths.Folder(GameFolder.Data));
        }
        catch (ProjectException)
        {
            PakName.Text = "";
        }
        Refresh();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void PlainClips_Click(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>
    /// Re-read the project (off the UI thread, once the open game has loaded): the items, the last build's report. A
    /// check result is dropped: it described the project as it was.
    /// </summary>
    private void Refresh()
    {
        if (Project is not { } p) return;
        int version = ++_version;
        var install = _ws.Install;
        var catalog = install is null ? null : _ws.Catalog;
        bool plain = PlainClips.IsChecked == true;
        string? game = _ws.GameId;
        Status.Text = "reading";
        Status.Foreground = Skin.Brush("FgDim");
        Task.Run(async () =>
        {
            if (install is not null) await _ws.WaitLoaded();
            var models = install is null ? null : _ws.Models;
            var items = ProjectBuild.Overview(p, catalog, models, plain, game);
            var contents = ProjectAssets.Scan(p);
            var last = ProjectBuild.LastReport(p);
            string json = Path.Combine(p.BuildFolder, $"{p.Name}.build.json");
            DateTime? written = File.Exists(json) ? File.GetLastWriteTime(json) : null;
            return (items, contents, last, written);
        }).ContinueWith(t =>
        {
            if (version != _version || !ReferenceEquals(Project, p)) return;
            if (t.Exception is { } ex)
            {
                Status.Text = ex.GetBaseException().Message;
                Status.Foreground = Skin.Brush("Error");
                return;
            }
            (_items, _contents, _last, _lastWritten) = t.Result;
            _check = null;
            Show();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Lay the reports over the items and fill the rows, the kind filter, the outputs and the status line.</summary>
    private void Show()
    {
        if (Project is not { } p) return;
        _lastView = _last is null ? null : ProjectBuild.Attribute(_items, _last);
        _checkView = _check is null ? null : ProjectBuild.Attribute(_items, _check);
        _rows = _items.Select((x, i) => new BuildItemRow(x, p.Folder, _lastView?.Items[i], _checkView?.Items[i])).ToList();

        string? kind = (KindFilter.SelectedItem as KindChoice)?.Kind;
        var choices = new List<KindChoice> { new(null, _rows.Count) };
        choices.AddRange(_rows.GroupBy(r => r.Kind).Select(g => new KindChoice(g.Key, g.Count())));
        _filling = true;
        KindFilter.ItemsSource = choices;
        KindFilter.SelectedItem = choices.FirstOrDefault(c => c.Kind == kind) ?? choices[0];
        _filling = false;
        ApplyFilter();
        ShowOutputs();

        var sb = new StringBuilder($"{_rows.Count} items");
        int refused = _rows.Count(r => r.Info.Refusal is not null);
        if (refused > 0) sb.Append($" · {refused} refused");
        sb.Append(_lastView is null ? " · not built" : $" · last {_lastWritten:yyyy-MM-dd HH:mm}: {_lastView.Verdict}");
        if (_checkView is not null) sb.Append($" · check: {_checkView.Verdict}");
        var other = (_checkView ?? _lastView)?.Other ?? [];
        var removed = (_checkView ?? _lastView)?.Removed ?? [];
        if (other.Count > 0) sb.Append($" · {other.Count} other warn");
        if (removed.Count > 0) sb.Append($" · {removed.Count} removed");
        Status.Text = sb.ToString();
        Status.ToolTip = other.Count + removed.Count > 0 ? string.Join("\n", other.Concat(removed.Select(r => $"removed {r}"))) : null;
        bool bad = _checkView is { } c ? c.Refused is not null || !c.Verified : _lastView is { Verified: false };
        Status.Foreground = Skin.Brush(bad ? "Error" : "FgDim");
    }

    private void KindFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling) ApplyFilter();
    }

    /// <summary>
    /// The rows of the picked kind; the picked item stays picked when it is still listed (and the Inspector, when it
    /// still shows it, gets its new state).
    /// </summary>
    private void ApplyFilter()
    {
        var keep = _picked;
        string? kind = (KindFilter.SelectedItem as KindChoice)?.Kind;
        var shown = kind is null ? _rows : _rows.Where(r => r.Kind == kind).ToList();
        _restoring = true;
        try
        {
            Rows.ItemsSource = shown;
            if (keep is { } k) Rows.SelectedItem = shown.FirstOrDefault(r => r.Kind == k.Kind && r.Info.Key == k.Key && r.Name == k.Name);
        }
        finally
        {
            _restoring = false;
        }
    }

    private void Names_Changed(object sender, TextChangedEventArgs e) => ShowOutputs();

    /// <summary>The files a build writes with the names in the boxes, their destinations, load orders and diffs.</summary>
    private void ShowOutputs()
    {
        if (_contents is not { } c || Outputs is null) return;
        string pak = PakName.Text.Trim();
        bool textures = _items.Any(i => i.Kind == "texture" && i.Refusal is null);
        var list = ProjectBuild.Outputs(c, textures, PackName.Text, pak.Length > 0 ? pak : null, _ws.Install,
                                        Project is { } p ? ModIdFor(p) : null);
        Outputs.ItemsSource = list.Select(o => new OutputRow(o, DiffSummary(_last, o.Kind), DiffSummary(_check, o.Kind))).ToList();
    }

    /// <summary>A report's diff for one output: "2 changed · 5 same"; "unknown" for a report from before hashes.</summary>
    private static string DiffSummary(JsonObject? report, string kind)
    {
        if (report is null) return "";
        if (report["diff"] is not JsonObject diff || report["hashes"] is null)
            return report["outputs"]?[kind] is null ? "" : "unknown";
        var counts = diff.Where(kv => kv.Key.StartsWith(kind + "/", StringComparison.Ordinal))
            .GroupBy(kv => (string?)kv.Value ?? "").ToDictionary(g => g.Key, g => g.Count());
        return string.Join(" · ", new[] { "added", "changed", "unchanged", "removed", "unknown" }
            .Where(counts.ContainsKey).Select(s => $"{counts[s]} {(s == "unchanged" ? "same" : s)}"));
    }

    // ---- the selected item ------------------------------------------------------------------------------------------

    private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = Rows.SelectedItem as BuildItemRow;
        RevealButton.IsEnabled = row is not null;
        OpenButton.IsEnabled = row?.Info.Source is { } s && File.Exists(s);
        ViewButton.IsEnabled = row is not null && _ws.Install is not null && row.Kind is "texture" or "mesh" or "scene" or "model"
                               && row.Info.Refusal is null;
        if (row is null || Project is not { } p) return;
        _picked = (row.Kind, row.Info.Key, row.Name);
        if (_restoring && !(_selection.Source == Source && _selection.Current is Selected.BuildItem)) return;
        _selection.Set(Source, new Selected.BuildItem(p, row.Info, row.Last, row.Check));
    }

    private void Rows_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewButton.IsEnabled) ViewItem_Click(sender, e);
        else RevealItem_Click(sender, e);
    }

    private void RevealItem_Click(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is BuildItemRow row) Shell.Reveal(row.Info.Source is { } s && File.Exists(s) ? s : row.Info.Folder);
    }

    /// <summary>Open the source file with whatever Windows opens it with.</summary>
    private void OpenItem_Click(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is not BuildItemRow { Info.Source: { } source }) return;
        try
        {
            Process.Start(new ProcessStartInfo(source) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Status.Text = $"{Path.GetFileName(source)}: {ex.Message}";
            Status.Foreground = Skin.Brush("Warn");
        }
    }

    /// <summary>
    /// Show what the item replaces in the window that shows that kind: a texture in Textures, a mesh's template in the
    /// Viewport, a model (a scene's, an override's stock member) in the Viewport and the Inspector.
    /// </summary>
    private void ViewItem_Click(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is not BuildItemRow row) return;
        try
        {
            switch (row.Info.Item)
            {
                case TextureAsset a:
                    _host.Reveal("textures", a.Resource);
                    break;
                case MeshItem m:
                    var catalog = _ws.Catalog;
                    var (pack, index) = ProjectBuild.TemplateOf(catalog, m.SourcePack, m.SourceIndex, row.Info.Key);
                    var entry = catalog.Packs.First(p => ReferenceEquals(p.Pack, pack));
                    _host.Reveal("viewport", new IsolateRequest(catalog, entry.Base + index, null, null));
                    break;
                case SceneItem or ModelItem when row.Info.Model is { } name && _ws.Models is { } models:
                    var model = models.Find(name) ?? throw new ProjectException($"{name} is not in the open game");
                    _selection.Set(Source, new Selected.Model(models, StockCopy.Model(models, model, _ws.Origins)));
                    _host.Open("viewport");
                    break;
            }
        }
        catch (ProjectException ex)
        {
            Status.Text = ex.Message;
            Status.Foreground = Skin.Brush("Warn");
        }
    }

    // ---- build folder, check, build -----------------------------------------------------------------------------------

    /// <summary>A different build folder is the project's own setting: stored in its manifest.</summary>
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (Project is not { } p) return;
        if (Shell.PickFolder(this, "Write the pack into...", p.BuildFolder) is not { } folder) return;
        try
        {
            p.SetBuildFolder(folder);
            Log.Info("build", $"{p.Name}: build folder {p.BuildFolder}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("build", $"{p.Name}: {ex.Message}");
        }
        OutFolder.Text = p.BuildFolder;
        Refresh();
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (Project is { } p) Shell.Reveal(p.BuildFolder);
    }

    /// <summary>The output names from the boxes, refused by name before anything runs; null when refused.</summary>
    private BuildEnv? Env(ModProject project)
    {
        string rpack = ProjectBuilder.PackName(PackName.Text), pakText = PakName.Text.Trim(), pak = ProjectBuild.PakName(pakText);
        if ((ProjectBuild.RpackRefusal(rpack, _ws.Install) ?? (pakText.Length > 0 ? ProjectBuild.PakRefusal(pak) : null)) is { } refusal)
        {
            Log.Warn("build", $"{project.Name}: {refusal}");
            Status.Text = refusal;
            Status.Foreground = Skin.Brush("Error");
            return null;
        }
        return new BuildEnv(_ws.Install, _ws.Catalog, _ws.Models, LoadPng, rpack, pakText.Length > 0 ? pak : null, _ws.RuntimeModding,
                            PlainClips.IsChecked == true) { ModId = ModIdFor(project) };
    }

    /// <summary>
    /// With runtime modding on for the open game, the build is laid out as a NightrunnerProxy mod folder,
    /// <c>&lt;build&gt;\&lt;id&gt;\</c> with mod.json (<see cref="ProjectBuild.WriteModFolder"/>); else null (stock folders).
    /// </summary>
    private string? ModIdFor(ModProject project) =>
        _ws.Install is not null && _ws.RuntimeModding ? ProjectBuild.ModId(project.Name) : null;

    /// <summary>
    /// Check: the whole build run on a copy of the project into a temp folder that is deleted after
    /// (<see cref="ProjectBuild.Check"/>); each row gets its verdict, warnings and would-be diff. Nothing is kept.
    /// </summary>
    private void Check_Click(object sender, RoutedEventArgs e)
    {
        if (Project is not { } project || Env(project) is not { } env) return;
        Status.Text = "check queued";
        Status.Foreground = Skin.Brush("FgDim");
        Jobs.Run($"check {project.Name}", "check", ctx =>
        {
            var r = ProjectBuild.Check(project, env, t => ctx.Report(null, t), ctx.Token);
            Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(Project, project)) return;
                _check = r.Report;
                Show();
            });
            if (r.Refused is not null || !r.Ok) throw new IOException(r.Verdict);
            return $"{r.Verdict}" + (r.Warnings.Count > 0 ? $"; {r.Warnings.Count} warning(s)" : "");
        });
    }

    /// <summary>The full build of every item, on the job queue; the tab re-reads the project and its report after.</summary>
    private void Build_Click(object sender, RoutedEventArgs e)
    {
        if (Project is not { } project)
        {
            Log.Warn("build", "no project open");
            return;
        }
        if (Env(project) is not { } env) return;
        project.SetOutputNames(env.RpackName, env.PakName);
        project.SetPlainClips(env.PlainClips);
        Status.Text = "build queued";
        Status.Foreground = Skin.Brush("FgDim");
        Jobs.Run($"build {project.Name}", "build", ctx =>
        {
            var r = ProjectBuild.Build(project, env, t => ctx.Report(null, t), ctx.Token);
            if (!r.Verified) throw new IOException(r.Verdict);
            var files = new[] { r.RpackPath, r.AnimsRpackPath, r.PakPath }.Where(f => f is not null).Select(Path.GetFileName);
            return $"{string.Join(" + ", files)}{(r.ModFolder is { } mod ? $" + mod {Path.GetFileName(mod)}" : "")}; {r.Verdict}" +
                   (r.Warnings.Count > 0 ? $"; {r.Warnings.Count} warning(s)" : "");
        });
    }

    /// <summary>PNG → BGRA32 pixels.</summary>
    internal static (byte[] Bgra, int Width, int Height) LoadPng(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource source = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return (pixels, source.PixelWidth, source.PixelHeight);
    }
}
