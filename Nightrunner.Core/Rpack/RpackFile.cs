using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace Nightrunner.Core.Rpack;

/// <summary>Thrown when a file is not a readable RP6L v4 pack.</summary>
public sealed class RpackFormatException(string message) : Exception(message);

/// <summary>
/// A memory-mapped RP6L v4 pack. Nothing is decoded at open time except the tables, which are copied into
/// managed arrays (a few MB); payload bytes are faulted in on demand, so a 29 GB pack opens in milliseconds.
/// </summary>
public sealed class RpackFile : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private bool _disposed;

    public string Path { get; }
    public long Length { get; }

    public RpackHeader Header;
    public Storage[] Storages { get; }
    public Physical[] Physicals { get; }
    public Logical[] Logicals { get; }

    /// <summary>Raw name blob, exactly as on disk (names are NUL-terminated, not necessarily valid UTF-8).</summary>
    public byte[] NameBlob { get; }

    /// <summary>ASCII-lowered copy of the name blob; the search scans this and never allocates strings.</summary>
    public byte[] NameBlobLower { get; }

    /// <summary>Per logical resource: start of its name inside the blob.</summary>
    public int[] NameStart { get; }

    /// <summary>Per logical resource: byte length of its name.</summary>
    public int[] NameLength { get; }

    /// <summary>Per logical resource: sum of its part sizes.</summary>
    public long[] ResourceSize { get; }

    public long StorageOffset { get; }
    public long PhysicalOffset { get; }
    public long LogicalOffset { get; }
    public long NameOffsetOffset { get; }
    public long NameBlobOffset { get; }
    public long TableEnd { get; }

    public int Count => Logicals.Length;

    public static RpackFile Open(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException(path);
        if (info.Length < RpackFormat.HeaderSize)
            throw new RpackFormatException($"{path}: file shorter than the {RpackFormat.HeaderSize}-byte header");
        return new RpackFile(path, info.Length);
    }

    private unsafe RpackFile(string path, long length)
    {
        Path = path;
        Length = length;
        _mmf = MemoryMappedFile.CreateFromFile(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1,
                           FileOptions.RandomAccess),
            mapName: null, capacity: 0, MemoryMappedFileAccess.Read, HandleInheritability.None,
            leaveOpen: false);
        try
        {
            Header = Read<RpackHeader>(0);
            if (Header.Magic != RpackFormat.Magic)
                throw new RpackFormatException(
                    $"{path}: bad magic 0x{Header.Magic:X8} (expected RP6L 0x{RpackFormat.Magic:X8})");
            if (Header.Version != RpackFormat.Version)
                throw new RpackFormatException(
                    $"{path}: unsupported version {Header.Version} (expected {RpackFormat.Version})");
            if (Header.StorageCount > RpackFormat.MaxStorages)
                throw new RpackFormatException(
                    $"{path}: storage_count {Header.StorageCount} exceeds the 8-bit physical index");

            StorageOffset = RpackFormat.HeaderSize;
            PhysicalOffset = StorageOffset + (long)Header.StorageCount * RpackFormat.StorageSize;
            LogicalOffset = PhysicalOffset + (long)Header.PhysicalCount * RpackFormat.PhysicalSize;
            NameOffsetOffset = LogicalOffset + (long)Header.LogicalCount * RpackFormat.LogicalSize;
            NameBlobOffset = NameOffsetOffset + (long)Header.NameCount * 4;
            TableEnd = NameBlobOffset + Header.NameBytes;
            if (TableEnd > length)
                throw new RpackFormatException(
                    $"{path}: tables (0x{TableEnd:X}) extend past end of file (0x{length:X})");
            if (TableEnd > int.MaxValue)
                throw new RpackFormatException($"{path}: table region 0x{TableEnd:X} is larger than 2 GB");

            using (var view = _mmf.CreateViewAccessor(0, TableEnd, MemoryMappedFileAccess.Read))
            {
                byte* p = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                try
                {
                    var all = new ReadOnlySpan<byte>(p + view.PointerOffset, (int)TableEnd);
                    Storages = MemoryMarshal.Cast<byte, Storage>(
                        all.Slice((int)StorageOffset, (int)Header.StorageCount * RpackFormat.StorageSize)).ToArray();
                    Physicals = MemoryMarshal.Cast<byte, Physical>(
                        all.Slice((int)PhysicalOffset, (int)Header.PhysicalCount * RpackFormat.PhysicalSize)).ToArray();
                    Logicals = MemoryMarshal.Cast<byte, Logical>(
                        all.Slice((int)LogicalOffset, (int)Header.LogicalCount * RpackFormat.LogicalSize)).ToArray();
                    NameBlob = all.Slice((int)NameBlobOffset, (int)Header.NameBytes).ToArray();
                    var nameOffsets = MemoryMarshal.Cast<byte, uint>(
                        all.Slice((int)NameOffsetOffset, (int)Header.NameCount * 4));
                    (NameStart, NameLength) = ResolveNames(nameOffsets);
                }
                finally
                {
                    view.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }

            ValidateTables();
            NameBlobLower = LowerAscii(NameBlob);
            ResourceSize = MeasureResources();
        }
        catch
        {
            _mmf.Dispose();
            throw;
        }
    }

    private T Read<T>(long offset) where T : unmanaged
    {
        using var view = _mmf.CreateViewAccessor(offset, Marshal.SizeOf<T>(), MemoryMappedFileAccess.Read);
        view.Read<T>(0, out var value);
        return value;
    }

    private (int[] Start, int[] Length) ResolveNames(ReadOnlySpan<uint> nameOffsets)
    {
        int n = Logicals.Length;
        var start = new int[n];
        var len = new int[n];
        var blob = NameBlob;
        for (int i = 0; i < n; i++)
        {
            uint ni = Logicals[i].NameIndex;
            if (ni >= (uint)nameOffsets.Length)
                throw new RpackFormatException($"{Path}: logical {i} name index {ni} of {nameOffsets.Length}");
            int s = (int)nameOffsets[(int)ni];
            if ((uint)s >= (uint)blob.Length)
                throw new RpackFormatException($"{Path}: name offset {s} outside the name blob ({blob.Length})");
            int nul = Array.IndexOf(blob, (byte)0, s);
            if (nul < 0)
                throw new RpackFormatException($"{Path}: logical {i} name at {s} is not NUL-terminated inside the name blob");
            start[i] = s;
            len[i] = nul - s;
        }
        return (start, len);
    }

    private void ValidateTables()
    {
        for (int i = 0; i < Physicals.Length; i++)
            if (Physicals[i].StorageIndex >= Storages.Length)
                throw new RpackFormatException(
                    $"{Path}: physical {i} references storage {Physicals[i].StorageIndex} of {Storages.Length}");
        for (int i = 0; i < Logicals.Length; i++)
        {
            long end = (long)Logicals[i].FirstPart + Logicals[i].PartCount;
            if (end > Physicals.Length)
                throw new RpackFormatException(
                    $"{Path}: logical {i} parts [{Logicals[i].FirstPart}, +{Logicals[i].PartCount}) " +
                    $"exceed {Physicals.Length}");
        }
    }

    private static byte[] LowerAscii(ReadOnlySpan<byte> src)
    {
        var dst = new byte[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            byte b = src[i];
            dst[i] = b >= 0x41 && b <= 0x5A ? (byte)(b + 0x20) : b;
        }
        return dst;
    }

    private long[] MeasureResources()
    {
        var sizes = new long[Logicals.Length];
        for (int i = 0; i < Logicals.Length; i++)
        {
            long total = 0;
            int first = (int)Logicals[i].FirstPart;
            for (int k = 0; k < Logicals[i].PartCount; k++) total += Physicals[first + k].Size;
            sizes[i] = total;
        }
        return sizes;
    }

    // ---- names ---------------------------------------------------------------------------------------------

    public ReadOnlySpan<byte> NameBytes(int logicalIndex) =>
        NameBlob.AsSpan(NameStart[logicalIndex], NameLength[logicalIndex]);

    public ReadOnlySpan<byte> NameBytesLower(int logicalIndex) =>
        NameBlobLower.AsSpan(NameStart[logicalIndex], NameLength[logicalIndex]);

    public string Name(int logicalIndex) =>
        Encoding.UTF8.GetString(NameBlob, NameStart[logicalIndex], NameLength[logicalIndex]);

    private Dictionary<string, int[]>? _byName;
    private readonly Lock _byNameLock = new();

    /// <summary>
    /// Logical indices (ascending) whose engine-folded name equals <paramref name="folded"/>. The index is built on
    /// first use (one pass over the name table); names are keyed byte for byte (Latin-1), so folding stays the engine's.
    /// </summary>
    public int[] IndicesOf(ReadOnlySpan<byte> folded)
    {
        var index = _byName;
        if (index is null)
            lock (_byNameLock)
            {
                if (_byName is null)
                {
                    var build = new Dictionary<string, List<int>>(Count, StringComparer.Ordinal);
                    for (int i = 0; i < Count; i++)
                    {
                        string key = Encoding.Latin1.GetString(NameBytesLower(i));
                        if (!build.TryGetValue(key, out var list)) build[key] = list = [];
                        list.Add(i);
                    }
                    _byName = build.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
                }
                index = _byName;
            }
        return index.TryGetValue(Encoding.Latin1.GetString(folded), out var hits) ? hits : [];
    }

    /// <summary>Engine name folding (FindLogicalResourceUsingName, ResourceCore +0x19250): ASCII A-Z only.</summary>
    public static byte[] EngineFold(ReadOnlySpan<byte> name) => LowerAscii(name);

    // ---- parts ---------------------------------------------------------------------------------------------

    public Storage PartStorage(int physIndex) => Storages[Physicals[physIndex].StorageIndex];

    public byte PartType(int physIndex) => Storages[Physicals[physIndex].StorageIndex].Type;

    public long PartOffset(int physIndex)
    {
        ref var p = ref Physicals[physIndex];
        return ((long)Storages[p.StorageIndex].BaseUnits + p.OffsetUnits) << RpackFormat.UnitShift;
    }

    /// <summary>True when the bytes live in this file uncompressed (storage method 0/1, not a child-pack part).</summary>
    public bool PartIsDirect(int physIndex)
    {
        ref var p = ref Physicals[physIndex];
        return Storages[p.StorageIndex].Method is 0 or 1 && !p.Child;
    }

    /// <summary>Why a part has no readable payload here, or null when it is directly readable.</summary>
    public string? PartUnreadableReason(int physIndex)
    {
        ref var p = ref Physicals[physIndex];
        if (p.Child) return "payload lives in a .rpacz child pack (bit 13)";
        int method = Storages[p.StorageIndex].Method;
        if (method is not (0 or 1)) return $"storage method {method} (compressed) is not supported";
        long off = PartOffset(physIndex);
        if (off + p.Size > Length) return $"span 0x{off:X}+0x{p.Size:X} exceeds file size 0x{Length:X}";
        return null;
    }

    /// <summary>Copy up to <paramref name="count"/> bytes of a part, starting <paramref name="skip"/> bytes in.</summary>
    public byte[] ReadPart(int physIndex, long skip = 0, long count = long.MaxValue)
    {
        if (PartUnreadableReason(physIndex) is { } why)
            throw new RpackFormatException($"part {physIndex}: {why}");
        long size = Physicals[physIndex].Size;
        if (skip >= size) return [];
        long take = Math.Min(count, size - skip);
        if (take <= 0) return [];
        if (take > int.MaxValue)
            throw new RpackFormatException($"part {physIndex}: {take} bytes is more than one array holds");
        var buf = new byte[take];
        CopyRange(PartOffset(physIndex) + skip, buf);
        return buf;
    }

    /// <summary>Copy an arbitrary byte range of the file (the tables are read this way too).</summary>
    public unsafe void CopyRange(long offset, Span<byte> destination)
    {
        if (destination.IsEmpty) return;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0 || offset + destination.Length > Length)
            throw new RpackFormatException(
                $"range 0x{offset:X}+0x{destination.Length:X} exceeds file size 0x{Length:X}");
        using var view = _mmf.CreateViewAccessor(offset, destination.Length, MemoryMappedFileAccess.Read);
        byte* p = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        try
        {
            new ReadOnlySpan<byte>(p + view.PointerOffset, destination.Length).CopyTo(destination);
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    /// <summary>Stream a byte range (export path — never materialises the range in memory).</summary>
    public Stream OpenRange(long offset, long size) =>
        _mmf.CreateViewStream(offset, size, MemoryMappedFileAccess.Read);

    public (long Offset, long Size) TableRange(RpackTable table) => table switch
    {
        RpackTable.Header => (0L, (long)RpackFormat.HeaderSize),
        RpackTable.Storages => (StorageOffset, (long)Header.StorageCount * RpackFormat.StorageSize),
        RpackTable.Physicals => (PhysicalOffset, (long)Header.PhysicalCount * RpackFormat.PhysicalSize),
        RpackTable.Logicals => (LogicalOffset, (long)Header.LogicalCount * RpackFormat.LogicalSize),
        RpackTable.Names => (NameOffsetOffset, TableEnd - NameOffsetOffset),
        _ => throw new ArgumentOutOfRangeException(nameof(table)),
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mmf.Dispose();
    }
}

public enum RpackTable
{
    Header,
    Storages,
    Physicals,
    Logicals,
    Names,
}
