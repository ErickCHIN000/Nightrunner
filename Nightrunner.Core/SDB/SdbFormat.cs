namespace Nightrunner.Core.Sdb;

/// <summary>Thrown when a file is not a readable MDBR/MDB shader database.</summary>
public sealed class SdbFormatException(string message) : Exception(message);

/// <summary>How a table's records are laid out in the stream.</summary>
public enum SdbShape
{
    /// <summary>compact count; count × W bytes.</summary>
    Flat,
    /// <summary>compact count; per record: compact n, n × W bytes (the span excludes the prefix).</summary>
    Vectors,
    /// <summary>compact count; per record: 4 bytes, compact n, n × 2 bytes.</summary>
    PairU16,
    /// <summary>compact count; per record: 2 bytes, compact n, n × 8 bytes.</summary>
    U16Records8,
    /// <summary>compact count; per record: 7 × (compact n, n × 2 bytes).</summary>
    Seven,
    /// <summary>compact count; per record: 23 bytes; gA × {8 B, compact n, n B}; gB × {4 B, compact n, n × 4 B}.</summary>
    Trailing,
}

public readonly record struct SdbTableDef(int Key, SdbShape Shape, int Width);

/// <summary>The table stream layout, selected by the inner MDB version word and never by the selected game.</summary>
public sealed record SdbLayout(string Name, uint InnerVersion, SdbTableDef[] OrderA, SdbTableDef[] OrderB)
{
    public IEnumerable<SdbTableDef> Order => OrderA.Concat(OrderB);
}

/// <summary>
/// MDBR/MDB constants: framing, the table order of both layouts, and what the twelve interpreted tables mean.
/// </summary>
/// <remarks>
/// Ported from the Python reader (`nightrunner/sdb/reader.py`), whose provenance is: (A) renderer disassembly —
/// framing at mcp_Mdb_LoadSdbFile +0x13B1C0, table order and shapes at mcp_Mdb_ParseBlocks +0xECC60, the compact
/// reader at +0xDB550, program blobs at +0xFF9D0, the resolver joins at ResolveTextureBindings +0x131D40 and
/// ApplyParameterOverrides +0xD89F0, the 0xAA record layout at the preset editor export +0x44D70;
/// (B) DyingLightExplorer; (C) 27 of the 39 tables are opaque — their records are exposed but never interpreted.
/// </remarks>
public static class SdbFormat
{
    /// <summary>"MDBR".</summary>
    public const uint OuterMagic = 0x5242444D;

    /// <summary>"MDB ".</summary>
    public const uint InnerMagic = 0x2042444D;

    public const uint OuterVersion = 0x23062801;

    /// <summary>Dying Light: The Beast.</summary>
    public const uint InnerVersionDltb = 0x24012001;

    /// <summary>Dying Light 2 — shares the outer version word, which is why only the inner one may select.</summary>
    public const uint InnerVersionDl2 = 0x23062801;

    /// <summary>MDBR (16) + MDB magic and version (8) + 28 opaque bytes.</summary>
    public const int HeaderSize = 52;

    /// <summary>A string index at or above this is resolved by the engine at runtime, not from table 0xAE.</summary>
    public const int RuntimeStringBase = 0xFBFF;

    public const int TableRoutes = 0xCA;
    public const int TableSlots = 0xCE;
    public const int TableValues = 0xD2;
    public const int TableSharedNames = 0xAE;
    public const int TableMaterialNames = 0xB2;
    public const int TableTokens = 0xB6;
    public const int TableStrings = 0xBA;
    public const int TablePresets = 0xAA;
    public const int TableShaders = 0x9A;
    public const int TableSelectors = 0xC2;
    public const int TableTextureArrays = 0xA2;

    /// <summary>Program blob groups, in stream order; the blobs themselves live consecutively in block B.</summary>
    public static readonly int[] ProgramKinds = [0, 2, 3];

    /// <summary>Tables whose records are UTF-8 text rather than bytes.</summary>
    public static readonly int[] StringTables = [0xAE, 0xB2, 0xB6, 0xBA];

    public static readonly string[] ValueTypes =
        ["bool", "int", "float", "vec2", "vec3", "vec4", "matrix", "string"];

    /// <summary>Byte width of each value type, by type id; -1 for a type that has none.</summary>
    public static readonly int[] ValueSizes = [1, 4, 4, 8, 12, 16, 64, 2];

    private static readonly SdbTableDef[] OrderA =
    [
        new(0x0C, SdbShape.Flat, 22), new(0x10, SdbShape.Vectors, 2), new(0x14, SdbShape.Vectors, 4),
        new(0x18, SdbShape.Vectors, 4), new(0x1C, SdbShape.Vectors, 8), new(0x20, SdbShape.Vectors, 4),
        new(0x24, SdbShape.Vectors, 2), new(0x9A, SdbShape.Flat, 25), new(0x9E, SdbShape.PairU16, 0),
        new(0xA2, SdbShape.Vectors, 4), new(0x62, SdbShape.Flat, 44), new(0x76, SdbShape.PairU16, 0),
        new(0x6A, SdbShape.U16Records8, 0), new(0x72, SdbShape.Vectors, 4), new(0x28, SdbShape.Flat, 27),
        new(0x30, SdbShape.Vectors, 4), new(0x34, SdbShape.Vectors, 4), new(0x3C, SdbShape.Vectors, 2),
        new(0x38, SdbShape.Vectors, 2), new(0x40, SdbShape.Vectors, 2), new(0x48, SdbShape.Vectors, 2),
        new(0x44, SdbShape.Vectors, 2), new(0x2C, SdbShape.Seven, 0), new(0x8E, SdbShape.Vectors, 2),
        new(0x92, SdbShape.Flat, 4), new(0x7E, SdbShape.Flat, 32), new(0x86, SdbShape.Flat, 8),
    ];

    private static readonly SdbTableDef[] OrderB =
    [
        new(0xC2, SdbShape.Vectors, 8), new(0xC6, SdbShape.Flat, 8), new(0xCA, SdbShape.Flat, 10),
        new(0xCE, SdbShape.Vectors, 4), new(0xD2, SdbShape.Vectors, 1), new(0xD6, SdbShape.Flat, 8),
        new(0xAE, SdbShape.Vectors, 1), new(0xB2, SdbShape.Vectors, 1), new(0xB6, SdbShape.Vectors, 1),
        new(0xBA, SdbShape.Vectors, 1), new(0xBE, SdbShape.Flat, 4), new(0xAA, SdbShape.Trailing, 0),
    ];

    // DL2 (inner 0x23062801), byte-exact on runtime_dx11/dx12.sdb and the DevTools sample: 0x62 pass records are
    // 43 bytes (DLTB 44), 0x28 stage descriptors 25 (DLTB 27), and the 0x40..0x2C group has one vectors-2 table
    // fewer. Which DLTB key the missing table corresponds to is not known natively; it is listed as 0x48 absent.
    private static SdbTableDef[] Dl2OrderA() =>
        [.. OrderA.Where(t => t.Key != 0x48)
                  .Select(t => t.Key switch
                  {
                      0x62 => t with { Width = 43 },
                      0x28 => t with { Width = 25 },
                      _ => t,
                  })];

    public static readonly SdbLayout Dltb = new("dltb", InnerVersionDltb, OrderA, OrderB);
    public static readonly SdbLayout Dl2 = new("dl2", InnerVersionDl2, Dl2OrderA(), OrderB);

    public static readonly SdbLayout[] Layouts = [Dltb, Dl2];

    public static SdbLayout? LayoutFor(uint innerVersion) =>
        Layouts.FirstOrDefault(l => l.InnerVersion == innerVersion);

    /// <summary>What an interpreted table holds; everything else is opaque and only exposed as bytes.</summary>
    public static string Meaning(int key) => key switch
    {
        0x20 => "destination override descriptors",
        0x9A => "shader records",
        0xA2 => "texture binding arrays (u16 param id, u16 default 0xAE index)",
        0x62 => "render pass records",
        0x28 => "stage descriptors (+2 u16 program blob index)",
        0xC2 => "selector arrays (u64; high 16 = 0x9A index)",
        0xCA => "routes: u16 material(B2), program(C2), tokens(B6), slots(CE), values(D2)",
        0xCE => "slot descriptors (u32: pid low16 | D2 offset bits16..30 | flag bit31)",
        0xD2 => "value blobs",
        0xAE => "shared names (textures + parameter identifiers)",
        0xB2 => "material names",
        0xB6 => "token strings 'preset;token;…'",
        0xBA => "expression/annotation/preset-name strings",
        0xBE => "(u16 id-or-hash, u16 BA index)",
        0xAA => "preset records",
        _ => "opaque",
    };

    public static bool IsInterpreted(int key) => Meaning(key) != "opaque";

    public static string TypeName(int type) =>
        (uint)type < (uint)ValueTypes.Length ? ValueTypes[type] : $"unknown_{type}";

    /// <summary>Byte width of a value of this type, or -1 when the type is not one this reader decodes.</summary>
    public static int ValueSize(int type) => (uint)type < (uint)ValueSizes.Length ? ValueSizes[type] : -1;
}
