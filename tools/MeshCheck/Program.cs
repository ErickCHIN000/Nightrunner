using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nightrunner.Core.Games;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;

// Mesh census: every type-0x10 resource of every detected install (runtime mod packs included, reported apart) must
// decode, re-encode its fixups/vertex/index parts byte for byte, and round-trip its skin part byte for byte.
// --xcheck N additionally decodes N random meshes per game with the Python prototype (tools/MeshCheck/pydump.py,
// read-only) and compares positions, UVs, normals/tangents, indices, submeshes, materials, bones and skins.
//
// --models instead runs the .model census: every .model in the stock dataN.pak files must parse, and every mesh entry
// is looked up in the rpacks (custom-data paks reported apart).
//
// --cloth runs the cloth census: every resource with a part 0xF3 (any type), every mesh's cloth objects decoded
// (ClothData), part 0xF3 re-encoded byte for byte, the image offsets re-derived by the layout rule, the render-mesh
// invariants checked; plus the .model clothResources and *.cloth pak members.
//
// --materials runs the viewport albedo census (MaterialCensus.cs): every surface the Model and Mesh views draw, by what
// colour it gets and, when none, why.
//
// Usage: dotnet run -c Release -- [--game dltb|dl2] [--xcheck N] [--seed S] [--python EXE] [--name MESH ...] [--models] [--cloth] [--materials [--examples]]

string? onlyGame = Arg("--game");
int xcheck = int.TryParse(Arg("--xcheck"), out int xn) ? xn : 0;
int seed = int.TryParse(Arg("--seed"), out int sd) ? sd : 1;
string python = Arg("--python") ?? "python";
var extraNames = args.Select((a, i) => (a, i)).Where(t => t.a == "--name" && t.i + 1 < args.Length).Select(t => args[t.i + 1]).ToList();

var installs = GameInstall.FindInstalls().Values
    .Where(g => g.Supported && (onlyGame is null || g.Id == onlyGame)).OrderBy(g => g.Id == "dltb" ? 0 : 1).ToList();
if (installs.Count == 0)
{
    Console.WriteLine("skip: no Chrome Engine install found (set NIGHTRUNNER_GAME_ROOT)");
    return 0;
}

if (args.Contains("--models")) return ModelCensus(installs);
if (args.Contains("--cloth")) return ClothCensus(installs);
if (args.Contains("--materials")) return MaterialCensus.Run(installs, args);

int exit = 0;
foreach (var install in installs)
{
    var packs = install.Rpacks(includeCustom: true);
    Console.WriteLine($"\n== {install}\n{packs.Length} packs");
    var sw = Stopwatch.StartNew();
    var failures = new ConcurrentBag<(string Pack, string Name, string Reason)>();
    var all = new List<(string Pack, int Index)>();
    long meshes = 0, custom = 0, entities = 0, entries = 0, submeshes = 0, vertices = 0, skinsRecords = 0, warnings = 0,
         skinOnly = 0, skinOnlyUsed = 0, withSkin = 0;
    var formats = new ConcurrentDictionary<int, long>();
    var layouts = new ConcurrentDictionary<string, long>();
    foreach (var path in packs)
    {
        bool isCustom = install.Paths.IsCustom(path);
        using var pack = RpackFile.Open(path);
        var idx = Enumerable.Range(0, pack.Count).Where(i => pack.Logicals[i].Type == 0x10).ToArray();
        if (idx.Length == 0) continue;
        string label = Path.GetRelativePath(install.Assets!, path);
        long before = failures.Count;
        Parallel.ForEach(idx, i =>
        {
            string name = pack.Name(i);
            string stage = "read";
            try
            {
                var parts = MeshDecoder.ReadParts(pack, i);
                stage = "decode";
                var m = MeshDecoder.Decode(parts);
                stage = "reencode";
                foreach (var (t, bytes) in MeshEncoder.Reencode(m))
                    if (!bytes.AsSpan().SequenceEqual(parts.ByType[t]))
                        throw new Exception($"part 0x{t:X2} differs");
                long sk = 0;
                if (parts.Skin is { } skin)
                {
                    stage = "skins";
                    var s = MeshSkins.Decode(skin);
                    if (s.Error is not null) throw new Exception(s.Error);
                    if (!s.Encode().AsSpan().SequenceEqual(skin)) throw new Exception("part 0x12 differs");
                    SknWriter.Write(s, m, null);
                    sk = s.Skins.Count;
                    var used = s.Skins.SelectMany(x => x.Replace).Select(r => (int)r.Material).Where(x => x >= m.Materials.Length).Distinct().Count();
                    Interlocked.Add(ref skinOnlyUsed, used);
                    Interlocked.Increment(ref withSkin);
                }
                Interlocked.Add(ref skinsRecords, sk);
                Interlocked.Increment(ref meshes);
                if (isCustom) Interlocked.Increment(ref custom);
                Interlocked.Add(ref entities, m.Entities.Length);
                Interlocked.Add(ref entries, m.GeometryEntries.Length);
                Interlocked.Add(ref submeshes, m.SubmeshCount);
                Interlocked.Add(ref vertices, m.VertexCount);
                Interlocked.Add(ref warnings, m.Warnings.Count);
                Interlocked.Add(ref skinOnly, Math.Max(0, m.MaterialCapacity - m.Materials.Length));
                foreach (var e in m.GeometryEntries) formats.AddOrUpdate(e.Format, 1, (_, v) => v + 1);
                layouts.AddOrUpdate(m.Layout, 1, (_, v) => v + 1);
            }
            catch (Exception e)
            {
                failures.Add((label, name, $"{stage}: {e.Message}"));
                Interlocked.Increment(ref meshes);
            }
        });
        lock (all) all.AddRange(idx.Select(i => (path, i)));
        Console.WriteLine($"  {label,-44} {idx.Length,7:N0}{(isCustom ? "  custom" : "")}{(failures.Count > before ? $"  FAIL {failures.Count - before}" : "")}");
    }
    sw.Stop();
    long ok = meshes - failures.Count;
    Console.WriteLine($"meshes {meshes:N0} (custom {custom:N0}) — decode + byte-exact re-encode + skins round trip: {ok:N0}/{meshes:N0}");
    Console.WriteLine($"entities {entities:N0}, geometry entries {entries:N0}, submeshes {submeshes:N0}, vertices {vertices:N0}");
    Console.WriteLine($"formats {string.Join(", ", formats.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value:N0}"))}; " +
                      $"layouts {string.Join(", ", layouts.Select(k => $"{k.Key}:{k.Value:N0}"))}");
    Console.WriteLine($"skin parts {withSkin:N0}, skins {skinsRecords:N0}, skin-only materials {skinOnly:N0} ({skinOnlyUsed:N0} used by a Replace), decode warnings {warnings:N0}");
    Console.WriteLine($"time {sw.Elapsed.TotalSeconds:F1} s, peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / (1 << 20):N0} MB");
    foreach (var g in failures.GroupBy(f => Regex.Replace(f.Reason, @"0x[0-9A-Fa-f]+|\d+", "#")).OrderByDescending(g => g.Count()))
    {
        Console.WriteLine($"  FAIL ×{g.Count()}: {g.Key}");
        foreach (var f in g.Take(3)) Console.WriteLine($"        {f.Pack} {f.Name}: {f.Reason}");
    }
    if (!failures.IsEmpty) exit = 1;

    if (xcheck > 0 && !CrossCheck(install, all, xcheck, seed, python, extraNames)) exit = 1;
}
return exit;

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static bool CrossCheck(GameInstall install, List<(string Pack, int Index)> all, int n, int seed, string python, List<string> names)
{
    var rng = new Random(seed);
    var pick = all.OrderBy(_ => rng.Next()).Take(n).ToList();
    foreach (var nm in names)
        foreach (var p in all.Select(a => a.Pack).Distinct())
        {
            using var pk = RpackFile.Open(p);
            for (int i = 0; i < pk.Count; i++)
                if (pk.Logicals[i].Type == 0x10 && pk.Name(i) == nm && !pick.Contains((p, i))) pick.Add((p, i));
        }
    string? script = null;   // the source copy: pydump.py finds nightrunner-main relative to itself
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && script is null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "tools", "MeshCheck", "pydump.py"))) script = Path.Combine(dir.FullName, "tools", "MeshCheck", "pydump.py");
    if (script is null) { Console.WriteLine("cross-check: tools/MeshCheck/pydump.py not found above the build output"); return false; }
    Console.WriteLine($"cross-check against the Python prototype: {pick.Count} meshes");
    var sw = Stopwatch.StartNew();
    int ok = 0, bad = 0;
    foreach (var grp in pick.GroupBy(p => p.Pack))
    {
        var psi = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(grp.Key);
        foreach (var (_, i) in grp) psi.ArgumentList.Add(i.ToString());
        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        using var proc = Process.Start(psi)!;
        var errTask = proc.StandardError.ReadToEndAsync();
        using var pack = RpackFile.Open(grp.Key);
        string? line;
        while ((line = proc.StandardOutput.ReadLine()) is not null)
        {
            using var doc = JsonDocument.Parse(line);
            var py = doc.RootElement;
            int index = py.GetProperty("index").GetInt32();
            var diffs = Compare(py, pack, index);
            if (diffs.Count == 0) ok++;
            else
            {
                bad++;
                Console.WriteLine($"  MISMATCH {Path.GetFileName(grp.Key)} #{index} {pack.Name(index)}: {string.Join("; ", diffs.Take(5))}");
            }
        }
        proc.WaitForExit();
        if (proc.ExitCode != 0) { Console.WriteLine($"  python failed: {errTask.Result}"); bad++; }
    }
    Console.WriteLine($"cross-check: {ok}/{ok + bad} equal ({sw.Elapsed.TotalSeconds:F1} s)");
    return bad == 0;
}

static List<string> Compare(JsonElement py, RpackFile pack, int index)
{
    var d = new List<string>();
    if (py.TryGetProperty("error", out var perr))
    {
        d.Add($"python error {perr.GetString()}");
        return d;
    }
    MeshModel m;
    try { m = MeshDecoder.Decode(pack, index); }
    catch (Exception e) { d.Add($"C# error {e.Message}"); return d; }
    void Eq<T>(string what, T a, T b) { if (!EqualityComparer<T>.Default.Equals(a, b)) d.Add($"{what}: py {a} cs {b}"); }
    static string B64<T>(T[] a) where T : struct => Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()));

    Eq("layout", py.GetProperty("layout").GetString(), m.Layout);
    var pents = py.GetProperty("entities").EnumerateArray().ToList();
    Eq("bones", pents.Count, m.Entities.Length);
    for (int i = 0; i < Math.Min(pents.Count, m.Entities.Length); i++)
    {
        Eq($"entity {i} name", pents[i][0].GetString(), Convert.ToHexString(m.Entities[i].Name).ToLowerInvariant());
        Eq($"entity {i} parent", pents[i][1].GetInt32(), m.Entities[i].Parent);
        Eq($"entity {i} type", pents[i][2].GetInt32(), m.Entities[i].Type);
    }
    var pm = py.GetProperty("materials").EnumerateArray().Select(x => x.GetString()).ToList();
    Eq("materials", string.Join(",", pm), string.Join(",", m.Materials.Select(x => Convert.ToHexString(x.Name).ToLowerInvariant())));
    Eq("capacity", py.GetProperty("capacity").GetInt32(), m.MaterialCapacity);
    var pe = py.GetProperty("entries").EnumerateArray().ToList();
    Eq("entries", pe.Count, m.GeometryEntries.Length);
    for (int k = 0; k < Math.Min(pe.Count, m.GeometryEntries.Length); k++)
    {
        var p = pe[k];
        var e = m.GeometryEntries[k];
        Eq($"entry {k} format", p.GetProperty("format").GetInt32(), e.Format);
        Eq($"entry {k} vertices", p.GetProperty("vertex_count").GetInt32(), e.VertexCount);
        var ps = p.GetProperty("submeshes").EnumerateArray().ToList();
        Eq($"entry {k} submeshes", ps.Count, e.Submeshes.Length);
        for (int s = 0; s < Math.Min(ps.Count, e.Submeshes.Length); s++)
        {
            Eq($"entry {k} sub {s} slot", ps[s][0].GetInt32(), e.Submeshes[s].MaterialSlot);
            Eq($"entry {k} sub {s} count", ps[s][1].GetInt32(), e.Submeshes[s].IndexCount);
            Eq($"entry {k} sub {s} palette", string.Join(",", ps[s][2].EnumerateArray().Select(x => x.GetInt32())), string.Join(",", e.Submeshes[s].Palette));
        }
        Eq($"entry {k} indices", p.GetProperty("indices").GetString(), B64(e.Submeshes.SelectMany(s => s.Indices).ToArray()));
        if (e.Vertices is not { } v) { Eq($"entry {k} vertices present", p.TryGetProperty("pos", out _), false); continue; }
        Eq($"entry {k} positions", p.GetProperty("pos").GetString(), B64(v.Positions));
        Eq($"entry {k} uv0", p.GetProperty("uv0").GetString(), B64(v.Uv0));
        Eq($"entry {k} uv1", p.GetProperty("uv1").GetString(), v.Uv1 is null ? null : B64(v.Uv1));
        Eq($"entry {k} sign", p.GetProperty("sign").GetString(), B64(v.TangentSign));
        Eq($"entry {k} weights", p.GetProperty("weights").GetString(), v.Weights is null ? null : B64(v.Weights.Select(w => (byte)MathF.Round(w * 255)).ToArray()));
        Eq($"entry {k} joints", p.GetProperty("joints").GetString(), v.Joints is null ? null : B64(v.Joints));
        foreach (var (key, arr) in new[] { ("normals", v.Normals), ("tangents", v.Tangents) })
        {
            var pf = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(Convert.FromBase64String(p.GetProperty(key).GetString()!));
            if (pf.Length != arr.Length) { d.Add($"entry {k} {key} length"); continue; }
            float worst = 0;
            for (int i = 0; i < arr.Length; i++) worst = Math.Max(worst, Math.Abs(pf[i] - arr[i]));
            if (!(worst <= 1e-6f)) d.Add($"entry {k} {key} max |Δ| {worst}");
        }
    }
    if (py.GetProperty("skins") is { ValueKind: JsonValueKind.Object } ps2)
    {
        var s = MeshSkins.Decode(m.SkinRaw!);
        if (ps2.TryGetProperty("error", out var se)) Eq("skins error", se.GetString(), s.Error);
        else
        {
            Eq("skin table", string.Join(",", ps2.GetProperty("table").EnumerateArray().Select(x => x.GetString())), string.Join(",", m.FullMaterialTable()));
            var list = ps2.GetProperty("skins").EnumerateArray().ToList();
            Eq("skins", list.Count, s.Skins.Count);
            for (int i = 0; i < Math.Min(list.Count, s.Skins.Count); i++)
            {
                var a = list[i];
                var b = s.Skins[i];
                string pyS = $"{a.GetProperty("name").GetString()}|{a.GetProperty("flags").GetUInt32():X}|{a.GetProperty("tail").GetString()}|{a.GetProperty("e_hi").GetInt32()}|" +
                             string.Join(",", a.GetProperty("pairs").EnumerateArray().Select(x => $"{x[0]}>{x[1]}")) + "|" +
                             string.Join(",", a.GetProperty("remap").EnumerateArray().Select(x => $"{x[0]}>{x[1]}:{x[2]}")) + "|" +
                             string.Join(",", a.GetProperty("refs").EnumerateArray().Select(x => $"{x[0]}:{x[1]}:{x[2]}"));
                string csS = $"{b.NameStr}|{b.Flags:X}|{(b.Color is null ? "" : Convert.ToHexString(b.Color).ToLowerInvariant())}|{b.EHi}|" +
                             string.Join(",", b.Replace.Select(x => $"{x.Slot}>{x.Material}")) + "|" +
                             string.Join(",", b.ReplaceSurface.Select(x => $"{x.Old}>{x.New}:{x.SurfaceFlags}")) + "|" +
                             string.Join(",", b.UseSkin.Select(x => $"{x.Record}:{x.Hi}:{x.Raw}"));
                Eq($"skin {i}", pyS, csS);
            }
        }
    }
    Eq("warnings", string.Join(" / ", py.GetProperty("warnings").EnumerateArray().Select(x => x.GetString())), string.Join(" / ", m.Warnings));
    return d;
}

static int ModelCensus(List<GameInstall> installs)
{
    int exit = 0;
    foreach (var install in installs)
    {
        var sw = Stopwatch.StartNew();
        using var catalog = new ModelCatalog(install.Paks(includeCustom: false));
        long models = 0, parsed = 0, slots = 0, refs = 0, multi = 0, found = 0, cloth = 0, overridden = 0;
        var errors = new Dictionary<string, int>();
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        using var rpacks = new RpackCatalog();
        rpacks.LoadAsync(install.Rpacks(includeCustom: false), install.Assets!, CancellationToken.None).GetAwaiter().GetResult();
        foreach (var e in catalog.Models)
        {
            models++;
            if (!e.Wins) overridden++;
            try
            {
                var doc = catalog.Load(e);
                parsed++;
                slots += doc.Slots.Count;
                foreach (var s in doc.Slots)
                {
                    refs += s.Meshes.Count;
                    if (s.Meshes.Count > 1) multi++;
                    if (s.HasCloth) cloth++;
                    foreach (var m in s.Meshes)
                        if (ModelResolver.Lookup(rpacks, m.MeshName, ModelResolver.MeshType).Length > 0) found++;
                        else if (m.MeshName.Length > 0) missing.Add(m.MeshName);
                }
            }
            catch (Exception ex) when (ex is ModelFormatException or JsonException or InvalidDataException or IOException)
            {
                string k = Regex.Replace($"{ex.GetType().Name}: {ex.Message}", @"\d+", "#");
                errors[k] = errors.GetValueOrDefault(k) + 1;
            }
        }
        Console.WriteLine($"\n== {install}\npaks {string.Join(", ", catalog.PakPaths.Select(Path.GetFileName))}");
        Console.WriteLine($"models {models:N0} · parsed {parsed:N0} · overridden {overridden:N0} · slots {slots:N0} · mesh refs {refs:N0} · " +
                          $"slots with >1 entry {multi:N0} · slots with cloth {cloth:N0} · refs found in rpacks {found:N0} · " +
                          $"missing names {missing.Count:N0} · {sw.Elapsed.TotalSeconds:F1} s");
        foreach (var (k, n) in errors) Console.WriteLine($"  error x{n}: {k}");
        foreach (var n in missing.Take(10)) Console.WriteLine($"  missing: {n}");
        if (parsed != models) exit = 1;
    }
    return exit;
}

// Every cloth mesh rebuilt from its own Cast export: unedited (every part byte-identical), and per cloth mapping one
// edit that deletes the last faces of the entry's first submesh plus the vertices only they used and reverses the
// submesh's vertex order (bp_vertex_id removed → ids recovered by exact match): the build must pass its self-check
// (which re-checks every cloth invariant) and every output vertex must keep its source's cloth data.
static int ClothRebuilds(GameInstall install)
{
    int unedited = 0, uneditedOk = 0, edits = 0, editsOk = 0;
    long vertsBefore = 0, vertsAfter = 0;
    var fails = new List<string>();
    string root = Path.Combine(Path.GetTempPath(), "nightrunner-clothcheck", Guid.NewGuid().ToString("N"));
    var sw = Stopwatch.StartNew();
    try
    {
        foreach (var path in install.Rpacks(includeCustom: false))
        {
            using var pack = RpackFile.Open(path);
            for (int i = 0; i < pack.Count; i++)
            {
                var lg = pack.Logicals[i];
                if (lg.Type != 0x10) continue;
                bool has = false;
                for (int k = 0; k < lg.PartCount && !has; k++) has = pack.PartType((int)lg.FirstPart + k) == MeshDecoder.PartCloth;
                if (!has) continue;
                var parts = MeshDecoder.ReadParts(pack, i);
                var model = MeshDecoder.Decode(parts);
                string name = parts.Name;
                string dir = Path.Combine(root, name.Trim());
                string cast = Path.Combine(dir, "model.cast");
                var (_, side) = Nightrunner.Core.Cast.CastExport.WriteFiles(model, name, cast);
                var sidecar = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(side!))!.AsObject();
                var options = new MeshBuildOptions { IgnoreBoneChanges = true };
                unedited++;
                try
                {
                    var r = MeshBuild.Build(parts.ByType, name, cast, sidecar, options);
                    if (parts.ByType.All(kv => r.Parts[kv.Key].AsSpan().SequenceEqual(kv.Value))) uneditedOk++;
                    else fails.Add($"{name} unedited: parts differ");
                }
                catch (MeshBuildException e) { fails.Add($"{name} unedited: {e.Message}"); }
                var cloth = ClothData.Decode(model)!;
                foreach (var map in cloth.Mappings)
                {
                    edits++;
                    try
                    {
                        var cf = Nightrunner.Core.Cast.CastFile.Load(cast);
                        var mdl = cf.Roots()[0].ChildOfType<Nightrunner.Core.Cast.Model>()!;
                        var mesh = mdl.Meshes().First(x => x.Property("bp_entry")!.NumberAt(0) == map.Entry);
                        var faces = mesh.FaceBuffer()!;
                        int drop = Math.Max(3, faces.Length / 10 / 3 * 3);
                        var kept = faces[..(faces.Length - drop)];
                        var used = kept.Distinct().Select(x => (int)x).OrderByDescending(x => x).ToArray();
                        var at = new Dictionary<int, uint>();
                        for (int v = 0; v < used.Length; v++) at[used[v]] = (uint)v;
                        CastKeep(mesh, used, kept.Select(f => at[(int)f]).ToArray());
                        mesh.RemoveProperty("bp_vertex_id");
                        string edited = Path.Combine(dir, $"edit{map.Index}.cast");
                        cf.Save(edited);
                        var imp = Nightrunner.Core.Cast.CastImport.Resolve(Nightrunner.Core.Cast.CastImport.Read(edited), sidecar);
                        var res = MeshRebuild.Rebuild(model, imp, name, ignoreBoneChanges: true);
                        var problems = MeshRebuild.Verify(res, model, name);
                        var ids = res.Plan.Entries[map.Entry].RawIds!;
                        var m2 = MeshDecoder.Decode(name, res.Image, res.Fixups, res.Vertex, res.Index, parts.Skin, res.Cloth ?? parts.Cloth);
                        var c2 = ClothData.Decode(m2)!;
                        var map2 = c2.Mappings[map.Index];
                        // every per-vertex float copy follows its source vertex
                        foreach (var (f, w) in new[] { (0x40, 3), (0x44, 3), (0x48, 4) })
                        {
                            if (cloth.Array(map.Index, f) is not { } a0) continue;
                            var o0 = ClothData.ReadF32(a0);
                            var o1 = ClothData.ReadF32(c2.Array(map.Index, f)!);
                            for (int v = 0; v < ids.Length; v++)
                                if (!o0.AsSpan((int)ids[v] * w, w).SequenceEqual(o1.AsSpan(v * w, w))) { problems.Add($"+0x{f:X2} vertex {v}"); break; }
                        }
                        if (map2.VertexCount != ids.Length) problems.Add("vertex count");
                        vertsBefore += map.VertexCount;
                        vertsAfter += map2.VertexCount;
                        if (problems.Count == 0) editsOk++;
                        else fails.Add($"{name} mapping {map.Index}: {string.Join("; ", problems.Take(3))}");
                    }
                    catch (Exception e) when (e is MeshBuildException or MeshFormatException)
                    {
                        fails.Add($"{name} mapping {map.Index}: {e.Message}");
                    }
                }
            }
        }
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
    Console.WriteLine($"rebuilds: unedited {uneditedOk}/{unedited} byte-identical (every part); delete + reorder per mapping {editsOk}/{edits} " +
                      $"(render vertices {vertsBefore:N0} → {vertsAfter:N0}); {sw.Elapsed.TotalSeconds:F1} s");
    foreach (var f in fails.Take(10)) Console.WriteLine($"  FAIL {f}");
    return fails.Count == 0 ? 0 : 1;
}

static void CastKeep(Nightrunner.Core.Cast.Mesh mesh, int[] keep, uint[] faces)
{
    static float[] G(float[] a, int[] keep, int w) => keep.SelectMany(i => a.AsSpan(i * w, w).ToArray()).ToArray();
    int n = mesh.VertexCount()!.Value;
    mesh.SetVertexPositionBuffer(G(mesh.VertexPositionBuffer()!, keep, 3));
    mesh.SetVertexNormalBuffer(G(mesh.VertexNormalBuffer()!, keep, 3));
    mesh.SetVertexTangentBuffer(G(mesh.VertexTangentBuffer()!, keep, 3));
    for (int l = 0; l < mesh.UVLayerCount(); l++) mesh.SetVertexUVLayerBuffer(l, G(mesh.VertexUVLayerBuffer(l)!, keep, 2));
    if (mesh.VertexWeightValueBuffer() is { } wv)
    {
        int mi = wv.Length / n;
        mesh.SetVertexWeightValueBuffer(G(wv, keep, mi));
        var wb = mesh.VertexWeightBoneBuffer()!;
        mesh.SetVertexWeightBoneBuffer(keep.SelectMany(i => wb.AsSpan(i * mi, mi).ToArray()).ToArray());
    }
    var sign = G(mesh.Property("bp_tangent_sign")!.ToSingleArray(), keep, 1);
    mesh.CreateProperty("bp_tangent_sign", Nightrunner.Core.Cast.CastPropertyType.Float).SetValues(sign);
    mesh.SetFaceBuffer(faces);
}

static int ClothCensus(List<GameInstall> installs)
{
    int exit = 0;
    foreach (var install in installs)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"\n== {install}");
        var partOwners = new SortedDictionary<string, int>();
        var rows = new List<(string Pack, string Name, int Size, ClothData? Cloth, string? Error, List<string> Problems, bool Exact)>();
        long meshes = 0;
        foreach (var path in install.Rpacks(includeCustom: true))
        {
            using var pack = RpackFile.Open(path);
            string label = Path.GetRelativePath(install.Assets!, path);
            for (int i = 0; i < pack.Count; i++)
            {
                var lg = pack.Logicals[i];
                bool hasCloth = false;
                for (int k = 0; k < lg.PartCount; k++) hasCloth |= pack.PartType((int)lg.FirstPart + k) == MeshDecoder.PartCloth;
                if (lg.Type == 0x10) meshes++;
                if (!hasCloth) continue;
                string key = $"type 0x{lg.Type:X2}";
                partOwners[key] = partOwners.GetValueOrDefault(key) + 1;
                if (lg.Type != 0x10) continue;
                string name = pack.Name(i);
                try
                {
                    var parts = MeshDecoder.ReadParts(pack, i);
                    var m = MeshDecoder.Decode(parts);
                    var c = ClothData.Decode(m)!;
                    var mapOffsets = c.Mappings.Select(x => x.Offset).ToList();
                    bool exact = c.EncodePart().AsSpan().SequenceEqual(parts.Cloth);
                    var img = (byte[])parts.Image!.Clone();
                    c.PatchImage(img, c.EntityInFileOffset, mapOffsets);
                    exact &= img.AsSpan().SequenceEqual(parts.Image);
                    rows.Add((label, name, parts.Cloth!.Length, c, null, c.Check(m), exact));
                }
                catch (Exception e) when (e is MeshFormatException or MeshUnsupportedException)
                {
                    rows.Add((label, name, 0, null, e.Message, [], false));
                }
            }
        }
        int ok = rows.Count(r => r.Cloth is not null && r.Exact && r.Problems.Count == 0);
        Console.WriteLine($"meshes {meshes:N0}; resources with a part 0xF3: {(partOwners.Count == 0 ? "none" : string.Join(", ", partOwners.Select(k => $"{k.Key}: {k.Value}")))}");
        Console.WriteLine($"cloth meshes {rows.Count}: decode + byte-exact part re-encode + image offsets re-derived + invariants {ok}/{rows.Count}");
        foreach (var r in rows.Where(r => r.Cloth is null || !r.Exact || r.Problems.Count > 0))
        {
            Console.WriteLine($"  FAIL {r.Pack} {r.Name}: {r.Error ?? (r.Exact ? "" : "not byte-exact; ")}{string.Join("; ", r.Problems.Take(3))}");
            exit = 1;
        }
        var cs = rows.Where(r => r.Cloth is not null).Select(r => r.Cloth!).ToList();
        if (cs.Count > 0)
        {
            var mp = cs.SelectMany(c => c.Mappings.Select(m => (Cloth: c, Map: m))).ToList();
            var okRows = rows.Where(r => r.Cloth is not null).ToList();
            Console.WriteLine($"part sizes {okRows.Min(r => r.Size):N0}..{okRows.Max(r => r.Size):N0} B (total {okRows.Sum(r => (long)r.Size):N0}); " +
                              $"particles {cs.Min(c => c.ParticleCount)}..{cs.Max(c => c.ParticleCount)} (total {cs.Sum(c => (long)c.ParticleCount):N0}); " +
                              $"sim triangles {cs.Sum(c => (long)c.IndexCount / 3):N0}");
            Console.WriteLine($"mappings {mp.Count} (per mesh {string.Join(",", cs.GroupBy(c => c.Mappings.Count).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"))}); " +
                              $"render vertices {mp.Sum(m => (long)m.Map.VertexCount):N0}, mapped {mp.Sum(m => (long)m.Map.MappedCount):N0}; " +
                              $"with skinning block (+0x38..+0x50) {mp.Count(m => m.Cloth.Array(m.Map.Index, 0x38) is not null)}, " +
                              $"with marked list (+0x2C..+0x34) {mp.Count(m => m.Cloth.Array(m.Map.Index, 0x2C) is not null)}");
            Console.WriteLine($"colliders {cs.Sum(c => c.Colliders.Count)} (meshes with any {cs.Count(c => c.Colliders.Count > 0)}; types " +
                              $"{string.Join(",", cs.SelectMany(c => c.Colliders).GroupBy(k => k.Type).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"))}); " +
                              $"proxy palettes per mesh {string.Join(",", cs.GroupBy(c => c.ProxyPalettes.Length).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"))}");
            Console.WriteLine("constraint sets with count > 0: " + string.Join(", ", Enumerable.Range(0, 3).Select(k => $"S{k} {cs.Count(c => c.Sets[k].Count > 0)}")) +
                              $", S3 {cs.Count(c => c.Set3Count > 0)}; entity arrays present: " +
                              string.Join(" ", ClothData.EntityFields.Select(f => $"{f.Name}:{cs.Count(c => c.Array(-1, f.Field) is not null)}")));
            Console.WriteLine("surface +0x38: " + string.Join(",", cs.GroupBy(c => c.Surface).Select(g => $"0x{g.Key:X}:{g.Count()}")) +
                              "; LOD-level mapping indices: " + string.Join(",", cs.GroupBy(c => string.Join("", c.LodMapping)).Select(g => $"{g.Key}:{g.Count()}")));
            Console.WriteLine("simulation floats (distinct rows): " + string.Join(" | ", cs.Select(c => string.Join(" ", c.Simulation.Select(x => x.ToString("G4")))).Distinct().Take(8)));
            Console.WriteLine($"ClothEntityInFile name == mesh name + \".msh\": {rows.Count(r => r.Cloth?.Name is { } n && string.Equals(n, r.Name + ".msh", StringComparison.OrdinalIgnoreCase))}/{rows.Count}");
            Console.WriteLine("names: " + string.Join(", ", rows.OrderBy(r => r.Name).Select(r => r.Name)));
        }

        if (rows.Count > 0) exit |= ClothRebuilds(install);

        // .model clothResources and *.cloth members
        using var catalog = new ModelCatalog(install.Paks(includeCustom: false));
        var f3 = rows.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int slots = 0, named = 0, init = 0, onF3 = 0, f3NoRes = 0, models = 0;
        var keys = new SortedDictionary<string, int>();
        var clothNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var f3Named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in catalog.Models)
        {
            ModelDocument doc;
            try { doc = catalog.Load(e); }
            catch (Exception ex) when (ex is ModelFormatException or JsonException or InvalidDataException or IOException) { continue; }
            bool any = false;
            foreach (var s in doc.Slots)
            {
                string mesh = s.Drawn?.MeshName ?? "";
                if (!s.HasCloth) { if (f3.Contains(mesh)) f3NoRes++; continue; }
                any = true;
                slots++;
                if (s.Json["clothResources"] is System.Text.Json.Nodes.JsonObject o)
                {
                    foreach (var (k, _) in o) keys[k] = keys.GetValueOrDefault(k) + 1;
                    string n = ModelDocument.Str(o["name"]) ?? "";
                    clothNames.Add(n);
                    if (string.Equals(n, mesh + ".cloth", StringComparison.OrdinalIgnoreCase)) named++;
                    if (ModelDocument.Flag(o["initialized"])) init++;
                }
                if (f3.Contains(mesh)) { onF3++; f3Named.Add(mesh); }
            }
            if (any) models++;
        }
        int clothMembers = 0;
        foreach (var p in catalog.PakPaths)
        {
            using var ix = PakIndex.Open(p);
            clothMembers += ix.Members.Count(m => m.Name.EndsWith(".cloth", StringComparison.OrdinalIgnoreCase));
        }
        Console.WriteLine($".model: {models} models, {slots} slots with clothResources (keys {string.Join(", ", keys.Select(k => $"{k.Key}:{k.Value}"))}); " +
                          $"name == slot mesh + \".cloth\" {named}; initialized {init}; slot mesh has a part 0xF3 {onF3} ({f3Named.Count} of {f3.Count} cloth meshes); " +
                          $"{clothNames.Count} distinct names; slots whose mesh has a part 0xF3 but no clothResources {f3NoRes}; *.cloth pak members {clothMembers}");
        Console.WriteLine($"time {sw.Elapsed.TotalSeconds:F1} s");
    }
    return exit;
}
