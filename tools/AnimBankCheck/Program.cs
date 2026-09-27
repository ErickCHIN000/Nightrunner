using System.Diagnostics;
using System.Security.Cryptography;
using Nightrunner.Core.Anim;
using Nightrunner.Core.Games;
using Nightrunner.Core.Rpack;

// Animation bank census over the detected DLTB install: every AnimationScr (0x42/0x43), AnimGraphBank (0x47/0x48) and
// AnimCustomResource (0x49/0x4A x2) of every pack must parse and re-serialise byte for byte; prints the totals the
// animation RE notes quote, the stream-pack identity check, and whether each constant clip reference resolves to a
// SeqTrack. Read-only. Usage: dotnet run -c Release -- [game root] [--show BANK SEQ] [--graph BANK]

string? root = args.FirstOrDefault(a => !a.StartsWith("--") && !IsOptionValue(a));
var install = GameInstall.Find(root, "dltb");
if (install is null)
{
    Console.WriteLine("skip: no DLTB install found (pass a game root, or set NIGHTRUNNER_GAME_ROOT)");
    return 0;
}
var packs = install.Rpacks();
Console.WriteLine($"{install}\n{packs.Length} packs");

int failures = 0;
void Fail(string what)
{
    failures++;
    if (failures <= 40) Console.WriteLine($"FAIL {what}");
}

var sw = Stopwatch.StartNew();
var scrByName = new Dictionary<string, AnimBank>(StringComparer.Ordinal);      // first registration wins
var graphTexts = new List<(string Pack, string Bank, AnimGraph Graph, GraphNode Node)>();
var hashes = new Dictionary<(byte Type, string Name), Dictionary<string, string>>(); // (type, name) → pack → sha
var bakers = new SortedDictionary<string, int>(StringComparer.Ordinal);
var customSeqBanks = new SortedSet<string>(StringComparer.Ordinal);
int scrRes = 0, graphRes = 0, customRes = 0;
long records = 0, events = 0, lists = 0, actions = 0, negative = 0, placeholders = 0;
var tags = new SortedDictionary<char, long>();
var modes = new SortedDictionary<uint, long>();
var fps = new SortedDictionary<float, long>();
var fpsByClip = new Dictionary<string, HashSet<float>>(StringComparer.Ordinal);   // anm2 name → fps it is played at
var commonGraphs = new List<(string Bank, AnimGraph Graph)>();
long relocations = 0;
int stubs = 0;

Console.WriteLine("\npack                                  0x42 recs     events   neg | 0x47 graphs  nodes  clips  const   var | 0x49");
foreach (var path in packs)
{
    using var pack = RpackFile.Open(path);
    string pn = Path.GetFileName(path);
    int s = 0, g = 0, c = 0;
    long pr = 0, pe = 0, pneg = 0, pg = 0, pnodes = 0, pclips = 0, pconst = 0, pvar = 0;
    foreach (int i in AnimBankPacks.OfType(pack, AnimBankPacks.TypeScr))
    {
        s++;
        string name = pack.Name(i);
        try
        {
            var parts = AnimBankPacks.ReadParts(pack, i, AnimBankPacks.ScrShape);
            Hash(AnimBankPacks.TypeScr, name, pn, parts);
            var bank = AnimBank.Parse(parts[0], parts[1]);
            Hash(0xFF, name, pn, [MaskJunk(parts[0], bank), parts[1]]);
            if (!bank.RecordsBytes().AsSpan().SequenceEqual(parts[0])) Fail($"{pn}/{name}: 0x42 round trip differs");
            if (!bank.ScriptBytes().AsSpan().SequenceEqual(parts[1])) Fail($"{pn}/{name}: 0x43 round trip differs");
            if (!bank.IsSorted) Fail($"{pn}/{name}: records not in _stricmp order");
            if (bank.TrailingRecords.Length + bank.TrailingScript.Length > 0) Fail($"{pn}/{name}: trailing bytes");
            for (int k = 0; k < bank.Records.Count; k += Math.Max(1, bank.Records.Count / 64))
                if (bank.Find(bank.Records[k].Name.ToUpperInvariant()) != k) Fail($"{pn}/{name}: lookup of record {k}");
            scrByName.TryAdd(name.ToLowerInvariant(), bank);
            pr += bank.Records.Count;
            pe += bank.EventCount;
            pneg += bank.Records.Count(r => r.FirstEventNegative);
            lists += bank.ActionLists.Count;
            foreach (var l in bank.ActionLists)
                foreach (var a in l)
                {
                    actions++;
                    foreach (var arg in a.Args) tags[arg.Tag] = tags.GetValueOrDefault(arg.Tag) + 1;
                }
            foreach (var r in bank.Records)
            {
                modes[r.DefaultMode] = modes.GetValueOrDefault(r.DefaultMode) + 1;
                fps[r.Fps] = fps.GetValueOrDefault(r.Fps) + 1;
                if (r.Anm2Name.Length == 0) placeholders++;
                else
                {
                    if (!fpsByClip.TryGetValue(r.Anm2Name, out var set)) fpsByClip[r.Anm2Name] = set = [];
                    set.Add(r.Fps);
                }
                foreach (var e in r.Events)
                    if (e.ActionList != AnimEventDef.NoActions && (uint)e.ActionList >= bank.ActionLists.Count)
                        Fail($"{pn}/{name}/{r.Name}: action list {e.ActionList} of {bank.ActionLists.Count}");
            }
        }
        catch (AnimBankFormatException e) { Fail($"{pn}/{name}: {e.Message}"); }
    }
    foreach (int i in AnimBankPacks.OfType(pack, AnimBankPacks.TypeGraph))
    {
        g++;
        string name = pack.Name(i);
        try
        {
            var parts = AnimBankPacks.ReadParts(pack, i, AnimBankPacks.GraphShape);
            Hash(AnimBankPacks.TypeGraph, name, pn, parts);
            var bank = AnimGraphBank.Parse(parts[0], parts[1]);
            if (!bank.FixupsBytes().AsSpan().SequenceEqual(parts[1])) Fail($"{pn}/{name}: 0x48 round trip differs");
            if (bank.Fixups.PrimarySize != parts[0].Length) Fail($"{pn}/{name}: primary size {bank.Fixups.PrimarySize} != image {parts[0].Length}");
            int nrel = bank.CheckRelocations();
            if (pn == "common_anims_pc.rpack") { relocations += nrel; if (bank.IsStub && parts[0].Length == 4) stubs++; }
            pg += bank.Graphs.Count;
            if (pn == "common_anims_pc.rpack") commonGraphs.AddRange(bank.Graphs.Select(gr => (name, gr)));
            foreach (var gr in bank.Graphs)
            {
                pnodes += gr.Nodes.Count;
                foreach (var n in gr.Nodes.Where(n => n.Clip is not null))
                {
                    pclips++;
                    if (n.Clip!.Sequence.Kind == GraphValueKind.Constant) pconst++;
                    else if (n.Clip.Sequence.Kind == GraphValueKind.Variable) pvar++;
                    graphTexts.Add((pn, name, gr, n));
                }
            }
        }
        catch (AnimBankFormatException e) { Fail($"{pn}/{name}: {e.Message}"); }
    }
    foreach (int i in AnimBankPacks.OfType(pack, AnimBankPacks.TypeCustom))
    {
        c++;
        string name = pack.Name(i);
        try
        {
            var parts = AnimBankPacks.ReadParts(pack, i, AnimBankPacks.CustomShape);
            Hash(AnimBankPacks.TypeCustom, name, pn, parts);
            var res = AnimCustomResource.Parse(parts[0], parts[1], parts[2], parts[3]);
            if (!res.HeaderFixupsBytes().AsSpan().SequenceEqual(parts[1])) Fail($"{pn}/{name}: header 0x4A round trip differs");
            if (!res.BankName.Equals(name, StringComparison.OrdinalIgnoreCase)) Fail($"{pn}/{name}: header names '{res.BankName}'");
            if (pn == "common_anims_pc.rpack")
            {
                string key = $"{res.BakerFunc} ({res.BakerDll}, v{res.BakerVersion})";
                bakers[key] = bakers.GetValueOrDefault(key) + 1;
                if (res.BakerFunc == "CSequenceBankBaker") customSeqBanks.Add(res.BankName);
            }
        }
        catch (AnimBankFormatException e) { Fail($"{pn}/{name}: {e.Message}"); }
    }
    if (s + g + c == 0) continue;
    scrRes += s; graphRes += g; customRes += c;
    records += pr; events += pe; negative += pneg;
    Console.WriteLine($"{pn,-36} {s,5} {pr,8:N0} {pe,10:N0} {pneg,5} | {g,4} {pg,6} {pnodes,6:N0} {pclips,6:N0} {pconst,6:N0} {pvar,5:N0} | {c,5:N0}");
}
Console.WriteLine($"{"total",-36} {scrRes,5} {records,8:N0} {events,10:N0} {negative,5} | {graphRes,4} {"",6} {"",6} {"",6} {"",6} {"",5} | {customRes,5:N0}");
Console.WriteLine($"census {sw.ElapsedMilliseconds} ms");

Console.WriteLine($"\nAnimationScr: {lists:N0} action lists, {actions:N0} actions, args {string.Join(" ", tags.Select(t => $"{t.Key}:{t.Value:N0}"))}; " +
                  $"{placeholders:N0} placeholder anm2 names");
Console.WriteLine($"defaultMode {string.Join(", ", modes.Select(m => $"{m.Key}: {m.Value:N0}"))}");
Console.WriteLine($"anm2 names by fps: {string.Join(", ", new[] { 30f, 60f, 120f }.Select(f => $"{f}: {fpsByClip.Count(c => c.Value.Contains(f)):N0}"))}; " +
                  $"{fpsByClip.Count(c => c.Value.Count > 1):N0} of {fpsByClip.Count:N0} names played at more than one fps");
Console.WriteLine($"fps {string.Join(", ", fps.OrderByDescending(f => f.Value).Take(8).Select(f => $"{f.Key}: {f.Value:N0}"))}");

// graph statistics over one pack (the doc quotes per-pack figures)
var common = graphTexts.Where(t => t.Pack == "common_anims_pc.rpack").ToList();
if (common.Count > 0)
{
    Console.WriteLine("\nAnimGraphBank (common_anims_pc):");
    var banks = common.Select(t => t.Bank).Distinct().Count();
    int perBankDistinct = common.Where(t => t.Node.Clip!.Sequence.Text is not null)
        .GroupBy(t => t.Bank).Sum(gp => gp.Select(t => t.Node.Clip!.Sequence.Text).Distinct().Count());
    int globalDistinct = common.Select(t => t.Node.Clip!.Sequence.Text).Where(x => x is not null).Distinct().Count();
    int reverse = common.Count(t => t.Node.Clip!.Sequence.Text is { } x && SeqRef.TryParseGds(x) is { } q &&
                                    x.IndexOf(".scr", StringComparison.OrdinalIgnoreCase) > x.IndexOf('@'));
    int dupMismatch = common.Count(t => t.Node.Clip!.Sequence.Text is { } x && t.Node.Clip.SequenceText != x);
    Console.WriteLine($"clip banks {banks}; distinct constants: {globalDistinct:N0} across the pack, {perBankDistinct:N0} summed per bank; " +
                      $"+0x80 copy differs from the constant: {dupMismatch}; <Seq>@<bank>.scr order: {reverse}");

    // resolve each constant against the 0x42 banks and the CSequenceBankBaker names
    int ok = 0, seqMissing = 0, inCustom = 0, bankMissing = 0;
    var missingBanks = new SortedDictionary<string, int>(StringComparer.Ordinal);
    foreach (var t in common)
    {
        if (t.Node.Clip!.SeqRef is not { } r) continue;
        if (scrByName.TryGetValue(r.Bank, out var b)) { if (b.Find(r.Seq) >= 0) ok++; else seqMissing++; }
        else if (customSeqBanks.Contains(r.Bank)) inCustom++;
        else { bankMissing++; missingBanks[r.Bank] = missingBanks.GetValueOrDefault(r.Bank) + 1; }
    }
    // the bank half often names a text script merged into another bank: try the SeqTrack name in every 0x42 bank
    int anyBank = common.Count(t => t.Node.Clip!.SeqRef is { } r && !scrByName.ContainsKey(r.Bank) &&
                                    scrByName.Values.Any(b => b.Find(r.Seq) >= 0));
    Console.WriteLine($"constant clip refs: {ok:N0} resolve to a 0x42 SeqTrack, {seqMissing:N0} name a 0x42 bank without that " +
                      $"SeqTrack, {inCustom:N0} name a CSequenceBankBaker bank (0x49, not decoded), {bankMissing:N0} name no bank");
    Console.WriteLine($"  of those naming no 0x42 bank, {anyBank:N0} name a SeqTrack that exists in some 0x42 bank");
    if (missingBanks.Count > 0)
        Console.WriteLine($"  banks not found: {string.Join(", ", missingBanks.Take(12).Select(m => $"{m.Key} ({m.Value})"))}");

    var graphs = commonGraphs.Select(t => t.Graph).ToList();
    var trans = graphs.SelectMany(gr => gr.Nodes).Where(n => n.StateMachine is not null)
        .SelectMany(n => n.StateMachine!.Transitions).ToList();
    var durations = trans.Select(x => x.Duration).Where(d => d is not null).ToList();
    var constDur = durations.Where(d => d!.Kind == GraphValueKind.Constant).ToList();
    var btes = graphs.SelectMany(gr => gr.Nodes).Where(n => n.BlendDuration is not null).ToList();
    Console.WriteLine($"state machines {graphs.Sum(gr => gr.Nodes.Count(n => n.StateMachine is not null)):N0}, transitions {trans.Count:N0} " +
                      $"(from any: {trans.Count(x => x.From == -1):N0}, with a BlendTransitionEffect: {durations.Count:N0})");
    Console.WriteLine($"BlendTransitionEffect nodes {btes.Count:N0}: duration constant {btes.Count(n => n.BlendDuration!.Kind == GraphValueKind.Constant):N0} " +
                      $"(0.3 s: {btes.Count(n => n.BlendDuration!.Float == 0.3f):N0}), variable {btes.Count(n => n.BlendDuration!.Kind == GraphValueKind.Variable):N0}; " +
                      $"via transitions: constant {constDur.Count:N0}, variable {durations.Count(d => d!.Kind == GraphValueKind.Variable):N0}");
    Console.WriteLine($"node classes {string.Join(" ", graphs.SelectMany(gr => gr.Nodes).GroupBy(n => (n.ClassId, n.NodeType)).OrderBy(x => x.Key.ClassId).Select(x => $"{AnimGraphBank.ClassName(x.Key.ClassId)}(type {x.Key.NodeType}):{x.Count():N0}"))}");
    int names = 0, match = 0;
    var odd = new List<string>();
    var oddKinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
    foreach (var l in graphs.SelectMany(gr => gr.Interface))
        if (l.Names is { } nm)
            for (int k = 0; k < nm.Length; k++)
            {
                names++;
                if (InterfaceList.ExpectedHash(nm[k]) == l.Hashes[k]) { match++; continue; }
                string why = l.Hashes[k] == 0 ? "hash 0" : l.Hashes[k] == InterfaceList.H41(nm[k]) ? "h41 of full name" : "other";
                oddKinds[$"{l.Kind} {why}"] = oddKinds.GetValueOrDefault($"{l.Kind} {why}") + 1;
                if (odd.Count < 6) odd.Add($"{l.Kind} '{nm[k]}' 0x{l.Hashes[k]:X8} (h41 0x{InterfaceList.H41(nm[k]):X8})");
            }
    Console.WriteLine($"graphs {graphs.Count} in {commonGraphs.Select(t => t.Bank).Distinct().Count()} banks ({commonGraphs.Count(t => t.Graph.Nodes.Count > 0)} with nodes), " +
                      $"{graphs.Sum(gr => gr.Nodes.Count):N0} nodes; interface debug names {names:N0}, hash rule holds for {match:N0}");
    if (odd.Count > 0)
        Console.WriteLine($"  exceptions: {string.Join(", ", oddKinds.Select(o => $"{o.Key}: {o.Value}"))}\n" +
                          $"  e.g. {string.Join("\n       ", odd)}");
    Console.WriteLine($"relocations {relocations:N0}, all inside their image; 4-byte stub banks {stubs}");
    Console.WriteLine($"max roots in one bank {commonGraphs.GroupBy(t => t.Bank).Max(x => x.Count())}; " +
                      $"roots with no nodes {commonGraphs.Count(t => t.Graph.Nodes.Count == 0)}");
}

Console.WriteLine("\nAnimCustomResource bakers (common_anims_pc):");
foreach (var (k, v) in bakers.OrderByDescending(b => b.Value)) Console.WriteLine($"  {v,6:N0}  {k}");
Console.WriteLine($"  {bakers.Values.Sum(),6:N0}  total; CSequenceBankBaker banks: {string.Join(", ", customSeqBanks)}");

// stream-pack identity
Console.WriteLine("\nstream copies:");
foreach (var (a, b) in new[] { ("common_anims_pc.rpack", "common_anims_stream_pc.rpack"), ("player_anims_static_pc.rpack", "player_anims_stream_pc.rpack") })
    foreach (byte type in new[] { AnimBankPacks.TypeScr, AnimBankPacks.TypeGraph, AnimBankPacks.TypeCustom })
    {
        var inA = hashes.Where(h => h.Key.Type == type && h.Value.ContainsKey(a)).ToList();
        var inB = hashes.Where(h => h.Key.Type == type && h.Value.ContainsKey(b)).ToList();
        if (inA.Count + inB.Count == 0) continue;
        int same = inA.Count(h => h.Value.TryGetValue(b, out var hb) && hb == h.Value[a]);
        int onlyA = inA.Count(h => !h.Value.ContainsKey(b)), onlyB = inB.Count(h => !h.Value.ContainsKey(a));
        Console.WriteLine($"  0x{type:X2} {a} vs {b}: {inA.Count} / {inB.Count}, byte-identical {same}, only-first {onlyA}, only-second {onlyB}");
        if (same == inA.Count && onlyB == 0) continue;
        var partDiff = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var h in inA.Where(h => h.Value.TryGetValue(b, out var hb) && hb != h.Value[a]))
        {
            var pa = h.Value[a].Split('|');
            var pb = h.Value[b].Split('|');
            string which = string.Join("+", Enumerable.Range(0, pa.Length).Where(k => pa[k] != pb[k]).Select(k => $"part {k}"));
            partDiff[which] = partDiff.GetValueOrDefault(which) + 1;
        }
        Console.WriteLine($"    NOT identical; differing: {string.Join(", ", partDiff.Select(p => $"{p.Key}: {p.Value}"))}");
        if (type == AnimBankPacks.TypeScr)
        {
            var masked = hashes.Where(h => h.Key.Type == 0xFF && h.Value.ContainsKey(a) && h.Value.ContainsKey(b)).ToList();
            Console.WriteLine($"    with junk words zeroed: identical {masked.Count(h => h.Value[a] == h.Value[b])} of {masked.Count}");
        }
    }

if (Opt("--show") is { } showBank && args.SkipWhile(x => x != "--show").Skip(2).FirstOrDefault() is { } showSeq)
{
    if (scrByName.TryGetValue(SeqRef.NormalizeBank(showBank), out var b) && b[showSeq] is { } r)
    {
        Console.WriteLine($"\n{showBank}@{showSeq}: #{b.Find(showSeq)} {r} mode {r.DefaultMode} blend {r.DefaultBlend} junk {r.Junk}");
        foreach (var e in r.Events)
            Console.WriteLine($"  t5 {e.Time5} ({e.Frame} f) id {e.Id} param {e.Param} list {e.ActionList}" +
                              (e.ActionList >= 0 ? " " + string.Join("; ", b.ActionLists[e.ActionList].Select(a => $"{a.Name}({string.Join(", ", a.Args)})")) : ""));
    }
    else Console.WriteLine($"\n{showBank}@{showSeq}: not found");
}
if (Opt("--graph") is { } gb)
    foreach (var t in graphTexts.Where(t => t.Pack == "common_anims_pc.rpack" && t.Bank == gb).Take(40))
        Console.WriteLine($"  {t.Graph.GraphName} {t.Node}: {t.Node.Clip!.Sequence}");

Console.WriteLine(failures == 0 ? "\nOK" : $"\n{failures} failure(s)");
return failures == 0 ? 0 : 1;

// one short hash per part, joined, so a comparison can say which parts differ
void Hash(byte type, string name, string pack, byte[][] parts)
{
    string key = string.Join("|", parts.Select(p => Convert.ToHexString(SHA256.HashData(p))[..16]));
    if (!hashes.TryGetValue((type, name), out var d)) hashes[(type, name)] = d = [];
    d.TryAdd(pack, key);
}

// 0x42 with the junk words zeroed (record +0x04, event +0x0A), to tell junk-only differences apart
static byte[] MaskJunk(byte[] part, AnimBank bank)
{
    var m = (byte[])part.Clone();
    int ev = bank.Records.Count * AnimBank.RecordSize;
    for (int i = 0; i < bank.Records.Count; i++)
    {
        Array.Clear(m, i * AnimBank.RecordSize + 4, 4);
        for (int k = 0; k < bank.Records[i].Events.Count; k++, ev += AnimBank.EventSize) Array.Clear(m, ev + 10, 2);
    }
    return m;
}

string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();

bool IsOptionValue(string a)
{
    int i = Array.IndexOf(args, a);
    return i > 0 && args[i - 1].StartsWith("--") || i > 1 && args[i - 2] == "--show";
}
