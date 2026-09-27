using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Nightrunner.Core.Sdb;

/// <summary>
/// A material as DevTools <c>.mat</c> source text, rebuilt from the compiled database:
/// <code>
/// sub material()
/// {
///   preset("opaque;dif_0_tex;…");        the route's token string
///   dif_0_tex("x_dif.png");               string: the 0xAE name
///   rghDef(0.4099999964);                 float: the stored single, widened, 10 decimals
///   of2_pos([0.5000000000, 0.0000000000]);  vec2/3/4: bracketed, same number form
/// }
/// </code>
/// Every parameter the route sets is written, one line each. Checked against the DL2 DevTools sources (see docs/formats.md,
/// SDB): the lines match as a set; their order is the author's, which the database does not keep, so they are written
/// in the order <see cref="Order"/> describes.
/// </summary>
public static class MatWriter
{
    /// <summary>
    /// The text, or throws <see cref="SdbFormatException"/> for a material this cannot write faithfully: more than one
    /// route, a parameter the preset does not declare (untyped bytes), a runtime string, or a type with no known
    /// source form (bool, int, matrix — no DevTools source uses one).
    /// </summary>
    public static string Write(SdbFile sdb, SdbMaterial material)
    {
        if (material.Routes.Count != 1)
            throw new SdbFormatException($"{material.Name}: {material.Routes.Count} routes; a .mat has one");
        var route = material.Routes[0];
        var text = new StringBuilder();
        text.Append("sub material()\n{\n");
        text.Append($"  preset(\"{route.Tokens}\");\n");
        foreach (var p in Order(sdb, route))
            text.Append($"  {p.Name}({Value(material.Name, p)});\n");
        text.Append("}\n");
        return text.ToString();
    }

    /// <summary>
    /// The preset's declaration order (then any it does not declare, in slot order). The database keeps no authoring
    /// order; this one reproduces the DevTools source's line order for about half the materials checked.
    /// </summary>
    public static IEnumerable<SdbParameter> Order(SdbFile sdb, SdbRoute route)
    {
        var declared = route.PresetIndices.Count > 0
            ? sdb.Preset(route.PresetIndices[0]).Parameters.Select((d, i) => (d.Id, i)).DistinctBy(x => x.Id).ToDictionary(x => x.Id, x => x.i)
            : [];
        return route.Parameters.Select((p, i) => (p, i))
            .OrderBy(x => declared.TryGetValue(x.p.Id, out int d) ? d : int.MaxValue).ThenBy(x => x.i).Select(x => x.p);
    }

    private static string Value(string material, SdbParameter p)
    {
        if (!p.Declared || p.ValueHex is null)
            throw new SdbFormatException($"{material}: parameter {p.Name} is not declared by the preset; its bytes are untyped");
        byte[] raw = Convert.FromHexString(p.ValueHex);
        return p.Type switch
        {
            2 => Number(raw),
            3 or 4 or 5 => "[" + string.Join(", ", Enumerable.Range(0, raw.Length / 4).Select(i => Number(raw.AsSpan(i * 4)))) + "]",
            7 when p.RuntimeIndex is { } r =>
                throw new SdbFormatException($"{material}: {p.Name} names runtime string {r}, which the database does not hold"),
            7 => $"\"{p.ValueText}\"",
            _ => throw new SdbFormatException($"{material}: {p.Name} is a {p.TypeName}; no DevTools source shows how one is written"),
        };
    }

    private static string Number(ReadOnlySpan<byte> raw) =>
        ((double)BinaryPrimitives.ReadSingleLittleEndian(raw)).ToString("F10", CultureInfo.InvariantCulture);
}
