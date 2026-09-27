using System.Diagnostics;
using Nightrunner.Core.Games;
using Nightrunner.Core.Rpack;
using Nightrunner.Core.Texture;

// Reader self-check: index every pack of an install, assert the table invariants, time the search and a
// random-access read. Skips (exit 0) when no install is present.
// Usage: dotnet run -- [game root] [--game dltb|dl2] [--bc6h]
//   --bc6h  every BC6H texture, every level: own one-region decoder against the library, and decode -> BC6H encode ->
//           decode drift (log2 and linear), bit-identical blocks, time.

string? Option(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
var root = args.FirstOrDefault(a => !a.StartsWith("--") && a != Option("--game"));
var install = GameInstall.Find(root, Option("--game"));
if (install is null)
{
    Console.WriteLine("skip: no Chrome Engine install found (pass a game root, or set NIGHTRUNNER_GAME_ROOT)");
    return 0;
}

var rpacks = install.Rpacks();
Console.WriteLine($"{install}\n{rpacks.Length} packs, {rpacks.Sum(p => new FileInfo(p).Length):N0} bytes on disk");
if (rpacks.Length == 0)
{
    Console.WriteLine("skip: install has no .rpack files");
    return 0;
}

int failures = 0;
void Check(bool ok, string what)
{
    if (ok) return;
    failures++;
    Console.WriteLine($"FAIL {what}");
}

var sw = Stopwatch.StartNew();
using var catalog = new RpackCatalog();
await catalog.LoadAsync(rpacks, install.Assets);
long indexMs = sw.ElapsedMilliseconds;
Console.WriteLine($"indexed {catalog.ResourceCount:N0} resources from {catalog.IndexedPacks.Length} packs " +
                  $"in {indexMs} ms, working set {Environment.WorkingSet / (1024 * 1024)} MB");

foreach (var e in catalog.Packs.Where(p => p.Error is not null))
    Console.WriteLine($"  unreadable: {e.Label}: {e.Error}");

// every logical resource: parts in range, offsets inside the file, name resolvable
long parts = 0, payload = 0, unreadable = 0;
foreach (var entry in catalog.IndexedPacks)
{
    var pack = entry.Pack!;
    Check(pack.Header.LogicalCount == (uint)pack.Count, $"{entry.Label}: logical count");
    for (int i = 0; i < pack.Count; i++)
    {
        var lg = pack.Logicals[i];
        Check(pack.NameLength[i] > 0, $"{entry.Label} #{i}: empty name");
        for (int k = 0; k < lg.PartCount; k++)
        {
            int pi = (int)lg.FirstPart + k;
            parts++;
            long off = pack.PartOffset(pi);
            long size = pack.Physicals[pi].Size;
            payload += size;
            if (pack.PartIsDirect(pi)) Check(off + size <= pack.Length, $"{entry.Label} part {pi}: span past EOF");
            else unreadable++;
        }
    }
}
Console.WriteLine($"{parts:N0} parts, {payload:N0} payload bytes, {unreadable:N0} not directly readable " +
                  "(compressed or child-pack)");

// search: every query must stay under a few ms and return only matching names
foreach (var q in new[] { "", "player", "head", "aiden torso", "#0" })
{
    var r = catalog.Search(q);
    Console.WriteLine($"  search {q,-14} {r.Count,8:N0} hits  {r.Elapsed.TotalMilliseconds,6:F1} ms");
    var words = RpackCatalog.ParseQuery(q).Words;
    foreach (int gid in r.Gids.Take(200))
    {
        string name = catalog.Name(gid).ToLowerInvariant();
        foreach (var w in words)
            Check(name.Contains(System.Text.Encoding.UTF8.GetString(w)), $"search '{q}': {name} does not match");
    }
}

// random-access reads out of the biggest pack — the point of memory mapping
var big = catalog.IndexedPacks.MaxBy(p => p.FileSize)!;
var bp = big.Pack!;
var rng = new Random(1);
sw.Restart();
int reads = 0;
long got = 0;
for (int i = 0; i < 2000; i++)
{
    int li = rng.Next(bp.Count);
    int pi = (int)bp.Logicals[li].FirstPart;
    if (bp.PartUnreadableReason(pi) is not null) continue;
    got += bp.ReadPart(pi, 0, 4096).Length;
    reads++;
}
Console.WriteLine($"{big.Label}: {reads} random reads, {got:N0} bytes, {sw.ElapsedMilliseconds} ms");

// textures: parse every IMGC header, decode a sample of each format
var byFormat = new Dictionary<string, (int Seen, int Decoded, int Refused, int Failed, string? Last)>();
int headers = 0, headerErrors = 0;
foreach (var entry in catalog.IndexedPacks)
{
    var pack = entry.Pack!;
    for (int i = 0; i < pack.Count; i++)
    {
        if (pack.Logicals[i].Type != 0x20) continue;
        TextureResource tex;
        try
        {
            tex = TextureResource.Open(pack, i);
            headers++;
        }
        catch (Exception e)
        {
            headerErrors++;
            if (headerErrors <= 5) Console.WriteLine($"  header: {pack.Name(i)}: {e.Message}");
            continue;
        }
        string fmt = tex.Header.FormatName;
        var row = byFormat.GetValueOrDefault(fmt);
        row.Seen++;
        // decode at most 3 per format, and only the top level
        if (row.Decoded + row.Refused + row.Failed < 3 && tex.HasBitmap && tex.TopLevel is { } top)
        {
            if (!TextureDecoder.CanDecode(tex.Header.Format)) row.Refused++;
            else
            {
                try
                {
                    var img = tex.Decode(top);
                    if (img.Bgra.Length != img.Width * img.Height * 4) throw new Exception("wrong buffer size");
                    row.Decoded++;
                }
                catch (Exception e)
                {
                    row.Failed++;
                    row.Last = e.Message;
                }
            }
        }
        byFormat[fmt] = row;
    }
}
Console.WriteLine();
Console.WriteLine($"textures: {headers:N0} IMGC headers parsed, {headerErrors} unreadable");
foreach (var (fmt, r) in byFormat.OrderByDescending(kv => kv.Value.Seen))
{
    Console.WriteLine($"  {fmt,-14} {r.Seen,7:N0} textures   sampled: {r.Decoded} decoded, {r.Refused} refused, " +
                      $"{r.Failed} failed{(r.Last is null ? "" : " — " + r.Last)}");
    Check(r.Failed == 0, $"texture decode {fmt}: {r.Last}");
}
Check(headerErrors == 0, $"{headerErrors} IMGC headers unreadable");

// re-encode: decode a texture, write it back in its own format, decode again, and measure the drift
Console.WriteLine();
Console.WriteLine("re-encode (mean absolute error per channel, 0-255):");
foreach (var want in new[] { "BC1", "BC4", "BC4_SNORM", "BC5", "BC5_SNORM", "BC7", "RGBA8" })
{
    int done = 0;
    foreach (var entry in catalog.IndexedPacks)
    {
        if (done >= 2) break;
        var pack = entry.Pack!;
        for (int i = 0; i < pack.Count && done < 2; i++)
        {
            if (pack.Logicals[i].Type != 0x20) continue;
            TextureResource tex;
            try { tex = TextureResource.Open(pack, i); }
            catch (Exception) { continue; }
            if (tex.Header.FormatName != want || !tex.HasBitmap || tex.TopLevel is not { } top) continue;
            if (top.Width < 64 || top.Width > 1024) continue;       // keep the check quick
            if (!ImgcEncoder.CanEncode(tex.Header.Format)) continue;

            try
            {
                var before = tex.Decode(top, rebuildNormalZ: false);
                var encoded = ImgcEncoder.Encode(tex.Header, before.Bgra, before.Width, before.Height, 1);
                var after = TextureDecoder.Decode(tex.Header.Format, encoded.Bitmap, before.Width, before.Height,
                                                  rebuildNormalZ: false);
                string channels = ImgcFormats.Get(tex.Header.Format).Channels;
                double error = 0;
                long samples = 0;
                for (int px = 0; px < before.Bgra.Length; px += 4)
                {
                    foreach (char c in channels)
                    {
                        int at = c switch { 'B' => 0, 'G' => 1, 'R' => 2, 'A' => 3, _ => -1 };
                        if (at < 0) continue;
                        error += Math.Abs(before.Bgra[px + at] - after.Bgra[px + at]);
                        samples++;
                    }
                }
                double mae = samples == 0 ? 0 : error / samples;
                Console.WriteLine($"  {want,-11} {pack.Name(i),-44} {before.Width}x{before.Height}  " +
                                  $"mae {mae,5:F2}  ({encoded.Bitmap.Length:N0} B)");
                Check(mae < 12, $"re-encode {want}: mean error {mae:F2} is too high");
                done++;
            }
            catch (Exception e)
            {
                Console.WriteLine($"  {want,-11} {pack.Name(i)}: {e.Message}");
                Check(false, $"re-encode {want}: {e.Message}");
                done++;
            }
        }
    }
    if (done == 0) Console.WriteLine($"  {want,-11} no sample found");
}

// BC6H: HDR, so the drift is measured on linear floats (log2 of value + 2^-10, and absolute)
Console.WriteLine();
Console.WriteLine(args.Contains("--bc6h") ? "BC6H census (every texture, every level):" : "BC6H re-encode (sample):");
{
    var hdr = new List<TextureResource>();
    foreach (var entry in catalog.IndexedPacks)
    {
        var pack = entry.Pack!;
        for (int i = 0; i < pack.Count; i++)
        {
            if (pack.Logicals[i].Type != 0x20) continue;
            try
            {
                var tex = TextureResource.Open(pack, i);
                if (tex.Header.FormatName.StartsWith("BC6H") && tex.HasBitmap) hdr.Add(tex);
            }
            catch (ImgcException) { }
        }
    }
    var sample = args.Contains("--bc6h") ? hdr : hdr.Where(t => t.Header.Width <= 1024).Take(4).ToList();
    long blocks = 0, same = 0, mismatched = 0, twoRegion = 0, n = 0, ms = 0;
    double logSe = 0, logMax = 0, absSe = 0, absMax = 0;
    var modes = new long[Bc6h.OneRegion.Length];
    Span<ushort> own = stackalloc ushort[48];
    var clock = new Stopwatch();
    foreach (var tex in sample)
    {
        bool signed = tex.Header.FormatName == "BC6H_SF16";
        double texLog = 0;
        long texN = 0;
        foreach (var l in args.Contains("--bc6h") ? tex.Levels : tex.Levels.Take(1))
        {
            var raw = tex.ReadLevel(l);
            var src = TextureDecoder.DecodeFloat(tex.Header.Format, raw, l.Width, l.Height);
            int bw = (l.Width + 3) / 4;
            for (int b = 0; b < raw.Length / 16; b++)
            {
                var block = raw.AsSpan(b * 16, 16);
                int mode = Bc6h.ModeOf(block);
                if (mode < 0 || !Bc6h.TryDecodeBlock(block, signed, own)) { twoRegion++; continue; }
                modes[mode]++;
                for (int p = 0; p < 16; p++)
                {
                    int x = b % bw * 4 + p % 4, y = b / bw * 4 + p / 4;
                    if (x >= l.Width || y >= l.Height) continue;
                    for (int c = 0; c < 3; c++)
                    {
                        ushort want = BitConverter.HalfToUInt16Bits((Half)src.Rgba[(y * l.Width + x) * 4 + c]);
                        if (want != own[p * 3 + c] && !(want == 0 && own[p * 3 + c] == 0x8000)) { mismatched++; p = 16; break; }
                    }
                }
            }
            clock.Restart();
            var enc = Bc6h.Encode(src, signed);
            ms += clock.ElapsedMilliseconds;
            var back = TextureDecoder.DecodeFloat(tex.Header.Format, enc, l.Width, l.Height);
            for (int b = 0; b < raw.Length; b += 16)
            {
                blocks++;
                if (raw.AsSpan(b, 16).SequenceEqual(enc.AsSpan(b, 16))) same++;
            }
            for (int i = 0; i < src.Rgba.Length; i++)
            {
                if ((i & 3) == 3) continue;
                double a = src.Rgba[i], z = back.Rgba[i];
                double lg = Math.Log2(Math.Abs(a) + 1.0 / 1024) - Math.Log2(Math.Abs(z) + 1.0 / 1024);
                logSe += lg * lg;
                texLog += lg * lg;
                logMax = Math.Max(logMax, Math.Abs(lg));
                absSe += (a - z) * (a - z);
                absMax = Math.Max(absMax, Math.Abs(a - z));
                n++;
                texN++;
            }
        }
        if (!args.Contains("--bc6h"))
            Console.WriteLine($"  {tex.Name,-52} {tex.Header.Width}x{tex.Header.Height}  log2 rmse {Math.Sqrt(texLog / texN):F5}");
    }
    if (sample.Count == 0) Console.WriteLine("  no BC6H texture found");
    else
    {
        Console.WriteLine($"  {sample.Count:N0} textures, {blocks:N0} blocks: modes " +
                          string.Join(" ", Bc6h.OneRegion.Select((m, i) => $"{m} {modes[i]:N0}")) + $", two-region {twoRegion:N0}");
        Console.WriteLine($"  own decoder vs library: {mismatched:N0} blocks differ");
        Console.WriteLine($"  re-encode: {same * 100.0 / blocks:F1}% blocks bit-identical, log2 rmse {Math.Sqrt(logSe / n):G3} " +
                          $"max {logMax:F4}, linear rmse {Math.Sqrt(absSe / n):G3} max {absMax:G3}, encode {ms:N0} ms");
        Check(mismatched == 0, "BC6H: own decoder differs from the library");
        Check(Math.Sqrt(logSe / n) < 0.01, $"BC6H re-encode drift {Math.Sqrt(logSe / n):F4} (log2 rms)");
    }
}

Console.WriteLine(failures == 0 ? "OK" : $"{failures} FAILURES");
return failures == 0 ? 0 : 1;
