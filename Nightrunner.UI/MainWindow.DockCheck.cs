using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvalonDock.Layout;
using Nightrunner.Core.Games;
using Nightrunner.Core.Logging;
using Nightrunner.UI.Views;

namespace Nightrunner.UI;

/// <summary>
/// The viewport dock check (<c>NIGHTRUNNER_DOCKCHECK=&lt;folder&gt;</c>): drives the dock scenarios a D3DImage viewport has
/// to survive — docked, floated, docked back, tabbed away and back, two viewports, resize, a game switch, and the
/// device-lost recovery path — and after each one captures every top-level window with PrintWindow (the composed
/// pixels, D3DImage included) into the folder, logging how much of each viewport is drawn.
/// </summary>
public sealed partial class MainWindow
{
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 r);

    private struct Rect32 { public int L, T, R, B; }

    public async Task RunDockCheck(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int step = 0;
        async Task Shot(string what)
        {
            await Task.Delay(700);
            step++;
            foreach (var view in FindViewports())
            {
                if (PresentationSource.FromVisual(view) is not HwndSource src) continue;
                var win = src.RootVisual as Window;
                var hwnd = src.Handle;
                var bmp = Capture(hwnd, out var origin);
                string file = Path.Combine(folder, $"{step:00}_{what}_{(win == this ? "main" : "float")}_{view.GetHashCode():X}.png");
                using (var fs = File.Create(file))
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    enc.Save(fs);
                }
                double drawn = Drawn(bmp, origin, view.View);
                string line = $"{step:00} {what,-14} {(win == this ? "main" : "floating"),-8} drawn {drawn:P1} error {view.View.RenderError ?? "none"} status '{view.StatusText}'";
                report.Add(line);
                Log.Info("dockcheck", line);
            }
        }

        var dltb = GameInstall.FindInstalls()["dltb"];
        var dl2 = GameInstall.FindInstalls()["dl2"];
        if (Environment.GetEnvironmentVariable("NIGHTRUNNER_DOCKCHECK_MODS") == "1")
        {
            await ModsCheck(Shot, report, dltb, dl2);
            File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
            File.WriteAllLines(Path.Combine(folder, "log.txt"), Log.Recent.Select(e => $"{e.TimeText} {e.Source,-9} {e.Message} {e.DurationText}"));
            Close();
            return;
        }
        if (Environment.GetEnvironmentVariable("NIGHTRUNNER_DOCKCHECK_ANIM") == "1")
        {
            // animations only: the DLTB and DL2 steps below, for quick runs on the viewer's animation paths
            ApplyView("Meshes");
            foreach (var install in new[] { dltb, dl2 })
            {
                OpenWorkspace(() => Task.CompletedTask);
                await _workspace.OpenGame(install);
                GameLabel.Text = _workspace.Title;
                ((LayoutContent)_open["viewport"]).IsSelected = true;
                await _workspace.WaitLoaded();
                await GameAnims(install.Id, Shot, report, folder);
            }
            File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
            File.WriteAllLines(Path.Combine(folder, "log.txt"), Log.Recent.Select(e => $"{e.TimeText} {e.Source,-9} {e.Message} {e.DurationText}"));
            Close();
            return;
        }
        ApplyView("Meshes");
        OpenWorkspace(() => Task.CompletedTask);
        await _workspace.OpenGame(dltb);
        GameLabel.Text = _workspace.Title;
        Reveal("meshes", "player_kc_basic_torso_a_tpp");
        await WaitLoaded();
        await Shot("docked");

        var doc = (LayoutDocument)_open["viewport"];
        doc.Float();
        await WaitRender();
        await Shot("floated");

        doc.Dock();
        doc.IsSelected = true;
        await WaitRender();
        await Shot("dockedback");

        Open("textures");
        await WaitRender();
        ((LayoutContent)_open["viewport"]).IsSelected = true;
        await WaitRender();
        await Shot("tabbedback");

        var second = new LayoutDocument { Title = "Viewport 2", ContentId = "viewport2", Content = new ViewportView(Context), CanClose = true };
        DocumentPane().Children.Add(second);
        _open["viewport2"] = second;
        second.Float();
        ((LayoutContent)_open["viewport"]).IsSelected = true;
        await WaitLoaded();
        await Shot("twoviewports");

        var gid = _selection.Current is Selected.Mesh sm ? sm.Gid : -1;
        Reveal("viewport", new IsolateRequest(_workspace.Catalog, gid, 0, 2));
        await WaitRender();
        await Shot("isolated");
        Reveal("viewport", new IsolateRequest(_workspace.Catalog, gid, null, null));
        ((ViewportView)_open["viewport"].Content).View.Wireframe = true;
        await WaitRender();
        await Shot("wireframe");
        ((ViewportView)_open["viewport"].Content).View.Wireframe = false;

        Width -= 300;
        Height -= 150;
        await WaitRender();
        await Shot("resized");

        Viewport.Gpu.Effects.DisposeAllResources();
        Viewport.Gpu.Effects.Reinitialize();
        await WaitRender();
        await Shot("devicereset");

        // skins: the picker drives the material swap; the Inspector follows through Workspace.SkinSelected
        var main = (ViewportView)_open["viewport"].Content;
        async Task Skins(string mesh, params string[] names)
        {
            Reveal("meshes", mesh);
            ((LayoutContent)_open["viewport"]).IsSelected = true;
            await WaitLoaded();
            foreach (var n in names)
            {
                var skins = Nightrunner.Core.Mesh.MeshSkins.Decode((await _workspace.Mesh(((Selected.Mesh)_selection.Current!).Gid)).SkinRaw!);
                main.PickSkin(skins.Skins.FindIndex(k => k.NameStr == n));
                await WaitRender();
                await Shot($"skin_{n.Replace(' ', '_')}");
            }
        }
        await Skins("veh_sedan_a", "Default", "Default_broken_glass", "Police", "Taxi", "color_a");
        await Skins("int_ce_a_elevator_2x3_body_a", "Default", "woodparquet_a_brown", "marbletiles_b_green");

        // models: the whole player assembled in bind pose, then bones over it, a picked bone, a hidden slot
        Reveal("models", "models/ft/player/player_kc_basic_tpp.model");
        ((LayoutContent)_open["viewport"]).IsSelected = true;
        await Until(() => main.StatusText.StartsWith("model ") && main.StatusText.Contains(" ms"));
        await WaitRender();
        await Shot("model");
        main.FrameBone("head", 0.16);
        await WaitRender();
        await Shot("model_face");
        main.View.Frame();
        if (_open.TryGetValue("inspector", out var insp) && insp.Content is FrameworkElement ie &&
            Find<System.Windows.Controls.TabItem>(ie, t => (t.Header as string) == "Bones") is { } bonesTab)
            bonesTab.IsSelected = true;
        main.SetBones(true);
        await WaitRender();
        await Shot("model_bones");
        _workspace.SelectBone("head");
        await WaitRender();
        await Shot("model_bone_head");
        main.SetBones(false);
        main.SetSlotVisible(0, false);
        await WaitRender();
        await Shot("model_slot0_hidden");
        main.SetSlotVisible(0, true);
        // animation: a player TPP clip played on the shown model (CPU skinning), captured at two moments
        if (await Task.Run(() => _workspace.Animations) is { } anims &&
            anims.Sequences.FirstOrDefault(s => s.Bank == "anims_player" && s.Record.Name.StartsWith("tpp_", StringComparison.Ordinal) &&
                                                s.Record.Name.Contains("run") && !s.Record.IsPlaceholder) is { } run)
        {
            main.SetBones(false);
            _selection.Set("Animations", new Selected.Sequence(anims, run));
            await Until(() => main.StatusText.Length > 0 && main.IsPlaying, 30000);
            await Task.Delay(400);
            await Shot("anim_a");
            await Task.Delay(300);
            await Shot("anim_b");
            report.Add($"animation: {run.Bank}@{run.Record.Name} ({run.Record.Anm2Name}) playing {main.IsPlaying}");
            // Space toggles playback with the 3D view focused
            bool wasPlaying = main.IsPlaying;
            main.PressInView(System.Windows.Input.Key.Space);
            bool paused = !main.IsPlaying;
            main.PressInView(System.Windows.Input.Key.Space);
            report.Add($"space in view: playing {wasPlaying}, paused {paused}, playing again {main.IsPlaying}");
            // the model follows the clip's rig: an FPP clip loads the FPP player, a banshee clip a banshee
            foreach (var (bank, prefix, shot) in new[] { ("weapon_unarmed", "fpp_", "anim_fpp"), ("anims_man_all", "banshee_", "anim_banshee") })
            {
                if (anims.Sequences.FirstOrDefault(s => s.Bank == bank && s.Record.Name.StartsWith(prefix, StringComparison.Ordinal) && !s.Record.IsPlaceholder &&
                                                        Nightrunner.Core.Anim.AnimCatalog.Clip(_workspace.Catalog, s.Record.Anm2Name) is not null) is not { } q) continue;
                _selection.Set("Animations", new Selected.Sequence(anims, q));
                await Until(() => main.IsPlaying && main.PlayingSequence == q, 60000);
                await WaitRender();
                await Task.Delay(400);
                await Shot(shot);
                report.Add($"{shot}: {q.Bank}@{q.Record.Name} on {main.ShownModel} playing {main.IsPlaying}");
                if (shot == "anim_fpp")
                {
                    // look through eyecamera, as the game's first-person view
                    main.SetEye(true);
                    await WaitRender();
                    await Task.Delay(300);
                    await Shot("anim_fpp_eye");
                    main.SetEye(false);
                }
            }
        }
        // layering: an NPC idle, then an additive clip played over it (hand to chin)
        if (await Task.Run(() => _workspace.Animations) is { } anims2 &&
            anims2.Find("npc_dialogs", "c_dominik_dialog_truefriends_ending_standing_idle") is { } idle &&
            anims2.Find("npc_dialogs", "hold_chin_additive") is { } chin)
        {
            main.SetLayering(false);
            _selection.Set("Animations", new Selected.Sequence(anims2, idle));
            await Until(() => main.IsPlaying && main.PlayingSequence == idle, 60000);
            await WaitRender();
            await Shot("anim_layer_base");
            main.SetLayering(true);
            _selection.Set("Animations", new Selected.Sequence(anims2, chin));
            await Until(() => main.LayerCount == 1, 30000);
            await WaitRender();
            await Task.Delay(300);
            await Shot("anim_layer");
            report.Add($"anim_layer: {idle.Record.Name} + {chin.Record.Name} on {main.ShownModel}, layers {main.LayerCount}");
            main.SetLayering(false);
        }
        // facial: an NPC with a v1 solid head, a facial-expression clip on its face (neutral frame, then mid-clip)
        if (await Task.Run(() => _workspace.Animations) is { } anims3 && _workspace.Models is { } faceModels &&
            faceModels.Find("dlc_ft_man_flock_01_a.model") is { } npc &&
            anims3.Sequences.FirstOrDefault(q => q.Bank == "solid_head_facial_expression" && !q.Record.IsPlaceholder &&
                                                 Nightrunner.Core.Anim.AnimCatalog.Clip(_workspace.Catalog, q.Record.Anm2Name) is not null) is { } expr)
        {
            main.SetLayering(false);
            _selection.Set("Models", new Selected.Model(faceModels, npc));
            await Until(() => main.ShownModel == npc.Basename && main.StatusText.Contains(" ms"), 60000);
            _selection.Set("Animations", new Selected.Sequence(anims3, expr));
            await Until(() => main.IsPlaying && main.PlayingSequence == expr, 60000);
            main.FrameBone("head", 0.14);
            main.PauseAt(0);
            await WaitRender();
            await Shot("face_neutral");
            main.PauseAt(1e9);
            await WaitRender();
            await Shot("face_expression");
            report.Add($"face: {expr.Bank}@{expr.Record.Name} on {main.ShownModel}");
            // v2 head (correctives) with one of its own dialogue lines
            if (faceModels.Find("dlc_ft_wmn_npc_olivia.model") is { } olivia &&
                anims3.Sequences.FirstOrDefault(q => q.Record.Anm2Name == "dlg_dlcftq1oliviafirstmeetaltdlg_dlc_ft_sq_olivia_034") is { } line)
            {
                _selection.Set("Models", new Selected.Model(faceModels, olivia));
                await Until(() => main.ShownModel == olivia.Basename && main.StatusText.Contains(" ms"), 60000);
                _selection.Set("Animations", new Selected.Sequence(anims3, line));
                await Until(() => main.IsPlaying && main.PlayingSequence == line, 60000);
                main.FrameBone("head", 0.14);
                foreach (var f in new[] { 60.0, 150.0, 240.0 })
                {
                    main.PauseAt(f);
                    await WaitRender();
                    await Shot($"face_v2_{f:0}");
                }
                report.Add($"face_v2: {line.Bank}@{line.Record.Name} on {main.ShownModel}");
            }
        }
        // prefab: meshes of a composite prefab (child entities expanded) placed by the engine's CreateXform
        if (await Task.Run(() => _workspace.Prefabs) is { } prefabCatalog)
        {
            var pick = prefabCatalog.Find("dlc_ft_vehicle_truck") ?? prefabCatalog.Prefabs.Where(p => p.Wins && p.Pack.Contains("common_prefabs"))
                .Select(p => (p, n: Nightrunner.Core.Prefab.PrefabPlacement.Meshes(prefabCatalog, p).Count))
                .Where(x => x.n is >= 8 and <= 40).OrderByDescending(x => x.n).Select(x => x.p).FirstOrDefault();
            if (pick is not null)
            {
                _selection.Set("Prefabs", new Selected.Prefab(prefabCatalog, pick));
                await Until(() => main.StatusText.StartsWith("prefab", StringComparison.Ordinal) && main.StatusText.Contains(" ms"), 60000);
                await WaitRender();
                await Shot("prefab");
                report.Add($"prefab: {pick.Name} -> {main.StatusText}");
                if (pick.Name == "dlc_ft_vehicle_truck")
                {
                    // the same truck with a class preset: its model mesh and vehicle script place wheels and doors on the rig
                    string before = main.StatusText;
                    _selection.Set("Prefabs", new Selected.Prefab(prefabCatalog, pick, "Preset;Vehicle_Baron"));
                    await Until(() => main.StatusText != before && main.StatusText.StartsWith("prefab", StringComparison.Ordinal) && main.StatusText.Contains(" ms"), 60000);
                    await WaitRender();
                    await Shot("prefab_rig");
                    main.View.FrameFrom(-40, 20);
                    await WaitRender();
                    await Shot("prefab_rig_side");
                    main.View.Frame();
                    report.Add($"prefab rig: {pick.Name} Preset;Vehicle_Baron -> {main.StatusText}");
                }
            }
        }
        // exports run on the job queue; the bar at the bottom shows the running one
        var sdbFile = (await _workspace.Sdb.EnsureAsync())?.File;
        var models = _workspace.Models!;
        var exportRoot = Path.Combine(folder, "export");
        var catalogNow = _workspace.Catalog;
        Func<int, Nightrunner.Core.Mesh.MeshModel> decode = gid => _workspace.Mesh(gid).GetAwaiter().GetResult();
        var job = Jobs.Run("export player_kc_basic_tpp.model", "export", ctx =>
            Nightrunner.Core.Export.AssetExport.ExportModel(models, models.Find("player_kc_basic_tpp.model")!, catalogNow, sdbFile,
                g => decode(g), null, exportRoot, new(Textures: false), t => ctx.Report(null, t), ctx.Token));
        await Task.Delay(1500);
        await Shot("export_running");
        await Until(() => !Jobs.Active.Contains(job), 120000);
        await WaitRender();
        await Shot("export_done");
        report.Add($"export job: {JobText.Text}");
        // a project with the player model override: the Items tab and the model editor
        var project = Nightrunner.Core.Project.ModProject.Create(Path.Combine(folder, "project"), "dockcheck", "dltb");
        _workspace.SetProject(project);
        var modelItem = Nightrunner.Core.Project.ProjectAssets.AddModel(project, models, models.Find("player_tpp_skeleton.model")!);
        Open("projects");
        await WaitRender();
        if (_open["projects"].Content is ProjectsView pv && pv.FindName("Tabs") is System.Windows.Controls.TabControl tabs) tabs.SelectedIndex = 1;
        _selection.Set("Projects", new Selected.ProjectModel(project, modelItem));
        await WaitRender();
        await Shot("project_model");
        var mainShot = Capture(new WindowInteropHelper(this).Handle, out _);
        using (var fs = File.Create(Path.Combine(folder, "project_model_main.png")))
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(mainShot));
            enc.Save(fs);
        }
        var usage = await _workspace.Usage();
        var player = _workspace.Models!.Load(_workspace.Models.Find("player_kc_basic_tpp.model")!);
        foreach (var mat in player.Slots.SelectMany(s => s.Meshes).Take(3).Select(m => m.MaterialsData[0].Name))
            report.Add($"used by {mat}: {usage?.MeshesOf(mat).Length ?? -1} meshes, {usage?.ModelsOf(mat).Length ?? -1} models");

        OpenWorkspace(() => Task.CompletedTask);
        await _workspace.OpenGame(dl2);
        GameLabel.Text = _workspace.Title;
        Reveal("meshes", "player_army_torso_a_tpp");
        ((LayoutContent)_open["viewport"]).IsSelected = true;
        await WaitLoaded();
        await Shot("gameswitch");
        await GameAnims("dl2", Shot, report, folder);

        // DL2 prefabs: the Prefabs view on the DL2 catalog (binary + text), a vehicle placed in the viewport
        if (await Task.Run(() => _workspace.Prefabs) is { } dl2Prefabs && dl2Prefabs.Find("veh_sedan_a") is { } sedan)
        {
            ApplyView("Prefabs");
            await WaitRender();
            _selection.Set("Prefabs", new Selected.Prefab(dl2Prefabs, sedan));
            var vp2 = FindViewports().FirstOrDefault();
            if (vp2 is not null) await Until(() => vp2.StatusText.StartsWith("prefab", StringComparison.Ordinal) && vp2.StatusText.Contains(" ms"), 90000);
            await WaitRender();
            await Shot("dl2_prefab");
            report.Add($"dl2 prefab: {sedan.Name} ({dl2Prefabs.Prefabs.Count:N0} entries) -> {vp2?.StatusText}");
        }

        // one click on "Everything" opens every window it lists, in the dock
        if (_layouts.Find("Everything") is { } everything)
        {
            ApplyView(everything.Name);
            await WaitRender();
            var missing = everything.AllPanels.Where(id => !_open.TryGetValue(id, out var c) || c.Root != Dock.Layout).ToList();
            report.Add($"everything: {_open.Count} open, missing {(missing.Count == 0 ? "none" : string.Join(",", missing))}");
            if (_open.TryGetValue("build", out var build))
            {
                build.IsSelected = true;
                await WaitRender();
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(Capture(new WindowInteropHelper(this).Handle, out _)));
                using var fs = File.Create(Path.Combine(folder, "everything_build.png"));
                enc.Save(fs);
            }
        }

        File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
        File.WriteAllLines(Path.Combine(folder, "log.txt"), Log.Recent.Select(e => $"{e.TimeText} {e.Source,-9} {e.Message} {e.DurationText}"));
        Close();
    }

    /// <summary>
    /// A game's animation paths: an FPP clip seen through the Eye, a TPP clip, a clip whose tracks
    /// pick another character's rig, and an object's clip played on its mesh — each reported with what it played on.
    /// </summary>
    private async Task GameAnims(string game, Func<string, Task> shot, List<string> report, string folder)
    {
        if (await Task.Run(() => _workspace.Animations) is not { } anims) return;
        // the Animations list filtered to one rpack, its rows showing the pack each clip plays from
        Open("animations");
        if (_open["animations"].Content is AnimationsView av)
        {
            await Until(() => av.RowCount > 0, 60000);
            int all = av.RowCount;
            av.FilterPack("player_anims_pc.rpack");
            await WaitRender();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(Capture(new WindowInteropHelper(this).Handle, out _)));
            using (var fs = File.Create(Path.Combine(folder, $"{game}_anims_rpack_main.png"))) enc.Save(fs);
            report.Add($"{game} animations: {all:N0} rows, {av.RowCount:N0} from player_anims_pc.rpack");
            av.FilterPack("all packs");
        }
        var main = (ViewportView)_open["viewport"].Content;
        ((LayoutContent)_open["viewport"]).IsSelected = true;
        main.SetLayering(false);
        var picks = game == "dl2"
            ? new[] { ("anims_player", "m_fpp_stick_idle_relaxed", "fpp"), ("m_fpp", "m_tpp_inventory_main_pose", "tpp"),
                      ("anims_man_all", "bsh_standup_back_01", "banshee"), ("anims_player", "obj_gre_gate_locked_01", "object") }
            : new[] { ("weapon_unarmed", "fpp_balls_begindodgeback", "fpp"), ("anims_player", "tpp_balls_runbackward", "tpp") };
        foreach (var (bank, seq, what) in picks)
        {
            if (anims.Find(bank, seq) is not { } q) { report.Add($"{game} anim {what}: {bank}@{seq} not found"); continue; }
            _selection.Set("Animations", new Selected.Sequence(anims, q));
            await Until(() => main.IsPlaying && main.PlayingSequence == q || main.StatusText.Contains(seq), 60000);
            await WaitRender();
            main.PauseAt(what == "object" ? 0 : 1e9);
            await WaitRender();
            await shot($"{game}_anim_{what}");
            report.Add($"{game} anim {what}: {bank}@{seq} on {main.ShownModel ?? main.StatusText} playing {main.PlayingSequence == q}");
            if (what == "object")
            {
                main.PauseAt(1e9);
                await WaitRender();
                await shot($"{game}_anim_{what}_end");
            }
            if (what == "fpp")
            {
                foreach (var f in new[] { 0.0, 1e9 })
                {
                    main.SetEye(true);
                    main.PauseAt(f);
                    await WaitRender();
                    await Task.Delay(300);
                    await shot($"{game}_anim_fpp_eye_{(f == 0 ? "start" : "end")}");
                }
                main.SetEye(false);
            }
        }
    }

    private IEnumerable<ViewportView> FindViewports() =>
        _open.Values.Select(c => c.Content).OfType<ViewportView>().Where(v => v.IsLoaded && v.IsVisible);

    private async Task WaitLoaded()
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 20000)
        {
            await Task.Delay(200);
            var views = FindViewports().ToList();
            if (views.Count > 0 && views.All(v => v.StatusText.Contains(" ms") || v.StatusText.Length > 0 && !v.StatusText.EndsWith("..."))) return;
        }
    }

    private static Task WaitRender() => Task.Delay(600);

    private static T? Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t && match(t)) return t;
            if (Find(c, match) is { } hit) return hit;
        }
        return null;
    }

    private static async Task Until(Func<bool> done, int ms = 30000)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < ms) await Task.Delay(200);
    }

    private static BitmapSource Capture(IntPtr hwnd, out Point origin)
    {
        GetWindowRect(hwnd, out var r);
        origin = new Point(r.L, r.T);
        int w = r.R - r.L, h = r.B - r.T;
        var wdc = GetWindowDC(hwnd);
        var mdc = CreateCompatibleDC(wdc);
        var hbm = CreateCompatibleBitmap(wdc, w, h);
        var old = SelectObject(mdc, hbm);
        PrintWindow(hwnd, mdc, 2);   // PW_RENDERFULLCONTENT: the composed window, D3DImage content included
        SelectObject(mdc, old);
        var src = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        DeleteObject(hbm);
        DeleteDC(mdc);
        ReleaseDC(hwnd, wdc);
        return src;
    }

    /// <summary>Share of the viewport's pixels that differ from its clear colour — 0 means nothing was presented.</summary>
    private static double Drawn(BitmapSource bmp, Point windowOrigin, FrameworkElement view)
    {
        var tl = view.PointToScreen(new Point(0, 0));
        var br = view.PointToScreen(new Point(view.ActualWidth, view.ActualHeight));
        int x0 = (int)(tl.X - windowOrigin.X), y0 = (int)(tl.Y - windowOrigin.Y);
        int x1 = (int)(br.X - windowOrigin.X), y1 = (int)(br.Y - windowOrigin.Y);
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        x1 = Math.Min(bmp.PixelWidth, x1); y1 = Math.Min(bmp.PixelHeight, y1);
        if (x1 <= x0 || y1 <= y0) return 0;
        var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int stride = bmp.PixelWidth * 4;
        var px = new byte[stride * bmp.PixelHeight];
        conv.CopyPixels(px, stride, 0);
        long diff = 0, all = 0;
        for (int y = y0; y < y1; y += 2)
            for (int x = x0; x < x1; x += 2)
            {
                int i = y * stride + x * 4;
                all++;
                if (Math.Abs(px[i] - 0x21) + Math.Abs(px[i + 1] - 0x1D) + Math.Abs(px[i + 2] - 0x1B) > 24) diff++;
            }
        return all == 0 ? 0 : (double)diff / all;
    }
}
