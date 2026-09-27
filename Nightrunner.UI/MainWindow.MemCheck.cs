using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using AvalonDock.Layout;
using Nightrunner.Core.Games;
using Nightrunner.Core.Logging;
using Nightrunner.UI.Views;

namespace Nightrunner.UI;

/// <summary>
/// The memory soak (<c>NIGHTRUNNER_MEMCHECK=&lt;folder&gt;</c>, game from <c>NIGHTRUNNER_MEMCHECK_GAME</c>, default dl2;
/// model count from <c>NIGHTRUNNER_MEMCHECK_MODELS</c>, texture count from <c>NIGHTRUNNER_MEMCHECK_TEXTURES</c>):
/// opens the game, shows models one after another in the viewport, then previews textures, and writes one CSV row per
/// step: private commit, working set split into private and shared (mapped pack pages), GC heap, and the session caches.
/// </summary>
public sealed partial class MainWindow
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemCounters
    {
        public uint cb, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                     QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage,
                     PrivateUsage, PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref MemCounters counters, uint cb);

    private static string MemRow(string step, Workspace ws)
    {
        var c = new MemCounters { cb = (uint)Marshal.SizeOf<MemCounters>() };
        GetProcessMemoryInfo(Process.GetCurrentProcess().Handle, ref c, c.cb);
        var gc = GC.GetGCMemoryInfo();
        long texBytes = 0;
        int texCount = 0;
        foreach (var t in ws.Textures.Values)
        {
            if (t is null) continue;
            texCount++;
            texBytes += (t.Dds?.LongLength ?? 0) + (t.Bgra?.LongLength ?? 0);
        }
        const double MB = 1024 * 1024;
        return string.Join(',',
            step.Replace(',', ';'),
            (c.PrivateUsage / MB).ToString("F0"),
            (c.PeakPagefileUsage / MB).ToString("F0"),
            (c.WorkingSetSize / MB).ToString("F0"),
            (c.PrivateWorkingSetSize / MB).ToString("F0"),
            ((c.WorkingSetSize - c.PrivateWorkingSetSize) / MB).ToString("F0"),
            (gc.HeapSizeBytes / MB).ToString("F0"),
            (gc.TotalCommittedBytes / MB).ToString("F0"),
            (GC.GetTotalMemory(false) / MB).ToString("F0"),
            texCount.ToString(),
            (texBytes / MB).ToString("F0"),
            ws.Surfaces.Count.ToString());
    }

    public async Task RunMemCheck(string folder)
    {
        Directory.CreateDirectory(folder);
        string csv = Path.Combine(folder, "mem.csv");
        File.WriteAllText(csv, "step,private_mb,peak_private_mb,ws_mb,ws_private_mb,ws_shared_mb,gc_heap_mb,gc_committed_mb,gc_live_mb,tex_count,tex_mb,surfaces" + Environment.NewLine);
        void Sample(string step)
        {
            var row = MemRow(step, _workspace);
            File.AppendAllText(csv, row + Environment.NewLine);
            Log.Info("memcheck", row);
        }
        void Collect()
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        int Env(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? v : fallback;

        Sample("start");
        string gameKey = Environment.GetEnvironmentVariable("NIGHTRUNNER_MEMCHECK_GAME") is { Length: > 0 } g ? g : "dl2";
        var install = GameInstall.FindInstalls()[gameKey];
        ApplyView("Meshes");
        OpenWorkspace(() => Task.CompletedTask);
        await _workspace.OpenGame(install);
        GameLabel.Text = _workspace.Title;
        await Task.Delay(2000);
        Sample("game_open");

        // models, spread across the whole list, each shown in the viewport
        var models = await Task.Run(() => _workspace.Models);
        Sample("models_indexed");
        Open("viewport");
        await WaitRender();
        var main = (ViewportView)_open["viewport"].Content;
        var winners = models!.Models.Where(m => m.Wins).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        int wantModels = Math.Min(Env("NIGHTRUNNER_MEMCHECK_MODELS", 120), winners.Count);
        double stride = winners.Count / (double)Math.Max(1, wantModels);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < wantModels; i++)
        {
            var m = winners[(int)(i * stride)];
            string before = main.StatusText;
            Reveal("models", m.Name);
            ((LayoutContent)_open["viewport"]).IsSelected = true;
            await Until(() => main.StatusText != before && main.StatusText.Contains(" ms") ||
                              main.StatusText != before && main.View.RenderError is not null, 60000);
            await Task.Delay(300);
            Sample($"model {i} {m.Basename}");
        }
        Sample($"models_done {sw.Elapsed.TotalSeconds:F0}s");
        Collect();
        Sample("models_done_gc");

        // prefabs: open the tab (builds the catalog), then show a spread of prefabs in the viewport
        int wantPrefabs = Env("NIGHTRUNNER_MEMCHECK_PREFABS", 0);
        if (wantPrefabs > 0)
        {
            Open("prefabs");
            await WaitRender();
            var prefabs = await Task.Run(() => _workspace.Prefabs);
            Sample($"prefab_catalog {prefabs!.Prefabs.Count} entries");
            string only = Environment.GetEnvironmentVariable("NIGHTRUNNER_MEMCHECK_PREFAB_KIND") ?? "";
            var pick = prefabs.Prefabs.Where(p => p.Wins && (only == "" || p.Source.ToString().Equals(only, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
            if (Environment.GetEnvironmentVariable("NIGHTRUNNER_MEMCHECK_PREFAB_NAMES") is { Length: > 0 } names)
            {
                var want = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                pick = want.Select(n => pick.FirstOrDefault(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).OfType<Nightrunner.Core.Prefab.PrefabEntry>().ToList();
                wantPrefabs = pick.Count;
            }
            double pstride = pick.Count / (double)Math.Max(1, wantPrefabs);
            var psw = Stopwatch.StartNew();
            for (int i = 0; i < wantPrefabs && i < pick.Count; i++)
            {
                var p = pick[(int)(i * pstride)];
                string before = main.StatusText;
                _selection.Set("Prefabs", new Selected.Prefab(prefabs, p));
                ((LayoutContent)_open["viewport"]).IsSelected = true;
                await Until(() => main.StatusText != before && (main.StatusText.StartsWith("prefab", StringComparison.Ordinal) && main.StatusText.Contains(" ms") ||
                                                                main.StatusText.Contains("no meshes") || main.View.RenderError is not null), 20000);
                await Task.Delay(300);
                int loadedText = prefabs.Prefabs.Count(e => e.Source == Nightrunner.Core.Prefab.PrefabSource.Pak && e.IsLoaded);
                Sample($"prefab {i} {p.Source} {p.Name} [{main.StatusText}] textLoaded={loadedText}");
            }
            Sample($"prefabs_done {psw.Elapsed.TotalSeconds:F0}s");
            Collect();
            Sample("prefabs_done_gc");
            // what each cache holds: drop it and measure the difference
            main.View.Show(null);
            Collect();
            Sample("drop_viewport_scene");
            _workspace.DropMeshCache();
            Collect();
            Sample("drop_mesh_cache");
            _workspace.Textures.Clear();
            _workspace.Surfaces.Clear();
            Collect();
            Sample("drop_texture_caches");
            int texts = prefabs.Prefabs.Count(e => e.Source == Nightrunner.Core.Prefab.PrefabSource.Pak && e.IsLoaded);
            Sample($"remaining: text prefab documents loaded={texts}, sub-prefab entries={prefabs.SubPrefabs.Count}");
        }

        // animations: open the tab (builds the sequence catalog), then play a spread of clips in the viewport
        int wantAnims = Env("NIGHTRUNNER_MEMCHECK_ANIMS", 0);
        if (wantAnims > 0)
        {
            Open("animations");
            await WaitRender();
            var anims = await Task.Run(() => _workspace.Animations);
            Sample("anim_catalog");
            _ = await Task.Run(() => _workspace.Rigs);
            Sample("anim_rigs");
            var seqs = anims!.Sequences.Where(s => !s.Record.IsPlaceholder).ToList();
            double astride = seqs.Count / (double)Math.Max(1, wantAnims);
            var asw = Stopwatch.StartNew();
            for (int i = 0; i < wantAnims && i < seqs.Count; i++)
            {
                var s = seqs[(int)(i * astride)];
                _selection.Set("Animations", new Selected.Sequence(anims, s));
                ((LayoutContent)_open["viewport"]).IsSelected = true;
                await Until(() => ReferenceEquals(main.PlayingSequence, s) && main.IsPlaying, 15000);
                await Task.Delay(400);
                Sample($"anim {i} {s.Bank}@{s.Record.Name} on {main.ShownModel}");
            }
            Sample($"anims_done {asw.Elapsed.TotalSeconds:F0}s");
            Collect();
            Sample("anims_done_gc");
        }

        // textures: preview a spread of texture resources
        int wantTex = Env("NIGHTRUNNER_MEMCHECK_TEXTURES", 150);
        if (wantTex > 0)
        {
            var hits = _workspace.Catalog.Search("", 0x20).Gids;
            Open("textures");
            await WaitRender();
            double tstride = hits.Length / (double)Math.Max(1, wantTex);
            for (int i = 0; i < wantTex && i < hits.Length; i++)
            {
                int gid = hits[(int)(i * tstride)];
                Reveal("textures", gid);
                await Task.Delay(700);
                if (i % 10 == 9) Sample($"texture {i} {_workspace.Catalog.Name(gid)}");
            }
            Collect();
            Sample("textures_done_gc");
        }

        Sample("end");
        File.WriteAllText(Path.Combine(folder, "done.txt"), DateTime.Now.ToString("O"));
    }
}
