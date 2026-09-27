using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Nightrunner.Core.Prefab;

public enum PrefabTextKind { Null, Bool, Number, String, Array, Object }

/// <summary>
/// A node of a text prefab's DOM, the one tree the three on-disk forms parse into: JSON, MessagePack (<c>MsgP</c> +
/// payload) and the YAML block subset the shipped files use. Objects keep their key order and <b>duplicate keys</b>
/// (shipped components repeat <c>EmbeddedObject</c>, <c>Bundle</c> and <c>Mesh</c>); numbers keep their source text.
/// </summary>
public sealed class PrefabTextNode
{
    public PrefabTextKind Kind { get; }
    public string? Text { get; }                                          // string value, or a number's / bool's source text
    public List<PrefabTextNode>? Items { get; }                           // array
    public List<KeyValuePair<string, PrefabTextNode>>? Members { get; }   // object, in order, duplicates kept

    private PrefabTextNode(PrefabTextKind kind, string? text = null, List<PrefabTextNode>? items = null,
                           List<KeyValuePair<string, PrefabTextNode>>? members = null)
    {
        Kind = kind;
        Text = text;
        Items = items;
        Members = members;
    }

    public static readonly PrefabTextNode Null = new(PrefabTextKind.Null);
    public static PrefabTextNode Bool(bool b) => new(PrefabTextKind.Bool, b ? "true" : "false");
    public static PrefabTextNode Number(string text) => new(PrefabTextKind.Number, text);
    public static PrefabTextNode String(string text) => new(PrefabTextKind.String, text);
    public static PrefabTextNode Array(List<PrefabTextNode> items) => new(PrefabTextKind.Array, items: items);
    public static PrefabTextNode Object(List<KeyValuePair<string, PrefabTextNode>> members) => new(PrefabTextKind.Object, members: members);

    /// <summary>The first member of that key, or null.</summary>
    public PrefabTextNode? this[string key]
    {
        get
        {
            if (Members is null) return null;
            foreach (var m in Members) if (m.Key == key) return m.Value;
            return null;
        }
    }

    /// <summary>A scalar as text (strings, numbers and bools); null for containers and null.</summary>
    public string? Scalar => Kind is PrefabTextKind.String or PrefabTextKind.Number or PrefabTextKind.Bool ? Text : null;

    public bool TryDouble(out double v)
    {
        v = 0;
        return Scalar is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    /// <summary>Compact JSON of this node (duplicate keys written as they are): how unknown parts are kept and shown.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        Write(sb);
        return sb.ToString();
    }

    private void Write(StringBuilder sb)
    {
        switch (Kind)
        {
            case PrefabTextKind.Null: sb.Append("null"); break;
            case PrefabTextKind.Bool or PrefabTextKind.Number: sb.Append(Text); break;
            case PrefabTextKind.String: sb.Append(JsonSerializer.Serialize(Text)); break;
            case PrefabTextKind.Array:
                sb.Append('[');
                for (int i = 0; i < Items!.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    Items[i].Write(sb);
                }
                sb.Append(']');
                break;
            default:
                sb.Append('{');
                for (int i = 0; i < Members!.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(JsonSerializer.Serialize(Members[i].Key)).Append(": ");
                    Members[i].Value.Write(sb);
                }
                sb.Append('}');
                break;
        }
    }

    // ---- JSON ---------------------------------------------------------------------------------------------------

    /// <summary>Parses JSON (a UTF-8 BOM is skipped); duplicate keys are kept.</summary>
    public static PrefabTextNode ParseJson(ReadOnlySpan<byte> utf8)
    {
        if (utf8.StartsWith("﻿"u8)) utf8 = utf8[3..];
        var r = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 256 });
        try
        {
            if (!r.Read()) throw new PrefabFormatException("empty JSON");
            var node = ReadJson(ref r);
            if (r.Read()) throw new PrefabFormatException($"JSON: data after the root value at byte {r.TokenStartIndex}");
            return node;
        }
        catch (JsonException e) { throw new PrefabFormatException($"JSON: {e.Message}"); }
        catch (InvalidOperationException e) when (e.InnerException is DecoderFallbackException)
        {
            throw new PrefabFormatException($"JSON: a string is not valid UTF-8 ({e.InnerException.Message})");
        }
    }

    private static PrefabTextNode ReadJson(ref Utf8JsonReader r)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.StartObject:
            {
                var members = new List<KeyValuePair<string, PrefabTextNode>>();
                while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                {
                    string key = r.GetString()!;
                    r.Read();
                    members.Add(new(key, ReadJson(ref r)));
                }
                return Object(members);
            }
            case JsonTokenType.StartArray:
            {
                var items = new List<PrefabTextNode>();
                while (r.Read() && r.TokenType != JsonTokenType.EndArray) items.Add(ReadJson(ref r));
                return Array(items);
            }
            case JsonTokenType.String: return String(r.GetString()!);
            case JsonTokenType.Number: return Number(Encoding.UTF8.GetString(r.HasValueSequence ? System.Buffers.BuffersExtensions.ToArray(r.ValueSequence) : r.ValueSpan));
            case JsonTokenType.True: return Bool(true);
            case JsonTokenType.False: return Bool(false);
            case JsonTokenType.Null: return Null;
            default: throw new PrefabFormatException($"JSON: unexpected {r.TokenType}");
        }
    }

    // ---- MessagePack ------------------------------------------------------------------------------------------

    /// <summary>
    /// Parses MessagePack (the payload after the 4-byte <c>MsgP</c> magic, as <c>dom::CreateReader</c> does); trailing
    /// NUL bytes after the root value are allowed.
    /// Types outside the ones a DOM needs (bin, ext) are refused by name.
    /// </summary>
    public static PrefabTextNode ParseMessagePack(ReadOnlySpan<byte> data)
    {
        int pos = 0;
        var node = ReadPack(data, ref pos, 0);
        // every shipped file ends with one NUL after the root value (a terminator for in-situ reading); anything else is refused
        if (data[pos..].IndexOfAnyExcept((byte)0) >= 0) throw new PrefabFormatException($"MessagePack: {data.Length - pos} non-NUL bytes after the root value");
        return node;
    }

    private static PrefabTextNode ReadPack(ReadOnlySpan<byte> d, ref int p, int depth)
    {
        if (depth > 256) throw new PrefabFormatException("MessagePack: nesting deeper than 256");
        byte b = Take(d, ref p, 1)[0];
        switch (b)
        {
            case <= 0x7F: return Number(b.ToString(CultureInfo.InvariantCulture));
            case >= 0xE0: return Number(((sbyte)b).ToString(CultureInfo.InvariantCulture));
            case >= 0x80 and <= 0x8F: return PackMap(d, ref p, b & 0x0F, depth);
            case >= 0x90 and <= 0x9F: return PackArray(d, ref p, b & 0x0F, depth);
            case >= 0xA0 and <= 0xBF: return String(Encoding.UTF8.GetString(Take(d, ref p, b & 0x1F)));
            case 0xC0: return Null;
            case 0xC2: return Bool(false);
            case 0xC3: return Bool(true);
            case 0xCA: return Number(BinaryPrimitives.ReadSingleBigEndian(Take(d, ref p, 4)).ToString("R", CultureInfo.InvariantCulture));
            case 0xCB: return Number(BinaryPrimitives.ReadDoubleBigEndian(Take(d, ref p, 8)).ToString("R", CultureInfo.InvariantCulture));
            case 0xCC: return Number(Take(d, ref p, 1)[0].ToString(CultureInfo.InvariantCulture));
            case 0xCD: return Number(BinaryPrimitives.ReadUInt16BigEndian(Take(d, ref p, 2)).ToString(CultureInfo.InvariantCulture));
            case 0xCE: return Number(BinaryPrimitives.ReadUInt32BigEndian(Take(d, ref p, 4)).ToString(CultureInfo.InvariantCulture));
            case 0xCF: return Number(BinaryPrimitives.ReadUInt64BigEndian(Take(d, ref p, 8)).ToString(CultureInfo.InvariantCulture));
            case 0xD0: return Number(((sbyte)Take(d, ref p, 1)[0]).ToString(CultureInfo.InvariantCulture));
            case 0xD1: return Number(BinaryPrimitives.ReadInt16BigEndian(Take(d, ref p, 2)).ToString(CultureInfo.InvariantCulture));
            case 0xD2: return Number(BinaryPrimitives.ReadInt32BigEndian(Take(d, ref p, 4)).ToString(CultureInfo.InvariantCulture));
            case 0xD3: return Number(BinaryPrimitives.ReadInt64BigEndian(Take(d, ref p, 8)).ToString(CultureInfo.InvariantCulture));
            case 0xD9: return String(Encoding.UTF8.GetString(Take(d, ref p, Take(d, ref p, 1)[0])));
            case 0xDA: return String(Encoding.UTF8.GetString(Take(d, ref p, BinaryPrimitives.ReadUInt16BigEndian(Take(d, ref p, 2)))));
            case 0xDB: return String(Encoding.UTF8.GetString(Take(d, ref p, Length(BinaryPrimitives.ReadUInt32BigEndian(Take(d, ref p, 4))))));
            case 0xDC: return PackArray(d, ref p, BinaryPrimitives.ReadUInt16BigEndian(Take(d, ref p, 2)), depth);
            case 0xDD: return PackArray(d, ref p, Length(BinaryPrimitives.ReadUInt32BigEndian(Take(d, ref p, 4))), depth);
            case 0xDE: return PackMap(d, ref p, BinaryPrimitives.ReadUInt16BigEndian(Take(d, ref p, 2)), depth);
            case 0xDF: return PackMap(d, ref p, Length(BinaryPrimitives.ReadUInt32BigEndian(Take(d, ref p, 4))), depth);
            default: throw new PrefabFormatException($"MessagePack: type byte 0x{b:X2} at {p - 1} (bin/ext) is not supported");
        }
    }

    private static int Length(uint n) => n <= int.MaxValue ? (int)n : throw new PrefabFormatException($"MessagePack: length {n}");

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> d, ref int p, int n)
    {
        if (n < 0 || p + n > d.Length) throw new PrefabFormatException($"MessagePack: truncated at {p} (needs {n} bytes)");
        var s = d.Slice(p, n);
        p += n;
        return s;
    }

    private static PrefabTextNode PackArray(ReadOnlySpan<byte> d, ref int p, int n, int depth)
    {
        var items = new List<PrefabTextNode>(Math.Min(n, 4096));
        for (int i = 0; i < n; i++) items.Add(ReadPack(d, ref p, depth + 1));
        return Array(items);
    }

    private static PrefabTextNode PackMap(ReadOnlySpan<byte> d, ref int p, int n, int depth)
    {
        var members = new List<KeyValuePair<string, PrefabTextNode>>(Math.Min(n, 4096));
        for (int i = 0; i < n; i++)
        {
            var k = ReadPack(d, ref p, depth + 1);
            if (k.Kind != PrefabTextKind.String) throw new PrefabFormatException($"MessagePack: map key of kind {k.Kind} at {p}");
            members.Add(new(k.Text!, ReadPack(d, ref p, depth + 1)));
        }
        return Object(members);
    }
}
