using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nightrunner.BuildCheck;
using Nightrunner.Core.Cast;
using Nightrunner.Core.Games;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Rpack;
using CastMesh = Nightrunner.Core.Cast.Mesh;
using CastModel = Nightrunner.Core.Cast.Model;
using File = System.IO.File;

// Mesh-writing cross-check: for DLTB meshes (named + random), export the per-mesh Cast + sidecar, derive edited scenes
// (unedited, renamed output, the same as .glb unedited and with moved vertices, moved vertices, a deleted triangle, a Blender-like round trip without bp_* properties or
// tangents, a new material, edited normals, edited weights, an added vertex), rebuild each with the Python prototype
// (tools/BuildCheck/pybuild.py, read-only, PYTHONDONTWRITEBYTECODE=1) and with MeshBuild.Build on the SAME files, and
// compare the produced parts byte for byte.
//
// Known by design: "newmat" — the prototype overwrites a skin-only class-11 entry (docs/porting.md, Divergences), C# grows the
// table, so only the vertex/index parts are compared; cloth meshes — C# remaps part 0xF3 (and patches its offsets in the
// image) or refuses a new cloth vertex, the prototype carries the part verbatim.
//
// Usage: dotnet run -c Release -- [--count N] [--seed S] [--name MESH ...] [--python EXE] [--out DIR] [--full-unedited]
//   --full-unedited: instead, the unedited round trip (export → build) over every DLTB mesh, C# only.

int count = int.TryParse(Arg("--count"), out int cn) ? cn : 10;
int seed = int.TryParse(Arg("--seed"), out int sd) ? sd : 7;
string python = Arg("--python") ?? "python";
string outRoot = Path.GetFullPath(Arg("--out") ?? Path.Combine(Path.GetTempPath(), "nightrunner-buildcheck"));
var names = args.Select((a, i) => (a, i)).Where(t => t.a == "--name" && t.i + 1 < args.Length).Select(t => args[t.i + 1]).ToList();
if (names.Count == 0) names = ["sh2_npc_crane", "player_kc_basic_torso_a_tpp", "veh_sedan_a", "wn_pistol_b_b", "dummybox_025m"];

if (!GameInstall.FindInstalls().TryGetValue("dltb", out var install))
{
    Console.WriteLine("skip: no DLTB install found (set NIGHTRUNNER_GAME_ROOT)");
    return 0;
}
var all = new List<(string Pack, int Index, string Name)>();
foreach (var p in install.Rpacks())
{
    using var pk = RpackFile.Open(p);
    for (int i = 0; i < pk.Count; i++) if (pk.Logicals[i].Type == 0x10) all.Add((p, i, pk.Name(i)));
}
if (args.Contains("--full-unedited")) return FullUnedited(all, outRoot);

var rng = new Random(seed);
var pick = new List<(string Pack, int Index, string Name)>();
foreach (var n in names)
    if (all.FirstOrDefault(a => a.Name.Trim() == n) is { Pack: not null } hit) pick.Add(hit);
    else Console.WriteLine($"not found: {n}");
pick.AddRange(all.Where(a => !pick.Contains(a)).OrderBy(_ => rng.Next()).Take(count));

string? script = null;
for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && script is null; dir = dir.Parent)
    if (File.Exists(Path.Combine(dir.FullName, "tools", "BuildCheck", "pybuild.py"))) script = Path.Combine(dir.FullName, "tools", "BuildCheck", "pybuild.py");
if (script is null) { Console.WriteLine("tools/BuildCheck/pybuild.py not found above the build output"); return 1; }

if (Directory.Exists(outRoot)) Directory.Delete(outRoot, true);
Directory.CreateDirectory(outRoot);
var jobs = new List<Job>();
foreach (var (pack, index, name) in pick)
{
    using var pk = RpackFile.Open(pack);
    var parts = MeshDecoder.ReadParts(pk, index);
    var model = MeshDecoder.Decode(parts);
    if (model.Layout != "dltb") continue;
    string safe = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    string root = Path.Combine(outRoot, safe);
    Directory.CreateDirectory(root);
    var partFiles = new Dictionary<string, string>();
    foreach (var (t, b) in parts.ByType)
    {
        string f = Path.Combine(root, $"part_{t:X2}.bin");
        File.WriteAllBytes(f, b);
        partFiles[$"{t:X2}"] = f;
    }
    string src = Path.Combine(root, "src", "model.cast");
    var (_, side) = CastExport.WriteFiles(model, name, src);
    foreach (var (variant, edit) in MeshBuildVariants.All())
    {
        string scene = Path.Combine(root, variant, variant.StartsWith("gltf", StringComparison.Ordinal) ? "model.glb" : "model.cast");
        Directory.CreateDirectory(Path.GetDirectoryName(scene)!);
        var cast = CastFile.Load(src);
        var mdl = cast.Roots()[0].ChildOfType<CastModel>()!;
        if (!edit(mdl, model)) { Directory.Delete(Path.GetDirectoryName(scene)!, true); continue; }
        if (scene.EndsWith(".glb", StringComparison.Ordinal)) Gltf.Save(cast, scene);
        else cast.Save(scene);
        jobs.Add(new Job($"{safe}/{variant}", variant, scene, side!, partFiles, variant == "rename" ? name.Trim() + "_nr" : name,
                         Path.Combine(root, variant, "py"), parts.ByType, model.ClothRaw is not null));
    }
}
Console.WriteLine($"{pick.Count} meshes, {jobs.Count} jobs → {outRoot}");

// ---- Python -----------------------------------------------------------------------------------------------------------
string jobsFile = Path.Combine(outRoot, "jobs.json");
File.WriteAllText(jobsFile, new JsonArray(jobs.Select(j => (JsonNode?)new JsonObject
{
    ["id"] = j.Id, ["scene"] = j.Scene, ["sidecar"] = j.Sidecar, ["name"] = j.Name, ["out"] = j.PyOut,
    ["parts"] = new JsonObject(j.PartFiles.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
}).ToArray()).ToJsonString());
var sw = Stopwatch.StartNew();
var psi = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
psi.ArgumentList.Add(script);
psi.ArgumentList.Add(jobsFile);
psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
using (var proc = Process.Start(psi)!)
{
    var err = proc.StandardError.ReadToEndAsync();
    proc.StandardOutput.ReadToEnd();
    proc.WaitForExit();
    if (proc.ExitCode != 0) { Console.WriteLine($"python failed: {err.Result}"); return 1; }
}
double pyTime = sw.Elapsed.TotalSeconds;

// ---- C# and comparison ------------------------------------------------------------------------------------------------
sw.Restart();
var tally = new SortedDictionary<string, int[]>(StringComparer.Ordinal);   // identical, both refused, by design, mismatch
int mismatches = 0;
foreach (var j in jobs)
{
    var t = tally.TryGetValue(j.Variant, out var a) ? a : tally[j.Variant] = new int[4];
    string? pyErr = File.Exists(Path.Combine(j.PyOut, "error.txt")) ? File.ReadAllText(Path.Combine(j.PyOut, "error.txt")) : null;
    MeshBuildResult? cs = null;
    string? csErr = null;
    try
    {
        var sidecar = JsonNode.Parse(File.ReadAllText(j.Sidecar))!.AsObject();
        cs = MeshBuild.Build(j.Original, j.Name, j.Scene, sidecar, new MeshBuildOptions { IgnoreBoneChanges = true });
    }
    catch (MeshBuildException e) { csErr = e.Message; }
    if (pyErr is not null || csErr is not null)
    {
        if (pyErr is not null && csErr is not null) { t[1]++; Console.WriteLine($"  both refuse {j.Id}\n    py: {pyErr}\n    cs: {csErr}"); }
        else if (csErr is not null && csErr.StartsWith(MeshRebuild.ClothRefusal, StringComparison.Ordinal))
        {
            t[2]++;
            Console.WriteLine($"  by design {j.Id}: C# refuses (cloth: {csErr})");
        }
        else if (csErr is null && pyErr!.Contains("class-11 table is full"))
        {
            t[2]++;
            Console.WriteLine($"  by design {j.Id}: the prototype refuses a full table, C# grows it");
        }
        else { t[3]++; mismatches++; Console.WriteLine($"  MISMATCH {j.Id}: py {pyErr ?? "ok"} | cs {csErr ?? "ok"}"); }
        continue;
    }
    var diffs = new List<string>();
    foreach (var type in new byte[] { 0x10, 0x11, 0xF0, 0xF1 })
    {
        string f = Path.Combine(j.PyOut, $"{type:X2}.bin");
        if (!File.Exists(f)) continue;
        var py = File.ReadAllBytes(f);
        if (!py.AsSpan().SequenceEqual(cs!.Parts[type])) diffs.Add($"0x{type:X2}{FirstDiff(py, cs.Parts[type])}");
    }
    bool newMaterial = cs!.Report["materials_added"] is JsonArray { Count: > 0 };
    bool clothRebuilt = j.Cloth && !cs.Parts[MeshDecoder.PartCloth].AsSpan().SequenceEqual(j.Original[MeshDecoder.PartCloth]);
    if (diffs.Count == 0) t[0]++;
    else if (clothRebuilt && diffs.All(d => d.StartsWith("0x10")))
    {
        t[2]++;
        Console.WriteLine($"  by design {j.Id}: {string.Join(", ", diffs)} (cloth part rebuilt and its offsets patched; the prototype carries part 0xF3 verbatim)");
    }
    else if (newMaterial && diffs.All(d => d.StartsWith("0x10") || d.StartsWith("0x11")))
    {
        t[2]++;
        Console.WriteLine($"  by design {j.Id}: {string.Join(", ", diffs)} (material table grown, skins remapped; the prototype overwrites a skin-only entry)");
    }
    else { t[3]++; mismatches++; Console.WriteLine($"  MISMATCH {j.Id}: {string.Join(", ", diffs)}"); }
}
Console.WriteLine($"\nvariant       identical  both-refuse  by-design  mismatch");
foreach (var (v, a) in tally) Console.WriteLine($"{v,-13} {a[0],9}  {a[1],11}  {a[2],9}  {a[3],8}");
Console.WriteLine($"total {jobs.Count} jobs, {mismatches} mismatches; python {pyTime:F1} s, C# {sw.Elapsed.TotalSeconds:F1} s");
return mismatches == 0 ? 0 : 1;

// ---------------------------------------------------------------------------------------------------------------------

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string FirstDiff(byte[] a, byte[] b)
{
    if (a.Length != b.Length) return $" (size {a.Length} vs {b.Length})";
    for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return $" (first at 0x{i:X})";
    return "";
}

static int FullUnedited(List<(string Pack, int Index, string Name)> all, string outRoot)
{
    Directory.CreateDirectory(outRoot);
    int ok = 0, bad = 0, dl2 = 0;
    var sw = Stopwatch.StartNew();
    foreach (var grp in all.GroupBy(a => a.Pack))
    {
        using var pk = RpackFile.Open(grp.Key);
        Parallel.ForEach(grp, a =>
        {
            var parts = MeshDecoder.ReadParts(pk, a.Index);
            string dir = Path.Combine(outRoot, $"u{a.Index}_{Environment.CurrentManagedThreadId}_{Guid.NewGuid():N}");
            try
            {
                var model = MeshDecoder.Decode(parts);
                if (model.Layout != "dltb") { Interlocked.Increment(ref dl2); return; }
                string cast = Path.Combine(dir, "model.cast");
                var (_, side) = CastExport.WriteFiles(model, a.Name, cast);
                var r = MeshBuild.Build(parts.ByType, a.Name, cast, JsonNode.Parse(File.ReadAllText(side!))!.AsObject());
                var diff = parts.ByType.Where(kv => !r.Parts[kv.Key].AsSpan().SequenceEqual(kv.Value)).Select(kv => $"0x{kv.Key:X2}").ToList();
                if (diff.Count == 0 && r.Warnings.Count == 0) Interlocked.Increment(ref ok);
                else
                {
                    Interlocked.Increment(ref bad);
                    Console.WriteLine($"  DIFF {a.Name}: {string.Join(",", diff)} {string.Join(" | ", r.Warnings.Take(2))}");
                }
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref bad);
                Console.WriteLine($"  ERROR {a.Name}: {e.Message}");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        });
    }
    Console.WriteLine($"unedited round trip: {ok}/{ok + bad} byte-identical with no warnings ({dl2} non-DLTB skipped), {sw.Elapsed.TotalSeconds:F1} s");
    return bad == 0 ? 0 : 1;
}

sealed record Job(string Id, string Variant, string Scene, string Sidecar, Dictionary<string, string> PartFiles, string Name, string PyOut,
                  IReadOnlyDictionary<byte, byte[]> Original, bool Cloth);
