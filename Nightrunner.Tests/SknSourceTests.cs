using System.Text.RegularExpressions;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Tests;

/// <summary>
/// The <c>.skn</c> emitter against real sources: DL2 ships DevTools with <c>.skn</c> files whose compiled skins are in
/// the game's packs. Emitting the compiled part and parsing both texts must give the same skins, minus what compiling
/// drops (comments, includes, NodePos/Texture blocks, Replace lines for materials the mesh does not have).
/// </summary>
/// <remarks>
/// Compile rules read off these pairs, applied before comparing: a <c>ReplaceSurface</c> whose new surface is
/// <c>""</c> compiles to the old id; material names match case-insensitively (the table may hold
/// <c>TRAVEL_BAG_NEW.MAT</c>); a <c>Default</c> skin gains identity <c>Replace(x, x)</c> pairs for mesh materials
/// its source does not list. <c>DevTools/projects</c> holds sample mods, not shipped sources, and is skipped.
/// </remarks>
public class SknSourceTests
{
    [Fact]
    public void EmittedSkinsMatchDevToolsSources()
    {
        var install = Installs.Require("dl2");
        var devtools = Path.Combine(install.Root, "DevTools");
        var sources = Directory.Exists(devtools)
            ? Directory.EnumerateFiles(devtools, "*.skn", SearchOption.AllDirectories)
                .Where(p => !Path.GetRelativePath(devtools, p).StartsWith("projects", StringComparison.OrdinalIgnoreCase)).ToList()
            : [];
        if (sources.Count == 0) Assert.Skip("DL2 DevTools .skn sources not installed");
        var defs = SurfaceDefs.Load(install);
        Assert.NotNull(defs);

        var byName = new Dictionary<string, (string Pack, int Index)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in install.Rpacks())
        {
            using var pk = RpackFile.Open(p);
            for (int i = 0; i < pk.Count; i++)
                if (pk.Logicals[i].Type == 0x10) byName.TryAdd(pk.Name(i), (p, i));
        }

        var problems = new List<string>();
        int compared = 0, skinsCompared = 0;
        foreach (var src in sources)
        {
            string mesh = Path.GetFileNameWithoutExtension(src);
            if (!byName.TryGetValue(mesh, out var at)) continue;
            using var pack = RpackFile.Open(at.Pack);
            var model = MeshDecoder.Decode(pack, at.Index);
            var skins = MeshSkins.Decode(model.SkinRaw!);
            var emitted = Parse(SknWriter.Write(skins, model, defs));
            var source = Parse(ReadWithIncludes(src));
            var meshMats = new HashSet<string>(model.Materials.Select(m => m.NameStr), StringComparer.OrdinalIgnoreCase);
            compared++;
            foreach (var (name, want) in source)
            {
                if (!emitted.TryGetValue(name, out var got))
                {
                    problems.Add($"{mesh}: skin '{name}' not in the compiled part");
                    continue;
                }
                skinsCompared++;
                var wantLines = want.Where(l => !l.StartsWith("Replace(") || meshMats.Contains(Args(l)[0])).ToHashSet();
                if (name == "Default")
                    got = got.Where(l => !(l.StartsWith("Replace(") && Args(l) is var a && a[0] == a[1] && !wantLines.Contains(l))).ToHashSet();
                if (Known.Contains((mesh, name))) continue;
                if (!wantLines.SetEquals(got))
                    problems.Add($"{mesh} '{name}': source [{string.Join(" ", wantLines.Except(got))}] emitted [{string.Join(" ", got.Except(wantLines))}]");
            }
        }
        Assert.True(compared > 0, "no DevTools .skn names a shipped mesh");
        Assert.True(problems.Count == 0, $"{compared} meshes, {skinsCompared} skins:\n" + string.Join("\n", problems));
    }

    /// <summary>Source and compiled part disagree for a reason not understood (C): <c>ColorI(255, 255, 255, 255)</c>
    /// written after <c>ReplaceSurface</c> does not reach the compiled skin.</summary>
    private static readonly HashSet<(string, string)> Known = [("dummy_box", "Barrier Transparent")];

    private static string ReadWithIncludes(string path, int depth = 0)
    {
        var text = File.ReadAllText(path);
        if (depth > 4) return text;
        return Regex.Replace(text, @"!include\(""([^""]+\.skn)""\)", m =>
        {
            var inc = Path.Combine(Path.GetDirectoryName(path)!, m.Groups[1].Value);
            return File.Exists(inc) ? ReadWithIncludes(inc, depth + 1) : "";
        });
    }

    /// <summary>Skin name → normalised statements. Later definitions of a name replace earlier ones.</summary>
    internal static Dictionary<string, HashSet<string>> Parse(string text)
    {
        text = Regex.Replace(text, @"//[^\n]*", "");
        var skins = new Dictionary<string, HashSet<string>>();
        HashSet<string>? cur = null;
        foreach (Match m in Regex.Matches(text, @"(\w+)\s*\(((?:""[^""]*""|[^()""])*)\)"))
        {
            string cmd = m.Groups[1].Value;
            var a = SplitArgs(m.Groups[2].Value);
            switch (cmd)
            {
                case "Skin":
                    skins[a[0]] = cur = [];
                    break;
                case "Replace" or "UseSkin" or "FilterInEditor" or "ColorI" when cur is not null:
                    cur.Add($"{cmd}({string.Join(",", cmd == "Replace" ? a.Select(x => x.ToLowerInvariant()) : a)})");
                    break;
                case "ReplaceSurface" when cur is not null:
                    var flags = a[2].Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Order();
                    cur.Add($"ReplaceSurface({a[0]},{(a[1] == "" ? a[0] : a[1])},{string.Join("|", flags)})");
                    break;
            }
        }
        return skins;
    }

    private static string[] Args(string line) => SplitArgs(line[(line.IndexOf('(') + 1)..^1]);

    private static string[] SplitArgs(string s) =>
        Regex.Matches(s, @"""([^""]*)""|([^,\s][^,]*)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value.Trim()).ToArray();
}
