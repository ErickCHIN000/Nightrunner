using System.Text.Json.Nodes;
using Nightrunner.Core.Cast;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;
using CastModel = Nightrunner.Core.Cast.Model;
using File = System.IO.File;

namespace Nightrunner.Tests;

// Opt-in (compiled only with -p:OptInTests=true; see tools/parity/README.md): the one check against a real Blender
// re-export. NIGHTRUNNER_SPLIT_SMOKE = a Blender re-export (.cast/.glb/.gltf) of a prototype single-Cast export of
// player_kc_basic_tpp. The file is made by hand from game data, so it is not part of the repository.
public partial class ModelSplitTests
{
    private const string SmokeVariable = "NIGHTRUNNER_SPLIT_SMOKE";

    [Fact]
    [Trait("Category", "OptIn")]
    public void SmokeSplitOfAnEditedPrototypeScene()
    {
        string? path = Environment.GetEnvironmentVariable(SmokeVariable);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) Assert.Skip($"{SmokeVariable} not set");
        var c = scene.Get();
        var (_, rep) = PrototypeScene(c);
        // objects that are not export submeshes go to the first part whose mesh has every bone they are weighted to
        var edited = Gltf.Load(path);
        var mdl = edited.Roots()[0].ChildOfType<CastModel>()!;
        var bones = mdl.Skeleton()!.Bones().Select(b => b.Name()!.ToLowerInvariant()).ToList();
        var known = rep["mesh_map"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).ToHashSet();
        var parts = rep["parts"]!.AsArray().Select(p => p!.AsObject())
            .Select(p => c.Model(p["pack"]!.GetValue<string>(), p["index"]!.GetValue<int>()).Entities.Select(e => MeshModel.Text(e.Name).ToLowerInvariant()).ToHashSet())
            .ToList();
        var assign = new Dictionary<string, string>();
        foreach (var m in mdl.Meshes())
        {
            if (known.Contains(System.Text.RegularExpressions.Regex.Replace(m.Name()!, @"\.\d{3}$", ""))) continue;
            var wb = m.VertexWeightBoneBuffer() ?? [];
            var wv = m.VertexWeightValueBuffer() ?? [];
            var used = wb.Where((b, i) => wv[i] > 0).Select(b => bones[(int)b]).ToHashSet();
            int part = parts.FindIndex(p => used.IsSubsetOf(p));
            assign[m.Name()!] = part < 0 ? "" : rep["mesh_map"]!.AsArray().First(x => x!["part"]!.GetValue<int>() == part)!["name"]!.GetValue<string>();
        }
        var res = Split(c, path, "smoke", rep, new ModelSplitOptions { Assign = assign, Check = Encode });
        Assert.Empty(res.Problems);
        var applied = res.Meshes.Where(m => m.Submeshes.Any(s => s.Applied)).ToList();
        Assert.NotEmpty(applied);
        Assert.All(applied, m => Assert.True(m.Check is { Ok: true }, $"{m.Mesh}: {m.Check?.Error}"));
    }
}
