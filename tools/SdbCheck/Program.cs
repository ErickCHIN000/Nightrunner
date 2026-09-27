using System.Diagnostics;
using Nightrunner.Core.Backends;
using Nightrunner.Core.Games;
using Nightrunner.Core.Sdb;

// SDB reader self-check: open every runtime_dx*.sdb of an install, assert the framing and table invariants,
// then resolve every material and report the totals the Python reader measured. Skips (exit 0) when no install
// is present. Usage: dotnet run -- [game root]

var install = GameInstall.Find(args.FirstOrDefault());
if (install is null)
{
    Console.WriteLine("skip: no Chrome Engine install found (pass a game root, or set NIGHTRUNNER_GAME_ROOT)");
    return 0;
}

var files = GameBackends.For(install).Sdb?.Files(install) ?? [];
Console.WriteLine($"{install}\n{files.Length} database(s)");
if (files.Length == 0)
{
    Console.WriteLine("skip: install has no runtime_dx*.sdb");
    return 0;
}

int failures = 0;
void Check(bool ok, string what)
{
    if (ok) return;
    failures++;
    Console.WriteLine($"FAIL {what}");
}

foreach (var path in files)
{
    Console.WriteLine($"\n=== {Path.GetFileName(path)} ===");
    var sw = Stopwatch.StartNew();
    using var sdb = SdbFile.Open(path);
    long openMs = sw.ElapsedMilliseconds;

    Console.WriteLine($"layout {sdb.Layout.Name} (inner 0x{sdb.InnerVersion:X8}), " +
                      $"{sdb.Length:N0} bytes = 16 + A {sdb.BlockASize:N0} + B {sdb.BlockBSize:N0}");
    Console.WriteLine($"opened and walked in {openMs} ms; compact tags {string.Join("/", sdb.CompactTags)}");

    Check(sdb.Tables.Count == sdb.Layout.OrderA.Length + sdb.Layout.OrderB.Length,
          $"{sdb.Tables.Count} tables parsed, layout declares {sdb.Layout.Order.Count()}");

    long tableBytes = sdb.Tables.Values.Sum(t => (long)t.Bytes);
    long programBytes = sdb.Programs.Values.Sum(g => g.Sum(s => (long)s.Length));
    Console.WriteLine($"{sdb.Tables.Values.Sum(t => (long)t.Count):N0} records in {tableBytes:N0} table bytes; " +
                      $"programs {string.Join(", ", sdb.Programs.Select(g => $"{g.Key}:{g.Value.Length:N0}"))} " +
                      $"= {programBytes:N0} bytes");
    Check(programBytes == sdb.BlockBSize, $"program blobs {programBytes:N0} != block B {sdb.BlockBSize:N0}");

    Console.WriteLine($"materials {sdb.MaterialCount:N0}, routes {sdb.RouteCount:N0}, presets {sdb.PresetCount:N0}");

    foreach (var t in sdb.Layout.Order.Select(d => sdb.Table(d.Key)).Where(t => SdbFormat.IsInterpreted(t.Key)))
        Console.WriteLine($"  0x{t.Key:X2} {t.Shape,-12} w{t.Width,-3} {t.Count,9:N0} rows {t.Bytes,12:N0} B  {t.Meaning}");

    // every preset decodes and consumes its record exactly
    sw.Restart();
    int incomplete = 0, presetParameters = 0;
    for (int i = 0; i < sdb.PresetCount; i++)
    {
        var p = sdb.Preset(i);
        presetParameters += p.Parameters.Count;
        if (!p.Complete) incomplete++;
    }
    Console.WriteLine($"presets: {presetParameters:N0} declared parameters in {sw.ElapsedMilliseconds} ms");
    Check(incomplete == 0, $"{incomplete} preset record(s) left bytes unconsumed");

    // resolve everything
    sw.Restart();
    int materials = 0, parameters = 0, declared = 0, typed = 0, runtimeNames = 0;
    int withoutRoute = 0, nonRendering = 0, presetMissing = 0, presetAmbiguous = 0, errors = 0;
    long bindings = 0, overrides = 0, shaderDefaults = 0;
    var textures = new HashSet<string>(StringComparer.Ordinal);
    for (int i = 0; i < sdb.MaterialCount; i++)
    {
        SdbMaterial m;
        try
        {
            m = sdb.Material(i);
        }
        catch (SdbFormatException e)
        {
            if (errors++ < 5) Console.WriteLine($"  material {i}: {e.Message}");
            continue;
        }
        materials++;
        if (m.Routes.Count == 0) withoutRoute++;
        if (m.NonRendering) nonRendering++;
        foreach (var r in m.Routes)
        {
            if (r.PresetIndices.Count == 0) presetMissing++;
            else if (r.PresetIndices.Count > 1) presetAmbiguous++;
            foreach (var p in r.Parameters)
            {
                parameters++;
                if (p.Declared) declared++;
                if (p.ValueText is not null) typed++;
                if (p.RuntimeIndex is not null) runtimeNames++;
            }
            foreach (var b in r.Variants.SelectMany(v => v.Bindings))
            {
                bindings++;
                if (b.Overridden) overrides++; else shaderDefaults++;
                if (b.RuntimeIndex is not null) runtimeNames++;
                if (!string.IsNullOrEmpty(b.Texture)) textures.Add(b.Texture);
            }
        }
    }
    long resolveMs = sw.ElapsedMilliseconds;

    Console.WriteLine($"resolved {materials:N0} materials in {resolveMs} ms " +
                      $"({(materials == 0 ? 0 : resolveMs * 1000.0 / materials):F0} us each)");
    Console.WriteLine($"  parameters {parameters:N0} ({declared:N0} declared, {typed:N0} typed)");
    Console.WriteLine($"  bindings   {bindings:N0} ({overrides:N0} override, {shaderDefaults:N0} shader default)");
    Console.WriteLine($"  textures   {textures.Count:N0} distinct names");
    Console.WriteLine($"  routes     {presetMissing:N0} preset missing, {presetAmbiguous:N0} ambiguous");
    Console.WriteLine($"  materials  {withoutRoute:N0} without a route, {nonRendering:N0} non-rendering");
    Console.WriteLine($"  runtime-resolved names used {runtimeNames:N0} time(s)");

    Check(errors == 0, $"{errors} material(s) failed to resolve");
    Check(materials == sdb.MaterialCount, "not every material resolved");
    Check(bindings == overrides + shaderDefaults, "binding sources do not add up");

    // one route per material is the census shape; report it rather than assume it
    int multi = 0;
    for (int i = 0; i < sdb.MaterialCount; i++) if (sdb.RoutesOf(i).Count > 1) multi++;
    Console.WriteLine($"  {sdb.MaterialCount - withoutRoute - multi:N0}/{sdb.MaterialCount:N0} materials have " +
                      $"exactly one route ({multi:N0} with more)");

    // name lookup round-trips
    string sample = sdb.MaterialName(sdb.MaterialCount / 2);
    Check(sdb.FindMaterial(sample).Contains(sdb.MaterialCount / 2), $"lookup of '{sample}' missed its own index");
    Check(sample.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)
              ? sdb.FindMaterial(sample[..^4]).Count > 0
              : true,
          $"lookup of '{sample}' without the .mat suffix found nothing");

    // the snapshot the panels read: lowered name blob, per-material preset, and the search over it
    sw.Restart();
    var index = SdbIndex.Build(sdb);
    Console.WriteLine($"index built in {index.BuildTime.TotalMilliseconds:F0} ms " +
                      $"({index.MaterialsByPreset.Count:N0} presets in use)");
    Check(index.Count == sdb.MaterialCount, "index does not cover every material");
    Check(index.Routed == materials - withoutRoute,
          $"{index.Routed:N0} materials got a preset name, {materials - withoutRoute:N0} have a route");

    var hit = index.Search("player", CancellationToken.None);
    Console.WriteLine($"search 'player': {hit.Materials.Length:N0} in {hit.Elapsed.TotalMilliseconds:F1} ms");
    var everything = index.Search("", CancellationToken.None);
    Check(everything.Materials.Length == sdb.MaterialCount, "an empty query did not return everything");
    var one = index.Search($"#{sdb.MaterialCount / 2}", CancellationToken.None);
    Check(one.Materials is [var only] && only == sdb.MaterialCount / 2, "the #index query missed");

    Console.WriteLine($"working set {Environment.WorkingSet / (1024 * 1024)} MB");
}

// a database labelled with the wrong inner version must fail loudly, never mis-resolve
var probe = Path.Combine(Path.GetTempPath(), "nightrunner-sdb-wrong-version.sdb");
try
{
    var head = new byte[SdbFormat.HeaderSize];
    using (var fs = File.OpenRead(files[0])) fs.ReadExactly(head);
    BitConverter.GetBytes(SdbFormat.HeaderSize - 16).CopyTo(head, 8);   // block A: the rest of the header
    BitConverter.GetBytes(0).CopyTo(head, 12);                          // block B: empty, so the size check passes
    BitConverter.GetBytes(0xDEADBEEFu).CopyTo(head, 20);                // and only the version is wrong
    File.WriteAllBytes(probe, head);
    try
    {
        using var bad = SdbFile.Open(probe);
        Check(false, "a database with an unknown inner version opened anyway");
    }
    catch (SdbFormatException e)
    {
        Console.WriteLine($"\nunknown inner version refused: {e.Message.Split(':')[^1].Trim()}");
    }
}
finally
{
    if (File.Exists(probe)) File.Delete(probe);
}

Console.WriteLine(failures == 0 ? "\nOK" : $"\n{failures} failure(s)");
return failures == 0 ? 0 : 1;
