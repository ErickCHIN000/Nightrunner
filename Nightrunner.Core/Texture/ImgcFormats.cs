namespace Nightrunner.Core.Texture;

/// <summary>How a format stores its samples, for the decoder's dispatch.</summary>
public enum FormatKind
{
    Unorm,
    Snorm,
    Uint,
    Sint,
    Float,
    Depth,
    Packed,
    Bc,
}

/// <summary>One row of the native IL::Format enum.</summary>
/// <param name="Unit">Bytes per pixel, or bytes per 4x4 block when <paramref name="Block"/>.</param>
/// <param name="Tier">"A" = read from the native code plus the corpus; "C" = derived from the enum name only.</param>
/// <param name="Corpus">Occurrences in the 49,885-texture census (survey 03 3.1).</param>
public readonly record struct ImgcFormat(
    byte Id, string Name, int Unit, bool Block, FormatKind Kind, string Channels, string Tier, int Corpus)
{
    public bool Srgb => ImgcFormats.SrgbToLinear.ContainsKey(Id);
    public override string ToString() => Name;
}

/// <summary>
/// The native IL::Format enum: id, sample size and block-ness. Ported from nightrunner/texture/formats.py.
/// </summary>
/// <remarks>
/// Provenance: imagelib IL::ToStr +0x50E10 for the names and ids, GetDescription +0x45D10 for block bytes, plus a
/// 49,885-texture census. Holes at 4, 5, 11, 43, 72; 255 is the "unknown" sentinel. The layout maths only needs
/// <see cref="ImgcFormat.Unit"/> and <see cref="ImgcFormat.Block"/>, which is why every row is here even though
/// only 18 ids actually occur in the shipped corpus.
/// </remarks>
public static class ImgcFormats
{
    public const byte UnknownId = 255;

    private static readonly ImgcFormat[] Rows =
    [
        new(0, "R8", 1, false, FormatKind.Unorm, "R", "A", 4055),
        new(1, "R8_SNORM", 1, false, FormatKind.Snorm, "R", "C", 0),
        new(2, "R8_UINT", 1, false, FormatKind.Uint, "R", "C", 0),
        new(3, "R8_SINT", 1, false, FormatKind.Sint, "R", "C", 0),
        new(6, "R16F", 2, false, FormatKind.Float, "R", "C", 0),
        new(7, "R16", 2, false, FormatKind.Unorm, "R", "C", 0),
        new(8, "R16_SNORM", 2, false, FormatKind.Snorm, "R", "A", 2),
        new(9, "R16_UINT", 2, false, FormatKind.Uint, "R", "C", 0),
        new(10, "R16_SINT", 2, false, FormatKind.Sint, "R", "C", 0),
        new(12, "R32F", 4, false, FormatKind.Float, "R", "C", 0),
        new(13, "R32_UINT", 4, false, FormatKind.Uint, "R", "C", 0),
        new(14, "R32_SINT", 4, false, FormatKind.Sint, "R", "C", 0),
        new(15, "RG8", 2, false, FormatKind.Unorm, "RG", "A", 1),
        new(16, "RG8_SNORM", 2, false, FormatKind.Snorm, "RG", "A", 53),
        new(17, "RG8_UINT", 2, false, FormatKind.Uint, "RG", "C", 0),
        new(18, "RG8_SINT", 2, false, FormatKind.Sint, "RG", "C", 0),
        new(19, "RG16F", 4, false, FormatKind.Float, "RG", "C", 0),
        new(20, "RG16", 4, false, FormatKind.Unorm, "RG", "C", 0),
        new(21, "RG16_SNORM", 4, false, FormatKind.Snorm, "RG", "C", 0),
        new(22, "RG16_UINT", 4, false, FormatKind.Uint, "RG", "C", 0),
        new(23, "RG16_SINT", 4, false, FormatKind.Sint, "RG", "C", 0),
        new(24, "RG32F", 8, false, FormatKind.Float, "RG", "C", 0),
        new(25, "RG32_UINT", 8, false, FormatKind.Uint, "RG", "C", 0),
        new(26, "RG32_SINT", 8, false, FormatKind.Sint, "RG", "C", 0),
        new(27, "R5G6B5", 2, false, FormatKind.Packed, "RGB", "C", 0),
        new(28, "RGB8", 3, false, FormatKind.Unorm, "RGB", "C", 0),
        new(29, "BGR8", 3, false, FormatKind.Unorm, "BGR", "C", 0),
        new(30, "R11G11B10F", 4, false, FormatKind.Packed, "RGB", "C", 0),
        new(31, "BGR32F", 12, false, FormatKind.Float, "BGR", "C", 0),
        new(32, "ARGB8", 4, false, FormatKind.Unorm, "BGRA", "A", 3),
        new(33, "ARGB8_SRGB", 4, false, FormatKind.Unorm, "BGRA", "C", 0),
        new(34, "XRGB8", 4, false, FormatKind.Unorm, "BGRX", "C", 0),
        new(35, "BGRA8", 4, false, FormatKind.Unorm, "????", "C", 0),
        new(36, "BGRX8", 4, false, FormatKind.Unorm, "????", "C", 0),
        new(37, "XBGR8", 4, false, FormatKind.Unorm, "????", "C", 0),
        new(38, "RGBA8", 4, false, FormatKind.Unorm, "RGBA", "A", 1363),
        new(39, "RGBA8_SNORM", 4, false, FormatKind.Snorm, "RGBA", "A", 4),
        new(40, "RGBA8_UINT", 4, false, FormatKind.Uint, "RGBA", "A", 3),
        new(41, "RGBA8_SINT", 4, false, FormatKind.Sint, "RGBA", "C", 0),
        new(42, "A2RGB10", 4, false, FormatKind.Packed, "????", "C", 0),
        new(44, "RGB10A2", 4, false, FormatKind.Packed, "RGBA", "C", 0),
        new(45, "RGB10A2_UINT", 4, false, FormatKind.Packed, "RGBA", "C", 0),
        new(46, "RGBA16F", 8, false, FormatKind.Float, "RGBA", "A", 61),
        new(47, "RGBA16", 8, false, FormatKind.Unorm, "RGBA", "A", 1),
        new(48, "RGBA16_SNORM", 8, false, FormatKind.Snorm, "RGBA", "A", 96),
        new(49, "RGBA16_UINT", 8, false, FormatKind.Uint, "RGBA", "C", 0),
        new(50, "RGBA16_SINT", 8, false, FormatKind.Sint, "RGBA", "C", 0),
        new(51, "RGBA32F", 16, false, FormatKind.Float, "RGBA", "C", 0),
        new(52, "RGBA32_UINT", 16, false, FormatKind.Uint, "RGBA", "C", 0),
        new(53, "RGBA32_SINT", 16, false, FormatKind.Sint, "RGBA", "C", 0),
        new(54, "D16", 2, false, FormatKind.Depth, "D", "C", 0),
        new(55, "D24_S8", 4, false, FormatKind.Depth, "DS", "C", 0),
        new(56, "D32F", 4, false, FormatKind.Depth, "D", "C", 0),
        new(57, "D24FS8", 4, false, FormatKind.Depth, "DS", "C", 0),
        new(58, "D32F_S8", 8, false, FormatKind.Depth, "DS", "C", 0),
        new(59, "BC1", 8, true, FormatKind.Bc, "RGBA", "A", 14664),
        new(60, "BC2", 16, true, FormatKind.Bc, "RGBA", "A", 0),
        new(61, "BC3", 16, true, FormatKind.Bc, "RGBA", "A", 0),
        new(62, "BC4_SNORM", 8, true, FormatKind.Bc, "R", "A", 1188),
        new(63, "BC4", 8, true, FormatKind.Bc, "R", "A", 12002),
        new(64, "BC5_SNORM", 16, true, FormatKind.Bc, "RG", "A", 7703),
        new(65, "BC5", 16, true, FormatKind.Bc, "RG", "A", 4),
        new(66, "BC6H_UF16", 16, true, FormatKind.Bc, "RGB", "A", 1255),
        new(67, "BC6H_SF16", 16, true, FormatKind.Bc, "RGB", "A", 0),
        new(68, "BC7", 16, true, FormatKind.Bc, "RGBA", "A", 7427),
        new(69, "R8_NO_TYPELESS", 1, false, FormatKind.Unorm, "R", "C", 0),
        new(70, "BC1_SRGB", 8, true, FormatKind.Bc, "RGBA", "C", 0),
        new(71, "BC3_SRGB", 16, true, FormatKind.Bc, "RGBA", "C", 0),
        new(73, "BC7_SRGB", 16, true, FormatKind.Bc, "RGBA", "C", 0),
        new(74, "RGBA8_SRGB", 4, false, FormatKind.Unorm, "RGBA", "C", 0),
    ];

    /// <summary>IL::IsSrgb (+0x48DC0): sRGB id → the linear id with the same layout.</summary>
    public static readonly IReadOnlyDictionary<byte, byte> SrgbToLinear = new Dictionary<byte, byte>
    {
        [33] = 32, [70] = 59, [71] = 61, [73] = 68, [74] = 38,
    };

    private static readonly ImgcFormat?[] ById = Build();

    private static ImgcFormat?[] Build()
    {
        var a = new ImgcFormat?[256];
        foreach (var r in Rows) a[r.Id] = r;
        return a;
    }

    public static IReadOnlyList<ImgcFormat> All => Rows;

    public static bool IsKnown(byte id) => ById[id] is not null;

    public static ImgcFormat Get(byte id) =>
        ById[id] ?? throw new ImgcException($"IL format id {id} is not in the native enum");

    public static string Name(byte id) => ById[id]?.Name ?? (id == UnknownId ? "unknown" : $"id_{id}");

    /// <summary>Bytes one mip level occupies, tight (no padding). Block formats round up to whole 4x4 blocks.</summary>
    public static long LevelBytes(byte id, int width, int height, int depth)
    {
        var f = Get(id);
        return f.Block
            ? (long)((width + 3) / 4) * ((height + 3) / 4) * depth * f.Unit
            : (long)width * height * depth * f.Unit;
    }
}
