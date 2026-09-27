using System.Diagnostics;
using System.Security.Cryptography;
using Nightrunner.Core.Games;
using Nightrunner.Core.Prefab;
using Nightrunner.Core.Rpack;

// Prefabs (type 0x61) self-check: every prefab-bearing pack of an install is parsed, re-encoded (both parts
// byte-exact), validated and decoded; per-pack sizes and counts are printed. A layout the decoder refuses (Dying Light 2)
// is parsed and round-tripped only. Skips (exit 0) when no install is present. Read-only: edits stay in memory.
// Usage: dotnet run -- [--game dltb|dl2] [--root DIR] [--pack PACK] [--doc] [--edits] [--text] [--place] [--dump NAME] [-v]
//   --text     every text *.prefab pak member read (JSON / MessagePack / YAML), compared with the binary prefab of its name
//   --place    the catalog as the UI builds it (binary + text), and every winning binary prefab placed
//   --doc      the notes' [DATA] numbers, measured per pack
//   --edits    rename and duplicate one prefab per pack (the --dump name, else the first), then value edits (transform, text,
//              scalar, m_SelfActive, entity removal) on the first candidates; validate, re-parse, decode, check
//   --show N   the decoded object model of prefab N
//   --dump N   annotated qword dump of prefab N's record-span closure; -v untyped slots grouped by field
//   --explore / --fields C,C|all / --rec I,I   class census, per-field slot census, record dumps

string? game = null, root = null, dump = null, packFilter = null;
bool edits = false, explore = false, verbose = false, docChecks = false, text = false, place = false;
string? fields = null, recs = null, show = null;
for (int i = 0; i < args.Length; i++)
    switch (args[i])
    {
        case "--game": game = args[++i]; break;
        case "--root": root = args[++i]; break;
        case "--dump": dump = args[++i]; break;
        case "--show": show = args[++i]; break;
        case "--pack": packFilter = args[++i]; break;
        case "--edits": edits = true; break;
        case "--text": text = true; break;
        case "--place": place = true; break;
        case "-v": verbose = true; break;
        case "--doc": docChecks = true; break;
        case "--explore": explore = true; break;
        case "--rec": recs = args[++i]; break;
        case "--fields": fields = args[++i]; break;
    }

var install = GameInstall.Find(root, game ?? "dltb");
if (install is null)
{
    Console.WriteLine("skip: no install found (pass --root, or set NIGHTRUNNER_GAME_ROOT)");
    return 0;
}
Console.WriteLine(install);

int failures = 0;
void Check(bool ok, string what)
{
    if (ok) return;
    failures++;
    Console.WriteLine($"FAIL {what}");
}

var total = Stopwatch.StartNew();
int packs = 0, refused = 0, dl2Decoded = 0;
long typedAll = 0, slotsAll = 0;
var kindsAll = new SortedDictionary<byte, long>();
Console.WriteLine($"{"pack",-34} {"0x61 B",12} {"0x62 B",12} {"nPrefab",7} {"nPreset",7} {"records",8} {"slots",9} {"sec B",10}  kinds");
foreach (var path in install.Rpacks())
{
    using var pack = RpackFile.Open(path);
    var idx = PrefabContainer.ResourcesIn(pack);
    if (idx.Length == 0) continue;
    string packName = Path.GetFileNameWithoutExtension(path);
    if (packFilter is not null && !packName.Equals(packFilter, StringComparison.OrdinalIgnoreCase)) continue;
    foreach (int li in idx)
    {
        packs++;
        var (img, meta) = PrefabContainer.ReadParts(pack, li);
        PrefabContainer c;
        try { c = PrefabContainer.Parse(img, meta); }
        catch (PrefabFormatException e) { Check(false, $"{packName}: {e.Message}"); continue; }
        var enc = c.EncodeMetadata();
        Check(SHA256.HashData(enc).SequenceEqual(SHA256.HashData(meta)), $"{packName}: metadata re-encode differs");
        Check(ReferenceEquals(c.EncodePrimary(), img) || c.EncodePrimary().AsSpan().SequenceEqual(img), $"{packName}: primary differs");
        var kinds = new SortedDictionary<byte, int>();
        foreach (var s in c.Slots) kinds[s.Kind] = kinds.GetValueOrDefault(s.Kind) + 1;
        foreach (var (k, v) in kinds) kindsAll[k] = kindsAll.GetValueOrDefault(k) + v;
        Console.WriteLine($"{packName,-34} {img.Length,12:N0} {meta.Length,12:N0} {c.PrefabCount,7:N0} {c.PresetCount,7:N0} " +
                          $"{c.Records.Count,8:N0} {c.Slots.Count,9:N0} {c.SecondarySize,10:N0}  " +
                          string.Join(" ", kinds.Select(kv => $"{kv.Key}:{kv.Value}")));
        PrefabLayout layout;
        try { layout = PrefabLayout.Detect(c); }
        catch (PrefabFormatException e)
        {
            refused++;
            Console.WriteLine($"  refused (parse and round trip only): {e.Message}");
            if (explore) Explore.Run(c, packName);
            if (recs is not null) Explore.DumpRecords(c, recs.Split(',').Select(int.Parse));
            if (fields is not null) Explore.FieldCensus(c, fields == "all" ? [] : fields.Split(',').Select(x => Convert.ToUInt32(x, 16)).ToArray());
            continue;
        }
        if (layout.IsDl2) dl2Decoded++;
        else
        {
            var problems = c.Problems();
            foreach (var pr in problems.Take(10)) Console.WriteLine($"  problem: {pr}");
            Check(problems.Count == 0, $"{packName}: {problems.Count} validation problem(s)");
        }
        var sw = Stopwatch.StartNew();
        var doc = PrefabDecoder.Decode(c);
        var cov = doc.Coverage;
        typedAll += cov.Typed;
        slotsAll += cov.Total;
        var comps = doc.Prefabs.SelectMany(x => x.Components).ToList();
        var blobs = doc.AllValues.ToList();
        var vals = blobs.SelectMany(x => x.Entries).ToList();
        Console.WriteLine($"  decode {sw.ElapsedMilliseconds} ms: slots typed {cov.Typed:N0}/{cov.Total:N0} ({cov.TypedFraction:P2}), " +
                          $"resolved untyped {cov.Generic:N0}; components {comps.Count:N0}, entities {comps.Count(x => x.Entity is not null):N0} " +
                          $"(prefab resolved {comps.Count(x => x.Entity is { EntityPrefab: >= 0 }):N0}), fields {doc.Prefabs.Sum(x => x.Fields.Count):N0}, " +
                          $"blobs {blobs.Count:N0} (embedded {doc.EmbeddedValues.Count:N0}) / values {vals.Count:N0} " +
                          $"({string.Join(" ", vals.GroupBy(v => v.Form).Select(g => $"{g.Key}:{g.Count()}"))}; engine-keyed {vals.Count(v => v.EngineKey):N0}), " +
                          $"presets {doc.PresetSets.Sum(x => x.Groups.Sum(g => g.Presets.Count)):N0} / values {doc.PresetSets.Sum(x => x.Groups.Sum(g => g.Presets.Sum(p => p.Values.Count))):N0}, " +
                          $"warnings {doc.Warnings.Count}");
        Check(comps.All(x => x.OwnerPrefab >= 0), $"{packName}: a component without a resolved owner");
        var ents = comps.Where(x => x.Entity is not null).Select(x => x.Entity!).ToList();
        int byIndex = ents.Count(e => e.EntityPrefab >= 0), byName = ents.Count(e => e.EntityPrefabClass is not null);
        Console.WriteLine($"  entity -> prefab: in this image {byIndex:N0}, by class name (kind 9 -> 0xC0000042) {byName:N0}, neither {ents.Count - byIndex - byName:N0}");
        if (verbose)
        {
            Console.WriteLine("  untyped slots by class: " + string.Join(", ", cov.UntypedByClass.OrderByDescending(kv => kv.Value).Take(25).Select(kv => $"{PrefabClasses.Name(kv.Key)}:{kv.Value}")));
            foreach (var w in doc.Warnings.Take(10)) Console.WriteLine($"  warning: {w}");
            Explore.Untyped(c, doc);
        }
        if (docChecks) DocChecks.Run(c, doc);
        if (show is not null && doc.Find(show) is { } shown) Show(shown);
        if (edits && c.PrefabCount > 0 && !layout.IsDl2) Edits(c, doc, packName, dump);
        if (explore) Explore.Run(c, packName);
        if (dump is not null) Explore.Dump(c, dump);
        if (recs is not null) Explore.DumpRecords(c, recs.Split(',').Select(int.Parse));
        if (fields is not null) Explore.FieldCensus(c, fields == "all" ? [] : fields.Split(',').Select(x => Convert.ToUInt32(x, 16)).ToArray());
    }
}
Console.WriteLine($"\nslots typed over every decoded resource: {typedAll:N0}/{slotsAll:N0} ({(double)typedAll / Math.Max(1, slotsAll):P2})");
Console.WriteLine($"{packs} Prefabs resource(s), {refused} refused by layout, {dl2Decoded} decoded with the Dying Light 2 layout (validate and edits: DLTB only); kinds {string.Join(" ", kindsAll.Select(kv => $"{kv.Key}:{kv.Value:N0}"))}; {total.ElapsedMilliseconds} ms");
if (text || place)
{
    int refusedText = TextCheck.Run(install, (ok, what) => { Check(ok, what); return ok; }, place, text);
    Console.WriteLine($"text members refused by name: {refusedText}");
}
Console.WriteLine(failures == 0 ? "OK" : $"{failures} failure(s)");
return failures == 0 ? 0 : 1;

// Rename and duplicate one prefab (the --dump name, else the first), then validate, re-encode, re-parse and decode.
// Structural only: nothing produced here has been loaded in the game.
void Edits(PrefabContainer c, PrefabDocument doc, string packName, string? name)
{
    var src = (name is not null ? doc.Find(name) : null) ?? doc.Prefabs[0];
    var sw = Stopwatch.StartNew();
    var r = c.Clone();
    int slots = r.Slots.Count, recs = r.Records.Count;
    PrefabEdits.Rename(r, src.Index, src.Name + "_renamed");
    var diff = Enumerable.Range(0, c.Primary.Length).Count(i => c.Primary[i] != r.Primary[i]);
    var back = Reparse(r, packName, "rename");
    var d2 = back is null ? null : PrefabDecoder.Decode(back);
    Check(d2?.Prefabs[src.Index].Name == src.Name + "_renamed", $"{packName}: rename not visible after re-parse");
    Console.WriteLine($"  rename '{src.Name}': records +{r.Records.Count - recs}, slots +{r.Slots.Count - slots}, primary bytes changed {diff}, {sw.ElapsedMilliseconds} ms");

    sw.Restart();
    var dup = c.Clone();
    var res = PrefabEdits.Duplicate(dup, src.Index, src.Name + "_copy");
    back = Reparse(dup, packName, "duplicate");
    if (back is null) return;
    var d3 = PrefabDecoder.Decode(back);
    var copy = d3.Prefabs[res.NewIndex];
    var orig = d3.Prefabs[src.Index];
    Check(copy.Name == src.Name + "_copy", $"{packName}: duplicate name '{copy.Name}'");
    Check(copy.Components.Count == orig.Components.Count && copy.Fields.Count == orig.Fields.Count
          && copy.Components.All(x => x.OwnerPrefab == res.NewIndex) && copy.Fields.All(x => x.OwnerPrefab == res.NewIndex),
          $"{packName}: duplicate graph differs from the original");
    Check(copy.Entities.Select(e => (e.Entity!.EntityPrefab, e.Entity.EntityPrefabClass))
              .SequenceEqual(orig.Entities.Select(e => (e.Entity!.EntityPrefab, e.Entity.EntityPrefabClass))),
          $"{packName}: duplicate entities point elsewhere");
    Check(d3.Prefabs.Where(p => p.Index != res.NewIndex).Select(p => p.Name).SequenceEqual(doc.Prefabs.Select(p => p.Name)),
          $"{packName}: other prefab names changed");
    Console.WriteLine($"  duplicate '{src.Name}' -> #{res.NewIndex}: records +{back.Records.Count - c.Records.Count} ({res.RecordsCopied} copied + 1 name element), " +
                      $"pieces {res.Pieces}, bytes {res.BytesCopied:N0}, slots +{back.Slots.Count - c.Slots.Count}; components {copy.Components.Count}, " +
                      $"entities {copy.Entities.Count()}, fields {copy.Fields.Count}; {sw.ElapsedMilliseconds} ms");
    ValueEdits(c, doc, packName);
}

// Value edits on the first candidates of the pack: a transform, a mesh name (pstring retarget), m_SelfActive (toggled
// in place, or inserted: the image grows by 16 and everything after moves), a float field value, and a child entity
// removal. Each is applied alone, validated, re-parsed and checked; every other prefab must decode as before.
void ValueEdits(PrefabContainer c, PrefabDocument doc, string packName)
{
    var cands = new List<PrefabEditOp>();
    foreach (var p in doc.Prefabs)
    {
        var unique = p.Components.GroupBy(x => x.Pcid).Where(g => g.Count() == 1).Select(g => g.First()).ToList();
        if (!cands.Any(o => o.Op == "transform") && unique.FirstOrDefault(x => x.Xform is not null) is { } t)
            cands.Add(new PrefabEditOp("transform", p.Name!, t.Pcid) { Translate = new Vec3(1, 2, 3) });
        if (!cands.Any(o => o.Op == "text") && unique.FirstOrDefault(x => x.Values?.Entries.Count(v => v.Key?.EndsWith("m_MeshName") == true && v.Form == PrefabValueForm.Text) == 1) is { } m)
            cands.Add(new PrefabEditOp("text", p.Name!, m.Pcid) { Field = "m_MeshName", Text = "nightrunner_edit_check.msh" });
        if (!cands.Any(o => o.Op == "active" && o.Active == true) && unique.FirstOrDefault(x => x.SelfActive == false) is { } a)
            cands.Add(new PrefabEditOp("active", p.Name!, a.Pcid) { Active = true });
        if (!cands.Any(o => o.Op == "active" && o.Active == false) && unique.FirstOrDefault(x => x.SelfActive is null && x.Values is { } b &&
                b.TotalSize == 8 + 16 * (b.Entries.Count + 1)) is { } ins)
            cands.Add(new PrefabEditOp("active", p.Name!, ins.Pcid) { Active = false });
        if (!cands.Any(o => o.Op == "scalar") && unique.FirstOrDefault(x => x.Values?.Entries.Count(v => v.Type == 0x09 && v.Form == PrefabValueForm.Scalar) > 0) is { } s)
            cands.Add(new PrefabEditOp("scalar", p.Name!, s.Pcid) { Field = s.Values!.Entries.First(v => v.Type == 0x09 && v.Form == PrefabValueForm.Scalar).Key, Value = 0.5 });
        if (!cands.Any(o => o.Op == "remove") && p.Interfaces.Count == 0 && p.PropertyCount == 0 && p.Components.Count > 1)
            if (unique.FirstOrDefault(x => x.Entity is not null && !p.Components.Any(y => y.Entity?.ParentPcid == x.Pcid || y.HierarchyParent == x.Pcid || y.XformComponent == x.Pcid)
                    && !p.PropertyBindings.Concat(p.PipeBindings).Any(b => b.SourcePcid == x.Pcid || b.TargetPcid == x.Pcid)
                    && !p.VirtualFields.Any(v => v.Destinations.Any(d => d.Pcid == x.Pcid)) && !p.Fields.Any(f => f.Destinations?.Any(d => d.Pcid == x.Pcid) == true)) is { } r)
                cands.Add(new PrefabEditOp("remove", p.Name!, r.Pcid));
        if (cands.Count == 6) break;
    }
    foreach (var op in cands)
    {
        var sw = Stopwatch.StartNew();
        var e = c.Clone();
        try { PrefabEdits.Apply(e, [op]); }
        catch (PrefabFormatException ex) { Check(false, $"{packName}: {ex.Message}"); continue; }
        var back = Reparse(e, packName, op.Op);
        if (back is null) continue;
        var d = PrefabDecoder.Decode(back);
        Check(PrefabEdits.Check(d, op) is null, $"{packName}: {op}: {PrefabEdits.Check(d, op)}");
        int others = 0;
        for (int i = 0; i < doc.Prefabs.Count; i++)
            if (doc.Prefabs[i].Name != op.Prefab && !doc.Prefabs[i].Components.Select(x => (x.Pcid, x.ClassId, x.Xform, x.Values?.Entries.Count, x.Entity?.PrefabName))
                    .SequenceEqual(d.Prefabs[i].Components.Select(x => (x.Pcid, x.ClassId, x.Xform, x.Values?.Entries.Count, x.Entity?.PrefabName))))
                others++;
        Check(others == 0, $"{packName}: {op}: {others} other prefab(s) decode differently");
        int changed = 0;
        for (int i = 0, n = Math.Min(c.Primary.Length, back.Primary.Length); i < n; i++) if (c.Primary[i] != back.Primary[i]) changed++;
        Console.WriteLine($"  {op.Op,-9} {op}: primary {back.Primary.Length - c.Primary.Length:+#;-#;0} B, bytes changed {changed:N0}, records {back.Records.Count - c.Records.Count:+#;-#;0}, " +
                          $"slots {back.Slots.Count - c.Slots.Count:+#;-#;0}, other prefabs unchanged {others == 0}; {sw.ElapsedMilliseconds} ms");
    }
}

void Show(PrefabRoot p)
{
    Console.WriteLine($"  #{p.Index} '{p.Name}' base {p.BaseClass} dom {p.DomFormat} flags 0x{p.Flags:X2}; interfaces {p.Interfaces.Count}, " +
                      $"properties {p.PropertyCount} (container {p.PropertyContainerSize}), pipes {p.PipesIn.Count}/{p.PipesOut.Count}, " +
                      $"bindings {p.PropertyBindings.Count}/{p.PipeBindings.Count}, virtual fields {p.VirtualFields.Count}");
    foreach (var f in p.Fields)
        Console.WriteLine($"    field {PrefabClasses.Name(f.ClassId)} '{f.Name}' {f.TypeName} init '{f.Init}' -> {f.TargetClass} " +
                          string.Join(", ", f.Destinations?.Select(d => $"{d.Field}@{d.Pcid}") ?? []));
    foreach (var x in p.Components)
    {
        Console.WriteLine($"    comp {x.ClassName} pcid {x.Pcid} flags 0x{x.Flags:X} {x.ComponentClass}" +
                          (x.Xform is { } xf ? $" T{xf.Translate} R{xf.Rotate} S{xf.Scale}" : "") + (x.HierarchyParent is { } hp ? $" hparent {hp}" : "") +
                          (x.Entity is { } e ? $" entity '{e.Name}' prefab '{e.PrefabName}' (#{e.EntityPrefab} {e.EntityPrefabClass}) presets '{e.PresetNames}' parent {e.ParentPcid}" : ""));
        foreach (var v in new[] { x.Values, x.ProxyValues }.OfType<PrefabValues>().SelectMany(b => b.Entries))
            Console.WriteLine($"      {(v.EngineKey ? "engine" : "field")} {v.Key} [{v.Type:X2}] {v.Form} {v.Text ?? v.Scalar?.ToString() ?? (v.Floats is null ? $"0x{v.Payload:X}" : string.Join(" ", v.Floats))}");
    }
    foreach (var b in p.PropertyBindings) Console.WriteLine($"    binding {b.SourcePcid}.{b.Source} -> {b.TargetPcid}.{b.Target}");
    foreach (var b in p.PipeBindings) Console.WriteLine($"    pipe {b.SourcePcid}.{b.Source} -> {b.TargetPcid}.{b.Target}");
    foreach (var v in p.VirtualFields)
        Console.WriteLine($"    virtual '{v.Name}' init '{v.Init}' -> {string.Join(", ", v.Destinations.Select(d => $"{d.Field}@{d.Pcid}"))}");
}

PrefabContainer? Reparse(PrefabContainer e, string packName, string what)
{
    try
    {
        e.Validate();
        var meta = e.EncodeMetadata();
        var back = PrefabContainer.Parse(e.EncodePrimary(), meta);
        back.Validate();
        Check(back.EncodeMetadata().AsSpan().SequenceEqual(meta), $"{packName}: {what} does not re-encode byte-exactly");
        return back;
    }
    catch (PrefabFormatException ex)
    {
        Check(false, $"{packName}: {what}: {ex.Message}");
        return null;
    }
}
