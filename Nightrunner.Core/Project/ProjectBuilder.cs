using System.Buffers.Binary;
using System.Text;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Rpack;
using Nightrunner.Core.Texture;

namespace Nightrunner.Core.Project;

/// <summary>What one build wrote and what reading it back found.</summary>
public sealed record ProjectBuildResult(string PackPath, BuildReport Report, IReadOnlyList<string> Built,
                                        IReadOnlyList<string> Skipped, string Verdict, bool Verified);

/// <summary>
/// A project's textures encoded as resource specs: a line per built and per skipped texture, the source pack of each
/// spec (for the pack's field08), and warnings.
/// </summary>
public sealed record TextureSpecSet(List<ResourceSpec> Specs, List<string> Built, List<string> Skipped,
                                    List<string> SourcePacks, List<string> Warnings)
{
    public void Deconstruct(out List<ResourceSpec> specs, out List<string> built, out List<string> skipped) =>
        (specs, built, skipped) = (Specs, Built, Skipped);
}

/// <summary>
/// Turns a project folder into a pack. Everything is resolved from the project handed in — its folder, its
/// textures and its <see cref="ModProject.BuildFolder"/> — so a build can never land in another project's folder.
/// </summary>
public static class ProjectBuilder
{
    public const string DefaultPackName = "assets_2_pc.rpack";

    /// <summary>Normalise a pack file name: default when blank, <c>.rpack</c> appended when missing.</summary>
    public static string PackName(string? name)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0) return DefaultPackName;
        return name.EndsWith(".rpack", StringComparison.OrdinalIgnoreCase) ? name : name + ".rpack";
    }

    /// <summary>
    /// Build every buildable texture of the project into <c>&lt;BuildFolder&gt;/&lt;pack&gt;</c>, then reopen the pack and
    /// read every part back. <paramref name="loadPng"/> decodes a PNG to BGRA32 (the UI's WPF decoder; tests supply
    /// their own). <paramref name="catalog"/> (the open game) gives the source packs' field08 (<see cref="PackField08"/>).
    /// </summary>
    public static ProjectBuildResult BuildTextures(ModProject project, string? packName,
                                                   Func<string, (byte[] Bgra, int Width, int Height)> loadPng,
                                                   CancellationToken ct = default, RpackCatalog? catalog = null)
    {
        string outDir = project.BuildFolder;
        string target = Path.Combine(outDir, PackName(packName));
        if (ProjectBuild.RpackRefusal(PackName(packName), null) is { } refusal) throw new RpackBuildException(refusal);
        var set = TextureSpecs(project, loadPng, ct);
        if (set.Specs.Count == 0) throw new RpackBuildException("nothing to build");
        Directory.CreateDirectory(outDir);
        var report = RpackWriter.Write(target, set.Specs, PackField08(false, set.SourcePacks.Select(s => SourceField08(catalog, s))));
        var (verdict, ok) = Verify(target, set.Specs.Count);
        return new ProjectBuildResult(target, report, set.Built, [.. set.Skipped, .. set.Warnings], verdict, ok);
    }

    /// <summary>
    /// The prototype's field08 for a project pack (<c>project.py</c>, <see cref="RpackWriter.ProjectField08"/>): 0x1000
    /// with a mesh, else the source packs' value when they all agree, else 0x1000. A source not in the open game counts
    /// as disagreeing.
    /// </summary>
    public static uint PackField08(bool hasMesh, IEnumerable<uint?> sourceField08s) =>
        RpackWriter.ProjectField08(null, hasMesh, sourceField08s.Select(f => f ?? RpackFormat.Field08OnDemand));

    /// <summary>The field08 of a pack in the open game, by label (or file name), or null.</summary>
    public static uint? SourceField08(RpackCatalog? catalog, string label) =>
        catalog?.Packs.FirstOrDefault(p => p.Label.Replace('\\', '/').Equals(label.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                                           || Path.GetFileName(p.Path).Equals(Path.GetFileName(label), StringComparison.OrdinalIgnoreCase))
            ?.Pack?.Header.Field08;

    /// <summary>The project's buildable textures encoded as resource specs, with a line per built and per skipped texture.</summary>
    public static TextureSpecSet TextureSpecs(ModProject project, Func<string, (byte[] Bgra, int Width, int Height)> loadPng,
                                              CancellationToken ct = default)
    {
        var (ready, _) = TextureAsset.Scan(project.Folder);
        var built = new List<string>();
        var skipped = new List<string>();
        var specs = new List<ResourceSpec>();
        var sourcePacks = new List<string>();
        var warnings = new List<string>();
        var sources = ready.GroupBy(r => r.Asset.Resource, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (source, asset) in ready)
        {
            ct.ThrowIfCancellationRequested();
            string file = Path.GetFileName(source);
            if (sources[asset.Resource] > 1)
            {
                skipped.Add($"{asset.Resource}: {sources[asset.Resource]} source files describe it ({file} is one); keep one");
                continue;
            }
            if (TextureSource.Refusal(source, asset) is { } why)
            {
                skipped.Add($"{asset.Resource}: {why}");
                continue;
            }
            var encoded = TextureSource.Encode(source, asset, loadPng,
                                               warning => Log.Warn("build", $"{asset.Resource} ({file}): {warning}"));
            if (encoded.Width != asset.Width || encoded.Height != asset.Height || encoded.Mips != asset.Mips || asset.EffectiveFormat != asset.Format)
            {
                // resized (or re-formatted): the header words that describe the old texels are brought up to date
                string change = $"{asset.Width}x{asset.Height} {asset.FormatName} {asset.Mips} mips -> {encoded.Width}x{encoded.Height} " +
                                $"{encoded.FormatName} {encoded.Mips} mips";
                if (RefreshMipSplit(encoded, asset) is { } splitRefusal)
                {
                    skipped.Add($"{asset.Resource}: {change}: {splitRefusal}");
                    continue;
                }
                if (!RefreshStats(encoded, asset, source, loadPng))
                    warnings.Add($"{asset.Resource}: {change}: header statistics kept from the original ({encoded.FormatName} statistics are not derivable)");
            }
            var header = asset.Parts.FirstOrDefault(w => w.Type == 0x20) ?? Fallback(0x20);
            var bitmap = asset.Parts.FirstOrDefault(w => w.Type == 0x21) ?? Fallback(0x21);
            specs.Add(new ResourceSpec(
                Encoding.UTF8.GetBytes(asset.Resource), 0x20, asset.LogicalFlags == 0 ? (byte)0x01 : asset.LogicalFlags,
                [
                    new PartSpec(0x20, encoded.Header, header.AlignRaw, header.StorageFlags, header.StorageMetadata,
                                 header.FlagBits, header.Fc),
                    new PartSpec(0x21, encoded.Bitmap, bitmap.AlignRaw, bitmap.StorageFlags, bitmap.StorageMetadata,
                                 bitmap.FlagBits, bitmap.Fc),
                ]));
            built.Add($"{asset.Resource}: {encoded.Summary}");
            sourcePacks.Add(asset.SourcePack);
            Log.Info("build", $"{asset.Resource}: {encoded.Summary}");
        }
        foreach (var w in warnings) Log.Warn("build", w);
        return new TextureSpecSet(specs, built, skipped, sourcePacks, warnings);
    }

    /// <summary>Levels in the second group of a stock mip split: 32 px and down (census: DLTB 17/17, DL2 55/55).</summary>
    private const int SplitRestLevels = 6, SplitRestTop = 32;

    /// <summary>
    /// The IMGC mip split (+0x48) of a texture with this geometry, by the rule every shipped split follows (DLTB 17/17,
    /// DL2 55/55 textures; all BC1, header flag 0x10): the first group is the levels above 32 px, the second the last
    /// six (32 px to 1 px); low u32 = tight bytes of the first group, high u32 = group size &lt;&lt; 28 | tight bytes of the
    /// second. Null when the chain does not end in those six levels.
    /// </summary>
    public static ulong? StockMipSplit(byte format, int width, int height, int depth, int mips, int faces)
    {
        int n = mips - SplitRestLevels;
        if (n is < 1 or > 15) return null;
        var (wn, hn, _) = ImgcHeader.MipDims(width, height, depth, n);
        var (wl, hl, _) = ImgcHeader.MipDims(width, height, depth, mips - 1);
        if (Math.Max(wn, hn) != SplitRestTop || Math.Max(wl, hl) != 1) return null;
        long first = 0, rest = 0;
        for (int m = 0; m < mips; m++)
        {
            var (w, h, d) = ImgcHeader.MipDims(width, height, depth, m);
            long bytes = ImgcFormats.LevelBytes(format, w, h, d) * faces;
            if (m < n) first += bytes; else rest += bytes;
        }
        if (first > uint.MaxValue || rest > 0x0FFFFFFF) return null;
        return ((ulong)(((uint)n << 28) | (uint)rest) << 32) | (uint)first;
    }

    /// <summary>A resized texture's mip split recomputed by <see cref="StockMipSplit"/>, or why it cannot be.</summary>
    private static string? RefreshMipSplit(EncodedTexture encoded, TextureAsset asset)
    {
        if (asset.MipSplit == 0) return null;
        int faces = asset.TexType == (int)TextureType.Cube ? 6 : 1;
        if (StockMipSplit(asset.Format, asset.Width, asset.Height, asset.Depth, asset.Mips, faces) != asset.MipSplit)
            return $"its mip split 0x{asset.MipSplit:X16} does not follow the stock rule, so it cannot be recomputed";
        if (StockMipSplit(asset.EffectiveFormat, encoded.Width, encoded.Height, asset.Depth, encoded.Mips, faces) is not { } split)
            return "the stock mip split (levels above 32 px, then 32 px to 1 px) does not fit the new size; " +
                   "clear mipSplit and header flag 0x10 in the sidecar to build it without one";
        BinaryPrimitives.WriteUInt64LittleEndian(encoded.Header.AsSpan(72), split);
        return null;
    }

    /// <summary>
    /// A resized R8 / RGBA8 texture built from a PNG gets new statistics by the prototype's PNG import
    /// (<c>texture/png.py::compute_stats</c>): minimum and maximum of the stored mip-0 channels (0 for channels the format
    /// does not store), mean of the source image's RGBA in [0,1]. Other formats and sources keep the original's (false).
    /// </summary>
    private static bool RefreshStats(EncodedTexture encoded, TextureAsset asset, string source, Func<string, (byte[] Bgra, int Width, int Height)> loadPng)
    {
        byte format = asset.EffectiveFormat;
        if (format is not (0 or 38) || !Path.GetExtension(source).Equals(".png", StringComparison.OrdinalIgnoreCase)
            || asset.TexType != (int)TextureType.Texture2D) return false;
        int channels = format == 0 ? 1 : 4, w = encoded.Width, h = encoded.Height;
        var min = new float[4];
        var max = new float[4];
        for (int c = 0; c < channels; c++) { min[c] = 1; max[c] = 0; }
        for (int i = 0; i < w * h; i++)                 // level 0 starts the bitmap; stored order R, G, B, A
            for (int c = 0; c < channels; c++)
            {
                float v = encoded.Bitmap[i * channels + c] / 255f;
                min[c] = Math.Min(min[c], v);
                max[c] = Math.Max(max[c], v);
            }
        var (bgra, sw, sh) = loadPng(source);
        var sum = new double[4];
        for (int i = 0; i < sw * sh; i++)
        {
            sum[0] += bgra[i * 4 + 2];
            sum[1] += bgra[i * 4 + 1];
            sum[2] += bgra[i * 4];
            sum[3] += bgra[i * 4 + 3];
        }
        var span = encoded.Header.AsSpan(16, 48);
        for (int c = 0; c < 4; c++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(span[(c * 4)..], min[c]);
            BinaryPrimitives.WriteSingleLittleEndian(span[(16 + c * 4)..], max[c]);
            BinaryPrimitives.WriteSingleLittleEndian(span[(32 + c * 4)..], (float)(sum[c] / 255.0 / Math.Max(1, sw * sh)));
        }
        return true;
    }

    private static TextureAsset.PartWords Fallback(byte type) => new()
    {
        Type = type,
        AlignRaw = 8,          // 16-byte alignment, what every stock texture storage uses
    };

    /// <summary>Read a written pack back with our own reader: every part readable, every texture header parses.</summary>
    public static (string Verdict, bool Ok) Verify(string path, int expectedTextures)
    {
        try
        {
            using var pack = RpackFile.Open(path);
            int textures = pack.Logicals.Count(l => l.Type == 0x20);
            var problems = new List<string>();
            for (int i = 0; i < pack.Count; i++)
            {
                var lg = pack.Logicals[i];
                for (int k = 0; k < lg.PartCount; k++)
                    if (pack.PartUnreadableReason((int)lg.FirstPart + k) is { } why) problems.Add($"{pack.Name(i)}: {why}");
                if (lg.Type != 0x20) continue;
                try { TextureResource.Open(pack, i); }
                catch (Exception e) when (e is ImgcException or RpackFormatException) { problems.Add($"{pack.Name(i)}: {e.Message}"); }
            }
            bool ok = problems.Count == 0 && textures == expectedTextures;
            string verdict = problems.Count == 0
                ? $"verified: reopens with {textures}/{expectedTextures} textures, every part readable"
                : $"verify found {problems.Count} problem(s): {problems[0]}";
            Log.Write(ok ? LogLevel.Info : LogLevel.Error, "build", verdict, null);
            return (verdict, ok);
        }
        catch (Exception e) when (e is RpackFormatException or IOException)
        {
            Log.Error("build", $"verify failed: {e.Message}");
            return ($"verify failed: {e.Message}", false);
        }
    }
}
