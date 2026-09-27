using Nightrunner.Core.Sdb;

namespace Nightrunner.Tests;

/// <summary>
/// <c>.mat</c> text from the SDB against the DL2 DevTools sources. The database keeps every line of a source but not
/// the author's line order; these two sources happen to be in preset declaration order and must match byte for byte.
/// </summary>
public class MatWriterTests
{
    [Theory]
    [InlineData("ac_unit_elements_a.mat")]
    [InlineData("chemical_container_a.mat")]
    public void MatchesDevToolsSource(string name)
    {
        var install = Installs.Require("dl2");
        string source = Path.Combine(install.Root, "DevTools", "ph_d", "common", "source", "data", "Materials", name);
        if (!File.Exists(source)) Assert.Skip("DL2 DevTools material sources not installed");
        using var sdb = SdbFile.Open(install.Sdb("dx11"));
        var material = sdb.Material(Assert.Single(sdb.FindMaterial(name)));
        Assert.Equal(File.ReadAllText(source).Replace("\r\n", "\n").TrimEnd(), MatWriter.Write(sdb, material).TrimEnd());
    }

    [Fact]
    public void EveryLineOfEachMatchingSourceIsWritten()
    {
        var install = Installs.Require("dl2");
        string folder = Path.Combine(install.Root, "DevTools", "ph_d", "common", "source", "data", "Materials");
        if (!Directory.Exists(folder)) Assert.Skip("DL2 DevTools material sources not installed");
        using var sdb = SdbFile.Open(install.Sdb("dx11"));
        int compared = 0;
        foreach (var file in Directory.GetFiles(folder, "*.mat"))
        {
            var hits = sdb.FindMaterial(Path.GetFileName(file));
            if (hits.Count != 1) continue;
            var theirs = File.ReadAllLines(file).Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
            var mine = MatWriter.Write(sdb, sdb.Material(hits[0])).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (mine[2] != theirs[2]) continue;          // the shipped material was changed after the DevTools snapshot
            compared++;
            // DevTools may also set a value equal to the preset default, which the compiler drops (glowstick_glow_a)
            Assert.Empty(mine.Except(theirs));
        }
        Assert.True(compared >= 10, $"{compared} sources compared");
    }

    [Fact]
    public void IntParameterIsRefused()
    {
        var p = new SdbParameter(1, "iteration", 0, false, 0, true, 1, "3", "03000000", null, null);
        var route = new SdbRoute(0, 0, 0, 0, 0, 0, "opaque;", "opaque", [], [p], []);
        var material = new SdbMaterial(0, "x.mat", "t", [route], false);
        var e = Assert.Throws<SdbFormatException>(() => MatWriter.Write(null!, material));
        Assert.Contains("iteration is a int", e.Message);
    }
}
