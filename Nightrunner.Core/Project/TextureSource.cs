using Nightrunner.Core.Texture;

namespace Nightrunner.Core.Project;

/// <summary>
/// What a project texture is built from, and the rules for each kind of source file:
/// <list type="bullet">
/// <item><b>PNG</b> — 8-bit pixels, re-encoded to the build format (mips regenerated). A float target (BC6H,
/// RGBA16F) is refused: a PNG cannot carry HDR, and the PNG an older "Add to project" wrote for a BC6H texture is a
/// tone-mapped preview, not data.</item>
/// <item><b>DDS</b> in the build format — texels taken verbatim, every level and face as the file has them (the
/// prototype's DDS import). A float DDS (RGBA16F, RGBA32F) or a BC6H DDS of the other signedness for a float target
/// is decoded and re-encoded; its own mips are kept, or regenerated when it has one level. Anything else is refused.</item>
/// <item><b>Radiance .hdr</b> — one linear surface for a 2D float target; mips regenerated.</item>
/// </list>
/// The template header (statistics, flags, extension, tail) comes from the sidecar in every case, as for PNGs.
/// </summary>
public static class TextureSource
{
    public enum Kind { Png, Dds, Radiance }

    public static Kind? KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => Kind.Png,
        ".dds" => Kind.Dds,
        ".hdr" => Kind.Radiance,
        _ => null,
    };

    private static readonly byte[] FloatTargets = [.. ImgcFormats.All.Where(f => ImgcEncoder.IsFloat(f.Id)).Select(f => f.Id)];

    /// <summary>Why <paramref name="path"/> cannot build <paramref name="asset"/>, or null when it can.</summary>
    public static string? Refusal(string path, TextureAsset asset)
    {
        byte target = asset.EffectiveFormat;
        string name = ImgcFormats.Name(target);
        switch (KindOf(path))
        {
            case Kind.Png:
                if (ImgcEncoder.IsFloat(target))
                    return $"{name} is HDR and a PNG cannot carry it: build it from a float DDS (Add to project writes " +
                           "one), a BC6H DDS or a Radiance .hdr";
                return ImgcEncoder.Refusal(target);
            case Kind.Radiance:
                if (!ImgcEncoder.IsFloat(target))
                    return $".hdr is HDR: it builds BC6H_UF16, BC6H_SF16 or RGBA16F, not {name}";
                if (asset.TexType != (int)TextureType.Texture2D)
                    return $".hdr holds one surface; {name} {(TextureType)asset.TexType} needs a DDS";
                return null;
            case Kind.Dds:
                try
                {
                    return Plan(path, asset, out _);
                }
                catch (Exception e) when (e is ImgcException or IOException or UnauthorizedAccessException)
                {
                    return e.Message;
                }
            default:
                return $"{Path.GetExtension(path)} is not a texture source";
        }
    }

    /// <summary>How a DDS builds: verbatim (<paramref name="passThrough"/>) or decoded and re-encoded; null or why not.</summary>
    private static string? Plan(string path, TextureAsset asset, out bool passThrough)
    {
        passThrough = false;
        var dds = ReadDdsHeader(path);
        byte target = asset.EffectiveFormat;
        string name = ImgcFormats.Name(target);
        if ((int)dds.Type != asset.TexType)
            return $"DDS is {dds.Type}, the texture is {(TextureType)asset.TexType}";
        if (dds.Format == target)
        {
            ReadDdsHeader(path, strict: true);      // the prototype's import rules: tier A formats, no sRGB ids
            passThrough = true;
            return null;
        }
        if (ImgcEncoder.IsFloat(target) && TextureDecoder.FloatReason(dds.Format) is null)
            return dds.Type == TextureType.Volume ? $"{name} volume textures cannot be written" : null;
        return ImgcEncoder.IsFloat(target)
            ? $"DDS is {dds.FormatName}, the texture builds as {name}: save it as {name}, RGBA16F or RGBA32F"
            : $"DDS is {dds.FormatName}, the texture builds as {name}: save it as {name}, or build it as {dds.FormatName}";
    }

    private static DdsFile ReadDdsHeader(string path, bool strict = false)
    {
        using var f = File.OpenRead(path);
        var head = new byte[Math.Min(f.Length, 148)];
        f.ReadExactly(head);
        return DdsReader.ReadHeader(head, f.Length, allowUnobserved: !strict);
    }

    /// <summary>
    /// Build the texture from its source. <paramref name="loadPng"/> decodes a PNG to BGRA32; <paramref name="warn"/>
    /// hears what was changed on the way (size, regenerated mips, values clamped to the format's range).
    /// </summary>
    public static EncodedTexture Encode(string path, TextureAsset asset,
                                        Func<string, (byte[] Bgra, int Width, int Height)> loadPng, Action<string> warn)
    {
        if (Refusal(path, asset) is { } why) throw new ImgcException(why);
        var template = asset.ToHeader();
        switch (KindOf(path))
        {
            case Kind.Png:
            {
                var (bgra, width, height) = loadPng(path);
                SizeWarning(asset, width, height, warn);
                return ImgcEncoder.Encode(template, bgra, width, height,
                                          width == asset.Width && height == asset.Height ? asset.Mips : 0,
                                          asset.LevelPadding);
            }
            case Kind.Radiance:
            {
                var top = RadianceHdr.Read(File.ReadAllBytes(path));
                return FromFloats(template, asset, [[top]], warn);
            }
            default:
            {
                Plan(path, asset, out bool passThrough);
                var file = File.ReadAllBytes(path);
                var dds = DdsReader.Read(file, allowUnobserved: !passThrough);
                foreach (var w in dds.Warnings) warn(w);
                var levels = DdsReader.Levels(dds, file);
                if (passThrough)
                {
                    SizeWarning(asset, dds.Width, dds.Height, warn);
                    return ImgcEncoder.FromLevels(template, dds.Width, dds.Height, dds.MipCount, levels, asset.LevelPadding);
                }

                var geometry = dds.Geometry().LevelLayout(ImgcHeader.LevelPaddingTight);
                var faces = new List<FloatImage>[dds.Faces];
                for (int f = 0; f < faces.Length; f++) faces[f] = [];
                for (int i = 0; i < levels.Count; i++)
                {
                    var l = geometry[i];
                    faces[l.Face].Add(TextureDecoder.DecodeFloat(dds.Format, levels[i], l.Width, l.Height));
                }
                return FromFloats(template, asset, faces, warn);
            }
        }
    }

    /// <summary>Per-face mip chains (one level = regenerate) → the float encoder.</summary>
    private static EncodedTexture FromFloats(ImgcHeader template, TextureAsset asset, List<FloatImage>[] faces,
                                             Action<string> warn)
    {
        var top = faces[0][0];
        SizeWarning(asset, top.Width, top.Height, warn);
        if (faces[0].Count == 1)
        {
            int full = 1;
            for (int w = top.Width, h = top.Height; w > 1 || h > 1; full++) (w, h) = (Math.Max(1, w >> 1), Math.Max(1, h >> 1));
            int mips = top.Width == asset.Width && top.Height == asset.Height ? Math.Min(asset.Mips, full) : full;
            if (mips > 1)
                warn($"{mips - 1} mip(s) regenerated with a box filter; supply a DDS with every mip to keep " +
                     "prefiltered ones (shipped reflection probes have them)");
            for (int f = 0; f < faces.Length; f++) faces[f] = faces[f][0].MipChain(mips);
        }
        int count = faces[0].Count;
        var surfaces = new List<FloatImage>(count * faces.Length);
        for (int m = 0; m < count; m++)
            foreach (var face in faces) surfaces.Add(face[m]);

        bool signed = template.FormatName == "BC6H_SF16" || template.FormatName == "RGBA16F";
        long clamped = surfaces.Sum(s => Bc6h.OutOfRange(s, signed));
        if (clamped > 0)
            warn($"{clamped:N0} channel value(s) outside {template.FormatName}'s range " +
                 $"({(signed ? "±65504" : "0..65504")}, NaN) clamped");
        return ImgcEncoder.EncodeFloat(template, surfaces, count, asset.LevelPadding);
    }

    private static void SizeWarning(TextureAsset asset, int width, int height, Action<string> warn)
    {
        if (width != asset.Width || height != asset.Height)
            warn($"size {asset.Width}x{asset.Height} -> {width}x{height}");
    }

    /// <summary>The formats a source can build as, for the "build as" picker.</summary>
    public static IReadOnlyList<byte> BuildFormats(string path)
    {
        switch (KindOf(path))
        {
            case Kind.Png:
                return [.. ImgcFormats.All.Where(f => ImgcEncoder.CanEncode(f.Id) && !ImgcEncoder.IsFloat(f.Id)).Select(f => f.Id)];
            case Kind.Radiance:
                return FloatTargets;
            case Kind.Dds:
                try
                {
                    var dds = ReadDdsHeader(path);
                    return TextureDecoder.FloatReason(dds.Format) is null
                        ? [.. FloatTargets.Union([dds.Format])]
                        : [dds.Format];
                }
                catch (Exception e) when (e is ImgcException or IOException or UnauthorizedAccessException)
                {
                    return [];
                }
            default:
                return [];
        }
    }

    /// <summary>
    /// A small display image of a non-PNG source (tone-mapped when HDR): the smallest level at least
    /// <paramref name="minWidth"/> wide. Null when it cannot be read.
    /// </summary>
    public static DecodedImage? Preview(string path, int minWidth = 96)
    {
        try
        {
            switch (KindOf(path))
            {
                case Kind.Radiance:
                    return RadianceHdr.Read(File.ReadAllBytes(path)).ToDisplay();
                case Kind.Dds:
                {
                    var file = File.ReadAllBytes(path);
                    var dds = DdsReader.Read(file, allowUnobserved: true);
                    var levels = dds.FileLevels().Where(l => l.Face == 0).ToList();
                    var l = levels.LastOrDefault(l => l.Width >= minWidth, levels[0]);
                    var texels = file.AsSpan(dds.DataOffset + (int)l.Offset, (int)l.Size);
                    if (TextureDecoder.FloatReason(dds.Format) is null)
                        return TextureDecoder.DecodeFloat(dds.Format, texels, l.Width, l.Height).ToDisplay();
                    return TextureDecoder.CanDecode(dds.Format) && l.Depth == 1
                        ? TextureDecoder.Decode(dds.Format, texels, l.Width, l.Height)
                        : null;
                }
                default:
                    return null;
            }
        }
        catch (Exception e) when (e is ImgcException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
