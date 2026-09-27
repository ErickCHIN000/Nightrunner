using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using Nightrunner.Core.Games;
using Nightrunner.Core.Model;
using Nightrunner.Core.Prefab;
using Nightrunner.Core.Rpack;

/// <summary>
/// Text prefab census (<c>--text</c>): every <c>*.prefab</c> member of the paks read with <see cref="PrefabTextReader"/>
/// (in parallel, one zip reader per thread), per format, with the fraction of DOM members read into model members, the
/// members that do not read (by name), sub-prefabs, and the relation to the binary prefabs of the same name: which
/// names both provide, and whether the two decodes agree (pcid-matched components, entity fields, transforms, values).
/// Then the catalog as the UI builds it (time, memory, shadowing) and a placement census.
/// </summary>
static class TextCheck
{
    public static int Run(GameInstall install, Func<bool, string, bool> check, bool place, bool text = true)
    {
        if (!text)
        {
            Placement(install);
            return 0;
        }
        var sw = Stopwatch.StartNew();
        var bin = new Dictionary<string, PrefabRoot>(StringComparer.Ordinal);
        foreach (var path in install.Rpacks())
        {
            using var pack = RpackFile.Open(path);
            foreach (int li in PrefabContainer.ResourcesIn(pack))
            {
                PrefabDocument doc;
                try { doc = PrefabDecoder.Decode(PrefabContainer.Read(pack, li)); }
                catch (PrefabFormatException) { continue; }
                foreach (var r in doc.Prefabs) if (r.Name is not null) bin.TryAdd(r.Name, r);
            }
        }
        Console.WriteLine($"\ntext prefabs: binary names {bin.Count:N0} ({sw.ElapsedMilliseconds} ms)");
        var stats = new ConcurrentDictionary<string, long>();
        void S(string k, long n = 1) => stats.AddOrUpdate(k, n, (_, v) => v + n);
        var errors = new ConcurrentDictionary<string, string>();
        var subNames = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var textNames = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        long total = 0, typed = 0, bytes = 0;
        foreach (var p in install.Paks())
        {
            var members = new List<(int Index, string Name, long Size)>();
            using (var z = ZipFile.OpenRead(p))
                for (int i = 0; i < z.Entries.Count; i++)
                    if (z.Entries[i].FullName.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) members.Add((i, z.Entries[i].FullName, z.Entries[i].Length));
            var local = new ThreadLocal<ZipArchive>(() => ZipFile.OpenRead(p), true);
            var psw = Stopwatch.StartNew();
            Parallel.ForEach(members, m =>
            {
                byte[] buf;
                using (var s = local.Value!.Entries[m.Index].Open()) { buf = new byte[m.Size]; s.ReadExactly(buf); }
                Interlocked.Add(ref bytes, buf.Length);
                string name = PrefabCatalog.NameOf(m.Name);
                textNames.TryAdd(name, 0);
                var fmt = PrefabTextReader.Detect(buf);
                S($"format {fmt}");
                PrefabDocument doc;
                try { doc = PrefabTextReader.Read(buf, name, Path.GetFileName(p), m.Name); }
                catch (PrefabFormatException e)
                {
                    errors[m.Name] = e.Message;
                    S($"refused {fmt}");
                    return;
                }
                S($"read {fmt}");
                S($"version {doc.TextSource!.Version?.ToString() ?? "none"}");
                Interlocked.Add(ref total, doc.Coverage.Total);
                Interlocked.Add(ref typed, doc.Coverage.Typed);
                S("components", doc.Prefabs.Sum(x => x.Components.Count));
                S("entities", doc.Prefabs.Sum(x => x.Entities.Count()));
                S("values", doc.Prefabs.Sum(x => x.Components.Sum(c => c.Values?.Entries.Count ?? 0)));
                S("native fields", doc.Prefabs.Sum(x => x.Components.Sum(c => c.NativeFields.Count)));
                if (doc.Prefabs.Count > 1) S("files with sub-prefabs");
                foreach (var sp in doc.Prefabs.Skip(1))
                {
                    subNames.TryAdd(sp.Name!, 0);
                    S(bin.ContainsKey(sp.Name!) ? "sub-prefabs also binary" : "sub-prefabs text only");
                }
                if (bin.TryGetValue(name, out var b)) Compare(b, doc.Prefabs[0], S);
            });
            foreach (var z in local.Values) z.Dispose();
            Console.WriteLine($"  {Path.GetFileName(p)}: {members.Count:N0} members, {psw.ElapsedMilliseconds} ms");
        }
        int both = textNames.Keys.Count(bin.ContainsKey);
        Console.WriteLine($"  {textNames.Count:N0} text members ({bytes:N0} B inflated) in {sw.ElapsedMilliseconds} ms; members read into model members {typed:N0}/{total:N0} ({(double)typed / Math.Max(1, total):P2}), the rest kept as DOM text");
        Console.WriteLine($"  names: text and binary {both:N0}, text only {textNames.Count - both:N0}, binary only {bin.Keys.Count(k => !textNames.ContainsKey(k) && !subNames.ContainsKey(k)):N0} (not even a sub-prefab)");
        foreach (var (k, v) in stats.OrderBy(kv => kv.Key)) Console.WriteLine($"  {k}: {v:N0}");
        foreach (var (k, v) in errors.OrderBy(kv => kv.Key).Take(20)) Console.WriteLine($"  refused {k}: {v}");
        check(stats.GetValueOrDefault("xform differs") == 0, "text and binary transforms differ");
        check(stats.GetValueOrDefault("entity parent differs") == 0, "text and binary entity parents differ");
        if (place) Placement(install);
        return errors.Count;
    }

    /// <summary>Binary and text decodes of one name, component by pcid.</summary>
    private static void Compare(PrefabRoot b, PrefabRoot t, Action<string, long> S)
    {
        S("compared prefabs", 1);
        if (b.Components.Count != t.Components.Count) S("component count differs", 1);
        var bp = b.Components.GroupBy(c => c.Pcid).ToDictionary(g => g.Key, g => g.First());
        foreach (var tc in t.Components)
        {
            if (!bp.TryGetValue(tc.Pcid, out var bc)) { S("text component not in binary (pcid)", 1); continue; }
            S("components matched by pcid", 1);
            if (bc.ComponentClass != tc.ComponentClass) S("component class differs", 1);
            if (tc.Entity is { } te && bc.Entity is { } be)
            {
                S("entities matched", 1);
                if (te.PrefabName != be.PrefabName) S("entity prefab differs", 1);
                if ((te.Name ?? "") != (be.Name ?? "")) S("entity name differs", 1);
                if ((te.PresetNames ?? "") != (be.PresetNames ?? "")) S("entity presets differ", 1);
                if ((te.Configurations ?? "") != (be.Configurations ?? "")) S("entity configurations differ", 1);
                if (te.ParentPcid != be.ParentPcid) S("entity parent differs", 1);
                if (te.ExtentsA.Length == 6)
                    S(te.ExtentsA.Zip(be.ExtentsA).All(z => Math.Abs(z.First - z.Second) < 1e-3) ? "entity extents = binary +0xD0"
                      : te.ExtentsA.Zip(be.ExtentsB).All(z => Math.Abs(z.First - z.Second) < 1e-3) ? "entity extents = binary +0xE8" : "entity extents differ", 1);
            }
            if (tc.Xform is { } tx)
            {
                static bool Eq(Vec3 a, Vec3 c, double tol) => Math.Abs(a.X - c.X) < tol && Math.Abs(a.Y - c.Y) < tol && Math.Abs(a.Z - c.Z) < tol;
                S(bc.Xform is { } bx && Eq(tx.Translate, bx.Translate, 1e-3) && Eq(tx.Rotate, bx.Rotate, 1e-2) && Eq(tx.Scale, bx.Scale, 1e-4)
                  ? "xform same" : "xform differs", 1);
            }
            if (tc.HierarchyParent is { } hp) S(bc.HierarchyParent == hp ? "hierarchy parent same" : "hierarchy parent differs", 1);
            if (tc.XformComponent is { } xc) S(bc.XformComponent == xc ? "xform component same" : "xform component differs", 1);
            foreach (var v in tc.Values?.Entries ?? [])
            {
                var bv = bc.Values?.Entries.FirstOrDefault(x => x.Key is { } k && (k == v.Key || k.EndsWith("::" + v.Key, StringComparison.Ordinal)));
                if (bv is null) { S("value key not in binary", 1); continue; }
                S("values matched by key", 1);
                if (bv.Text is not null) S(bv.Text == v.Text ? "text value same" : "text value differs", 1);
            }
        }
    }

    /// <summary>The catalog as the UI builds it, and every winning prefab placed (text members read on the way).</summary>
    private static void Placement(GameInstall install)
    {
        var rc = new RpackCatalog();
        rc.LoadAsync(install.Rpacks(), install.Assets).GetAwaiter().GetResult();
        using var paks = new ModelCatalog(install.Paks());
        GC.Collect();
        long mem0 = GC.GetTotalMemory(true);
        var sw = Stopwatch.StartNew();
        var cat = PrefabCatalog.Build(rc, paks);
        long ms = sw.ElapsedMilliseconds;
        long mem1 = GC.GetTotalMemory(true);
        Console.WriteLine($"\ncatalog: {cat.Prefabs.Count:N0} entries ({cat.BinaryCount:N0} binary, {cat.TextCount:N0} text) in {ms} ms, +{(mem1 - mem0) / 1e6:F0} MB; " +
                          $"shadowed {cat.Prefabs.Count(p => !p.Wins):N0} (text by binary {cat.Prefabs.Count(p => !p.Wins && p.Source == PrefabSource.Pak && p.ShadowedBy?.Source == PrefabSource.Rpack):N0}), pack errors {cat.Errors.Count}");
        sw.Restart();
        long meshes = 0, found = 0, nomesh = 0, failed = 0, withMeshes = 0;
        var winners = cat.Prefabs.Where(p => p.Wins && p.Source == PrefabSource.Rpack).ToList();
        foreach (var e in winners)
        {
            List<PrefabMesh> placed;
            try { placed = PrefabPlacement.Meshes(cat, e); }
            catch (PrefabFormatException) { failed++; continue; }
            if (placed.Count > 0) withMeshes++;
            meshes += placed.Count;
            nomesh += placed.Count(m => m.Mesh.Equals("nomesh", StringComparison.OrdinalIgnoreCase));
            found += placed.Count(m => rc.Lookup(m.Mesh, 0x10).Length > 0);
        }
        Console.WriteLine($"placement (winning binary prefabs): {winners.Count:N0} prefabs, {withMeshes:N0} with meshes, {meshes:N0} meshes " +
                          $"({found:N0} in a loaded pack, {nomesh:N0} still 'nomesh'), {failed} failed; {sw.ElapsedMilliseconds} ms; text members read {cat.Prefabs.Count(p => p.Source == PrefabSource.Pak && p.IsLoaded):N0}");
    }
}
