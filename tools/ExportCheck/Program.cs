using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Nightrunner.Core.Cast;
using Nightrunner.Core.Games;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Rpack;
using File = System.IO.File;

// Per-mesh Cast export cross-check: for each detected install, pick meshes (fixed interesting names, one per category
// found by a scan — vertex formats 0/3/6/8, LOD chains, cloth, skeleton-only, unowned entries, non-finite vertex
// floats, most bones — plus a seeded random sample), export each with the C# port (CastExport.WriteFiles) and with the
// Python prototype (tools/ExportCheck/pyexport.py → gui/meshdata.py::export_cast_files, read-only), and compare the
// .cast and .mesh.json bytes. When a .cast differs, the two files are compared node by node: structure, hashes,
// property names/types/counts, exact values for non-float properties, max |Δ| for float ones (quaternions compared up
// to sign).
//
// Usage: dotnet run -c Release -- <out-folder> [--game dltb|dl2] [--random N] [--seed S] [--python EXE] [--name MESH ...]
//                                 [--script tools/ExportCheck/pyexport.py]
// Everything is written under <out-folder>/<game>/{cs,py}/.

if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
{
    Console.WriteLine("usage: ExportCheck <out-folder> [--game dltb|dl2] [--random N] [--seed S] [--python EXE] [--name MESH ...]");
    return 2;
}
string outRoot = Path.GetFullPath(args[0]);
string? onlyGame = Arg("--game");
int randomCount = int.TryParse(Arg("--random"), out int rn) ? rn : 16;
int seed = int.TryParse(Arg("--seed"), out int sd) ? sd : 7;
string python = Arg("--python") ?? "python";
var extraNames = args.Select((a, i) => (a, i)).Where(t => t.a == "--name" && t.i + 1 < args.Length).Select(t => args[t.i + 1]).ToList();

var fixedNames = new Dictionary<string, string[]>
{
    ["dltb"] = ["player_kc_basic_torso_a_tpp", "sh2_npc_crane", "veh_sedan_a", "sh_npc_ft_crane_hair_a", "sh2_npc_aiden_beast",
                "sh2_player_tpp_phx_skeleton", "wn_pistol_b_b", "dummybox_025m"],
    ["dl2"] = ["dummy_box", "player_army_torso_a_tpp"],
};

string? script = Arg("--script");   // default: the source copy: pyexport.py finds nightrunner-main relative to itself
for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && script is null; dir = dir.Parent)
    if (File.Exists(Path.Combine(dir.FullName, "tools", "ExportCheck", "pyexport.py")) &&
        Directory.Exists(Path.Combine(dir.FullName, "nightrunner-main"))) script = Path.Combine(dir.FullName, "tools", "ExportCheck", "pyexport.py");
if (script is null) { Console.WriteLine("tools/ExportCheck/pyexport.py not found above the build output"); return 2; }

var installs = GameInstall.FindInstalls().Values
    .Where(g => g.Supported && (onlyGame is null || g.Id == onlyGame)).OrderBy(g => g.Id == "dltb" ? 0 : 1).ToList();
if (installs.Count == 0)
{
    Console.WriteLine("skip: no Chrome Engine install found (set NIGHTRUNNER_GAME_ROOT)");
    return 0;
}

var summary = new List<string[]>();
int exit = 0;
foreach (var install in installs)
{
    Console.WriteLine($"\n== {install}");
    var sw = Stopwatch.StartNew();
    var all = new List<(string Pack, int Index, string Name)>();
    foreach (var p in install.Rpacks())
    {
        using var pk = RpackFile.Open(p);
        for (int i = 0; i < pk.Count; i++)
            if (pk.Logicals[i].Type == 0x10) all.Add((Path.GetFullPath(p), i, pk.Name(i)));
    }
    var picks = new List<(string Pack, int Index, string Name, string Why)>();
    void Add((string Pack, int Index, string Name) m, string why)
    {
        int k = picks.FindIndex(x => x.Pack == m.Pack && x.Index == m.Index);
        if (k >= 0) picks[k] = picks[k] with { Why = picks[k].Why + ", " + why };
        else picks.Add((m.Pack, m.Index, m.Name, why));
    }
    foreach (var nm in fixedNames.GetValueOrDefault(install.Id, []).Concat(extraNames))
    {
        var hit = all.FirstOrDefault(a => a.Name.Trim() == nm);
        if (hit.Pack is null) Console.WriteLine($"  fixed name not found: {nm}");
        else Add(hit, "named");
    }
    // category scan over a seeded order: first mesh of each kind
    var rng = new Random(seed);
    var order = all.OrderBy(_ => rng.Next()).ToList();
    var categories = new Dictionary<string, (string, int, string)?>
    {
        ["format 0"] = null, ["format 3"] = null, ["format 6"] = null, ["format 8"] = null, ["LOD chain"] = null,
        ["cloth"] = null, ["skeleton only"] = null, ["unowned entry"] = null, ["non-finite floats"] = null, ["unsupported format"] = null,
    };
    (string, int, string)? mostBones = null;
    int maxBones = -1;
    foreach (var grp in order.GroupBy(o => o.Pack))
    {
        using var pk = RpackFile.Open(grp.Key);
        foreach (var m in grp)
        {
            MeshModel model;
            try { model = MeshDecoder.Decode(pk, m.Index); } catch { continue; }
            void Cat(string c, bool hit) { if (hit && categories[c] is null) categories[c] = m; }
            foreach (int f in new[] { 0, 3, 6, 8 }) Cat($"format {f}", model.GeometryEntries.Any(e => e.Format == f && e.Vertices is not null));
            Cat("LOD chain", model.GeometryEntries.Any(e => e.Element > 0));
            Cat("cloth", model.ClothRaw is not null);
            Cat("skeleton only", model.GeometryEntries.Length == 0 && model.Entities.Length > 1);
            Cat("unowned entry", model.GeometryEntries.Any(e => e.OwnerEntity is null));
            Cat("unsupported format", model.GeometryEntries.Any(e => !Vertex.Supported(e.Format)));
            Cat("non-finite floats", model.GeometryEntries.Any(e => e.Vertices is { } v &&
                (v.Positions.Any(x => !float.IsFinite(x)) || v.Uv0.Any(x => !float.IsFinite(x)) || (v.Uv1?.Any(x => !float.IsFinite(x)) ?? false))));
            if (model.Entities.Length > maxBones) { maxBones = model.Entities.Length; mostBones = m; }
        }
    }
    foreach (var (c, m) in categories)
        if (m is { } mm) Add(mm, c);
        else Console.WriteLine($"  category not found in any mesh: {c}");
    if (mostBones is { } mb) Add(mb, $"most bones ({maxBones})");
    foreach (var m in order.Where(o => !picks.Any(p => p.Pack == o.Pack && p.Index == o.Index)).Take(randomCount)) Add(m, "random");

    string csDir = Path.Combine(outRoot, install.Id, "cs"), pyDir = Path.Combine(outRoot, install.Id, "py");
    foreach (var d in new[] { csDir, pyDir })
    {
        Directory.CreateDirectory(d);   // a fresh set: earlier runs' files would otherwise pass for fixtures
        foreach (var f in Directory.EnumerateFiles(d).Where(f => f.EndsWith(".cast", StringComparison.Ordinal) || f.EndsWith(".mesh.json", StringComparison.Ordinal)).ToList())
            File.Delete(f);
    }
    string FileFor(string dir, (string Pack, int Index, string Name, string Why) m) =>
        Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(m.Pack)}_{m.Index}_{Safe(m.Name)}.cast");

    // ---- C# ------------------------------------------------------------------------------------------------------
    var csErr = new Dictionary<(string, int), string>();
    var csSw = Stopwatch.StartNew();
    foreach (var grp in picks.GroupBy(p => p.Pack))
    {
        using var pk = RpackFile.Open(grp.Key);
        foreach (var m in grp)
        {
            string path = FileFor(csDir, m);
            DeleteOld(path);
            try
            {
                var model = MeshDecoder.Decode(pk, m.Index);
                CastExport.WriteFiles(model, m.Name, path, new MeshSidecarSource(m.Pack, m.Index, m.Name));
            }
            catch (Exception ex) { csErr[(m.Pack, m.Index)] = $"{ex.GetType().Name}: {ex.Message}"; }
        }
    }
    double csSeconds = csSw.Elapsed.TotalSeconds;

    // ---- Python --------------------------------------------------------------------------------------------------
    var pyErr = new Dictionary<(string, int), string>();
    var pySw = Stopwatch.StartNew();
    foreach (var grp in picks.GroupBy(p => p.Pack))
    {
        var psi = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(grp.Key);
        foreach (var m in grp)
        {
            string path = FileFor(pyDir, m);
            DeleteOld(path);
            psi.ArgumentList.Add(m.Index.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(path);
        }
        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        using var proc = Process.Start(psi)!;
        var errTask = proc.StandardError.ReadToEndAsync();
        string? line;
        var seen = new HashSet<int>();
        while ((line = proc.StandardOutput.ReadLine()) is not null)
        {
            using var doc = JsonDocument.Parse(line);
            int index = doc.RootElement.GetProperty("index").GetInt32();
            seen.Add(index);
            if (!doc.RootElement.GetProperty("ok").GetBoolean()) pyErr[(grp.Key, index)] = doc.RootElement.GetProperty("error").GetString()!;
        }
        proc.WaitForExit();
        foreach (var m in grp)
            if (!seen.Contains(m.Index)) pyErr[(m.Pack, m.Index)] = $"python exited {proc.ExitCode}: {Last(errTask.Result)}";
    }
    double pySeconds = pySw.Elapsed.TotalSeconds;

    // ---- compare -------------------------------------------------------------------------------------------------
    int compared = 0, castSame = 0, jsonSame = 0, bothFailed = 0, castStructOk = 0;
    double maxDiff = 0;
    string maxWhere = "";
    Console.WriteLine($"  {picks.Count} meshes (C# {csSeconds:F1} s, Python {pySeconds:F1} s)");
    Console.WriteLine($"  {"mesh",-52} {"why",-26} {"cast",-9} {"json",-6} detail");
    foreach (var m in picks)
    {
        string label = $"{Path.GetFileNameWithoutExtension(m.Pack)}#{m.Index} {m.Name}";
        csErr.TryGetValue((m.Pack, m.Index), out var ce);
        pyErr.TryGetValue((m.Pack, m.Index), out var pe);
        if (ce is not null || pe is not null)
        {
            if (ce is not null && pe is not null) bothFailed++;
            Row(label, m.Why, "-", "-", $"C#: {ce ?? "ok"} | py: {pe ?? "ok"}");
            if (ce is not null && pe is null) exit = 1;
            continue;
        }
        compared++;
        string cp = FileFor(csDir, m), pp = FileFor(pyDir, m);
        var cb = File.ReadAllBytes(cp);
        var pb = File.ReadAllBytes(pp);
        var cj = File.ReadAllBytes(MeshSidecar.PathFor(cp));
        var pj = File.ReadAllBytes(MeshSidecar.PathFor(pp));
        bool castEq = cb.AsSpan().SequenceEqual(pb), jsonEq = cj.AsSpan().SequenceEqual(pj);
        if (castEq) castSame++;
        if (jsonEq) jsonSame++;
        var detail = new List<string>();
        if (!castEq)
        {
            var d = CompareCast(cb, pb);
            if (d.Structural.Count == 0) castStructOk++;
            else exit = 1;
            detail.AddRange(d.Structural.Take(3));
            foreach (var (where, (count, max)) in d.Floats.OrderByDescending(x => x.Value.Max))
            {
                detail.Add($"{where}: {count} values differ, max {max:G3}");
                if (max > maxDiff) { maxDiff = max; maxWhere = $"{m.Name} {where}"; }
            }
            if (d.SignFlips > 0) detail.Add($"{d.SignFlips} quaternions q vs -q");
        }
        if (!jsonEq)
        {
            exit = 1;
            detail.Add("json: " + FirstJsonDifference(pj, cj));
        }
        Row(label, m.Why, castEq ? "same" : "differs", jsonEq ? "same" : "DIFF", string.Join("; ", detail.Take(6)));
    }
    Console.WriteLine($"  compared {compared} (both refused {bothFailed}) · .cast identical {castSame}/{compared}, " +
                      $"structurally equal {castSame + castStructOk}/{compared} · .mesh.json identical {jsonSame}/{compared} · " +
                      $"max float |Δ| {maxDiff:G3} {maxWhere} · {sw.Elapsed.TotalSeconds:F1} s");
    summary.Add([install.Id, picks.Count.ToString(), compared.ToString(), $"{castSame}", $"{castSame + castStructOk}", $"{jsonSame}",
                 $"{bothFailed}", maxDiff.ToString("G3", CultureInfo.InvariantCulture)]);
}

Console.WriteLine("\n== summary");
Console.WriteLine($"{"game",-6} {"picked",7} {"compared",9} {"cast==",7} {"cast~=",7} {"json==",7} {"refused",8} {"max|Δ|",9}");
foreach (var r in summary)
    Console.WriteLine($"{r[0],-6} {r[1],7} {r[2],9} {r[3],7} {r[4],7} {r[5],7} {r[6],8} {r[7],9}");
return exit;

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static void Row(string label, string why, string cast, string json, string detail) =>
    Console.WriteLine($"  {Trunc(label, 52),-52} {Trunc(why, 26),-26} {cast,-9} {json,-6} {detail}");

static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

static string Safe(string name)
{
    var sb = new StringBuilder();
    foreach (char c in name.Trim()) sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
    return sb.Length == 0 ? "_" : sb.ToString();
}

static void DeleteOld(string castPath)
{
    foreach (var p in new[] { castPath, MeshSidecar.PathFor(castPath) })
        if (File.Exists(p)) File.Delete(p);
}

static string Last(string stderr) => stderr.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";

// ---- structural comparison ---------------------------------------------------------------------------------------

static (List<string> Structural, Dictionary<string, (int Count, double Max)> Floats, int SignFlips) CompareCast(byte[] cs, byte[] py)
{
    var structural = new List<string>();
    var floats = new Dictionary<string, (int, double)>();
    int flips = 0;
    CastFile a, b;
    try { a = CastFile.Read(cs); b = CastFile.Read(py); }
    catch (Exception e) { structural.Add($"read: {e.Message}"); return (structural, floats, 0); }
    if (a.RootNodes.Count != b.RootNodes.Count) structural.Add("root count");
    for (int i = 0; i < Math.Min(a.RootNodes.Count, b.RootNodes.Count); i++) Walk(a.RootNodes[i], b.RootNodes[i], "root");
    return (structural, floats, flips);

    void Walk(CastNode x, CastNode y, string path)
    {
        string kind = Fourcc(x.Identifier);
        if (x.Identifier != y.Identifier) { structural.Add($"{path}: node {kind} vs {Fourcc(y.Identifier)}"); return; }
        if (x.Hash != y.Hash) structural.Add($"{path}: hash");
        var xk = x.Properties.Keys.ToList();
        var yk = y.Properties.Keys.ToList();
        if (!xk.SequenceEqual(yk)) structural.Add($"{path}: property list [{string.Join(",", xk)}] vs [{string.Join(",", yk)}]");
        foreach (var k in xk.Intersect(yk))
        {
            var p = x.Properties[k];
            var q = y.Properties[k];
            string where = $"{kind}.{k}";
            if (p.Type != q.Type || p.ValueCount != q.ValueCount) { structural.Add($"{path}.{k}: {p.Identifier}×{p.ValueCount} vs {q.Identifier}×{q.ValueCount}"); continue; }
            if (p.Type == CastPropertyType.String)
            {
                if (p.StringValue != q.StringValue) structural.Add($"{path}.{k}: '{p.StringValue}' vs '{q.StringValue}'");
                continue;
            }
            if (p.Type is CastPropertyType.Float or CastPropertyType.Vector2 or CastPropertyType.Vector3 or CastPropertyType.Vector4)
            {
                var fa = p.Floats;
                var fb = q.Floats;
                bool isQuat = kind == "bone" && k is "lr" or "wr";
                if (isQuat)
                {
                    bool same = true, neg = true;
                    for (int j = 0; j < fa.Length; j++)
                    {
                        same &= BitConverter.SingleToUInt32Bits(fa[j]) == BitConverter.SingleToUInt32Bits(fb[j]);
                        neg &= Math.Abs(fa[j] + fb[j]) <= 1e-6;
                    }
                    if (!same && neg && Math.Abs(fa[3]) <= 1e-6) { flips++; continue; }
                }
                int count = 0;
                double max = 0;
                for (int j = 0; j < fa.Length; j++)
                {
                    if (BitConverter.SingleToUInt32Bits(fa[j]) == BitConverter.SingleToUInt32Bits(fb[j])) continue;
                    count++;
                    double dd = float.IsNaN(fa[j]) && float.IsNaN(fb[j]) ? 0 : Math.Abs((double)fa[j] - fb[j]);
                    if (double.IsNaN(dd)) { structural.Add($"{path}.{k}[{j}]: {fa[j]} vs {fb[j]}"); continue; }
                    max = Math.Max(max, dd);
                }
                if (count > 0)
                {
                    var (c0, m0) = floats.GetValueOrDefault(where);
                    floats[where] = (c0 + count, Math.Max(m0, max));
                }
                continue;
            }
            var pa = p.ToDoubleArray();
            var pb2 = q.ToDoubleArray();
            for (int j = 0; j < pa.Length; j++)
                if (pa[j] != pb2[j]) { structural.Add($"{path}.{k}[{j}]: {pa[j]} vs {pb2[j]}"); break; }
        }
        if (x.ChildNodes.Count != y.ChildNodes.Count) structural.Add($"{path}: {x.ChildNodes.Count} vs {y.ChildNodes.Count} children");
        for (int i = 0; i < Math.Min(x.ChildNodes.Count, y.ChildNodes.Count); i++)
            Walk(x.ChildNodes[i], y.ChildNodes[i], $"{path}/{Fourcc(x.ChildNodes[i].Identifier)}{i}");
    }

    static string Fourcc(uint id) => Encoding.ASCII.GetString(BitConverter.GetBytes(id));
}

static string FirstJsonDifference(byte[] py, byte[] cs)
{
    int n = Math.Min(py.Length, cs.Length), i = 0;
    while (i < n && py[i] == cs[i]) i++;
    int lineStart = Array.LastIndexOf(py, (byte)'\n', Math.Max(0, i - 1)) + 1;
    string Ctx(byte[] b)
    {
        int end = Array.IndexOf(b, (byte)'\n', Math.Min(i, b.Length - 1));
        if (end < 0) end = b.Length;
        int start = Math.Min(lineStart, b.Length);
        return Encoding.UTF8.GetString(b, start, Math.Max(0, Math.Min(end, start + 160) - start)).Trim();
    }
    return $"byte {i} (sizes py {py.Length} / C# {cs.Length}): py `{Ctx(py)}` C# `{Ctx(cs)}`";
}
