using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Nightrunner.Core.Prefab;

/// <summary>
/// The YAML block subset the shipped text prefabs use (Dying Light 2: 303 files; Dying Light: The Beast:
/// <c>prefabs/game.prefab</c>), parsed into <see cref="PrefabTextNode"/> documents: <c>---</c> / <c>...</c> document
/// markers, <c>#</c> comment lines, block maps and sequences (compact <c>- key: value</c> items, a sequence at its
/// key's indent), plain scalars (continued on deeper lines, folded with a space), single- and double-quoted scalars
/// (possibly over several lines), <c>{}</c>, <c>[]</c> and one-line flow sequences of scalars. Anything else (anchors,
/// tags, block scalars, nested flow collections) is refused by name rather than guessed.
/// </summary>
public static partial class PrefabYaml
{
    private readonly record struct Line(int Indent, string Text, int Number);

    public static List<PrefabTextNode> ParseDocuments(ReadOnlySpan<byte> utf8)
    {
        if (utf8.StartsWith("﻿"u8)) utf8 = utf8[3..];
        var text = Encoding.UTF8.GetString(utf8);
        var docs = new List<List<Line>>();
        List<Line>? cur = null;
        int n = 0;
        foreach (var raw in text.Split('\n'))
        {
            n++;
            var line = raw.TrimEnd('\r', ' ');
            if (line.Contains('\t')) throw new PrefabFormatException($"YAML line {n}: tab character");
            var t = line.TrimStart(' ');
            if (t.Length == 0 || t[0] == '#') continue;
            if (t.StartsWith("---", StringComparison.Ordinal) && line.Length == t.Length)
            {
                if (t.Length > 3) throw new PrefabFormatException($"YAML line {n}: content after '---'");
                docs.Add(cur = []);
                continue;
            }
            if (t == "..." && line.Length == 3) { cur = null; continue; }
            if (cur is null) docs.Add(cur = []);
            cur.Add(new Line(line.Length - t.Length, t, n));
        }
        var result = new List<PrefabTextNode>(docs.Count);
        foreach (var d in docs)
        {
            int i = 0;
            result.Add(d.Count == 0 ? PrefabTextNode.Null : Node(d, ref i, d[0].Indent));
            if (i != d.Count) throw new PrefabFormatException($"YAML line {d[i].Number}: unexpected indentation");
        }
        return result;
    }

    private static bool IsSeqItem(string t) => t == "-" || t.StartsWith("- ", StringComparison.Ordinal);

    /// <summary>Index of the ':' ending a map key on this line, or −1 when the line is not <c>key:</c> / <c>key: value</c>.</summary>
    private static int KeyColon(string t)
    {
        if (t.Length == 0 || t[0] is '\'' or '"' or '[' or '{' || IsSeqItem(t)) return -1;
        for (int k = 0; k < t.Length; k++)
            if (t[k] == ':' && (k + 1 == t.Length || t[k + 1] == ' ')) return k;
        return -1;
    }

    private static PrefabTextNode Node(List<Line> lines, ref int i, int indent)
    {
        var l = lines[i];
        if (IsSeqItem(l.Text)) return Sequence(lines, ref i, l.Indent);
        if (KeyColon(l.Text) >= 0) return Map(lines, ref i, l.Indent);
        i++;
        return Scalar(l.Text, lines, ref i, indent - 1, l.Number);
    }

    private static PrefabTextNode Map(List<Line> lines, ref int i, int indent)
    {
        var members = new List<KeyValuePair<string, PrefabTextNode>>();
        while (i < lines.Count && lines[i].Indent == indent && !IsSeqItem(lines[i].Text))
        {
            var l = lines[i];
            int c = KeyColon(l.Text);
            if (c < 0) throw new PrefabFormatException($"YAML line {l.Number}: expected 'key:' in a map");
            string key = l.Text[..c];
            string rest = c + 1 < l.Text.Length ? l.Text[(c + 2)..].TrimStart(' ') : "";
            i++;
            members.Add(new(key, Value(rest, lines, ref i, indent, l.Number)));
        }
        if (i < lines.Count && lines[i].Indent > indent) throw new PrefabFormatException($"YAML line {lines[i].Number}: unexpected indentation");
        return PrefabTextNode.Object(members);
    }

    /// <summary>The value after <c>key:</c> or <c>- </c> at <paramref name="indent"/>: inline, or the deeper block below.</summary>
    private static PrefabTextNode Value(string rest, List<Line> lines, ref int i, int indent, int number)
    {
        if (rest.Length > 0) return Scalar(rest, lines, ref i, indent, number);
        if (i < lines.Count && lines[i].Indent > indent) return Node(lines, ref i, lines[i].Indent);
        // a block sequence may sit at its key's own indent
        if (i < lines.Count && lines[i].Indent == indent && IsSeqItem(lines[i].Text)) return Sequence(lines, ref i, indent);
        return PrefabTextNode.Null;
    }

    private static PrefabTextNode Sequence(List<Line> lines, ref int i, int indent)
    {
        var items = new List<PrefabTextNode>();
        while (i < lines.Count && lines[i].Indent == indent && IsSeqItem(lines[i].Text))
        {
            var l = lines[i];
            string rest = l.Text.Length > 1 ? l.Text[2..].TrimStart(' ') : "";
            int inner = indent + (l.Text.Length - rest.Length);
            if (rest.Length > 0 && (KeyColon(rest) >= 0 || IsSeqItem(rest)))
            {
                // compact item: its content starts at column indent + 2; continue it as if on its own line
                lines[i] = new Line(inner, rest, l.Number);
                items.Add(Node(lines, ref i, inner));
                continue;
            }
            i++;
            items.Add(Value(rest, lines, ref i, indent, l.Number));
        }
        return PrefabTextNode.Array(items);
    }

    /// <summary>
    /// A scalar starting with <paramref name="first"/>; lines deeper than <paramref name="owner"/> continue it (folded
    /// with a space), as do the lines of an unclosed quoted scalar.
    /// </summary>
    private static PrefabTextNode Scalar(string first, List<Line> lines, ref int i, int owner, int number)
    {
        if (first[0] is '&' or '*' or '!' or '|' or '>')
            throw new PrefabFormatException($"YAML line {number}: '{first[0]}' (anchor, alias, tag or block scalar) is not supported");
        if (first[0] is '\'' or '"')
        {
            char q = first[0];
            var sb = new StringBuilder(first);
            while (!Closed(sb.ToString(), q))
            {
                if (i >= lines.Count) throw new PrefabFormatException($"YAML line {number}: unterminated {q}-quoted scalar");
                sb.Append(' ').Append(lines[i++].Text);
            }
            string s = sb.ToString();
            int end = CloseAt(s, q);
            if (s[(end + 1)..].Trim().Length > 0) throw new PrefabFormatException($"YAML line {number}: text after a quoted scalar");
            return PrefabTextNode.String(q == '\'' ? s[1..end].Replace("''", "'") : Unescape(s[1..end], number));
        }
        var plain = new StringBuilder(first);
        // a scalar value cannot own a nested block, so every deeper line continues it (even one starting "- ")
        while (i < lines.Count && lines[i].Indent > owner)
            plain.Append(' ').Append(lines[i++].Text);
        string v = plain.ToString();
        if (v == "{}") return PrefabTextNode.Object([]);
        if (v == "[]") return PrefabTextNode.Array([]);
        if (v[0] == '{') throw new PrefabFormatException($"YAML line {number}: a flow map with content is not supported");
        if (v[0] == '[')
        {
            if (v[^1] != ']' || v.IndexOfAny(['[', '{', '\'', '"'], 1) >= 0)
                throw new PrefabFormatException($"YAML line {number}: only one-line flow sequences of plain scalars are supported");
            return PrefabTextNode.Array(v[1..^1].Split(',').Select(x => Plain(x.Trim())).ToList());
        }
        return Plain(v);
    }

    private static bool Closed(string s, char q) => CloseAt(s, q) > 0;

    private static int CloseAt(string s, char q)
    {
        for (int k = 1; k < s.Length; k++)
        {
            if (q == '"' && s[k] == '\\') { k++; continue; }
            if (s[k] != q) continue;
            if (q == '\'' && k + 1 < s.Length && s[k + 1] == '\'') { k++; continue; }
            return k;
        }
        return -1;
    }

    private static string Unescape(string s, int number)
    {
        try { return Regex.Unescape(s); }
        catch (ArgumentException) { throw new PrefabFormatException($"YAML line {number}: bad escape in a double-quoted scalar"); }
    }

    private static PrefabTextNode Plain(string v) => v switch
    {
        "" or "null" or "~" or "Null" or "NULL" => PrefabTextNode.Null,
        "true" or "True" or "TRUE" => PrefabTextNode.Bool(true),
        "false" or "False" or "FALSE" => PrefabTextNode.Bool(false),
        _ when NumberRx().IsMatch(v) => PrefabTextNode.Number(v),
        _ => PrefabTextNode.String(v),
    };

    [GeneratedRegex(@"^[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRx();

    public static bool TryNumber(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
