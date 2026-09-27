namespace Nightrunner.Core.Prefab;

/// <summary>
/// The byte offsets the decoder reads that differ between the two games' builds of <c>cbs::Prefab</c> and
/// <c>cbs::PrefabEntityComponent</c>. Dying Light: The Beast offsets come from the prefab format notes (§7);
/// Dying Light 2 offsets were measured on the shipped packs (no DL2 binary was read): every root pointer field of a
/// DLTB <c>cbs::Prefab</c> from <c>m_pBaseClass</c> on sits 0x38 bytes earlier in the 0x228-byte DL2 root (the
/// field census matches slot kind and target class at every one of them), and the DL2 entity component is 0x120
/// bytes: the DLTB layout without the forced GUID at +0x120.
/// </summary>
public sealed record PrefabLayout(
    string Game,
    int RootStride,              // cbs::Prefab size and root spacing
    int RootFields,              // m_Fields
    int RootBaseClass,           // m_pBaseClass
    int RootInterfaces,          // m_PrefabInterfaces
    int RootComponents,          // m_Components
    int RootProperties,          // PrefabPropertySet vector
    int RootContainerSize,       // PrefabPropertySet container size (u32)
    int RootPipesIn,
    int RootPipesOut,
    int RootPropertyBindings,
    int RootPipeBindings,
    int RootVirtualFields,
    int RootDomFormat,           // u8 source DOM format, u8 flags after it
    int EntitySize,
    int? EntityForcedGuid)
{
    public static readonly PrefabLayout Dltb = new("Dying Light: The Beast", 0x260, 0x28, 0x120, 0x198, 0x1A8, 0x1B8, 0x1C8,
                                                   0x1D0, 0x1E0, 0x1F0, 0x210, 0x230, 0x258, 0x128, 0x120);

    public static readonly PrefabLayout Dl2 = new("Dying Light 2", 0x228, 0x28, 0xE8, 0x160, 0x170, 0x180, 0x190,
                                                  0x198, 0x1A8, 0x1B8, 0x1D8, 0x1F8, 0x220, 0x120, null);

    public bool IsDl2 => ReferenceEquals(this, Dl2);

    /// <summary>
    /// The layout of a parsed container: DLTB when <see cref="PrefabContainer.LayoutProblem"/> reports none, DL2 when
    /// every prefab root is a 0x228-byte <c>cbs::Prefab</c> at a 0x228 stride and the secondary image holds only the
    /// four DL2 element classes; anything else is refused by name.
    /// </summary>
    public static PrefabLayout Detect(PrefabContainer c)
    {
        var dltb = c.LayoutProblem();
        if (dltb is null) return Dltb;
        if (Dl2Problem(c) is { } why)
            throw new PrefabFormatException($"unsupported Prefabs layout: {dltb}; not the Dying Light 2 layout either: {why}");
        return Dl2;
    }

    /// <summary>Null when the container has the Dying Light 2 layout; otherwise why not.</summary>
    public static string? Dl2Problem(PrefabContainer c)
    {
        if (c.Records.Count < c.RootCount) return $"{c.Records.Count} records < {c.RootCount} roots";
        var spans = c.Spans();
        for (int i = 0; i < c.PrefabCount; i++)
        {
            var r = c.Records[i];
            if (r.ClassRaw != PrefabClasses.Prefab || (r.FlagsRaw & PrefabContainer.RecInSecondary) != 0)
                return $"root {i} is class 0x{r.ClassRaw:X8}, not a primary cbs::Prefab";
            if (r.Offset != (uint)(Dl2.RootStride * i)) return $"root {i} at 0x{r.Offset:X}, not 0x{Dl2.RootStride * i:X} (0x228 stride)";
            if (spans[i].End - spans[i].Start < Dl2.RootStride) return $"root {i} spans 0x{spans[i].End - spans[i].Start:X} bytes (< 0x228)";
        }
        foreach (var r in c.Records)
            if ((r.FlagsRaw & PrefabContainer.RecInSecondary) != 0 && !PrefabClasses.IsResolveElement(r.ClassRaw) && r.ClassRaw != PrefabClasses.PairElement)
                return $"class 0x{r.ClassRaw:X8} in the secondary image (DL2 holds only B1000000/C0000042/C0000043/C0000070)";
        return null;
    }
}
