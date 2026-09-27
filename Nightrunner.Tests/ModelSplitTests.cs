using System.Text.Json.Nodes;
using Nightrunner.Core.Cast;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;
using CastMesh = Nightrunner.Core.Cast.Mesh;
using CastModel = Nightrunner.Core.Cast.Model;
using File = System.IO.File;

namespace Nightrunner.Tests;

// Port of nightrunner-main/tests/test_model_split.py (cast/split.py). The prototype's sample pack is replaced by the DLTB
// install: player_kc_basic_tpp is exported once per run with ModelCast (format /2) and split back. "Blender" is simulated
// as the prototype's test does: bp_* properties and tangents gone, triangles rotated, weight lanes reversed.
//
// A /1 file (the prototype's export, first-supplier rest) is rebuilt from the /2 export by re-baking every vertex with
// ModelSkeleton.Merge(partRigs: false): the splitter must undo each file with the rule it was baked with.
//
// Tiers: synthetic tests always run; install-backed ones skip without an install. The smoke test on a real Blender
// re-export (SmokeSplitOfAnEditedPrototypeScene) is opt-in: OptIn/ModelSplitSmokeTests.cs, tools/parity/README.md.
public partial class ModelSplitTests(ModelSplitTests.Scene scene) : IClassFixture<ModelSplitTests.Scene>
{
    private const string Hair = "HEADCOVER.sh_npc_ft_crane_hair_a.e0.s0";
    private const string HeadS0 = "HEAD.sh2_npc_crane.e0.s0";
    private const string HeadS1 = "HEAD.sh2_npc_crane.e0.s1";

    /// <summary>The DLTB catalogs and one /2 export of player_kc_basic_tpp, built on first use.</summary>
    public sealed class Scene : IDisposable
    {
        private readonly Lock _lock = new();
        private Ctx? _ctx;

        public sealed class Ctx(RpackCatalog rpacks, string dir, string castPath, JsonObject report) : IDisposable
        {
            private readonly Dictionary<(string, int), MeshModel> _models = [];
            public RpackCatalog Rpacks { get; } = rpacks;
            public string Dir { get; } = dir;
            public string CastPath { get; } = castPath;
            public JsonObject Report { get; } = report;

            public (IReadOnlyDictionary<byte, byte[]> Parts, MeshModel Mesh, string LogicalName) Source(string label, int index)
            {
                var entry = Rpacks.IndexedPacks.First(p => p.Label == label);
                var parts = MeshDecoder.ReadParts(entry.Pack!, index);
                return (parts.ByType, MeshDecoder.Decode(parts), entry.Pack!.Name(index));
            }

            public MeshModel Model(string label, int index)
            {
                lock (_models)
                    return _models.TryGetValue((label, index), out var m) ? m : _models[(label, index)] = Source(label, index).Mesh;
            }

            public MeshModel? SkeletonByName(string label, string name)
            {
                foreach (int gid in Rpacks.Lookup(name.EndsWith(".msh") ? name[..^4] : name, 0x10))
                {
                    var (e, i) = Rpacks.Split(gid);
                    if (e.Label == label) return Model(label, i);
                }
                return null;
            }

            public string Temp(string name) => Directory.CreateDirectory(Path.Combine(Dir, name)).FullName;

            public void Dispose()
            {
                Rpacks.Dispose();
                try { Directory.Delete(Dir, true); } catch (IOException) { }
            }
        }

        public Ctx Get()
        {
            var install = Installs.Require("dltb");
            lock (_lock)
            {
                if (_ctx is not null) return _ctx;
                var rpacks = new RpackCatalog();
                rpacks.LoadAsync(install.Rpacks(), install.Assets, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                using var models = new ModelCatalog(install.Paks());
                var cache = new Dictionary<int, MeshModel>();
                MeshModel Decode(int gid)
                {
                    lock (cache)
                    {
                        if (cache.TryGetValue(gid, out var m)) return m;
                        var (e, i) = rpacks.Split(gid);
                        return cache[gid] = MeshDecoder.Decode(e.Pack!, i);
                    }
                }
                var res = ModelResolver.Resolve(models.Load(models.Find("player_kc_basic_tpp.model")!), rpacks, null, Decode);
                string dir = Directory.CreateTempSubdirectory("nr-modelsplit-").FullName;
                var export = ModelCast.Write(res, rpacks, Decode, Path.Combine(dir, "export"), "player_kc_basic_tpp", textures: false);
                return _ctx = new Ctx(rpacks, dir, export.CastPath, export.Report);
            }
        }

        public void Dispose() => _ctx?.Dispose();
    }

    private static ModelSplitResult Split(Scene.Ctx c, string edited, string name, JsonObject? report = null, ModelSplitOptions? options = null)
    {
        return ModelSplit.Split(edited, report ?? c.Report, c.Temp(name), c.Source,
                                (options ?? new ModelSplitOptions()) with { SkeletonByName = c.SkeletonByName });
    }

    /// <summary>Re-save like the Blender exporter: no custom properties or tangents, rotated triangles, reversed weight lanes.</summary>
    private static string Blenderize(string src, string dst, Action<CastMesh, CastModel>? edit = null, Action<CastModel>? extra = null,
                                     bool drop = false)
    {
        var cast = CastFile.Load(src);
        var mdl = cast.Roots()[0].ChildOfType<CastModel>()!;
        foreach (var m in mdl.Meshes())
        {
            if (drop)
            {
                mdl.RemoveChild(m);
                continue;
            }
            foreach (var k in m.Properties.Keys.Where(k => k.StartsWith("bp_", StringComparison.Ordinal)).Append("vt").ToList())
                m.RemoveProperty(k);
            var f = m.FaceBuffer()!;
            var rf = new uint[f.Length];
            for (int i = 0; i < f.Length; i += 3) (rf[i], rf[i + 1], rf[i + 2]) = (f[i + 2], f[i], f[i + 1]);
            m.SetFaceBuffer(rf);
            int mi = m.MaximumWeightInfluence();
            var wb = m.VertexWeightBoneBuffer()!;
            var wv = m.VertexWeightValueBuffer()!;
            var rb = new uint[wb.Length];
            var rw = new float[wv.Length];
            for (int i = 0; i < wb.Length; i += mi)
                for (int k = 0; k < mi; k++) (rb[i + k], rw[i + k]) = (wb[i + mi - 1 - k], wv[i + mi - 1 - k]);
            m.SetVertexWeightBoneBuffer(rb);
            m.SetVertexWeightValueBuffer(rw);
            edit?.Invoke(m, mdl);
        }
        extra?.Invoke(mdl);
        cast.Save(dst);
        return dst;
    }

    private static byte[] Template(MeshModel model) => CastExport.Build(model, model.Name).Cast.ToBytes();

    private static MeshModel PartModel(Scene.Ctx c, JsonObject report, string mesh)
    {
        var pr = report["parts"]!.AsArray().Select(p => p!.AsObject()).First(p => p["mesh"]!.GetValue<string>() == mesh);
        return c.Model(pr["pack"]!.GetValue<string>(), pr["index"]!.GetValue<int>());
    }

    private static CastMesh Node(string castPath, string suffix) =>
        CastFile.Load(castPath).Roots()[0].ChildOfType<CastModel>()!.Meshes().Single(m => m.Name()!.EndsWith(suffix, StringComparison.Ordinal));

    private static int[] BoneIndices(CastModel mdl, params string[] names)
    {
        var bones = mdl.Skeleton()!.Bones().Select(b => b.Name()).ToList();
        return names.Select(n => bones.IndexOf(n)).ToArray();
    }

    // ---- synthetic --------------------------------------------------------------------------------------------------

    [Fact]
    public void FormatsAndRules()
    {
        Assert.Equal(ModelSplitRule.PartRigs, ModelSplit.RuleOf(ModelCast.Format));
        Assert.Equal(ModelSplitRule.FirstSupplier, ModelSplit.RuleOf("nightrunner.model_cast/1"));
        Assert.Equal(ModelSplitRule.FirstSupplier, ModelSplit.RuleOf("beastpack.model_cast/1"));
        Assert.Null(ModelSplit.RuleOf("nightrunner.model_cast/3"));
        Assert.Null(ModelSplit.RuleOf(null));
        string dir = Directory.CreateTempSubdirectory("nr-modelsplit-").FullName;
        try
        {
            var e = Assert.Throws<ModelSplitException>(() => ModelSplit.Split("missing.cast", new JsonObject { ["format"] = "x" }, dir,
                (_, _) => throw new InvalidOperationException("no source is read for a foreign report")));
            Assert.Contains("'x'", e.Message);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- /2: the C# export ------------------------------------------------------------------------------------------

    [Fact]
    public void UneditedSplitReproducesEachMeshCast()
    {
        var c = scene.Get();
        foreach (var (name, edited) in new[] { ("asis", c.CastPath), ("blender", Blenderize(c.CastPath, Path.Combine(c.Dir, "plain.cast"))) })
        {
            var res = Split(c, edited, name);
            Assert.Equal(ModelSplitRule.PartRigs, res.Rule);
            Assert.Empty(res.Problems);
            Assert.Empty(res.UnmatchedMeshes);
            Assert.Equal(14, res.Meshes.Count);
            var report = JsonNode.Parse(File.ReadAllText(res.ReportPath))!;
            Assert.Equal("part_rigs", report["rest_rule"]!.GetValue<string>());
            foreach (var m in res.Meshes)
            {
                Assert.Empty(m.Problems);
                Assert.All(m.Submeshes, s => Assert.True(s.Applied && s.Flag("in_place") && s.Stat("moved") == 0 && s.Stat("new") == 0, $"{name} {s.Name}: {s.Status}"));
                var model = PartModel(c, c.Report, m.Mesh);
                Assert.True(Template(model).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(m.Dir!, "model.cast"))), $"{name} {m.Mesh}");
                Assert.True(File.Exists(Path.Combine(m.Dir!, "mesh.json")));
                Assert.True(File.Exists(Path.Combine(m.Dir!, "image.bin")));
            }
        }
    }

    [Fact]
    public void MovedHeadVertexMovesOnlyThatVertex()
    {
        var c = scene.Get();
        const int vertex = 17;
        string edited = Blenderize(c.CastPath, Path.Combine(c.Dir, "head.cast"), (m, _) =>
        {
            if (m.Name() != HeadS0) return;
            var vp = m.VertexPositionBuffer()!;
            vp[vertex * 3 + 1] += 0.01f;
            m.SetVertexPositionBuffer(vp);
        });
        var res = Split(c, edited, "head");
        Assert.Empty(res.Problems);
        var head = res.Meshes.Single(m => m.Mesh == "sh2_npc_crane.msh");
        var sub = head.Submeshes.Single(s => s.Name == HeadS0);
        Assert.Equal((1, 0, true), (sub.Stat("moved"), sub.Stat("new"), sub.Flag("in_place")));
        var model = PartModel(c, c.Report, "sh2_npc_crane.msh");
        var before = CastFile.Read(Template(model)).Roots()[0].ChildOfType<CastModel>()!.Meshes().Single(m => m.Name()!.EndsWith(".e0.s0")).VertexPositionBuffer()!;
        var after = Node(Path.Combine(head.Dir!, "model.cast"), ".e0.s0").VertexPositionBuffer()!;
        Assert.Equal(before.Length, after.Length);
        var changed = Enumerable.Range(0, before.Length / 3).Where(i => Enumerable.Range(0, 3).Any(k => before[i * 3 + k] != after[i * 3 + k])).ToList();
        Assert.Equal([vertex], changed);
        // the head's own rig supplies its rest pose (/2): its rebind is ~identity, so the move comes back as it was made
        Assert.InRange(after[vertex * 3 + 1] - before[vertex * 3 + 1], 0.01 - 1e-4, 0.01 + 1e-4);
        Assert.InRange(Math.Abs(after[vertex * 3] - before[vertex * 3]) + Math.Abs(after[vertex * 3 + 2] - before[vertex * 3 + 2]), 0, 1e-4);
        foreach (var m in res.Meshes.Where(m => m != head))
            Assert.True(Template(PartModel(c, c.Report, m.Mesh)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(m.Dir!, "model.cast"))), m.Mesh);
    }

    [Fact]
    public void TopologyChangeAndForeignBone()
    {
        var c = scene.Get();
        string edited = Blenderize(c.CastPath, Path.Combine(c.Dir, "topo.cast"), (m, mdl) =>
        {
            if (m.Name()!.EndsWith("sh2_npc_ft_crane_beard_a.e0.s0", StringComparison.Ordinal))
                m.SetFaceBuffer(m.FaceBuffer()![60..]);               // 20 triangles gone, vertices left loose
            if (m.Name() == Hair)
            {
                int beard = BoneIndices(mdl, "sh2_npc_ft_crane_beard_a")[0];  // a beard-only bone
                var wb = m.VertexWeightBoneBuffer()!;
                var wv = m.VertexWeightValueBuffer()!;
                int lane = Enumerable.Range(0, 4).MaxBy(k => wv[k]);
                wb[lane] = (uint)beard;
                m.SetVertexWeightBoneBuffer(wb);
            }
        });
        var res = Split(c, edited, "topo");
        var beardSub = res.Meshes.Single(m => m.Mesh == "sh2_npc_ft_crane_beard_a.msh").Submeshes[0];
        Assert.True(beardSub.Applied, beardSub.Status);
        Assert.True(beardSub.Stat("loose_dropped") > 0);
        Assert.Equal(0, beardSub.Stat("new"));
        var hair = res.Meshes.Single(m => m.Mesh == "sh_npc_ft_crane_hair_a.msh");
        Assert.Contains(hair.Problems, p => p.Contains("sh2_npc_ft_crane_beard_a"));
        Assert.True(hair.Submeshes[0].Refused, hair.Submeshes[0].Status);
    }

    [Fact]
    public void StaleReportIsRefused()
    {
        var c = scene.Get();
        var rep = (JsonObject)c.Report.DeepClone();
        rep["bones"]!.AsArray().RemoveAt(rep["bones"]!.AsArray().Count - 1);
        Assert.Throws<ModelSplitException>(() => Split(c, c.CastPath, "stale", rep));
        rep["format"] = "x";
        Assert.Throws<ModelSplitException>(() => Split(c, c.CastPath, "stale", rep));
    }

    // ---- assign / hide / LOD follow / double-sided (AssignHideTests) -------------------------------------------------

    private static void Sphere(CastModel mdl, string name, int bone, (double X, double Y, double Z) center, double radius = 0.1)
    {
        const int U = 12, V = 8;
        var pts = new List<float>();
        var nrm = new List<float>();
        var uvs = new List<float>();
        for (int j = 0; j <= V; j++)
            for (int i = 0; i <= U; i++)
            {
                double th = Math.PI * j / V, ph = 2 * Math.PI * i / U;
                double nx = Math.Sin(th) * Math.Cos(ph), ny = Math.Cos(th), nz = Math.Sin(th) * Math.Sin(ph);
                pts.AddRange([(float)(center.X + radius * nx), (float)(center.Y + radius * ny), (float)(center.Z + radius * nz)]);
                nrm.AddRange([(float)nx, (float)ny, (float)nz]);
                uvs.AddRange([(float)i / U, (float)j / V]);
            }
        var faces = new List<uint>();
        for (int j = 0; j < V; j++)
            for (int i = 0; i < U; i++)
            {
                uint a = (uint)(j * (U + 1) + i), b = a + U + 1;
                faces.AddRange([a, b, a + 1, a + 1, b, b + 1]);
            }
        var m = mdl.CreateMesh();
        m.SetName(name);
        m.SetVertexPositionBuffer(pts.ToArray());
        m.SetVertexNormalBuffer(nrm.ToArray());
        m.SetUVLayerCount(1);
        m.SetVertexUVLayerBuffer(0, uvs.ToArray());
        m.SetFaceBuffer(faces.ToArray());
        m.SetMaximumWeightInfluence(1);
        var mat = mdl.CreateMaterial();
        mat.SetName("Blender_" + name.Split('.')[0]);
        m.SetMaterial(mat.Hash);
        m.SetVertexWeightBoneBuffer(Enumerable.Repeat((uint)bone, pts.Count / 3).ToArray());
        m.SetVertexWeightValueBuffer(Enumerable.Repeat(1f, pts.Count / 3).ToArray());
    }

    private static string Spheres(Scene.Ctx c, string name) => Blenderize(c.CastPath, Path.Combine(c.Dir, name + ".cast"), drop: true, extra: mdl =>
    {
        int head = BoneIndices(mdl, "head")[0];
        Sphere(mdl, "ball.001", head, (0, 1.7, 0));
        Sphere(mdl, "ball_b", head, (0, 1.8, 0), 0.05);
        Sphere(mdl, "junk", head, (0, 0, 0), 0.01);
    });

    [Fact]
    public void AssignMergeHideAndLods()
    {
        var c = scene.Get();
        var res = Split(c, Spheres(c, "assign"), "assign", options: new ModelSplitOptions
        {
            Assign = new Dictionary<string, string> { ["ball"] = Hair, ["ball_b"] = Hair, ["junk"] = "" },
            Hide = [HeadS1],
        });
        Assert.Empty(res.Problems);
        Assert.Empty(res.UnmatchedMeshes);
        var hair = res.Meshes.Single(m => m.LogicalName == "sh_npc_ft_crane_hair_a");
        var sub = hair.Submeshes.Single(s => s.Name == Hair);
        Assert.Equal(117 * 2, sub.Stat("new"));                   // two merged spheres
        Assert.True(sub.Flag("replaced"));
        var lod = hair.Submeshes.Where(s => s.Fields.GetValueOrDefault("lod_of") as string == Hair).ToList();
        Assert.Single(lod);                                          // e1 follows
        Assert.True(lod[0].Applied, lod[0].Status);
        var head = res.Meshes.Single(m => m.LogicalName == "sh2_npc_crane");
        var hid = head.Submeshes.Single(s => s.Name == HeadS1);
        Assert.True(hid.Flag("hidden"));
        Assert.Equal(1, hid.Stat("vertices"));
        Assert.All(head.Submeshes.Where(s => s != hid && s.Fields.GetValueOrDefault("lod_of") is null),
                   s => Assert.StartsWith("not in", s.Status));

        var ds = Split(c, Spheres(c, "ds"), "ds", options: new ModelSplitOptions
        {
            Assign = new Dictionary<string, string> { ["ball"] = Hair }, DoubleSided = ["ball"], Lods = false,
        });
        var dsHair = ds.Meshes.Single(m => m.LogicalName == "sh_npc_ft_crane_hair_a");
        Assert.DoesNotContain(dsHair.Submeshes, s => s.Fields.ContainsKey("lod_of"));
        var dsSub = dsHair.Submeshes.Single(s => s.Name == Hair);
        Assert.Equal((117 * 2, 192 * 2), (dsSub.Stat("new"), dsSub.Stat("faces")));
        Assert.Equal(["ball_b", "junk"], ds.UnmatchedMeshes.Order());

        Assert.Throws<ModelSplitException>(() => Split(c, Spheres(c, "bad"), "bad", options: new ModelSplitOptions
        {
            Assign = new Dictionary<string, string> { ["ball"] = "NOPE.e0.s0" },
        }));
    }

    // ---- /1: a prototype-era file ---------------------------------------------------------------------------------------

    /// <summary>
    /// The /2 export re-baked as the prototype bakes (<c>gui/modelcast.py</c>: every rest pose from the first supplier) and
    /// its report relabelled <c>/1</c>, with the skeleton's logical index as the prototype records it.
    /// </summary>
    private static (string Cast, JsonObject Report) PrototypeScene(Scene.Ctx c)
    {
        var rep = (JsonObject)c.Report.DeepClone();
        rep["format"] = ModelSplit.FormatV1;
        var sk = rep["skeleton"]!.AsObject();
        var parts = rep["parts"]!.AsArray().Select(p => p!.AsObject()).ToList();
        var models = parts.Select(p => c.Model(p["pack"]!.GetValue<string>(), p["index"]!.GetValue<int>())).ToList();
        string skName = sk["name"]!.GetValue<string>();
        foreach (int gid in c.Rpacks.Lookup(skName[..^4], 0x10))
            if (c.Rpacks.Split(gid).Entry.Label == sk["pack"]!.GetValue<string>()) sk["index"] = c.Rpacks.Split(gid).Index;
        var skeleton = c.SkeletonByName(sk["pack"]!.GetValue<string>(), skName)!;
        var skel = ModelSkeleton.Merge(skeleton, skName, parts.Select((p, i) => (p["mesh"]!.GetValue<string>(), models[i])).ToList(), partRigs: false);
        Assert.Empty(skel.Overrides);
        var partOf = rep["mesh_map"]!.AsArray().ToDictionary(x => x!["name"]!.GetValue<string>(), x => x!["part"]!.GetValue<int>());
        var cast = CastFile.Load(c.CastPath);
        foreach (var node in cast.Roots()[0].ChildOfType<CastModel>()!.Meshes())
        {
            var model = models[partOf[node.Name()!]];
            var ge = model.GeometryEntries[(int)node.Property("bp_entry")!.Integers[0]];
            var s = ge.Submeshes[(int)node.Property("bp_submesh")!.Integers[0]];
            if (ge.Vertices is not { Skinned: true } v) continue;
            var (rebind, _) = ModelCast.Rebind(model, skel, skel.Map(model));
            var ids = node.Property("bp_vertex_id")!.Integers;
            var pos = new float[ids.Length * 3];
            var nrm = new float[ids.Length * 3];
            for (int i = 0; i < ids.Length; i++)
            {
                int src = (int)ids[i];
                var blend = new double[16];
                double total = 0;
                for (int k = 0; k < 4; k++) total += v.Weights![src * 4 + k];
                for (int k = 0; k < 4; k++)
                {
                    float w = v.Weights![src * 4 + k];
                    if (!(w > 0)) continue;
                    var r = rebind[s.Palette[Math.Min((int)v.Joints![src * 4 + k], s.Palette.Length - 1)]];
                    for (int j = 0; j < 16; j++) blend[j] += w / Math.Max(total, 1e-12) * r[j];
                }
                double x = v.Positions[src * 3], y = v.Positions[src * 3 + 1], z = v.Positions[src * 3 + 2];
                pos[i * 3] = (float)(blend[0] * x + blend[1] * y + blend[2] * z + blend[3]);
                pos[i * 3 + 1] = (float)(blend[4] * x + blend[5] * y + blend[6] * z + blend[7]);
                pos[i * 3 + 2] = (float)(blend[8] * x + blend[9] * y + blend[10] * z + blend[11]);
                x = v.Normals[src * 3]; y = v.Normals[src * 3 + 1]; z = v.Normals[src * 3 + 2];
                double nx = blend[0] * x + blend[1] * y + blend[2] * z, ny = blend[4] * x + blend[5] * y + blend[6] * z, nz = blend[8] * x + blend[9] * y + blend[10] * z;
                double len = Math.Max(Math.Sqrt(nx * nx + ny * ny + nz * nz), 1e-12);
                (nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]) = ((float)(nx / len), (float)(ny / len), (float)(nz / len));
            }
            node.SetVertexPositionBuffer(pos);
            node.SetVertexNormalBuffer(nrm);
        }
        string path = Path.Combine(c.Dir, "v1.cast");
        cast.Save(path);
        return (path, rep);
    }

    [Fact]
    public void PrototypeFileSplitsWithTheFirstSupplierRest()
    {
        var c = scene.Get();
        var (v1, rep) = PrototypeScene(c);
        string edited = Blenderize(v1, Path.Combine(c.Dir, "v1_blender.cast"));
        var res = Split(c, edited, "v1", rep);
        Assert.Equal(ModelSplitRule.FirstSupplier, res.Rule);
        Assert.Equal("first_supplier", JsonNode.Parse(File.ReadAllText(res.ReportPath))!["rest_rule"]!.GetValue<string>());
        Assert.Empty(res.Problems);
        foreach (var m in res.Meshes)
        {
            Assert.All(m.Submeshes, s => Assert.True(s.Applied && s.Stat("moved") == 0 && s.Stat("new") == 0, $"{s.Name}: {s.Status} moved {s.Stat("moved")}"));
            Assert.True(Template(PartModel(c, rep, m.Mesh)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(m.Dir!, "model.cast"))), m.Mesh);
        }

        // undone with the other rule, the head (bound to its own face rig) does not come back
        var wrong = (JsonObject)rep.DeepClone();
        wrong["format"] = ModelCast.Format;
        var bad = Split(c, edited, "v1_as_v2", wrong);
        var head = bad.Meshes.Single(m => m.Mesh == "sh2_npc_crane.msh");
        Assert.True(head.Submeshes.Sum(s => s.Stat("moved") + s.Stat("new")) > 100);
    }

    // ---- the encoder check ------------------------------------------------------------------------------------------------

    private static ModelSplitCheckResult Encode(IReadOnlyDictionary<byte, byte[]> parts, string name, string cast, JsonObject? sidecar)
    {
        var r = MeshBuild.Build(parts, name, cast, sidecar, new MeshBuildOptions { IgnoreBoneChanges = true });
        var entries = r.Report["entries"] is JsonArray a ? a.Select(x => x?["path"]?.GetValue<string>()).ToList() : [];
        return new ModelSplitCheckResult(r.Parts, r.Warnings, entries);
    }

    [Fact]
    public void EncoderAcceptsTheSplit()
    {
        var c = scene.Get();
        string edited = Blenderize(c.CastPath, Path.Combine(c.Dir, "check.cast"), (m, _) =>
        {
            if (m.Name() != HeadS0) return;
            var vp = m.VertexPositionBuffer()!;
            vp[1] += 0.005f;
            m.SetVertexPositionBuffer(vp);
        });
        var res = Split(c, edited, "check", options: new ModelSplitOptions { Check = Encode });
        foreach (var m in res.Meshes)
        {
            Assert.NotNull(m.Check);
            Assert.True(m.Check!.Ok, $"{m.Mesh}: {m.Check.Error}");
            Assert.Equal(m.Mesh != "sh2_npc_crane.msh", m.Check.Unchanged);
        }
    }
}
