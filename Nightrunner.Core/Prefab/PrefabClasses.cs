namespace Nightrunner.Core.Prefab;

/// <summary>A class id of the prefab ClassReader stream with the name and element stride the notes give.</summary>
public readonly record struct PrefabClass(uint Id, string Name, int? Stride);

/// <summary>
/// Class ids of the <c>Prefabs</c> stream (prefab format notes §3.2, §7). Only ids, names and strides the notes state
/// are listed; the full 336-id Construct catalog is not in this checkout, so every other id is shown as
/// <c>0xC00000xx</c> and has no stride. <c>0xC004xxxx</c> / <c>0xC005xxxx</c> carry the field's ERTTIType in the low
/// 16 bits.
/// </summary>
public static class PrefabClasses
{
    public const uint RawData = 0xB0000000;
    public const uint FieldDefaults = 0xB0000002;
    public const uint PstringElement = 0xB1000000;
    public const uint PrefabComponent = 0xC0000001;
    public const uint ComponentWithXformPtr = 0xC0000007;
    public const uint EmbeddedObject = 0xC000000D;
    public const uint EmbeddedObjectVector = 0xC000000E;
    public const uint EntityComponent = 0xC000000F;
    public const uint GameObjectProxy = 0xC0000011;
    public const uint HierarchyComponent = 0xC0000012;
    public const uint MeshRender = 0xC0000017;
    public const uint Property = 0xC000001E;
    public const uint Replicator = 0xC0000021;
    public const uint XformComponent = 0xC0000029;
    public const uint EmbeddedObjects = 0xC000002B;
    public const uint PointerArray = 0xC000002F;
    public const uint InterfaceInfo = 0xC0000030;
    public const uint PropertyBinding = 0xC0000031;
    public const uint PipeBinding = 0xC0000032;
    public const uint Prefab = 0xC0000033;
    public const uint MessagePipe = 0xC000003D;
    public const uint ClassElement = 0xC0000042;
    public const uint FieldElement = 0xC0000043;
    public const uint ClassPresets = 0xC0000067;
    public const uint PresetGroup = 0xC0000069;
    public const uint Preset = 0xC000006B;
    public const uint PresetFieldValue = 0xC000006D;
    public const uint PresetGroupValue = 0xC000006F;
    /// <summary>
    /// Dying Light 2 only: a fourth secondary-image element <c>{u64 out, string_base a, string_base b}</c> (0x18, text
    /// after it), the target of kind-9 slots in 0xB0000002 blocks (an entity's +0x90); its texts read as configuration
    /// pairs (e.g. <c>physics</c> / <c>Disabled</c>). Observed, not in the notes.
    /// </summary>
    public const uint PairElement = 0xC0000070;
    public const uint FieldVirtualBase = 0xC0040000;
    public const uint ExternalFieldBase = 0xC0050000;

    /// <summary>
    /// <c>CRTTIFieldVirtual&lt;T&gt;</c> stride for the four types the notes' table lists (bool, string, float, mtx34).
    /// §7 gives 0x68 for every T, but shipped records of types 0x14, 0x15, 0x34, 0x36, 0x48, 0x49, 0x4E, 0x4F, 0x58,
    /// 0x5A, 0x5B and 0x5D carry pointer fields past +0x68 (e.g. 0x48: a vector at +0x60, a field element at +0x70), so
    /// no stride is assumed for the other types.
    /// </summary>
    public const int FieldVirtualStride = 0x68;
    private static readonly HashSet<uint> FieldVirtual68 = [0xC0040009, 0xC004000B, 0xC004000C, 0xC0040020];

    private static readonly Dictionary<uint, PrefabClass> Known = new PrefabClass[]
    {
        new(RawData, "raw", null),
        new(FieldDefaults, "CRTTIField defaults (raw)", null),
        new(PstringElement, "ttl::pstring resolve element", 0x10),
        new(PrefabComponent, "cbs::PrefabComponent", 0x40),
        new(ComponentWithXformPtr, "ComponentWithXformPtr", null),
        new(EmbeddedObject, "EmbeddedObject", null),
        new(EmbeddedObjectVector, "cbs::PrefabEmbeddedObjectVector", 0x28),
        new(EntityComponent, "cbs::PrefabEntityComponent", 0x128),
        new(GameObjectProxy, "GameObjectProxy", null),
        new(HierarchyComponent, "cbs::PrefabHierarchyComponent", 0x78),
        new(MeshRender, "MeshRender", null),
        new(Property, "PrefabProperty", 0x30),
        new(Replicator, "Replicator", null),
        new(XformComponent, "XformComponent", null),
        new(EmbeddedObjects, "SEmbeddedObjects", null),
        new(PointerArray, "vector<PrefabElement*> storage", 8),
        new(InterfaceInfo, "PrefabInterfaceInfo", 0x28),
        new(PropertyBinding, "PropertyBinding", null),
        new(PipeBinding, "PipeBinding", null),
        new(Prefab, "cbs::Prefab", 0x260),
        new(MessagePipe, "MessagePipe", null),
        new(ClassElement, "class-by-name resolve element", 0x18),
        new(FieldElement, "field-by-name resolve element", 0x20),
        new(ClassPresets, "CRttiClassPresets", 0x38),
        new(PresetGroup, "CRttiPresetGroup", 0x70),
        new(Preset, "CRttiPreset", 0x30),
        new(PresetFieldValue, "RttiPreset::FieldValue", 0x30),
        new(PresetGroupValue, "preset GroupValue pair", null),
        new(PairElement, "string-pair resolve element (DL2)", 0x18),
    }.ToDictionary(c => c.Id);

    /// <summary>ERTTIType names the notes give (§3.2 table); every other type prints as its number.</summary>
    private static readonly Dictionary<ushort, string> TypeNames = new()
    {
        [0x09] = "float", [0x0B] = "bool", [0x0C] = "string", [0x20] = "mtx34",
    };

    public static string TypeName(ushort type) => TypeNames.TryGetValue(type, out var n) ? n : $"type 0x{type:X2}";

    public static bool IsFieldVirtual(uint id) => (id & 0xFFFF0000) == FieldVirtualBase;
    public static bool IsExternalField(uint id) => (id & 0xFFFF0000) == ExternalFieldBase;
    public static bool IsField(uint id) => IsFieldVirtual(id) || IsExternalField(id);
    public static bool IsResolveElement(uint id) => id is PstringElement or ClassElement or FieldElement;

    /// <summary>
    /// True for the namespaces the Construct switch dispatches on (§3.2): raw 0xB0000000/0xB0000002, the pstring
    /// element, 0xC0000000–0xC0000095, and the two field namespaces. Whether a given id inside them constructs is in
    /// the catalog, not here.
    /// </summary>
    public static bool InKnownNamespace(uint id) =>
        id is RawData or FieldDefaults or PstringElement
        || (id >= 0xC0000000 && id <= 0xC0000095)
        || IsField(id);

    public static int? Stride(uint id) =>
        Known.TryGetValue(id, out var c) ? c.Stride : FieldVirtual68.Contains(id) ? FieldVirtualStride : null;

    public static string Name(uint id)
    {
        if (Known.TryGetValue(id, out var c)) return c.Name;
        if (IsFieldVirtual(id)) return $"CRTTIFieldVirtual<{TypeName((ushort)id)}>";
        if (IsExternalField(id)) return $"CExternalRTTIField<{TypeName((ushort)id)}>";
        return $"0x{id:X8}";
    }

    public static bool IsNamed(uint id) => Known.ContainsKey(id) || IsField(id);
}
