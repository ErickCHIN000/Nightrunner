using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Nightrunner.Core.Games;

namespace Nightrunner.Core.Mesh;

/// <summary>
/// Surface ids and surface flags from the game's <c>surface.def</c> (<c>$SRF_X (i, n)</c> paired with
/// <c>$SRFS_X (s, "Name")</c>; flags the same under <c>SRF_FLAG_</c>). Read-only, out of <c>source/dataN.pak</c>.
/// </summary>
public sealed partial class SurfaceDefs
{
    public IReadOnlyDictionary<int, string> Surfaces { get; }
    public IReadOnlyDictionary<int, string> Flags { get; }

    private SurfaceDefs(Dictionary<int, string> surfaces, Dictionary<int, string> flags)
    {
        Surfaces = surfaces;
        Flags = flags;
    }

    [GeneratedRegex(@"^\s*\$(SRFS?)_(\w+)\s*\(\s*([is])\s*,\s*(""[^""]*""|-?\d+)\s*\)", RegexOptions.Multiline)]
    private static partial Regex Line();

    public static SurfaceDefs Parse(string text)
    {
        var ids = new Dictionary<string, int>();
        var names = new Dictionary<string, string>();
        foreach (Match m in Line().Matches(text))
        {
            string key = m.Groups[2].Value, v = m.Groups[4].Value;
            if (m.Groups[1].Value == "SRF" && m.Groups[3].Value == "i") ids[key] = int.Parse(v);
            else if (m.Groups[1].Value == "SRFS" && m.Groups[3].Value == "s") names[key] = v.Trim('"');
        }
        var surfaces = new Dictionary<int, string>();
        var flags = new Dictionary<int, string>();
        foreach (var (key, id) in ids)
        {
            if (!names.TryGetValue(key, out var name)) continue;
            if (key.StartsWith("FLAG_")) flags[id] = name;
            else surfaces[id] = name;
        }
        return new SurfaceDefs(surfaces, flags);
    }

    /// <summary>
    /// <c>surface.def</c> from an install's paks (the highest-numbered pak that has it wins, as <c>dataN</c> overrides
    /// <c>data0</c>); null when no pak carries it.
    /// </summary>
    public static SurfaceDefs? Load(GameInstall install)
    {
        foreach (var pak in install.Paks().Reverse())
        {
            try
            {
                using var z = ZipFile.OpenRead(pak);
                var e = z.GetEntry("surface.def");
                if (e is null) continue;
                using var r = new StreamReader(e.Open(), Encoding.UTF8);
                return Parse(r.ReadToEnd());
            }
            catch (InvalidDataException) { }   // not a zip: skip it
        }
        return null;
    }
}

/// <summary>
/// Compiled skins → <c>.skn</c> source text. Lossy by design: comments, <c>!include</c>s, <c>NodePos</c> and
/// <c>Replace</c> lines for materials the mesh does not have never reach the compiled part.
/// </summary>
/// <remarks>
/// Checked against the DL2 DevTools sources: <c>ColorI(0, r, g, b)</c> compiles to the colour object
/// <c>r g b ff 00 00 00 ff</c> with e_hi = 1; <c>UseSkin(name, 1)</c> to a ref with hi 0x10, raw 0x81000000;
/// surface flags are joined with <c>" | "</c> and written <c>""</c> when zero; a new surface equal to the old one is
/// written <c>""</c> as the sources do (<c>""</c> compiles to the old id). Anything outside those shapes is
/// written as numbers with a comment rather than guessed.
/// </remarks>
public static class SknWriter
{
    public static string Write(MeshSkins skins, MeshModel model, SurfaceDefs? defs)
    {
        if (skins.Error is not null) throw new MeshFormatException($"skins unreadable: {skins.Error}");
        var table = model.FullMaterialTable();
        string Mat(int i) => i < table.Length ? table[i] : $"material_{i}";
        string Surface(int id) => defs is not null && defs.Surfaces.TryGetValue(id, out var n) ? Q(n) : id.ToString();
        string SurfFlags(int f)
        {
            if (defs is null) return f == 0 ? "0" : $"0x{f:X}";
            var parts = new List<string>();
            int rest = f;
            for (int bit = 0; bit < 16; bit++)
            {
                int m = 1 << bit;
                if ((f & m) == 0 || !defs.Flags.TryGetValue(m, out var n)) continue;
                parts.Add(n);
                rest &= ~m;
            }
            if (rest != 0) parts.Add($"0x{rest:X}");
            return Q(string.Join(" | ", parts));
        }

        var sb = new StringBuilder();
        foreach (var s in skins.Skins)
        {
            sb.Append("Skin(").Append(Q(s.NameStr)).Append(")\n{\n");
            if (s.FilterInEditor) sb.Append("    FilterInEditor()\n");
            foreach (var u in s.UseSkin)
            {
                string target = u.Record < skins.Skins.Count ? Q(skins.Skins[u.Record].NameStr) : $"#{u.Record}";
                if (u.Hi == 0x10 && u.Raw == 0x81000000) sb.Append($"    UseSkin({target}, 1)\n");
                else sb.Append($"    UseSkin({target}, 1) // unseen ref words 0x{u.Hi:X2} 0x{u.Raw:X8}\n");
            }
            if (s.HasColor && s.Color is { } c)
            {
                if (c[3] == 0xFF && c[4] == 0 && c[5] == 0 && c[6] == 0 && c[7] == 0xFF)
                    sb.Append($"    ColorI(0, {c[0]}, {c[1]}, {c[2]})\n");
                else sb.Append($"    // ColorI: unseen colour object {Convert.ToHexString(c)}\n");
            }
            foreach (var p in s.Replace)
                sb.Append($"    Replace({Q(Mat(p.Slot))}, {Q(Mat(p.Material))})\n");
            foreach (var r in s.ReplaceSurface)
                sb.Append($"    ReplaceSurface({Surface(r.Old)}, {(r.New == r.Old ? Q("") : Surface(r.New))}, {SurfFlags(r.SurfaceFlags)})\n");
            sb.Append("}\n");
        }
        return sb.ToString();
    }

    private static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
