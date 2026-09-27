using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using Nightrunner.Core.Anim;
using Nightrunner.Core.Games;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Rpack;

// ANM2 census over one install (DLTB by default): every type-0x40 resource (plain 0x40 part, or the 0x44 + 0x45 stream
// pair) must parse, decode with its payload consumed exactly, re-serialise byte for byte, and re-encode from its values
// with the shipped recipe (with and without reusing the source quantisation). Plain and stream copies of the same name
// must be the same bytes. Then: the old header versions (V1/V0) against skeleton bind locals and each other, frame-0
// bone locals of player idles against the player skeleton, a fresh-encode error sample, and decode timings.
//
// Usage: dotnet run -c Release -- [--game dltb|dl2] [--against dltb|dl2] [--pack NAME] [--limit N] [--fresh N] [--show N]
//        --rigs: the viewer's rig pick for every sequence (RigCensus.cs); --limit N samples every n-th

string game = Arg("--game") ?? "dltb";
string? onlyPack = Arg("--pack");
int limit = int.TryParse(Arg("--limit"), out int lim) ? lim : int.MaxValue;
int freshN = int.TryParse(Arg("--fresh"), out int fr) ? fr : 400;
int show = int.TryParse(Arg("--show"), out int sh) ? sh : 12;

var install = GameInstall.FindInstalls().GetValueOrDefault(game);
if (install is null)
{
    Console.WriteLine($"skip: no {game} install found (set NIGHTRUNNER_GAME_ROOT)");
    return 0;
}
var packs = install.Rpacks(includeCustom: true);
Console.WriteLine($"{install}\n{packs.Length} packs");

if (Arg("--diff") is { } diffName) return DiffOne(diffName);
if (args.Contains("--rigs")) return RigCensus.Run(install, packs, limit, show);

var results = new ConcurrentBag<Result>();
var hashes = new ConcurrentDictionary<(string Pack, string Name), (byte[] Sha, bool Pair)>();
var sw = Stopwatch.StartNew();
foreach (var path in packs)
{
    string label = Path.GetRelativePath(install.Assets!, path);
    if (onlyPack is not null && !label.Contains(onlyPack, StringComparison.OrdinalIgnoreCase)) continue;
    using var pack = RpackFile.Open(path);
    var idx = Enumerable.Range(0, pack.Count).Where(i => pack.Logicals[i].Type == Anm2Resource.TypeAnimation).Take(limit).ToArray();
    if (idx.Length == 0) continue;
    Parallel.ForEach(idx, i => results.Add(Check(pack, label, i)));
    Console.WriteLine($"  {label}: {idx.Length:N0} clips");
}
Console.WriteLine($"census {results.Count:N0} clips in {sw.Elapsed.TotalSeconds:F1} s\n");

var all = results.ToList();
Report(all);
StreamPairs();
OldVersions();
Physical();
Fresh();
Timing();
return 0;

// ---- per clip ----------------------------------------------------------------------------------------------

Result Check(RpackFile pack, string label, int i)
{
    string name = pack.Name(i);
    var r = new Result { Pack = label, Name = name };
    byte[] bytes;
    try { bytes = Anm2Resource.Read(pack, i, out r.Pair); }
    catch (Exception e) { r.Error = "read: " + e.Message; return r; }
    hashes[(label, name)] = (SHA256.HashData(bytes), r.Pair);
    r.Version = Anm2Header.PeekVersion(bytes);
    r.Bytes = bytes.Length;
    try
    {
        var t0 = Stopwatch.GetTimestamp();
        var payload = Anm2Payload.Decode(bytes);
        var clip = Anm2Clip.FromPayload(payload);
        r.DecodeMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        r.Decoded = true;
        r.Tracks = clip.TrackCount;
        r.Frames = clip.FrameBound;
        r.Segments = payload.Segments.Length;
        r.Pose = clip.IsPoseWeights;
        r.Poses = clip.PoseHashes.Length;
        r.IdentityVfr = payload.Header.IdentityVfr;
        r.TimeEqFrame = payload.Header.TimeBound == payload.Header.FrameBound;
        int F = 0;
        var seams = new HashSet<int>();
        foreach (var s in payload.Segments) { F += s.Frames; seams.Add(F); }
        r.Row15Quirks = clip.EndCopies.Keys.Count(f => !seams.Contains(f));
        var cutSet = Anm2Encoder.Cuts(clip);
        r.Row15Cuts = cutSet.Count(f => !seams.Contains(f));
        r.SeamCuts = cutSet.Count(seams.Contains);
        r.SeamCopies = clip.EndCopies.Keys.Count(seams.Contains);
        r.PadRows = clip.PadRows.Count;
        if (!clip.IsPoseWeights) r.SeamJump = clip.MaxSeamJump();
        r.NonFinal15 = payload.Segments.Take(payload.Segments.Length - 1).All(s => s.Frames % 15 == 0);
        r.MaxAbsValue = clip.Values.Max(Math.Abs);
        r.MaxQuatErr = 0;
        if (!clip.IsPoseWeights)
            for (int k = 0; k < clip.KeyCount; k++)
                for (int t = 0; t < clip.TrackCount; t++)
                    r.MaxQuatErr = Math.Max(r.MaxQuatErr, Math.Abs(clip.Rotation(k, t).Length() - 1));
        r.Serialise = payload.Encode().AsSpan().SequenceEqual(bytes);
        var t1 = Stopwatch.GetTimestamp();
        var enc = Anm2Encoder.Encode(clip);
        r.EncodeMs = Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
        r.Recipe = enc.AsSpan().SequenceEqual(bytes);
        var gridless = Anm2Encoder.Quantize(clip, new Anm2EncodeOptions { ReuseSourceGrid = false });
        r.GridRecipe = gridless.Encode().AsSpan().SequenceEqual(bytes);
        if (!r.GridRecipe) r.GridWhy = Diff(payload, gridless);
        GridStats(payload, gridless, r);
        var greedy = Anm2Encoder.Quantize(clip, new Anm2EncodeOptions { ReuseSourceLayout = false });
        r.GreedyRecipe = greedy.Encode().AsSpan().SequenceEqual(bytes);
        if (!r.GreedyRecipe) r.GreedyWhy = Diff(payload, greedy);
        var pure = Anm2Encoder.Quantize(clip, Anm2EncodeOptions.Recipe);
        r.PureRecipe = pure.Encode().AsSpan().SequenceEqual(bytes);
        if (!r.PureRecipe) r.PureWhy = Diff(payload, pure);
        if (!r.Recipe) r.RecipeWhy = Diff(payload, Anm2Encoder.Quantize(clip));
        if (r.Version == 3)
        {
            var as3 = Anm2Encoder.Encode(clip, new Anm2EncodeOptions { Version = 3 });
            r.V3Exact = as3.AsSpan().SequenceEqual(bytes);
        }
    }
    catch (Anm2UnsupportedException e) { r.Refused = e.Message; }
    catch (Exception e) { r.Error = e.GetType().Name + ": " + e.Message; }
    return r;
}

// Stream-segments whose recipe bias/scale equal the shipped ones (same layout and split, so they line up).
static void GridStats(Anm2Payload a, Anm2Payload b, Result r)
{
    if (a.Segments.Length != b.Segments.Length) return;
    for (int s = 0; s < a.Segments.Length; s++)
    {
        var x = a.Segments[s];
        var y = b.Segments[s];
        if (x.NumAnimated != y.NumAnimated) return;
        for (int i = 0; i < x.NumAnimated; i++)
        {
            r.GridStreams++;
            if (BitConverter.SingleToInt32Bits(x.Bias[i]) == BitConverter.SingleToInt32Bits(y.Bias[i]) &&
                BitConverter.SingleToInt32Bits(x.Scale[i]) == BitConverter.SingleToInt32Bits(y.Scale[i])) r.GridSame++;
        }
    }
}

static string Diff(Anm2Payload a, Anm2Payload b)
{
    if (!a.Header.SegmentFrames.AsSpan().SequenceEqual(b.Header.SegmentFrames))
        return $"segments [{string.Join(",", a.Header.SegmentFrames)}] vs [{string.Join(",", b.Header.SegmentFrames)}]";
    for (int s = 0; s < a.Segments.Length; s++)
    {
        var x = a.Segments[s];
        var y = b.Segments[s];
        if (!x.Flags.AsSpan().SequenceEqual(y.Flags)) return $"seg {s}: flags";
        if (!x.Constants.AsSpan().SequenceEqual(y.Constants)) return $"seg {s}: constants";
        int bias = 0, scale = 0, codes = 0;
        for (int i = 0; i < x.NumAnimated; i++)
        {
            if (BitConverter.SingleToInt32Bits(x.Bias[i]) != BitConverter.SingleToInt32Bits(y.Bias[i])) bias++;
            if (BitConverter.SingleToInt32Bits(x.Scale[i]) != BitConverter.SingleToInt32Bits(y.Scale[i])) scale++;
        }
        for (int i = 0; i < x.Codes.Length; i++) if (x.Codes[i] != y.Codes[i]) codes++;
        if (bias + scale + codes > 0) return $"seg {s}: {bias} bias, {scale} scale, {codes} codes differ";
    }
    return "header or bit layout";
}

// ---- reports -----------------------------------------------------------------------------------------------

void Report(List<Result> rs)
{
    Console.WriteLine("pack                                   clips  pair   V3     V2   V1   V0  decoded  serialise  exact  no-grid  greedy  pure    pose");
    foreach (var g in rs.GroupBy(x => x.Pack).OrderBy(g => g.Key))
        Row(g.Key, g.ToList());
    Row("TOTAL", rs);
    Console.WriteLine();

    var dec = rs.Where(x => x.Decoded).ToList();
    Console.WriteLine($"max tracks {dec.Max(x => x.Tracks)}, max frameBound {dec.Max(x => x.Frames)}, max segments {dec.Max(x => x.Segments)}, " +
                      $"max bytes {dec.Max(x => x.Bytes):N0}, max |value| {dec.Max(x => x.MaxAbsValue):G5}");
    Console.WriteLine($"max pose count {dec.Max(x => x.Poses)}; pose-weight clips {dec.Count(x => x.Pose):N0}; single pose (frameBound 1) {dec.Count(x => x.Frames == 1):N0}");
    Console.WriteLine($"identity VFR {dec.Count(x => x.IdentityVfr):N0}/{dec.Count:N0}; timeBound == frameBound {dec.Count(x => x.TimeEqFrame):N0}");
    Console.WriteLine($"non-final segments all multiples of 15 frames: {dec.Count(x => x.Segments > 1 && x.NonFinal15):N0} of {dec.Count(x => x.Segments > 1):N0} multi-segment clips");
    Console.WriteLine($"seam copies {dec.Sum(x => x.SeamCopies):N0} ({dec.Sum(x => x.SeamCuts):N0} of them cuts > {Anm2Encoder.CutThreshold} in {dec.Count(x => x.SeamCuts > 0):N0} clips); " +
                      $"row-15 copies that differ {dec.Sum(x => x.Row15Quirks):N0} in {dec.Count(x => x.Row15Quirks > 0)} clips ({dec.Sum(x => x.Row15Cuts)} of them cuts); pad rows that differ {dec.Sum(x => x.PadRows)}");
    var unique = dec.GroupBy(x => x.Name).Select(g => g.First()).ToList();
    Console.WriteLine($"unique names: {rs.Select(x => x.Name).Distinct().Count():N0} ({unique.Count:N0} decoded, {rs.Where(x => x.Refused is not null).Select(x => x.Name).Distinct().Count()} refused); " +
                      $"pose-weight {unique.Count(x => x.Pose):N0}; row-15 clips {unique.Count(x => x.Row15Quirks > 0)}; hard-cut clips {unique.Count(x => x.SeamJump > 0.01)}; " +
                      $"exact {unique.Count(x => x.Recipe):N0}, no-grid {unique.Count(x => x.GridRecipe):N0}, greedy {unique.Count(x => x.GreedyRecipe):N0}, pure {unique.Count(x => x.PureRecipe):N0}");
    var cuts = dec.Where(x => x.SeamJump > 0.01).OrderByDescending(x => x.SeamJump).ToList();
    Console.WriteLine($"hard cuts (seam translation jump > 1 cm): {cuts.Count} clips, max {(cuts.Count > 0 ? cuts[0].SeamJump : 0):F3} m {(cuts.Count > 0 ? cuts[0].Name : "")}");
    Console.WriteLine($"max ||q| − 1| over all decoded TRS keys: {dec.Where(x => !x.Pose).Max(x => x.MaxQuatErr):E2}");
    foreach (var x in rs.Where(x => x.Refused is not null).Take(show)) Console.WriteLine($"  refused {x.Pack} {x.Name}: {x.Refused}");
    foreach (var x in rs.Where(x => x.Error is not null).Take(show)) Console.WriteLine($"  ERROR {x.Pack} {x.Name}: {x.Error}");
    foreach (var x in dec.Where(x => !x.Serialise).Take(show)) Console.WriteLine($"  serialise differs {x.Pack} {x.Name}");
    foreach (var x in dec.Where(x => !x.Recipe).Take(show)) Console.WriteLine($"  recipe differs {x.Pack} {x.Name} v{x.Version}: {x.RecipeWhy}");
    Console.WriteLine($"recipe bias/scale == shipped for {dec.Sum(x => x.GridSame):N0} of {dec.Sum(x => x.GridStreams):N0} animated stream-segments ({100.0 * dec.Sum(x => x.GridSame) / Math.Max(1, dec.Sum(x => x.GridStreams)):F3} %)");
    foreach (var g in dec.Where(x => !x.GridRecipe).GroupBy(x => Kind(x.GridWhy)).OrderByDescending(g => g.Count()))
        Console.WriteLine($"  no-grid misses, {g.Key}: {g.Count():N0} (e.g. {g.First().Name}: {g.First().GridWhy})");
    foreach (var g in dec.Where(x => !x.GreedyRecipe).GroupBy(x => Kind(x.GreedyWhy)).OrderByDescending(g => g.Count()))
        Console.WriteLine($"  greedy-layout misses, {g.Key}: {g.Count():N0} (e.g. {g.First().Name}: {g.First().GreedyWhy})");
    foreach (var x in dec.Where(x => !x.GreedyRecipe).Take(show)) Console.WriteLine($"    {x.Name}: {x.GreedyWhy}");
    var why = dec.Where(x => !x.PureRecipe).GroupBy(x => Kind(x.PureWhy)).OrderByDescending(g => g.Count());
    foreach (var g in why) Console.WriteLine($"  pure recipe misses, {g.Key}: {g.Count():N0} (e.g. {g.First().Name}: {g.First().PureWhy})");
    Console.WriteLine($"V3 clips re-encoded as V3 bit-exact: {dec.Count(x => x.Version == 3 && x.V3Exact):N0}/{dec.Count(x => x.Version == 3):N0}");
    Console.WriteLine();
}

static string Kind(string? why) => why is null ? "?" : why.StartsWith("segments") ? "segmentation" :
    why.Contains("codes differ") && why.Contains(" 0 bias, 0 scale") ? "codes only" :
    why.Contains("bias") ? "bias/scale" : why;

void Row(string label, List<Result> g)
{
    var d = g.Where(x => x.Decoded).ToList();
    Console.WriteLine($"{label,-38} {g.Count,6:N0} {g.Count(x => x.Pair),5:N0} {g.Count(x => x.Version == 3),6:N0} {g.Count(x => x.Version == 2),4} {g.Count(x => x.Version == 1),4} {g.Count(x => x.Version == 0),4} " +
                      $"{d.Count,8:N0} {d.Count(x => x.Serialise),10:N0} {d.Count(x => x.Recipe),6:N0} {d.Count(x => x.GridRecipe),8:N0} {d.Count(x => x.GreedyRecipe),7:N0} {d.Count(x => x.PureRecipe),6:N0} {d.Count(x => x.Pose),7:N0}");
}

void StreamPairs()
{
    (string A, string B)[] pairs = [("common_anims_pc.rpack", "common_anims_stream_pc.rpack"), ("player_anims_static_pc.rpack", "player_anims_stream_pc.rpack")];
    foreach (var (a, b) in pairs)
    {
        var left = hashes.Where(kv => kv.Key.Pack == a).ToDictionary(kv => kv.Key.Name, kv => kv.Value);
        var right = hashes.Where(kv => kv.Key.Pack == b).ToDictionary(kv => kv.Key.Name, kv => kv.Value);
        if (left.Count == 0 || right.Count == 0) continue;
        int same = 0, diff = 0, missing = 0;
        foreach (var (n, h) in left)
        {
            if (!right.TryGetValue(n, out var o)) { missing++; continue; }
            if (h.Sha.AsSpan().SequenceEqual(o.Sha)) same++; else diff++;
        }
        Console.WriteLine($"stream pair {a} ({left.Count:N0}, pair form {left.Values.Count(v => v.Pair)}) vs {b} ({right.Count:N0}, pair form {right.Values.Count(v => v.Pair):N0}): " +
                          $"identical {same:N0}, different {diff}, no twin {missing}");
    }
    Console.WriteLine();
}

// V1 and V0 clips: per version, decode / re-serialise / recipe counts; frame-0 translations against the skeleton mesh
// whose bind locals they match best; and the two encodings of one clip (a V1 clip and its V0 "_demo" twin) against each other.
void OldVersions()
{
    var old = all.Where(x => x.Version is 0 or 1).ToList();
    if (old.Count == 0) { Console.WriteLine("V1/V0: none\n"); return; }
    foreach (var g in old.GroupBy(x => x.Version).OrderByDescending(g => g.Key))
    {
        var d = g.Where(x => x.Decoded).ToList();
        Console.WriteLine($"V{g.Key}: {g.Count()} copies of {g.Select(x => x.Name).Distinct().Count()} clips; decoded {d.Count}, re-serialise exact {d.Count(x => x.Serialise)}, " +
                          $"recipe exact {d.Count(x => x.Recipe)}, plain recipe exact {d.Count(x => x.PureRecipe)}; frames {d.Min(x => x.Frames)}..{d.Max(x => x.Frames)}, tracks {d.Min(x => x.Tracks)}..{d.Max(x => x.Tracks)}");
    }
    var skels = new List<(string Name, MeshModel M, Dictionary<uint, int> ByHash)>();
    var seenSkel = new HashSet<string>();
    foreach (var path in packs)
    {
        using var pack = RpackFile.Open(path);
        for (int i = 0; i < pack.Count; i++)
        {
            if (pack.Logicals[i].Type != 0x10) continue;
            string n = pack.Name(i);
            if (!n.Contains("skeleton") || !seenSkel.Add(n)) continue;
            try
            {
                var m = MeshDecoder.Decode(pack, i);
                var byHash = new Dictionary<uint, int>();
                for (int e = 0; e < m.Entities.Length; e++) byHash.TryAdd(Anm2Hash.H41(m.Entities[e].NameStr), e);
                skels.Add((n, m, byHash));
            }
            catch (Exception e) when (e is MeshFormatException or MeshUnsupportedException or RpackFormatException) { }
        }
    }
    var clips = new Dictionary<string, Anm2Clip>();
    foreach (var r in old.Where(x => x.Decoded).GroupBy(x => x.Name).Select(g => g.First()))
    {
        var path = packs.First(p => Path.GetRelativePath(install.Assets!, p) == r.Pack);
        using var pack = RpackFile.Open(path);
        int i = pack.IndicesOf(RpackFile.EngineFold(System.Text.Encoding.Latin1.GetBytes(r.Name))).First(ix => pack.Logicals[ix].Type == 0x40);
        clips[r.Name] = Anm2Resource.Decode(pack, i);
    }
    Console.WriteLine($"  frame-0 translations vs bind locals of the best-matching of {skels.Count} skeleton meshes (bound = tracks the skeleton has):");
    int matched = 0;
    foreach (var (n, c) in clips.OrderBy(kv => kv.Value.Version).ThenBy(kv => kv.Key))
    {
        double Off(MeshModel m, int bone, int t)
        {
            var L = m.Entities[bone].Local;
            var tr = c.Translation(0, t);
            return Math.Max(Math.Abs(tr.X - L[3]), Math.Max(Math.Abs(tr.Y - L[7]), Math.Abs(tr.Z - L[11])));
        }
        var scored = skels.Select(sk =>
        {
            var offs = new List<double>();
            for (int t = 0; t < c.TrackCount; t++) if (sk.ByHash.TryGetValue(c.TrackHashes[t], out int b)) offs.Add(Off(sk.M, b, t));
            offs.Sort();
            return (sk.Name, Offs: offs, Close: offs.Count(x => x <= 1e-5));
        }).OrderByDescending(x => x.Close).ThenByDescending(x => x.Offs.Count).First();
        bool atBind = scored.Offs.Count > 0 && scored.Offs[scored.Offs.Count / 2] <= 1e-5;
        if (atBind) matched++;
        Console.WriteLine($"    V{c.Version} {n} (T {c.TrackCount}, F {c.FrameBound}): {scored.Name} bound {scored.Offs.Count}, ≤ 1e-5 m {scored.Close}, ≤ 1 mm {scored.Offs.Count(x => x <= 1e-3)}, " +
                          $"median {(scored.Offs.Count > 0 ? scored.Offs[scored.Offs.Count / 2] : double.NaN):E1} m");
    }
    Console.WriteLine($"  {matched}/{clips.Count} clips have a skeleton mesh whose bind translations their frame 0 matches (median ≤ 1e-5 m)");
    foreach (var (n, c1) in clips.Where(kv => kv.Value.Version == 1))
        if (clips.TryGetValue(n + "_demo", out var c2) && c2.FrameBound == c1.FrameBound)
            Console.WriteLine($"  V1 {n} vs V0 {n}_demo: {Compare(c1, c2)}");

    // --against GAME: the same names in another install (DLTB ships V3 re-encodes of most DL2 V1/V0 clips)
    if (Arg("--against") is { } otherId && GameInstall.FindInstalls().GetValueOrDefault(otherId) is { } other)
    {
        var otherPacks = other.Rpacks(includeCustom: true).Where(p => !Path.GetFileName(p).Contains("stream")).ToArray();
        int identical = 0, equal = 0, close = 0, differ = 0, missing = 0;
        foreach (var (n, c1) in clips.OrderBy(kv => kv.Key))
        {
            byte[]? ob = null;
            foreach (var path in otherPacks)
            {
                using var pack = RpackFile.Open(path);
                int i = pack.IndicesOf(RpackFile.EngineFold(System.Text.Encoding.Latin1.GetBytes(n))).FirstOrDefault(ix => pack.Logicals[ix].Type == 0x40, -1);
                if (i >= 0) { ob = Anm2Resource.Read(pack, i); break; }
            }
            if (ob is null) { missing++; continue; }
            var c2 = Anm2Clip.Decode(ob);
            if (c2.Version == c1.Version && Anm2Encoder.Encode(c1).AsSpan().SequenceEqual(ob)) { identical++; continue; }
            var (dv, deg, common) = ValueDiff(c1, c2);
            if (dv <= 1e-6 && deg <= 1e-3) equal++; else if (dv <= 1e-3 && deg <= 0.1) close++; else differ++;
            Console.WriteLine($"  V{c1.Version} {n} vs {otherId} V{c2.Version} (T {c1.TrackCount}/{c2.TrackCount}, F {c1.FrameBound}/{c2.FrameBound}): {Compare(c1, c2)}");
        }
        Console.WriteLine($"  against {otherId}: {identical} byte-identical, {equal} other-version copies with equal values (≤ 1e-6, ≤ 0.001°), " +
                          $"{close} within 1e-3 and 0.1°, {differ} differ, {missing} absent");
    }
    Console.WriteLine();
}

// Max translation/scale |Δ| and rotation angle (degrees, in double) over the tracks two clips share and the keys both
// have. Rotations are compared as rotations: near w = 0 the stereographic values of q and −q differ widely.
static (double MaxStream, double MaxDegrees, int Common) ValueDiff(Anm2Clip a, Anm2Clip b)
{
    double dv = 0, deg = 0;
    int common = 0;
    for (int t1 = 0; t1 < a.TrackCount; t1++)
    {
        int t2 = Array.IndexOf(b.TrackHashes, a.TrackHashes[t1]);
        if (t2 < 0) continue;
        common++;
        for (int k = 0; k < Math.Min(a.KeyCount, b.KeyCount); k++)
        {
            for (int j = 3; j < 9; j++) dv = Math.Max(dv, Math.Abs(a.Stream(k, t1, j) - b.Stream(k, t2, j)));
            deg = Math.Max(deg, Angle(Q(a, k, t1), Q(b, k, t2)));
        }
    }
    return (dv, deg, common);

    static (double, double, double, double) Q(Anm2Clip c, int k, int t)
    {
        double x = c.Stream(k, t, 0), y = c.Stream(k, t, 1), z = c.Stream(k, t, 2), n2 = x * x + y * y + z * z, inv = 1 / (1 + n2);
        return (2 * x * inv, 2 * y * inv, 2 * z * inv, (1 - n2) * inv);
    }
    static double Angle((double X, double Y, double Z, double W) p, (double X, double Y, double Z, double W) q)
    {
        double sg = p.X * q.X + p.Y * q.Y + p.Z * q.Z + p.W * q.W < 0 ? -1 : 1;
        double dx = p.X - sg * q.X, dy = p.Y - sg * q.Y, dz = p.Z - sg * q.Z, dw = p.W - sg * q.W;
        return 4 * Math.Asin(Math.Min(1, Math.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw) / 2)) * 180 / Math.PI;
    }
}

static string Compare(Anm2Clip a, Anm2Clip b)
{
    var (dv, deg, common) = ValueDiff(a, b);
    return $"{common} common tracks over {Math.Min(a.KeyCount, b.KeyCount)} keys: max translation/scale |Δ| {dv:E2}, max rotation {deg:E2}°";
}

void Physical()
{
    MeshModel? skel = null;
    foreach (var path in packs)
    {
        using var pack = RpackFile.Open(path);
        foreach (int i in pack.IndicesOf("sh2_player_tpp_phx_skeleton"u8))
            if (pack.Logicals[i].Type == 0x10) { skel = MeshDecoder.Decode(pack, i); break; }
        if (skel is not null) break;
    }
    if (skel is null) { Console.WriteLine("physical: sh2_player_tpp_phx_skeleton not found"); return; }
    var names = skel.Entities.Select(e => e.NameStr).ToArray();
    // idle clips of the player rig: most tracks bind to the skeleton
    var idles = new List<(string Pack, string Name, Anm2Clip Clip, Anm2Binding Bind)>();
    foreach (var path in packs.Where(p => Path.GetFileName(p).StartsWith("player_anims_pc")))
    {
        using var pack = RpackFile.Open(path);
        for (int i = 0; i < pack.Count && idles.Count < 400; i++)
        {
            if (pack.Logicals[i].Type != 0x40) continue;
            string n = pack.Name(i);
            if (!n.Contains("idle") || !n.Contains("tpp")) continue;
            var clip = Anm2Resource.Decode(pack, i);
            var bind = Anm2Binding.Bind(clip.TrackHashes, skel);
            if (bind.Bound >= 0.8 * clip.TrackCount) idles.Add((Path.GetFileName(path), n, clip, bind));
        }
    }
    Console.WriteLine($"physical: {names.Length} skeleton bones; {idles.Count} player TPP idle clips with ≥ 80 % of tracks bound");
    var dts = new List<double>();
    var offBones = new Dictionary<string, int>();
    double worstBone = 0;
    int clipsDone = 0;
    foreach (var (_, n, clip, bind) in idles)
    {
        clipsDone++;
        var perClip = new List<double>();
        for (int t = 0; t < clip.TrackCount; t++)
        {
            int b = bind.TrackToBone[t];
            if (b < 0) continue;
            var L = skel.Entities[b].Local;
            var tr = clip.Translation(0, t);
            double d = Math.Max(Math.Abs(tr.X - L[3]), Math.Max(Math.Abs(tr.Y - L[7]), Math.Abs(tr.Z - L[11])));
            perClip.Add(d);
            if (d > 1e-3) offBones[names[b]] = offBones.GetValueOrDefault(names[b]) + 1;
        }
        dts.AddRange(perClip);
        worstBone = Math.Max(worstBone, perClip.Count > 0 ? perClip.Max() : 0);
        if (clipsDone <= 4)
        {
            perClip.Sort();
            Console.WriteLine($"  {n}: {perClip.Count} bound, frame-0 translation vs bind: median {perClip[perClip.Count / 2]:E1} m, " +
                              $"≤ 1e-5 m {perClip.Count(x => x <= 1e-5)}, ≤ 1 mm {perClip.Count(x => x <= 1e-3)}, misses {string.Join(" ", bind.Misses.Select(m => m.Name ?? $"0x{m.Hash:X8}"))}");
        }
    }
    dts.Sort();
    if (dts.Count > 0)
        Console.WriteLine($"  all {dts.Count:N0} bound tracks: median {dts[dts.Count / 2]:E1} m, ≤ 1e-5 m {100.0 * dts.Count(x => x <= 1e-5) / dts.Count:F1} %, " +
                          $"≤ 1 mm {100.0 * dts.Count(x => x <= 1e-3) / dts.Count:F1} % (the rest are animated translations: pelvis, clavicles, holders)");
    Console.WriteLine($"  bones over 1 mm (clips): {string.Join(", ", offBones.OrderByDescending(kv => kv.Value).Take(16).Select(kv => $"{kv.Key} {kv.Value}"))}");
    // rotation: bones the idle does not rotate (fingers at rest) match the bind rotation
    if (idles.Count > 0)
    {
        var (_, n0, c0, b0) = idles[0];
        int close = 0, total = 0;
        double best = double.MaxValue;
        for (int t = 0; t < c0.TrackCount; t++)
        {
            int b = b0.TrackToBone[t];
            if (b < 0) continue;
            var L = skel.Entities[b].Local;
            var q = c0.Rotation(0, t);
            var m = Matrix4x4.CreateFromQuaternion(q);   // row-vector convention: transpose of the column-vector rotation
            double err = 0;
            float[] R = [m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, m.M13, m.M23, m.M33];
            for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) err = Math.Max(err, Math.Abs(R[r * 3 + c] - L[r * 4 + c]));
            total++;
            if (err < 1e-3) close++;
            best = Math.Min(best, err);
        }
        Console.WriteLine($"  {n0}: frame-0 rotation equals the bind rotation (< 1e-3) for {close}/{total} bound bones, best {best:E1}");
    }
    Console.WriteLine();
}

void Fresh()
{
    var pick = all.Where(x => x.Decoded).OrderBy(x => x.Name.GetHashCode() & 0x7FFFFFFF).Take(freshN).ToList();
    var checks = new ConcurrentBag<(string Name, Anm2CheckResult C)>();
    foreach (var byPack in pick.GroupBy(x => x.Pack))
    {
        var path = packs.First(p => Path.GetRelativePath(install.Assets!, p) == byPack.Key);
        using var pack = RpackFile.Open(path);
        Parallel.ForEach(byPack, r =>
        {
            int i = pack.IndicesOf(RpackFile.EngineFold(System.Text.Encoding.Latin1.GetBytes(r.Name))).First(ix => pack.Logicals[ix].Type == 0x40);
            var clip = Anm2Resource.Decode(pack, i);
            Anm2Clip src;
            if (clip.IsPoseWeights) src = Anm2Encoder.FromStreams(clip.TrackHashes, clip.PoseHashes, clip.FrameBound, (double[])clip.Values.Clone());
            else
            {
                int K = clip.KeyCount, T = clip.TrackCount;
                var q = new Quaternion[K * T];
                var t = new Vector3[K * T];
                var s = new Vector3[K * T];
                for (int k = 0; k < K; k++)
                    for (int j = 0; j < T; j++) { q[k * T + j] = clip.Rotation(k, j); t[k * T + j] = clip.Translation(k, j); s[k * T + j] = clip.Scale(k, j); }
                src = Anm2Encoder.FromTrs(clip.TrackHashes, K, q, t, s);
            }
            checks.Add((r.Name, Anm2Encoder.SelfCheck(src, Anm2Encoder.Encode(src))));
        });
    }
    var cs = checks.ToList();
    var worst = cs.OrderByDescending(x => x.C.MaxRotationDegrees).FirstOrDefault();
    Console.WriteLine($"fresh encode of {cs.Count} clips from float TRS: {cs.Count(x => x.C.HeaderOk)} pass the self-check; max error rotation " +
                      $"{cs.Where(x => !double.IsNaN(x.C.MaxRotationDegrees)).Max(x => x.C.MaxRotationDegrees):F4}° ({worst.Name}), " +
                      $"translation {cs.Max(x => x.C.MaxTranslation):E2} m, scale {cs.Max(x => x.C.MaxScale):E2}, pose weight {cs.Max(x => x.C.MaxWeight):E2}");
    Console.WriteLine();
}

void Timing()
{
    var dec = all.Where(x => x.Decoded).ToList();
    var ms = dec.Select(x => x.DecodeMs).OrderBy(x => x).ToList();
    var big = dec.OrderByDescending(x => x.Bytes).First();
    // re-time the largest clip single-threaded, warm
    var path = packs.First(p => Path.GetRelativePath(install.Assets!, p) == big.Pack);
    using var pack = RpackFile.Open(path);
    int i = pack.IndicesOf(RpackFile.EngineFold(System.Text.Encoding.Latin1.GetBytes(big.Name))).First(ix => pack.Logicals[ix].Type == 0x40);
    var bytes = Anm2Resource.Read(pack, i);
    Anm2Clip.Decode(bytes);
    var sw2 = Stopwatch.StartNew();
    const int reps = 10;
    Anm2Clip? c = null;
    for (int k = 0; k < reps; k++) c = Anm2Clip.Decode(bytes);
    double decodeBig = sw2.Elapsed.TotalMilliseconds / reps;
    sw2.Restart();
    c!.ToTrs();
    double trs = sw2.Elapsed.TotalMilliseconds;
    sw2.Restart();
    Anm2Encoder.Encode(c);
    double enc = sw2.Elapsed.TotalMilliseconds;
    Console.WriteLine($"decode (parallel census, per clip): median {ms[ms.Count / 2]:F3} ms, p99 {ms[(int)(ms.Count * 0.99)]:F2} ms, max {ms[^1]:F1} ms");
    Console.WriteLine($"largest clip {big.Name} ({big.Bytes:N0} bytes, {big.Tracks} tracks, {big.Frames} frames, {big.Segments} segments): " +
                      $"decode {decodeBig:F1} ms, ToTrs {trs:F1} ms, recipe encode {enc:F0} ms");
}

// --diff NAME: where the re-serialised and recipe bytes first differ from the shipped clip.
int DiffOne(string name)
{
    foreach (var path in packs)
    {
        using var pack = RpackFile.Open(path);
        foreach (int i in pack.IndicesOf(RpackFile.EngineFold(System.Text.Encoding.Latin1.GetBytes(name))))
        {
            if (pack.Logicals[i].Type != 0x40) continue;
            var bytes = Anm2Resource.Read(pack, i);
            var p = Anm2Payload.Decode(bytes);
            var clip = Anm2Clip.FromPayload(p);
            Console.WriteLine($"{Path.GetFileName(path)} {name}: {bytes.Length} bytes, header {p.Header.HeaderSize}, segments [{string.Join(",", p.Header.SegmentFrames)}], tracks {clip.TrackCount}");
            var gq = Anm2Encoder.Quantize(clip, new Anm2EncodeOptions { ReuseSourceLayout = false });
            Console.WriteLine($"  shipped segment sizes [{string.Join(",", p.Segments.Select(x => Anm2Payload.SegmentSize(x, clip.TrackCount)))}]");
            Console.WriteLine($"  greedy  [{string.Join(",", gq.Header.SegmentFrames)}] sizes [{string.Join(",", gq.Segments.Select(x => Anm2Payload.SegmentSize(x, clip.TrackCount)))}]");
            Console.WriteLine($"  cuts [{string.Join(",", Anm2Encoder.Cuts(clip))}], end copies [{string.Join(",", clip.EndCopies.Keys)}]");
            foreach (var (label, enc) in new[] { ("serialise", p.Encode()), ("recipe", Anm2Encoder.Encode(clip)),
                         ("greedy", Anm2Encoder.Encode(clip, new Anm2EncodeOptions { ReuseSourceLayout = false })), ("pure", Anm2Encoder.Encode(clip, Anm2EncodeOptions.Recipe)) })
            {
                int n = Math.Min(enc.Length, bytes.Length), first = -1, count = 0;
                for (int k = 0; k < n; k++) if (enc[k] != bytes[k]) { if (first < 0 && k >= p.Header.HeaderSize) first = k; count++; }
                Console.WriteLine($"  {label}: {enc.Length} bytes, {count} differ, first at {first} (0x{first:X}, payload +0x{first - p.Header.HeaderSize:X})");
                if (first >= 0)
                {
                    int at = first & ~15;
                    Console.WriteLine($"    shipped {Convert.ToHexString(bytes, at, Math.Min(48, bytes.Length - at))}");
                    Console.WriteLine($"    ours    {Convert.ToHexString(enc, at, Math.Min(48, enc.Length - at))}");
                }
            }
            return 0;
        }
    }
    Console.WriteLine($"{name}: not found");
    return 1;
}

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

sealed class Result
{
    public string Pack = "", Name = "";
    public bool Pair, Decoded, Serialise, Recipe, GridRecipe, GreedyRecipe, PureRecipe, V3Exact, Pose, IdentityVfr, TimeEqFrame, NonFinal15;
    public int Version = -1, Tracks, Frames, Segments, Poses, Row15Quirks, Row15Cuts, SeamCuts, SeamCopies, PadRows;
    public long Bytes;
    public double DecodeMs, EncodeMs, SeamJump, MaxAbsValue, MaxQuatErr;
    public string? Error, Refused, PureWhy, RecipeWhy, GridWhy, GreedyWhy;
    public long GridStreams, GridSame;
}
