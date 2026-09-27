using System.Buffers.Binary;
using System.Text;

namespace Nightrunner.Core.Prefab;

/// <summary>
/// Pointer and text reads over a parsed <see cref="PrefabContainer"/>: slot lookup by (image, offset),
/// <c>ttl::string_base</c> (§6), and the text of the three resolve elements (pstring 0xB1000000, class 0xC0000042,
/// field 0xC0000043) that kind-9 slots point at. Read-only; built once per container state.
/// </summary>
public sealed class PrefabText
{
    public const int MaxString = 1 << 20;

    private readonly PrefabContainer _c;
    private readonly Dictionary<uint, int> _primSlots;
    private readonly Dictionary<uint, int> _secSlots;
    private readonly Dictionary<uint, int> _secRecords = [];

    public PrefabText(PrefabContainer c)
    {
        _c = c;
        _primSlots = new Dictionary<uint, int>(c.Slots.Count);
        _secSlots = [];
        for (int i = 0; i < c.Slots.Count; i++)
        {
            var s = c.Slots[i];
            ((s.Kind & PrefabContainer.KindSlotSecondary) != 0 ? _secSlots : _primSlots).TryAdd(s.Offset, i);
        }
        for (int i = 0; i < c.Records.Count; i++)
            if ((c.Records[i].FlagsRaw & PrefabContainer.RecInSecondary) != 0) _secRecords.TryAdd(c.Records[i].Offset, i);
    }

    public PrefabContainer Container => _c;

    /// <summary>Index of the slot whose word lies at <paramref name="offset"/> of the given image, else −1.</summary>
    public int SlotAt(bool secondary, int offset) =>
        (secondary ? _secSlots : _primSlots).TryGetValue((uint)offset, out int i) ? i : -1;

    /// <summary>The slot at a field, or null when the field has no slot (a null pointer, or not a pointer).</summary>
    public (int Index, SlotTarget Target)? Pointer(bool secondary, int offset)
    {
        int i = SlotAt(secondary, offset);
        return i < 0 ? null : (i, _c.Target(_c.Slots[i]));
    }

    public ulong U64(bool secondary, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(_c.ImageOf(secondary).AsSpan(offset, 8));

    /// <summary>Secondary record (index) starting exactly at an offset, else −1.</summary>
    public int SecondaryRecordAt(int offset) => _secRecords.TryGetValue((uint)offset, out int i) ? i : -1;

    /// <summary>
    /// <c>ttl::string_base</c> at a field (§6): a tagged slot means pointer form (top nibble of the tag: 1 = NUL-
    /// terminated, ≥2 = u32 length at ptr − 8); no slot means inline form (top nibble 0, chars in bytes 0..6, length
    /// 7 − (byte7 &amp; 0xF)). Null when the bytes do not read as either.
    /// </summary>
    public string? StringBase(bool secondary, int offset)
    {
        var img = _c.ImageOf(secondary);
        if (offset < 0 || offset > img.Length - 8) return null;
        if (Pointer(secondary, offset) is { } p)
        {
            var t = p.Target;
            if (t.Target < 0) return null;
            int mode = t.Tag >> 12;
            return mode == 1 ? CString(t.ToSecondary, (int)t.Target) : LengthPrefixed(t.ToSecondary, (int)t.Target);
        }
        // Dying Light 2 writes the empty string as a zero word (its only inline string_base; DLTB writes byte 7 = 0x07)
        if (BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan(offset)) == 0) return "";
        byte b7 = img[offset + 7];
        if (b7 >> 4 != 0) return null;
        int len = 7 - (b7 & 0xF);
        if (len < 0) return null;
        return Encoding.UTF8.GetString(img, offset, len);
    }

    /// <summary>Text with a <c>{u32 len, u32 cap}</c> header at ptr − 8 and a NUL after it.</summary>
    public string? LengthPrefixed(bool secondary, int ptr)
    {
        var img = _c.ImageOf(secondary);
        if (ptr < 8 || ptr > img.Length) return null;
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(ptr - 8));
        if (len > MaxString || ptr + (long)len >= img.Length || img[ptr + (int)len] != 0) return null;
        return Encoding.UTF8.GetString(img, ptr, (int)len);
    }

    public string? CString(bool secondary, int ptr)
    {
        var img = _c.ImageOf(secondary);
        if (ptr < 0 || ptr >= img.Length) return null;
        int end = Array.IndexOf(img, (byte)0, ptr, Math.Min(MaxString, img.Length - ptr));
        return end < 0 ? null : Encoding.UTF8.GetString(img, ptr, end - ptr);
    }

    /// <summary>
    /// Text of element <paramref name="k"/> of resolve record <paramref name="record"/>: the pstring (B1: string_base
    /// at +8), the class name (0x42: string_base at +8), <c>class::field</c> (0x43: string_bases at +8 and +0x10), or
    /// <c>a:b</c> (0x70, Dying Light 2 only: the same two string_bases).
    /// </summary>
    public string? ElementText(int record, int k = 0)
    {
        // elements are shared by every field with the same text: one string per element keeps decoded models small
        long key = ((long)record << 20) | (uint)k;
        if (_elementText.TryGetValue(key, out var cached)) return cached;
        return _elementText[key] = ElementTextUncached(record, k);
    }

    private readonly Dictionary<long, string?> _elementText = [];

    private string? ElementTextUncached(int record, int k)
    {
        var r = _c.Records[record];
        bool sec = (r.FlagsRaw & PrefabContainer.RecInSecondary) != 0;
        int? stride = PrefabClasses.Stride(r.ClassRaw);
        if (stride is null || !(PrefabClasses.IsResolveElement(r.ClassRaw) || r.ClassRaw == PrefabClasses.PairElement)) return null;
        int at = (int)r.Offset + k * stride.Value;
        if (at + stride.Value > _c.ImageOf(sec).Length) return null;
        if (r.ClassRaw is PrefabClasses.FieldElement or PrefabClasses.PairElement)
        {
            var cls = StringBase(sec, at + 8);
            var fld = StringBase(sec, at + 0x10);
            return cls is null || fld is null ? null : r.ClassRaw == PrefabClasses.PairElement ? $"{cls}:{fld}" : $"{cls}::{fld}";
        }
        return StringBase(sec, at + 8);
    }

    /// <summary>The resolve element (record, element index) at a secondary offset, or null.</summary>
    public (int Record, int Element)? ElementAt(int offset)
    {
        int i = SecondaryRecordAt(offset);
        if (i >= 0) return (i, 0);
        return null;
    }

    /// <summary>
    /// Text a kind-9 slot at a field resolves to (pstring, <c>CRTTI*</c> class name or <c>CRTTIField*</c>
    /// <c>class::field</c>); null for no slot or a slot of another kind.
    /// </summary>
    public string? Resolved(bool secondary, int offset) => ResolvedWith(secondary, offset).Text;

    public (string? Text, uint ClassId, int Slot) ResolvedWith(bool secondary, int offset)
    {
        if (Pointer(secondary, offset) is not { } p) return (null, 0, -1);
        var t = p.Target;
        if ((_c.Slots[p.Index].Kind & PrefabContainer.KindIndirect) == 0 || !t.ToSecondary) return (null, 0, p.Index);
        if (ElementAt((int)t.Target) is not { } e) return (null, 0, p.Index);
        return (ElementText(e.Record, e.Element), _c.Records[e.Record].ClassRaw, p.Index);
    }

    /// <summary>A pstring field: the text of its B1000000 element, or null (null pstring or not a pstring).</summary>
    public string? PstringAt(bool secondary, int offset)
    {
        var (text, cls, _) = ResolvedWith(secondary, offset);
        return cls == PrefabClasses.PstringElement ? text : null;
    }
}
