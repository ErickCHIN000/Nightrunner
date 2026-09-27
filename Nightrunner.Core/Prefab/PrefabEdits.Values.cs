using System.Buffers.Binary;
using System.Text;
using Nightrunner.Core.Mesh.ClassReader;

namespace Nightrunner.Core.Prefab;

/// <summary>
/// One edit of a <c>Prefabs</c> resource, as a project stores it. <see cref="Op"/>: <c>transform</c>
/// (<see cref="Translate"/>/<see cref="Rotate"/>/<see cref="Scale"/>), <c>text</c> (<see cref="Field"/> ←
/// <see cref="Text"/>), <c>scalar</c> (<see cref="Field"/> ← <see cref="Value"/>), <c>active</c>
/// (<see cref="Active"/>), <c>remove</c> (a child entity) or <c>rename</c> (the prefab ← <see cref="Text"/>). The
/// prefab is named, the component is its pcid.
/// </summary>
public sealed record PrefabEditOp(string Op, string Prefab, uint Pcid = 0)
{
    public string? Field { get; init; }
    public string? Text { get; init; }
    public double? Value { get; init; }
    public bool? Active { get; init; }
    public Vec3? Translate { get; init; }
    public Vec3? Rotate { get; init; }
    public Vec3? Scale { get; init; }

    public override string ToString() => Op switch
    {
        "transform" => $"{Prefab} #{Pcid}: transform T{Translate} R{Rotate} S{Scale}",
        "text" => $"{Prefab} #{Pcid}: {Field} = {Text}",
        "scalar" => $"{Prefab} #{Pcid}: {Field} = {Value}",
        "active" => $"{Prefab} #{Pcid}: active = {Active}",
        "remove" => $"{Prefab} #{Pcid}: remove",
        "rename" => $"{Prefab}: rename to {Text}",
        _ => $"{Prefab}: {Op}",
    };
}

// Value edits on the decoded model, written back into the container. Each changes only the bytes it names (plus
// appended resolve elements / text and, for an inserted value, the relocation that insertion needs), so an unedited
// byte stays identical. None of them validates the whole container (that is the caller's step: Apply validates once,
// the project build validates and re-parses). Nothing produced here has been loaded in the game.
public static partial class PrefabEdits
{
    public const string SelfActiveClass = "cbs::CComponent", SelfActiveField = "m_SelfActive";

    /// <summary>Applies edits in order, then validates once. Throws <see cref="PrefabFormatException"/> naming the first edit that is refused.</summary>
    public static void Apply(PrefabContainer c, IEnumerable<PrefabEditOp> ops)
    {
        foreach (var op in ops)
        {
            try
            {
                int prefab = Index(c, op.Prefab);
                switch (op.Op)
                {
                    case "transform":
                        var now = Component(c, prefab, op.Pcid).Xform ?? throw new PrefabFormatException("the component has no transform");
                        SetTransform(c, prefab, op.Pcid, new PrefabXform(op.Translate ?? now.Translate, op.Rotate ?? now.Rotate, op.Scale ?? now.Scale));
                        break;
                    case "text": SetText(c, prefab, op.Pcid, op.Field ?? "", op.Text ?? ""); break;
                    case "scalar": SetScalar(c, prefab, op.Pcid, op.Field ?? "", op.Value ?? throw new PrefabFormatException("no value")); break;
                    case "active": SetActive(c, prefab, op.Pcid, op.Active ?? throw new PrefabFormatException("no value")); break;
                    case "remove": RemoveEntity(c, prefab, op.Pcid); break;
                    case "rename": Rename(c, prefab, op.Text ?? ""); break;
                    default: throw new PrefabFormatException($"unknown edit '{op.Op}'");
                }
            }
            catch (PrefabFormatException e)
            {
                throw new PrefabFormatException($"{op}: {e.Message}");
            }
        }
        c.Validate();
    }

    /// <summary>
    /// Whether a decoded (re-parsed) resource shows an edit's intended value; null when it does, else what differs.
    /// Edits later in the same list may legitimately overwrite earlier ones, so a caller checks the final state.
    /// </summary>
    public static string? Check(PrefabDocument doc, PrefabEditOp op)
    {
        string name = op.Prefab;
        if (op.Op == "rename") return doc.Find(op.Text ?? "") is null ? $"no prefab '{op.Text}' after the edit" : null;
        var root = doc.Find(name);
        // a later rename may have moved the name: then the edit cannot be checked by name
        if (root is null) return null;
        var comp = root.Components.FirstOrDefault(x => x.Pcid == op.Pcid);
        switch (op.Op)
        {
            case "remove": return comp is null ? null : $"pcid {op.Pcid} is still a component";
            case "transform":
                if (comp?.Xform is not { } x) return $"pcid {op.Pcid} has no transform";
                if (op.Translate is { } t && t != x.Translate) return $"translate reads {x.Translate}";
                if (op.Rotate is { } r && r != x.Rotate) return $"rotate reads {x.Rotate}";
                if (op.Scale is { } s && s != x.Scale) return $"scale reads {x.Scale}";
                return null;
            case "active":
                return comp?.SelfActive == op.Active ? null : $"m_SelfActive reads {comp?.SelfActive}";
        }
        var v = new[] { comp?.Values, comp?.ProxyValues }.OfType<PrefabValues>().SelectMany(b => b.Entries)
            .FirstOrDefault(e => e.Key is { } k && (k == op.Field || k.EndsWith("::" + op.Field, StringComparison.Ordinal)));
        return op.Op switch
        {
            "text" => v?.Text == op.Text ? null : $"{op.Field} reads '{v?.Text}'",
            "scalar" => v?.Scalar is { } d && (v.Type == 0x09 ? (float)d == (float)op.Value! : d == op.Value) ? null : $"{op.Field} reads {v?.Scalar}",
            _ => $"unknown edit '{op.Op}'",
        };
    }

    /// <summary>Index of the prefab named <paramref name="name"/> in this resource.</summary>
    public static int Index(PrefabContainer c, string name)
    {
        c.RequireDltbLayout();
        var tx = new PrefabText(c);
        for (int i = 0; i < c.PrefabCount; i++)
            if (tx.PstringAt(false, (int)c.Records[i].Offset + 0x10) == name) return i;
        throw new PrefabFormatException($"no prefab '{name}' in this resource");
    }

    /// <summary>The component with <paramref name="pcid"/> of prefab <paramref name="prefab"/> (refused when absent or not unique).</summary>
    public static PrefabComponent Component(PrefabContainer c, int prefab, uint pcid)
    {
        var root = Root(c, prefab);
        var hits = root.Components.Where(x => x.Pcid == pcid).ToList();
        return hits.Count switch
        {
            1 => hits[0],
            0 => throw new PrefabFormatException($"prefab '{root.Name}' has no component with pcid {pcid}"),
            _ => throw new PrefabFormatException($"prefab '{root.Name}' has {hits.Count} components with pcid {pcid}"),
        };
    }

    private static PrefabRoot Root(PrefabContainer c, int prefab)
    {
        c.RequireDltbLayout();
        if (prefab < 0 || prefab >= c.PrefabCount) throw new PrefabFormatException($"no prefab {prefab} (the resource has {c.PrefabCount})");
        return PrefabDecoder.Decode(c).Prefabs[prefab];
    }

    // ---- transform ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes translate / rotate (degrees) / scale of an entity, xform or hierarchy component (nine floats at +0x40,
    /// in place). The entity's extents (+0xD0/+0xE8) are left as they were.
    /// </summary>
    public static void SetTransform(PrefabContainer c, int prefab, uint pcid, PrefabXform x)
    {
        var comp = Component(c, prefab, pcid);
        if (comp.Xform is null)
            throw new PrefabFormatException($"{comp.ClassName} (pcid {pcid}) has no transform (only entity, xform and hierarchy components do)");
        var tx = new PrefabText(c);
        for (int o = 0x40; o < 0x64; o += 8)
            if (tx.SlotAt(false, comp.Offset + (o & ~7)) >= 0) throw new PrefabFormatException($"pcid {pcid}: a pointer slot lies in the transform bytes");
        float[] f = [x.Translate.X, x.Translate.Y, x.Translate.Z, x.Rotate.X, x.Rotate.Y, x.Rotate.Z, x.Scale.X, x.Scale.Y, x.Scale.Z];
        if (f.Any(v => !float.IsFinite(v))) throw new PrefabFormatException("a transform value is not finite");
        for (int i = 0; i < 9; i++) BinaryPrimitives.WriteSingleLittleEndian(c.Primary.AsSpan(comp.Offset + 0x40 + 4 * i), f[i]);
    }

    // ---- values -------------------------------------------------------------------------------------------------

    /// <summary>The value entry of <paramref name="field"/> (key <c>field</c> or <c>class::field</c>) and where its 16 bytes are.</summary>
    private static (PrefabValues Blob, int Entry, PrefabValue Value) Entry(PrefabComponent comp, string field)
    {
        if (string.IsNullOrEmpty(field)) throw new PrefabFormatException("no field named");
        var hits = new List<(PrefabValues, int, PrefabValue)>();
        foreach (var blob in new[] { comp.Values, comp.ProxyValues }.OfType<PrefabValues>())
            for (int i = 0; i < blob.Entries.Count; i++)
                if (blob.Entries[i].Key is { } k && (k == field || k.EndsWith("::" + field, StringComparison.Ordinal)))
                    hits.Add((blob, blob.Offset + 8 + 16 * i, blob.Entries[i]));
        return hits.Count switch
        {
            1 => hits[0],
            0 => throw new PrefabFormatException($"pcid {comp.Pcid} has no value for {field} (adding values is supported for m_SelfActive only)"),
            _ => throw new PrefabFormatException($"pcid {comp.Pcid} has {hits.Count} values matching {field}"),
        };
    }

    /// <summary>
    /// Sets a text value (a mesh or skin name, a script …). A pstring value (kind-9 slot to a resolve element) is
    /// retargeted at an existing pstring element with the new text, or at one appended to the secondary image; a
    /// <c>string_base</c> value (tagged 0x2100 slot to primary text) gets its text appended to the primary image. So
    /// there is no length limit and only the 8-byte slot word changes in place. Empty text (a null pstring has no
    /// slot) and class/field references are refused.
    /// </summary>
    public static void SetText(PrefabContainer c, int prefab, uint pcid, string field, string text)
    {
        var comp = Component(c, prefab, pcid);
        var (_, entry, value) = Entry(comp, field);
        if (value.Form != PrefabValueForm.Text) throw new PrefabFormatException($"{field} is a {value.Form.ToString().ToLowerInvariant()} value, not text");
        if (string.IsNullOrEmpty(text)) throw new PrefabFormatException("empty text (a null value has no slot; not supported)");
        if (text.Contains('\0')) throw new PrefabFormatException("text cannot contain NUL");
        int at = entry + 8;
        var tx = new PrefabText(c);
        var p = tx.Pointer(false, at) ?? throw new PrefabFormatException($"{field}: no slot at its payload");
        byte kind = c.Slots[p.Index].Kind;
        if ((kind & PrefabContainer.KindIndirect) != 0)
        {
            int rec = tx.SecondaryRecordAt((int)p.Target.Target);
            if (rec < 0 || c.Records[rec].ClassRaw != PrefabClasses.PstringElement)
                throw new PrefabFormatException($"{field}: the value is a class or field reference, not a pstring");
            int e = FindPstring(c, tx, text) ?? AppendPstring(c, text);
            BinaryPrimitives.WriteUInt64LittleEndian(c.Primary.AsSpan(at), (ulong)e + 1);
        }
        else if (p.Target.Tag == PrefabContainer.StringTag && !p.Target.ToSecondary && kind == PrefabContainer.KindTagged)
        {
            int target = AppendPrimaryText(c, text);
            BinaryPrimitives.WriteUInt64LittleEndian(c.Primary.AsSpan(at), (ulong)(uint)(target + 1) | ((ulong)p.Target.Tag << 48));
        }
        else throw new PrefabFormatException($"{field}: slot kind {kind} tag 0x{p.Target.Tag:X4} is not a known text form");
        c.Sync();
    }

    /// <summary>
    /// Sets a scalar value whose storage the field object states: float (ERTTIType 0x09, the low 4 payload bytes) or
    /// bool (0x0B, the low byte, 0 or 1). Engine-keyed values (their width is the engine's, hole PF1) and every other
    /// type are refused.
    /// </summary>
    public static void SetScalar(PrefabContainer c, int prefab, uint pcid, string field, double value)
    {
        var comp = Component(c, prefab, pcid);
        var (_, entry, v) = Entry(comp, field);
        if (v.Form != PrefabValueForm.Scalar || v.Type is not (0x09 or 0x0B))
            throw new PrefabFormatException($"{field} is a {v.Form.ToString().ToLowerInvariant()} value" +
                                            (v.EngineKey ? " keyed by an engine field (width unknown)" : $" of type 0x{v.Type:X2}") + "; only float and bool field values are written");
        if (new PrefabText(c).SlotAt(false, entry + 8) >= 0) throw new PrefabFormatException($"{field}: a slot lies at the payload");
        var span = c.Primary.AsSpan(entry + 8);
        if (v.Type == 0x09)
        {
            if (!float.IsFinite((float)value)) throw new PrefabFormatException("not a finite float");
            BinaryPrimitives.WriteSingleLittleEndian(span, (float)value);
        }
        else
        {
            if (value is not (0 or 1)) throw new PrefabFormatException("a bool is 0 or 1");
            span[0] = (byte)value;
        }
    }

    /// <summary>
    /// Sets <c>cbs::CComponent::m_SelfActive</c> of a component. An existing value (payload 0 or 1) is rewritten in
    /// its low byte. A missing one is inserted as a new 16-byte blob entry before the terminator (key: a kind-9 slot to
    /// the resource's <c>cbs::CComponent::m_SelfActive</c> field element, appended when it has none): the image grows by 16 bytes
    /// at that point, and every record, slot and pointer target after it moves by 16 (alignment kept). Insertion is
    /// refused when the blob holds out-of-line values after its terminator (their offsets are blob-relative and the
    /// engine-keyed ones cannot all be told apart) or when the component has no blob.
    /// </summary>
    public static void SetActive(PrefabContainer c, int prefab, uint pcid, bool active)
    {
        var comp = Component(c, prefab, pcid);
        var blob = comp.Values;
        int i = blob is null ? -1 : blob.Entries.ToList().FindIndex(v => v.EngineKey && v.Key == $"{SelfActiveClass}::{SelfActiveField}");
        if (blob is not null && i >= 0)
        {
            int at = blob.Offset + 8 + 16 * i + 8;
            ulong payload = BinaryPrimitives.ReadUInt64LittleEndian(c.Primary.AsSpan(at));
            if (payload > 1) throw new PrefabFormatException($"m_SelfActive payload 0x{payload:X} is not 0 or 1");
            if (new PrefabText(c).SlotAt(false, at) >= 0) throw new PrefabFormatException("a slot lies at the m_SelfActive payload");
            c.Primary[at] = active ? (byte)1 : (byte)0;
            return;
        }
        if (blob is null) throw new PrefabFormatException($"pcid {pcid} has no value container to add m_SelfActive to");
        int term = blob.Offset + 8 + 16 * blob.Entries.Count;
        if (BinaryPrimitives.ReadUInt64LittleEndian(c.Primary.AsSpan(term)) != ulong.MaxValue
            || BinaryPrimitives.ReadUInt64LittleEndian(c.Primary.AsSpan(term + 8)) != ulong.MaxValue)
            throw new PrefabFormatException($"pcid {pcid}: the value blob has no terminator after its entries");
        if (term + 16 != blob.Offset + blob.TotalSize)
            throw new PrefabFormatException($"pcid {pcid}: the value blob holds out-of-line values after its terminator; inserting would move them");
        var tx = new PrefabText(c);
        int element = FindFieldElement(c, tx, SelfActiveClass, SelfActiveField) ?? AppendFieldElement(c, SelfActiveClass, SelfActiveField, 0);
        InsertPrimary(c, term, 16);
        BinaryPrimitives.WriteUInt64LittleEndian(c.Primary.AsSpan(term), (ulong)element + 1);
        BinaryPrimitives.WriteUInt64LittleEndian(c.Primary.AsSpan(term + 8), active ? 1UL : 0UL);
        BinaryPrimitives.WriteUInt32LittleEndian(c.Primary.AsSpan(blob.Offset), blob.TotalSize + 16);
        c.Slots.Add(new Slot((uint)term, 9));      // indirect group is the tail of the slot table
        c.Sync();
    }

    // ---- remove a child entity ------------------------------------------------------------------------------------

    /// <summary>
    /// Removes a child entity from its prefab's component vector (+0x1A8): the later pointers move down one place
    /// with their slots, the last word is zeroed and its slot dropped, the vector's size and capacity (and the pointer
    /// array record's count, when the storage is a record) drop by one. The entity's own object stays in the image,
    /// unreferenced by the prefab (still constructed at load; not verified in game). Refused when anything names its
    /// pcid (parents, xform pointers, bindings, virtual or field destinations), when the prefab has interfaces or
    /// properties (their pcid lists are not decoded), or when it is the last component.
    /// </summary>
    public static void RemoveEntity(PrefabContainer c, int prefab, uint pcid)
    {
        var root = Root(c, prefab);
        var comp = Component(c, prefab, pcid);
        if (comp.Entity is null) throw new PrefabFormatException($"pcid {pcid} is a {comp.ClassName}, not a child entity");
        var why = new List<string>();
        foreach (var x in root.Components.Where(x => x.Pcid != pcid))
        {
            if (x.Entity?.ParentPcid == pcid) why.Add($"entity pcid {x.Pcid} has it as parent");
            if (x.HierarchyParent == pcid) why.Add($"hierarchy pcid {x.Pcid} has it as parent");
            if (x.XformComponent == pcid) why.Add($"pcid {x.Pcid} is placed by it");
        }
        foreach (var b in root.PropertyBindings.Concat(root.PipeBindings))
            if (b.SourcePcid == pcid || b.TargetPcid == pcid) why.Add($"binding {b.Source} → {b.Target}");
        foreach (var v in root.VirtualFields)
            if (v.Destinations.Any(d => d.Pcid == pcid)) why.Add($"virtual field {v.Name}");
        foreach (var f in root.Fields)
            if (f.Destinations?.Any(d => d.Pcid == pcid) == true) why.Add($"field {f.Name}");
        if (root.Interfaces.Count > 0) why.Add("the prefab has interfaces (their component lists are not decoded)");
        if (root.PropertyCount > 0) why.Add("the prefab has properties (not decoded)");
        if (why.Count > 0) throw new PrefabFormatException($"pcid {pcid} cannot be removed: {string.Join("; ", why.Take(4))}");

        int vec = root.Offset + 0x1A8;
        var tx = new PrefabText(c);
        var p = tx.Pointer(false, vec) ?? throw new PrefabFormatException("the prefab has no component vector");
        int data = (int)p.Target.Target;
        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(c.Primary.AsSpan(vec + 8));
        if (BinaryPrimitives.ReadUInt32LittleEndian(c.Primary.AsSpan(vec + 12)) != n) throw new PrefabFormatException("component vector capacity != size");
        if (n <= 1) throw new PrefabFormatException("the last component cannot be removed (an empty vector has no slot)");
        int k = -1;
        var slotOf = new int[n];
        for (int j = 0; j < n; j++)
        {
            slotOf[j] = tx.SlotAt(false, data + 8 * j);
            if (slotOf[j] < 0 || c.Slots[slotOf[j]].Kind != 0) throw new PrefabFormatException($"component vector element {j} has no plain slot");
            if (c.Target(c.Slots[slotOf[j]]).Target == comp.Offset) k = j;
        }
        if (k < 0) throw new PrefabFormatException($"pcid {pcid} is not in the component vector");
        for (int j = k + 1; j < n; j++)
            c.Slots[slotOf[j]] = c.Slots[slotOf[j]] with { Offset = (uint)(data + 8 * (j - 1)) };
        c.Primary.AsSpan(data + 8 * (k + 1), 8 * (n - 1 - k)).CopyTo(c.Primary.AsSpan(data + 8 * k));
        c.Primary.AsSpan(data + 8 * (n - 1), 8).Clear();
        c.Slots.RemoveAt(slotOf[k]);
        BinaryPrimitives.WriteUInt32LittleEndian(c.Primary.AsSpan(vec + 8), (uint)(n - 1));
        BinaryPrimitives.WriteUInt32LittleEndian(c.Primary.AsSpan(vec + 12), (uint)(n - 1));
        int rec = c.Records.FindIndex(r => (r.FlagsRaw & PrefabContainer.RecInSecondary) == 0 && r.Offset == data);
        if (rec >= 0)
        {
            var r = c.Records[rec];
            if (r.Count != n) throw new PrefabFormatException($"the pointer array record counts {r.Count}, the vector {n}");
            c.Records[rec] = r with { FlagsRaw = (r.FlagsRaw & ~PrefabContainer.RecCountMask) | (uint)(n - 1) };
        }
        c.Sync();
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    /// <summary>An existing pstring resolve element with exactly this text (elements may be shared), else null.</summary>
    private static int? FindPstring(PrefabContainer c, PrefabText tx, string text)
    {
        for (int i = 0; i < c.Records.Count; i++)
            if (c.Records[i].ClassRaw == PrefabClasses.PstringElement && c.Records[i].Count == 1 && tx.ElementText(i) == text)
                return (int)c.Records[i].Offset;
        return null;
    }

    private static int? FindFieldElement(PrefabContainer c, PrefabText tx, string cls, string field)
    {
        for (int i = 0; i < c.Records.Count; i++)
            if (c.Records[i].ClassRaw == PrefabClasses.FieldElement && c.Records[i].Count == 1 && tx.ElementText(i) == $"{cls}::{field}")
                return (int)c.Records[i].Offset;
        return null;
    }

    /// <summary>
    /// Appends a field-by-name resolve element (0xC0000043, 0x20: <c>{u64 0, string_base class, string_base field,
    /// u8 mode, 7 × 0}</c>) with its two texts after it (<c>{len, cap}</c> text NUL pad 8, each a tagged 0x2100 kind-14
    /// slot) at the end of the secondary image, as <see cref="AppendPstring"/> does for pstrings. <paramref name="mode"/>
    /// is the byte at +0x18, whose meaning is not known: the caller passes what the shipped elements of that field carry
    /// (every shipped <c>cbs::CComponent::m_SelfActive</c> element, 4 of 4 over the DLTB packs, has 0). Texts of 7 bytes
    /// or less would be inline string_bases; that form is not written here (refused).
    /// </summary>
    internal static int AppendFieldElement(PrefabContainer c, string cls, string field, byte mode)
    {
        if (c.Secondary is null) throw new PrefabFormatException("the resource has no secondary image to hold a resolve element");
        var a = Encoding.UTF8.GetBytes(cls);
        var b = Encoding.UTF8.GetBytes(field);
        if (a.Length <= 7 || b.Length <= 7) throw new PrefabFormatException($"{cls}::{field}: texts of 7 bytes or less are inline; not written");
        int e = (int)PrefabContainer.Align(c.Secondary.Length, 8);
        int ta = e + 0x20, sa = (int)PrefabContainer.Align(8 + a.Length + 1, 8);
        int tb = ta + sa, sb = (int)PrefabContainer.Align(8 + b.Length + 1, 8);
        var sec = new byte[tb + sb];
        c.Secondary.CopyTo(sec, 0);
        var s = sec.AsSpan();
        BinaryPrimitives.WriteUInt64LittleEndian(s[(e + 8)..], ((ulong)PrefabContainer.StringTag << 48) | (uint)(ta + 8 + 1));
        BinaryPrimitives.WriteUInt64LittleEndian(s[(e + 0x10)..], ((ulong)PrefabContainer.StringTag << 48) | (uint)(tb + 8 + 1));
        s[e + 0x18] = mode;
        foreach (var (at, text) in new[] { (ta, a), (tb, b) })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s[at..], (uint)text.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(s[(at + 4)..], (uint)text.Length);
            text.CopyTo(s[(at + 8)..]);
        }
        c.Secondary = sec;
        c.Records.Add(new Record((uint)e, PrefabClasses.FieldElement, PrefabContainer.RecReverse | PrefabContainer.RecInSecondary | 1));
        int firstIndirect = c.Slots.FindIndex(x => (x.Kind & PrefabContainer.KindIndirect) != 0);
        c.Slots.InsertRange(firstIndirect < 0 ? c.Slots.Count : firstIndirect, [new Slot((uint)(e + 8), 14), new Slot((uint)(e + 0x10), 14)]);
        c.Sync();
        return e;
    }

    /// <summary>
    /// Appends <c>{u32 len, u32 cap}</c> + text + NUL, padded to 16, at the end of the primary image (an inline tail of
    /// the last record's span, as shipped text is). Returns the text offset (the pointer target).
    /// </summary>
    internal static int AppendPrimaryText(PrefabContainer c, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        int e = (int)PrefabContainer.Align(c.Primary.Length, 8);
        var np = new byte[PrefabContainer.Align(e + 8 + bytes.Length + 1, 16)];
        c.Primary.CopyTo(np, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(np.AsSpan(e), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(np.AsSpan(e + 4), (uint)bytes.Length);
        bytes.CopyTo(np, e + 8);
        c.Primary = np;
        c.Sync();
        return e + 8;
    }

    /// <summary>
    /// Inserts <paramref name="count"/> zero bytes (a multiple of 16) into the primary image at <paramref name="at"/>
    /// and relocates: primary records at or after it, primary slot words at or after it, and every pointer word whose
    /// primary target is at or after it (tags kept). Refused inside the roots and when a slot targets
    /// <paramref name="at"/> itself (it would be ambiguous which side it belongs to).
    /// </summary>
    internal static void InsertPrimary(PrefabContainer c, int at, int count)
    {
        if (count % 16 != 0) throw new PrefabFormatException("insertions keep 16-byte alignment");
        if (at < PrefabContainer.RootStride * c.PrefabCount) throw new PrefabFormatException("cannot insert among the prefab roots");
        var targets = c.Slots.Select(c.Target).ToList();
        if (targets.Any(t => !t.ToSecondary && t.Target == at)) throw new PrefabFormatException($"a slot targets 0x{at:X}, where the insertion goes");
        var np = new byte[c.Primary.Length + count];
        c.Primary.AsSpan(0, at).CopyTo(np);
        c.Primary.AsSpan(at).CopyTo(np.AsSpan(at + count));
        var sec = c.Secondary;
        for (int i = 0; i < c.Slots.Count; i++)
        {
            var s = c.Slots[i];
            var t = targets[i];
            bool inSec = (s.Kind & PrefabContainer.KindSlotSecondary) != 0;
            long word = inSec ? s.Offset : s.Offset >= at ? s.Offset + count : s.Offset;
            if (!inSec && word != s.Offset) c.Slots[i] = s with { Offset = (uint)word };
            if (!t.ToSecondary && t.Target >= at)
            {
                ulong w = (ulong)(t.Target + count + 1) | (ulong)t.Tag << 48;
                BinaryPrimitives.WriteUInt64LittleEndian((inSec ? sec! : np).AsSpan((int)word), w);
            }
        }
        for (int i = 0; i < c.Records.Count; i++)
        {
            var r = c.Records[i];
            if ((r.FlagsRaw & PrefabContainer.RecInSecondary) == 0 && r.Offset >= at) c.Records[i] = r with { Offset = r.Offset + (uint)count };
        }
        c.Primary = np;
        c.Sync();
    }
}
