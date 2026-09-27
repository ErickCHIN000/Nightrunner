using System.Globalization;
using Nightrunner.Core.Mesh.ClassReader;

namespace Nightrunner.Core.Anim;

/// <summary>
/// A graph parameter word (u64, readers 0x1802df9b0…): <c>type = v &amp; 0xF</c>, <c>mode = (v &gt;&gt; 4) &amp; 3</c>,
/// <c>index = (v &gt;&gt; 6) &amp; 0x1FFFFFFF</c>. Mode 1 is a constant from the root <see cref="ValueTable"/>, mode 2 a
/// graph variable. Bits 35..63 (<see cref="Rest"/>) are all ones in every decoded word; kept raw, meaning (C).
/// </summary>
public readonly record struct ValueRef(ulong Raw)
{
    public const int ModeConstant = 1, ModeVariable = 2;
    public const int TypeFloat = 0, TypeInt = 1, TypeString = 2, TypeVec4 = 3, TypeMtx34 = 4, TypeNone = 0xC;

    public int Type => (int)(Raw & 0xF);
    public int Mode => (int)((Raw >> 4) & 3);
    public int Index => (int)((Raw >> 6) & 0x1FFFFFFF);
    public ulong Rest => Raw >> 35;

    public bool IsConstant => Mode == ModeConstant;
    public bool IsVariable => Mode == ModeVariable;

    public string TypeName => Type switch
    {
        TypeFloat => "f32", TypeInt => "i32", TypeString => "string", TypeVec4 => "vec4", TypeMtx34 => "mtx34",
        TypeNone => "none", _ => $"type{Type}",
    };

    /// <summary>Builds a word with <see cref="Rest"/> all ones, as the shipped words are.</summary>
    public static ValueRef Make(int type, int mode, int index) =>
        new(0xFFFFFFF8_00000000UL | (((ulong)(uint)index & 0x1FFFFFFF) << 6) | ((ulong)(uint)(mode & 3) << 4) |
            (ulong)(uint)(type & 0xF));

    public override string ToString() => $"{TypeName} {(Mode switch { 1 => "const", 2 => "var", _ => $"mode{Mode}" })} #{Index}";
}

public enum GraphValueKind
{
    Constant,
    Variable,

    /// <summary>Mode 0 or 3: no constant or variable behind the word (unset / not decoded).</summary>
    Other,

    /// <summary>A constant this reader refuses to interpret (see <see cref="GraphValue.Why"/>).</summary>
    Unresolved,
}

/// <summary>A resolved <see cref="ValueRef"/>: the constant (float, int or string), or the variable it reads.</summary>
public sealed record GraphValue(ValueRef Ref, GraphValueKind Kind, object? Constant = null, int VariableIndex = -1,
                                string? VariableName = null, uint VariableHash = 0, string? Why = null)
{
    public string? Text => Constant as string;
    public float? Float => Constant is float f ? f : null;

    public override string ToString() => Kind switch
    {
        GraphValueKind.Constant => Constant switch
        {
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            string s => $"\"{s}\"",
            _ => "?",
        },
        GraphValueKind.Variable => VariableName is { } n ? $"var \"{n}\"" : $"var #{VariableIndex} (0x{VariableHash:X8})",
        GraphValueKind.Unresolved => $"unresolved {Ref}: {Why}",
        _ => Ref.ToString(),
    };
}

/// <summary>
/// Class 0x0F: the constant pool a root's <see cref="ValueRef"/>s index (root +0x50).
/// </summary>
/// <remarks>
/// <code>
/// +0x00 ptr entries  {u32 value, i32 extra (−1 in every entry)} × nEntries   value = f32 / i32 bits, or a string index
/// +0x08 ptr strings  char* × nStrings (class 0x00 raw records)
/// +0x10 ptr blocks   48-byte items × nBlocks (by span; transform-like t/q/s vec4s) — not referenced by any decoded
///                    word, kept as an offset only (C)
/// +0x18 ptr          null in every bank          +0x20 u32 nEntries  +0x24 u32 nStrings
/// +0x28 u32 nBlocks  +0x2C u32 (0 in every bank)
/// </code>
/// </remarks>
public sealed class ValueTable
{
    public int Offset { get; }
    public uint[] Values { get; }
    public int[] Extra { get; }
    public string[] Strings { get; }
    public int? BlocksOffset { get; }
    public uint BlockCount { get; }
    public uint Count2C { get; }

    internal ValueTable(Image img, int off)
    {
        Offset = off;
        uint n = img.U32(off + 0x20), ns = img.U32(off + 0x24);
        BlockCount = img.U32(off + 0x28);
        Count2C = img.U32(off + 0x2C);
        Values = new uint[n];
        Extra = new int[n];
        if (n > 0)
        {
            int e = img.Target(off) ?? throw new AnimBankFormatException($"value table 0x{off:X}: {n} entries, null pointer");
            for (int i = 0; i < n; i++)
            {
                Values[i] = img.U32(e + i * 8);
                Extra[i] = (int)img.U32(e + i * 8 + 4);
            }
        }
        Strings = new string[ns];
        if (ns > 0)
        {
            int s = img.Target(off + 0x08) ?? throw new AnimBankFormatException($"value table 0x{off:X}: {ns} strings, null pointer");
            for (int i = 0; i < ns; i++)
                Strings[i] = img.StringAt(s + i * 8) is { } b ? AnimBank.Latin1.GetString(b) : "";
        }
        BlocksOffset = img.Target(off + 0x10);
        if (img.Target(off + 0x18) is not null)
            throw new AnimBankFormatException($"value table 0x{off:X}: +0x18 is set (never seen; layout unknown)");
    }

    /// <summary>Resolves a word against this table and the root's variable list.</summary>
    public GraphValue Resolve(ValueRef r, InterfaceList? vars)
    {
        switch (r.Mode)
        {
            case ValueRef.ModeConstant:
                if (r.Index >= Values.Length)
                    return new(r, GraphValueKind.Unresolved, Why: $"index {r.Index} past {Values.Length} constants");
                uint v = Values[r.Index];
                return r.Type switch
                {
                    ValueRef.TypeFloat => new(r, GraphValueKind.Constant, BitConverter.UInt32BitsToSingle(v)),
                    ValueRef.TypeInt => new(r, GraphValueKind.Constant, (int)v),
                    ValueRef.TypeString => v < Strings.Length
                        ? new(r, GraphValueKind.Constant, Strings[v])
                        : new(r, GraphValueKind.Unresolved, Why: $"string {v} past {Strings.Length}"),
                    _ => new(r, GraphValueKind.Unresolved, Why: $"{r.TypeName} constants are not decoded"),
                };
            case ValueRef.ModeVariable:
                string? name = vars?.Names is { } names && r.Index < names.Length ? names[r.Index] : null;
                uint hash = vars is not null && r.Index < vars.Hashes.Length ? vars.Hashes[r.Index] : 0;
                return new(r, GraphValueKind.Variable, VariableIndex: r.Index, VariableName: name, VariableHash: hash);
            default:
                return new(r, GraphValueKind.Other);
        }
    }
}

/// <summary>
/// One interface list of a root graph: the h41 hash list the game maps names through (class 0x11
/// <c>{u32* hashes, u64 count}</c>) and, where the bank keeps them, the debug names (root +0x1B0…+0x1F0,
/// <c>{string* names, u32 count, u32 capacity}</c>).
/// </summary>
/// <remarks>
/// Hash rule, measured: <c>h41(name)</c>; a debug name shown with a leading <c>"* "</c> hashes without it; a
/// path-qualified internal entry (<c>"Graph / Sub / X :: Y"</c>, with or without the star) stores hash 0. See
/// <see cref="ExpectedHash"/>.
/// </remarks>
public sealed class InterfaceList(string kind, uint[] hashes, string[]? names)
{
    public string Kind { get; } = kind;
    public uint[] Hashes { get; } = hashes;

    /// <summary>Debug names in hash-list order, or null when the bank does not keep them.</summary>
    public string[]? Names { get; } = names;

    public int Count => Hashes.Length;

    /// <summary>The hash the list should hold for a debug name, by the measured rule.</summary>
    public static uint ExpectedHash(string name)
    {
        if (name.Contains(" / ", StringComparison.Ordinal)) return 0;
        return H41(name.StartsWith("* ", StringComparison.Ordinal) ? name[2..] : name);
    }

    /// <summary>
    /// Engine name hash (<c>SimpleSampler::AddBone</c> 0x1802bb630): <c>h = h·41 + c</c> from 0, A–Z lowered, c added as
    /// a signed char, wrapping at u32.
    /// </summary>
    public static uint H41(string s)
    {
        uint h = 0;
        foreach (byte b in AnimBank.Latin1.GetBytes(s))
        {
            int c = b is >= (byte)'A' and <= (byte)'Z' ? b + 32 : (sbyte)b;
            h = unchecked(h * 41 + (uint)c);
        }
        return h;
    }
}
