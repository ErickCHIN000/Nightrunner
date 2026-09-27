using System.Text;
using System.Text.Json;
using Nightrunner.Core.Cast;
using Nightrunner.Core.Games;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Rpack;
using CastMesh = Nightrunner.Core.Cast.Mesh;
using CastModel = Nightrunner.Core.Cast.Model;
using File = System.IO.File;

namespace Nightrunner.Tests;

// Port of nightrunner-main/tests/test_cast_export.py (cast/export.py) plus the per-mesh sidecar (mesh/sidecar.py).
// The prototype's sample pack is cut from shipped meshes: the samples are looked up by name in the DLTB install.
//
// Tiers: synthetic tests always run; install-backed ones skip without an install. The exact bytes of an export are frozen
// as SHA-256s (ExportBytesAreFrozen, SyntheticExportBytesAreFrozen), checked against the prototype when they were taken;
// the live comparison with the prototype is tools/ExportCheck (needs Python and the prototype checkout).
//
// Ported: test_quaternion_helpers, test_export_all_samples, test_build_cast_in_memory. test_identifiers_per_spec lives
// in CastLibTests.
public class CastExportTests
{
    internal static readonly string[] Samples =
    [
        "sh_npc_ft_crane_hair_a", "sh2_player_tpp_phx_skeleton", "dlc_ft_freak_banshee_clothes_matriarch",
        "gas_tank_pistol_anm", "wn_pistol_b_b", "dlc_ft_safe_zone_cable_e", "sh2_npc_aiden_beast", "sh2_npc_crane",
        "dummybox_025m", "npc_b_man_pants_b_holster_bag_c", "sh2_npc_ft_crane_beard_a", "anim_hammer_a",
        "bdp_ce_a_ornament_str_d", "alarm_siren_anm", "barrier", "ui_bg_ph_ft", "ui_bg_the_beast",
    ];

    /// <summary>DL2 meshes of the same checks (tools/ExportCheck's named DL2 picks).</summary>
    internal static readonly string[] Dl2Samples = ["dummy_box", "player_army_torso_a_tpp"];

    // ---- rotation helpers (test_quaternion_helpers) ----------------------------------------------------------------

    [Fact]
    public void QuaternionHelpers()
    {
        var eye = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        Assert.Equal([0.0, 0.0, 0.0, 1.0], CastExport.QuaternionXyzw(eye));
        var rz = new double[3, 3] { { 0, -1, 0 }, { 1, 0, 0 }, { 0, 0, 1 } };   // 90° about Z
        var q = CastExport.QuaternionXyzw(rz);
        double h = Math.Sqrt(0.5);
        AssertClose([0, 0, h, h], q, 1e-9);
        var scaled = new double[3, 3];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) scaled[i, j] = rz[i, j] * 2.5;
        AssertClose(Flat(rz), Flat(CastExport.PolarRotation(scaled)), 1e-12);
    }

    [Fact]
    public void PolarRotationIsProperAndQuaternionReproducesIt()
    {
        var rng = new Random(3);
        for (int n = 0; n < 200; n++)
        {
            var m = new double[3, 3];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) m[i, j] = rng.NextDouble() * 2 - 1;
            var r = CastExport.PolarRotation(m);
            // orthonormal, det +1
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    double d = r[0, i] * r[0, j] + r[1, i] * r[1, j] + r[2, i] * r[2, j];
                    Assert.Equal(i == j ? 1 : 0, d, 1e-12);
                }
            Assert.Equal(1, Det(r), 1e-12);
            var q = CastExport.QuaternionXyzw(r);
            Assert.True(q[3] >= 0);
            AssertClose(Flat(r), Flat(FromQuaternion(q)), 1e-12);
        }
        // a reflection's closest proper rotation, a 180° turn (w = 0) and a singular matrix
        var mirror = new double[3, 3] { { -1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        Assert.Equal(1, Det(CastExport.PolarRotation(mirror)), 1e-12);
        var flip = new double[3, 3] { { 1, 0, 0 }, { 0, -1, 0 }, { 0, 0, -1 } };
        var qf = CastExport.QuaternionXyzw(flip);
        AssertClose([1, 0, 0, 0], qf.Select(Math.Abs).ToArray(), 1e-12);
        var zero = new double[3, 3];
        AssertClose(Flat(new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } }), Flat(CastExport.PolarRotation(zero)), 0);
        var nan = new double[3, 3] { { double.NaN, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        Assert.Throws<MeshUnsupportedException>(() => CastExport.PolarRotation(nan));
    }

    [Fact]
    public void CastTextReplacesInvalidUtf8()
    {
        Assert.Equal("dlc_ft_ce_a_hatch_1x_a_anm�", CastExport.CastText(Encoding.ASCII.GetBytes("dlc_ft_ce_a_hatch_1x_a_anm").Append((byte)0x8C).ToArray()));
        Assert.Equal("a�b", CastExport.CastText("a\uD800b"));
    }

    // ---- JSON as the prototype's dump_json writes it -----------------------------------------------------------------

    [Fact]
    public void PythonFloatRepr()
    {
        (double, string)[] cases =
        [
            (1.0, "1.0"), (-2.5, "-2.5"), (0.0, "0.0"), (-0.0, "-0.0"), ((double)0.1f, "0.10000000149011612"),
            (1e-05, "1e-05"), (1.5e-07, "1.5e-07"), (0.0001, "0.0001"), (1e16, "1e+16"), (1234567890123456.0, "1234567890123456.0"),
            (123456789012345680.0, "1.2345678901234568e+17"), (double.NaN, "NaN"), (double.PositiveInfinity, "Infinity"),
            (double.NegativeInfinity, "-Infinity"), (1e300, "1e+300"), (5e-324, "5e-324"), (100.0, "100.0"), (0.5, "0.5"),
            ((double)3.4028235e38f, "3.4028234663852886e+38"), ((double)-1.1754944e-38f, "-1.1754943508222875e-38"),
            (Math.Pow(2, -25), "2.9802322387695312e-08"),   // .NET "R" gives 2.980232238769531E-08, which is not this double
        ];
        foreach (var (v, s) in cases) Assert.Equal(s, MeshSidecar.PyFloat(v));
    }

    [Fact]
    public void JsonLayoutMatchesDumpJson()
    {
        var doc = new OrderedDictionary<string, object?>
        {
            ["a"] = 1, ["b"] = new List<object?> { 1.5, "x\"\\\n\u0001é", null, true }, ["c"] = new OrderedDictionary<string, object?>(),
            ["d"] = new List<object?>(), ["e"] = new OrderedDictionary<string, object?> { ["k"] = new List<object?> { new List<object?> { 2 } } },
        };
        const string expected = "{\n  \"a\": 1,\n  \"b\": [\n    1.5,\n    \"x\\\"\\\\\\n\\u0001é\",\n    null,\n    true\n  ],\n" +
                                "  \"c\": {},\n  \"d\": [],\n  \"e\": {\n    \"k\": [\n      [\n        2\n      ]\n    ]\n  }\n}\n";
        Assert.Equal(expected, MeshSidecar.ToJson(doc));
    }

    // ---- synthetic mesh (always runs) ---------------------------------------------------------------------------------

    [Fact]
    public void SyntheticSkinnedMeshExports()
    {
        foreach (int fmt in new[] { 6, 8 })
        {
            var (img, fx, vb, ib) = MeshSynth.SkinnedDl2(fmt);
            var m = MeshDecoder.Decode("synth_skinned", img, fx, vb, ib);
            var (cast, rep) = CastExport.Build(m, "synth_skinned");
            CheckAgainstModel(CastFile.Read(cast.ToBytes()), rep, m, "synth_skinned");
            var side = MeshSidecar.Build(m, cast: MeshSidecar.CastInfo("synth_skinned.cast", rep));
            using var parsed = JsonDocument.Parse(MeshSidecar.ToJson(side));
            Assert.Equal("dl2", parsed.RootElement.GetProperty("layout").GetString());
            Assert.Equal(m.Entities.Length, parsed.RootElement.GetProperty("entities").GetArrayLength());
        }
    }

    [Fact]
    public void UnownedEntryWritesAllOnesOwner()
    {
        var (img, fx, vb, ib) = MeshSynth.SkinnedDl2();
        var m = MeshDecoder.Decode("synth_skinned", img, fx, vb, ib);
        m.GeometryEntries[0].OwnerEntity = null;   // the prototype's struct.pack('I', -1) fails here
        var (cast, _) = CastExport.Build(m, "synth_skinned");
        var mesh = CastFile.Read(cast.ToBytes()).RootNodes[0].ChildOfType<CastModel>()!.Meshes()[0];
        Assert.Equal([CastExport.NoOwner], mesh.Property("bp_owner_entity")!.Integers);
        var side = MeshSidecar.ToJson(MeshSidecar.Build(m));
        Assert.Contains("\"owner_entity\": null", side);
    }

    // ---- install-backed (test_export_all_samples, test_build_cast_in_memory) -------------------------------------

    [Fact]
    public void ExportAllSamples()
    {
        foreach (var name in Samples)
        {
            var (m, p) = MeshFixtures.Load("dltb", name);
            var (cast, rep) = CastExport.Build(m, p.Name);
            CheckAgainstModel(CastFile.Read(cast.ToBytes()), rep, m, p.Name);
        }
    }

    [Fact]
    public void BuildCastInMemory()
    {
        var (m, p) = MeshFixtures.Load("dltb", "sh_npc_ft_crane_hair_a");
        var (_, rep) = CastExport.Build(m, p.Name);
        Assert.Equal(2, rep.Nodes["mesh"]);
        Assert.Equal(["sh_npc_ft_crane_hair_a.e0.s0", "sh_npc_ft_crane_hair_a.e1.s0"], rep.Meshes.Select(x => x.Name));
        Assert.True(rep.BoneFrameResidual < 1e-3);
    }

    /// <summary>The head (DLTB sh2_npc_crane) per mesh: positions read back by bp_vertex_id equal the decoded ones.</summary>
    [Fact]
    public void CraneHeadPositionsByVertexId()
    {
        var (m, p) = MeshFixtures.Load("dltb", "sh2_npc_crane");
        string dir = Path.Combine(Path.GetTempPath(), "nightrunner-tests", Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "sh2_npc_crane.cast");
            var (rep, sidecar) = CastExport.WriteFiles(m, p.Name, path);
            Assert.True(File.Exists(path) && File.Exists(sidecar));
            Assert.False(File.Exists(path + ".tmp"));
            var meshes = CastFile.Load(path).RootNodes[0].ChildOfType<CastModel>()!.Meshes();
            Assert.Equal(rep.Meshes.Count, meshes.Count);
            Assert.NotEmpty(meshes);
            long checkedVertices = 0;
            foreach (var mesh in meshes)
            {
                var e = m.GeometryEntries[(int)mesh.Property("bp_entry")!.Integers[0]];
                var ids = mesh.Property("bp_vertex_id")!.Integers;
                var vp = mesh.VertexPositionBuffer()!;
                Assert.Equal(ids.Length * 3, vp.Length);
                for (int i = 0; i < ids.Length; i++)
                    for (int k = 0; k < 3; k++)
                        Assert.True(Math.Abs(vp[i * 3 + k] - e.Vertices!.Positions[ids[i] * 3 + k]) <= 1e-4,
                                    $"{mesh.Name()} vertex {ids[i]} axis {k}");
                checkedVertices += ids.Length;
            }
            Assert.True(checkedVertices > 1000);
            using var side = JsonDocument.Parse(File.ReadAllText(sidecar!));
            Assert.Equal("sh2_npc_crane.cast", side.RootElement.GetProperty("cast").GetProperty("file").GetString());
            var raw = Convert.FromBase64String(side.RootElement.GetProperty("geometry_entries")[0].GetProperty("vertex_raw_b64").GetString()!);
            Assert.Equal(m.GeometryEntries[0].Vertices!.Raw, raw);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ---- frozen export bytes -------------------------------------------------------------------------------------------

    /// <summary>
    /// SHA-256 of the port's <c>.cast</c> and <c>.mesh.json</c> per sample, as `nr mesh export` writes them, with the
    /// sidecar's <c>source.pack</c> given as the pack's file name (the prototype records the absolute path) and
    /// <c>cast.file</c> = <c>&lt;name&gt;.cast</c>. Checked 2026-09-27 against the prototype (tools/ExportCheck on the same
    /// meshes): every <c>.mesh.json</c> byte-identical given the same two strings; every <c>.cast</c> byte-identical or
    /// equal but for bone rotation components within 4e-14 (numpy's LAPACK vs the port's Jacobi eigenvectors), so the
    /// Cast hashes are the port's own bytes. A mismatch message lists the current values in this table's form.
    /// </summary>
    private static readonly Dictionary<string, (string Cast, string Sidecar)> Golden = new()
    {
        ["dltb/alarm_siren_anm"] = ("aeb96805e54f635f2fb29edad3b2e62fdf41ac2b59f4545fa3b794701b642dd1", "bfef96fc5c8391d9c158fc8a95792f3c6d03847b799e0ac3352d1f5c35875fc1"),
        ["dltb/anim_hammer_a"] = ("270e570e3dfab768ae1d77eb5d159e47a4806de4c27e6a743429c7b7179d38e6", "620024ce42a822440d5f948c1a9507424931113878257d09a4478058ff2f6af8"),
        ["dltb/barrier"] = ("1075c38e85ad67fd40315a8ac0f017a3a06289db29b5baadf8b3465e6b752fb5", "ac4429dadc899ad9c48d33801119a772839672176c1696ec949ce9b1ddecf9a9"),
        ["dltb/bdp_ce_a_ornament_str_d"] = ("d5eb48a57fe87a0f00bbd9811cdfcfec932148c43d8c3016e5cac96b4808f8e7", "cc3de35f960d846f127d249ee627bf6fb012fef90f95416e3845d51d4e6e64f9"),
        ["dltb/dlc_ft_freak_banshee_clothes_matriarch"] = ("c6d559926a0d43371b23a64f8e23c307792daca4e0d5d03ab7cd91b3ef314c0c", "0c8aa9a54c5d1eed8b11f15a68fd0974e23d3247abb0ddf814a71fa0e77319d2"),
        ["dltb/dlc_ft_safe_zone_cable_e"] = ("76a66921c00269d7c670724bd3cce362513a9cbf64dcb92259a35562c725c7cc", "4e9008acd34a62707aef7e74fdbc122049794beca33bde1a10e027271f8379b7"),
        ["dltb/dummybox_025m"] = ("7614169483e583671db03767853414ab4d4ea56be7a8d71460bc2fc53cd789e3", "630583aa556f544eeae997a1db8176c14372f45a47f7fb7fbac737dfea9af494"),
        ["dltb/gas_tank_pistol_anm"] = ("8730653267e5e8ab63fac9f00744b15414221e56285d0f661a7be0bdad43cb0c", "290768f026c30db97aa7e4f2aa21c73305c783f536502e451f5595c5fffd759e"),
        ["dltb/npc_b_man_pants_b_holster_bag_c"] = ("a80bc098ca53a6f5be251c61466a15c556baa7bdf7b1d87657f03cc12366416d", "ad784cb1be9220eb160b8e4ef4f9daca73da14b77be275bbe624c18e0ce23821"),
        ["dltb/sh_npc_ft_crane_hair_a"] = ("682a3d7af4c9e746ddc047f2efa30fb8fd7a2cc35744d8bbc7c0a6ce72ca4c09", "0450f80a97698b2994d7a0d2c261bda656112c74775c5986f4bd5c27a7332d3c"),
        ["dltb/sh2_npc_aiden_beast"] = ("3410babdff3a3124e87da3de06d1b18dac228e4a5bb1cc770420932a4e15b734", "cee76f590e2c82e8e0e6b029488e71841e0f6a24fb47f2bbbf990f3a2daa9952"),
        ["dltb/sh2_npc_crane"] = ("5958985a3fee422940120727ad2a57847b5eadbfea5d0679c0c2e814ad87ffd7", "915ca94f0d0e29aa36210eb16e5285240d88599200ca52cdbfe45c5440aa4a1e"),
        ["dltb/sh2_npc_ft_crane_beard_a"] = ("d0fe9e25511e81cc1b545134d93e65ef6b3536aa69674795810a485aaa481b23", "ac753a97c3cd6a23745d198b5b3c9cb8c01aac63aac15945582cc4b3540cb984"),
        ["dltb/sh2_player_tpp_phx_skeleton"] = ("a62f6108a4f264aa6cf578dec309cb7661369e1ad3c92e744c6b4e7f8aa992ca", "33efd83cc52024938a67b11b16c05837be6263aa5f3a8e9179d89e067474720c"),
        ["dltb/ui_bg_ph_ft"] = ("47ccb4c11f0edc0f14f40a16cbdcd5a3af70e12559aa21b38906567cbeadf0a0", "f9f4cbcd1d3866bc6d4c52cb93047f30a8aed7c6965ba4ac37064a8a504d6e07"),
        ["dltb/ui_bg_the_beast"] = ("82bb46eeda381c6a3f64c5c1111dddf2f2b828c66d38bb5a6fb519534a168ab7", "029f19690b36a143607c84978824edcf48bfa3438bfad8851df576d4adf2eab7"),
        ["dltb/wn_pistol_b_b"] = ("a2e129ff7b4e1dd004cd4fb1e9e5f94290e5fd842fb5904612912aa221bd388d", "85b174bb0ea4db6f29b2f2c4fc1fb027236695fed444f176df1e1df1ed8e70a7"),
        ["dl2/dummy_box"] = ("433a0276b836443678482546de35ddaa61368560b6b9a296c73f1e661bc1b9d0", "e61084a0f8f4c30f411433eb2491d17de39907ff30e1dce300eaa3a34fca0d3a"),
        ["dl2/player_army_torso_a_tpp"] = ("1ead0cf748c944285588cb35c60ecfff189da0e8a6c72e6eb4225414f9c88c42", "de3a6001247eab9e79d52cfa9d406c4db8068cc86b7966ed53d3a440419e9a74"),
    };

    /// <summary>MeshSynth.SkinnedDl2(fmt) exported by the prototype (export_cast + build_sidecar, fresh hash counter,
    /// no source): the port writes the same bytes.</summary>
    private static readonly Dictionary<int, (string Cast, string Sidecar)> GoldenSynthetic = new()
    {
        [6] = ("c1170a4b53e3ab66b2c9215bd828fda9a794962896cbad767a599e53407a64d3", "4efc4a1f9a4c32ef565b20f79ee9a344c36da6dcde8b4348b7fa1134a88ae182"),
        [8] = ("28044fce038b9256fba02698dc7b3ea6c7b0f98c3e0bb30f463a93cfb57a3f94", "dce7fcbf4d6f67dae0295e596b0b5f4af318be9e03154979afc9ca8c7255b623"),
    };

    private static string Sha(byte[] b) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(b));

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void SyntheticExportBytesAreFrozen(int fmt)
    {
        var (img, fx, vb, ib) = MeshSynth.SkinnedDl2(fmt);
        var m = MeshDecoder.Decode("synth_skinned", img, fx, vb, ib);
        var (cast, rep) = CastExport.Build(m, "synth_skinned");
        var side = MeshSidecar.ToJson(MeshSidecar.Build(m, cast: MeshSidecar.CastInfo("synth_skinned.cast", rep)));
        Assert.Equal(GoldenSynthetic[fmt], (Sha(cast.ToBytes()), Sha(Encoding.UTF8.GetBytes(side))));
    }

    public static TheoryData<string, string> ExportedMeshes()
    {
        var d = new TheoryData<string, string>();
        foreach (var n in Samples) d.Add("dltb", n);
        foreach (var n in Dl2Samples) d.Add("dl2", n);
        return d;
    }

    [Theory]
    [MemberData(nameof(ExportedMeshes))]
    public void ExportBytesAreFrozen(string game, string name)
    {
        var (pack, index) = MeshFixtures.Locate(game, name);
        var p = MeshFixtures.Parts(game, name);
        var m = MeshDecoder.Decode(p);
        var (cast, rep) = CastExport.Build(m, p.Name);
        var side = MeshSidecar.ToJson(MeshSidecar.Build(m, cast: MeshSidecar.CastInfo(p.Name + ".cast", rep),
                                                        source: new MeshSidecarSource(Path.GetFileName(pack), index, p.Name)));
        var actual = (Sha(cast.ToBytes()), Sha(Encoding.UTF8.GetBytes(side)));
        string key = $"{game}/{name}";
        Assert.True(Golden.TryGetValue(key, out var want) && want == actual,
                    $"{key}: the export changed, or the install's copy of the mesh did (a game update). Now:\n" +
                    $"        [\"{key}\"] = (\"{actual.Item1}\", \"{actual.Item2}\"),");
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static void CheckAgainstModel(CastFile c, CastExportReport rep, MeshModel m, string name)
    {
        var counts = CastExport.NodeCounts(c);
        Assert.Equal(rep.Nodes.Where(kv => kv.Value != 0).ToDictionary(), counts);
        Assert.Equal(m.Entities.Length, counts["bone"]);
        Assert.Equal(m.GeometryEntries.Sum(e => e.Submeshes.Count(s => e.Vertices is not null && s.IndexCount != 0)), counts.GetValueOrDefault("mesh"));
        Assert.Equal(m.GeometryEntries.SelectMany(e => e.Submeshes).Select(s => s.MaterialSlot).Distinct().Count(), counts.GetValueOrDefault("matl"));
        var root = c.RootNodes[0];
        Assert.Equal("y", root.ChildOfType<Metadata>()!.UpAxis());
        var mdl = root.ChildOfType<CastModel>()!;
        Assert.Equal(name, mdl.Name());
        var bones = mdl.Skeleton()!.Bones();
        for (int i = 0; i < bones.Count; i++)
        {
            Assert.Equal(m.Entities[i].NameStr, bones[i].Name());
            Assert.Equal(m.Entities[i].Parent, bones[i].ParentIndex());
            Assert.Equal((uint)i, bones[i].Property("bp_entity")!.Integers[0]);
        }
        foreach (CastMesh mesh in mdl.Meshes())
        {
            int entry = (int)mesh.Property("bp_entry")!.Integers[0], sub = (int)mesh.Property("bp_submesh")!.Integers[0];
            var e = m.GeometryEntries[entry];
            var s = e.Submeshes[sub];
            var v = e.Vertices!;
            Assert.Equal($"{name}.e{entry}.s{sub}", mesh.Name());
            Assert.Equal((byte)e.Format, mesh.Property("bp_format")!.Bytes[0]);
            var ids = mesh.Property("bp_vertex_id")!.Integers;
            var used = s.Indices.Select(x => (uint)x).Distinct().Order().ToArray();
            Assert.Equal(used, ids);
            Assert.Equal(used.Length, mesh.VertexCount());
            Assert.Equal(s.IndexCount / 3, mesh.FaceCount());
            var vp = mesh.VertexPositionBuffer()!;
            for (int i = 0; i < used.Length; i++)
                for (int k = 0; k < 3; k++)
                {
                    float want = v.Positions[used[i] * 3 + k];
                    if (float.IsFinite(want)) Assert.Equal(want, vp[i * 3 + k]);
                }
            var faces = mesh.FaceBuffer()!;
            Assert.Equal(s.Indices.Select(x => (uint)x), faces.Select(f => used[f]));
            Assert.Equal(m.MaterialName(s.MaterialSlot), (mesh.Material() as Material)!.Name());
            Assert.Equal(v.Uv1 is not null ? 2 : 1, mesh.UVLayerCount());
            if (v.Skinned)
            {
                Assert.Equal(4, mesh.MaximumWeightInfluence());
                var wb = mesh.VertexWeightBoneBuffer()!;
                var wv = mesh.VertexWeightValueBuffer()!;
                Assert.Equal(used.Length * 4, wb.Length);
                Assert.All(wb, b => Assert.True(b < m.Entities.Length));
                for (int i = 0; i < used.Length; i++)
                {
                    Assert.Equal(1.0, wv[i * 4] + (double)wv[i * 4 + 1] + wv[i * 4 + 2] + wv[i * 4 + 3], 1e-5);
                    for (int k = 0; k < 4; k++)
                        if (wv[i * 4 + k] > 0) Assert.Equal(s.Palette[v.Joints![used[i] * 4 + k]], wb[i * 4 + k]);
                }
            }
            else Assert.Null(mesh.VertexWeightBoneBuffer());
            Assert.All(mesh.Property("bp_tangent_sign")!.Floats, x => Assert.True(x is 1f or -1f));
        }
    }

    private static double[] Flat(double[,] m) => m.Cast<double>().ToArray();

    private static void AssertClose(double[] want, double[] got, double tol)
    {
        Assert.Equal(want.Length, got.Length);
        for (int i = 0; i < want.Length; i++) Assert.True(Math.Abs(want[i] - got[i]) <= tol, $"[{i}] {want[i]} vs {got[i]}");
    }

    private static double Det(double[,] m) =>
        m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
        + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

    private static double[,] FromQuaternion(double[] q)
    {
        double x = q[0], y = q[1], z = q[2], w = q[3];
        return new double[3, 3]
        {
            { 1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y) },
            { 2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x) },
            { 2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y) },
        };
    }
}
