using System.Text.Json;
using System.Text.Json.Serialization;
using Nightrunner.Core.Rpack;
using Nightrunner.Core.Texture;

namespace Nightrunner.Core.Project;

/// <summary>
/// What a project stores beside an edited source file (a PNG; for an HDR texture a float DDS, or a .hdr) so the
/// texture can be written back into a pack: everything from the original IMGC header that the pixels do not carry,
/// plus the storage words of the parts it came from.
/// </summary>
/// <remarks>
/// The source file is the editable thing (<see cref="TextureSource"/>); this file is the receipt. Unknown header bytes (statistics, flags, extension,
/// tail) and the physical/storage bits ride along verbatim, so a rebuild reproduces the original's shape instead
/// of inventing one.
/// </remarks>
public sealed class TextureAsset
{
    public const string Extension = ".nrtex.json";

    /// <summary>Where edited textures live inside a project.</summary>
    public const string Folder = "textures";

    [JsonPropertyName("schema")] public string Schema { get; set; } = "nightrunner/texture@1";

    /// <summary>The resource name, exactly as the pack stores it (this is also the PNG's file name).</summary>
    [JsonPropertyName("resource")] public string Resource { get; set; } = "";

    [JsonPropertyName("sourcePack")] public string SourcePack { get; set; } = "";
    [JsonPropertyName("sourceIndex")] public int SourceIndex { get; set; }
    [JsonPropertyName("game")] public string? Game { get; set; }

    // ---- IMGC header (everything the pixels do not carry) ---------------------------------------------------
    [JsonPropertyName("format")] public byte Format { get; set; }
    [JsonPropertyName("formatName")] public string FormatName { get; set; } = "";
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("depth")] public int Depth { get; set; } = 1;
    [JsonPropertyName("mips")] public int Mips { get; set; }
    [JsonPropertyName("texType")] public int TexType { get; set; }
    [JsonPropertyName("headerFlags")] public uint HeaderFlags { get; set; }
    [JsonPropertyName("headerSize")] public int HeaderSize { get; set; } = ImgcHeader.FixedSize;
    [JsonPropertyName("headerVersion")] public uint HeaderVersion { get; set; } = ImgcHeader.KnownVersion;
    [JsonPropertyName("reserved")] public byte Reserved { get; set; }
    [JsonPropertyName("mipSplit")] public ulong MipSplit { get; set; }
    [JsonPropertyName("statsBase64")] public string StatsBase64 { get; set; } = "";
    [JsonPropertyName("extensionBase64")] public string ExtensionBase64 { get; set; } = "";
    [JsonPropertyName("tailBase64")] public string TailBase64 { get; set; } = "";
    [JsonPropertyName("levelPadding")] public int LevelPadding { get; set; } = ImgcHeader.LevelPaddingStock;

    /// <summary>
    /// The IL format to write when this texture is built, when it should differ from the one it came from —
    /// e.g. a mask authored as greyscale goes back as BC4, a colour map as BC1. Null means "as it came".
    /// </summary>
    [JsonPropertyName("buildFormat")] public byte? BuildFormat { get; set; }

    /// <summary>The format a build will actually use.</summary>
    [JsonIgnore] public byte EffectiveFormat => BuildFormat ?? Format;

    [JsonIgnore] public string EffectiveFormatName => ImgcFormats.Name(EffectiveFormat);

    // ---- container words ------------------------------------------------------------------------------------
    [JsonPropertyName("logicalFlags")] public byte LogicalFlags { get; set; }
    [JsonPropertyName("parts")] public List<PartWords> Parts { get; set; } = [];

    /// <summary>The storage and physical bits one part was stored with.</summary>
    public sealed class PartWords
    {
        [JsonPropertyName("type")] public byte Type { get; set; }
        [JsonPropertyName("alignRaw")] public byte AlignRaw { get; set; }
        [JsonPropertyName("storageFlags")] public byte StorageFlags { get; set; }
        [JsonPropertyName("storageMetadata")] public byte StorageMetadata { get; set; }
        [JsonPropertyName("flagBits")] public uint FlagBits { get; set; }
        [JsonPropertyName("fc")] public uint Fc { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The PNG file name for a resource: its exact name, with .png added when it has none.</summary>
    public static string PngName(string resourceName) =>
        resourceName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? resourceName : resourceName + ".png";

    public static string SidecarName(string resourceName) => PngName(resourceName) + Extension;

    /// <summary>
    /// Textures a project keeps as linear floats instead of a PNG: BC6H and RGBA16F. An 8-bit PNG cannot carry
    /// them, so "Add to project" writes an RGBA16F DDS (<see cref="DdsWriter.ToFloatDds"/>) instead.
    /// </summary>
    public static bool IsHdr(byte format) => ImgcEncoder.IsFloat(format);

    /// <summary>The DDS file name for an HDR resource: its exact name, with .dds added when it has none.</summary>
    public static string HdrName(string resourceName) =>
        resourceName.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? resourceName : resourceName + ".dds";

    /// <summary>The editable file a texture of this format is added to a project as.</summary>
    public static string SourceName(string resourceName, byte format) =>
        IsHdr(format) ? HdrName(resourceName) : PngName(resourceName);

    /// <summary>Source files a build reads: PNG, DDS (float, BC6H or any stored format) and Radiance .hdr.</summary>
    public static readonly string[] SourceExtensions = [".png", ".dds", ".hdr"];

    /// <summary>Record what a texture was, ready to save beside its PNG.</summary>
    public static TextureAsset From(RpackFile pack, int logicalIndex, TextureResource texture, string packLabel,
                                    string? gameId)
    {
        var h = texture.Header;
        var lg = pack.Logicals[logicalIndex];
        var asset = new TextureAsset
        {
            Resource = pack.Name(logicalIndex),
            SourcePack = packLabel,
            SourceIndex = logicalIndex,
            Game = gameId,
            Format = h.Format,
            FormatName = h.FormatName,
            Width = h.Width,
            Height = h.Height,
            Depth = h.Depth,
            Mips = h.MipCount,
            TexType = (int)h.Type,
            HeaderFlags = h.Flags,
            HeaderSize = h.HeaderSize,
            HeaderVersion = h.Version,
            Reserved = h.Reserved,
            MipSplit = h.MipSplit,
            StatsBase64 = Convert.ToBase64String(h.StatsRaw),
            ExtensionBase64 = Convert.ToBase64String(h.Extension),
            TailBase64 = Convert.ToBase64String(h.Tail),
            LevelPadding = texture.LevelPadding ?? ImgcHeader.LevelPaddingStock,
            LogicalFlags = lg.Flags,
        };
        for (int k = 0; k < lg.PartCount; k++)
        {
            int pi = (int)lg.FirstPart + k;
            var storage = pack.PartStorage(pi);
            asset.Parts.Add(new PartWords
            {
                Type = storage.Type,
                AlignRaw = storage.AlignRaw,
                StorageFlags = storage.Flags,
                StorageMetadata = storage.Metadata,
                FlagBits = pack.Physicals[pi].FlagBits,
                Fc = pack.Physicals[pi].Fc,
            });
        }
        return asset;
    }

    /// <summary>
    /// The header this asset describes, for the encoder to fill in. The format is the chosen build format, so a
    /// texture can be written back as something other than what it was read as.
    /// </summary>
    public ImgcHeader ToHeader() => new()
    {
        Version = HeaderVersion,
        HeaderSize = HeaderSize,
        Flags = HeaderFlags,
        StatsRaw = StatsBase64.Length > 0 ? Convert.FromBase64String(StatsBase64) : new byte[48],
        Width = Width,
        Height = Height,
        Depth = Depth,
        Reserved = Reserved,
        Format = EffectiveFormat,
        Packed = (byte)((TexType & 3) | (Mips << 2)),
        MipSplit = MipSplit,
        Extension = ExtensionBase64.Length > 0 ? Convert.FromBase64String(ExtensionBase64) : [],
        Tail = TailBase64.Length > 0 ? Convert.FromBase64String(TailBase64) : [],
    };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static TextureAsset Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<TextureAsset>(File.ReadAllText(path), Json)
                   ?? throw new ProjectException($"{path} is empty");
        }
        catch (JsonException e)
        {
            throw new ProjectException($"{path}: {e.Message}");
        }
    }

    /// <summary>
    /// Every source file (<see cref="SourceExtensions"/>) in a project's textures folder that has a sidecar, plus the
    /// ones that do not.
    /// </summary>
    public static (List<(string Source, TextureAsset Asset)> Ready, List<string> Orphans) Scan(string projectFolder)
    {
        var dir = Path.Combine(projectFolder, Folder);
        List<(string, TextureAsset)> ready = [];
        List<string> orphans = [];
        if (!Directory.Exists(dir)) return (ready, orphans);

        var sources = Directory.EnumerateFiles(dir)
            .Where(f => SourceExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            var sidecar = source + Extension;
            if (!File.Exists(sidecar))
            {
                orphans.Add(source);
                continue;
            }
            try
            {
                ready.Add((source, Load(sidecar)));
            }
            catch (ProjectException)
            {
                orphans.Add(source);
            }
        }
        return (ready, orphans);
    }
}
