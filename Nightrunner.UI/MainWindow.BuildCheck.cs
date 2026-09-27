using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvalonDock.Layout;
using Nightrunner.Core.Games;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;
using Nightrunner.Core.Project;
using Nightrunner.UI.Views;

namespace Nightrunner.UI;

/// <summary>
/// The Build tab check (<c>NIGHTRUNNER_BUILDCHECK=&lt;folder&gt;</c>): makes a DLTB project in <c>&lt;folder&gt;\project</c>
/// (or <c>NIGHTRUNNER_BUILDCHECK_PROJECT</c>)
/// (textures, a mesh, a model scene with a hidden submesh, a <c>.model</c> override with a slot off, a clip) through the
/// calls the windows use, or reopens it, then captures the Build tab as shown, after Check, after Build, and — with
/// <c>NIGHTRUNNER_BUILDCHECK_EDIT=1</c> — after a texture edit, its Check and the rebuild. <c>NIGHTRUNNER_BUILDCHECK_RUNTIME=1</c> / <c>0</c>
/// switches runtime modding on / off for the run (in memory; the user's settings are not written). Writes <c>report.txt</c>.
/// </summary>
public sealed partial class MainWindow
{
    public async Task RunBuildCheck(string folder)
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
            report.Add($"{step:00} {what}: {JobText.Text}");
        }

        var dltb = GameInstall.FindInstalls()["dltb"];
        if (Environment.GetEnvironmentVariable("NIGHTRUNNER_BUILDCHECK_RUNTIME") is ("1" or "0") and var runtime)
            _workspace.Settings.SetRuntimeModding(dltb.Id, runtime == "1");
        report.Add($"runtime modding {(_workspace.Settings.RuntimeModdingFor(dltb.Id) ? "on" : "off")}");
        OpenWorkspace(() => Task.CompletedTask);
        await _workspace.OpenGame(dltb);
        GameLabel.Text = _workspace.Title;
        await _workspace.WaitLoaded();

        string dir = Environment.GetEnvironmentVariable("NIGHTRUNNER_BUILDCHECK_PROJECT") is { Length: > 0 } named ? named : Path.Combine(folder, "project");
        bool fresh = !ModProject.IsProject(dir);
        var project = fresh ? ModProject.Create(dir, "buildcheck", "dltb") : ModProject.Open(dir);
        _workspace.SetProject(project);
        if (fresh) await AddEverything(project, report);

        Open("build");
        ((LayoutContent)_open["build"]).IsSelected = true;
        var view = (BuildView)_open["build"].Content;
        await Until(() => view.IsLoaded);
        await WaitRender();
        await Shot("shown");

        async Task Press(string content, string shot)
        {
            if (Find<Button>(view, b => b.Content as string == content) is not { } button)
            {
                report.Add($"no {content} button");
                return;
            }
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Task.Delay(500);
            await Until(() => Jobs.Active.Count == 0, 900000);
            await WaitRender();
            await Shot(shot);
        }

        await Press("Check", "checked");
        await Press("Build", "built");
        if (Find<ListView>(view, l => l.Name == "Rows") is { } rows)
            foreach (var kind in new[] { "mesh", "model" })
            {
                var row = rows.Items.Cast<object>().FirstOrDefault(r => (r.GetType().GetProperty("Kind")?.GetValue(r) as string) == kind);
                if (row is null) continue;
                rows.SelectedItem = row;
                await WaitRender();
                await Shot($"{kind}_selected");
                if (Find<Button>(view, b => b.Content as string == "View") is { } viewButton)
                {
                    viewButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    await Task.Delay(4000);
                    await Shot($"{kind}_view");
                    ((LayoutContent)_open["build"]).IsSelected = true;
                }
            }

        if (Environment.GetEnvironmentVariable("NIGHTRUNNER_BUILDCHECK_EDIT") == "1"
            && Directory.GetFiles(Path.Combine(dir, TextureAsset.Folder), "*.png").OrderBy(f => f).FirstOrDefault() is { } png)
        {
            var (bgra, w, h) = BuildView.LoadPng(png);
            for (int i = 0; i < bgra.Length; i += 4) { bgra[i] ^= 0xFF; bgra[i + 1] ^= 0xFF; bgra[i + 2] ^= 0xFF; }
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4)));
            using (var fs = File.Create(png)) enc.Save(fs);
            report.Add($"edited {Path.GetFileName(png)}");
            _workspace.NotifyProjectContentChanged();
            await WaitRender();
            await Shot("edited");
            await Press("Check", "edited_checked");
            await Press("Build", "rebuilt");
        }

        string json = Path.Combine(project.BuildFolder, $"{project.Name}.build.json");
        if (File.Exists(json)) File.Copy(json, Path.Combine(folder, $"{step:00}_build.json"), true);
        File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
        File.WriteAllLines(Path.Combine(folder, "log.txt"), Log.Recent.Select(e => $"{e.TimeText} {e.Level} {e.Source,-9} {e.Message} {e.DurationText}"));
        Close();
    }

    /// <summary>Every item kind the Build tab lists, added the way the windows add them, plus one edit each where cheap.</summary>
    private async Task AddEverything(ModProject project, List<string> report)
    {
        var catalog = _workspace.Catalog;
        var origins = _workspace.Origins;
        var models = _workspace.Models!;
        void Try(string what, Func<string> add)
        {
            try { report.Add($"add {what}: {add()}"); }
            catch (Exception e) { report.Add($"add {what} FAILED: {e.Message}"); }
        }

        // textures: the Textures window's Add to project
        var names = catalog.Search("_dif", 0x20).Gids.Select(catalog.Name).Where(n => n.StartsWith("player", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (names.Length < 2) names = catalog.Search("_dif", 0x20).Gids.Select(catalog.Name).Take(2).ToArray();
        Reveal("textures", new AddTexturesRequest(names, "buildcheck"));
        report.Add($"add textures: {string.Join(", ", names)}");

        Try("mesh", () =>
        {
            int gid = StockCopy.Resource(catalog, catalog.Lookup("player_kc_basic_torso_a_tpp", 0x10)[0], origins);
            return ProjectAssets.AddMesh(project, catalog, gid).Folder;
        });

        var stock = _workspace.StockCatalog();
        MeshModel Decode(int gid) => _workspace.Mesh(gid).GetAwaiter().GetResult();
        await Task.Run(() => Try("scene", () =>
        {
            var entry = StockCopy.Model(models, models.Find("player_kc_basic_tpp.model")!, origins);
            var scene = ProjectAssets.AddScene(project, models, entry, stock, Decode);
            // hide one exported submesh that is not the mesh item's
            var castReport = JsonNode.Parse(File.ReadAllText(scene.ReportPath))!.AsObject();
            string hide = (castReport["mesh_map"] as JsonArray ?? []).OfType<JsonObject>().Select(m => (string?)m["name"] ?? "")
                .Last(n => n.Length > 0 && !n.Contains("torso_a", StringComparison.OrdinalIgnoreCase));
            scene.Options["hide"] = new JsonArray(hide);
            File.WriteAllText(Path.Combine(scene.Folder, ProjectAssets.SplitSettings), scene.Options.ToJsonString(new() { WriteIndented = true }));
            return $"{scene.Folder} (hide {hide})";
        }));

        Try("model", () =>
        {
            var item = ProjectAssets.AddModel(project, models, StockCopy.Model(models, models.Find("player_tpp_skeleton.model")!, origins));
            var doc = item.Load();
            string slot = ModelEdit.Slots(doc).OfType<JsonObject>().Select(s => (string?)s["name"] ?? "")
                .First(n => n.Length > 0 && ModelEdit.Resources(ModelEdit.Slot(doc, n)).Count > 0);
            ModelEdit.SetSlotEnabled(doc, slot, false, item.Stash);
            item.Save(doc);
            return $"{item.Folder} (slot {slot} off)";
        });

        var anims = _workspace.Animations;
        await Task.Run(() =>
        {
            // a bone clip (facial pose-weight clips are refused by name; the first banks are full of them)
            report.Add($"sequences: {anims?.Sequences.Count ?? -1}");
            foreach (var seq in (anims?.Sequences ?? []).Where(s => !s.Record.IsPlaceholder && s.Record.Anm2Name.Length > 0
                                                                    && !s.Record.Anm2Name.Contains("brk_", StringComparison.OrdinalIgnoreCase)
                                                                    && !s.Record.Anm2Name.Contains("dlg_", StringComparison.OrdinalIgnoreCase)
                                                                    && s.Record.Anm2Name.Contains("idle", StringComparison.OrdinalIgnoreCase))
                                                        .DistinctBy(s => s.Record.Anm2Name).Take(200))
            {
                try
                {
                    var item = ProjectAssets.AddAnim(project, stock, seq.Record.Anm2Name, seq.Record.Fps > 0 ? seq.Record.Fps : 30, null,
                                                     $"{seq.Bank}@{seq.Record.Name}");
                    report.Add($"add clip: {item.Folder}");
                    return;
                }
                catch (ProjectException e) { report.Add($"clip {seq.Record.Anm2Name} refused: {e.Message}"); }
            }
        });
        _workspace.NotifyProjectContentChanged();
    }
}
