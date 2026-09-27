namespace Nightrunner.Core.Rpack;

/// <summary>Native resource type catalogue (EResType). Ported from nightrunner/container/catalogue.py.</summary>
/// <remarks>Source: ResourceCore_x64_rwdi.dll +0x876D0, 39 rows of stride 40, dumped 2026-09-10.</remarks>
public readonly record struct ResType(byte Id, int Version, int MemCat, string Name, string Short, string Pretty, bool Gpu);

public static class ResTypes
{
    private static readonly ResType[] Rows =
    [
        new(0x00, 1, 0, "_INVALID_", "INVALID", "Invalid", false),
        new(0x10, 60, 84, "_MESH_", "MESH", "Mesh", false),
        new(0x11, 60, 84, "_MESH_FIXUPS_", "MESH_FIX", "MeshFixups", false),
        new(0x12, 13, 85, "_SKIN_", "SKIN", "Skin", false),
        new(0x18, 3, 83, "_MODEL_", "MODEL", "Model", false),
        new(0x20, 11, 109, "_TEXTURE_", "TEXTURE", "Texture", false),
        new(0x21, 11, 116, "_TEXTURE_BITMAP_DATA_", "BITMAP", "TextureBitmapData", true),
        new(0x22, 11, 116, "_TEXTURE_MIP_BITMAP_DATA_", "STRMBMP", "TextureMipBitmapData", true),
        new(0x30, 13, 82, "_MATERIAL_", "MATERIAL", "Material", false),
        new(0x31, 13, 114, "_SHADER_", "SHADER", "Shader", true),
        new(0x40, 4, 5, "_ANIMATION_", "ANIM", "Animation", false),
        new(0x41, 4, 5, "_ANIMATION_STREAM_", "ANIMSTRM", "AnimationStream", false),
        new(0x42, 4, 8, "_ANIMATION_SCR_", "ANIMSCR", "AnimationScr", false),
        new(0x43, 4, 8, "_ANIMATION_SCRFIXUPS_", "ANIMSFIX", "AnimationScrFixups", false),
        new(0x44, 2, 5, "_ANM2_METADATA_", "ANM_META", "ANM2Header", false),
        new(0x45, 2, 5, "_ANM2_PAYLOAD_", "ANM_DATA", "ANM2Payload", false),
        new(0x46, 2, 5, "_ANM2_FALLBACK_", "ANM_FLBK", "ANM2Fallback", false),
        new(0x47, 140, 11, "_ANIM_GRAPH_BANK_", "ANMGRAPH", "AnimGraphBank", false),
        new(0x48, 140, 11, "_ANIM_GRAPH_BANK_FIXUPS_", "AGRPHFIX", "AnimGraphBankFixups", false),
        new(0x49, 4, 14, "_ANIM_CUSTOM_RESOURCE_", "ACSTMRES", "AnimCustomResource", false),
        new(0x4A, 4, 14, "_ANIM_CUSTOM_RESOURCE_FIXUPS_", "ACRESFIX", "AnimCustomResourceFixups", false),
        new(0x51, 2, 178, "_GPUFX_", "GPUFX", "GpuFx", false),
        new(0x55, 2, 77, "_ENV_BIN_", "ENV_BIN", "EnvprobeBin", false),
        new(0x56, 2, 77, "_VXL_BIN_", "VXL_BIN", "VoxelizerBin", false),
        new(0x5A, 2, 122, "_AREA_", "AREA", "Area", false),
        new(0x60, 2, 148, "_PREFAB_TEXT_", "PRFBTXT", "PrefabText", false),
        new(0x61, 8, 148, "_PREFAB_", "PREFAB", "Prefab", false),
        new(0x62, 8, 148, "_PREFAB_DATA_FIXUPS_", "PRFBFXUP", "PrefabFixUps", false),
        new(0x65, 2, 18, "_SOUND_", "SOUND", "Sound", false),
        new(0x66, 2, 18, "_SOUND_MUSIC_", "MUSIC", "Music", false),
        new(0x67, 2, 18, "_SOUND_SPEECH_", "SPEECH", "Speech", false),
        new(0x68, 2, 18, "_SOUND_STREAM_", "SNDSTRM", "SFX_stream", false),
        new(0x69, 2, 18, "_SOUND_LOCAL_", "SNDLOCAL", "SFX_local", false),
        new(0xF0, 5, 115, "_VERTEX_DATA_", "VERTEXES", "VertexData", true),
        new(0xF1, 4, 115, "_INDEX_DATA_", "INDEXES", "IndexData", true),
        new(0xF2, 4, 115, "_GEOMETRY_DATA_", "GEOMETRY", "GeometryData", true),
        new(0xF3, 2, 115, "_CLOTH_DATA_", "CLOTH", "ClothData", true),
        new(0xF8, 8, 75, "_TINY_OBJECTS_", "TINYOBJS", "TinyObjects", false),
        new(0xFF, 2, 107, "_BUILDER_INFORMATION_", "BUILDER", "BuilderInformation", false),
    ];

    private static readonly ResType?[] ById = BuildIndex();
    private static readonly string[] Labels = BuildLabels();

    private static ResType?[] BuildIndex()
    {
        var a = new ResType?[256];
        foreach (var r in Rows) a[r.Id] = r;
        return a;
    }

    private static string[] BuildLabels()
    {
        var a = new string[256];
        for (int i = 0; i < 256; i++) a[i] = ById[i] is { } t ? t.Pretty : $"Unknown_{i:X2}";
        return a;
    }

    public static IReadOnlyList<ResType> All => Rows;

    public static ResType? Get(byte id) => ById[id];

    /// <summary>Pretty name, e.g. "Mesh" — never allocates.</summary>
    public static string Name(byte id) => Labels[id];

    /// <summary>"0x10 Mesh" — the label used in filters and columns.</summary>
    public static string Label(byte id) => $"0x{id:X2} {Labels[id]}";

    /// <summary>Folder name the extractor uses per logical type.</summary>
    public static string FamilyDir(byte id) => id switch
    {
        0x10 => "mesh", 0x20 => "texture", 0x40 => "anim", 0x42 => "animscr", 0x47 => "animgraph",
        0x49 => "animcustom", 0x55 => "envprobe", 0x56 => "voxelizer", 0x5A => "area", 0x61 => "prefab",
        _ => "other",
    };
}
