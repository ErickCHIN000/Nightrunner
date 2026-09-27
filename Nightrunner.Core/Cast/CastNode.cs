// Port of the Cast library by DTZxPorter (https://github.com/dtzxporter/cast), MIT licence, Copyright (c) 2020 Nick.
// The full licence text is at the top of CastFile.cs (and licenses/Cast.MIT.txt).

using System.Runtime.InteropServices;
using System.Text;

namespace Nightrunner.Core.Cast;

/// <summary>The ten Cast property types (<c>CastProperty_t</c>); the identifier strings are the on-disk ones.</summary>
public enum CastPropertyType : byte
{
    /// <summary><c>"b"</c>, u8.</summary>
    Byte,
    /// <summary><c>"h"</c>, u16.</summary>
    Short,
    /// <summary><c>"i"</c>, u32.</summary>
    Integer,
    /// <summary><c>"l"</c>, u64.</summary>
    Long,
    /// <summary><c>"f"</c>, f32.</summary>
    Float,
    /// <summary><c>"d"</c>, f64.</summary>
    Double,
    /// <summary><c>"s"</c>, one NUL-terminated UTF-8 string.</summary>
    String,
    /// <summary><c>"2v"</c>, 2 × f32 per element.</summary>
    Vector2,
    /// <summary><c>"3v"</c>, 3 × f32 per element.</summary>
    Vector3,
    /// <summary><c>"4v"</c>, 4 × f32 per element.</summary>
    Vector4,
}

/// <summary>
/// A single property of a Cast node (<c>CastProperty</c>). Values live in one typed array — <c>byte[]</c>,
/// <c>ushort[]</c>, <c>uint[]</c>, <c>ulong[]</c>, <c>float[]</c> (also the flattened vectors) or <c>double[]</c> — or,
/// for <c>"s"</c>, one string. Setting an array keeps the reference; it is not copied.
/// </summary>
public sealed class CastProperty
{
    internal static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly string[] Ids = ["b", "h", "i", "l", "f", "d", "s", "2v", "3v", "4v"];
    private static readonly int[] Sizes = [1, 2, 4, 8, 4, 8, 0, 8, 12, 16];
    private static readonly int[] Arities = [1, 1, 1, 1, 1, 1, 1, 2, 3, 4];

    private Array _values;
    private string? _string;

    internal CastProperty(string name, CastPropertyType type)
    {
        Name = name;
        Type = type;
        _values = EmptyOf(type);
    }

    public string Name { get; }
    public CastPropertyType Type { get; }
    /// <summary>The on-disk type identifier: <c>b h i l f d s 2v 3v 4v</c>.</summary>
    public string Identifier => Ids[(int)Type];
    /// <summary>Bytes per element (0 for strings).</summary>
    public int ElementSize => Sizes[(int)Type];
    /// <summary>Scalars per element: 2/3/4 for the vector types, 1 otherwise.</summary>
    public int Arity => Arities[(int)Type];

    /// <summary>Number of scalars held (Python <c>len(values)</c>); 1 for a string that is set.</summary>
    public int ValueCount => Type == CastPropertyType.String ? (_string is null ? 0 : 1) : _values.Length;
    /// <summary>The element count written to the file (<c>len(values) / array</c>).</summary>
    public int Count => ValueCount / Arity;

    /// <summary>The raw typed array (a one-element <c>string[]</c> for a string property).</summary>
    public Array Values => Type == CastPropertyType.String ? (_string is null ? Array.Empty<string>() : new[] { _string }) : _values;

    public byte[] Bytes => As<byte[]>(CastPropertyType.Byte);
    public ushort[] Shorts => As<ushort[]>(CastPropertyType.Short);
    public uint[] Integers => As<uint[]>(CastPropertyType.Integer);
    public ulong[] Longs => As<ulong[]>(CastPropertyType.Long);
    public double[] Doubles => As<double[]>(CastPropertyType.Double);
    /// <summary>The <c>f</c>/<c>2v</c>/<c>3v</c>/<c>4v</c> values, vectors flattened.</summary>
    public float[] Floats => IsFloatBacked(Type) ? (float[])_values : throw Mismatch("f/2v/3v/4v");
    /// <summary>The string of an <c>"s"</c> property (null until set).</summary>
    public string? StringValue => Type == CastPropertyType.String ? _string : throw Mismatch("s");

    public bool IsType(string identifier) => Identifier == identifier;

    public CastProperty SetValues(byte[] values) => Store(values, CastPropertyType.Byte);
    public CastProperty SetValues(ushort[] values) => Store(values, CastPropertyType.Short);
    public CastProperty SetValues(uint[] values) => Store(values, CastPropertyType.Integer);
    public CastProperty SetValues(ulong[] values) => Store(values, CastPropertyType.Long);
    public CastProperty SetValues(double[] values) => Store(values, CastPropertyType.Double);

    /// <summary>Values of an <c>f</c>, <c>2v</c>, <c>3v</c> or <c>4v</c> property; vectors flattened.</summary>
    public CastProperty SetValues(float[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!IsFloatBacked(Type)) throw Mismatch("f/2v/3v/4v");
        if (values.Length % Arity != 0)
            throw new ArgumentException($"property '{Name}' ({Identifier}) needs a multiple of {Arity} floats, got {values.Length}");
        _values = values;
        return this;
    }

    /// <summary>The value of an <c>"s"</c> property. An embedded NUL is refused: the file would not read back.</summary>
    public CastProperty SetString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Type != CastPropertyType.String) throw Mismatch("s");
        if (value.Contains('\0'))
            throw new ArgumentException($"property '{Name}': a Cast string cannot hold a NUL (it ends the string on read)");
        _string = value;
        return this;
    }

    /// <summary>Integer value <paramref name="index"/> of a <c>b</c>/<c>h</c>/<c>i</c>/<c>l</c> property.</summary>
    public ulong IntegerAt(int index) => _values switch
    {
        byte[] a => a[index],
        ushort[] a => a[index],
        uint[] a => a[index],
        ulong[] a => a[index],
        _ => throw Mismatch("b/h/i/l"),
    };

    /// <summary>Numeric value <paramref name="index"/> of any non-string property.</summary>
    public double NumberAt(int index) => _values switch
    {
        byte[] a => a[index],
        ushort[] a => a[index],
        uint[] a => a[index],
        ulong[] a => a[index],
        float[] a => a[index],
        double[] a => a[index],
        _ => throw Mismatch("numeric"),
    };

    /// <summary>The values as <c>uint</c>: <c>i</c> returns its own array, <c>b</c>/<c>h</c> widen, <c>l</c> is range-checked.</summary>
    public uint[] ToUInt32Array() => _values switch
    {
        uint[] a => a,
        byte[] a => Array.ConvertAll(a, v => (uint)v),
        ushort[] a => Array.ConvertAll(a, v => (uint)v),
        ulong[] a => Array.ConvertAll(a, v => checked((uint)v)),
        _ => throw Mismatch("b/h/i/l"),
    };

    /// <summary>The values as <c>float</c>: float-backed types return their own array, others convert.</summary>
    public float[] ToSingleArray() => _values switch
    {
        float[] a => a,
        double[] a => Array.ConvertAll(a, v => (float)v),
        _ when Type != CastPropertyType.String => Array.ConvertAll(ToDoubleArray(), v => (float)v),
        _ => throw Mismatch("numeric"),
    };

    /// <summary>The values as <c>double</c> (always a new array).</summary>
    public double[] ToDoubleArray()
    {
        var r = new double[ValueCount];
        if (Type == CastPropertyType.String) throw Mismatch("numeric");
        for (int i = 0; i < r.Length; i++) r[i] = NumberAt(i);
        return r;
    }

    /// <summary>Encoded size in bytes: 8-byte header, name, payload (Python <c>length()</c>).</summary>
    public long Length()
    {
        long result = 8 + Utf8.GetByteCount(Name);
        if (Type == CastPropertyType.String)
            return result + Utf8.GetByteCount(_string ?? throw NoString()) + 1;
        return result + (long)ElementSize * Count;
    }

    /// <summary>The payload bytes (not the string case).</summary>
    internal ReadOnlySpan<byte> PayloadBytes() => _values switch
    {
        byte[] a => a,
        ushort[] a => MemoryMarshal.AsBytes(a.AsSpan()),
        uint[] a => MemoryMarshal.AsBytes(a.AsSpan()),
        ulong[] a => MemoryMarshal.AsBytes(a.AsSpan()),
        float[] a => MemoryMarshal.AsBytes(a.AsSpan()),
        double[] a => MemoryMarshal.AsBytes(a.AsSpan()),
        _ => throw Mismatch("numeric"),
    };

    internal InvalidOperationException NoString() => new($"string property '{Name}' has no value");

    /// <summary><c>castTypeForMaximum</c>: <c>"b"</c> when every value fits a byte, <c>"h"</c> a u16, else <c>"i"</c>. Empty input
    /// is refused, as <c>max()</c> refuses it in the prototype.</summary>
    public static string TypeForMaximum(ReadOnlySpan<uint> values)
    {
        if (values.IsEmpty) throw new ArgumentException("castTypeForMaximum: empty value list has no maximum", nameof(values));
        uint maximum = 0;
        foreach (var v in values) if (v > maximum) maximum = v;
        return maximum <= 0xFF ? "b" : maximum <= 0xFFFF ? "h" : "i";
    }

    /// <summary>Maps an on-disk identifier to its type, or null when it is none of the ten.</summary>
    public static CastPropertyType? ParseIdentifier(string identifier)
    {
        int i = Array.IndexOf(Ids, identifier);
        return i < 0 ? null : (CastPropertyType)i;
    }

    internal void LoadValues(Array values) => _values = values;
    internal void LoadString(string value) => _string = value;

    private static bool IsFloatBacked(CastPropertyType t) =>
        t is CastPropertyType.Float or CastPropertyType.Vector2 or CastPropertyType.Vector3 or CastPropertyType.Vector4;

    private static Array EmptyOf(CastPropertyType t) => t switch
    {
        CastPropertyType.Byte => Array.Empty<byte>(),
        CastPropertyType.Short => Array.Empty<ushort>(),
        CastPropertyType.Integer => Array.Empty<uint>(),
        CastPropertyType.Long => Array.Empty<ulong>(),
        CastPropertyType.Double => Array.Empty<double>(),
        CastPropertyType.String => Array.Empty<string>(),
        _ => Array.Empty<float>(),
    };

    private T As<T>(CastPropertyType t) where T : class => Type == t ? (T)(object)_values : throw Mismatch(Ids[(int)t]);

    private CastProperty Store(Array values, CastPropertyType t)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (Type != t) throw Mismatch(Ids[(int)t]);
        _values = values;
        return this;
    }

    private InvalidOperationException Mismatch(string wanted) =>
        new($"property '{Name}' is '{Identifier}', not {wanted}");
}

/// <summary>
/// Hands out node hashes. The prototype numbers every node it constructs — created or loaded — from one
/// process-global counter starting at <see cref="Base"/>; here each <see cref="CastFile"/> owns a sequence (a fresh
/// one reproduces a fresh Python process). Share one sequence between files to reproduce a process that built or
/// loaded several.
/// </summary>
public sealed class CastHashSequence
{
    /// <summary><c>castHashBase</c>, "PRTRWINS" little-endian.</summary>
    public const ulong Base = 0x534E495752545250;

    public CastHashSequence(ulong next = Base) => Next = next;

    /// <summary>The hash the next node receives.</summary>
    public ulong Next { get; set; }

    /// <summary><c>castNextHash</c>.</summary>
    public ulong Take() => Next++;
}

/// <summary>
/// A generic Cast node (<c>CastNode</c>): identifier, hash, ordered properties, children. Node types the library
/// knows are subclasses; any other identifier reads into a plain <see cref="CastNode"/> and writes back unchanged.
/// </summary>
/// <remarks>
/// Properties are keyed by name in insertion order, with Python <c>dict</c> semantics: re-creating an existing name
/// replaces it in its original position; removing and re-creating moves it to the end.
/// <para/>
/// A node read from a file whose properties include a type this library has no size for keeps its whole body as
/// <see cref="RawBody"/> (the prototype cannot read such a file at all). It writes back verbatim; its parsed
/// properties are informational and it refuses edits.
/// </remarks>
public class CastNode
{
    private readonly OrderedDictionary<string, CastProperty> _properties = new(StringComparer.Ordinal);
    private readonly List<CastNode> _children = [];

    public CastNode(uint identifier, CastHashSequence hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        Identifier = identifier;
        Hashes = hashes;
        Hash = hashes.Take();
    }

    public uint Identifier { get; }
    /// <summary>The node's unique id; hash-valued properties (<c>m</c>, slots, bones) point at it.</summary>
    public ulong Hash { get; set; }
    /// <summary>The sequence the <c>Create*</c> helpers take new hashes from.</summary>
    public CastHashSequence Hashes { get; }
    public CastNode? ParentNode { get; private set; }
    public IReadOnlyList<CastNode> ChildNodes => _children;
    public IReadOnlyDictionary<string, CastProperty> Properties => _properties;

    /// <summary>Undecoded node body (everything after the 24-byte header), when a property type was unknown.</summary>
    public byte[]? RawBody { get; private set; }
    public bool IsOpaque => RawBody is not null;
    /// <summary>Header counts kept for an opaque node.</summary>
    public uint DeclaredPropertyCount { get; private set; }
    public uint DeclaredChildCount { get; private set; }

    /// <summary>First child whose runtime type is exactly <typeparamref name="T"/> (<c>ChildOfType</c>).</summary>
    public T? ChildOfType<T>() where T : CastNode
    {
        foreach (var x in _children) if (x.GetType() == typeof(T)) return (T)x;
        return null;
    }

    /// <summary>Every child whose runtime type is exactly <typeparamref name="T"/> (<c>ChildrenOfType</c>).</summary>
    public List<T> ChildrenOfType<T>() where T : CastNode
    {
        var r = new List<T>();
        foreach (var x in _children) if (x.GetType() == typeof(T)) r.Add((T)x);
        return r;
    }

    public CastNode? ChildByHash(ulong hash)
    {
        foreach (var x in _children) if (x.Hash == hash) return x;
        return null;
    }

    /// <summary><c>properties.get(name)</c>.</summary>
    public CastProperty? Property(string name) => _properties.GetValueOrDefault(name);

    public bool HasProperty(string name) => _properties.ContainsKey(name);

    /// <summary>Creates (or replaces, in place) the property <paramref name="name"/> of the given type identifier.</summary>
    public CastProperty CreateProperty(string name, string type) =>
        CreateProperty(name, CastProperty.ParseIdentifier(type) ?? throw new ArgumentException($"unknown Cast property type '{type}'"));

    public CastProperty CreateProperty(string name, CastPropertyType type)
    {
        ArgumentNullException.ThrowIfNull(name);
        GuardEditable();
        var property = new CastProperty(name, type);
        _properties[name] = property;
        return property;
    }

    /// <summary><c>properties.pop(name, None)</c>.</summary>
    public bool RemoveProperty(string name)
    {
        GuardEditable();
        return _properties.Remove(name);
    }

    public T CreateChild<T>(T child) where T : CastNode
    {
        ArgumentNullException.ThrowIfNull(child);
        GuardEditable();
        child.ParentNode = this;
        _children.Add(child);
        return child;
    }

    /// <summary><c>childNodes.remove(child)</c>.</summary>
    public bool RemoveChild(CastNode child)
    {
        GuardEditable();
        if (!_children.Remove(child)) return false;
        child.ParentNode = null;
        return true;
    }

    /// <summary>Encoded size in bytes of this node and everything under it (Python <c>length()</c>).</summary>
    public long Length()
    {
        if (RawBody is not null) return 0x18 + RawBody.LongLength;
        long result = 0x18;
        foreach (var p in _properties.Values) result += p.Length();
        foreach (var c in _children) result += c.Length();
        return result;
    }

    // ---- helpers shared by the typed nodes ------------------------------------------------------------------

    protected string? GetString(string name) => Property(name)?.StringValue;
    protected void SetStringProperty(string name, string value) => CreateProperty(name, CastPropertyType.String).SetString(value);
    protected float[]? GetFloats(string name) => Property(name)?.ToSingleArray();
    protected uint[]? GetUInts(string name) => Property(name)?.ToUInt32Array();
    protected bool GetFlag(string name, bool fallback) => Property(name) is { } p ? p.NumberAt(0) >= 1 : fallback;
    protected void SetFlag(string name, bool enabled) => CreateProperty(name, CastPropertyType.Byte).SetValues([(byte)(enabled ? 1 : 0)]);
    protected void SetHash(string name, ulong hash) => CreateProperty(name, CastPropertyType.Long).SetValues([hash]);
    protected void SetFloat(string name, float value) => CreateProperty(name, CastPropertyType.Float).SetValues([value]);

    protected void SetVector(string name, CastPropertyType type, ReadOnlySpan<float> values) =>
        CreateProperty(name, type).SetValues(values.ToArray());

    /// <summary>Hash-valued property → the node it names among <paramref name="scope"/>'s children (null when absent,
    /// not integer-typed, or unmatched).</summary>
    protected CastNode? Referenced(string name, CastNode? scope)
    {
        var p = Property(name);
        if (p is null || scope is null || p.Type > CastPropertyType.Long || p.ValueCount == 0) return null;
        return scope.ChildByHash(p.IntegerAt(0));
    }

    /// <summary>An integer buffer typed by its maximum (<c>castTypeForMaximum</c>): <c>b</c>, <c>h</c> or <c>i</c>.</summary>
    protected void SetIntegerBuffer(string name, ReadOnlySpan<uint> values)
    {
        switch (CastProperty.TypeForMaximum(values))
        {
            case "b":
                var b = new byte[values.Length];
                for (int i = 0; i < b.Length; i++) b[i] = (byte)values[i];
                CreateProperty(name, CastPropertyType.Byte).SetValues(b);
                break;
            case "h":
                var h = new ushort[values.Length];
                for (int i = 0; i < h.Length; i++) h[i] = (ushort)values[i];
                CreateProperty(name, CastPropertyType.Short).SetValues(h);
                break;
            default:
                CreateProperty(name, CastPropertyType.Integer).SetValues(values.ToArray());
                break;
        }
    }

    // ---- reader hooks ---------------------------------------------------------------------------------------

    internal void LoadProperty(CastProperty property) => _properties[property.Name] = property;

    internal void LoadChild(CastNode child)
    {
        child.ParentNode = this;
        _children.Add(child);
    }

    internal void MakeOpaque(byte[] body, uint propertyCount, uint childCount)
    {
        RawBody = body;
        DeclaredPropertyCount = propertyCount;
        DeclaredChildCount = childCount;
    }

    internal IEnumerable<CastProperty> PropertyValues => _properties.Values;
    internal int PropertyCount => _properties.Count;

    private void GuardEditable()
    {
        if (RawBody is not null)
            throw new InvalidOperationException($"node 0x{Identifier:X8} holds undecoded bytes (unknown property type) and is kept verbatim; it cannot be edited");
    }
}

/// <summary>Colour helpers (<c>CastColor</c>), ported as vendored.</summary>
public static class CastColor
{
    public static double SRGBToLinear(double srgb) =>
        srgb <= 0.04045 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);

    /// <summary>
    /// As vendored: <c>(pow(linear, 1/2.4) + 1.055) + -0.055</c>. The standard curve is
    /// <c>1.055 · pow(linear, 1/2.4) − 0.055</c>; this one returns 1.749 for 0.5. Not fixed here (see docs/porting.md, Divergences).
    /// </summary>
    public static double LinearToSRGB(double linear) =>
        linear <= 0.0031308 ? linear * 12.92 : (Math.Pow(linear, 1.0 / 2.4) + 1.055) + -0.055;

    public static (double R, double G, double B, double A) ToLinearFromSRGB((double R, double G, double B, double A) c) =>
        (SRGBToLinear(c.R), SRGBToLinear(c.G), SRGBToLinear(c.B), c.A);

    public static (double R, double G, double B, double A) ToSRGBFromLinear((double R, double G, double B, double A) c) =>
        (LinearToSRGB(c.R), LinearToSRGB(c.G), LinearToSRGB(c.B), c.A);

    /// <summary>Unpacks <c>r | g&lt;&lt;8 | b&lt;&lt;16 | a&lt;&lt;24</c> to floats in 0..1.</summary>
    public static (double R, double G, double B, double A) FromInteger(uint color) =>
        ((color & 0xFF) / 255.0, ((color >> 8) & 0xFF) / 255.0, ((color >> 16) & 0xFF) / 255.0, (color >> 24) / 255.0);

    /// <summary>Packs floats, each clamped to 0..255 after ×255 and truncated. NaN is refused, as <c>int(nan)</c> is.</summary>
    public static uint ToInteger((double R, double G, double B, double A) c) =>
        Channel(c.R) | Channel(c.G) << 8 | Channel(c.B) << 16 | Channel(c.A) << 24;

    private static uint Channel(double v) =>
        double.IsNaN(v) ? throw new ArgumentException("NaN colour channel") : (uint)Math.Max(Math.Min(v * 255.0, 255.0), 0.0);
}
