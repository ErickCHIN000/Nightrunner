using System.IO.Compression;
using System.Text.Json.Nodes;
using Nightrunner.Core.Model;

namespace Nightrunner.Tests;

/// <summary>
/// The mod PAK writer and its JSON form. <c>PyJson.Dumps</c> was checked byte for byte against the prototype's
/// <c>json.dumps(indent=2, ensure_ascii=True)</c> on every stock <c>.model</c> (DLTB 815, DL2 2,330).
/// </summary>
public class PakWriterTests
{
    [Theory]
    [InlineData(1.0, "1.0")]
    [InlineData(0.1, "0.1")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(1e-05, "1e-05")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(1e16, "1e+16")]
    [InlineData(123456789012345.0, "123456789012345.0")]
    [InlineData(0.30000000000000004, "0.30000000000000004")]
    [InlineData(-0.0, "-0.0")]
    public void FloatReprMatchesPython(double value, string repr) => Assert.Equal(repr, PyJson.Float(value));

    [Fact]
    public void DumpsLikePython()
    {
        var doc = JsonNode.Parse("""{"a": [1, 2.50, {}], "b": {"c": "é\"", "d": []}, "e": null, "f": true}""");
        Assert.Equal("{\n  \"a\": [\n    1,\n    2.5,\n    {}\n  ],\n  \"b\": {\n    \"c\": \"\\u00e9\\\"\",\n    \"d\": []\n  },\n" +
                     "  \"e\": null,\n  \"f\": true\n}", PyJson.Dumps(doc));
    }

    [Fact]
    public void WritesVerifiesAndRefusesBadPaths()
    {
        string dir = Directory.CreateTempSubdirectory("nr-pakw-").FullName;
        try
        {
            string pak = Path.Combine(dir, "data5.pak");
            var doc = JsonNode.Parse("""{"version": 6, "slots": []}""")!;
            var r = PakWriter.Write(pak, new Dictionary<string, JsonNode> { ["models/player/x.model"] = doc },
                                    new Dictionary<string, string> { ["scripts/a.scr"] = "sub main() {}" });
            Assert.Equal(["models/player/x.model", "scripts/a.scr"], r.Members);
            Assert.False(File.Exists(pak + ".partial"));
            using (var zip = ZipFile.OpenRead(pak))
                Assert.Equal(PyJson.Dumps(doc), new StreamReader(zip.GetEntry("models/player/x.model")!.Open()).ReadToEnd());
            using (var models = new ModelCatalog([pak]))
                Assert.Equal(6, models.Load(models.Find("x.model")!).Version);

            Assert.Throws<ModelFormatException>(() => PakWriter.Write(pak, new Dictionary<string, JsonNode> { ["y.model"] = doc }));
            foreach (var bad in new[] { "../x.model", "/x.model", "c:x.model", "a/ü.model", "" })
                Assert.Throws<ModelFormatException>(() =>
                    PakWriter.Write(pak, new Dictionary<string, JsonNode> { [bad] = doc }, overwrite: true));
            Assert.Throws<ModelFormatException>(() => PakWriter.Write(pak, new Dictionary<string, JsonNode>
                { ["A.model"] = doc, ["a.model"] = doc }, overwrite: true));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
