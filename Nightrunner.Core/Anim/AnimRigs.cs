using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Core.Anim;

/// <summary>
/// A skeleton some <c>.model</c> names (<c>preset.skeletonName</c>), its bone hashes, and those models. The
/// <c>*_normal_mask</c> helper bones are left out: TPP player clips drive the 12 the FPP rig has (DLTB
/// <c>tpp_balls_runbackward</c> binds 78 tracks on the TPP rig, 90 on the FPP one), so counting them would send every
/// TPP clip to the FPP arms.
/// </summary>
public sealed record AnimRig(string Skeleton, HashSet<uint> Bones, IReadOnlyList<string> Models)
{
    public int Bound(uint[] tracks) => tracks.Count(Bones.Contains);
}

/// <summary>
/// The rig a clip plays on (<see cref="AnimRigs.Pick"/>): the rig binding the most tracks, how many it binds, and
/// whether the clip looks like an object's (most of its bone tracks bind no rig).
/// </summary>
public sealed record RigPick(AnimRig? Rig, int Bound, int Tracks, int Named)
{
    /// <summary>
    /// Fewer than a quarter of the clip's tracks (root, attach and camera tracks aside) bind the best rig: an object's
    /// clip (a gate, a chest, a cable), not a character's. Character clips bind 38% or more even on DL2's rigs, whose
    /// skeleton meshes lack the bones the model's parts add.
    /// </summary>
    public bool Object => Named > 0 && Bound * 4 < Named;

    public string Describe() => Rig is null
        ? $"no rig binds any of its {Tracks} tracks"
        : $"{Rig.Skeleton} binds {Bound}/{Tracks} tracks";
}

/// <summary>
/// Which model to play a clip on. A clip names no rig, so the choice is the viewer's, and the clip's own track hashes
/// decide: the rig binding the most tracks wins. Name words (<c>fpp</c>, <c>tpp</c>, <c>biter</c>...; the sequence's
/// before the bank's) only break a tie among rigs binding within <see cref="Near"/> of the best: that separates FPP
/// clips from TPP ones (once the FPP rig's hand-mask bones are left out, see <see cref="AnimRig"/>) and a biter's clip
/// from the player rig binding one bone more. Then the current rig, the rig more models use, and the rig with the
/// fewest bones the clip leaves still. The census is <c>tools/AnimCheck --rigs</c>.
/// </summary>
public sealed class AnimRigs
{
    /// <summary>A rig binding within this share of the best rig's count (at least <see cref="Slack"/> tracks) ties with it.</summary>
    public const double Near = 0.1;
    public const int Slack = 1;

    public IReadOnlyList<AnimRig> Rigs { get; }
    /// <summary>Skeletons a model names that no loaded pack provides.</summary>
    public IReadOnlyList<string> Missing { get; }

    private AnimRigs(List<AnimRig> rigs, List<string> missing)
    {
        Rigs = rigs;
        Missing = missing;
    }

    /// <summary>Rigs given directly (tests).</summary>
    public static AnimRigs Of(params AnimRig[] rigs) => new([.. rigs], []);

    public static AnimRigs Build(RpackCatalog catalog, ModelCatalog models)
    {
        var bySkel = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in models.Models.Where(m => m.Wins))
        {
            string? s;
            try { s = models.Load(m).Skeleton; }
            catch (ModelFormatException) { continue; }
            if (s is not { Length: > 0 }) continue;
            if (!bySkel.TryGetValue(s, out var list)) bySkel[s] = list = [];
            list.Add(m.Basename);
        }
        var rigs = new List<AnimRig>();
        var missing = new List<string>();
        foreach (var (skel, list) in bySkel.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            string key = skel.EndsWith(".msh", StringComparison.OrdinalIgnoreCase) ? skel[..^4] : skel;
            var hits = catalog.Lookup(key, 0x10);
            if (hits.Length == 0) { missing.Add(skel); continue; }
            var (e, i) = catalog.Split(hits[0]);
            try
            {
                var bones = ModelSkeleton.FromMesh(MeshDecoder.Decode(e.Pack!, i), key).Names
                    .Where(n => !n.EndsWith("_normal_mask", StringComparison.OrdinalIgnoreCase)).Select(Anm2Hash.H41).ToHashSet();
                rigs.Add(new AnimRig(skel, bones, list.Order(StringComparer.Ordinal).ToList()));
            }
            catch (Exception ex) when (ex is MeshFormatException or RpackFormatException)
            {
                missing.Add(skel);
            }
        }
        return new AnimRigs(rigs, missing);
    }

    /// <summary>The rig to play <paramref name="tracks"/> on; null when no rig binds any track.</summary>
    public AnimRig? Best(uint[] tracks, string sequence, string bank, string? currentSkeleton) =>
        Pick(tracks, sequence, bank, currentSkeleton).Rig;

    /// <summary>The rig binding the most of <paramref name="tracks"/>, ties broken as the class summary says.</summary>
    public RigPick Pick(uint[] tracks, string sequence, string bank, string? currentSkeleton, double near = Near)
    {
        int named = tracks.Count(t => !NotBones.Contains(t));
        var scored = Rigs.Select(r => (r, n: r.Bound(tracks))).Where(x => x.n > 0).ToList();
        if (scored.Count == 0) return new RigPick(null, 0, tracks.Length, named);
        int best = scored.Max(x => x.n);
        int floor = Floor(best, near);
        var seqWords = Words(sequence).ToHashSet();
        var bankWords = Words(bank).ToHashSet();
        var pick = scored.Where(x => x.n >= floor)
            .OrderByDescending(x => RigWords(x.r).Count(seqWords.Contains))
            .ThenByDescending(x => RigWords(x.r).Count(bankWords.Contains))
            .ThenByDescending(x => x.n)
            .ThenByDescending(x => string.Equals(x.r.Skeleton, currentSkeleton, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.r.Models.Count)
            .ThenBy(x => x.r.Bones.Count - x.n)
            .ThenBy(x => x.r.Skeleton, StringComparer.Ordinal)
            .First();
        return new RigPick(pick.r, pick.n, tracks.Length, named);
    }

    /// <summary>Whether a skeleton binding <paramref name="bound"/> tracks ties with a rig binding <paramref name="best"/>.</summary>
    public static bool Ties(int bound, int best) => bound >= Floor(best, Near);

    /// <summary>
    /// Whether a shown skeleton binding <paramref name="shownBound"/> of a clip's tracks plays it poorly (the viewer's
    /// Follow rig off keeps the shown model and says so): under half of its bone tracks, or clearly fewer than the
    /// character rig <paramref name="pick"/> found binds.
    /// </summary>
    public static bool PoorFit(int shownBound, RigPick pick) =>
        shownBound < Math.Max(1, (pick.Named + 1) / 2) || (pick.Rig is not null && !pick.Object && !Ties(shownBound, pick.Bound));

    private static int Floor(int best, double near) => best - Math.Max(Slack, (int)(best * near));

    /// <summary>Root-motion, attach, camera and the unnamed identity track: not bones a rig is expected to have.</summary>
    private static readonly HashSet<uint> NotBones =
        [.. Anm2Hash.SpecialTracks.Select(Anm2Hash.H41), Anm2Hash.UnknownIdentityTrack];

    /// <summary>
    /// A model of <paramref name="rig"/> to show: the current model with <c>tpp</c>/<c>fpp</c> swapped when the rig has
    /// it, else the one sharing the most name words with the rig and sequence (rig-preview, test and invalid models
    /// last), shortest name first.
    /// </summary>
    public static string Model(AnimRig rig, string? currentModel, string sequence)
    {
        if (currentModel is { } cur)
        {
            if (rig.Models.FirstOrDefault(m => m.Equals(cur, StringComparison.OrdinalIgnoreCase)) is { } same) return same;
            foreach (var (a, b) in new[] { ("_tpp", "_fpp"), ("_fpp", "_tpp") })
            {
                string swapped = cur.Replace(a, b, StringComparison.OrdinalIgnoreCase);
                if (swapped != cur && rig.Models.FirstOrDefault(m => m.Equals(swapped, StringComparison.OrdinalIgnoreCase)) is { } hit) return hit;
            }
        }
        var words = Words(rig.Skeleton).Concat(Words(sequence)).ToHashSet();
        return rig.Models
            .OrderBy(m => m.StartsWith('_') || m.Contains("skeleton") || m.Contains("todelete") || m.Contains("invalid") || m.StartsWith("test_"))
            .ThenByDescending(m => Words(m).Count(words.Contains))
            .ThenBy(m => m.Length)
            .ThenBy(m => m, StringComparer.Ordinal)
            .First();
    }

    // light / medium / heavy name hit strengths in clips and body builds in skeletons; anims / all come from bank names
    /// <summary>The rig's name words; a player rig not named <c>fpp</c> is the <c>tpp</c> one (DL2's <c>player_skeleton</c>).</summary>
    private static IEnumerable<string> RigWords(AnimRig rig)
    {
        var words = Words(rig.Skeleton).ToList();
        if (words.Contains("player") && !words.Contains("fpp") && !words.Contains("tpp")) words.Add("tpp");
        return words;
    }

    private static readonly HashSet<string> Noise = ["skeleton", "msh", "model", "phx", "sh2", "dlc", "ft", "a", "b", "c", "m", "npc",
                                                     "light", "medium", "heavy", "anims", "all"];

    private static IEnumerable<string> Words(string name) =>
        name.ToLowerInvariant().Split(['_', '.', '/', '\\', ' '], StringSplitOptions.RemoveEmptyEntries).Where(w => !Noise.Contains(w) && !char.IsDigit(w[0]));
}
