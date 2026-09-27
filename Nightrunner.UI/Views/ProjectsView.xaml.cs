using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Nightrunner.Core.Logging;
using System.Text.Json.Nodes;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Nightrunner.Core.Games;
using Nightrunner.Core.Project;
using Nightrunner.Core.Texture;

namespace Nightrunner.UI.Views;

/// <summary>A project folder in the recent list.</summary>
public sealed record RecentProject(string Name, string Folder)
{
    public bool Exists => ModProject.IsProject(Folder);
}

/// <summary>
/// One texture in a project: its thumbnail, where it came from, and the format a build should write it as.
/// Changing the format saves the sidecar straight away — there is nothing else to press.
/// </summary>
public sealed class AssetRow : INotifyPropertyChanged
{
    private readonly string _sidecar;
    private ImageSource? _thumbnail;

    public AssetRow(string png, TextureAsset asset)
    {
        Path = png;
        Asset = asset;
        _sidecar = png + TextureAsset.Extension;
        var info = new FileInfo(png);
        FileSizeText = info.Exists ? Format.Size(info.Length) : "";
        ModifiedText = info.Exists ? info.LastWriteTime.ToString("yyyy-MM-dd HH:mm") : "";
    }

    public string Path { get; }
    public TextureAsset Asset { get; }
    public string Name => Asset.Resource;
    public string SizeText => $"{Asset.Width}x{Asset.Height}";
    public string OriginalFormat => Asset.FormatName;
    public string FileSizeText { get; }
    public string ModifiedText { get; }

    public string Detail => $"{Asset.SourcePack} · {Asset.Mips} mips · " +
                            (Asset.BuildFormat is null ? "as it came" : $"rebuilt as {Asset.EffectiveFormatName}");

    /// <summary>Loaded small and only when the row is shown.</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnail is not null) return _thumbnail;
            if (TextureSource.KindOf(Path) is not TextureSource.Kind.Png)
            {
                if (TextureSource.Preview(Path) is { } img)
                {
                    var src = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, img.Bgra, img.Stride);
                    src.Freeze();
                    _thumbnail = src;
                }
                return _thumbnail;
            }
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(Path);
                bmp.DecodePixelWidth = 96;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.EndInit();
                bmp.Freeze();
                _thumbnail = bmp;
            }
            catch (Exception e) when (e is IOException or NotSupportedException or UriFormatException)
            {
                _thumbnail = null;
            }
            return _thumbnail;
        }
    }

    /// <summary>The formats this source can build as (a PNG: the 8-bit encoders; a DDS or .hdr: the float ones).</summary>
    public IReadOnlyList<string> Formats => _formats ??= [.. TextureSource.BuildFormats(Path)
        .Append(Asset.EffectiveFormat).Distinct()
        .OrderByDescending(id => ImgcFormats.Get(id).Corpus).Select(ImgcFormats.Name)];

    private IReadOnlyList<string>? _formats;

    public string SelectedFormat
    {
        get => Asset.EffectiveFormatName;
        set
        {
            if (value is null || value == Asset.EffectiveFormatName) return;
            var match = ImgcFormats.All.FirstOrDefault(f => f.Name == value);
            if (match.Name is null) return;
            Asset.BuildFormat = match.Id == Asset.Format ? null : match.Id;
            try
            {
                Asset.Save(_sidecar);
                Log.Info("project", $"{Name}: build as {Asset.EffectiveFormatName}" +
                                    (Asset.BuildFormat is null ? " (back to its original format)" : ""));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Error("project", $"{Name}: could not save {_sidecar}: {e.Message}");
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedFormat)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One row of a project's contents.</summary>
public sealed class ContentRow(ProjectEntry entry)
{
    public ProjectEntry Entry => entry;
    public string Name => entry.Name;
    public string FullPath => entry.FullPath;
    public string Kind => entry.IsDirectory ? "folder" : Path.GetExtension(entry.Name).TrimStart('.').ToLowerInvariant() is { Length: > 0 } ext ? ext : "file";
    public string FilesText => entry.IsDirectory ? entry.Files.ToString("N0") : "";
    public string SizeText => Format.Size(entry.Bytes);
    public string ModifiedText => entry.Modified.ToString("yyyy-MM-dd HH:mm");

    public Brush NameBrush => Skin.Brush(entry.IsDirectory && entry.Files == 0 ? "FgDim" : "FgBright");
}

/// <summary>
/// One non-texture item on the Projects window (mesh, scene, model override, clip, prefab edits) as the Build tab reads it
/// (<see cref="BuildItemInfo"/>): its template, the file the build reads and whether it changed since the export, the
/// kind's own numbers (<see cref="BuildItemInfo.Counts"/>, by name through the indexer), and what the build refuses.
/// </summary>
public sealed class ProjectItemRow(BuildItemInfo info, string projectFolder, BuildItemState? last)
{
    public BuildItemInfo Info => info;
    public BuildItemState? Last => last;

    public string Kind => info.Kind;
    public string Name => info.Item is PrefabItem pf ? pf.SourcePack : info.Name;
    public string? NameTip => info.Refusal is { } r ? $"{Rel(info.Folder)}\n{r}" : Rel(info.Folder);

    /// <summary>The line under the name: what the build refuses about it, else its form and pending edits.</summary>
    public string Detail => info.Refusal ?? string.Join(" · ", new[] { info.Form, info.Edits }.Where(s => s.Length > 0));
    public Brush DetailBrush => Skin.Brush(info.Refusal is null ? "FgFaint" : "Warn");
    public Brush NameBrush => Skin.Brush(info.Refusal is null ? "FgBright" : "FgDim");
    public string Template => info.Template;
    public string SourceText => info.Source is { } s ? Path.GetFileName(s) : "";
    public string? SourceTip => info.Source is { } s ? Rel(s) : null;

    /// <summary>edited: the source changed since the export (a model: differs from the stock member); export / stock: not.</summary>
    public string StateText => info.Changed switch { true => "edited", false => info.Kind == "model" ? "stock" : "export", _ => "" };
    public Brush StateBrush => Skin.Brush(info.Changed == true ? "FgBright" : "FgDim");
    public string Edits => info.Edits;
    public string Modified { get; } = When(info.Source is { } s && File.Exists(s) ? File.GetLastWriteTime(s)
                                           : Directory.Exists(info.Folder) ? Directory.GetLastWriteTime(info.Folder) : null);
    /// <summary>A count by name (<see cref="BuildItemInfo.Counts"/>); blank when not known.</summary>
    public string this[string key] => info.Counts.TryGetValue(key, out int n) ? n.ToString("N0", CultureInfo.InvariantCulture) : "";

    // scene
    public string ModelName => info.Item is SceneItem && info.Model is { } m ? Path.GetFileName(m) : "";
    public string? ModelTip => info.Model;

    // model override
    public string Member => (info.Item as ModelItem)?.Member ?? "";
    public string Pak => (info.Item as ModelItem)?.Pak ?? "";
    public string Paths => (info.Item as ModelItem)?.PathMode ?? "";
    public string Gear => info.Item is ModelItem { NoGear: true } ? "off" : "";

    // clip
    public string ClipForm => info.Counts.TryGetValue("stream", out int s) ? s == 1 ? "stream" : "plain" : "";
    public string Fps => info.Item is AnimItem a ? a.Fps.ToString("0.##", CultureInfo.InvariantCulture) : "";
    public string Forced => info.Counts.TryGetValue("forced", out int f) && f == 1 ? "forced" : "";

    // prefab edits
    public string Resource => (info.Item as PrefabItem)?.SourceName ?? "";
    public string Index => info.Item is PrefabItem p ? p.SourceIndex.ToString(CultureInfo.InvariantCulture) : "";
    public string Prefabs => info.Item is PrefabItem p ? string.Join(", ", p.Edits.Select(e => e.Prefab).Distinct(StringComparer.Ordinal)) : "";

    private string Rel(string path) => System.IO.Path.GetRelativePath(projectFolder, path).Replace('\\', '/');
    private static string When(DateTime? t) => t?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";
}

/// <summary>
/// A column of a kind tab: header, binding path, width, cell style, and optional foreground and tooltip paths; with
/// <see cref="Detail"/>, a second, smaller line under the text (the Textures tab's name cell).
/// </summary>
public sealed record KindColumn(string Header, string Path, double Width, string Style, string? Brush = null, string? Tip = null)
{
    public string? Detail { get; init; }
    public string? DetailBrush { get; init; }
}

/// <summary>One kind's tab on the Projects window: its count line, Refresh / Reveal / Open / View / Remove, and the rows.</summary>
public sealed class KindTab
{
    public KindTab(string kind, string header, string noun, IEnumerable<KindColumn> columns)
    {
        Kind = kind;
        Noun = noun;
        Header.SetResourceReference(FrameworkElement.StyleProperty, "Dim");
        Header.Text = noun;
        Refresh = Make("Refresh", null);
        Reveal = Make("Reveal", "Show the item's file in Explorer");
        Open = Make("Open", "Open the file the build reads");
        View = Make("View", "Show what it replaces");
        Remove = Make("Remove", "Send the item's folder to the Recycle Bin");

        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(Header, Dock.Left);
        bar.Children.Add(Header);
        foreach (var b in new[] { Remove, View, Open, Reveal, Refresh })
        {
            DockPanel.SetDock(b, Dock.Right);
            bar.Children.Add(b);
        }
        bar.Children.Add(new TextBlock());

        List.ItemContainerStyle = new Style(typeof(ListViewItem), (Style)Application.Current.FindResource(typeof(ListViewItem)))
        {
            Setters = { new Setter(FrameworkElement.HeightProperty, 40.0) },
        };
        VirtualizingPanel.SetIsVirtualizing(List, true);
        VirtualizingPanel.SetVirtualizationMode(List, VirtualizationMode.Recycling);
        var grid = new GridView();
        foreach (var c in columns)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetResourceReference(FrameworkElement.StyleProperty, c.Style);
            text.SetBinding(TextBlock.TextProperty, new Binding(c.Path));
            if (c.Brush is not null) text.SetBinding(TextBlock.ForegroundProperty, new Binding(c.Brush));
            if (c.Tip is not null) text.SetBinding(FrameworkElement.ToolTipProperty, new Binding(c.Tip));
            text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var cell = text;
            if (c.Detail is not null)
            {
                cell = new FrameworkElementFactory(typeof(StackPanel));
                cell.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                var detail = new FrameworkElementFactory(typeof(TextBlock));
                detail.SetValue(TextBlock.FontSizeProperty, 11.0);
                detail.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
                detail.SetBinding(TextBlock.TextProperty, new Binding(c.Detail));
                if (c.DetailBrush is not null) detail.SetBinding(TextBlock.ForegroundProperty, new Binding(c.DetailBrush));
                cell.AppendChild(text);
                cell.AppendChild(detail);
            }
            grid.Columns.Add(new GridViewColumn { Header = c.Header, Width = c.Width, CellTemplate = new DataTemplate { VisualTree = cell } });
        }
        List.View = grid;

        var panel = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        panel.Children.Add(bar);
        panel.Children.Add(List);
        Tab = new TabItem { Header = header, Content = panel, Visibility = Visibility.Collapsed };
    }

    private static Button Make(string content, string? tip) =>
        new() { Content = content, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0), ToolTip = tip, IsEnabled = content == "Refresh" };

    public string Kind { get; }
    public string Noun { get; }
    public TabItem Tab { get; }
    public TextBlock Header { get; } = new();
    public ListView List { get; } = new() { SelectionMode = SelectionMode.Extended };
    public Button Refresh { get; }
    public Button Reveal { get; }
    public Button Open { get; }
    public Button View { get; }
    public Button Remove { get; }
    public ProjectItemRow? Picked => List.SelectedItem as ProjectItemRow;
}

/// <summary>
/// Projects: create, open and keep a mod project folder. Managing what is in it only — building a pack from it
/// is the Build area's job.
/// </summary>
public partial class ProjectsView : UserControl
{
    private readonly Workspace _ws;
    private readonly Selection _selection;
    private readonly IPanelHost _host;

    public ProjectsView(PanelContext ctx)
    {
        _ws = ctx.Workspace;
        _selection = ctx.Selection;
        _host = ctx.Host;
        InitializeComponent();
        BuildKindTabs();
        _ws.ProjectChanged += ShowProject;
        _ws.ProjectContentChanged += RefreshContents;
        Loaded += (_, _) =>
        {
            RefreshRecent();
            ShowProject();
        };
    }

    private ModProject? Project => _ws.Project;

    /// <summary>Stop following the workspace — called when this window is closed for good.</summary>
    public void Detach()
    {
        _ws.ProjectChanged -= ShowProject;
        _ws.ProjectContentChanged -= RefreshContents;
        _selection.ClearFrom("Projects");
    }

    /// <summary>Picking a project in the list puts it in the Inspector; double-clicking opens it.</summary>
    private void Recent_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Recent.SelectedItem is not RecentProject r)
        {
            _selection.ClearFrom("Projects");
            return;
        }
        try
        {
            _selection.Set("Projects", new Selected.Project(ModProject.Open(r.Folder)));
        }
        catch (Exception ex) when (ex is ProjectException or IOException)
        {
            Fail(ex.Message);
        }
    }

    // ---- recent --------------------------------------------------------------------------------------------

    private void RefreshRecent()
    {
        var rows = _ws.Settings.RecentProjects
            .Select(folder => new RecentProject(
                ModProject.IsProject(folder) ? SafeName(folder) : Path.GetFileName(folder) + "  (missing)", folder))
            .ToList();
        Recent.ItemsSource = rows;
    }

    private static string SafeName(string folder)
    {
        try
        {
            return ModProject.Open(folder).Name;
        }
        catch (Exception e) when (e is ProjectException or IOException)
        {
            return Path.GetFileName(folder);
        }
    }

    private void Recent_DoubleClick(object sender, RoutedEventArgs e)
    {
        if (Recent.SelectedItem is RecentProject r) Load(r.Folder);
    }

    // ---- open / create -------------------------------------------------------------------------------------

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Open a project folder" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) Load(dlg.FolderName);
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Pick (or create) the folder for the new project",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var project = ModProject.Create(dlg.FolderName, Path.GetFileName(dlg.FolderName), _ws.GameId);
            _ws.SetProject(project);
            RefreshRecent();
            Log.Info("project", $"created '{project.Name}' at {project.Folder}");
            Status.Text = $"created {project.Folder}";
        }
        catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException)
        {
            Fail(ex.Message);
        }
    }

    private void Load(string folder)
    {
        try
        {
            var project = ModProject.Open(folder);
            _ws.SetProject(project);
            RefreshRecent();
            Log.Info("project", $"opened '{project.Name}' from {folder}");
            Status.Text = $"opened {folder}";
        }
        catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException)
        {
            Fail(ex.Message);
        }
    }




    // ---- textures ------------------------------------------------------------------------------------------

    private void RefreshAssets(ModProject project)
    {
        var (ready, orphans) = TextureAsset.Scan(project.Folder);
        var rows = ready.Select(r => new AssetRow(r.Source, r.Asset)).ToList();
        Assets.ItemsSource = rows;
        AssetsHeader.Text = $"textures — {rows.Count}" +
                            (orphans.Count > 0 ? $" · {orphans.Count} PNG(s) without a sidecar" : "");
    }

    private void AssetOpen_Click(object sender, RoutedEventArgs e)
    {
        if (Assets.SelectedItem is not AssetRow row) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(row.Path)
            {
                UseShellExecute = true,
            });
            Log.Info("project", $"opened {row.Path}");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            Shell.Reveal(row.Path);
        }
    }

    /// <summary>Remove a texture from the project: the PNG and its sidecar, to the Recycle Bin, after asking.</summary>
    private void AssetRemove_Click(object sender, RoutedEventArgs e)
    {
        if (Project is not { } p) return;
        var rows = Assets.SelectedItems.Cast<AssetRow>().ToList();
        if (rows.Count == 0) return;
        var listed = string.Join(Environment.NewLine, rows.Take(10).Select(r => r.Name));
        if (MessageBox.Show(Window.GetWindow(this)!,
                $"Remove these textures from the project?{Environment.NewLine}{Environment.NewLine}{listed}" +
                $"{Environment.NewLine}{Environment.NewLine}The PNG and its sidecar go to the Recycle Bin.",
                "Remove textures", MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;

        int done = 0;
        foreach (var row in rows)
        {
            if (!p.Contains(row.Path)) continue;
            foreach (var file in new[] { row.Path, row.Path + TextureAsset.Extension })
            {
                if (!File.Exists(file)) continue;
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        file, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                               or OperationCanceledException)
                {
                    Fail(ex.Message);
                    return;
                }
            }
            done++;
        }
        Log.Info("project", $"removed {done} texture(s) from '{p.Name}'");
        Status.Text = $"removed {done} texture(s)";
        RefreshContents();
    }

    // ---- recent-list menu ----------------------------------------------------------------------------------

    private void RecentMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src &&
            ItemsControl.ContainerFromElement(Recent, src) is ListBoxItem item)
            item.IsSelected = true;
        if (Recent.SelectedItem is null) e.Handled = true;
    }

    private RecentProject? Picked => Recent.SelectedItem as RecentProject;

    private void RecentOpen_Click(object sender, RoutedEventArgs e)
    {
        if (Picked is { } r) Load(r.Folder);
    }

    private void RecentReveal_Click(object sender, RoutedEventArgs e)
    {
        if (Picked is { } r) Shell.Reveal(r.Folder);
    }

    /// <summary>Rename the project (the manifest name; the folder keeps its own name).</summary>
    private void RecentRename_Click(object sender, RoutedEventArgs e)
    {
        if (Picked is not { } r) return;
        ModProject project;
        try
        {
            project = ModProject.Open(r.Folder);
        }
        catch (Exception ex) when (ex is ProjectException or IOException)
        {
            Fail(ex.Message);
            return;
        }
        var name = PromptWindow.Ask(this, "Rename project", $"New name for {project.Name}:", project.Name);
        if (name is null || name == project.Name) return;

        using var op = Log.Start("project", $"rename '{project.Name}' to '{name}'");
        try
        {
            string was = project.Manifest.Name;
            project.Manifest.Name = name;
            project.Save();
            op.Result = $"{was} -> {name} ({project.ManifestPath})";
            if (Project is { } open && open.Folder.Equals(project.Folder, StringComparison.OrdinalIgnoreCase))
                _ws.SetProject(project);
            RefreshRecent();
            Status.Text = $"renamed to {name}";
            Status.Foreground = Skin.Brush("FgDim");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            op.Failed(ex.Message);
            Fail(ex.Message);
        }
    }

    /// <summary>Take it off the recent list only — the folder is untouched.</summary>
    private void RecentRemove_Click(object sender, RoutedEventArgs e)
    {
        if (Picked is not { } r) return;
        _ws.Settings.ForgetProject(r.Folder);
        _ws.Settings.Save();
        Log.Info("project", $"removed from the list (folder kept): {r.Folder}");
        if (Project is { } open && open.Folder.Equals(r.Folder, StringComparison.OrdinalIgnoreCase))
            _ws.SetProject(null);
        RefreshRecent();
        Status.Text = "removed from the list";
        Status.Foreground = Skin.Brush("FgDim");
    }

    /// <summary>Delete the project folder and everything in it, after a confirmation that says exactly that.</summary>
    private void RecentDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Picked is not { } r) return;
        int files = 0;
        long bytes = 0;
        try
        {
            var scan = ModProject.Open(r.Folder).Scan();
            files = scan.Files;
            bytes = scan.Bytes;
        }
        catch (Exception ex) when (ex is ProjectException or IOException)
        {
        }

        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"DELETE this project and everything inside it?\n\n{r.Folder}\n\n" +
            $"{files:N0} files, {Format.Size(bytes)} will be removed from disk.\n\n" +
            "This cannot be undone from the app.",
            "Delete project", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        using var op = Log.Start("project", $"delete {r.Folder}");
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                r.Folder, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            _ws.Settings.ForgetProject(r.Folder);
            _ws.Settings.Save();
            if (Project is { } open && open.Folder.Equals(r.Folder, StringComparison.OrdinalIgnoreCase))
                _ws.SetProject(null);
            op.Result = $"{files:N0} files ({Format.Size(bytes)}) sent to the Recycle Bin";
            RefreshRecent();
            Status.Text = "deleted";
            Status.Foreground = Skin.Brush("Warn");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            op.Failed(ex.Message);
            Fail(ex.Message);
        }
    }

    // ---- contents ------------------------------------------------------------------------------------------

    private void ShowProject()
    {
        if (Project is not { } p)
        {
            Title.Text = "no project open";
            Meta.Text = "";
            Contents.ItemsSource = null;
            Assets.ItemsSource = null;
            ContentsHeader.Text = "other content";
            AssetsHeader.Text = "textures";
            ClearItems();
            return;
        }
        Title.Text = p.Name;
        Meta.Text = $"{p.Folder} · modified {p.Manifest.Modified:yyyy-MM-dd HH:mm}";
        _selection.Set("Projects", new Selected.Project(p));
        RefreshContents();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshContents();

    private void RefreshContents()
    {
        if (Project is not { } p)
        {
            Contents.ItemsSource = null;
            Assets.ItemsSource = null;
            ClearItems();
            return;
        }
        try
        {
            var scan = p.Scan();
            var rest = scan.Entries.Where(x => !(x.IsDirectory && ItemFolders.Contains(x.Name))).ToList();
            Contents.ItemsSource = rest.Select(x => new ContentRow(x)).ToList();
            ContentsHeader.Text = $"other content — {rest.Sum(x => x.Files):N0} files · " +
                                  $"{Format.Size(rest.Sum(x => x.Bytes))}";
            RefreshAssets(p);
            RefreshItems(p);
            Status.Text = p.Folder;
            Status.Foreground = Skin.Brush("FgDim");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(ex.Message);
        }
    }

    // ---- meshes, scenes, models, clips, prefab edits ------------------------------------------------------------

    /// <summary>The kinds after Textures, in tab order: kind, tab header, the noun its count line uses.</summary>
    private static readonly (string Kind, string Header, string Noun)[] Kinds =
    [
        ("mesh", "Meshes", "meshes"), ("scene", "Scenes", "scenes"), ("model", "Models", "models"), ("anim", "Anims", "clips"),
        ("prefab", "Prefabs", "prefab edits"),
    ];

    /// <summary>Top-level folders whose content an item tab lists; "other content" leaves them out.</summary>
    private static readonly HashSet<string> ItemFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        TextureAsset.Folder, ProjectAssets.MeshFolder, ProjectAssets.SceneFolder, ProjectAssets.ModelFolder, ProjectAssets.AnimFolder,
        ProjectAssets.PrefabFolder,
    };

    private readonly Dictionary<string, KindTab> _kindTabs = [];
    private int _itemsVersion;
    private bool _restoring;

    private void BuildKindTabs()
    {
        foreach (var (kind, header, noun) in Kinds)
        {
            var tab = new KindTab(kind, header, noun, ColumnsFor(kind));
            tab.List.SelectionChanged += (_, _) => KindSelected(tab);
            tab.List.MouseDoubleClick += (_, _) =>
            {
                if (tab.View.IsEnabled) ViewItem(tab);
                else RevealItem(tab);
            };
            tab.Refresh.Click += Refresh_Click;
            tab.Reveal.Click += (_, _) => RevealItem(tab);
            tab.Open.Click += (_, _) => OpenItem(tab);
            tab.View.Click += (_, _) => ViewItem(tab);
            tab.Remove.Click += (_, _) => RemoveItems(tab);
            _kindTabs[kind] = tab;
            Tabs.Items.Add(tab.Tab);
        }
    }

    private static List<KindColumn> ColumnsFor(string kind)
    {
        static KindColumn Name(string header) =>
            new(header, nameof(ProjectItemRow.Name), 300, "CellName", nameof(ProjectItemRow.NameBrush), nameof(ProjectItemRow.NameTip))
            {
                Detail = nameof(ProjectItemRow.Detail), DetailBrush = nameof(ProjectItemRow.DetailBrush),
            };
        static KindColumn Num(string header, string key, double width) => new(header, $"[{key}]", width, "CellNum");
        static KindColumn Dim(string header, string path, double width, string? tip = null) => new(header, path, width, "CellDim", Tip: tip);
        var template = Dim("template", nameof(ProjectItemRow.Template), 190, nameof(ProjectItemRow.Template));
        var file = Dim("file", nameof(ProjectItemRow.SourceText), 190, nameof(ProjectItemRow.SourceTip));
        var state = new KindColumn("state", nameof(ProjectItemRow.StateText), 60, "CellDim", nameof(ProjectItemRow.StateBrush));
        var modified = Dim("modified", nameof(ProjectItemRow.Modified), 110);
        return kind switch
        {
            "mesh" =>
            [
                Name("mesh"), template, file, state, Num("verts", "vertices", 70), Num("subs", "submeshes", 50), modified,
            ],
            "scene" =>
            [
                Name("scene"), Dim("model", nameof(ProjectItemRow.ModelName), 190, nameof(ProjectItemRow.ModelTip)), file, state,
                Num("parts", "parts", 50), Num("assigned", "assigned", 70), Num("skipped", "skipped", 64), Num("hidden", "hidden", 56),
                modified,
            ],
            "model" =>
            [
                Name("model"), Dim("member", nameof(ProjectItemRow.Member), 250, nameof(ProjectItemRow.Member)),
                Dim("pak", nameof(ProjectItemRow.Pak), 76), Dim("paths", nameof(ProjectItemRow.Paths), 62), Num("off", "off", 44),
                Num("swaps", "swap", 52), Num("rtti", "rtti", 44), Dim("gear", nameof(ProjectItemRow.Gear), 48), state, modified,
            ],
            "anim" =>
            [
                Name("clip"), template, Dim("form", nameof(ProjectItemRow.ClipForm), 64),
                new("fps", nameof(ProjectItemRow.Fps), 48, "CellNum"), file, state, Dim("plain", nameof(ProjectItemRow.Forced), 60), modified,
            ],
            _ =>
            [
                Name("pack"), Dim("resource", nameof(ProjectItemRow.Resource), 100), new("index", nameof(ProjectItemRow.Index), 56, "CellNum"),
                Num("edits", "edits", 50), Dim("prefabs", nameof(ProjectItemRow.Prefabs), 280, nameof(ProjectItemRow.Prefabs)), modified,
            ],
        };
    }

    /// <summary>
    /// Read every item the way the Build tab does (<see cref="ProjectBuild.Overview"/>, off the UI thread, once the open
    /// game has loaded, so both windows agree) and lay the last build's report over them for the Inspector.
    /// </summary>
    private void RefreshItems(ModProject p)
    {
        int version = ++_itemsVersion;
        var install = _ws.Install;
        var catalog = install is null ? null : _ws.Catalog;
        string? game = _ws.GameId;
        Task.Run(async () =>
        {
            if (install is not null) await _ws.WaitLoaded();
            var models = install is null ? null : _ws.Models;
            var items = ProjectBuild.Overview(p, catalog, models, p.Manifest.PlainClips, game);
            var last = ProjectBuild.LastReport(p);
            return (Items: items, Last: last is null ? null : ProjectBuild.Attribute(items, last));
        }).ContinueWith(t =>
        {
            if (version != _itemsVersion || !ReferenceEquals(Project, p)) return;
            if (t.Exception is { } ex)
            {
                Fail(ex.GetBaseException().Message);
                return;
            }
            ShowItems(p, t.Result.Items, t.Result.Last);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>One tab per kind the project has; the picked row stays picked (without re-publishing it) when still listed.</summary>
    private void ShowItems(ModProject p, List<BuildItemInfo> items, BuildReportView? last)
    {
        foreach (var (kind, _, noun) in Kinds)
        {
            var tab = _kindTabs[kind];
            var keep = tab.Picked is { } was ? (was.Info.Key, was.Info.Name) : ((string Key, string Name)?)null;
            var rows = items.Select((x, i) => (x, i)).Where(e => e.x.Kind == kind)
                .Select(e => new ProjectItemRow(e.x, p.Folder, last?.Items[e.i])).ToList();
            _restoring = true;
            try
            {
                tab.List.ItemsSource = rows;
                if (keep is { } k) tab.List.SelectedItem = rows.FirstOrDefault(r => r.Info.Key == k.Key && r.Info.Name == k.Name);
            }
            finally
            {
                _restoring = false;
            }
            int edited = rows.Count(r => r.Info.Changed == true), problems = rows.Count(r => r.Info.Refusal is not null);
            tab.Header.Text = $"{noun} — {rows.Count}" + (edited > 0 ? $" · {edited} edited" : "") +
                              (problems > 0 ? $" · {problems} problem(s)" : "");
            tab.Tab.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Enable(tab);
        }
        if (Tabs.SelectedItem is TabItem { Visibility: not Visibility.Visible }) Tabs.SelectedIndex = 0;
    }

    private void ClearItems()
    {
        _itemsVersion++;
        foreach (var tab in _kindTabs.Values)
        {
            tab.List.ItemsSource = null;
            tab.Tab.Visibility = Visibility.Collapsed;
        }
        if (Tabs.SelectedItem is TabItem { Visibility: not Visibility.Visible }) Tabs.SelectedIndex = 0;
    }

    private void Enable(KindTab tab)
    {
        var row = tab.Picked;
        tab.Reveal.IsEnabled = row is not null;
        tab.Open.IsEnabled = row?.Info.Source is { } s && File.Exists(s);
        tab.View.IsEnabled = row is not null && CanView(row);
        tab.Remove.IsEnabled = tab.List.SelectedItems.Count > 0;
    }

    /// <summary>A picked row goes to the Inspector: a model override or scene opens its editor, anything else its build view.</summary>
    private void KindSelected(KindTab tab)
    {
        Enable(tab);
        if (_restoring || tab.Picked is not { } row || Project is not { } p) return;
        _selection.Set("Projects", row.Info.Item switch
        {
            ModelItem m => new Selected.ProjectModel(p, m),
            SceneItem s => new Selected.ProjectScene(p, s),
            _ => new Selected.BuildItem(p, row.Info, row.Last, null),
        });
    }

    private bool CanView(ProjectItemRow row) => _ws.Install is not null && row.Info.Refusal is null && row.Info.Item switch
    {
        MeshItem => true,
        SceneItem or ModelItem => row.Info.Model is not null,
        AnimItem a => Sequence(a) is not null,
        PrefabItem pf => pf.Edits.Count > 0,
        _ => false,
    };

    /// <summary>The sequence a clip was added from (<c>bank@Seq</c>), when anim.json records it.</summary>
    private static string? Sequence(AnimItem a) => a.Settings["sequence"] is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0 ? s : null;

    private void RevealItem(KindTab tab)
    {
        if (tab.Picked is { } row) Shell.Reveal(row.Info.Source is { } s && File.Exists(s) ? s : row.Info.Folder);
    }

    /// <summary>Open the source file with whatever Windows opens it with.</summary>
    private void OpenItem(KindTab tab)
    {
        if (tab.Picked is not { Info.Source: { } source }) return;
        try
        {
            Process.Start(new ProcessStartInfo(source) { UseShellExecute = true });
            Log.Info("project", $"opened {source}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Status.Text = $"{Path.GetFileName(source)}: {ex.Message}";
            Status.Foreground = Skin.Brush("Warn");
        }
    }

    /// <summary>
    /// Show what the item replaces, as the Build tab does: a mesh's template in the Viewport, a model (a scene's, an
    /// override's stock member) in the Viewport and the Inspector; a clip's sequence in Animations; the first edited prefab
    /// in Prefabs.
    /// </summary>
    private void ViewItem(KindTab tab)
    {
        if (tab.Picked is not { } row) return;
        try
        {
            switch (row.Info.Item)
            {
                case MeshItem m:
                    var catalog = _ws.Catalog;
                    var (pack, index) = ProjectBuild.TemplateOf(catalog, m.SourcePack, m.SourceIndex, row.Info.Key);
                    var entry = catalog.Packs.First(e => ReferenceEquals(e.Pack, pack));
                    _host.Reveal("viewport", new IsolateRequest(catalog, entry.Base + index, null, null));
                    break;
                case SceneItem or ModelItem when row.Info.Model is { } name && _ws.Models is { } models:
                    var model = models.Find(name) ?? throw new ProjectException($"{name} is not in the open game");
                    _selection.Set("Projects", new Selected.Model(models, StockCopy.Model(models, model, _ws.Origins)));
                    _host.Open("viewport");
                    break;
                case AnimItem a when Sequence(a) is { } seq:
                    _host.Reveal("animations", seq);
                    break;
                case PrefabItem { Edits.Count: > 0 } pf:
                    _host.Reveal("prefabs", pf.Edits[0].Prefab);
                    break;
            }
        }
        catch (ProjectException ex)
        {
            Status.Text = ex.Message;
            Status.Foreground = Skin.Brush("Warn");
        }
    }

    /// <summary>Remove the picked items' folders — to the Recycle Bin, and only after the user confirms the exact paths.</summary>
    private void RemoveItems(KindTab tab)
    {
        if (Project is not { } p) return;
        string root = Path.GetFullPath(p.Folder).TrimEnd(Path.DirectorySeparatorChar);
        var folders = tab.List.SelectedItems.Cast<ProjectItemRow>().Select(r => Path.GetFullPath(r.Info.Folder).TrimEnd(Path.DirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => p.Contains(f) && Directory.Exists(f) && !f.Equals(root, StringComparison.OrdinalIgnoreCase)
                        && !ItemFolders.Any(x => f.Equals(Path.Combine(root, x), StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (folders.Count == 0) return;
        var listed = string.Join("\n", folders.Take(10).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')));
        if (folders.Count > 10) listed += $"\n… and {folders.Count - 10} more";
        if (MessageBox.Show(Window.GetWindow(this)!, $"Send these to the Recycle Bin?\n\n{listed}", "Remove from project",
                            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        int done = 0;
        foreach (var folder in folders)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    folder, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                done++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                Fail(ex.Message);
                break;
            }
        }
        Log.Info("project", $"removed {done} {tab.Noun} item(s) from '{p.Name}' (Recycle Bin)");
        Status.Text = $"removed {done} item(s)";
        _ws.NotifyProjectContentChanged();
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (Project is not { } p) return;
        var dlg = new OpenFileDialog { Title = "Add files to the project", Multiselect = true };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        Import(p, dlg.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Project is not { } p) return;
        var dlg = new OpenFolderDialog { Title = "Add a folder to the project" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        Import(p, [dlg.FolderName]);
    }

    private void Import(ModProject p, string[] paths)
    {
        try
        {
            string? sub = (Contents.SelectedItem as ContentRow)?.Entry is { IsDirectory: true } dir
                ? dir.Name
                : null;
            int n = p.Import(paths, sub);
            Log.Info("project", $"added {n} file(s) to '{p.Name}'" + (sub is null ? "" : $"/{sub}"));
            Status.Text = n == 0
                ? "nothing copied (already there?)"
                : $"copied {n} file(s){(sub is null ? "" : $" into {sub}")}";
            RefreshContents();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(ex.Message);
        }
    }

    /// <summary>Delete selected items — to the Recycle Bin, and only after the user confirms the exact paths.</summary>
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Project is not { } p) return;
        var rows = Contents.SelectedItems.Cast<ContentRow>().ToList();
        if (rows.Count == 0) return;

        var listed = string.Join("\n", rows.Take(10).Select(r => r.FullPath));
        if (rows.Count > 10) listed += $"\n… and {rows.Count - 10} more";
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"Send these to the Recycle Bin?\n\n{listed}", "Remove from project",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        int done = 0;
        foreach (var row in rows)
        {
            if (!p.Contains(row.FullPath)) continue;      // never touch anything outside the project folder
            try
            {
                if (row.Entry.IsDirectory)
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                        row.FullPath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        row.FullPath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                done++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                Fail(ex.Message);
                break;
            }
        }
        Log.Info("project", $"removed {done} item(s) from '{p.Name}' (Recycle Bin)");
        Status.Text = $"removed {done} item(s)";
        RefreshContents();
    }

    private void Contents_DoubleClick(object sender, RoutedEventArgs e)
    {
        if (Contents.SelectedItem is ContentRow row) Shell.Reveal(row.FullPath);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (Project is { } p) Shell.Reveal(p.Folder);
    }

    private void Fail(string message)
    {
        Status.Text = message;
        Status.Foreground = Skin.Brush("Error");
    }
}
