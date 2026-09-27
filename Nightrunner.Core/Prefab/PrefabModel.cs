namespace Nightrunner.Core.Prefab;

// Read-only object model of a decoded Prefabs resource (prefab format notes §7). Offsets are primary-image offsets.
// Names follow the notes; where the notes give a structure but no field name, the member says what was observed.

/// <summary>A decoded <c>Prefabs</c> resource: its roots, preset sets and how much of the slot table was read.</summary>
public sealed record PrefabDocument(
    IReadOnlyList<PrefabRoot> Prefabs,
    IReadOnlyList<PresetSet> PresetSets,
    IReadOnlyList<PrefabValues> EmbeddedValues,      // +0 of every EmbeddedObject (0xC000000D) record
    PrefabCoverage Coverage,
    IReadOnlyList<string> Warnings)
{
    /// <summary>The binary layout the resource was decoded with; null for a text prefab document.</summary>
    public PrefabLayout? Layout { get; init; }

    /// <summary>For a text prefab: the pak member and its format; null for a binary resource.</summary>
    public PrefabTextSource? TextSource { get; init; }

    public PrefabRoot? Find(string name) => Prefabs.FirstOrDefault(p => p.Name == name);

    /// <summary>Every value blob: components (+0x30), GameObjectProxy (+0x50) and embedded objects (+0).</summary>
    public IEnumerable<PrefabValues> AllValues =>
        Prefabs.SelectMany(p => p.Components).SelectMany(c => new[] { c.Values, c.ProxyValues }).OfType<PrefabValues>()
               .Concat(EmbeddedValues);
}

/// <summary>
/// Slot coverage: <see cref="Typed"/> slots were read as a field the notes name; <see cref="Generic"/> slots were only
/// resolved (text, object or inline target) by the generic pass, without a field meaning. Per class, the untyped
/// slots in <see cref="UntypedByClass"/> (key: class id of the record whose bytes hold the slot, 0 for tails).
/// </summary>
public sealed record PrefabCoverage(int Total, int Typed, int Generic, IReadOnlyDictionary<uint, int> UntypedByClass,
                                   IReadOnlyList<int> UntypedSlots)
{
    public double TypedFraction => Total == 0 ? 1 : (double)Typed / Total;
}

/// <summary>A <c>cbs::Prefab</c> root (0xC0000033, 0x260 B).</summary>
public sealed record PrefabRoot(
    int Index,
    int Offset,
    string? Name,                                    // +0x10 m_ClassName (registry key)
    string? BaseClass,                               // +0x120 m_pBaseClass (kind 9 → 0xC0000042)
    byte DomFormat,                                  // +0x258 source DOM format
    byte Flags,                                      // +0x259
    IReadOnlyList<PrefabField> Fields,               // +0x28 m_Fields
    IReadOnlyList<PrefabComponent> Components,       // +0x1A8 m_Components
    IReadOnlyList<PrefabInterface> Interfaces,       // +0x198 m_PrefabInterfaces
    int PropertyCount,                               // +0x1B8 PrefabPropertySet vector size
    uint PropertyContainerSize,                      // +0x1C8
    IReadOnlyList<PrefabPipe> PipesIn,               // +0x1D0
    IReadOnlyList<PrefabPipe> PipesOut,              // +0x1E0
    IReadOnlyList<PrefabBinding> PropertyBindings,   // +0x1F0
    IReadOnlyList<PrefabBinding> PipeBindings,       // +0x210
    IReadOnlyList<PrefabVirtualField> VirtualFields) // +0x230 m_VirtualFields
{
    public IEnumerable<PrefabComponent> Entities => Components.Where(c => c.Entity is not null);

    /// <summary>Text prefab only: the document's <c>Version</c> (null when absent, and for binary prefabs).</summary>
    public int? TextVersion { get; init; }

    /// <summary>
    /// Text prefab only: members the model has no place for (<c>PrefabEditorData</c>, <c>RuntimeData.Extents</c>,
    /// <c>Interface.Properties</c>, …), each kept as its compact JSON text. Empty for binary prefabs.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> TextOther { get; init; } = [];
}

/// <summary>A <c>CRTTIField</c> object owned by a prefab (0xC004xxxx virtual, 0xC005xxxx external).</summary>
public sealed record PrefabField(
    int Offset,
    uint ClassId,
    string? Name,                // +0x08
    ushort Type,                 // +0x12 ERTTIType (== low 16 bits of the class id on shipped data)
    ushort Flags,                // +0x14
    uint EditFlags,              // +0x18
    string? Init,                // +0x20 init string ("f:EDIT;e:...;")
    int OwnerPrefab,             // +0x28 → prefab index, −1 if none
    int? DefaultsOffset,         // +0x30 → 0xB0000002 record (layout beyond +0/+0x10 unknown, PF2)
    IReadOnlyList<PrefabDestination>? Destinations,   // +0x50 (CRTTIFieldVirtual with the 0x68 layout only)
    string? TargetClass)         // +0x60 CRTTI* (class name, or "prefab:NAME" when it points at a prefab)
{
    public string TypeName => PrefabClasses.TypeName(Type);
}

/// <summary>Where a virtual field forwards: an engine field (<c>class::field</c>) or a prefab field, on a pcid.</summary>
public sealed record PrefabDestination(string? Field, uint Pcid);

/// <summary>A <c>PrefabElement</c> component (base 0x40), with the xform and entity parts when its class has them.</summary>
public sealed record PrefabComponent(
    int Offset,
    uint ClassId,
    uint Pcid,                   // +0x08 (inline form: u32 at +0x0C)
    uint Flags,                  // +0x10
    int OwnerPrefab,             // +0x18 → prefab index
    string? ComponentClass,      // +0x20 CRTTI* (e.g. CoMeshRender)
    bool HasEmbeddedObjects,     // +0x28 SEmbeddedObjects*
    PrefabValues? Values,        // +0x30 → GamePrefabValuesContainer
    PrefabValues? ProxyValues,   // GameObjectProxy +0x50 → a second container
    PrefabXform? Xform,
    PrefabEntity? Entity)
{
    public string ClassName => PrefabClasses.Name(ClassId);

    /// <summary><c>PrefabHierarchyComponent</c> (0x78): its parent node's pcid, u32 at +0x68 (observed, not in the notes).</summary>
    public uint? HierarchyParent { get; init; }

    /// <summary>The component's <c>cbs::CComponent::m_SelfActive</c> value when set (0 = starts inactive, e.g. damage variants).</summary>
    public bool? SelfActive => IsText
        ? Values?.Entries.FirstOrDefault(v => v.Key == "m_SelfActive") is { Text: { } t } ? t.Trim() != "0" : null
        : Values?.Entries.FirstOrDefault(v => v.Key == "cbs::CComponent::m_SelfActive") is { } v ? (v.Payload & 0xFF) != 0 : null;

    /// <summary>
    /// Read from a text prefab: <see cref="Offset"/> is −1 − ordinal, <see cref="ClassId"/> is
    /// <see cref="PrefabClasses.EntityComponent"/> for <c>CEntity</c> and 0 otherwise (the text names the component
    /// class only), value keys are the text's field names (no <c>class::</c> prefix) and values are text.
    /// </summary>
    public bool IsText { get; init; }

    /// <summary>
    /// <c>m_XformComponent</c>: the pcid of the component whose transform a mesh, area, point or spawner uses (text:
    /// the named field; binary: u32 at +0x40, or +0x1A8 of 0xC0000028 — measured, see <see cref="PrefabDecoder"/>).
    /// Null when absent or 0.
    /// </summary>
    public uint? XformComponent { get; init; }

    /// <summary>
    /// Text prefab: every <c>PrefabFieldsNative</c> entry (<see cref="PrefabValue.Type"/> = its ERTTIType) or older
    /// <c>PrefabFields</c> text entry, including the ones also read into <see cref="Pcid"/>, <see cref="Xform"/> and
    /// <see cref="Entity"/>. Empty for binary components (their native fields are the C++ object's bytes).
    /// </summary>
    public IReadOnlyList<PrefabValue> NativeFields { get; init; } = [];

    /// <summary>Text prefab: component members the model has no place for (<c>Presets</c>, <c>EmbeddedObject</c>, …) as JSON text, in order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> TextOther { get; init; } = [];
}

public readonly record struct Vec3(float X, float Y, float Z)
{
    public override string ToString() => $"({X:G6}, {Y:G6}, {Z:G6})";
}

/// <summary><c>PrefabXformComponent</c> (0x68): translate, rotate (Euler, degrees) and scale.</summary>
public sealed record PrefabXform(Vec3 Translate, Vec3 Rotate, Vec3 Scale);

/// <summary><c>PrefabEntityComponent</c> (0xC000000F, 0x128): an instance of another prefab.</summary>
public sealed record PrefabEntity(
    string? Name,                // +0x68
    string? PrefabName,          // +0x70
    string? PresetNames,         // +0x78 (';'-separated)
    string? Configurations,      // +0x80
    int EntityPrefab,            // +0x98 m_EntityPrefab → prefab index in the same image (kind 0), else −1
    string? EntityPrefabClass,   // +0x98 as a kind-9 class element: the prefab is found by class name at load
                                 // (another pack's prefab; every pack but common_prefabs_pc does this)
    uint ParentPcid,             // +0xB8
    float[] ExtentsA,            // +0xD0 (6 floats)
    float[] ExtentsB,            // +0xE8 (6 floats)
    long ForcedGuid);            // +0x120 (−1 on disk); 0 on Dying Light 2 (its entity component is 0x120 bytes)

/// <summary>An element of <c>m_PrefabInterfaces</c> (0x28): name, class and how many components it lists.</summary>
public sealed record PrefabInterface(string? Name, string? Class, int ComponentCount);

/// <summary><c>PrefabMessagePipe</c> {pstring, u64 id}.</summary>
public sealed record PrefabPipe(string? Name, ulong Id);

/// <summary>A property or pipe binding {u32 srcPcid, u32 dstPcid, pstring, pstring} (0x20 per element on disk).</summary>
public sealed record PrefabBinding(uint SourcePcid, uint TargetPcid, string? Source, string? Target);

/// <summary>An entry of <c>m_VirtualFields</c>: name, init text and where it forwards.</summary>
public sealed record PrefabVirtualField(string? Name, string? Init, IReadOnlyList<PrefabVirtualDestination> Destinations);

public sealed record PrefabVirtualDestination(string? Field, ulong Pcid);

/// <summary>A <c>GamePrefabValuesContainer</c> blob (§7.1).</summary>
public sealed record PrefabValues(int Offset, uint TotalSize, uint Alignment, IReadOnlyList<PrefabValue> Entries);

public enum PrefabValueForm
{
    /// <summary>Raw 8-byte payload whose width the file does not state (engine-field keys, PF1).</summary>
    Raw,
    /// <summary>Inline scalar of a known type (float, bool).</summary>
    Scalar,
    /// <summary>Inline little-endian payload of a type in CHAR..VEC2 (0x01–0x0D) whose exact width is not named.</summary>
    Inline,
    /// <summary>A pstring (kind-9 slot) or a <c>ttl::string_base</c> pointer (tag 0x2100).</summary>
    Text,
    /// <summary>A pointer to an object in the image (entity / embedded object).</summary>
    Object,
    /// <summary>Out-of-line bytes at blob + payload (known width).</summary>
    OutOfLine,
}

/// <summary>
/// One blob entry. <see cref="Key"/> is the field name (field-object key) or <c>class::field</c> (engine key);
/// <see cref="Type"/> is known only for field-object keys.
/// </summary>
public sealed record PrefabValue(
    string? Key,
    bool EngineKey,
    ushort? Type,
    ulong Payload,
    PrefabValueForm Form,
    string? Text,
    double? Scalar,
    float[]? Floats);

/// <summary>A <c>CRttiClassPresets</c> root (0xC0000067, 0x38).</summary>
public sealed record PresetSet(int Index, int Offset, int ClassPrefab, string? ClassName, IReadOnlyList<PresetGroup> Groups);

/// <summary>A <c>CRttiPresetGroup</c> (0x70), keyed by the pair name in the set's group vector.</summary>
public sealed record PresetGroup(string? Key, string? Name, IReadOnlyList<Preset> Presets);

/// <summary>A <c>CRttiPreset</c> (0x30): key in the group, its own name (+0x10) and its field values (+0x20).</summary>
public sealed record Preset(string? Key, string? Name, IReadOnlyList<PresetFieldValue> Values);

/// <summary><c>RttiPreset::FieldValue</c> (0x30): field, 16-byte typed union (raw), text value, ext layout id.</summary>
public sealed record PresetFieldValue(string? Field, byte[] Union, string? Text, int LayoutId);
