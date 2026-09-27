using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using AvalonDock.Layout;
using Nightrunner.Core.Audio;
using Nightrunner.Core.Games;
using Nightrunner.Core.Logging;
using Nightrunner.UI.Views;
using Nightrunner.UI.Views.Inspectors;

namespace Nightrunner.UI;

/// <summary>
/// The audio check (<c>NIGHTRUNNER_AUDIOCHECK=&lt;folder&gt;</c>): per installed game, opens the Audio window, waits for the
/// scan and the event names, then drives it the way a user would — inspector probes, search, play, pause, seek, stop,
/// selection changes and play switches while playing, closing the window while playing — and ends by closing the app
/// with a sound playing. Volume is 0. Writes captures, <c>report.txt</c> and the audio log lines into the folder.
/// </summary>
public sealed partial class MainWindow
{
    public async Task RunAudioCheck(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int mark = Log.Recent.Length;
        void Save(string name)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(Capture(new WindowInteropHelper(this).Handle, out _)));
            using var fs = File.Create(Path.Combine(folder, name + ".png"));
            enc.Save(fs);
        }
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        T? Field<T>(AudioExplorerView v, string name) => (T?)typeof(AudioExplorerView).GetField(name, Any)!.GetValue(v);
        void Invoke(AudioExplorerView v, string method, params object?[] args) => typeof(AudioExplorerView).GetMethod(method, Any)!.Invoke(v, args);
        Task Play(AudioExplorerView v, AudioEntryRow row) => (Task)typeof(AudioExplorerView).GetMethod("PlayEntryAsync", Any)!.Invoke(v, [row])!;
        string Player(AudioExplorerView v) => $"'{v.PlayerTitle.Text}' {v.PlayerStatus.Text} {v.PlayerTime.Text}{(v.PlayerStatus.ToolTip is string tip ? $" ({tip})" : "")}";
        string Inspector() => _open.TryGetValue("inspector", out var i) && i.Content is InspectorView iv && iv.Host.Content is AudioInspector ai
            ? ai.Info.Text.Replace("\n", " | ") : "(no audio inspector)";
        var rng = new Random(11);

        var installs = GameInstall.FindInstalls(savedRoots: _workspace.Settings.Roots);
        AudioExplorerView? view = null;
        foreach (var id in new[] { "dltb", "dl2" })
        {
            if (!installs.TryGetValue(id, out var install)) { report.Add($"{id}: not installed"); continue; }
            var previous = view;
            bool previousPlaying = previous is not null && Field<AudioPlaybackSession>(previous, "_player") is not null;
            OpenWorkspace(() => _workspace.OpenGame(install));
            await Until(() => _workspace.Install?.Id == id, 30000);
            if (previous is not null)
                report.Add($"{id}: game switch with the previous window {(previousPlaying ? "playing" : "idle")} -> its player {(Field<AudioPlaybackSession>(previous, "_player") is null ? "disposed" : "STILL ALIVE")}, status {Player(previous)}");
            Open("inspector");
            Open("audio");
            view = (AudioExplorerView)_open["audio"].Content;
            view.VolumeSlider.Value = 0;
            ((LayoutContent)_open["audio"]).IsSelected = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Until(() => view.Status.Text.Contains("named"), 240000);
            report.Add($"{id}: status '{view.Status.Text}' after {sw.Elapsed.TotalSeconds:0.0} s; {view.ArchiveHeader.Text}; {view.Timing.Text}");
            Save($"{id}_1_list");
            var catalog = Field<AespCatalog>(view, "_catalog")!;
            var rows = view.Rows.Items.Cast<AudioEntryRow>().ToArray();

            view.Rows.SelectedIndex = 0;
            await Until(() => Inspector().Contains(" kHz"), 10000);
            report.Add($"{id}: inspector: {Inspector()}");
            Save($"{id}_2_inspector");

            view.Search.Text = "weapon";
            await Task.Delay(1500);
            report.Add($"{id}: search 'weapon': {view.Timing.Text}");
            view.Search.Text = rows[0].IdText;
            await Task.Delay(1000);
            report.Add($"{id}: search by id {rows[0].IdText}: {view.Timing.Text}");
            view.Search.Text = "";
            await Task.Delay(1500);

            var stereo = rows.Where(r => catalog.Entries[r.Index].Size is > 400_000 and < 3_000_000).OrderBy(_ => rng.Next()).Take(40).ToArray();
            var row = stereo[0];
            await Play(view, row);
            await Task.Delay(1500);
            report.Add($"{id}: play {row.IdText}: {Player(view)}");
            Save($"{id}_3_playing");

            int warnBefore = Log.Recent.Length;
            for (int k = 0; k < 60; k++) { view.Rows.SelectedIndex = rng.Next(view.Rows.Items.Count); await Task.Delay(rng.Next(5, 60)); }
            await Task.Delay(1500);
            report.Add($"{id}: 60 selection changes while playing: {Player(view)}; inspector now: {Inspector()[..Math.Min(80, Inspector().Length)]}; audio warnings {Log.Recent.Skip(warnBefore).Count(e => e.Source == "audio" && e.Level >= LogLevel.Warn)}");

            Invoke(view, "PlayPause_Click", this, new RoutedEventArgs());
            await Task.Delay(700);
            string paused = Player(view);
            await Task.Delay(700);
            report.Add($"{id}: pause: {paused} -> {Player(view)}");
            view.SeekSlider.Value = view.SeekSlider.Maximum * 0.25;
            Invoke(view, "SeekToSlider");
            await Task.Delay(600);
            report.Add($"{id}: seek to 25% while paused (stays paused): {Player(view)}");
            Invoke(view, "PlayPause_Click", this, new RoutedEventArgs());
            await Task.Delay(700);
            report.Add($"{id}: resume: {Player(view)}");
            view.SeekSlider.Value = view.SeekSlider.Maximum * 0.5;
            Invoke(view, "SeekToSlider");
            await Task.Delay(600);
            report.Add($"{id}: seek to 50% of {view.SeekSlider.Maximum:0.0} s: {Player(view)}");
            Invoke(view, "Stop_Click", this, new RoutedEventArgs());
            await Task.Delay(500);
            report.Add($"{id}: stop: {Player(view)}");

            warnBefore = Log.Recent.Length;
            AudioEntryRow last = stereo[0];
            foreach (var r in stereo.Skip(1).Take(30)) { last = r; _ = Play(view, r); await Task.Delay(rng.Next(0, 30)); }
            await Task.Delay(2500);
            report.Add($"{id}: 30 play switches 0-30 ms apart: {Player(view)}; last asked {last.IdText}; audio warnings {Log.Recent.Skip(warnBefore).Count(e => e.Source == "audio" && e.Level >= LogLevel.Warn)}");

            var shortRow = rows.Where(r => catalog.Entries[r.Index].Size is > 3_000 and < 12_000).First();
            await Play(view, shortRow);
            await Task.Delay(3000);
            report.Add($"{id}: short WEM {shortRow.IdText} played to the end: {Player(view)}");
            Invoke(view, "PlayPause_Click", this, new RoutedEventArgs());
            await Task.Delay(300);
            report.Add($"{id}: Play after the end: {Player(view)}");

            await Play(view, stereo[1]);
            await Task.Delay(800);
            var closing = view;
            ((LayoutContent)_open["audio"]).Close();
            await Task.Delay(800);
            report.Add($"{id}: closed the Audio window while playing -> player {(Field<AudioPlaybackSession>(closing, "_player") is null ? "disposed" : "STILL ALIVE")}");

            Open("audio");
            view = (AudioExplorerView)_open["audio"].Content;
            view.VolumeSlider.Value = 0;
            await Until(() => view.Status.Text.Contains("WEMs"), 240000);
            rows = view.Rows.Items.Cast<AudioEntryRow>().ToArray();
            await Play(view, rows.First(r => r.Index == stereo[2].Index));
            await Task.Delay(1000);
            report.Add($"{id}: reopened and playing: {Player(view)}");
        }

        report.Add($"closing the app while playing: {(view is null ? "-" : Player(view))}");
        File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
        File.WriteAllLines(Path.Combine(folder, "log.txt"), Log.Recent.Skip(mark).Where(e => e.Source == "audio")
                                                              .Select(e => $"{e.TimeText} {e.Level} {e.Message}"));
        Close();
    }
}
