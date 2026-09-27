using System.IO;
using AvalonDock.Layout;
using Nightrunner.Core.Games;
using Nightrunner.UI.Views;

namespace Nightrunner.UI;

public sealed partial class MainWindow
{
    /// <summary>
    /// <c>NIGHTRUNNER_DOCKCHECK_MODS=1</c>: the viewport's Mods and Follow toggles, on DLTB with runtime modding on (set in
    /// memory only: the dock check never saves settings) and on DL2. The stock player with Mods off and on, a mesh a mod
    /// overrides and one only a mod has, then a clip played with Follow on (the model follows the rig) and off (the NPC
    /// shown stays), and Follow turned back on while it plays (picked again).
    /// </summary>
    private async Task ModsCheck(Func<string, Task> shot, List<string> report, GameInstall dltb, GameInstall dl2)
    {
        ApplyView("Meshes");
        foreach (var (install, player, npc, clipBank, clipSeq) in new[]
                 {
                     (dltb, "player_kc_basic_tpp.model", "dlc_ft_man_flock_01_a.model", "weapon_unarmed", "fpp_balls_begindodgeback"),
                     (dl2, "player_aiden_melee_a.model", "man_sc_worker_01.model", "anims_player", "m_fpp_stick_idle_relaxed"),
                 })
        {
            _workspace.Settings.SetRuntimeModding(install.Id, true);
            OpenWorkspace(() => Task.CompletedTask);
            await _workspace.OpenGame(install);
            GameLabel.Text = _workspace.Title;
            ((LayoutContent)_open["viewport"]).IsSelected = true;
            await _workspace.WaitLoaded();
            string id = install.Id;
            var main = (ViewportView)_open["viewport"].Content;
            await main.SetMods(false);
            await main.SetFollow(true);
            var loaded = _workspace.Catalog.IndexedPacks.Where(p => !_workspace.Origins.IsStock(p.Path)).ToList();
            report.Add($"{id}: modded packs {string.Join(", ", loaded.Select(p => p.Label))}; stock view {(_workspace.Content(false).Stock ? "separate" : "same as all")}");
            if (_workspace.Models is not { } models) continue;

            async Task ShowModel(Nightrunner.Core.Model.ModelCatalog m, Nightrunner.Core.Model.ModelEntry e, string what)
            {
                string before = main.StatusText;
                _selection.Set("Models", new Selected.Model(m, e));
                await Until(() => main.ShownModel == e.Basename && main.StatusText.Contains(" ms") && main.StatusText != before, 90000);
                await WaitRender();
                await shot($"{id}_{what}");
                report.Add($"{id} {what}: {e.Name} ({Path.GetFileName(e.Pak)}) mods {main.Mods} -> {main.StatusText}");
            }
            async Task Flip(bool mods, string what)
            {
                string before = main.StatusText;
                await main.SetMods(mods);
                await Until(() => main.StatusText.Contains(" ms") && main.StatusText != before, 60000);
                await WaitRender();
                await shot($"{id}_{what}");
                report.Add($"{id} {what}: mods {main.Mods} -> {main.StatusText}");
            }

            // 1. the stock player: Mods off, then on (what the game loads), then off again
            if (models.Find(player) is { } p)
            {
                await ShowModel(models, p, "player_modsoff");
                await Flip(true, "player_modson");
                await Flip(false, "player_modsoff_again");
            }

            // 2. meshes: one a mod overrides (stock copy with Mods off), one only a mod has (drawn, marked mod)
            var overridden = new List<int>();
            var modOnly = new List<int>();
            foreach (var pack in loaded)
                for (int i = 0; i < pack.Count && overridden.Count + modOnly.Count < 4000; i++)
                    if (pack.Pack!.Logicals[i].Type == 0x10)
                    {
                        int gid = pack.Base + i;
                        (_workspace.Content(false).Resource(gid) is int s && s != gid ? overridden : modOnly).Add(gid);
                    }
            report.Add($"{id} mod meshes: {overridden.Count} override stock ({string.Join(", ", overridden.Take(6).Select(_workspace.Catalog.Name))}), " +
                       $"{modOnly.Count} only in mods ({string.Join(", ", modOnly.Take(3).Select(_workspace.Catalog.Name))})");
            foreach (var (gid, what) in overridden.Take(1).Select(g => (g, "mesh_overridden")).Concat(modOnly.Take(1).Select(g => (g, "mesh_modonly"))))
            {
                string before = main.StatusText;
                _selection.Set("Meshes", new Selected.Mesh(_workspace.Catalog, gid));
                await Until(() => main.StatusText.Contains(" ms") && main.StatusText != before, 60000);
                await WaitRender();
                await shot($"{id}_{what}_modsoff");
                report.Add($"{id} {what}: {_workspace.Catalog.Name(gid)} ({_workspace.Catalog.Split(gid).Entry.Label}) mods off -> {main.StatusText}");
                await Flip(true, $"{what}_modson");
                await main.SetMods(false);
            }

            // 3. a player clip on an NPC: Follow on loads the player; off keeps the NPC; on again while it plays picks again
            if (await Task.Run(() => _workspace.Animations) is { } anims && anims.Find(clipBank, clipSeq) is { } q && models.Find(npc) is { } n)
            {
                async Task Play(string what)
                {
                    _selection.Set("Animations", new Selected.Sequence(anims, q));
                    await Until(() => main.IsPlaying && main.PlayingSequence == q, 90000);
                    main.PauseAt(1e9);
                    await WaitRender();
                    await shot($"{id}_{what}");
                    report.Add($"{id} {what}: {clipBank}@{clipSeq} follow {main.Follow} on {main.ShownModel} -> {main.StatusText}");
                }
                await ShowModel(models, n, "npc");
                await Play("clip_follow_on");
                await main.SetFollow(false);
                await ShowModel(models, n, "npc_again");
                await Play("clip_follow_off");
                await main.SetFollow(true);
                await Until(() => main.IsPlaying && main.ShownModel != n.Basename, 90000);
                main.PauseAt(1e9);
                await WaitRender();
                await shot($"{id}_clip_follow_reon");
                report.Add($"{id} clip_follow_reon: follow {main.Follow} on {main.ShownModel} playing {main.PlayingSequence == q} -> {main.StatusText}");
            }
        }
    }
}
