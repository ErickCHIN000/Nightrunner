using System.Collections;
using System.Globalization;
using System.Text;
using Nightrunner.Core.Mesh.ClassReader;

namespace Nightrunner.Core.Mesh;

/// <summary>Where a mesh came from (the sidecar's <c>source</c>): pack path, logical index, resource name.</summary>
public sealed record MeshSidecarSource(string Pack, int Index, string Name);

/// <summary>
/// <c>&lt;stem&gt;.mesh.json</c> — everything about a mesh resource that the Cast cannot carry. Port of
/// <c>mesh/sidecar.py</c> (schema <c>nightrunner.mesh/1</c>) and of the formatting of <c>util/jsonio.py::dump_json</c>
/// (indent 2, <c>ensure_ascii=False</c>, Python float repr, trailing newline), so the file is byte-identical to the
/// prototype's.
/// </summary>
/// <remarks>
/// Byte blobs are base64 (<c>*_b64</c>), small opaque words hex, floats plain numbers. Positions/UVs are not here (they
/// are in the Cast); the raw vertex windows are, so an unedited vertex re-encodes bit-exactly and the raw holes survive a
/// round trip through tools that drop custom properties.
/// <para/>
/// The document is an ordered tree of <see cref="OrderedDictionary{TKey,TValue}"/> (string → object?), lists, strings,
/// numbers, booleans and nulls, in the prototype's key order.
/// <para/>
/// Difference: the model's name is a string here (the rpack reader decodes it with U+FFFD for invalid UTF-8), so
/// <c>name_hex</c> is the UTF-8 of that string; the prototype keeps the raw bytes through <c>surrogateescape</c>. Every
/// shipped name is valid UTF-8, where the two agree.
/// </remarks>
public static class MeshSidecar
{
    public const string Schema = "nightrunner.mesh/1";
    public const string Tool = "nightrunner 0.1.0";

    /// <summary><c>&lt;dir&gt;/&lt;stem&gt;.mesh.json</c> beside a Cast path (<c>out.with_name(out.stem + ".mesh.json")</c>).</summary>
    public static string PathFor(string castPath) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(castPath) ?? "", System.IO.Path.GetFileNameWithoutExtension(castPath) + ".mesh.json");

    /// <summary>The <c>cast</c> block <c>export_cast_files</c> passes: file name, node counts, skipped, mesh → entry/submesh.</summary>
    public static OrderedDictionary<string, object?> CastInfo(string fileName, Nightrunner.Core.Cast.CastExportReport rep) => new(StringComparer.Ordinal)
    {
        ["file"] = fileName,
        ["nodes"] = new OrderedDictionary<string, object?>(rep.Nodes.Select(kv => KeyValuePair.Create(kv.Key, (object?)kv.Value)), StringComparer.Ordinal),
        ["skipped"] = rep.Skipped.Cast<object?>().ToList(),
        ["meshes"] = rep.Meshes.Select(x => (object?)new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = x.Name, ["entry"] = x.Entry, ["submesh"] = x.Submesh,
        }).ToList(),
    };

    /// <summary>Field table of a vertex format: name, offset, numpy dtype string, count (the prototype's <c>field_table</c>).</summary>
    public static List<object?> FieldTable(int fmt)
    {
        (string, int, string, int)[] f = fmt switch
        {
            0 => [("pos", 0, "<f2", 3), ("raw_06", 6, "<u2", 1), ("qtan", 8, "|i1", 4), ("uv0", 12, "<f2", 2)],
            3 => [("pos", 0, "<f4", 3), ("qtan", 12, "<i2", 4), ("uv0", 20, "<f2", 2), ("uv1", 24, "<f2", 2), ("raw_tail", 28, "<u4", 1)],
            6 => [("pos", 0, "<f4", 3), ("weights", 12, "|u1", 4), ("joints", 16, "|u1", 4), ("qtan", 20, "<i2", 4),
                  ("uv0", 28, "<f2", 2), ("uv1", 32, "<f2", 2), ("raw_tail", 36, "<u4", 1)],
            8 => [("pos", 0, "<f4", 3), ("weights", 12, "|u1", 4), ("joints", 16, "|u1", 4), ("qtan", 20, "<i2", 4),
                  ("uv0", 28, "<f2", 2), ("uv1", 32, "<f2", 2), ("raw_tail", 36, "<u4", 1), ("raw_ext", 40, "|u1", 40)],
            _ => throw new MeshUnsupportedException($"vertex format {fmt} is not one of 0/3/6/8"),
        };
        return f.Select(x => (object?)Obj(("name", x.Item1), ("offset", x.Item2), ("dtype", x.Item3), ("count", x.Item4))).ToList();
    }

    public static OrderedDictionary<string, object?> Build(MeshModel model, IReadOnlyDictionary<string, string>? partSha256 = null,
                                                           OrderedDictionary<string, object?>? cast = null, MeshSidecarSource? source = null)
    {
        var entries = new List<object?>();
        foreach (var e in model.GeometryEntries)
        {
            var rec = Obj(
                ("index", e.Index), ("array_record", e.ArrayRecord), ("element", e.Element), ("image_offset", e.Offset),
                ("owner_entity", e.OwnerEntity), ("format", e.Format), ("vertex_base", e.VertexBase),
                ("vertex_count", e.VertexCount), ("index_base", e.IndexBase),
                ("raw_00", Hex(e.Raw00)), ("raw_12", e.Raw12), ("raw_14", e.Raw14), ("raw_17", e.Raw17), ("raw_34", Hex(e.Raw34)),
                ("material_slots_offset", e.MaterialSlotsOffset), ("index_counts_offset", e.IndexCountsOffset));
            if (e.StreamOffset is { } so)
            {
                rec["stream_offset"] = so;
                rec["raw_stream"] = Hex(e.RawStream);
            }
            rec["submeshes"] = e.Submeshes.Select(s => (object?)Obj(
                ("index", s.Index), ("material_slot", s.MaterialSlot), ("index_count", s.IndexCount),
                ("index_base", s.IndexBase), ("palette", s.Palette.Select(x => (object?)(int)x).ToList()),
                ("palette_desc_offset", s.PaletteDescOffset), ("palette_offset", s.PaletteOffset))).ToList();
            if (e.Vertices is { } v)
            {
                rec["vertex_stride"] = Vertex.Stride(e.Format);
                rec["vertex_fields"] = FieldTable(e.Format);
                rec["vertex_raw_b64"] = Convert.ToBase64String(v.Raw);
                if (v.Skinned)
                {
                    var hist = new SortedDictionary<int, int>();
                    for (int i = 0; i < v.Count; i++) { int s = v.WeightSum(i); hist[s] = hist.GetValueOrDefault(s) + 1; }
                    rec["weight_sum_histogram"] = new OrderedDictionary<string, object?>(
                        hist.Select(kv => KeyValuePair.Create(kv.Key.ToString(CultureInfo.InvariantCulture), (object?)kv.Value)), StringComparer.Ordinal);
                }
            }
            entries.Add(rec);
        }
        var entities = model.Entities.Select(en => (object?)Obj(
            ("index", en.Index), ("image_offset", en.Offset), ("name", MeshModel.Text(en.Name)), ("name_hex", Hex(en.Name)),
            ("parent", en.Parent), ("type", en.Type), ("flags", $"0x{en.Flags:X8}"), ("geometry_count", en.GeometryCount),
            ("geometry_array_record", en.GeometryArrayRecord), ("geometry_entries", en.GeometryEntries.Select(x => (object?)x).ToList()),
            ("local_3x4", Floats(en.Local)), ("inv_bind_3x4", Floats(en.InvBind)),
            ("bounds_center", Floats(en.BoundsCenter)), ("bounds_half", Floats(en.BoundsHalf)),
            ("aux_offset", en.AuxOffset), ("raw_aux", Hex(en.RawAux)), ("raw_90", Hex(en.Raw90)), ("raw_ca", Hex(en.RawCa)))).ToList();
        var materials = Obj(
            ("header_offset", model.MaterialHeaderOffset), ("count", model.Materials.Length), ("capacity", model.MaterialCapacity),
            ("entries", model.Materials.Select(m => (object?)Obj(
                ("index", m.Index), ("image_offset", m.Offset), ("name", MeshModel.Text(m.Name)), ("name_hex", Hex(m.Name)),
                ("name_tag", $"0x{m.NameTag:X4}"), ("name_inline", m.NameInline), ("raw", Hex(m.Raw)))).ToList()));
        var opaque = model.Opaque.Select(o =>
        {
            var d = Obj(("record", o.Record), ("class_id", o.ClassId), ("offset", o.Offset), ("size", o.Size));
            if (o.ClassId != 0) d["hex"] = Hex(o.Data);
            return (object?)d;
        }).ToList();
        var nameBytes = Encoding.UTF8.GetBytes(model.Name);
        return Obj(
            ("schema", Schema), ("tool", Tool),
            ("name", MeshModel.Text(nameBytes)), ("name_hex", Hex(nameBytes)),
            ("layout", model.Layout),
            ("embedded_name", MeshModel.Text(model.EmbeddedName)),
            ("embedded_name_hex", Hex(model.EmbeddedName)),
            ("scr_name", model.ScrName is null ? null : MeshModel.Text(model.ScrName)),
            ("source", source is null ? Obj() : Obj(("pack", source.Pack), ("index", source.Index), ("name", source.Name))),
            ("sha256", partSha256 is null ? Obj() : new OrderedDictionary<string, object?>(
                partSha256.Select(kv => KeyValuePair.Create(kv.Key, (object?)kv.Value)), StringComparer.Ordinal)),
            ("buffers", Obj(("vertex_size", model.VertexBuffer?.Length ?? 0), ("index_size", model.IndexBuffer?.Length ?? 0),
                            ("skin_size", model.SkinRaw?.Length), ("cloth_size", model.ClothRaw?.Length))),
            ("fixups", FixupsJson(model.Fixups)),
            ("image_size", model.Image.Size), ("image_part_size", model.Image.Data.Length),
            ("root_raw", Hex(model.RootRaw)),
            ("entities", entities),
            ("materials", materials),
            ("geometry_entries", entries),
            ("opaque_records", opaque),
            ("cast", cast ?? Obj()),
            ("warnings", model.Warnings.Cast<object?>().ToList()));
    }

    /// <summary><c>Fixups.to_json()</c> (summary form, no record/slot lists).</summary>
    public static OrderedDictionary<string, object?> FixupsJson(Fixups fx) => Obj(
        ("primary_size", fx.PrimarySize), ("record_count", fx.Records.Count),
        ("object_count_raw", $"0x{fx.ObjectCountRaw:X8}"), ("secondary_present", fx.SecondaryPresentFlag),
        ("secondary_size", fx.Secondary?.Length),
        ("slot_count", fx.Slots.Count), ("slot_kinds", fx.Slots.Select(s => (int)s.Kind).Distinct().Order().Select(x => (object?)x).ToList()),
        ("trailing_bytes", fx.Trailing.Length),
        ("class_census", new OrderedDictionary<string, object?>(
            fx.ClassCensus().Select(kv => KeyValuePair.Create(kv.Key.ToString(CultureInfo.InvariantCulture), (object?)kv.Value)), StringComparer.Ordinal)));

    /// <summary>Atomic write (<c>&lt;path&gt;.tmp</c>, then replace), UTF-8 without BOM, LF, as <c>dump_json</c>.</summary>
    public static void Write(object? document, string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        string tmp = full + ".tmp";
        try
        {
            System.IO.File.WriteAllText(tmp, ToJson(document), new UTF8Encoding(false));
            System.IO.File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
        }
    }

    // ---- Python json.dump(indent=2, ensure_ascii=False) + "\n" ----------------------------------------------------

    /// <summary>The text <c>dump_json</c> writes (indent 2), trailing newline included.</summary>
    public static string ToJson(object? document)
    {
        var sb = new StringBuilder(1 << 16);
        WriteValue(sb, document, 0);
        sb.Append('\n');
        return sb.ToString();
    }

    private static void WriteValue(StringBuilder sb, object? v, int level)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case string s: WriteString(sb, s); break;
            case int or uint or long or ulong or short or ushort or byte or sbyte:
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            case float f: sb.Append(PyFloat(f)); break;
            case double d: sb.Append(PyFloat(d)); break;
            case IDictionary dict:
                if (dict.Count == 0) { sb.Append("{}"); break; }
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry kv in dict)
                {
                    sb.Append(first ? "" : ",").Append('\n').Append(' ', 2 * (level + 1));
                    first = false;
                    WriteString(sb, kv.Key as string ?? throw new ArgumentException("JSON object keys must be strings"));
                    sb.Append(": ");
                    WriteValue(sb, kv.Value, level + 1);
                }
                sb.Append('\n').Append(' ', 2 * level).Append('}');
                break;
            case IEnumerable list:
            {
                bool any = false;
                foreach (var item in list)
                {
                    sb.Append(any ? "," : "[").Append('\n').Append(' ', 2 * (level + 1));
                    any = true;
                    WriteValue(sb, item, level + 1);
                }
                if (!any) sb.Append("[]");
                else sb.Append('\n').Append(' ', 2 * level).Append(']');
                break;
            }
            default: throw new ArgumentException($"no JSON form for {v.GetType().Name}");
        }
    }

    /// <summary>Python's <c>json</c> string escaping with <c>ensure_ascii=False</c>.</summary>
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
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>
    /// <c>repr(float)</c> as <c>json</c> writes it: shortest round-trip digits; fixed notation when the decimal exponent
    /// is in (-4, 16], else <c>d.ddde±XX</c>; <c>NaN</c>/<c>Infinity</c>/<c>-Infinity</c>.
    /// </summary>
    public static string PyFloat(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        if (d == 0) return double.IsNegative(d) ? "-0.0" : "0.0";
        string r = ShortestDigits(Math.Abs(d));
        // split r into significant digits and decimal point position (value = 0.DIGITS × 10^decpt)
        int e = 0;
        int ei = r.IndexOfAny(['E', 'e']);
        string mant = r;
        if (ei >= 0)
        {
            e = int.Parse(r[(ei + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            mant = r[..ei];
        }
        int dot = mant.IndexOf('.');
        string intPart = dot >= 0 ? mant[..dot] : mant, frac = dot >= 0 ? mant[(dot + 1)..] : "";
        string all = intPart + frac;
        int decpt = intPart.Length + e;
        int lead = 0;
        while (lead < all.Length - 1 && all[lead] == '0') { lead++; decpt--; }
        string digits = all[lead..].TrimEnd('0');
        if (digits.Length == 0) digits = "0";
        var sb = new StringBuilder();
        if (d < 0) sb.Append('-');
        if (decpt > -4 && decpt <= 16)
        {
            if (decpt <= 0) sb.Append("0.").Append('0', -decpt).Append(digits);
            else if (decpt >= digits.Length) sb.Append(digits).Append('0', decpt - digits.Length).Append(".0");
            else sb.Append(digits, 0, decpt).Append('.').Append(digits, decpt, digits.Length - decpt);
        }
        else
        {
            int exp = decpt - 1;
            sb.Append(digits[0]);
            if (digits.Length > 1) sb.Append('.').Append(digits, 1, digits.Length - 1);
            sb.Append('e').Append(exp < 0 ? '-' : '+').Append(Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Shortest decimal that parses back to <paramref name="x"/> (&gt; 0), as <c>repr</c> finds it. .NET's "R" is
    /// that except at some powers of two, where the rounding interval below is half as wide: 2^-25 gives
    /// "2.980232238769531E-08", which parses to the double below (Python: 2.9802322387695312e-08). Then the digits are
    /// searched: the correctly rounded p-digit value first, then its upper and lower neighbours, for p = 1..17.
    /// </summary>
    private static string ShortestDigits(double x)
    {
        var inv = CultureInfo.InvariantCulture;
        string r = x.ToString("R", inv);
        if (double.Parse(r, inv) == x) return r;
        for (int p = 1; p <= 17; p++)
        {
            string s = x.ToString("E" + (p - 1).ToString(inv), inv);
            int ei = s.IndexOf('E');
            long m = long.Parse(s[..ei].Replace(".", ""), inv);
            int e = int.Parse(s[(ei + 1)..], NumberStyles.AllowLeadingSign, inv) - (p - 1);
            foreach (long c in new[] { m, m + 1, m - 1 })
            {
                if (c <= 0) continue;
                string cand = $"{c.ToString(inv)}E{e.ToString(inv)}";
                if (double.Parse(cand, inv) == x) return cand;
            }
        }
        return x.ToString("E16", inv);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static OrderedDictionary<string, object?> Obj(params (string Key, object? Value)[] items)
    {
        var d = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in items) d[k] = v;
        return d;
    }

    private static string Hex(byte[] b) => Convert.ToHexStringLower(b);

    private static List<object?> Floats(float[] a) => a.Select(x => (object?)(double)x).ToList();
}
