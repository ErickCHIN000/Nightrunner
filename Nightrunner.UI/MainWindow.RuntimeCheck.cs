using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Nightrunner.Core.Games;
using Nightrunner.Core.Logging;
using Nightrunner.UI.Views;

namespace Nightrunner.UI;

/// <summary>
/// The runtime check (<c>NIGHTRUNNER_RUNTIMECHECK=&lt;folder&gt;</c>): captures the Settings page as it opens and after its
/// runtime checks ran, then the Mods window for each installed game, into the folder with <c>report.txt</c>. Settings are
/// not saved while it runs. <c>NIGHTRUNNER_RUNTIMECHECK_SWITCH=&lt;mod id&gt;</c>: on DLTB (and the fake root), also click that mod's "on" box
/// twice (it writes <c>nightrunner.json</c>, then puts it back) and capture the window after each click.
/// </summary>
public sealed partial class MainWindow
{
    public async Task RunRuntimeCheck(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        Width = 1500;
        Height = 1300;
        void Save(string name)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(Capture(new WindowInteropHelper(this).Handle, out _)));
            using var fs = File.Create(Path.Combine(folder, name + ".png"));
            enc.Save(fs);
            report.Add($"captured {name}");
        }

        ShowSettings();
        await Task.Delay(1500);
        Save("settings_open");
        foreach (var row in _settings.GameRows)
        {
            if (row.Install is null) continue;
            row.ValidateRuntime();
            report.Add($"{row.Id}: {row.RuntimeStatus.Replace(Environment.NewLine, " | ")}");
            foreach (var p in row.RuntimePaths) report.Add($"{row.Id}:   {p.Label,-8} {p.State,-7} {p.Path}");
            if (row.RuntimeMods.Length > 0) report.Add($"{row.Id}:   {row.RuntimeMods}");
        }
        await Task.Delay(800);
        Save("settings_checked");

        // the Mods window on each installed game: the list, a mod's items, its overrides revealed in Meshes
        var installs = GameInstall.FindInstalls(savedRoots: _workspace.Settings.Roots);
        // NIGHTRUNNER_RUNTIMECHECK_FAKE=<root>: also a DLTB-shaped scratch root (invalid mods), never a game folder
        if (Environment.GetEnvironmentVariable("NIGHTRUNNER_RUNTIMECHECK_FAKE") is { Length: > 0 } fake)
            installs["fake"] = new GameInstall(fake, GameProfile.Dltb);
        foreach (var id in new[] { "dltb", "dl2", "fake" })
        {
            if (!installs.TryGetValue(id, out var install)) continue;
            OpenWorkspace(() => _workspace.OpenGame(install));
            ApplyView("Mods");
            await Task.Delay(500);
            if (!_open.TryGetValue("mods", out var item) || item.Content is not ModsView mods)
            {
                report.Add($"{id}: no Mods window");
                continue;
            }
            item.IsSelected = true;
            await Until(() => mods.Rows.Count > 0 || mods.IsLoaded, 10000);
            await mods.Ready;
            await Task.Delay(800);
            foreach (var r in mods.Rows)
                report.Add($"{id}: mod {r.Id} order {r.Order} on {r.On} loads {r.Loads} valid {r.Valid} items {r.Mod.Items.Count} " +
                           $"overrides {r.OverrideCount} problems {r.Mod.Problems.Count}");
            Save($"mods_{id}");
            if (id is "dltb" or "fake" && Environment.GetEnvironmentVariable("NIGHTRUNNER_RUNTIMECHECK_SWITCH") is { Length: > 0 } switchId)
                await SwitchCheck(mods, install, switchId, report, Save);
            if (mods.Rows.FirstOrDefault(r => !r.Mod.Valid) is { } invalid)
            {
                mods.ModList.SelectedItem = invalid;
                mods.Tabs.SelectedIndex = 2;
                await Task.Delay(600);
                Save($"mods_{id}_invalid");
            }
            if (mods.Rows.FirstOrDefault(r => r.Overrides is { Count: > 0 }) is { } with)
            {
                mods.ModList.SelectedItem = with;
                mods.Tabs.SelectedIndex = 1;
                await Task.Delay(600);
                Save($"mods_{id}_overrides");
                var mesh = with.Overrides!.FirstOrDefault(o => o.Type == 0x10);
                if (mesh is not null)
                {
                    mods.OverrideList.SelectedItem = mods.OverrideList.Items.OfType<ModOverrideRow>().First(o => o.Value == mesh);
                    Reveal("meshes", mesh.Name);
                    await Task.Delay(2500);
                    Save($"mods_{id}_meshes");
                    report.Add($"{id}: revealed {mesh.Name} in Meshes");
                }
            }
        }

        File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
        File.WriteAllLines(Path.Combine(folder, "log.txt"), Log.Recent.Select(e => $"{e.TimeText} {e.Source,-9} {e.Message} {e.DurationText}"));
        Close();
    }

    /// <summary>
    /// Click <paramref name="modId"/>'s "on" box in the Mods window (the real click path), wait for the reload, report the
    /// rows, what the workspace loads and the file, capture; then click it back and check the file has its first bytes again.
    /// </summary>
    private async Task SwitchCheck(ModsView mods, GameInstall install, string modId, List<string> report, Action<string> save)
    {
        string path = Path.Combine(install.Paths.RuntimeFolder, RuntimeContent.SettingsName);
        byte[]? original = File.Exists(path) ? File.ReadAllBytes(path) : null;
        report.Add($"switch: {modId} · {RuntimeContent.SettingsName} {(original is null ? "none" : $"{original.Length} bytes")}");
        for (int step = 0; step < 2; step++)
        {
            var row = mods.Rows.FirstOrDefault(r => r.Id.Equals(modId, StringComparison.OrdinalIgnoreCase));
            if (row is null) { report.Add("switch: no such mod"); return; }
            mods.ModList.SelectedItem = row;
            mods.ModList.ScrollIntoView(row);
            await Task.Delay(300);
            var box = mods.ModList.ItemContainerGenerator.ContainerFromItem(row) is ListViewItem item ? Child<CheckBox>(item) : null;
            if (box is null) { report.Add("switch: no box"); return; }
            bool want = box.IsChecked != true;
            var reloaded = new TaskCompletionSource();
            void Opened() => reloaded.TrySetResult();
            _workspace.Opened += Opened;
            typeof(ButtonBase).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(box, null);
            await Task.WhenAny(reloaded.Task, Task.Delay(15000));
            _workspace.Opened -= Opened;
            await Task.Delay(300);
            await mods.Ready;
            await _workspace.WaitLoaded();
            await Task.Delay(1000);
            var last = _workspace.LastSwitch;
            report.Add($"switch: click {modId} -> {(want ? "on" : "off")}: {last?.State} · {last?.Message} · reloaded {reloaded.Task.IsCompleted}");
            foreach (var r in mods.Rows) report.Add($"switch:   row {r.Id} on {r.On} loads {r.Loads} valid {r.Valid}");
            report.Add($"switch:   workspace loads {string.Join(", ", _workspace.Runtime.Items.Where(i => i.Loads).Select(i => i.Source).Distinct())}" +
                       $" · {_workspace.Catalog.IndexedPacks.Length} packs");
            foreach (var line in File.ReadAllLines(path)) report.Add($"switch:   | {line}");
            save($"switch_{step + 1}_{(want ? "on" : "off")}");
        }
        byte[]? now = File.Exists(path) ? File.ReadAllBytes(path) : null;
        report.Add($"switch: {RuntimeContent.SettingsName} back to its first bytes: " +
                   $"{(original is null ? now is null : now is not null && now.AsSpan().SequenceEqual(original))}");
    }

    private static T? Child<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var c = VisualTreeHelper.GetChild(parent, i);
            if (c is T t) return t;
            if (Child<T>(c) is { } found) return found;
        }
        return null;
    }
}
