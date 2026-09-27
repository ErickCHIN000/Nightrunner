using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace Nightrunner.Core.Sdb;

/// <summary>One table of the stream: where its records are, and how to reach record <c>i</c>.</summary>
/// <remarks>
/// A flat table needs no per-record bookkeeping — its offsets are arithmetic. Everything else gets two int
/// arrays. Offsets are absolute file offsets into block A, so they can be shown as-is.
/// </remarks>
public sealed class SdbTable
{
    public int Key { get; }
    public SdbShape Shape { get; }
    public int Width { get; }

    /// <summary>File offset of the table's compact count.</summary>
    public int Start { get; }

    /// <summary>File offset one past the table's last record.</summary>
    public int End { get; }

    public int Count { get; }

    private readonly int[]? _offsets;
    private readonly int[]? _lengths;
    private readonly int _flatBase;

    internal SdbTable(int key, SdbShape shape, int width, int start, int end, int count, int flatBase,
                      int[]? offsets, int[]? lengths)
    {
        Key = key;
        Shape = shape;
        Width = width;
        Start = start;
        End = end;
        Count = count;
        _flatBase = flatBase;
        _offsets = offsets;
        _lengths = lengths;
    }

    public int Bytes => End - Start;

    public int Offset(int index) => _offsets is null ? _flatBase + index * Width : _offsets[index];

    public int Length(int index) => _lengths is null ? Width : _lengths[index];

    public string Meaning => SdbFormat.Meaning(Key);

    public override string ToString() => $"0x{Key:X2} {Shape} w{Width} × {Count}";
}

/// <summary>
/// A read-only shader database (<c>runtime_dx11.sdb</c> / <c>runtime_dx12.sdb</c>).
/// </summary>
/// <remarks>
/// Block A — the tables, at most ~24 MB — is copied into a managed array at open time, so every record is a span
/// over ordinary memory with no pointer lifetime to get wrong. Block B is the DXBC program blobs, up to ~335 MB;
/// it stays memory-mapped and is only touched when someone asks for a blob.
///
/// There is no writer and there will not be one: table growth and new strings are undecoded, the 0xD2 value blob
/// is shared between materials, and it is unknown where a replacement database would have to live.
/// </remarks>
public sealed partial class SdbFile : IDisposable
{
    private readonly MemoryMappedFile? _mmf;
    private readonly byte[] _a;
    private bool _disposed;

    public string Path { get; }
    public long Length { get; }
    public SdbLayout Layout { get; }
    public uint InnerVersion { get; }

    /// <summary>The 28 bytes after the inner MDB version word. Not understood; preserved verbatim.</summary>
    public byte[] InnerOpaque { get; }

    public int BlockASize { get; }
    public long BlockBSize { get; }

    /// <summary>Tables by their renderer word key, in the layout's stream order.</summary>
    public IReadOnlyDictionary<int, SdbTable> Tables => _tables;
    private readonly Dictionary<int, SdbTable> _tables = [];

    /// <summary>Program blob spans (file offset, length) in block B, by group kind.</summary>
    public IReadOnlyDictionary<int, (long Offset, int Length)[]> Programs => _programs;
    private readonly Dictionary<int, (long Offset, int Length)[]> _programs = [];

    /// <summary>How often each compact-int width tag occurred while walking block A.</summary>
    public int[] CompactTags { get; } = new int[4];

    public string Name => System.IO.Path.GetFileName(Path);

    public int MaterialCount => _tables[SdbFormat.TableMaterialNames].Count;
    public int RouteCount => _tables[SdbFormat.TableRoutes].Count;
    public int PresetCount => _tables[SdbFormat.TablePresets].Count;

    public static SdbFile Open(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException(path);
        if (info.Length < SdbFormat.HeaderSize)
            throw new SdbFormatException($"{path}: shorter than the MDBR + MDB headers");
        return new SdbFile(path, info.Length);
    }

    private SdbFile(string path, long length)
    {
        Path = path;
        Length = length;

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                       FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
        {
            Span<byte> head = stackalloc byte[SdbFormat.HeaderSize];
            fs.ReadExactly(head);

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(head);
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
            uint sizeA = BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);
            uint sizeB = BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
            if (magic != SdbFormat.OuterMagic || version != SdbFormat.OuterVersion)
                throw new SdbFormatException(
                    $"{path}: expected MDBR version 0x{SdbFormat.OuterVersion:X8}, got 0x{magic:X8} 0x{version:X8}");
            if (16L + sizeA + sizeB != length)
                throw new SdbFormatException($"{path}: 16 + A({sizeA}) + B({sizeB}) != file size {length}");

            uint inner = BinaryPrimitives.ReadUInt32LittleEndian(head[16..]);
            InnerVersion = BinaryPrimitives.ReadUInt32LittleEndian(head[20..]);
            var layout = inner == SdbFormat.InnerMagic ? SdbFormat.LayoutFor(InnerVersion) : null;
            if (layout is null)
            {
                string known = string.Join(", ", SdbFormat.Layouts.Select(l => $"0x{l.InnerVersion:X8}"));
                throw new SdbFormatException(
                    $"{path}: expected inner MDB version in ({known}), got 0x{inner:X8} 0x{InnerVersion:X8}");
            }
            Layout = layout;
            InnerOpaque = head[24..52].ToArray();

            if (16L + sizeA > int.MaxValue)
                throw new SdbFormatException($"{path}: block A ({sizeA} bytes) is larger than 2 GB");
            BlockASize = (int)sizeA;
            BlockBSize = sizeB;

            _a = new byte[16 + BlockASize];
            fs.Position = 0;
            fs.ReadExactly(_a);
        }

        try
        {
            var a = new SdbCursor(_a, SdbFormat.HeaderSize, _a.Length, CompactTags);
            long bPos = 16L + BlockASize;

            foreach (var def in Layout.OrderA) ReadTable(ref a, def);

            foreach (int kind in SdbFormat.ProgramKinds)
            {
                int n = a.Count(4);
                var spans = new (long, int)[n];
                for (int i = 0; i < n; i++)
                {
                    int len = BinaryPrimitives.ReadInt32LittleEndian(_a.AsSpan(a.Skip(4)));
                    if (len < 0 || bPos + len > length)
                        throw new SdbFormatException(
                            $"{path}: program blob {kind}[{i}] of {len} bytes runs past the end of block B");
                    spans[i] = (bPos, len);
                    bPos += len;
                }
                _programs[kind] = spans;
            }

            foreach (var def in Layout.OrderB) ReadTable(ref a, def);

            if (a.Position != a.End || bPos != length)
                throw new SdbFormatException(
                    $"{path}: unconsumed bytes: A={a.End - a.Position} B={length - bPos}");

            CheckRoutes();

            if (BlockBSize > 0)
                _mmf = MemoryMappedFile.CreateFromFile(
                    new FileStream(path, FileMode.Open, FileAccess.Read,
                                   FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess),
                    mapName: null, capacity: 0, MemoryMappedFileAccess.Read, HandleInheritability.None,
                    leaveOpen: false);
        }
        catch
        {
            _mmf?.Dispose();
            throw;
        }
    }

    // ---- table walk ------------------------------------------------------------------------------------------

    private void ReadTable(ref SdbCursor a, SdbTableDef def)
    {
        int start = a.Position;
        int minimum = def.Shape switch
        {
            SdbShape.Flat => def.Width,
            SdbShape.Vectors => 1,
            SdbShape.PairU16 => 5,
            SdbShape.U16Records8 => 3,
            SdbShape.Seven => 7,
            SdbShape.Trailing => 25,
            _ => 1,
        };
        int count = a.Count(minimum);

        if (def.Shape == SdbShape.Flat)
        {
            int flatBase = a.Skip(count * def.Width);
            _tables[def.Key] = new SdbTable(def.Key, def.Shape, def.Width, start, a.Position, count, flatBase,
                                            null, null);
            return;
        }

        var offsets = new int[count];
        var lengths = new int[count];
        for (int i = 0; i < count; i++)
        {
            switch (def.Shape)
            {
                case SdbShape.Vectors:
                {
                    int len = a.Count(def.Width) * def.Width;
                    offsets[i] = a.Skip(len);
                    lengths[i] = len;
                    break;
                }
                case SdbShape.PairU16:
                {
                    int r = a.Position;
                    a.Skip(4);
                    a.Skip(a.Count(2) * 2);
                    offsets[i] = r;
                    lengths[i] = a.Position - r;
                    break;
                }
                case SdbShape.U16Records8:
                {
                    int r = a.Position;
                    a.Skip(2);
                    a.Skip(a.Count(8) * 8);
                    offsets[i] = r;
                    lengths[i] = a.Position - r;
                    break;
                }
                case SdbShape.Seven:
                {
                    int r = a.Position;
                    for (int k = 0; k < 7; k++) a.Skip(a.Count(2) * 2);
                    offsets[i] = r;
                    lengths[i] = a.Position - r;
                    break;
                }
                case SdbShape.Trailing:
                {
                    int r = a.Position;
                    a.Skip(23);
                    int ga = a.Count(9);
                    for (int k = 0; k < ga; k++)
                    {
                        a.Skip(8);
                        a.Skip(a.Count(1));
                    }
                    int gb = a.Count(5);
                    for (int k = 0; k < gb; k++)
                    {
                        a.Skip(4);
                        a.Skip(a.Count(4) * 4);
                    }
                    offsets[i] = r;
                    lengths[i] = a.Position - r;
                    break;
                }
                default:
                    throw new SdbFormatException($"unknown SDB table shape {def.Shape}");
            }
        }
        _tables[def.Key] = new SdbTable(def.Key, def.Shape, def.Width, start, a.Position, count, 0,
                                        offsets, lengths);
    }

    /// <summary>Every route reference checked once, which is what proves the file is what we think it is.</summary>
    private void CheckRoutes()
    {
        var routes = Table(SdbFormat.TableRoutes);
        int[] keys = [SdbFormat.TableMaterialNames, SdbFormat.TableSelectors, SdbFormat.TableTokens,
                      SdbFormat.TableSlots, SdbFormat.TableValues];
        int[] counts = [.. keys.Select(k => Table(k).Count)];
        for (int i = 0; i < routes.Count; i++)
        {
            var row = _a.AsSpan(routes.Offset(i), 10);
            for (int k = 0; k < 5; k++)
            {
                int value = BinaryPrimitives.ReadUInt16LittleEndian(row[(k * 2)..]);
                if (value >= counts[k])
                    throw new SdbFormatException(
                        $"{Path}: route {i} references 0x{keys[k]:X2}[{value}] of {counts[k]}");
            }
        }
    }

    // ---- raw access ------------------------------------------------------------------------------------------

    public SdbTable Table(int key) =>
        _tables.TryGetValue(key, out var t) ? t : throw new SdbFormatException($"unknown SDB table 0x{key:X2}");

    public ReadOnlySpan<byte> Record(int key, int index)
    {
        var t = Table(key);
        if ((uint)index >= (uint)t.Count)
            throw new SdbFormatException($"0x{key:X2}[{index}] out of range (count {t.Count})");
        return _a.AsSpan(t.Offset(index), t.Length(index));
    }

    public string RecordHex(int key, int index) => Convert.ToHexString(Record(key, index)).ToLowerInvariant();

    public string Text(int key, int index)
    {
        if (Array.IndexOf(SdbFormat.StringTables, key) < 0)
            throw new SdbFormatException($"0x{key:X2} is not a string table");
        return Encoding.UTF8.GetString(Record(key, index));
    }

    /// <summary>A DXBC program blob, faulted in from block B on demand.</summary>
    public byte[] Program(int kind, int index)
    {
        if (!_programs.TryGetValue(kind, out var spans) || (uint)index >= (uint)spans.Length)
            throw new SdbFormatException($"program kind {kind} index {index} out of range");
        var (offset, len) = spans[index];
        var buffer = new byte[len];
        if (len > 0)
        {
            ObjectDisposedException.ThrowIf(_disposed || _mmf is null, this);
            using var view = _mmf.CreateViewStream(offset, len, MemoryMappedFileAccess.Read);
            view.ReadExactly(buffer);
        }
        return buffer;
    }

    /// <summary>(material B2, program C2, tokens B6, slots CE, values D2) — the on-disk order.</summary>
    public (int Material, int Program, int Tokens, int Slots, int Values) RouteRow(int index)
    {
        var row = Record(SdbFormat.TableRoutes, index);
        return (BinaryPrimitives.ReadUInt16LittleEndian(row),
                BinaryPrimitives.ReadUInt16LittleEndian(row[2..]),
                BinaryPrimitives.ReadUInt16LittleEndian(row[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(row[6..]),
                BinaryPrimitives.ReadUInt16LittleEndian(row[8..]));
    }

    /// <summary>The 0xBA string index a preset record names itself with, without decoding the record.</summary>
    public int PresetNameId(int index) =>
        BinaryPrimitives.ReadInt32LittleEndian(Record(SdbFormat.TablePresets, index)[8..]);

    public string MaterialName(int index) => Text(SdbFormat.TableMaterialNames, index);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mmf?.Dispose();
    }
}

/// <summary>
/// Reads the stream's packed integers: <c>tag = b &amp; 3</c>, width <c>(1, 2, 4, 1)[tag]</c>, value is the
/// little-endian word shifted right by two.
/// </summary>
internal struct SdbCursor(byte[] data, int start, int end, int[] tags)
{
    private static ReadOnlySpan<int> Widths => [1, 2, 4, 1];

    private readonly byte[] _data = data;
    private readonly int[] _tags = tags;

    public int Position = start;
    public int End { get; } = end;

    public int Skip(int n)
    {
        if (n < 0 || Position + n > End)
            throw new SdbFormatException($"SDB stream: need {n} bytes at 0x{Position:X}, block ends at 0x{End:X}");
        int p = Position;
        Position += n;
        return p;
    }

    public int Compact()
    {
        int p = Position;
        if (p >= End) throw new SdbFormatException($"SDB stream: compact int past block end at 0x{p:X}");
        byte b = _data[p];
        int tag = b & 3;
        int w = Widths[tag];
        if (p + w > End) throw new SdbFormatException($"SDB stream: truncated compact int at 0x{p:X}");
        _tags[tag]++;
        Position = p + w;
        return w switch
        {
            1 => b >> 2,
            2 => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(p)) >> 2,
            _ => (int)(BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(p)) >> 2),
        };
    }

    /// <summary>A compact count, rejected when that many records of <paramref name="minimum"/> bytes cannot fit.</summary>
    public int Count(int minimum)
    {
        int n = Compact();
        if (minimum != 0 && n > (End - Position) / minimum)
            throw new SdbFormatException($"SDB stream: count {n} cannot fit at 0x{Position:X}");
        return n;
    }
}
