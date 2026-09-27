using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Nightrunner.Core.Backends;
using Nightrunner.Core.Games;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;
using Nightrunner.Core.Sdb;
using Nightrunner.Core.Texture;

/// <summary>
/// --materials: the viewport's albedo census. Every stock .model is resolved as the Model view does (ModelResolver, drawn
/// entry per slot, LOD 0 parts, base material after the join with the model's texture overrides) and every mesh a model
/// draws as the Mesh view does (its default skin, its own material names); each surface is classified by what the
/// viewport ends up with for colour: a texture, a composed recipe, hidden, or grey/white and why.
/// </summary>
static class MaterialCensus
{
    private static readonly DecodedImage Dummy = new(1, 1, [128, 128, 128, 255]);

    public static int Run(List<GameInstall> installs, string[] args)
    {
        int examples = args.Contains("--examples") ? 8 : 3;
        foreach (var install in installs)
        {
            var sw = Stopwatch.StartNew();
            var backend = GameBackends.For(install.Profile);
            string? sdbPath = backend.Sdb?.Files(install).OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
            using var sdb = sdbPath is null ? null : SdbFile.Open(sdbPath);
            if (args.Contains("--levels"))
            {
                Levels(install);
                continue;
            }
            var named = args.Select((a, i) => (a, i)).Where(t => t.a == "--material" && t.i + 1 < args.Length).Select(t => args[t.i + 1]).ToList();
            if (named.Count > 0)
            {
                using var cat = new RpackCatalog();
                cat.LoadAsync(install.Rpacks(includeCustom: false), install.Assets!, CancellationToken.None).GetAwaiter().GetResult();
                foreach (var n in named) Dump(sdb, cat, n, args);
                foreach (var n in args.Select((a, i) => (a, i)).Where(t => t.a == "--rows" && t.i + 1 < args.Length).Select(t => args[t.i + 1]))
                {
                    if (ViewerSurface.Open(cat, n, true, out var gt) is { } why) { Console.WriteLine($"{n}: {why}"); continue; }
                    var img = gt!.Decode(gt.TopLevel!.Value, rebuildNormalZ: false);
                    Console.WriteLine($"{n}: {img.Width}x{img.Height}");
                    for (int y = 0; y < img.Height; y++)
                        Console.WriteLine($"  row {y,3}: " + string.Join(" ", Enumerable.Range(0, 8).Select(k => k * (img.Width - 1) / 7)
                            .Select(x => { int o = (y * img.Width + x) * 4; return $"{img.Bgra[o + 2]:X2}{img.Bgra[o + 1]:X2}{img.Bgra[o]:X2}"; })));
                }
                continue;
            }
            using var rpacks = new RpackCatalog();
            rpacks.LoadAsync(install.Rpacks(includeCustom: false), install.Assets!, CancellationToken.None).GetAwaiter().GetResult();
            using var models = new ModelCatalog(install.Paks(includeCustom: false));
            var meshes = new ConcurrentDictionary<int, Lazy<MeshModel?>>();
            MeshModel Decode(int gid) => meshes.GetOrAdd(gid, g => new Lazy<MeshModel?>(() =>
            {
                var (e, i) = rpacks.Split(g);
                try { return backend.Meshes!.Decode(e.Pack!, i); }
                catch (Exception ex) when (ex is MeshFormatException or MeshUnsupportedException or RpackFormatException) { return null; }
            })).Value ?? throw new MeshFormatException("decode failed");
            int SkinOf(int gid, MeshModel m) => m.SkinRaw is { } raw && MeshSkins.Decode(raw) is { Error: null } k ? k.DefaultIndex : -1;

            var textureVerdicts = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var means = new ConcurrentDictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            string? Tex(string name) => textureVerdicts.GetOrAdd(name, n =>
            {
                var why = ViewerSurface.Open(rpacks, n, false, out var t);
                if (t is not null) means[n] = t.Header.Stats().Mean.Take(3).Average();
                return why;
            });
            float Bright(string name) => Tex(name) is null ? means.GetValueOrDefault(name) : 0;
            var layerVerdicts = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            string? Layer(string name) => layerVerdicts.GetOrAdd(name, n => ViewerSurface.Open(rpacks, n, true, out _));

            var model = new Tally();
            var mesh = new Tally();
            var drawnMeshes = new ConcurrentDictionary<int, byte>();
            var perModel = new ConcurrentDictionary<string, (int White, int All)>();
            var filter = args.SkipWhile(a => a != "--filter").Skip(1).FirstOrDefault() is { } f ? new Regex(f, RegexOptions.IgnoreCase) : null;
            var list = models.Models.Where(m => m.Wins && (filter is null || filter.IsMatch(m.Basename))).ToList();
            Parallel.ForEach(list, e =>
            {
                ModelDocument doc;
                try { doc = models.Load(e); }
                catch (Exception) { model.Add("model does not parse", e.Basename); return; }
                ResolvedModel res;
                try { res = ModelResolver.Resolve(doc, rpacks, sdb, Decode, SkinOf); }
                catch (Exception ex) { model.Add($"resolve threw {ex.GetType().Name}", e.Basename); return; }
                for (int s = 0; s < res.Slots.Count; s++)
                {
                    if (res.Slots[s].Meshes.FirstOrDefault(m => m.Drawn) is not { Found: true, Error: null } drawn) continue;
                    var m = Decode(drawn.Gids[0]);
                    drawnMeshes.TryAdd(drawn.Gids[0], 0);
                    foreach (int slot in LodSlots(m))
                    {
                        var sub = drawn.Submeshes.FirstOrDefault(x => x.Slot == slot);
                        string where = $"{e.Basename} [{res.Slots[s].Slot.Name}] {sub?.BaseMaterial}";
                        string verdict;
                        if (sub is null) verdict = "no submesh row";
                        else
                        {
                            var overrides = sub.Textures.Where(t => t.Source.StartsWith("model override", StringComparison.Ordinal))
                                .ToDictionary(t => t.Param, t => t.Texture);
                            verdict = Classify(sdb, sub.BaseMaterial, overrides, Tex, Layer, Bright);
                        }
                        model.Add(verdict, where);
                        perModel.AddOrUpdate(e.Basename, verdict.StartsWith("white") ? (1, 1) : (0, 1), (_, v) => (v.White + (verdict.StartsWith("white") ? 1 : 0), v.All + 1));
                    }
                }
            });
            Parallel.ForEach(drawnMeshes.Keys, gid =>
            {
                var m = Decode(gid);
                var table = m.FullMaterialTable();
                var skins = m.SkinRaw is { } raw && MeshSkins.Decode(raw) is { Error: null } k ? k : null;
                var map = skins?.MaterialsFor(skins.DefaultIndex, m.Materials.Length) ?? Enumerable.Range(0, m.Materials.Length).ToArray();
                foreach (int slot in LodSlots(m))
                {
                    string where = rpacks.Name(gid);
                    if (slot >= map.Length || map[slot] >= table.Length) { mesh.Add("slot outside the material table", where); continue; }
                    mesh.Add(Classify(sdb, table[map[slot]], null, Tex, Layer, Bright), $"{where} {table[map[slot]]}");
                }
            });
            Console.WriteLine($"\n== {install}\nsdb {Path.GetFileName(sdbPath)} · models {list.Count:N0} · meshes drawn {drawnMeshes.Count:N0} · " +
                              $"textures checked {textureVerdicts.Count:N0} · {sw.Elapsed.TotalSeconds:F1} s");
            model.Print("model surfaces (every model)", examples);
            mesh.Print("mesh surfaces (meshes the models draw, default skin)", examples);
            if (args.Contains("--examples"))
                foreach (var (k, v) in perModel.OrderByDescending(kv => kv.Value.White).Take(15))
                    Console.WriteLine($"  most white: {k} {v.White}/{v.All}");
        }
        return 0;
    }

    /// <summary>
    /// --levels: the level the viewport decodes (<see cref="ViewerSurface.PreviewLevel"/>) for every texture resource:
    /// the top level when it is at most 2048 wide, else the next mip down; counts what does not fit that rule.
    /// </summary>
    private static void Levels(GameInstall install)
    {
        var tally = new ConcurrentDictionary<string, ConcurrentBag<string>>();
        void Add(string k, string n) => tally.GetOrAdd(k, _ => []).Add(n);
        foreach (var path in install.Rpacks(includeCustom: false))
        {
            using var pack = RpackFile.Open(path);
            Parallel.ForEach(Enumerable.Range(0, pack.Count).Where(i => pack.Logicals[i].Type == 0x20), i =>
            {
                TextureResource t;
                try { t = TextureResource.Open(pack, i); }
                catch (Exception e) when (e is ImgcException or RpackFormatException) { Add("header refused", pack.Name(i)); return; }
                if (t.TopLevel is not { } top) { Add(t.Header.HeaderOnly ? "header-only" : "no levels", pack.Name(i)); return; }
                string kind = t.Header.Type.ToString();
                int big = Math.Max(top.Width, top.Height);
                var pv = ViewerSurface.PreviewLevel(t);
                if (pv is not { } p) { Add($"{kind}: no level ≤ 2048 (top {big}) → top decoded", pack.Name(i)); return; }
                int got = Math.Max(p.Width, p.Height), want = Math.Min(big, 2048);
                if (p.Face != 0) Add($"{kind}: preview not face 0", pack.Name(i));
                else if (got == big) Add($"{kind}: top level (≤ 2048)", "");
                else if (got * 2 >= want && got <= 2048) Add($"{kind}: next mip ≤ 2048 ({big} → {got})", "");
                else Add($"{kind}: skips to {got} from {big}", pack.Name(i));
            });
        }
        Console.WriteLine($"\n== {install} texture levels the viewport decodes");
        foreach (var (k, v) in tally.OrderBy(kv => kv.Key))
        {
            Console.WriteLine($"  {v.Count,8:N0}  {k}");
            foreach (var n in v.Where(x => x.Length > 0).Distinct().Take(3)) Console.WriteLine($"              {n}");
        }
    }

    /// <summary>--material NAME: everything the preview reads of one SDB material.</summary>
    private static void Dump(SdbFile? sdb, RpackCatalog catalog, string name, string[] args)
    {
        var (m, problem) = ViewerSurface.Resolve(sdb, name);
        Console.WriteLine($"\n## {name}: {problem ?? m!.Route?.Preset}");
        if (m is null) return;
        foreach (var r in m.Routes)
        {
            Console.WriteLine($"  tokens {r.Tokens}");
            foreach (var p in r.Parameters) Console.WriteLine($"  param {p.Name} type {p.Type} = {p.ValueText}");
            foreach (var line in r.Variants.SelectMany(v => v.Bindings).Select(b => $"  bind {b.Parameter} = {b.Texture ?? $"<runtime {b.RuntimeIndex}>"}{(b.Overridden ? "" : " (default)")}").Distinct())
                Console.WriteLine(line);
            if (args.Contains("--preset") && r.PresetIndices.Count == 1)
                foreach (var p in sdb!.Preset(r.PresetIndices[0]).Parameters)
                    Console.WriteLine($"  preset {p.Name} type {p.Type} expr '{p.Expression}' ann '{p.Annotation}' default {p.DefaultText}");
        }
        foreach (var t in m.Textures)
        {
            string? why = ViewerSurface.Open(catalog, t, false, out var tex);
            Console.WriteLine($"  tex {t}: {why ?? $"{tex!.Header.FormatName} {tex.TopLevel!.Value.Width}x{tex.TopLevel!.Value.Height} mean {string.Join(",", tex.Header.Stats().Mean.Select(x => x.ToString("F2")))}"}");
        }
    }

    /// <summary>The material slots the viewport's LOD-0 parts use (MeshScenes.Parts: submeshes with 3+ indices).</summary>
    private static IEnumerable<int> LodSlots(MeshModel m) =>
        m.GeometryEntries.Where(e => e.Element == 0 && e.Vertices is not null)
            .SelectMany(e => e.Submeshes).Where(s => s.Indices.Length >= 3).Select(s => s.MaterialSlot).Distinct();

    /// <summary>
    /// What colour the viewport gives one surface, as a verdict: "ok: …", "hidden", or "white: …" (grey in the viewer)
    /// with the reason. <paramref name="albedo"/> says why a plain texture cannot be shown (null: it can);
    /// <paramref name="layer"/> the same for a layer a recipe decodes.
    /// </summary>
    public static string Classify(SdbFile? sdb, string material, IReadOnlyDictionary<string, string>? overrides,
                                  Func<string, string?> albedo, Func<string, string?> layer, Func<string, float>? bright = null)
    {
        var (mat, problem) = ViewerSurface.Resolve(sdb, material);
        var surface = MaterialPreview.Compose(mat, overrides, t => layer(t) is null ? Dummy : null);
        if (surface.Hidden) return "hidden: non-rendering";
        if (surface.Image is not null) return $"ok: {surface.Recipe}";
        if (surface.PlainTexture is { } plain)
        {
            if (albedo(plain) is { } why) return "white: albedo " + Regex.Replace(why, @"'[^']*'", "'…'");
            var left = surface.Warnings.Where(w => w.EndsWith("not applied")).Select(w => w.Split(' ')[0] switch
            {
                "dye" => "dye", "gradient" => "gradient over dif_0_tex", _ => w,
            }).Distinct().ToList();
            return "ok: plain" + (surface.Tint is not null ? " ×dif_0_val" : "") +
                   (left.Count > 0 ? $" ({string.Join(", ", left)} not applied)" : "") +
                   (bright?.Invoke(plain) >= 0.85f ? " (albedo mean ≥ 0.85)" : "");
        }
        if (mat is null) return $"white: {problem}";
        string gradient = surface.Warnings.FirstOrDefault(w => w.StartsWith("gradient map: ") && !w.Contains("base row") && !w.Contains("row offsets")) is { } g
            ? $" [{Regex.Replace(g, @"_\d+[a-z_]*\.(png|dds)", "…")}]" : "";
        return "white: " + MaterialPreview.NoColour(mat, MaterialPreview.Facts(mat, overrides).Textures) + gradient;
    }

    private sealed class Tally
    {
        private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _by = new();
        public void Add(string verdict, string where) => _by.GetOrAdd(verdict, _ => []).Add(where);

        public void Print(string title, int examples)
        {
            long total = _by.Values.Sum(v => v.Count), white = _by.Where(k => k.Key.StartsWith("white")).Sum(k => k.Value.Count);
            Console.WriteLine($"-- {title}: {total:N0} surfaces, white {white:N0} ({(total == 0 ? 0 : 100.0 * white / total):F1}%)");
            foreach (var (k, v) in _by.OrderByDescending(kv => kv.Value.Count))
            {
                Console.WriteLine($"  {v.Count,8:N0}  {k}");
                if (k.StartsWith("white") || k.Contains("not applied") || k.Contains("mean"))
                    foreach (var w in v.Distinct().Order().Take(examples)) Console.WriteLine($"              {w}");
            }
        }
    }
}
