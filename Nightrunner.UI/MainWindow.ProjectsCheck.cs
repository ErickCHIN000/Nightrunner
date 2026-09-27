using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using AvalonDock.Layout;
using Nightrunner.Core.Games;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Prefab;
using Nightrunner.Core.Project;
using Nightrunner.UI.Views;

namespace Nightrunner.UI;

/// <summary>
/// The Projects window check (<c>NIGHTRUNNER_PROJECTSCHECK=&lt;folder&gt;</c>): opens DLTB and a project — an existing one
/// named by <c>NIGHTRUNNER_PROJECTSCHECK_PROJECT</c>, only read, or else a new one in <c>&lt;folder&gt;\project</c> made with
/// every item kind (the Build check's items plus a prefab edit and an unreadable mesh folder), taken off the recent list
/// after (a named one too with <c>NIGHTRUNNER_PROJECTSCHECK_FORGET=1</c>) — then captures the Projects
/// window on each of its tabs, with the first row picked, and lists every row's cells in <c>report.txt</c>.
/// </summary>
public sealed partial class MainWindow
{
    public async Task RunProjectsCheck(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int step = 0;
        Width = 1900;
        Height = 1060;
        Left = 0;
        Top = 0;
        async Task Shot(string what)
        {
            await Task.Delay(900);
            step++;
            var bmp = Capture(new WindowInteropHelper(this).Handle, out _);
            using var fs = File.Create(Path.Combine(folder, $"{step:00}_{what}.png"));
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            enc.Save(fs);
            report.Add($"{step:00} {what}");
        }

        var dltb = GameInstall.FindInstalls()["dltb"];
        OpenWorkspace(() => Task.CompletedTask);
        await _workspace.OpenGame(dltb);
        GameLabel.Text = _workspace.Title;
        await _workspace.WaitLoaded();

        string? named = Environment.GetEnvironmentVariable("NIGHTRUNNER_PROJECTSCHECK_PROJECT") is { Length: > 0 } n ? n : null;
        string dir = named ?? Path.Combine(folder, "project");
        bool fresh = named is null && !ModProject.IsProject(dir);
        var project = fresh ? ModProject.Create(dir, "projectscheck", "dltb") : ModProject.Open(dir);
        if (fresh)
        {
            _workspace.SetProject(project);
            await AddEverything(project, report);
            AddPrefabEdit(project, report);
            // a folder the scan cannot read: listed with its reason
            Directory.CreateDirectory(Path.Combine(dir, ProjectAssets.MeshFolder, "broken"));
            File.WriteAllText(Path.Combine(dir, ProjectAssets.MeshFolder, "broken", "notes.txt"), "no sidecar");
            await Until(() => Directory.Exists(Path.Combine(dir, TextureAsset.Folder)), 60000);
            await Task.Delay(3000);
        }
        _workspace.SetProject(project);

        Open("projects");
        ((LayoutContent)_open["projects"]).IsSelected = true;
        var view = (ProjectsView)_open["projects"].Content;
        await Until(() => view.IsLoaded);
        _workspace.NotifyProjectContentChanged();
        await Task.Delay(3000);
        await WaitRender();
        await Shot("shown");

        if (Find<TabControl>(view, t => t.Name == "Tabs") is { } tabs)
        {
            foreach (var tab in tabs.Items.OfType<TabItem>().Where(t => t.Visibility == Visibility.Visible).ToList())
            {
                string header = tab.Header as string ?? "?";
                tabs.SelectedItem = tab;
                await WaitRender();
                if (Find<ListView>(tabs, l => l.IsVisible) is not { } list)
                {
                    report.Add($"{header}: no list");
                    continue;
                }
                report.Add($"{header}: {list.Items.Count} row(s)");
                foreach (var row in list.Items.Cast<object>().Take(12))
                    report.Add("  " + string.Join(" | ", row.GetType().GetProperties()
                        .Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0)
                        .Select(p => $"{p.Name}={p.GetValue(row)}")));
                if (list.Items.Count > 0) list.SelectedIndex = 0;
                await WaitRender();
                await Shot($"tab_{header.ToLowerInvariant()}");
                report.Add($"  inspector: {_selection.Current?.GetType().Name} from {_selection.Source}");
                // View: a mesh's template in the Viewport, a clip's sequence in Animations, an edited prefab in Prefabs
                if (header is "Meshes" or "Anims" or "Prefabs"
                    && Find<Button>(tabs, b => b.Content as string == "View" && b.IsVisible) is { } viewButton)
                {
                    report.Add($"  view enabled: {viewButton.IsEnabled}");
                    if (!viewButton.IsEnabled) continue;
                    viewButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    await Task.Delay(6000);
                    await Shot($"view_{header.ToLowerInvariant()}");
                    report.Add($"  after view: {_selection.Current?.GetType().Name} from {_selection.Source}");
                    ((LayoutContent)_open["projects"]).IsSelected = true;
                    await WaitRender();
                }
            }
        }
        if (Find<ListView>(view, l => l.Name == "Contents") is { } contents)
            report.Add("other content: " + string.Join(", ", contents.Items.Cast<ContentRow>().Select(r => $"{r.Name} {r.FilesText}")));

        if (named is null || Environment.GetEnvironmentVariable("NIGHTRUNNER_PROJECTSCHECK_FORGET") == "1")
        {
            _workspace.Settings.ForgetProject(dir);
            _workspace.Settings.Save();
        }
        File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
        File.WriteAllLines(Path.Combine(folder, "log.txt"), Log.Recent.Select(e => $"{e.TimeText} {e.Level} {e.Source,-9} {e.Message} {e.DurationText}"));
        Close();
    }

    /// <summary>One transform edit of a prefab in the smallest pack with a Prefabs resource, as the Prefab inspector adds it.</summary>
    private void AddPrefabEdit(ModProject project, List<string> report)
    {
        try
        {
            var catalog = _workspace.StockCatalog();
            var (entry, target) = catalog.IndexedPacks.Where(p => PrefabContainer.ResourcesIn(p.Pack!).Length == 1).OrderBy(p => p.FileSize)
                .Select(p => (p, PrefabContainer.Read(p.Pack!, PrefabContainer.ResourcesIn(p.Pack!).Single())))
                .Where(x => x.Item2.LayoutProblem() is null)
                .Select(x => (x.p, PrefabDecoder.Decode(x.Item2).Prefabs.FirstOrDefault(r => r.Components.Any(y => y.Xform is not null))))
                .First(x => x.Item2 is not null);
            var comp = target!.Components.First(x => x.Xform is not null && target.Components.Count(y => y.Pcid == x.Pcid) == 1);
            var item = ProjectAssets.AddPrefabEdits(project, catalog, entry.Label,
                                                    [new PrefabEditOp("transform", target.Name!, comp.Pcid) { Translate = new Vec3(1, 2, 3) }]);
            report.Add($"add prefab edit: {item.Folder} ({target.Name})");
        }
        catch (Exception e)
        {
            report.Add($"add prefab edit FAILED: {e.Message}");
        }
    }
}
