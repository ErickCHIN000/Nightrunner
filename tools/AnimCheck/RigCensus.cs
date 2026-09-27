using System.Diagnostics;
using Nightrunner.Core.Anim;
using Nightrunner.Core.Games;
using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;

/// <summary>
/// --rigs: which rig the viewer plays each sequence's clip on. For every sequence with a shipped bone clip (or every
/// n-th, with --limit), the tracks are bound to every rig; the pick is compared with the rig binding the most tracks.
/// "legacy" is the name-word pick used before the current rig pick (candidates within 80% of the best count, name words first). Object
/// clips (under a quarter of their tracks bind a rig) are looked up in <see cref="AnimObjects"/>. RIGS_LIST=1 lists the
/// rigs and the model each shows; RIGS_FIND="words" lists matching sequences instead of the census.
/// </summary>
static class RigCensus
{
    public static int Run(GameInstall install, string[] packs, int limit, int show)
    {
        var sw = Stopwatch.StartNew();
        using var catalog = new RpackCatalog();
        catalog.LoadAsync(packs, install.Assets).GetAwaiter().GetResult();
        using var models = new ModelCatalog(install.Paks(true), install.Paths.IsCustom);
        var rigs = AnimRigs.Build(catalog, models);
        var anims = AnimCatalog.Build(catalog);
        if (Environment.GetEnvironmentVariable("RIGS_LIST") is not null)
            foreach (var r in rigs.Rigs) Console.WriteLine($"  rig {r.Skeleton}: {r.Bones.Count} bones, {r.Models.Count} models -> {AnimRigs.Model(r, null, "")}");
        Console.WriteLine($"{rigs.Rigs.Count} rigs ({rigs.Missing.Count} skeletons missing), {anims.Sequences.Count:N0} sequences, loaded in {sw.Elapsed.TotalSeconds:F1} s");

        if (Environment.GetEnvironmentVariable("RIGS_FIND") is { } find)
        {
            var words = find.Split(' ');
            foreach (var s in anims.Sequences.Where(s => words.All(w => s.Record.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(60))
                Console.WriteLine($"  {s.Bank}@{s.Record.Name} -> {s.Record.Anm2Name}");
            return 0;
        }
        var seqs = anims.Sequences.Where(s => !s.Record.IsPlaceholder).ToList();
        int step = limit >= seqs.Count ? 1 : Math.Max(1, seqs.Count / Math.Max(1, limit));
        var sample = seqs.Where((_, i) => i % step == 0).ToList();
        var tracksOf = new Dictionary<string, uint[]?>(StringComparer.OrdinalIgnoreCase);
        int noClip = 0, pose = 0, bad = 0, n = 0;
        int oldFail = 0, oldFewer = 0, newFail = 0, newFewer = 0, changed = 0, objectLike = 0, noRigMesh = 0;
        var examples = new List<string>();
        var fewer = new List<string>();
        int noRigNotObject = 0, oldCrossed = 0, newCrossed = 0;
        var crossedEx = new List<string>();
        var altFewer = new int[Windows.Length];
        var altCrossed = new int[Windows.Length];
        Console.WriteLine($"legacy fallback player_kc_basic_tpp.model: {(models.Find("player_kc_basic_tpp.model") is null ? "absent" : "present")}");
        var objSw = Stopwatch.StartNew();
        var objectIndex = AnimObjects.Build(catalog);
        Console.WriteLine($"object index: {objectIndex.Meshes:N0} meshes in {objSw.Elapsed.TotalSeconds:F1} s");
        int objMesh = 0, objSkinned = 0, objRigid = 0;
        var objects = new List<string>();
        sw.Restart();
        foreach (var s in sample)
        {
            string clip = s.Record.Anm2Name;
            if (!tracksOf.TryGetValue(clip, out var tracks))
            {
                tracks = null;
                if (AnimCatalog.Clip(catalog, clip) is { } w)
                {
                    try
                    {
                        var h = Anm2Header.Parse(Anm2Resource.Read(w.Pack, w.Index));
                        tracks = h.IsPoseWeights ? [] : h.TrackHashes;
                    }
                    catch (Exception e) when (e is Anm2FormatException or Anm2UnsupportedException or RpackFormatException) { tracks = [uint.MaxValue]; }
                }
                tracksOf[clip] = tracks;
            }
            if (tracks is null) { noClip++; continue; }
            if (tracks.Length == 0) { pose++; continue; }
            if (tracks is [uint.MaxValue]) { bad++; continue; }
            n++;
            int best = rigs.Rigs.Count == 0 ? 0 : rigs.Rigs.Max(r => r.Bound(tracks));
            var old = Legacy(rigs, tracks, s.Record.Name, s.Bank, null);
            var pick = rigs.Pick(tracks, s.Record.Name, s.Bank, null);
            int oldBound = old?.Bound(tracks) ?? 0, newBound = pick.Rig?.Bound(tracks) ?? 0;
            var nameWords = s.Record.Name.ToLowerInvariant().Split('_');
            bool tppName = nameWords.Contains("tpp"), fppName = nameWords.Contains("fpp");
            bool Crossed(AnimRig? r) => r is not null && (tppName && r.Skeleton.Contains("fpp", StringComparison.OrdinalIgnoreCase) ||
                                                          fppName && r.Skeleton.Contains("player", StringComparison.OrdinalIgnoreCase) && !r.Skeleton.Contains("fpp", StringComparison.OrdinalIgnoreCase));
            if (Crossed(old)) oldCrossed++;
            if (Crossed(pick.Rig))
            {
                newCrossed++;
                if (crossedEx.Count < show) crossedEx.Add($"  {s.Bank}@{s.Record.Name}: {pick.Describe()}; {string.Join(", ", rigs.Rigs.Where(r => r.Skeleton.Contains("player")).Select(r => $"{r.Skeleton} {r.Bound(tracks)}"))}");
            }
            if (old is null) oldFail++;
            else if (oldBound < best) oldFewer++;
            if (pick.Rig is null) newFail++;
            else if (newBound < best)
            {
                newFewer++;
                if (fewer.Count < show) fewer.Add($"  {s.Bank}@{s.Record.Name}: {pick.Rig.Skeleton} {newBound}, best {string.Join(", ", rigs.Rigs.Where(r => r.Bound(tracks) == best).Select(r => r.Skeleton))} {best} of {tracks.Length}");
            }
            if (pick.Rig is null && !pick.Object) noRigNotObject++;
            for (int v = 0; v < Windows.Length; v++)
            {
                var alt = rigs.Pick(tracks, s.Record.Name, s.Bank, null, Windows[v]);
                if (alt.Rig is not null && alt.Bound < best) altFewer[v]++;
                if (Crossed(alt.Rig)) altCrossed[v]++;
            }
            if (!string.Equals(old?.Skeleton, pick.Rig?.Skeleton, StringComparison.OrdinalIgnoreCase))
            {
                changed++;
                if (examples.Count < show)
                    examples.Add($"  {s.Bank}@{s.Record.Name}: {old?.Skeleton ?? "-"} {oldBound} -> {pick.Rig?.Skeleton ?? "-"} {newBound} of {tracks.Length}");
            }
            if (pick.Object)
            {
                objectLike++;
                string where = "no mesh";
                if (objectIndex.Best(tracks, clip, catalog) is { } hit && hit.Bound * 2 >= pick.Named)
                {
                    objMesh++;
                    if (pick.Rig is null) noRigMesh++;
                    var (pe, pi) = catalog.Split(hit.Gid);
                    try
                    {
                        var m = Nightrunner.Core.Mesh.MeshDecoder.Decode(pe.Pack!, pi);
                        if (m.Skinned) objSkinned++; else objRigid++;
                        where = $"{catalog.Name(hit.Gid)} {hit.Bound}/{tracks.Length} ({(m.Skinned ? "skinned" : "rigid")}, {m.Entities.Length} entities)";
                    }
                    catch (Exception e) { where = $"{catalog.Name(hit.Gid)}: {e.Message}"; }
                }
                if (objects.Count < show || where == "no mesh" && objects.Count < show * 2) objects.Add($"  {s.Bank}@{s.Record.Name} ({clip}): {pick.Describe()}; {where}");
            }
        }
        Console.WriteLine($"sampled {sample.Count:N0} sequences (every {step}), {tracksOf.Count:N0} clips read in {sw.Elapsed.TotalSeconds:F1} s: " +
                          $"{n:N0} bone clips, {pose:N0} pose-weight, {noClip:N0} clip not shipped, {bad:N0} unreadable");
        Console.WriteLine($"legacy: {oldFail:N0} no rig, {oldFewer:N0} on a rig binding fewer tracks than the best");
        Console.WriteLine($"now:    {newFail:N0} no rig, {newFewer:N0} on a rig binding fewer tracks than the best");
        Console.WriteLine($"tpp-named clips on an fpp rig or fpp-named on a tpp player rig: legacy {oldCrossed:N0}, now {newCrossed:N0}");
        foreach (var e in crossedEx) Console.WriteLine(e);
        foreach (var e in fewer) Console.WriteLine(e);
        Console.WriteLine($"no rig and not an object clip (only root/attach/camera tracks): {noRigNotObject:N0}");
        for (int v = 0; v < Windows.Length; v++)
            Console.WriteLine($"  tie window {Windows[v]:P0} (at least {AnimRigs.Slack}): {altFewer[v]:N0} fewer than the best, {altCrossed[v]:N0} tpp/fpp crossed");
        Console.WriteLine($"{changed:N0} picks changed");
        foreach (var e in examples) Console.WriteLine(e);
        Console.WriteLine($"object clips (under a quarter of their tracks bind a character rig): {objectLike:N0}; a mesh binds half or more: {objMesh:N0} ({objSkinned:N0} skinned, {objRigid:N0} rigid); of the {newFail:N0} with no rig, {noRigMesh:N0} play on a mesh");
        foreach (var o in objects) Console.WriteLine(o);
        return 0;
    }

    static readonly double[] Windows = [0, 0.05, 0.1, 0.15];

    /// <summary>The earlier name-word pick, kept here to measure against.</summary>
    static AnimRig? Legacy(AnimRigs rigs, uint[] tracks, string sequence, string bank, string? current)
    {
        var scored = rigs.Rigs.Select(r => (r, n: r.Bound(tracks))).Where(x => x.n > 0).ToList();
        if (scored.Count == 0) return null;
        int best = scored.Max(x => x.n);
        var words = Words(sequence).Concat(Words(bank)).ToHashSet();
        return scored.Where(x => x.n >= best * 0.8)
            .OrderByDescending(x => Words(x.r.Skeleton).Count(words.Contains))
            .ThenByDescending(x => string.Equals(x.r.Skeleton, current, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.n)
            .ThenByDescending(x => x.r.Models.Count)
            .First().r;
    }

    static readonly HashSet<string> Noise = ["skeleton", "msh", "model", "phx", "sh2", "dlc", "ft", "a", "b", "c", "m", "npc"];

    static IEnumerable<string> Words(string name) =>
        name.ToLowerInvariant().Split(['_', '.', '/', '\\', ' '], StringSplitOptions.RemoveEmptyEntries).Where(w => !Noise.Contains(w) && !char.IsDigit(w[0]));
}
