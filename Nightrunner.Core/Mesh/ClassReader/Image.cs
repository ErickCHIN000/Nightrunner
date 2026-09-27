using System.Buffers.Binary;

namespace Nightrunner.Core.Mesh.ClassReader;

/// <summary>A resolved pointer slot. <see cref="Target"/> is null for a null pointer.</summary>
public readonly record struct Pointer(int Slot, byte Kind, ulong Raw, int? Target, ushort Tag);

/// <summary>
/// ClassReader primary image (mesh part 0x10 <c>_MESH_</c>) with its fixups: bounds-checked typed reads and pointer
/// resolution through the slot table. Read-only. Port of <c>classreader/image.py</c> (the <c>ImagePatch</c> writer
/// is not ported: mesh writing is out of scope).
/// </summary>
/// <remarks>
/// Pointer rule (A): stored u64 S; S == 0 ⇒ null; else target = S − 1, or (S &amp; 0xFFFF_FFFF_FFFF) − 1 with the high
/// 16 bits a tag for a tagged slot. A zero word without a slot is a null pointer (shipped palette descriptors store
/// null that way); a non-zero word without a slot is an error.
/// </remarks>
public sealed class Image
{
    public const int MaxString = 65536;
    private const ulong OffsetMask = 0xFFFF_FFFF_FFFF;

    /// <summary>The whole 0x10 part, including padding past <see cref="Size"/>.</summary>
    public byte[] Data { get; }
    public Fixups Fixups { get; }

    /// <summary>Primary image size from the fixups.</summary>
    public int Size { get; }

    private readonly Dictionary<uint, byte> _slots;

    public Image(byte[] data, Fixups fixups)
    {
        Data = data;
        Fixups = fixups;
        if (fixups.PrimarySize > data.Length)
            throw new MeshFormatException(
                $"fixups primary_size {fixups.PrimarySize} exceeds the image part ({data.Length} bytes)");
        Size = (int)fixups.PrimarySize;
        _slots = fixups.SlotMap();
    }

    public static Image FromParts(byte[] image, ReadOnlySpan<byte> fixups) => new(image, Fixups.Parse(fixups));

    public void Check(long off, long size)
    {
        if (off < 0 || off + size > Size)
            throw new MeshFormatException($"read of {size} bytes at 0x{off:X} outside the primary image (0x{Size:X})");
    }

    public ReadOnlySpan<byte> Span(int off, int size)
    {
        Check(off, size);
        return Data.AsSpan(off, size);
    }

    public byte[] Raw(int off, int size) => Span(off, size).ToArray();

    public byte U8(int off) { Check(off, 1); return Data[off]; }
    public ushort U16(int off) => BinaryPrimitives.ReadUInt16LittleEndian(Span(off, 2));
    public short I16(int off) => BinaryPrimitives.ReadInt16LittleEndian(Span(off, 2));
    public uint U32(int off) => BinaryPrimitives.ReadUInt32LittleEndian(Span(off, 4));
    public ulong U64(int off) => BinaryPrimitives.ReadUInt64LittleEndian(Span(off, 8));

    public float[] F32s(int off, int count)
    {
        var s = Span(off, count * 4);
        var r = new float[count];
        for (int i = 0; i < count; i++) r[i] = BinaryPrimitives.ReadSingleLittleEndian(s[(i * 4)..]);
        return r;
    }

    public ushort[] U16s(int off, int count)
    {
        var s = Span(off, count * 2);
        var r = new ushort[count];
        for (int i = 0; i < count; i++) r[i] = BinaryPrimitives.ReadUInt16LittleEndian(s[(i * 2)..]);
        return r;
    }

    public uint[] U32s(int off, int count)
    {
        var s = Span(off, count * 4);
        var r = new uint[count];
        for (int i = 0; i < count; i++) r[i] = BinaryPrimitives.ReadUInt32LittleEndian(s[(i * 4)..]);
        return r;
    }

    /// <summary>NUL-terminated bytes at an offset (terminator must lie inside the primary image).</summary>
    public byte[] CString(int off, int limit = MaxString)
    {
        Check(off, 0);
        int end = Math.Min(Size, off + limit);
        int n = Data.AsSpan(off, end - off).IndexOf((byte)0);
        if (n < 0) throw new MeshFormatException($"unterminated string at 0x{off:X}");
        return Data.AsSpan(off, n).ToArray();
    }

    public bool IsSlot(int off) => _slots.ContainsKey((uint)off);

    public byte? SlotKind(int off) => _slots.TryGetValue((uint)off, out var k) ? k : null;

    public Pointer Pointer(int off, bool allowUnslottedNull = true)
    {
        bool slotted = _slots.TryGetValue((uint)off, out byte kind);
        ulong raw = U64(off);
        if (!slotted)
        {
            if (raw == 0 && allowUnslottedNull) return new(off, 0, 0, null, 0);
            throw new MeshFormatException($"0x{off:X} is not a relocation slot (word 0x{raw:X})");
        }
        if ((kind & ~Slot.Tagged) != 0)
            throw new MeshUnsupportedException(
                $"slot 0x{off:X}: kind 0x{kind:X} (indirect/secondary) is not resolvable offline");
        if (raw == 0) return new(off, kind, raw, null, 0);
        long target;
        ushort tag = 0;
        if ((kind & Slot.Tagged) != 0)
        {
            target = (long)(raw & OffsetMask) - 1;
            tag = (ushort)(raw >> 48);
        }
        else target = raw > long.MaxValue ? -1 : (long)raw - 1;
        if (target < 0 || target >= Size)
            throw new MeshFormatException($"slot 0x{off:X}: target 0x{target:X} outside the primary image (0x{Size:X})");
        return new(off, kind, raw, (int)target, tag);
    }

    public int? Target(int off) => Pointer(off).Target;

    public byte[]? StringAt(int ptrOff) => Target(ptrOff) is { } t ? CString(t) : null;

    public List<Record> Records => Fixups.Records;
    public (int Start, int End) RecordSpan(int index) => Fixups.RecordSpan(index);
    public int? RecordAt(int offset) => Fixups.RecordAt((uint)offset);

    /// <summary>Bytes of the part past the primary image (16-byte padding in shipped meshes).</summary>
    public ReadOnlySpan<byte> Padding => Data.AsSpan(Size);

    /// <summary>
    /// The <c>.msh</c> name MeshMgr registers the mesh under (A: GetFileName = **(this+0x68)): root+0x00 of the
    /// class-3 record 0. Differs from the pack's logical name only for a renamed copy.
    /// </summary>
    public static byte[] EmbeddedName(byte[] image, ReadOnlySpan<byte> fixups)
    {
        var fx = Fixups.Parse(fixups);
        if (fx.Records.Count == 0) throw new MeshFormatException("no records: missing root object");
        var root = fx.Records[0];
        if (root.ClassId != 3 || root.Secondary || root.Count != 1)
            throw new MeshFormatException(
                $"record 0 is class {root.ClassId} count {root.Count} (expected class 3 root, count 1)");
        return new Image(image, fx).StringAt((int)root.Offset)
               ?? throw new MeshFormatException("root name pointer is null");
    }
}
