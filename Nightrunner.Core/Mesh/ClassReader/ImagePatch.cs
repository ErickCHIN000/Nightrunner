using System.Buffers.Binary;

namespace Nightrunner.Core.Mesh.ClassReader;

/// <summary>
/// Mutable copy of a primary image + its fixups, the editing surface of mesh writing. Port of
/// <c>classreader/image.py::ImagePatch</c>: writes go through <c>Write*</c>; new data is appended after the primary image
/// (shared storage is never overwritten); pointers are retargeted through their existing slots or new slots are
/// registered. <see cref="Finish"/> returns both parts with the primary size updated.
/// </summary>
public sealed class ImagePatch
{
    private const ulong OffsetMask = 0xFFFF_FFFF_FFFF;

    private readonly Image _src;
    private byte[] _buf;
    private int _size;
    private readonly byte[] _tail;
    private readonly Dictionary<uint, byte> _slots;

    /// <summary>The working fixups (a deep copy of the source's).</summary>
    public Fixups Fixups { get; }

    public ImagePatch(Image image)
    {
        _src = image;
        _size = image.Size;
        _buf = new byte[Math.Max(16, image.Size * 2)];
        image.Data.AsSpan(0, image.Size).CopyTo(_buf);
        _tail = image.Data.AsSpan(image.Size).ToArray();
        Fixups = Fixups.Parse(image.Fixups.ToBytes());
        _slots = Fixups.SlotMap();
        _slotsSorted = true;
        for (int i = 1; i < Fixups.Slots.Count && _slotsSorted; i++) _slotsSorted = Fixups.Slots[i - 1].Offset <= Fixups.Slots[i].Offset;
    }

    /// <summary>Current primary image size.</summary>
    public int Size => _size;

    public ReadOnlySpan<byte> Span(int off, int size)
    {
        if (off < 0 || size < 0 || (long)off + size > _size)
            throw new MeshFormatException($"read of {size} bytes at 0x{off:X} outside the image (0x{_size:X})");
        return _buf.AsSpan(off, size);
    }

    public ulong U64(int off) => BinaryPrimitives.ReadUInt64LittleEndian(Span(off, 8));

    public void Write(int off, ReadOnlySpan<byte> data)
    {
        if (off < 0 || (long)off + data.Length > _size)
            throw new MeshFormatException($"write of {data.Length} bytes at 0x{off:X} outside the image (0x{_size:X})");
        data.CopyTo(_buf.AsSpan(off));
    }

    public void WriteU16(int off, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); Write(off, b); }
    public void WriteU32(int off, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); Write(off, b); }
    public void WriteU64(int off, ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); Write(off, b); }

    public void WriteF32s(int off, ReadOnlySpan<float> values)
    {
        var b = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), values[i]);
        Write(off, b);
    }

    /// <summary>Appends <paramref name="data"/> at the end of the primary image, zero-padded to <paramref name="align"/>; returns its offset.</summary>
    public int Append(ReadOnlySpan<byte> data, int align = 1)
    {
        int off = (int)Fixups.Align(_size, align);
        Grow(off + data.Length);
        _buf.AsSpan(_size, off - _size).Clear();
        data.CopyTo(_buf.AsSpan(off));
        _size = off + data.Length;
        return off;
    }

    /// <summary>Appends a NUL-terminated string (refuses one containing NUL).</summary>
    public int AppendString(ReadOnlySpan<byte> s, int align = 1)
    {
        if (s.IndexOf((byte)0) >= 0) throw new MeshFormatException("string contains NUL");
        return Append([.. s, 0], align);
    }

    private void Grow(int need)
    {
        if (need <= _buf.Length) return;
        Array.Resize(ref _buf, Math.Max(need, _buf.Length * 2));
    }

    public byte? SlotKind(int off) => _slots.TryGetValue((uint)off, out var k) ? k : null;

    /// <summary>Registers a new relocation slot; the table is then (stably) sorted by offset, as shipped tables are.</summary>
    public void AddSlot(int off, byte kind = 0)
    {
        if (_slots.ContainsKey((uint)off)) throw new MeshFormatException($"slot 0x{off:X} already exists");
        if (off % 8 != 0) throw new MeshFormatException($"slot 0x{off:X} is not 8-byte aligned");
        _slots[(uint)off] = kind;
        var list = Fixups.Slots;
        if (!_slotsSorted)
        {
            var sorted = list.OrderBy(s => s.Offset).ToList();   // stable, as Python's list.sort
            list.Clear();
            list.AddRange(sorted);
            _slotsSorted = true;
        }
        int i = list.Count;
        while (i > 0 && list[i - 1].Offset > (uint)off) i--;
        list.Insert(i, new Slot((uint)off, kind));
    }

    private bool _slotsSorted;

    /// <summary>
    /// Points the slot at <paramref name="slotOff"/> to <paramref name="target"/> (null = null pointer). A tagged slot keeps
    /// its tag unless <paramref name="tag"/> is given; a field that is not yet a slot needs <paramref name="kind"/>.
    /// </summary>
    public void Retarget(int slotOff, int? target, byte? kind = null, ushort? tag = null)
    {
        if (!_slots.TryGetValue((uint)slotOff, out byte k))
        {
            if (kind is not { } nk) throw new MeshFormatException($"0x{slotOff:X} is not a relocation slot (pass a kind to add one)");
            AddSlot(slotOff, nk);
            k = nk;
        }
        if ((k & ~Slot.Tagged) != 0) throw new MeshUnsupportedException($"slot 0x{slotOff:X}: kind 0x{k:X} cannot be retargeted offline");
        ulong old = U64(slotOff);
        if (target is not { } t)
        {
            WriteU64(slotOff, 0);
            return;
        }
        if (t < 0 || t >= _size) throw new MeshFormatException($"target 0x{t:X} outside the image");
        ulong value = (ulong)t + 1;
        if ((k & Slot.Tagged) != 0) value |= tag is { } g ? (ulong)g << 48 : old & ~OffsetMask;
        WriteU64(slotOff, value);
    }

    /// <summary>Index of the first primary record starting exactly at <paramref name="offset"/> (current table), else null.</summary>
    public int? RecordAt(int offset)
    {
        var recs = Fixups.Records;
        for (int i = 0; i < recs.Count; i++)
            if (recs[i].Offset == (uint)offset && !recs[i].Secondary) return i;
        return null;
    }

    /// <summary>Adds an object record, kept in ascending offset order (after records at the same offset); returns its index.</summary>
    public int AddRecord(int offset, uint classId, int count, uint classHi = 0xB0, uint flagsHi = 0)
    {
        var rec = new Record((uint)offset, (classHi << 24) | (classId & 0xFFFFFF), ((uint)count & 0x3FFFFFFF) | flagsHi);
        return Insert(rec);
    }

    /// <summary>
    /// Moves record <paramref name="index"/> to a new (offset, count) — an array re-appended at the end of the image (the old
    /// bytes stay, unreferenced). Returns the record's new index (records stay in ascending offset order).
    /// </summary>
    public int RelocateRecord(int index, int offset, int count)
    {
        var r = Fixups.Records[index];
        Fixups.Records.RemoveAt(index);
        return Insert(new Record((uint)offset, r.ClassRaw, (r.FlagsRaw & ~0x3FFFFFFFu) | ((uint)count & 0x3FFFFFFF)));
    }

    private int Insert(Record rec)
    {
        var recs = Fixups.Records;
        int i = recs.Count;
        while (i > 0 && recs[i - 1].Offset > rec.Offset) i--;
        recs.Insert(i, rec);
        return i;
    }

    /// <summary>
    /// (image bytes, fixups bytes). An image whose primary size did not change keeps its exact original length; a grown one
    /// is the new primary image, then the original padding bytes, then zeros to a 16-byte multiple. Fixups whose record and
    /// slot tables kept their lengths keep their trailing bytes; otherwise the trailing bytes become zero padding to 16.
    /// </summary>
    public (byte[] Image, byte[] Fixups) Finish()
    {
        bool grown = _size != _src.Size;
        Fixups.PrimarySize = (uint)_size;
        int len = _size + _tail.Length;
        if (grown) len = (int)Fixups.Align(len, 16);
        var image = new byte[len];
        _buf.AsSpan(0, _size).CopyTo(image);
        _tail.CopyTo(image.AsSpan(_size));
        var srcFx = _src.Fixups;
        if (Fixups.Records.Count != srcFx.Records.Count || Fixups.Slots.Count != srcFx.Slots.Count)
        {
            Fixups.Trailing = [];
            int body = Fixups.ToBytes().Length;
            Fixups.Trailing = new byte[Fixups.Align(body, 16) - body];
        }
        return (image, Fixups.ToBytes());
    }
}
