using System.Buffers.Binary;

namespace Nightrunner.Core.Prefab;

/// <summary>
/// Decodes a parsed <see cref="PrefabContainer"/> into the read-only object model of <see cref="PrefabDocument"/>
/// (prefab format notes §6–§8). Only layouts the notes give are read as typed fields; every other slot is resolved by
/// a generic pass and counted in <see cref="PrefabCoverage"/>, so the coverage number says exactly how much of the
/// pointer graph has a meaning here. Never writes.
/// </summary>
/// <remarks>
/// Measured beyond the notes and used here (see the PrefabCheck census): bindings are 0x20 per element (the notes list
/// 0x18 bytes of members); a preset group's presets are {pstring key, CRttiPreset*} pairs at +0x18; a preset's field
/// values vector is at +0x20 and its own name pstring at +0x10. <c>CRTTIFieldVirtual</c> destinations (+0x50) and
/// CRTTI* (+0x60) are read only on the 0x68 layout (a tagged vector at +0x50); several ERTTITypes (0x14, 0x15, 0x34,
/// 0x36, 0x48, 0x49, 0x4E, 0x4F, 0x58, 0x5A, 0x5B, 0x5D) use a longer layout that is left to the generic pass.
/// </remarks>
public sealed class PrefabDecoder
{
    private const ushort TagFieldVector = 0x006C;
    private const int BindingStride = 0x20;

    private readonly PrefabContainer _c;
    private readonly PrefabLayout _l;
    private readonly PrefabText _t;
    private readonly bool[] _typed;
    private readonly Dictionary<uint, int> _recordAt = [];
    private readonly List<string> _warnings = [];

    private PrefabDecoder(PrefabContainer c, PrefabLayout layout)
    {
        _c = c;
        _l = layout;
        _t = new PrefabText(c);
        _typed = new bool[c.Slots.Count];
        for (int i = 0; i < c.Records.Count; i++)
            if ((c.Records[i].FlagsRaw & PrefabContainer.RecInSecondary) == 0) _recordAt.TryAdd(c.Records[i].Offset, i);
    }

    /// <summary>
    /// Decodes a <c>Prefabs</c> resource of either game (<see cref="PrefabLayout.Detect"/>); any other layout is
    /// refused by name.
    /// </summary>
    public static PrefabDocument Decode(PrefabContainer c) => new PrefabDecoder(c, PrefabLayout.Detect(c)).Run();

    private PrefabDocument Run()
    {
        var prefabs = new List<PrefabRoot>((int)_c.PrefabCount);
        for (int i = 0; i < _c.PrefabCount; i++) prefabs.Add(Root(i));
        var sets = new List<PresetSet>((int)_c.PresetCount);
        for (int i = 0; i < _c.PresetCount; i++) sets.Add(Presets(i));
        var embedded = new List<PrefabValues>();
        foreach (var r in _c.Records)
            if (r.ClassRaw == PrefabClasses.EmbeddedObject && (r.FlagsRaw & PrefabContainer.RecInSecondary) == 0
                && Obj((int)r.Offset) is int b and >= 0)
                embedded.Add(Values(b));
        MarkElementText();
        return new PrefabDocument(prefabs, sets, embedded, Coverage(), _warnings) { Layout = _l };
    }

    // ---- roots ----------------------------------------------------------------------------------------------

    private PrefabRoot Root(int index)
    {
        int b = (int)_c.Records[index].Offset;
        var fields = new List<PrefabField>();
        foreach (int f in PointerVector(b + _l.RootFields))
            if (Record(f) is { } fr && PrefabClasses.IsField(_c.Records[fr].ClassRaw)) fields.Add(Field(f, _c.Records[fr].ClassRaw));
            else _warnings.Add($"prefab {index}: m_Fields entry 0x{f:X} is not a field object");
        var comps = new List<PrefabComponent>();
        foreach (int o in PointerVector(b + _l.RootComponents))
            if (Record(o) is { } cr) comps.Add(Component(o, _c.Records[cr].ClassRaw));
            else _warnings.Add($"prefab {index}: component 0x{o:X} is not a record start");
        var interfaces = new List<PrefabInterface>();
        foreach (int e in Elements(b + _l.RootInterfaces, 0x28))
            interfaces.Add(new PrefabInterface(Pstr(e), ClassRef(e + 8), Vector(e + 0x18)?.Count ?? 0));
        int propCount = Vector(b + _l.RootProperties)?.Count ?? 0;
        var virtuals = new List<PrefabVirtualField>();
        foreach (int e in Elements(b + _l.RootVirtualFields, 0x20))
        {
            var dests = new List<PrefabVirtualDestination>();
            foreach (int d in Elements(e + 0x10, 0x10)) dests.Add(new PrefabVirtualDestination(Pstr(d), U64(d + 8)));
            virtuals.Add(new PrefabVirtualField(Pstr(e), Pstr(e + 8), dests));
        }
        return new PrefabRoot(
            index, b, Pstr(b + 0x10), ClassRef(b + _l.RootBaseClass), _c.Primary[b + _l.RootDomFormat], _c.Primary[b + _l.RootDomFormat + 1],
            fields, comps, interfaces, propCount, U32(b + _l.RootContainerSize),
            Pipes(b + _l.RootPipesIn), Pipes(b + _l.RootPipesOut), Bindings(b + _l.RootPropertyBindings), Bindings(b + _l.RootPipeBindings), virtuals);
    }

    private List<PrefabPipe> Pipes(int at) => Elements(at, 0x10).Select(e => new PrefabPipe(Pstr(e), U64(e + 8))).ToList();

    private List<PrefabBinding> Bindings(int at) =>
        Elements(at, BindingStride).Select(e => new PrefabBinding(U32(e), U32(e + 4), Pstr(e + 8), Pstr(e + 0x10))).ToList();

    private PrefabField Field(int o, uint classId)
    {
        int? defaults = Obj(o + 0x30) is int d and >= 0 ? d : null;
        List<PrefabDestination>? dests = null;
        string? target = null;
        // the 0x68 layout: +0x50 a tagged field vector, +0x60 a CRTTI* (class element or prefab)
        if (PrefabClasses.IsFieldVirtual(classId) && _t.Pointer(false, o + 0x50) is { } v && v.Target.Tag == TagFieldVector
            && (_t.ResolvedWith(false, o + 0x60).ClassId == PrefabClasses.ClassElement || PrefabAt(Peek(o + 0x60)) >= 0))
        {
            dests = Elements(o + 0x50, 0x10).Select(e => new PrefabDestination(FieldRef(e), U32(e + 8))).ToList();
            target = ClassRef(o + 0x60);
        }
        return new PrefabField(o, classId, Pstr(o + 8), U16(o + 0x12), U16(o + 0x14), U32(o + 0x18), Pstr(o + 0x20),
                               PrefabAt(Obj(o + 0x28)), defaults, dests, target);
    }

    private PrefabComponent Component(int o, uint classId)
    {
        ulong q = U64(o + 8);
        uint pcid;
        if ((q & 3) != 0) pcid = U32(o + 0xC);
        else
        {
            int p = Obj(o + 8);
            pcid = p >= 0 ? U32(p + 8) : 0;
        }
        int blob = Obj(o + 0x30);
        int proxy = classId == PrefabClasses.GameObjectProxy ? Obj(o + 0x50) : -1;
        PrefabXform? xf = null;
        PrefabEntity? ent = null;
        if (classId is PrefabClasses.EntityComponent or PrefabClasses.XformComponent or PrefabClasses.HierarchyComponent)
            xf = new PrefabXform(V3(o + 0x40), V3(o + 0x4C), V3(o + 0x58));
        if (classId == PrefabClasses.EntityComponent)
        {
            int child = PrefabAt(Peek(o + 0x98));
            string? byName = null;
            if (child >= 0) Obj(o + 0x98);
            else if (_t.ResolvedWith(false, o + 0x98).ClassId == PrefabClasses.ClassElement) byName = ClassRef(o + 0x98);
            ent = new PrefabEntity(Pstr(o + 0x68), Pstr(o + 0x70), Pstr(o + 0x78), Pstr(o + 0x80), child, byName,
                                   U32(o + 0xB8), Floats(o + 0xD0, 6), Floats(o + 0xE8, 6),
                                   _l.EntityForcedGuid is { } g ? (long)U64(o + g) : 0);
        }
        bool embedded = Obj(o + 0x28) >= 0;
        return new PrefabComponent(o, classId, pcid, U32(o + 0x10), PrefabAt(Obj(o + 0x18)), ClassRef(o + 0x20), embedded,
                                   blob >= 0 ? Values(blob) : null, proxy >= 0 ? Values(proxy) : null, xf, ent)
        {
            // PrefabHierarchyComponent (0x78) = the xform component's 0x68 bytes + u32 parent pcid at +0x68 (observed)
            HierarchyParent = classId == PrefabClasses.HierarchyComponent ? U32(o + 0x68) : null,
            XformComponent = XformPcidAt(classId) is { } xo && U32(o + xo) is var xp and not 0 ? xp : null,
        };
    }

    /// <summary>
    /// Where a component keeps <c>m_XformComponent</c> (u32 pcid), measured, not in the notes: +0x40 of the mesh render
    /// (0x17), mesh logic (0x16), mesh editor helper (0x15), area (0x03), box (0x05) and point (0x1D) records and +0x1A8
    /// of 0xC0000028 (CoWorldSpawner). On Dying Light 2 it equals the text prefab's <c>m_XformComponent</c> on 748/757
    /// mesh render, 757/766 mesh logic, 1,823/1,823 editor helper, 41/41 area and 17,025/17,025 world spawner
    /// components of the same name and pcid; in both games every non-zero value names a CoHierarchy* / CoWorldXform* /
    /// CoSkeleton component of the same prefab.
    /// </summary>
    private static int? XformPcidAt(uint classId) => classId switch
    {
        0xC0000003 or 0xC0000005 or 0xC0000015 or 0xC0000016 or PrefabClasses.MeshRender or 0xC000001D => 0x40,
        0xC0000028 => 0x1A8,
        _ => null,
    };

    // ---- value blobs (§7.1) ---------------------------------------------------------------------------------

    private PrefabValues Values(int b)
    {
        uint total = U32(b), align = U32(b + 4);
        var entries = new List<PrefabValue>();
        long end = Math.Min((long)b + total, _c.Primary.Length);
        bool terminated = false;
        for (int e = b + 8; e + 16 <= end; e += 16)
        {
            ulong k = U64(e), p = U64(e + 8);
            // DLTB ends the entries with {−1, −1} (§7.1); DL2 with {0, 0} (a null key; measured on every DL2 blob)
            if (_l.IsDl2 ? k == 0 && p == 0 && _t.Pointer(false, e) is null : k == ulong.MaxValue && p == ulong.MaxValue)
            {
                terminated = true;
                break;
            }
            string? key = null;
            ushort? type = null;
            bool engine = false;
            if (_t.Pointer(false, e) is { } kp)
            {
                if ((_c.Slots[kp.Index].Kind & PrefabContainer.KindIndirect) != 0)
                {
                    key = FieldRef(e);
                    engine = true;
                }
                else
                {
                    int f = Obj(e);
                    key = f >= 0 ? _t.PstringAt(false, f + 8) : null;
                    if (f >= 0 && Record(f) is { } fr && PrefabClasses.IsField(_c.Records[fr].ClassRaw)) type = U16(f + 0x12);
                }
            }
            entries.Add(Value(b, total, e + 8, key, engine, type, p));
        }
        if (!terminated) _warnings.Add($"value blob 0x{b:X}: no terminator inside total_size {total}");
        return new PrefabValues(b, total, align, entries);
    }

    private PrefabValue Value(int blob, uint total, int at, string? key, bool engine, ushort? type, ulong payload)
    {
        if (_t.Pointer(false, at) is { } pp)
        {
            if ((_c.Slots[pp.Index].Kind & PrefabContainer.KindIndirect) != 0)
            {
                var (text, _, _) = _t.ResolvedWith(false, at);
                Mark(pp.Index);
                return new PrefabValue(key, engine, type, payload, PrefabValueForm.Text, text, null, null);
            }
            if (pp.Target.Tag == PrefabContainer.StringTag)
            {
                Mark(pp.Index);
                string? sb = _t.LengthPrefixed(pp.Target.ToSecondary, (int)pp.Target.Target);
                return new PrefabValue(key, engine, type, payload, PrefabValueForm.Text, sb, null, null);
            }
            int obj = Obj(at);
            string? what = obj >= 0 && Record(obj) is { } r ? PrefabClasses.Name(_c.Records[r].ClassRaw) : null;
            return new PrefabValue(key, engine, type, payload, PrefabValueForm.Object, what, null, null);
        }
        switch (type)
        {
            case 0x09:
                return new PrefabValue(key, engine, type, payload, PrefabValueForm.Scalar, null,
                                       BitConverter.Int32BitsToSingle((int)(uint)payload), null);
            case 0x0B:
                return new PrefabValue(key, engine, type, payload, PrefabValueForm.Scalar, null, (byte)payload, null);
            case 0x20 when payload + 48 <= total:
                return new PrefabValue(key, engine, type, payload, PrefabValueForm.OutOfLine, null, null,
                                       Floats(blob + (int)payload, 12));
        }
        if (type is >= 0x01 and <= 0x0D)
            return new PrefabValue(key, engine, type, payload, PrefabValueForm.Inline, null, null, null);
        return new PrefabValue(key, engine, type, payload, PrefabValueForm.Raw, null, null, null);
    }

    // ---- presets --------------------------------------------------------------------------------------------

    private PresetSet Presets(int i)
    {
        int index = (int)_c.PrefabCount + i;
        int b = (int)_c.Records[index].Offset;
        int cls = PrefabAt(Obj(b + 8));
        var groups = new List<PresetGroup>();
        foreach (int e in Elements(b + 0x10, 0x10))
        {
            string? key = Pstr(e);
            int g = Obj(e + 8);
            if (g < 0) { groups.Add(new PresetGroup(key, null, [])); continue; }
            var presets = new List<Preset>();
            foreach (int pe in Elements(g + 0x18, 0x10))
            {
                string? pkey = Pstr(pe);
                int p = Obj(pe + 8);
                if (p < 0) { presets.Add(new Preset(pkey, null, [])); continue; }
                var values = Elements(p + 0x20, 0x30).Select(fv => new PresetFieldValue(
                    FieldRef(fv + 8), _c.Primary.AsSpan(fv + 0x10, 16).ToArray(), Pstr(fv + 0x20), (int)U32(fv + 0x28))).ToList();
                presets.Add(new Preset(pkey, Pstr(p + 0x10), values));
            }
            groups.Add(new PresetGroup(key, Pstr(g + 8), presets));
        }
        return new PresetSet(i, b, cls, cls >= 0 ? _t.PstringAt(false, (int)_c.Records[cls].Offset + 0x10) : null, groups);
    }

    // ---- typed reads (each marks the slot it consumes) --------------------------------------------------------

    private void Mark(int slot) { if (slot >= 0) _typed[slot] = true; }

    /// <summary>A pstring field: kind-9 slot to a B1 element, or null (zero word, no slot).</summary>
    private string? Pstr(int off)
    {
        var (text, cls, slot) = _t.ResolvedWith(false, off);
        if (slot >= 0 && cls == PrefabClasses.PstringElement) { Mark(slot); return text; }
        return null;
    }

    /// <summary>A CRTTI* field: kind 9 → class element text, or kind 0 → a prefab (prefixed "prefab:").</summary>
    private string? ClassRef(int off)
    {
        var (text, cls, slot) = _t.ResolvedWith(false, off);
        if (slot < 0) return null;
        if (cls == PrefabClasses.ClassElement) { Mark(slot); return text; }
        int p = PrefabAt(Peek(off));
        if (p < 0) return null;
        Mark(slot);
        return "prefab:" + _t.PstringAt(false, (int)_c.Records[p].Offset + 0x10);
    }

    /// <summary>A CRTTIField* field: kind 9 → <c>class::field</c>, or kind 0 → a field object's name.</summary>
    private string? FieldRef(int off)
    {
        var (text, cls, slot) = _t.ResolvedWith(false, off);
        if (slot < 0) return null;
        if (cls == PrefabClasses.FieldElement) { Mark(slot); return text; }
        int f = Peek(off);
        if (f < 0 || Record(f) is not { } r || !PrefabClasses.IsField(_c.Records[r].ClassRaw)) return null;
        Mark(slot);
        return _t.PstringAt(false, f + 8);
    }

    /// <summary>Target of a primary → primary slot (marks it), or −1.</summary>
    private int Obj(int off)
    {
        int t = Peek(off);
        if (t >= 0) Mark(_t.SlotAt(false, off));
        return t;
    }

    private int Peek(int off)
    {
        if (_t.Pointer(false, off) is not { } p || p.Target.ToSecondary || p.Target.Target < 0) return -1;
        return (int)p.Target.Target;
    }

    /// <summary>A <c>ttl::vector</c> (heap mode, tagged slot): data offset and size, or null when empty.</summary>
    private (int Data, int Count)? Vector(int off)
    {
        if (_t.Pointer(false, off) is not { } p || p.Target.ToSecondary || p.Target.Target < 0) return null;
        if ((_c.Slots[p.Index].Kind & PrefabContainer.KindTagged) == 0 || _c.Primary[off + 7] != 0)
        {
            _warnings.Add($"vector at 0x{off:X}: not a heap-mode tagged vector");
            return null;
        }
        Mark(p.Index);
        return ((int)p.Target.Target, (int)U32(off + 8));
    }

    private IEnumerable<int> Elements(int off, int stride)
    {
        if (Vector(off) is not { } v) yield break;
        for (int i = 0; i < v.Count; i++)
        {
            int e = v.Data + i * stride;
            if (e + stride > _c.Primary.Length) { _warnings.Add($"vector at 0x{off:X}: element {i} outside the image"); yield break; }
            yield return e;
        }
    }

    private List<int> PointerVector(int off) => Elements(off, 8).Select(Obj).Where(t => t >= 0).ToList();

    private int? Record(int offset) => _recordAt.TryGetValue((uint)offset, out int i) ? i : null;

    /// <summary>Prefab index of a root offset, or −1.</summary>
    private int PrefabAt(int target) =>
        target >= 0 && target % _l.RootStride == 0 && Record(target) is { } r && r < _c.PrefabCount
        && _c.Records[r].ClassRaw == PrefabClasses.Prefab ? r : -1;

    private uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(_c.Primary.AsSpan(o));
    private ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(_c.Primary.AsSpan(o));
    private ulong U64(int o) => BinaryPrimitives.ReadUInt64LittleEndian(_c.Primary.AsSpan(o));
    private float F32(int o) => BinaryPrimitives.ReadSingleLittleEndian(_c.Primary.AsSpan(o));
    private Vec3 V3(int o) => new(F32(o), F32(o + 4), F32(o + 8));
    private float[] Floats(int o, int n) => Enumerable.Range(0, n).Select(i => F32(o + 4 * i)).ToArray();

    // ---- coverage -------------------------------------------------------------------------------------------

    /// <summary>The text slots of every resolve element a typed kind-9 slot reached count as typed too.</summary>
    private void MarkElementText()
    {
        for (int i = 0; i < _typed.Length; i++)
        {
            var s = _c.Slots[i];
            if (!_typed[i] || (s.Kind & PrefabContainer.KindIndirect) == 0) continue;
            var t = _c.Target(s);
            int rec = _t.SecondaryRecordAt((int)t.Target);
            if (rec < 0) continue;
            Mark(_t.SlotAt(true, (int)t.Target + 8));
            if (_c.Records[rec].ClassRaw == PrefabClasses.FieldElement) Mark(_t.SlotAt(true, (int)t.Target + 0x10));
        }
    }

    private PrefabCoverage Coverage()
    {
        var primStarts = _recordAt.Keys.Order().ToArray();
        var secStarts = new List<(uint Off, uint Cls)>();
        foreach (var r in _c.Records)
            if ((r.FlagsRaw & PrefabContainer.RecInSecondary) != 0) secStarts.Add((r.Offset, r.ClassRaw));
        secStarts.Sort();
        var secKeys = secStarts.Select(x => x.Off).ToArray();
        int typed = 0, generic = 0;
        var byClass = new SortedDictionary<uint, int>();
        var untyped = new List<int>();
        for (int i = 0; i < _typed.Length; i++)
        {
            if (_typed[i]) { typed++; continue; }
            untyped.Add(i);
            var s = _c.Slots[i];
            var t = _c.Target(s);
            if (t.Target >= 0 && t.Target < _c.ImageOf(t.ToSecondary).Length) generic++;
            uint cls;
            if (t.InSecondary)
            {
                int k = Array.BinarySearch(secKeys, (uint)t.Offset);
                if (k < 0) k = ~k - 1;
                cls = k >= 0 ? secStarts[k].Cls : 0;
            }
            else
            {
                int k = Array.BinarySearch(primStarts, (uint)t.Offset);
                if (k < 0) k = ~k - 1;
                cls = k >= 0 ? _c.Records[_recordAt[primStarts[k]]].ClassRaw : 0;
            }
            byClass[cls] = byClass.GetValueOrDefault(cls) + 1;
        }
        return new PrefabCoverage(_typed.Length, typed, generic, byClass, untyped);
    }
}
