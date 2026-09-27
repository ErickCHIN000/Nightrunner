using System.Globalization;
using System.Numerics;
using System.Text;

namespace Nightrunner.Core.Cast;

/// <summary>
/// The JSON the glTF port reads and writes, with Python's semantics: a document is a tree of <c>null</c>, <c>bool</c>,
/// <see cref="BigInteger"/> (JSON integers; the writer also takes <c>int</c>/<c>long</c>/<c>uint</c>/<c>ulong</c>),
/// <c>double</c>, <c>string</c>, <see cref="List{T}"/> of object and <see cref="OrderedDictionary{TKey,TValue}"/> (insertion
/// order; a repeated key keeps its first position and takes the last value, as a Python dict does).
/// </summary>
/// <remarks>
/// The writer reproduces <c>json.dumps</c> byte for byte: <c>ensure_ascii</c> escaping (lowercase <c>\uXXXX</c>), float
/// <c>repr</c> (shortest round trip, <c>1e-05</c> / <c>1e+16</c> outside <c>-4 &lt; decpt &lt;= 16</c>), <c>NaN</c> /
/// <c>Infinity</c> literals, and either the compact separators <c>(",", ":")</c> or <c>indent=1</c>. The reader is
/// <c>json.loads</c>: integers and floats stay distinct, <c>NaN</c> / <c>Infinity</c> / <c>-Infinity</c> are accepted,
/// control characters inside strings and a leading BOM are refused.
/// </remarks>
internal static class GltfJson
{
    // ---- writing ----------------------------------------------------------------------------------------------

    /// <summary><c>json.dumps(value, separators=(",", ":"))</c>.</summary>
    public static string Compact(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value, null, 0, "\n");
        return sb.ToString();
    }

    /// <summary><c>json.dumps(value, indent=1)</c>, lines joined by <paramref name="newline"/>.</summary>
    public static string Indented(object? value, string newline = "\n")
    {
        var sb = new StringBuilder();
        Write(sb, value, 1, 0, newline);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? v, int? indent, int level, string nl)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case string s: WriteString(sb, s); break;
            case double d: sb.Append(FloatRepr(d, json: true)); break;
            case float f: sb.Append(FloatRepr(f, json: true)); break;
            case int or long or uint or ulong or byte or ushort or BigInteger:
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            case OrderedDictionary<string, object?> o:
                if (o.Count == 0) { sb.Append("{}"); break; }
                sb.Append('{');
                bool first = true;
                foreach (var (k, x) in o)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, indent, level + 1, nl);
                    WriteString(sb, k);
                    sb.Append(indent is null ? ":" : ": ");
                    Write(sb, x, indent, level + 1, nl);
                }
                NewLine(sb, indent, level, nl);
                sb.Append('}');
                break;
            case System.Collections.IList l:
                if (l.Count == 0) { sb.Append("[]"); break; }
                sb.Append('[');
                for (int i = 0; i < l.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    NewLine(sb, indent, level + 1, nl);
                    Write(sb, l[i], indent, level + 1, nl);
                }
                NewLine(sb, indent, level, nl);
                sb.Append(']');
                break;
            default: throw new GltfException($"cannot write {v.GetType().Name} as JSON");
        }
    }

    private static void NewLine(StringBuilder sb, int? indent, int level, string nl)
    {
        if (indent is null) return;
        sb.Append(nl).Append(' ', indent.Value * level);
    }

    /// <summary><c>json.encoder.py_encode_basestring_ascii</c>.</summary>
    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>
    /// Python <c>float.__repr__</c>; with <paramref name="json"/> the non-finite values are the JSON literals
    /// <c>NaN</c> / <c>Infinity</c> / <c>-Infinity</c> (<c>json.dumps</c>), otherwise <c>nan</c> / <c>inf</c> / <c>-inf</c>.
    /// </summary>
    public static string FloatRepr(double d, bool json)
    {
        if (double.IsNaN(d)) return json ? "NaN" : "nan";
        if (double.IsPositiveInfinity(d)) return json ? "Infinity" : "inf";
        if (double.IsNegativeInfinity(d)) return json ? "-Infinity" : "-inf";
        if (d == 0) return double.IsNegative(d) ? "-0.0" : "0.0";
        // .NET's "R" is the shortest round-trip digit string too; only the layout differs.
        string r = d.ToString("R", CultureInfo.InvariantCulture);
        bool negative = r[0] == '-';
        if (negative) r = r[1..];
        int e = 0, ei = r.IndexOfAny(['E', 'e']);
        if (ei >= 0)
        {
            e = int.Parse(r[(ei + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            r = r[..ei];
        }
        int dot = r.IndexOf('.');
        string whole = dot < 0 ? r : r[..dot], frac = dot < 0 ? "" : r[(dot + 1)..];
        string digits = whole + frac;
        int decpt = whole.Length + e;
        int lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
        digits = digits[lead..].TrimEnd('0');
        decpt -= lead;
        if (digits.Length == 0) digits = "0";
        var sb = new StringBuilder();
        if (negative) sb.Append('-');
        if (decpt > -4 && decpt <= 16)
        {
            if (decpt <= 0) sb.Append("0.").Append('0', -decpt).Append(digits);
            else if (decpt >= digits.Length) sb.Append(digits).Append('0', decpt - digits.Length).Append(".0");
            else sb.Append(digits, 0, decpt).Append('.').Append(digits, decpt, digits.Length - decpt);
        }
        else
        {
            int x = decpt - 1;
            sb.Append(digits[0]);
            if (digits.Length > 1) sb.Append('.').Append(digits, 1, digits.Length - 1);
            sb.Append('e').Append(x < 0 ? '-' : '+').Append(Math.Abs(x).ToString("00", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>Python <c>str(value)</c> of a parsed JSON value (a string is itself, anything else its <c>repr</c>).</summary>
    public static string PyStr(object? v) => v as string ?? PyRepr(v);

    /// <summary>Python <c>repr</c> of a parsed JSON value.</summary>
    public static string PyRepr(object? v)
    {
        switch (v)
        {
            case null: return "None";
            case bool b: return b ? "True" : "False";
            case string s: return StrRepr(s);
            case double d: return FloatRepr(d, json: false);
            case BigInteger i: return i.ToString(CultureInfo.InvariantCulture);
            case OrderedDictionary<string, object?> o:
                return "{" + string.Join(", ", o.Select(p => StrRepr(p.Key) + ": " + PyRepr(p.Value))) + "}";
            case List<object?> l: return "[" + string.Join(", ", l.Select(PyRepr)) + "]";
            default: return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        }
    }

    /// <summary>Python <c>str.__repr__</c>: single quotes unless the text holds a <c>'</c> and no <c>"</c>.</summary>
    private static string StrRepr(string s)
    {
        char q = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder().Append(q);
        for (int i = 0; i < s.Length; i++)
        {
            int cp = s[i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) cp = char.ConvertToUtf32(s[i], s[++i]);
            if (cp == q || cp == '\\') sb.Append('\\').Append((char)cp);
            else if (cp == '\t') sb.Append("\\t");
            else if (cp == '\n') sb.Append("\\n");
            else if (cp == '\r') sb.Append("\\r");
            else if (cp < 0x20 || cp == 0x7F) sb.Append("\\x").Append(cp.ToString("x2", CultureInfo.InvariantCulture));
            else if (cp < 0x7F) sb.Append((char)cp);
            else if (Printable(cp)) sb.Append(char.ConvertFromUtf32(cp is >= 0xD800 and <= 0xDFFF ? 0xFFFD : cp));
            else if (cp <= 0xFF) sb.Append("\\x").Append(cp.ToString("x2", CultureInfo.InvariantCulture));
            else if (cp <= 0xFFFF) sb.Append("\\u").Append(cp.ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append("\\U").Append(cp.ToString("x8", CultureInfo.InvariantCulture));
        }
        return sb.Append(q).ToString();
    }

    // str.isprintable: not Cc Cf Cs Co Cn Zl Zp Zs (the ASCII space is handled by the caller).
    private static bool Printable(int cp)
    {
        if (cp is >= 0xD800 and <= 0xDFFF) return false;
        var cat = CharUnicodeInfo.GetUnicodeCategory(cp);
        return cat is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
            or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator);
    }

    /// <summary>Python truthiness of a parsed JSON value.</summary>
    public static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        BigInteger i => !i.IsZero,
        double d => d != 0,
        string s => s.Length > 0,
        System.Collections.ICollection c => c.Count > 0,
        _ => true,
    };

    // ---- reading ----------------------------------------------------------------------------------------------

    /// <summary><c>json.loads(text)</c>.</summary>
    public static object? Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') throw new GltfException("JSON: unexpected UTF-8 BOM");
        var p = new Parser(text);
        p.Ws();
        var v = p.Value(0);
        p.Ws();
        if (p.Pos != text.Length) throw p.Error("extra data");
        return v;
    }

    private sealed class Parser(string s)
    {
        private const int MaxDepth = 1000;
        public int Pos;

        public GltfException Error(string what) => new($"JSON: {what} at offset {Pos}");

        public void Ws()
        {
            while (Pos < s.Length && s[Pos] is ' ' or '\t' or '\n' or '\r') Pos++;
        }

        public object? Value(int depth)
        {
            if (depth > MaxDepth) throw Error("nesting too deep");
            if (Pos >= s.Length) throw Error("expecting value");
            char c = s[Pos];
            switch (c)
            {
                case '{': return Object(depth);
                case '[': return Array(depth);
                case '"': return String();
            }
            if (Word("null")) return null;
            if (Word("true")) return true;
            if (Word("false")) return false;
            if (Word("NaN")) return double.NaN;
            if (Word("Infinity")) return double.PositiveInfinity;
            if (Word("-Infinity")) return double.NegativeInfinity;
            return Number();
        }

        private bool Word(string w)
        {
            if (string.CompareOrdinal(s, Pos, w, 0, w.Length) != 0) return false;
            Pos += w.Length;
            return true;
        }

        private object Number()
        {
            int start = Pos;
            if (Pos < s.Length && s[Pos] == '-') Pos++;
            if (Pos >= s.Length || !char.IsAsciiDigit(s[Pos])) { Pos = start; throw Error("expecting value"); }
            if (s[Pos] == '0') Pos++;
            else while (Pos < s.Length && char.IsAsciiDigit(s[Pos])) Pos++;
            bool isFloat = false;
            if (Pos + 1 < s.Length && s[Pos] == '.' && char.IsAsciiDigit(s[Pos + 1]))
            {
                isFloat = true;
                Pos++;
                while (Pos < s.Length && char.IsAsciiDigit(s[Pos])) Pos++;
            }
            if (Pos < s.Length && s[Pos] is 'e' or 'E')
            {
                int e = Pos + 1;
                if (e < s.Length && s[e] is '+' or '-') e++;
                if (e < s.Length && char.IsAsciiDigit(s[e]))
                {
                    isFloat = true;
                    Pos = e;
                    while (Pos < s.Length && char.IsAsciiDigit(s[Pos])) Pos++;
                }
            }
            string t = s[start..Pos];
            return isFloat ? double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture)
                           : BigInteger.Parse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }

        private string String()
        {
            Pos++;
            var sb = new StringBuilder();
            while (true)
            {
                if (Pos >= s.Length) throw Error("unterminated string");
                char c = s[Pos++];
                if (c == '"') return sb.ToString();
                if (c < 0x20) { Pos--; throw Error("invalid control character"); }
                if (c != '\\') { sb.Append(c); continue; }
                if (Pos >= s.Length) throw Error("unterminated string");
                char e = s[Pos++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (Pos + 4 > s.Length || !int.TryParse(s.AsSpan(Pos, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int u))
                            throw Error("invalid \\uXXXX escape");
                        Pos += 4;
                        sb.Append((char)u);
                        break;
                    default: Pos--; throw Error("invalid \\escape");
                }
            }
        }

        private OrderedDictionary<string, object?> Object(int depth)
        {
            Pos++;
            var o = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
            Ws();
            if (Pos < s.Length && s[Pos] == '}') { Pos++; return o; }
            while (true)
            {
                Ws();
                if (Pos >= s.Length || s[Pos] != '"') throw Error("expecting property name enclosed in double quotes");
                string k = String();
                Ws();
                if (Pos >= s.Length || s[Pos] != ':') throw Error("expecting ':' delimiter");
                Pos++;
                Ws();
                o[k] = Value(depth + 1);
                Ws();
                if (Pos < s.Length && s[Pos] == ',') { Pos++; continue; }
                if (Pos < s.Length && s[Pos] == '}') { Pos++; return o; }
                throw Error("expecting ',' delimiter");
            }
        }

        private List<object?> Array(int depth)
        {
            Pos++;
            var l = new List<object?>();
            Ws();
            if (Pos < s.Length && s[Pos] == ']') { Pos++; return l; }
            while (true)
            {
                Ws();
                l.Add(Value(depth + 1));
                Ws();
                if (Pos < s.Length && s[Pos] == ',') { Pos++; continue; }
                if (Pos < s.Length && s[Pos] == ']') { Pos++; return l; }
                throw Error("expecting ',' delimiter");
            }
        }
    }
}
