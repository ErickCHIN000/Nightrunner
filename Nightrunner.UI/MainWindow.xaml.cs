using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AvalonDock.Layout;
using Nightrunner.Core.Logging;
using Nightrunner.UI.Views;
using RK = AvalonDock.Themes.VS2013.Themes.ResourceKeys;

namespace Nightrunner.UI;

/// <summary>
/// The shell. A main bar is always on top (app name, the open game and project, the window and view controls,
/// Games, Settings). Below it is either a shell page — the game selector or settings — or the dock, where every
/// area is a window that can be split, tabbed, floated or closed. Areas are declared in <see cref="Panels"/>;
/// arrangements of them are <see cref="WorkspaceView"/>s.
/// </summary>
public sealed partial class MainWindow : Window, IPanelHost
{
    public static readonly RoutedCommand FocusSearchCommand = new();

    private readonly Workspace _workspace = new();
    private readonly Selection _selection = new();
    private readonly LayoutStore _layouts = new();
    private readonly GameSelectView _selector;
    private readonly SettingsView _settings;

    /// <summary>The open windows, by panel id.</summary>
    private readonly Dictionary<string, LayoutContent> _open = [];

    private string _currentView = "";

    /// <summary>Where a document opened from the Windows menu lands. Rebuilt with every view.</summary>
    private LayoutDocumentPane _docPane = null!;

    public MainWindow()
    {
        InitializeComponent();
        _selector = new GameSelectView(_workspace.Settings);
        _settings = new SettingsView(_workspace.Settings);
        _selector.GameChosen += install => OpenWorkspace(() => _workspace.OpenGame(install));
        _selector.PacksChosen += paths => OpenWorkspace(() => _workspace.OpenPacks(paths));
        _selector.SettingsRequested += ShowSettings;
        _settings.RootsChanged += () => _selector.Refresh();
        _settings.PlayRequested += install => OpenWorkspace(() => _workspace.OpenGame(install));
        _settings.RuntimeChanged += gameId =>
        {
            // custom-folder content appears or disappears, so the open game's packs are re-read
            if (_workspace.GameId == gameId) _ = _workspace.Reload();
        };

        Dock.Theme = new AvalonDock.Themes.Vs2013DarkTheme();
        Dock.ActiveContentChanged += (_, _) =>
            App.ActivePanel = (Dock.Layout?.ActiveContent?.Title) ?? "";
        GameAccent.Changed += ApplyDockAccent;
        _workspace.ProjectChanged += () =>
            ProjectLabel.Text = _workspace.Project is { } p ? $"· {p.Name}" : "";

        _docPane = DocPane;
        Jobs.Active.CollectionChanged += (_, _) => ShowJobs();
        Jobs.Finished += (job, summary, error) =>
        {
            _lastJob = error is not null ? $"{job.Title}: {error}" : $"{job.Title}: {(job.Cancelled ? "cancelled" : summary ?? "done")}";
            _lastJobFailed = error is not null;
            ShowJobs();
            if (job.Source is "project" or "build") _workspace.NotifyProjectContentChanged();
        };
        CommandBindings.Add(new CommandBinding(FocusSearchCommand, (_, _) => FocusSearch()));
        ViewHost.Content = _selector;
        Loaded += (_, _) =>
        {
            UseDarkChrome();
            GameAccent.Apply(null);
            ApplyDockAccent();
            GameLabel.Text = "no game";
            if (Environment.GetEnvironmentVariable("NIGHTRUNNER_MEMCHECK") is { Length: > 0 } memFolder)
                _ = RunMemCheck(memFolder).ContinueWith(t => System.IO.File.WriteAllText(System.IO.Path.Combine(memFolder, "error.txt"), t.Exception!.ToString()),
                                                 TaskContinuationOptions.OnlyOnFaulted);
            if (Environment.GetEnvironmentVariable("NIGHTRUNNER_TEXCHECK") is { Length: > 0 } texFolder)
                _ = RunTexCheck(texFolder).ContinueWith(t => System.IO.File.WriteAllText(System.IO.Path.Combine(texFolder, "error.txt"), t.Exception!.ToString()),
                                                 TaskContinuationOptions.OnlyOnFaulted);
            if (Environment.GetEnvironmentVariable("NIGHTRUNNER_RUNTIMECHECK") is { Length: > 0 } rtFolder)
                _ = RunRuntimeCheck(rtFolder).ContinueWith(t => System.IO.File.WriteAllText(System.IO.Path.Combine(rtFolder, "error.txt"), t.Exception!.ToString()),
                                                 TaskContinuationOptions.OnlyOnFaulted);
            if (Environment.GetEnvironmentVariable("NIGHTRUNNER_DOCKCHECK") is { Length: > 0 } spike)
                _ = RunDockCheck(spike).ContinueWith(t => System.IO.File.WriteAllText(System.IO.Path.Combine(spike, "error.txt"), t.Exception!.ToString() + Environment.NewLine +
                    string.Join(Environment.NewLine, Log.Recent.Select(e => $"{e.TimeText} {e.Level} {e.Source} {e.Message}"))),
                                                 TaskContinuationOptions.OnlyOnFaulted);
            if (Environment.GetEnvironmentVariable("NIGHTRUNNER_PROJECTSCHECK") is { Length: > 0 } projectsFolder)
                _ = RunProjectsCheck(projectsFolder).ContinueWith(t => System.IO.File.WriteAllText(System.IO.Path.Combine(projectsFolder, "error.txt"), t.Exception!.ToString()),
                                                 TaskContinuationOptions.OnlyOnFaulted);
            if (Environment.GetEnvironmentVariable("NIGHTRUNNER_BUILDCHECK") is { Length: > 0 } buildFolder)
                _ = RunBuildCheck(buildFolder).ContinueWith(t => System.IO.File.WriteAllText(System.IO.Path.Combine(buildFolder, "error.txt"), t.Exception!.ToString()),
                                                 TaskContinuationOptions.OnlyOnFaulted);
        };
        Closed += (_, _) => _workspace.Dispose();
    }

    private PanelContext Context => new(_workspace, _selection, this);

    // ---- IPanelHost ----------------------------------------------------------------------------------------

    /// <summary>Open a window by id and bring it to the front. Unknown ids are ignored.</summary>
    public void Open(string panelId)
    {
        if (Panels.ById(panelId) is { } def) OpenPanel(def);
    }

    /// <summary>
    /// Open a window and hand it something to show. The payload is whatever the two panels agreed on; a window
    /// that does not understand it ignores it, which is why this is a dynamic call and not an interface.
    /// </summary>
    public void Reveal(string panelId, object payload)
    {
        if (Panels.ById(panelId) is not { } def) return;
        var item = OpenPanel(def);
        try
        {
            (item.Content as dynamic)?.Reveal(payload);
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            Log.Warn("shell", $"the {def.Title} window cannot show a {payload.GetType().Name}");
        }
    }

    // ---- shell pages ---------------------------------------------------------------------------------------

    private void Games_Click(object sender, RoutedEventArgs e)
    {
        _selector.Refresh();
        ShowPage(_selector);
    }

    private string? _lastJob;
    private bool _lastJobFailed;
    private Job? _shownJob;

    /// <summary>The job bar: the running job with its progress, or the last one's result until the next starts.</summary>
    private void ShowJobs()
    {
        if (_shownJob is not null) _shownJob.PropertyChanged -= OnJobChanged;
        _shownJob = Jobs.Active.FirstOrDefault();
        if (_shownJob is { } job)
        {
            job.PropertyChanged += OnJobChanged;
            OnJobChanged(job, null);
            JobCancel.Visibility = JobProgress.Visibility = Visibility.Visible;
            JobQueued.Text = Jobs.Active.Count > 1 ? $"+{Jobs.Active.Count - 1} queued" : "";
            JobText.Foreground = Skin.Brush("Fg");
            JobBar.Visibility = Visibility.Visible;
        }
        else
        {
            JobCancel.Visibility = JobProgress.Visibility = Visibility.Collapsed;
            JobQueued.Text = "";
            JobText.Text = _lastJob ?? "";
            JobText.Foreground = Skin.Brush(_lastJobFailed ? "Error" : "FgDim");
            JobBar.Visibility = _lastJob is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void OnJobChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs? e)
    {
        if (sender is not Job job) return;
        JobText.Text = $"{job.Title} · {job.Text}";
        JobProgress.IsIndeterminate = job.Fraction is null;
        JobProgress.Value = job.Fraction ?? 0;
    }

    private void JobCancel_Click(object sender, RoutedEventArgs e) => _shownJob?.Cancel();

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void ShowSettings() => ShowPage(_settings);

    private void ShowPage(UserControl page)
    {
        ViewHost.Content = page;
        ViewHost.Visibility = Visibility.Visible;
        Dock.Visibility = Visibility.Collapsed;
    }

    private void OpenWorkspace(Func<Task> open)
    {
        if (_open.Count == 0) ApplyView(_layouts.DefaultName);
        _ = open();
        GameLabel.Text = _workspace.Title;
        App.Game = _workspace.Title;
        Title = $"Nightrunner — {_workspace.Title}";
        BtnWindows.IsEnabled = true;
        BtnViews.IsEnabled = true;
        ViewHost.Visibility = Visibility.Collapsed;
        Dock.Visibility = Visibility.Visible;
    }

    // ---- dockable windows ----------------------------------------------------------------------------------

    /// <summary>Open a window where its placement says, or focus it when it is already up.</summary>
    private LayoutContent OpenPanel(PanelDef def, WorkspaceView? view = null)
    {
        if (_open.TryGetValue(def.Id, out var already))
        {
            already.IsActive = true;
            already.IsSelected = true;
            return already;
        }

        var content = def.Create(Context);
        LayoutContent item;
        if (def.Placement == PanelPlacement.Document)
        {
            var doc = new LayoutDocument { Title = def.Title, ContentId = def.Id, Content = content, CanClose = true };
            DocumentPane().Children.Add(doc);
            item = doc;
        }
        else
        {
            var anchorable = new LayoutAnchorable
            {
                Title = def.Title,
                ContentId = def.Id,
                Content = content,
                CanClose = true,
                CanHide = false,
            };
            SidePane(def, view).Children.Add(anchorable);
            item = anchorable;
        }

        item.Closed += (_, _) =>
        {
            _open.Remove(def.Id);
            (content as dynamic)?.Detach();
        };
        _open[def.Id] = item;
        item.IsActive = true;
        item.IsSelected = true;
        return item;
    }

    /// <summary>The pane a side window docks into, made on first use.</summary>
    private LayoutAnchorablePane SidePane(PanelDef def, WorkspaceView? view = null)
    {
        string name = def.Placement == PanelPlacement.Right ? "rightPane" : "bottomPane";
        double extent = def.Placement == PanelPlacement.Right
            ? view?.RightWidth ?? def.Extent
            : view?.BottomHeight ?? def.Extent;
        if (Dock.Layout.Descendents().OfType<LayoutAnchorablePane>().FirstOrDefault(p => p.Name == name)
            is { } existing)
            return existing;

        var pane = new LayoutAnchorablePane { Name = name };
        var root = Dock.Layout.RootPanel;
        if (def.Placement == PanelPlacement.Right)
        {
            pane.DockWidth = new GridLength(extent, GridUnitType.Pixel);
            if (root.Orientation != Orientation.Horizontal)
            {
                // the documents already sit above a bottom pane: the right pane joins the top row
                var top = root.Children.OfType<LayoutPanel>().FirstOrDefault();
                if (top is not null)
                {
                    top.Children.Add(pane);
                    return pane;
                }
            }
            root.Children.Add(pane);
            return pane;
        }

        pane.DockHeight = new GridLength(extent, GridUnitType.Pixel);
        if (root.Orientation == Orientation.Horizontal)
        {
            // the bottom pane sits under everything, so the root has to run vertically
            var inner = new LayoutPanel { Orientation = Orientation.Horizontal };
            foreach (var child in root.Children.ToArray())
            {
                root.RemoveChild(child);
                inner.Children.Add(child);
            }
            root.Orientation = Orientation.Vertical;
            root.Children.Add(inner);
        }
        root.Children.Add(pane);
        return pane;
    }

    /// <summary>The window selector: every area, ticked when it is open.</summary>
    private void Windows_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = BtnWindows,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        foreach (var def in Panels.All)
        {
            var item = new MenuItem { Header = def.Title, IsCheckable = true, IsChecked = _open.ContainsKey(def.Id) };
            var captured = def;
            item.Click += (_, _) =>
            {
                if (_open.TryGetValue(captured.Id, out var open)) open.Close();
                else OpenPanel(captured);
            };
            menu.Items.Add(item);
        }
        BtnWindows.ContextMenu = menu;
        menu.IsOpen = true;
    }

    // ---- workspace views -----------------------------------------------------------------------------------

    private void Views_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = BtnViews,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        foreach (var view in _layouts.All)
        {
            string captured = view.Name;
            var item = new MenuItem
            {
                Header = view.Name + (view.BuiltIn ? "" : " *") +
                         (Same(view.Name, _layouts.DefaultName) ? "   (default)" : ""),
                IsCheckable = true,
                IsChecked = Same(view.Name, _currentView),
            };
            item.Click += (_, _) => ApplyView(captured);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        Add(menu, "Save current as…", SaveViewAs);
        Add(menu, $"Overwrite '{_currentView}'", () => OverwriteView(_currentView),
            _currentView.Length > 0 && !_layouts.IsBuiltIn(_currentView));
        Add(menu, $"Copy '{_currentView}'…", () => CopyView(_currentView), _currentView.Length > 0);
        Add(menu, $"Set '{_currentView}' as default", () => _layouts.DefaultName = _currentView,
            _currentView.Length > 0);
        Add(menu, $"Delete '{_currentView}'", () => DeleteView(_currentView),
            _currentView.Length > 0 && !_layouts.IsBuiltIn(_currentView));
        BtnViews.ContextMenu = menu;
        menu.IsOpen = true;

        static void Add(ContextMenu menu, string header, Action run, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Switch to a view: rebuild the dock from what the view describes.</summary>
    public void ApplyView(string name)
    {
        var view = _layouts.Find(name) ?? LayoutStore.BuiltIn[0];
        using var op = Log.Start("layout", $"apply workspace view '{view.Name}'");
        try
        {
            CloseAll();

            var root = new LayoutRoot();
            var row = new LayoutPanel { Orientation = Orientation.Horizontal };
            var documents = BuildDocuments(view.DocumentTree);
            row.Children.Add((ILayoutPanelElement)documents);

            if (view.Right.Count > 0)
            {
                var rightPane = new LayoutAnchorablePane
                {
                    Name = "rightPane",
                    DockWidth = new GridLength(view.RightWidth, GridUnitType.Pixel),
                };
                foreach (var id in view.Right)
                    if (Panels.ById(id) is { } def)
                        rightPane.Children.Add(Anchorable(def));
                if (rightPane.ChildrenCount > 0) row.Children.Add(rightPane);
            }

            if (view.Bottom.Count > 0)
            {
                var column = new LayoutPanel { Orientation = Orientation.Vertical };
                column.Children.Add(row);
                var bottomPane = new LayoutAnchorablePane
                {
                    Name = "bottomPane",
                    DockHeight = new GridLength(view.BottomHeight, GridUnitType.Pixel),
                };
                foreach (var id in view.Bottom)
                    if (Panels.ById(id) is { } def)
                        bottomPane.Children.Add(Anchorable(def));
                if (bottomPane.ChildrenCount > 0) column.Children.Add(bottomPane);
                root.RootPanel = column;
            }
            else
            {
                root.RootPanel = row;
            }

            Dock.Layout = root;
            ApplyDockAccent();
            ActivateFront(view);
            _currentView = view.Name;
            op.Result = Describe(view);
        }
        catch (Exception ex)
        {
            op.Failed(ex.GetBaseException() is { } root && root != ex ? $"{ex.Message} ({root.GetType().Name}: {root.Message})" : ex.Message);
            Fallback();
        }
    }

    private static string Describe(WorkspaceView view)
    {
        var tree = view.DocumentTree;
        return tree.IsSplit
            ? $"{tree.Children!.Count} document panes: " +
              string.Join(" | ", tree.Children.Select(c => string.Join(",", c.AllPanels)))
            : $"{tree.AllPanels.Count()} document(s)";
    }

    /// <summary>Turn one node of a view's document tree into the dock's own panes.</summary>
    private ILayoutDocumentPane BuildDocuments(DocNode node, Orientation parent = Orientation.Horizontal)
    {
        if (node.IsSplit)
        {
            var orientation = node.Orientation == "Vertical" ? Orientation.Vertical : Orientation.Horizontal;
            var group = new LayoutDocumentPaneGroup { Orientation = orientation };
            foreach (var child in node.Children!)
                group.Children.Add(BuildDocuments(child, orientation));
            SetExtent(group, node, parent);
            return group;
        }

        var pane = new LayoutDocumentPane();
        foreach (var id in node.Panels ?? [])
        {
            if (Panels.ById(id) is not { } def || def.Placement != PanelPlacement.Document) continue;
            pane.Children.Add(Document(def));
        }
        SetExtent(pane, node, parent);
        _docPane = pane;                // the last pane built is where a new document lands
        return pane;
    }

    private static void SetExtent(object element, DocNode node, Orientation parent)
    {
        var length = new GridLength(node.Size <= 0 ? 1 : node.Size,
                                    node.Star ? GridUnitType.Star : GridUnitType.Pixel);
        switch (element)
        {
            case LayoutDocumentPaneGroup group when parent == Orientation.Horizontal:
                group.DockWidth = length;
                break;
            case LayoutDocumentPaneGroup group:
                group.DockHeight = length;
                break;
            case LayoutDocumentPane pane when parent == Orientation.Horizontal:
                pane.DockWidth = length;
                break;
            case LayoutDocumentPane pane:
                pane.DockHeight = length;
                break;
        }
    }

    private LayoutDocument Document(PanelDef def)
    {
        var content = def.Create(Context);
        var doc = new LayoutDocument { Title = def.Title, ContentId = def.Id, Content = content, CanClose = true };
        Track(def, doc, content);
        return doc;
    }

    private LayoutAnchorable Anchorable(PanelDef def)
    {
        var content = def.Create(Context);
        var anchorable = new LayoutAnchorable
        {
            Title = def.Title,
            ContentId = def.Id,
            Content = content,
            CanClose = true,
            CanHide = false,
        };
        Track(def, anchorable, content);
        return anchorable;
    }

    private void Track(PanelDef def, LayoutContent item, object content)
    {
        item.Closed += (_, _) =>
        {
            _open.Remove(def.Id);
            (content as dynamic)?.Detach();
        };
        _open[def.Id] = item;
    }

    /// <summary>Bring each pane's remembered tab to the front, and focus the view's active document.</summary>
    private void ActivateFront(WorkspaceView view)
    {
        foreach (var node in Leaves(view.DocumentTree))
            if (node.Active is { } id && _open.TryGetValue(id, out var tab))
                tab.IsSelected = true;

        string? front = view.DocumentTree.IsSplit
            ? Leaves(view.DocumentTree).FirstOrDefault()?.Active
            : view.DocumentTree.Active;
        if (front is not null && _open.TryGetValue(front, out var item))
        {
            item.IsSelected = true;
            item.IsActive = true;
            return;
        }
        FocusFirstDocument();
    }

    private static IEnumerable<DocNode> Leaves(DocNode node) =>
        node.IsSplit ? node.Children!.SelectMany(Leaves) : [node];

    private void Fallback()
    {
        CloseAll();
        Dock.Layout = new LayoutRoot();
        var panel = new LayoutPanel { Orientation = Orientation.Horizontal };
        var pane = new LayoutDocumentPane();
        panel.Children.Add(pane);
        Dock.Layout.RootPanel = panel;
        _docPane = pane;
        foreach (var def in Panels.All.Where(p => p.OpenByDefault)) OpenPanel(def);
        ApplyDockAccent();
    }

    /// <summary>A document pane to put a new window in, making one when the layout has none.</summary>
    private LayoutDocumentPane DocumentPane()
    {
        if (_docPane.Root == Dock.Layout) return _docPane;
        var existing = Dock.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
        if (existing is not null) return _docPane = existing;
        var pane = new LayoutDocumentPane();
        Dock.Layout.RootPanel.Children.Insert(0, pane);
        return _docPane = pane;
    }

    /// <summary>Describe the dock as it stands, splits and all, so it can be saved under a name.</summary>
    private WorkspaceView Capture(string name)
    {
        var view = new WorkspaceView { Name = name };
        var documentRoot = Dock.Layout.Descendents().OfType<ILayoutDocumentPane>()
            .Select(TopMostDocumentElement).FirstOrDefault();
        view.Layout = documentRoot is null ? DocNode.Pane() : CaptureDocuments(documentRoot);
        view.Documents = [.. view.Layout.AllPanels];      // so an older build can still read the view
        view.Active = view.Layout.Active ?? view.Documents.FirstOrDefault();

        foreach (var item in _open.Values.OfType<LayoutAnchorable>())
        {
            if (item.ContentId is not { } id) continue;
            if ((item.Parent as LayoutAnchorablePane)?.Name == "bottomPane") view.Bottom.Add(id);
            else view.Right.Add(id);
        }
        if (Pane("rightPane") is { } right && right.DockWidth.IsAbsolute) view.RightWidth = right.DockWidth.Value;
        if (Pane("bottomPane") is { } bottom && bottom.DockHeight.IsAbsolute)
            view.BottomHeight = bottom.DockHeight.Value;
        return view;
    }

    /// <summary>Climb from a pane to the outermost group that still only holds documents.</summary>
    private static ILayoutElement TopMostDocumentElement(ILayoutDocumentPane pane)
    {
        ILayoutElement element = (ILayoutElement)pane;
        while (element.Parent is LayoutDocumentPaneGroup group) element = group;
        return element;
    }

    private static DocNode CaptureDocuments(ILayoutElement element)
    {
        switch (element)
        {
            case LayoutDocumentPaneGroup group:
            {
                var node = new DocNode
                {
                    Orientation = group.Orientation.ToString(),
                    Children = group.Children.Select(c => CaptureDocuments((ILayoutElement)c)).ToList(),
                };
                Extent(node, group.DockWidth, group.DockHeight, group.Parent as LayoutDocumentPaneGroup);
                return node;
            }
            case LayoutDocumentPane pane:
            {
                var node = new DocNode
                {
                    Panels = pane.Children.Select(c => c.ContentId ?? "").Where(id => id.Length > 0).ToList(),
                    Active = pane.SelectedContent?.ContentId,
                };
                Extent(node, pane.DockWidth, pane.DockHeight, pane.Parent as LayoutDocumentPaneGroup);
                return node;
            }
            default:
                return DocNode.Pane();
        }

        static void Extent(DocNode node, GridLength width, GridLength height, LayoutDocumentPaneGroup? parent)
        {
            var length = parent?.Orientation == Orientation.Vertical ? height : width;
            node.Size = length.Value <= 0 ? 1 : length.Value;
            node.Star = !length.IsAbsolute;
        }
    }

    private LayoutAnchorablePane? Pane(string name) =>
        Dock.Layout.Descendents().OfType<LayoutAnchorablePane>().FirstOrDefault(p => p.Name == name);

    private void CloseAll()
    {
        foreach (var item in _open.Values.ToArray()) item.Close();
        _open.Clear();
    }

    /// <summary>Land on the first document, not whichever window happened to open last.</summary>
    private void FocusFirstDocument()
    {
        foreach (var def in Panels.All.Where(p => p.Placement == PanelPlacement.Document))
        {
            if (!_open.TryGetValue(def.Id, out var item)) continue;
            item.IsActive = true;
            item.IsSelected = true;
            return;
        }
    }

    private void SaveViewAs()
    {
        string suggestion = _currentView.Length > 0 && !_layouts.IsBuiltIn(_currentView) ? _currentView : "";
        if (PromptWindow.Ask(this, "Save workspace view", "Name for this arrangement:", suggestion) is { } name)
            OverwriteView(name);
    }

    private void OverwriteView(string name)
    {
        try
        {
            _layouts.Put(Capture(name));
            _currentView = name;
        }
        catch (InvalidOperationException e)
        {
            Log.Warn("layout", e.Message);
        }
    }

    private void CopyView(string name)
    {
        if (PromptWindow.Ask(this, "Copy workspace view", $"Name for the copy of '{name}':", name + " copy")
            is not { } newName) return;
        if (_layouts.Copy(name, newName) is not null) _currentView = newName;
    }

    private void DeleteView(string name)
    {
        if (MessageBox.Show(this, $"Delete the workspace view '{name}'?", "Delete view",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        _layouts.Delete(name);
        if (Same(_currentView, name)) _currentView = "";
    }

    private void FocusSearch()
    {
        if (Dock.Visibility != Visibility.Visible) return;
        var active = _open.Values.FirstOrDefault(i => i.IsActive && i.Content is RawExplorerView or TexturesView)
                     ?? _open.GetValueOrDefault("raw")
                     ?? _open.GetValueOrDefault("textures");
        if (active is null) return;
        active.IsActive = true;
        (active.Content as dynamic)?.FocusSearch();
    }

    /// <summary>
    /// The dock theme paints its own Visual-Studio blue. Re-point the accent keys at the game's colour so the
    /// active tab, the caption and the drop preview match everything else.
    /// </summary>
    private void ApplyDockAccent()
    {
        if (Application.Current is not { } app) return;
        var accent = (System.Windows.Media.Brush)app.Resources["Accent"];
        var deep = (System.Windows.Media.Brush)app.Resources["AccentDeep"];
        var faint = (System.Windows.Media.Brush)app.Resources["AccentFaint"];
        (object Key, System.Windows.Media.Brush Brush)[] map =
        [
            (RK.ControlAccentBrushKey, accent),
            // backgrounds that carry text get the deep step; the bright accent stays for thin marks
            (RK.DocumentWellTabSelectedActiveBackground, deep),
            (RK.DocumentWellTabSelectedInactiveBackground, faint),
            (RK.ToolWindowCaptionActiveBackground, deep),
            (RK.AutoHideTabHoveredBorder, accent),
            (RK.AutoHideTabHoveredText, accent),
            (RK.DockingButtonForegroundBrushKey, accent),
            (RK.PreviewBoxBorderBrushKey, accent),
            (RK.PreviewBoxBackgroundBrushKey, faint),
        ];
        foreach (var (key, brush) in map) Dock.Resources[key] = brush;
    }

    /// <summary>Dark title bar — without it Windows paints the one white strip the theme cannot reach.</summary>
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, in int value, int size);

    private void UseDarkChrome()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20, in on, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
    }
}
