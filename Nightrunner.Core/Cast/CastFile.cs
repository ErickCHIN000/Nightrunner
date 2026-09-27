// Port of the Cast library by DTZxPorter (https://github.com/dtzxporter/cast), vendored by the prototype as
// nightrunner-main/nightrunner/cast/castlib.py (upstream commit a8ca18a0acf3b97b19332c53b54b47fcc3217755), used under
// the licence below (also in licenses/Cast.MIT.txt), which applies to this port as well.
//
// MIT License
//
// Copyright (c) 2020 Nick
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Nightrunner.Core.Cast;

/// <summary>A Cast file that cannot be read, or a tree that cannot be written, named by its exact problem.</summary>
public sealed class CastFormatException(string message) : Exception(message);

/// <summary>
/// A Cast file: a list of root nodes (Python <c>Cast</c>; renamed because <c>Cast</c> is this namespace).
/// </summary>
/// <remarks>
/// <code>
/// file      u32 magic 'cast' (0x74736163), u32 version (1), u32 root count, u32 reserved (written 0, ignored on read)
/// node      u32 identifier, u32 length (whole node incl. children; recomputed on write, ignored on read),
///           u64 hash, u32 property count, u32 child count, properties, children
/// property  char[2] type (NUL-padded), u16 name length, u32 element count, name (UTF-8, no NUL), payload:
///           count × element (b h i l f d 2v 3v 4v, little-endian), or for "s" one NUL-terminated UTF-8 string
///           (the count is not read; 1 is written)
/// </code>
/// No alignment anywhere. Version 1 is the only one this library knows (the vendored copy refuses others). The
/// writer reproduces castlib byte for byte: same property order (dict insertion order), same widths, same hashes when
/// the <see cref="CastHashSequence"/> is in the same state as the prototype's global counter.
/// <para/>
/// Name clashes: <see cref="File"/> meets <c>System.IO.File</c> and <see cref="Mesh"/>/<see cref="Model"/> the
/// <c>Nightrunner.Core.Mesh</c>/<c>.Model</c> namespaces outside this namespace; alias them there
/// (<c>using CastMesh = Nightrunner.Core.Cast.Mesh;</c>).
/// </remarks>
public sealed class CastFile
{
    public const uint Magic = 0x74736163;   // 'cast'
    public const uint Version = 1;
    public const int MaxRoots = 4096;
    public const int NodeHeaderSize = 0x18, PropertyHeaderSize = 8, FileHeaderSize = 16;
    private const int MaxDepth = 4096;

    public CastFile() : this(new CastHashSequence()) { }

    public CastFile(CastHashSequence hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        Hashes = hashes;
    }

    /// <summary>The sequence new nodes of this file take their hashes from.</summary>
    public CastHashSequence Hashes { get; }

    /// <summary>The root-level nodes, in file order. A file may hold any node type here, not only <see cref="Root"/>.</summary>
    public List<CastNode> RootNodes { get; } = [];

    public IReadOnlyList<CastNode> Roots() => [.. RootNodes];

    public Root CreateRoot()
    {
        var root = new Root(Hashes);
        RootNodes.Add(root);
        return root;
    }

    // ---- reading ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads a Cast file. Like the prototype, every node read takes one hash from <paramref name="hashes"/> (a new
    /// sequence at <see cref="CastHashSequence.Base"/> when null) before its stored hash replaces it, so nodes created
    /// afterwards get the hashes a fresh Python process would give them.
    /// </summary>
    public static CastFile Load(string path, CastHashSequence? hashes = null) => Read(System.IO.File.ReadAllBytes(path), hashes);

    public static CastFile Read(ReadOnlySpan<byte> data, CastHashSequence? hashes = null)
    {
        RequireLittleEndian();
        if (data.Length < FileHeaderSize) throw new CastFormatException($"not a cast file: {data.Length} bytes, header needs {FileHeaderSize}");
        uint magic = U32(data, 0), version = U32(data, 4), roots = U32(data, 8);
        if (magic != Magic) throw new CastFormatException($"not a cast file (bad magic 0x{magic:X8})");
        if (version != Version) throw new CastFormatException($"unsupported cast version {version}");
        if (roots > MaxRoots) throw new CastFormatException($"cast file declares {roots} root nodes");
        var cast = new CastFile(hashes ?? new CastHashSequence());
        int pos = FileHeaderSize;
        for (uint i = 0; i < roots; i++) cast.RootNodes.Add(ReadNode(data, ref pos, cast.Hashes, 0));
        return cast;
    }

    private static CastNode ReadNode(ReadOnlySpan<byte> d, ref int pos, CastHashSequence hashes, int depth)
    {
        if (depth > MaxDepth) throw new CastFormatException($"node nesting deeper than {MaxDepth} at offset {pos}");
        int start = pos;
        Need(d, pos, NodeHeaderSize, "node header");
        uint identifier = U32(d, pos), length = U32(d, pos + 4), propertyCount = U32(d, pos + 16), childCount = U32(d, pos + 20);
        ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(d[(pos + 8)..]);
        pos += NodeHeaderSize;
        var node = NewNode(identifier, hashes);
        node.Hash = hash;
        for (uint i = 0; i < propertyCount; i++)
        {
            int propertyStart = pos;
            var property = ReadProperty(d, ref pos);
            if (property is null)
            {
                // A type castlib has no size for (it raises): keep the node's whole body, bounded by its length field.
                long end = start + (long)length;
                if (end < propertyStart || end > d.Length)
                    throw new CastFormatException($"node 0x{identifier:X8} at offset {start}: property type " +
                        $"'{Convert.ToHexString(d.Slice(propertyStart, 2))}' at offset {propertyStart} is unknown and the node length {length} does not bound it");
                node.MakeOpaque(d[(start + NodeHeaderSize)..(int)end].ToArray(), propertyCount, childCount);
                pos = (int)end;
                return node;
            }
            node.LoadProperty(property);
        }
        if (childCount > (uint)((d.Length - pos) / NodeHeaderSize))
            throw new CastFormatException($"node 0x{identifier:X8} at offset {start} declares {childCount} children, more than the file can hold");
        for (uint i = 0; i < childCount; i++) node.LoadChild(ReadNode(d, ref pos, hashes, depth + 1));
        return node;
    }

    /// <summary>One property, or null (position unspecified) when its type identifier is not one of the ten.</summary>
    private static CastProperty? ReadProperty(ReadOnlySpan<byte> d, ref int pos)
    {
        Need(d, pos, PropertyHeaderSize, "property header");
        var type = ParseType(d.Slice(pos, 2));
        if (type is null) return null;
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(d[(pos + 2)..]);
        uint count = U32(d, pos + 4);
        pos += PropertyHeaderSize;
        Need(d, pos, nameLength, "property name");
        string name = Decode(d.Slice(pos, nameLength), pos, "property name");
        pos += nameLength;
        var property = new CastProperty(name, type.Value);
        if (type == CastPropertyType.String)
        {
            int nul = d[pos..].IndexOf((byte)0);
            if (nul < 0) throw new CastFormatException($"property '{name}': string at offset {pos} has no terminating NUL");
            property.LoadString(Decode(d.Slice(pos, nul), pos, $"property '{name}'"));
            pos += nul + 1;
            return property;
        }
        long bytes = (long)property.ElementSize * count;
        if (bytes > d.Length - pos)
            throw new CastFormatException($"property '{name}' at offset {pos}: {count} × {property.ElementSize} bytes run past the end of the file");
        var payload = d.Slice(pos, (int)bytes);
        property.LoadValues(type switch
        {
            CastPropertyType.Byte => payload.ToArray(),
            CastPropertyType.Short => MemoryMarshal.Cast<byte, ushort>(payload).ToArray(),
            CastPropertyType.Integer => MemoryMarshal.Cast<byte, uint>(payload).ToArray(),
            CastPropertyType.Long => MemoryMarshal.Cast<byte, ulong>(payload).ToArray(),
            CastPropertyType.Double => MemoryMarshal.Cast<byte, double>(payload).ToArray(),
            _ => MemoryMarshal.Cast<byte, float>(payload).ToArray(),
        });
        pos += (int)bytes;
        return property;
    }

    /// <summary><c>header[0].decode("utf-8").strip('\0')</c> looked up among the ten identifiers.</summary>
    private static CastPropertyType? ParseType(ReadOnlySpan<byte> id)
    {
        id = id.Trim((byte)0);
        if (id.Length is 0 or > 2) return null;
        foreach (var b in id) if (b >= 0x80) return null;
        return CastProperty.ParseIdentifier(System.Text.Encoding.ASCII.GetString(id));
    }

    private static CastNode NewNode(uint identifier, CastHashSequence h) => identifier switch
    {
        Root.Id => new Root(h),
        Model.Id => new Model(h),
        Mesh.Id => new Mesh(h),
        Hair.Id => new Hair(h),
        BlendShape.Id => new BlendShape(h),
        Skeleton.Id => new Skeleton(h),
        Animation.Id => new Animation(h),
        Curve.Id => new Curve(h),
        CurveModeOverride.Id => new CurveModeOverride(h),
        NotificationTrack.Id => new NotificationTrack(h),
        Bone.Id => new Bone(h),
        IKHandle.Id => new IKHandle(h),
        Constraint.Id => new Constraint(h),
        Material.Id => new Material(h),
        File.Id => new File(h),
        Color.Id => new Color(h),
        Instance.Id => new Instance(h),
        Metadata.Id => new Metadata(h),
        _ => new CastNode(identifier, h),
    };

    private static string Decode(ReadOnlySpan<byte> bytes, int at, string what)
    {
        try { return CastProperty.Utf8.GetString(bytes); }
        catch (System.Text.DecoderFallbackException) { throw new CastFormatException($"{what} at offset {at} is not valid UTF-8"); }
    }

    // ---- writing ----------------------------------------------------------------------------------------------

    /// <summary>Writes the file to <paramref name="path"/> directly (as <c>Cast.save</c>; no temporary file).</summary>
    public void Save(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        Write(fs);
    }

    public byte[] ToBytes()
    {
        var lengths = ComputeLengths();
        long total = FileHeaderSize;
        foreach (var r in RootNodes) total += lengths[r];
        if (total > Array.MaxLength) throw new CastFormatException($"cast file of {total} bytes does not fit one array");
        var buffer = new byte[total];
        using var ms = new MemoryStream(buffer);
        WriteCore(ms, lengths);
        return buffer;
    }

    public void Write(Stream stream) => WriteCore(stream, ComputeLengths());

    private void WriteCore(Stream s, Dictionary<CastNode, long> lengths)
    {
        RequireLittleEndian();
        Span<byte> header = stackalloc byte[FileHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], checked((uint)RootNodes.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 0);
        s.Write(header);
        foreach (var root in RootNodes) WriteNode(s, root, lengths);
    }

    private Dictionary<CastNode, long> ComputeLengths()
    {
        var lengths = new Dictionary<CastNode, long>(ReferenceEqualityComparer.Instance);
        foreach (var r in RootNodes) Measure(r, lengths, 0);
        return lengths;
    }

    private static long Measure(CastNode node, Dictionary<CastNode, long> lengths, int depth)
    {
        if (depth > MaxDepth) throw new CastFormatException($"node nesting deeper than {MaxDepth}");
        long length;
        if (node.RawBody is not null) length = NodeHeaderSize + node.RawBody.LongLength;
        else
        {
            length = NodeHeaderSize;
            foreach (var p in node.PropertyValues)
            {
                int nameBytes = CastProperty.Utf8.GetByteCount(p.Name);
                if (nameBytes > ushort.MaxValue) throw new CastFormatException($"property name of {nameBytes} bytes does not fit u16");
                length += p.Length();
            }
            foreach (var c in node.ChildNodes) length += Measure(c, lengths, depth + 1);
        }
        if (length > uint.MaxValue) throw new CastFormatException($"node 0x{node.Identifier:X8} is {length} bytes; the length field is u32");
        if (!lengths.TryAdd(node, length)) throw new CastFormatException($"node 0x{node.Identifier:X8} appears twice in the tree");
        return length;
    }

    private static void WriteNode(Stream s, CastNode node, Dictionary<CastNode, long> lengths)
    {
        Span<byte> h = stackalloc byte[NodeHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(h, node.Identifier);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)lengths[node]);
        BinaryPrimitives.WriteUInt64LittleEndian(h[8..], node.Hash);
        if (node.RawBody is not null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h[16..], node.DeclaredPropertyCount);
            BinaryPrimitives.WriteUInt32LittleEndian(h[20..], node.DeclaredChildCount);
            s.Write(h);
            s.Write(node.RawBody);
            return;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], (uint)node.PropertyCount);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], (uint)node.ChildNodes.Count);
        s.Write(h);
        foreach (var p in node.PropertyValues) WriteProperty(s, p);
        foreach (var c in node.ChildNodes) WriteNode(s, c, lengths);
    }

    private static void WriteProperty(Stream s, CastProperty p)
    {
        byte[] name = CastProperty.Utf8.GetBytes(p.Name);
        Span<byte> h = stackalloc byte[PropertyHeaderSize];
        string id = p.Identifier;
        h[0] = (byte)id[0];
        h[1] = id.Length > 1 ? (byte)id[1] : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(h[2..], (ushort)name.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)p.Count);
        s.Write(h);
        s.Write(name);
        if (p.Type == CastPropertyType.String)
        {
            s.Write(CastProperty.Utf8.GetBytes(p.StringValue ?? throw p.NoString()));
            s.WriteByte(0);
        }
        else s.Write(p.PayloadBytes());
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static uint U32(ReadOnlySpan<byte> d, int at) => BinaryPrimitives.ReadUInt32LittleEndian(d[at..]);

    private static void Need(ReadOnlySpan<byte> d, int pos, int count, string what)
    {
        if (count > d.Length - pos) throw new CastFormatException($"{what} at offset {pos} runs past the end of the file ({d.Length} bytes)");
    }

    // Typed arrays are copied to and from the file as raw memory: Cast is little-endian.
    private static void RequireLittleEndian()
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Cast I/O needs a little-endian machine");
    }
}
