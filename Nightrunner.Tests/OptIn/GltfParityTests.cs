using Nightrunner.Core.Cast;

namespace Nightrunner.Tests;

// Opt-in (compiled only with -p:OptInTests=true; see tools/parity/README.md): historical parity with the Python
// prototype on the files its glTF fixture script (tools/parity/gltf_fixtures.py) wrote into NIGHTRUNNER_GLTF_FIXTURES.
public partial class GltfTests
{
    private const string FixturesVariable = "NIGHTRUNNER_GLTF_FIXTURES";

    private static string RequireFixtures()
    {
        var dir = Environment.GetEnvironmentVariable(FixturesVariable);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            Assert.Skip($"{FixturesVariable} not set to the folder the prototype's glTF fixture script wrote");
        return dir!;
    }

    /// <summary>Every Cast the script exported (synth/ and real/) written by both sides: identical bytes (measured
    /// 2026-09-23: 76/76 — 3 synthetic and 35 `nr mesh export` Casts, 20 DLTB and 15 DL2, as .glb and .gltf + .bin), or
    /// failing that the same document with float accessor data equal up to the sign of zero.</summary>
    [Fact]
    [Trait("Category", "OptIn")]
    public void PrototypeExportsMatch()
    {
        var dir = RequireFixtures();
        var files = new[] { "synth", "real" }.Where(s => Directory.Exists(Path.Combine(dir, s)))
            .SelectMany(s => Directory.GetFiles(Path.Combine(dir, s), "*.cast")).Where(f => !f.EndsWith(".read.cast")).Order().ToList();
        Assert.NotEmpty(files);
        var outDir = TempDir();
        var report = new List<string>();
        int identical = 0, total = 0;
        try
        {
            foreach (var cast in files)
            {
                var c = CastFile.Load(cast);
                // the prototype wrote next to the Cast, textures relative to it: give ours the same neighbours
                var tex = Path.Combine(Path.GetDirectoryName(cast)!, "tex");
                if (Directory.Exists(tex))
                {
                    Directory.CreateDirectory(Path.Combine(outDir, "tex"));
                    foreach (var t in Directory.GetFiles(tex)) System.IO.File.Copy(t, Path.Combine(outDir, "tex", Path.GetFileName(t)), true);
                }
                foreach (var ext in new[] { ".glb", ".gltf" })
                {
                    var theirs = Path.ChangeExtension(cast, ext);
                    if (!System.IO.File.Exists(theirs)) continue;
                    var ours = Path.Combine(outDir, Path.GetFileName(theirs));
                    Gltf.Save(c, ours);
                    total++;
                    Assert.Empty(GltfCheck.Validate(ours).Select(p => $"{Path.GetFileName(ours)}: {p}"));
                    bool same = System.IO.File.ReadAllBytes(theirs).AsSpan().SequenceEqual(System.IO.File.ReadAllBytes(ours));
                    if (ext == ".gltf")
                        same &= System.IO.File.ReadAllBytes(Path.ChangeExtension(theirs, ".bin")).AsSpan()
                            .SequenceEqual(System.IO.File.ReadAllBytes(Path.ChangeExtension(ours, ".bin")));
                    if (same) { identical++; continue; }
                    var diff = GltfCheck.Compare(theirs, ours, maxUlp: 0);
                    Assert.True(diff.Problems.Count == 0, $"{Path.GetFileName(theirs)}: {string.Join("; ", diff.Problems.Take(5))}");
                    report.Add($"{Path.GetFileName(theirs)}: {diff.Summary}");
                }
            }
        }
        finally { Directory.Delete(outDir, true); }
        TestContext.Current.TestOutputHelper?.WriteLine($"glTF exports: {identical}/{total} byte-identical; {string.Join("; ", report)}");
        Assert.True(total >= 6, $"{total} files");
    }

    /// <summary>Every glTF / GLB in the folder read by both sides: the Cast load_scene saved, byte for byte, or the same
    /// tree with every float bit-identical (up to the sign of zero) except bone rotations, which come from an eigenvector:
    /// numpy's LAPACK and the C# Jacobi agree to 1.8e-15 and differ in signed zeros (measured 2026-09-23: 56/79 files
    /// byte-identical, the other 23 differ only there).</summary>
    [Fact]
    [Trait("Category", "OptIn")]
    public void PrototypeReadsMatch()
    {
        var dir = RequireFixtures();
        var files = Directory.GetFiles(dir, "*.gl*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".glb") || f.EndsWith(".gltf")).Order().ToList();
        int identical = 0, total = 0;
        var report = new List<string>();
        foreach (var f in files)
        {
            var theirs = f + ".read.cast";
            if (!System.IO.File.Exists(theirs)) continue;
            total++;
            var ours = Gltf.Load(f).ToBytes();
            var expected = System.IO.File.ReadAllBytes(theirs);
            if (expected.AsSpan().SequenceEqual(ours)) { identical++; continue; }
            var (problems, summary) = GltfCheck.CompareCasts(CastFile.Read(expected), CastFile.Read(ours), maxUlp: 0, rotationTolerance: 1e-12);
            Assert.True(problems.Count == 0, $"{Path.GetFileName(f)}: {string.Join("; ", problems)}");
            report.Add($"{Path.GetFileName(f)}: {summary}");
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"glTF reads: {identical}/{total} byte-identical; {string.Join("; ", report)}");
        Assert.True(total >= 6, $"{total} files");
    }
}
