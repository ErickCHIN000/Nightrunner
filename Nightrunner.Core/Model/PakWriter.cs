using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Nightrunner.Core.Model;

/// <summary>
/// Writes a mod PAK (port of <c>pak/model_json.py::write_models_pak</c>): a deflated ZIP with each <c>.model</c>
/// document at exactly the member path given, serialised as the prototype does (<c>json.dumps(indent=2,
/// ensure_ascii=True)</c>, see <see cref="PyJson"/>) so member bytes match its build; plain UTF-8 text members;
/// member paths validated (relative, no <c>..</c>, no drive, <c>[A-Za-z0-9_\-./ ]</c> only); written to
/// <c>.partial</c>, re-read and checked, then moved into place.
/// </summary>
public static partial class PakWriter
{
    [GeneratedRegex(@"^[A-Za-z0-9_\-./ ]+$")]
    private static partial Regex SafeMember();

    public sealed record Result(string Path, IReadOnlyList<string> Members, long Bytes);

    public static Result Write(string outPath, IReadOnlyDictionary<string, JsonNode> documents,
                               IReadOnlyDictionary<string, string>? texts = null, bool overwrite = false)
    {
        if (File.Exists(outPath) && !overwrite) throw new ModelFormatException($"{outPath} exists");
        var entries = new List<(string Name, byte[] Data)>();
        foreach (var (name, doc) in documents) entries.Add((Check(name), Encoding.UTF8.GetBytes(PyJson.Dumps(doc))));
        foreach (var (name, text) in texts ?? new Dictionary<string, string>()) entries.Add((Check(name), Encoding.UTF8.GetBytes(text)));
        if (entries.Count == 0) throw new ModelFormatException("nothing to write");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _) in entries)
            if (!seen.Add(name)) throw new ModelFormatException($"duplicate member path '{name}'");

        string tmp = outPath + ".partial";
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath))!);
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            foreach (var (name, data) in entries)
            {
                using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                s.Write(data);
            }
        using (var zip = ZipFile.OpenRead(tmp))
            foreach (var (name, data) in entries)
            {
                using var s = (zip.GetEntry(name) ?? throw new ModelFormatException($"written PAK lacks '{name}'")).Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                if (!ms.ToArray().AsSpan().SequenceEqual(data))
                {
                    File.Delete(tmp);
                    throw new ModelFormatException($"written PAK failed verification at member '{name}'");
                }
            }
        File.Move(tmp, outPath, overwrite: true);
        return new Result(outPath, entries.Select(e => e.Name).ToList(), new FileInfo(outPath).Length);
    }

    private static string Check(string name)
    {
        string n = name.Replace('\\', '/');
        if (n.Length == 0 || n.StartsWith('/') || n.Split('/').Contains("..") || n.Contains(':') || !SafeMember().IsMatch(n))
            throw new ModelFormatException($"invalid PAK member path '{name}'");
        return n;
    }
}

/// <summary>
/// Python's <c>json.dumps(obj, indent=2, ensure_ascii=True)</c> for a parsed JSON tree: <c>", "</c>/<c>": "</c>
/// separators with newline indentation, non-ASCII as <c>\uXXXX</c> (surrogate pairs), integers as written, floats
/// in <c>repr</c> form (<c>1.0</c>, <c>0.1</c>, <c>1e-05</c>, <c>1e+16</c>), empty containers as <c>[]</c>/<c>{}</c>.
/// </summary>
public static class PyJson
{
    public static string Dumps(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(sb, node, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonNode? node, int depth)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject o:
                if (o.Count == 0) { sb.Append("{}"); break; }
                sb.Append('{');
                bool first = true;
                foreach (var (k, v) in o)
                {
                    sb.Append(first ? "\n" : ",\n").Append(' ', (depth + 1) * 2);
                    first = false;
                    Str(sb, k);
                    sb.Append(": ");
                    Write(sb, v, depth + 1);
                }
                sb.Append('\n').Append(' ', depth * 2).Append('}');
                break;
            case JsonArray a:
                if (a.Count == 0) { sb.Append("[]"); break; }
                sb.Append('[');
                for (int i = 0; i < a.Count; i++)
                {
                    sb.Append(i == 0 ? "\n" : ",\n").Append(' ', (depth + 1) * 2);
                    Write(sb, a[i], depth + 1);
                }
                sb.Append('\n').Append(' ', depth * 2).Append(']');
                break;
            case JsonValue v:
                Value(sb, v);
                break;
        }
    }

    private static void Value(StringBuilder sb, JsonValue v)
    {
        if (v.TryGetValue(out JsonElement e))
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String: Str(sb, e.GetString()!); return;
                case JsonValueKind.True: sb.Append("true"); return;
                case JsonValueKind.False: sb.Append("false"); return;
                case JsonValueKind.Null: sb.Append("null"); return;
                case JsonValueKind.Number: Number(sb, e.GetRawText()); return;
            }
        }
        if (v.TryGetValue(out string? s)) { Str(sb, s!); return; }
        if (v.TryGetValue(out bool b)) { sb.Append(b ? "true" : "false"); return; }
        if (v.TryGetValue(out long l)) { sb.Append(l.ToString(CultureInfo.InvariantCulture)); return; }
        if (v.TryGetValue(out int i)) { sb.Append(i.ToString(CultureInfo.InvariantCulture)); return; }
        if (v.TryGetValue(out double d)) { sb.Append(Float(d)); return; }
        if (v.TryGetValue(out float f)) { sb.Append(Float(f)); return; }
        Number(sb, v.ToJsonString());
    }

    /// <summary>A JSON number token as Python re-prints it: integers unchanged, anything with . e E through float repr.</summary>
    private static void Number(StringBuilder sb, string raw)
    {
        if (raw.IndexOfAny(['.', 'e', 'E']) < 0) { sb.Append(raw); return; }
        sb.Append(Float(double.Parse(raw, CultureInfo.InvariantCulture)));
    }

    /// <summary>Python float repr: shortest round-trip digits; exponent form below 1e-4 or from 1e16.</summary>
    public static string Float(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) throw new ModelFormatException("NaN/Infinity is not valid JSON (allow_nan=False)");
        if (d == 0) return BitConverter.DoubleToInt64Bits(d) < 0 ? "-0.0" : "0.0";
        string r = d.ToString("R", CultureInfo.InvariantCulture);      // shortest round-trip, "E" notation when .NET picks it
        // digits and decimal exponent of the shortest form
        string mant = r, sign = "";
        if (mant.StartsWith('-')) { sign = "-"; mant = mant[1..]; }
        int exp = 0;
        int ei = mant.IndexOfAny(['E', 'e']);
        if (ei >= 0) { exp = int.Parse(mant[(ei + 1)..], CultureInfo.InvariantCulture); mant = mant[..ei]; }
        int dot = mant.IndexOf('.');
        string digits = dot < 0 ? mant : mant.Remove(dot, 1);
        int pointPos = (dot < 0 ? mant.Length : dot) + exp;              // decimal point position within digits
        int lead = digits.Length - digits.TrimStart('0').Length;
        digits = digits.TrimStart('0');
        pointPos -= lead;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) digits = "0";
        int decExp = pointPos - 1;                                        // scientific exponent
        if (decExp < -4 || decExp >= 16)
        {
            string m = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
            return $"{sign}{m}e{(decExp < 0 ? "-" : "+")}{Math.Abs(decExp):00}";
        }
        if (pointPos <= 0) return $"{sign}0.{new string('0', -pointPos)}{digits}";
        if (pointPos >= digits.Length) return $"{sign}{digits}{new string('0', pointPos - digits.Length)}.0";
        return $"{sign}{digits[..pointPos]}.{digits[pointPos..]}";
    }

    private static void Str(StringBuilder sb, string s)
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
                    if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
