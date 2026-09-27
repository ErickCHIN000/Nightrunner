using System.Buffers.Binary;

namespace Nightrunner.Core.Mesh.ClassReader;

/// <summary>
/// ClassReader metadata blob (mesh part 0x11 <c>_MESH_FIXUPS_</c>). Port of <c>classreader/fixups.py</c>.
/// </summary>
/// <remarks>
/// <code>
/// 0x00 u32 primary_size      byte size of the primary image (the 0x10 part may be longer: padding)
/// 0x04 u32 record_count
/// 0x08 u32 object_count_raw  bit 31 = secondary stream present; low bits always 1 in the corpus (C)
/// 0x0C {u32 offset, u32 class_raw, u32 flags_raw} × records
///      u32 slot_count, u32 slot_offset × S, u8 slot_kind × S
///      [bit 31: align 4, u32 secondary_size, align 16, secondary bytes]
///      trailing bytes (zero padding in every shipped mesh), kept verbatim
/// </code>
/// Every field is kept raw, so <see cref="ToBytes"/> reproduces the blob byte for byte.
/// </remarks>
public sealed class Fixups
{
    public const uint SecondaryPresent = 0x80000000;
    public const int MaxRecords = 1_000_000;
    public const int MaxSlots = 10_000_000;

    public uint PrimarySize { get; set; }
    public uint ObjectCountRaw { get; }
    public List<Record> Records { get; }
    public List<Slot> Slots { get; }

    /// <summary>Secondary image when <see cref="SecondaryPresent"/> is set; never present in a mesh.</summary>
    public byte[]? Secondary { get; }

    /// <summary>Bytes after the parsed structure (padding), kept verbatim.</summary>
    public byte[] Trailing { get; set; }

    private int[]? _spanEnds;
    private Dictionary<uint, int>? _recordAt;

    public Fixups(uint primarySize, uint objectCountRaw, List<Record> records, List<Slot> slots,
                  byte[]? secondary = null, byte[]? trailing = null)
    {
        PrimarySize = primarySize;
        ObjectCountRaw = objectCountRaw;
        Records = records;
        Slots = slots;
        Secondary = secondary;
        Trailing = trailing ?? [];
    }

    public bool SecondaryPresentFlag => (ObjectCountRaw & SecondaryPresent) != 0;
    public uint ObjectCount => ObjectCountRaw & 0x7FFFFFFF;

    public static Fixups Parse(ReadOnlySpan<byte> buf)
    {
        int n = buf.Length;
        if (n < 16) throw new MeshFormatException($"fixups blob too short ({n} bytes)");
        uint primary = U32(buf, 0), recordCount = U32(buf, 4), objRaw = U32(buf, 8);
        if (recordCount > MaxRecords)
            throw new MeshFormatException($"fixups: record_count {recordCount} exceeds the offline bound {MaxRecords}");
        long pos = 12, end = pos + recordCount * 12L;
        if (end + 4 > n) throw new MeshFormatException($"fixups: {recordCount} records do not fit in {n} bytes");
        var records = new List<Record>((int)recordCount);
        for (int i = 0; i < recordCount; i++)
        {
            int o = (int)pos + i * 12;
            records.Add(new Record(U32(buf, o), U32(buf, o + 4), U32(buf, o + 8)));
        }
        pos = end;
        uint slotCount = U32(buf, (int)pos);
        pos += 4;
        if (slotCount > MaxSlots)
            throw new MeshFormatException($"fixups: slot_count {slotCount} exceeds the offline bound {MaxSlots}");
        if (pos + slotCount * 5L > n) throw new MeshFormatException($"fixups: {slotCount} slots do not fit in {n} bytes");
        var slots = new List<Slot>((int)slotCount);
        int kinds = (int)(pos + slotCount * 4L);
        for (int i = 0; i < slotCount; i++)
            slots.Add(new Slot(U32(buf, (int)pos + i * 4), buf[kinds + i]));
        pos = kinds + slotCount;
        byte[]? secondary = null;
        if ((objRaw & SecondaryPresent) != 0)
        {
            pos = Align(pos, 4);
            if (pos + 4 > n) throw new MeshFormatException("fixups: secondary size word missing");
            uint secSize = U32(buf, (int)pos);
            pos = Align(pos + 4, 16);
            if (pos + secSize > n) throw new MeshFormatException($"fixups: secondary image {secSize} bytes does not fit");
            secondary = buf.Slice((int)pos, (int)secSize).ToArray();
            pos += secSize;
        }
        var fx = new Fixups(primary, objRaw, records, slots, secondary, buf[(int)pos..].ToArray());
        for (int i = 0; i < records.Count; i++)
            if (!records[i].Secondary && records[i].Offset > primary)
                throw new MeshFormatException(
                    $"fixups: record {i} offset 0x{records[i].Offset:X} beyond primary image (0x{primary:X})");
        return fx;
    }

    public byte[] ToBytes()
    {
        var sec = Secondary ?? [];
        int size = 12 + Records.Count * 12 + 4 + Slots.Count * 5;
        if (SecondaryPresentFlag) size = (int)Align(Align(size, 4) + 4, 16) + sec.Length;
        var out_ = new byte[size + Trailing.Length];
        var s = out_.AsSpan();
        W32(s, 0, PrimarySize);
        W32(s, 4, (uint)Records.Count);
        W32(s, 8, ObjectCountRaw);
        int pos = 12;
        foreach (var r in Records)
        {
            W32(s, pos, r.Offset); W32(s, pos + 4, r.ClassRaw); W32(s, pos + 8, r.FlagsRaw);
            pos += 12;
        }
        W32(s, pos, (uint)Slots.Count);
        pos += 4;
        for (int i = 0; i < Slots.Count; i++) W32(s, pos + i * 4, Slots[i].Offset);
        pos += Slots.Count * 4;
        for (int i = 0; i < Slots.Count; i++) s[pos + i] = Slots[i].Kind;
        pos += Slots.Count;
        if (SecondaryPresentFlag)
        {
            pos = (int)Align(pos, 4);
            W32(s, pos, (uint)sec.Length);
            pos = (int)Align(pos + 4, 16);
            sec.CopyTo(s[pos..]);
            pos += sec.Length;
        }
        Trailing.CopyTo(s[pos..]);
        return out_;
    }

    public Dictionary<uint, byte> SlotMap()
    {
        var d = new Dictionary<uint, byte>(Slots.Count);
        foreach (var sl in Slots) d[sl.Offset] = sl.Kind;
        return d;
    }

    /// <summary>(record index, record) for every primary record of a class.</summary>
    public IEnumerable<(int Index, Record Record)> RecordsOfClass(uint classId)
    {
        for (int i = 0; i < Records.Count; i++)
            if (Records[i].ClassId == classId && !Records[i].Secondary) yield return (i, Records[i]);
    }

    /// <summary>
    /// [start, end) of a record in the primary image: end is the next primary record's offset (records ascend on
    /// every shipped mesh) or <see cref="PrimarySize"/> for the last.
    /// </summary>
    public (int Start, int End) RecordSpan(int index)
    {
        var r = Records[index];
        if (r.Secondary) throw new MeshFormatException($"record {index} lives in the secondary image");
        if (_spanEnds is null || _spanEnds.Length != Records.Count)
        {
            var ends = new int[Records.Count];
            int next = (int)PrimarySize;
            for (int i = Records.Count - 1; i >= 0; i--)
            {
                ends[i] = next;
                if (!Records[i].Secondary) next = (int)Records[i].Offset;
            }
            _spanEnds = ends;
        }
        return ((int)r.Offset, _spanEnds[index]);
    }

    /// <summary>Index of the primary record starting exactly at an offset (the first one), else null.</summary>
    public int? RecordAt(uint offset)
    {
        if (_recordAt is null)
        {
            var d = new Dictionary<uint, int>(Records.Count);
            for (int i = 0; i < Records.Count; i++)
                if (!Records[i].Secondary) d.TryAdd(Records[i].Offset, i);
            _recordAt = d;
        }
        return _recordAt.TryGetValue(offset, out int k) ? k : null;
    }

    public IEnumerable<Slot> UnsupportedSlots() => Slots.Where(s => !s.Supported);

    public SortedDictionary<uint, int> ClassCensus()
    {
        var h = new SortedDictionary<uint, int>();
        foreach (var r in Records) h[r.ClassId] = h.GetValueOrDefault(r.ClassId) + 1;
        return h;
    }

    private static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    private static void W32(Span<byte> b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b[o..], v);
    internal static long Align(long v, int a) => (v + a - 1) / a * a;
}

/// <summary>An object record: offset, class word (low 24 bits = class id, high 8 = 0xB0 in meshes), flags word.</summary>
public readonly record struct Record(uint Offset, uint ClassRaw, uint FlagsRaw)
{
    public const uint InSecondary = 0x40000000;
    public const uint ReversePrepass = 0x80000000;

    public uint ClassId => ClassRaw & 0xFFFFFF;
    public uint ClassHi => ClassRaw >> 24;
    public int Count => (int)(FlagsRaw & 0x3FFFFFFF);
    public bool Secondary => (FlagsRaw & InSecondary) != 0;
    public bool Reverse => (FlagsRaw & ReversePrepass) != 0;
}

/// <summary>A relocation slot: image offset of an 8-byte pointer field plus its kind bits.</summary>
public readonly record struct Slot(uint Offset, byte Kind)
{
    /// <summary>Second pass: dereference again (flagged, not resolved offline).</summary>
    public const byte Indirect = 0x1;
    /// <summary>Low 48 bits = offset + 1, high 16 = tag kept verbatim.</summary>
    public const byte Tagged = 0x2;
    public const byte InSecondary = 0x4;
    public const byte ToSecondary = 0x8;

    public bool Supported => (Kind & ~Tagged) == 0;
    public bool IsTagged => (Kind & Tagged) != 0;
}
