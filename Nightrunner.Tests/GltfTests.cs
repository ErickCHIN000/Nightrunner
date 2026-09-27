using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Nightrunner.Core.Cast;
using CastMesh = Nightrunner.Core.Cast.Mesh;
using CastModel = Nightrunner.Core.Cast.Model;
using Json = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace Nightrunner.Tests;

// Port of cast/gltf.py checked against the prototype's own output.
//
// Always-run tier: GltfSynth rebuilds, call for call, the three Cast trees the prototype fixture script builds (skinned
// with textures and every oddity the writer handles; 300 bones; static without metadata) and the hand-written "foreign"
// glTF it reads (helper armature, matrix nodes, a second skin, interleaved and normalised accessors, a data: URI). The
// SHA-256 of each file the prototype wrote is recorded below: the .cast input, the .glb / .gltf / .bin it wrote from it
// (saved from the loaded .cast, so the values are the float32 ones both sides hold) and the .cast load_scene made of
// each. Every written file also goes through a structural check (GltfCheck). The script is tools/parity/gltf_fixtures.py;
// the live comparison with its output on game data is opt-in (OptIn/GltfParityTests.cs, tools/parity/README.md).
//
// Ported from tests/test_gltf.py: test_document_shape (install-backed, on the port's export of DLTB wn_pistol_b_b). Not ported:
// test_every_sample_round_trips_byte_identical, test_blender_like_edit_through_rpx_build, test_glb_export_and_split
// (they go through the mesh encoder, the rpx build and split_model_cast) and SdbSwitchTests (GUI context).
public partial class GltfTests
{
    // sha256 of what the prototype wrote (Windows: the .gltf text has CRLF line ends, as Python's text mode writes them).
    // *.read.cast: what load_scene made, saved, with -0.0 bone rotation components written as +0.0 — numpy's LAPACK
    // eigenvectors carry signed zeros the C# Jacobi does not; that is the only difference in these files.
    private static readonly Dictionary<string, string> Python = new()
    {
        ["synth_skinned.cast"] = "6007a48fe5a2bf448cb641d2003fbcb7b1ba32bc1ef46572d6f27cf49c37e6ae",
        ["synth_skinned.glb"] = "93b570fa13d9ba396d4a945b74ae3661558baaf8c78598b77f5ea1849fc49a74",
        ["synth_skinned.gltf"] = "3cee7ac80d8890b0a03f34b6c7cd03016107d329aef61ecb98b62ee6c1157d94",
        ["synth_skinned.bin"] = "67f4924a06e6e4a50af495ff0552435df2e710301eb1f2e71c056290d5fccbd2",
        ["synth_skinned.read.cast"] = "6bf32c207b62c4ce01714c6c545fcf89fecdb23108380d44be8a5ff61d9e4b6f",
        ["synth_bones300.cast"] = "7a6688fb7129ad142602a6dec23b2a24e8eb1bdb6de0b07d2f3693ad935e69b2",
        ["synth_bones300.glb"] = "1723281bbafc244827fee80c8c7bf1622b8422965d6c8adfb0f4c385e092f3ad",
        ["synth_bones300.gltf"] = "763e2187c63b5bd753486b70440eb090aaddc811d60ba5c5127d2abac92a82d7",
        ["synth_bones300.bin"] = "b0c49daf342fa643e89d84c8f5122ca1261dc64dbd2b00ea996eb71d655875f1",
        ["synth_bones300.read.cast"] = "ec08dc8a810316bb4352ad456a83c1c44ffbe7f4736f9c4e5f9fe18d885cdee9",
        ["synth_static.cast"] = "c48d9d6d55ff62c62751808f88f11fe377e75814b8a6b8677e9294fcee6dd46a",
        ["synth_static.glb"] = "41c94c154cfb6c7b8ef7b6e68c11656cdff7edd1b9652158d8046257119d1b53",
        ["synth_static.gltf"] = "6aa6172e550e86333e8d01b781f5019099d5e5e10bca2659aa14eaefdf82205c",
        ["synth_static.bin"] = "d5f51791fc8c780c39d411cad001b84237a39f54db7f64b2d0263598c58c1326",
        ["synth_static.read.cast"] = "befcd30a78afddc92f9d0e1bc7e62ee95a4de625bf179ead7d00c5970d8fe3bb",
        ["foreign.read.cast"] = "7b7abc9791a2908137785048528c55e919775c4774677b01a2a69b9164cfe3e0",
    };

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    /// <summary>Bone rotations with −0.0 components written as +0.0 (in place).</summary>
    private static CastFile ZeroSigned(CastFile cast)
    {
        foreach (var mdl in cast.RootNodes[0].ChildrenOfType<CastModel>())
            foreach (var b in mdl.Skeleton()?.Bones() ?? [])
                foreach (var k in new[] { "lr", "wr" })
                    if (b.Property(k) is { } p) p.SetValues(Array.ConvertAll(p.Floats, x => x == 0 ? 0f : x));
        return cast;
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "nightrunner-gltf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // ---- always-run: the prototype's bytes --------------------------------------------------------------------

    [Theory]
    [InlineData("synth_skinned")]
    [InlineData("synth_bones300")]
    [InlineData("synth_static")]
    public void SynthWritesPrototypeBytes(string name)
    {
        var dir = TempDir();
        try
        {
            GltfSynth.WriteTextures(dir);
            var cast = GltfSynth.Build(name);
            Assert.Equal(Python[name + ".cast"], Sha(cast.ToBytes()));
            foreach (var ext in new[] { "glb", "gltf" })
            {
                var path = Path.Combine(dir, $"{name}.{ext}");
                Gltf.Save(cast, path);
                Assert.Equal(Python[$"{name}.{ext}"], Sha(System.IO.File.ReadAllBytes(path)));
                Assert.Empty(GltfCheck.Validate(path));
                // read back → the Cast load_scene makes of the same file
                Assert.Equal(Python[name + ".read.cast"], Sha(ZeroSigned(Gltf.Load(path)).ToBytes()));
            }
            Assert.Equal(Python[name + ".bin"], Sha(System.IO.File.ReadAllBytes(Path.Combine(dir, name + ".bin"))));
            Assert.False(System.IO.File.Exists(Path.Combine(dir, name + ".glb.partial")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(".gltf")]
    [InlineData(".glb")]
    public void ForeignGltfReadsAsPrototype(string ext)
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "foreign" + ext);
            if (ext == ".gltf") System.IO.File.WriteAllText(path, GltfSynth.ForeignJson());
            else System.IO.File.WriteAllBytes(path, GltfSynth.ForeignGlb());
            var cast = Gltf.Load(path);
            Assert.Equal(Python["foreign.read.cast"], Sha(ZeroSigned(cast).ToBytes()));
            var mdl = cast.Roots()[0].ChildOfType<CastModel>()!;
            Assert.Equal("42", cast.Roots()[0].ChildOfType<Metadata>()!.Software());   // str(generator)
            Assert.Equal(["hips", "joint_1", "hand"], mdl.Skeleton()!.Bones().Select(b => b.Name()));
            Assert.Equal(["body", "body#2", "body2", "body2#2", "mesh_7"], mdl.Meshes().Select(m => m.Name()));
            var hips = mdl.Skeleton()!.Bones()[0];
            Assert.Equal("{'a': [1, None]}", hips.Property("bp_obj")!.StringValue);
            Assert.Equal([1f, 2.5f], hips.Property("bp_list")!.Floats);
            Assert.Equal([1, 0], hips.Property("bp_bools")!.Bytes);
            Assert.Null(hips.Property("other"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- always-run: behaviour --------------------------------------------------------------------------------

    [Fact]
    public void RoundTripKeepsGeometry()
    {
        var dir = TempDir();
        try
        {
            var cast = GltfSynth.Build("synth_skinned");
            var src = cast.Roots()[0].ChildOfType<CastModel>()!;
            foreach (var ext in new[] { "glb", "gltf" })
            {
                var path = Path.Combine(dir, "rt." + ext);
                Gltf.Save(cast, path);
                var back = Gltf.Load(path).Roots()[0].ChildOfType<CastModel>()!;
                Assert.Equal("synth", back.Name());
                Assert.Equal(src.Skeleton()!.Bones().Select(b => b.ParentIndex() is var p && p >= 0 && p < 5 ? p : -1),
                             back.Skeleton()!.Bones().Select(b => b.ParentIndex()));
                var quad = back.Meshes()[0];
                var sq = src.Meshes()[0];
                Assert.Equal(sq.VertexPositionBuffer(), quad.VertexPositionBuffer());
                Assert.Equal(sq.FaceBuffer()!, quad.FaceBuffer()!);
                Assert.Equal(sq.Property("bp_vertex_id")!.Integers, quad.Property("bp_vertex_id")!.Integers);
                Assert.Equal([1f, -1f, 1f, -1f], quad.Property("bp_tangent_sign")!.Floats);
                // the inf / NaN UVs come back exactly
                Assert.Equal(Bits(sq.VertexUVLayerBuffer(0)!), Bits(quad.VertexUVLayerBuffer(0)!));
                Assert.Equal("00ff", quad.Property("bp_raw_00")!.StringValue);
                Assert.Equal([7u, 8u], quad.Property("bp_h")!.Integers);
                Assert.IsType<Material>(quad.Material());
                Assert.Equal("body.mat", ((Material)quad.Material()!).Name());
                Assert.Equal("mask", ((Material)quad.Material()!).Property("bp_alpha_mode")!.StringValue);
                // top-4 of six influences, renormalised
                var six = back.Meshes()[1];
                Assert.Equal(4, six.MaximumWeightInfluence());
                Assert.Equal([1f, 0f, 0f, 0f], six.VertexWeightValueBuffer()![8..12]);
                Assert.Equal(4, back.Meshes().Count);   // "empty" had no vertices
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    private static uint[] Bits(float[] v) => Array.ConvertAll(v, BitConverter.SingleToUInt32Bits);

    [Fact]
    public void SaveDispatchesByExtension()
    {
        var dir = TempDir();
        try
        {
            var cast = GltfSynth.Build("synth_static");
            var c = Path.Combine(dir, "a.CAST");
            Gltf.Save(cast, c);
            Assert.Equal(cast.ToBytes(), Gltf.Load(c).ToBytes());
            var e = Assert.Throws<GltfException>(() => Gltf.Save(cast, Path.Combine(dir, "a.fbx")));
            Assert.Equal("unknown scene extension '.fbx' (use .cast, .gltf, .glb)", e.Message);
            Gltf.Save(cast, Path.Combine(dir, "sub", "b.GLB"));
            Assert.Empty(GltfCheck.Validate(Path.Combine(dir, "sub", "b.GLB")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void GlbEmbedsImagesAndGltfReferencesThem()
    {
        var dir = TempDir();
        try
        {
            GltfSynth.WriteTextures(dir);
            var cast = GltfSynth.Build("synth_skinned");
            Gltf.Save(cast, Path.Combine(dir, "out", "m.glb"), dir);
            Gltf.Save(cast, Path.Combine(dir, "out", "m.gltf"), dir);
            var glb = GltfCheck.ReadJson(Path.Combine(dir, "out", "m.glb"));
            var images = (List<object?>)glb["images"]!;
            Assert.Equal(3, images.Count);                              // tex/missing.png is dropped
            Assert.Equal("image/jpeg", ((Json)images[2]!)["mimeType"]);
            var gltf = GltfCheck.ReadJson(Path.Combine(dir, "out", "m.gltf"));
            Assert.Equal(["../tex/body_dif.png", "../tex/body%20nrm.png", "../tex/glass.jpg", "../tex/missing.png"],
                         ((List<object?>)gltf["images"]!).Select(i => ((Json)i!)["uri"]));
            Assert.Empty(GltfCheck.Validate(Path.Combine(dir, "out", "m.glb")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WriterRefusals()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "x.glb");
            Assert.Equal("the scene has no root node", Assert.Throws<GltfException>(() => Gltf.Save(new CastFile(), path)).Message);
            var c = new CastFile();
            c.CreateRoot();
            Assert.Equal("the scene has no Model", Assert.Throws<GltfException>(() => Gltf.Save(c, path)).Message);
            // a parent cycle (the prototype recurses until RecursionError)
            c = new CastFile();
            var sk = c.CreateRoot().CreateModel().CreateSkeleton();
            sk.CreateBone().SetParentIndex(1);
            sk.CreateBone().SetParentIndex(0);
            Assert.Contains("cycle", Assert.Throws<GltfException>(() => Gltf.Save(c, path)).Message);
            // weights that do not fill n × mi (a reshape error in the prototype)
            c = new CastFile();
            var mdl = c.CreateRoot().CreateModel();
            mdl.CreateSkeleton().CreateBone();
            var me = mdl.CreateMesh();
            me.SetVertexPositionBuffer([0f, 0f, 0f]);
            me.SetMaximumWeightInfluence(4);
            me.SetVertexWeightBoneBuffer([0u, 0u]);
            me.SetVertexWeightValueBuffer([1f, 0f]);
            me.SetFaceBuffer([0u]);
            Assert.Contains("weight bones", Assert.Throws<GltfException>(() => Gltf.Save(c, path)).Message);
            Assert.False(System.IO.File.Exists(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReaderRefusals()
    {
        var dir = TempDir();
        try
        {
            string Write(string name, string json)
            {
                var p = Path.Combine(dir, name);
                System.IO.File.WriteAllText(p, json);
                return p;
            }
            var basic = """{"asset":{"version":"2.0"},"nodes":[{"mesh":0}],"meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],"accessors":[ACC],"bufferViews":[{"buffer":0,"byteLength":12}],"buffers":[{"byteLength":12,"uri":"data:,AAAAAAAAAAAAAAAA"}]}""";
            var ok = Write("ok.gltf", basic.Replace("ACC", """{"bufferView":0,"componentType":5126,"count":1,"type":"VEC3"}"""));
            Assert.Equal([0u], Gltf.Load(ok).Roots()[0].ChildOfType<CastModel>()!.Meshes()[0].FaceBuffer()!);
            Assert.Equal("sparse accessors are not supported", Assert.Throws<GltfException>(() => Gltf.Load(Write("sparse.gltf",
                basic.Replace("ACC", """{"bufferView":0,"componentType":5126,"count":1,"type":"VEC3","sparse":{}}""")))).Message);
            Assert.Contains("run past", Assert.Throws<GltfException>(() => Gltf.Load(Write("short.gltf",
                basic.Replace("ACC", """{"bufferView":0,"componentType":5126,"count":2,"type":"VEC3"}""")))).Message);
            Assert.Contains("component type", Assert.Throws<GltfException>(() => Gltf.Load(Write("i32.gltf",
                basic.Replace("ACC", """{"bufferView":0,"componentType":5124,"count":1,"type":"VEC3"}""")))).Message);
            Assert.Contains("BOM", Assert.Throws<GltfException>(() => Gltf.Load(Write("bom.gltf", "﻿{}"))).Message);
            // extras the Cast cannot hold as the prototype would save them
            Assert.Contains("does not fit u32", Assert.Throws<GltfException>(() => Gltf.Load(Write("neg.gltf",
                """{"materials":[{"extras":{"bp_owner_entity":-1}}]}"""))).Message);
            Assert.Contains("one value", Assert.Throws<GltfException>(() => Gltf.Load(Write("strs.gltf",
                """{"materials":[{"extras":{"bp_x":["a","b"]}}]}"""))).Message);
            var glb = Path.Combine(dir, "nojson.glb");
            var raw = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(raw, Gltf.GlbMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(8), 20);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(16), Gltf.ChunkBin);
            System.IO.File.WriteAllBytes(glb, raw);
            Assert.Equal($"{glb}: GLB without a JSON chunk", Assert.Throws<GltfException>(() => Gltf.Load(glb)).Message);
            // a scene without anything is still a Cast: root, metadata, a model named after the file
            var empty = Gltf.Load(Write("empty.gltf", "{}"));
            Assert.Equal("empty", empty.Roots()[0].ChildOfType<CastModel>()!.Name());
            Assert.Equal("glTF", empty.Roots()[0].ChildOfType<Metadata>()!.Software());
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- always-run: Python formatting ------------------------------------------------------------------------

    [Theory]   // expected strings are Python's repr()
    [InlineData(0.1, "0.1")]
    [InlineData(1.0, "1.0")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(123456789012345678.0, "1.2345678901234568e+17")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(1.5e-7, "1.5e-07")]
    [InlineData(0.10000000149011612, "0.10000000149011612")]
    [InlineData(-3.4028234663852886e+38, "-3.4028234663852886e+38")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(1e300, "1e+300")]
    [InlineData(123.456, "123.456")]
    public void FloatReprMatchesPython(double x, string repr) => Assert.Equal(repr, GltfJson.FloatRepr(x, json: false));

    [Theory]   // round(x, 9) in Python
    [InlineData(0.7071067690849304, 0.707106769)]
    [InlineData(0.0009765625, 0.000976562)]      // an exact tie: half to even
    [InlineData(0.0029296875, 0.002929688)]      // tie, rounds up to even
    [InlineData(-1e-12, -0.0)]
    [InlineData(0.9233805094, 0.923380509)]
    [InlineData(1.0, 1.0)]
    public void PyRoundMatchesPython(double x, double expected)
    {
        var r = Gltf.PyRound(x, 9);
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(r));
    }

    [Fact]
    public void JsonMatchesPythonDumps()
    {
        var doc = new Json { ["a"] = new List<object?> { 1, 2.5, "é\"\\\n\u007f", true, null, double.NaN, double.NegativeInfinity },
                             ["b"] = new Json(), ["c"] = new List<object?>(), ["d"] = new Json { ["x"] = 18446744073709551615UL } };
        // json.dumps(doc, separators=(",", ":")) and json.dumps(doc, indent=1)
        Assert.Equal("{\"a\":[1,2.5,\"\\u00e9\\\"\\\\\\n\\u007f\",true,null,NaN,-Infinity],\"b\":{},\"c\":[],\"d\":{\"x\":18446744073709551615}}", GltfJson.Compact(doc));
        Assert.Equal("{\n \"a\": [\n  1,\n  2.5,\n  \"\\u00e9\\\"\\\\\\n\\u007f\",\n  true,\n  null,\n  NaN,\n  -Infinity\n ],\n \"b\": {},\n \"c\": [],\n \"d\": {\n  \"x\": 18446744073709551615\n }\n}",
                     GltfJson.Indented(doc));
        var back = (Json)GltfJson.Parse(GltfJson.Compact(doc))!;
        var a = (List<object?>)back["a"]!;
        Assert.IsType<BigInteger>(a[0]);
        Assert.IsType<double>(a[1]);
        Assert.True(double.IsNaN((double)a[5]!));
        Assert.Equal("{'x': 18446744073709551615}", GltfJson.PyRepr(back["d"]));
        Assert.Equal("['é', \"it's\", 1.0, True, None]", GltfJson.PyRepr(GltfJson.Parse("""["é","it's",1.0,true,null]""")));
        Assert.Equal("a%20b/%C3%A9~_.-", Gltf.Quote("a b/é~_.-"));
    }

    // ---- install-backed ---------------------------------------------------------------------------------------

    /// <summary>test_document_shape: the GLB of the pistol's Cast as `nr mesh export` writes it (DLTB wn_pistol_b_b).</summary>
    [Fact]
    public void DocumentShape()
    {
        var (m, p) = MeshFixtures.Load("dltb", "wn_pistol_b_b");
        var cast = CastFile.Read(CastExport.Build(m, p.Name).Cast.ToBytes());
        var dir = TempDir();
        try
        {
            var glb = Path.Combine(dir, "pistol.glb");
            Gltf.Save(cast, glb);
            var doc = GltfCheck.ReadJson(glb);
            Assert.Equal("2.0", ((Json)doc["asset"]!)["version"]);
            var skin = (Json)((List<object?>)doc["skins"]!)[0]!;
            var nodes = (List<object?>)doc["nodes"]!;
            var skeleton = (int)(BigInteger)skin["skeleton"]!;
            Assert.EndsWith("_armature", (string)((Json)nodes[skeleton]!)["name"]!);
            Assert.DoesNotContain((BigInteger)skeleton, ((List<object?>)skin["joints"]!).Cast<BigInteger>());
            var names = nodes.Cast<Json>().Where(n => n.ContainsKey("mesh")).Select(n => (string)n["name"]!).ToList();
            Assert.Contains("wn_pistol_b_b.e0.s0", names);
            var meshes = ((List<object?>)doc["meshes"]!).Cast<Json>().ToList();
            var attrs = (Json)((Json)((List<object?>)meshes[0]["primitives"]!)[0]!)["attributes"]!;
            foreach (var a in new[] { "POSITION", "NORMAL", "TANGENT", "TEXCOORD_0", "JOINTS_0", "WEIGHTS_0", "_BP_VERTEX_ID" })
                Assert.Contains(a, attrs.Keys);
            // the pistol has inf UVs: 0 in the accessor, exact values in extras
            Assert.Contains(meshes, g => g.TryGetValue("extras", out var ex) && ((Json)ex!).ContainsKey("bp_nonfinite_uv"));
            foreach (var a in ((List<object?>)doc["accessors"]!).Cast<Json>())
                foreach (var k in new[] { "min", "max" })
                    if (a.TryGetValue(k, out var v)) Assert.All((List<object?>)v!, x => Assert.True(double.IsFinite(Convert.ToDouble(x))));
            Assert.Empty(GltfCheck.Validate(glb));
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// <summary>
/// The glTF fixture script's builders, call for call (castlib → this port), and its hand-written foreign glTF. Float
/// literals go through double first, as struct.pack('f') takes them.
/// </summary>
internal static class GltfSynth
{
    private static readonly float NanF = BitConverter.Int32BitsToSingle(0x7FC00000);   // Python float('nan') packed 'f'
    private static float F(double x) => double.IsNaN(x) ? NanF : (float)x;
    private static float[] Fs(params double[] v) => Array.ConvertAll(v, F);

    public static CastFile Build(string name) => name switch
    {
        "synth_skinned" => Skinned(),
        "synth_bones300" => Bones300(),
        "synth_static" => Static(),
        _ => throw new ArgumentException(name),
    };

    public static byte[] TexBytes(int seed, int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)((i * 31 + seed) & 0xFF);
        return b;
    }

    public static void WriteTextures(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "tex"));
        System.IO.File.WriteAllBytes(Path.Combine(dir, "tex", "body_dif.png"), TexBytes(1, 67));
        System.IO.File.WriteAllBytes(Path.Combine(dir, "tex", "body nrm.png"), TexBytes(2, 30));
        System.IO.File.WriteAllBytes(Path.Combine(dir, "tex", "glass.jpg"), TexBytes(3, 45));
    }

    private static CastFile Skinned()
    {
        var c = new CastFile();
        var root = c.CreateRoot();
        var meta = root.CreateMetadata();
        meta.SetSoftware("nightrunner test");
        meta.SetUpAxis("y");
        var mdl = root.CreateModel();
        mdl.SetName("synth");
        var sk = mdl.CreateSkeleton();

        Bone Bone(string? name, int parent, float[]? lp, float[]? lr)
        {
            var b = sk.CreateBone();
            if (name is not null) b.SetName(name);
            b.SetParentIndex(parent);
            if (lp is not null) b.SetLocalPosition(lp);
            if (lr is not null) b.SetLocalRotation(lr);
            return b;
        }

        Bone("root", -1, Fs(0, 0, 0), Fs(0, 0, 0, 1)).CreateProperty("bp_entity", "i").SetValues([0u]);
        var spine = Bone("spine", 0, Fs(0, 1.25, 0.5), Fs(0, 0.7071067690849304, 0, 0.7071067690849304));
        spine.CreateProperty("bp_flags", "i").SetValues([3u]);
        spine.CreateProperty("bp_note", "s").SetString("x");
        Bone("arm.l", 1, Fs(0.5, 0.25, -0.125), Fs(0.5, 0.5, 0.5, 0.5));
        Bone(null, 7, null, null);
        Bone("tip", 2, Fs(0.1, 0.2, 0.3), Fs(0.1, 0.2, 0.3, 0.9));

        Material Mat(string? name, (string Slot, string Path)[] slots, Action<Material> props)
        {
            var m = mdl.CreateMaterial();
            if (name is not null) m.SetName(name);
            m.SetType("pbr");
            foreach (var (slot, path) in slots)
            {
                var f = m.CreateFile();
                f.SetPath(path);
                m.SetSlot(slot, f.Hash);
            }
            props(m);
            return m;
        }

        var m0 = Mat("body.mat", [("albedo", "tex/body_dif.png"), ("normal", "tex/body nrm.png")], m =>
        {
            m.CreateProperty("bp_alpha_mode", "s").SetString("mask");
            m.CreateProperty("bp_material_slot", "i").SetValues([0u]);
        });
        var m1 = Mat("glass.mat", [("diffuse", "tex/glass.jpg")], m =>
        {
            m.CreateProperty("bp_alpha_mode", "s").SetString("blend");
            m.CreateProperty("bp_values", "f").SetValues(Fs(0.5, 0.25));
        });
        Mat("missing.mat", [("albedo", "tex/missing.png")], _ => { });
        Mat(null, [("albedo", "tex/body_dif.png")], _ => { });

        double inf = double.PositiveInfinity, nan = double.NaN;
        var me = mdl.CreateMesh();
        me.SetName("quad");
        me.SetVertexPositionBuffer(Fs(0, 0, 0, 1, 0, 0, 0, 1, 0.5, 1, 1, -0.5));
        me.SetVertexNormalBuffer(Fs(0, 0, 1, 0, 2, 0, 0.6, 0.8, 0, 0, 0, -1));
        me.SetVertexTangentBuffer(Fs(1, 0, 0, 0, 0, 1, 0.3, 0.3, 0.3, 1, 0, 0));
        me.CreateProperty("bp_tangent_sign", "f").SetValues(Fs(1, -1, 1, -1));
        me.SetUVLayerCount(2);
        me.SetVertexUVLayerBuffer(0, Fs(0, 0, inf, 0.5, 0.25, nan, 1, 1));
        me.SetVertexUVLayerBuffer(1, Fs(0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5));
        me.SetMaximumWeightInfluence(4);
        me.SetSkinningMethod("linear");
        me.SetVertexWeightBoneBuffer([0, 1, 0, 0, 1, 2, 4, 0, 2, 0, 0, 0, 3, 1, 2, 4]);
        me.SetVertexWeightValueBuffer(Fs(1, 0, 0, 0, 0.5, 0.25, 0.25, 0, 0.5, 0, 0, 0, 0.25, 0.25, 0.25, 0.125));
        me.SetFaceBuffer([0, 1, 2, 2, 1, 3]);
        me.SetMaterial(m0.Hash);
        me.CreateProperty("bp_format", "b").SetValues(new byte[] { 3 });
        me.CreateProperty("bp_raw_00", "s").SetString("00ff");
        me.CreateProperty("bp_vertex_id", "i").SetValues([10u, 11u, 12u, 13u]);
        me.CreateProperty("bp_h", "h").SetValues(new ushort[] { 7, 8 });
        me.CreateProperty("bp_l", "l").SetValues([4000000000UL]);
        me.CreateProperty("bp_d", "d").SetValues([0.1]);
        me.CreateProperty("bp_v", "3v").SetValues(Fs(1, 2, 3));
        me.CreateProperty("bp_empty", "i").SetValues(Array.Empty<uint>());

        me = mdl.CreateMesh();
        me.SetName("six");
        me.SetVertexPositionBuffer(Fs(0, 0, 0, 2, 0, 0, 0, 2, 0));
        me.SetMaximumWeightInfluence(6);
        me.SetSkinningMethod("linear");
        me.SetVertexWeightBoneBuffer([0, 1, 2, 3, 4, 1, 4, 3, 2, 1, 0, 2, 1, 2, 3, 4, 0, 0]);
        me.SetVertexWeightValueBuffer(Fs(0.1, 0.2, 0.2, 0.2, 0.2, 0.1, 0.5, 0, 0.25, 0.25, 0, 0, 0, 0, 0, 0, 0, 0));
        me.SetFaceBuffer([0, 1, 2]);
        me.SetMaterial(m1.Hash);

        me = mdl.CreateMesh();
        me.SetName("two");
        me.SetVertexPositionBuffer(Fs(0, 0, 1, 2, 0, 1, 0, 2, 1));
        me.SetMaximumWeightInfluence(2);
        me.SetVertexWeightBoneBuffer([1, 2, 2, 0, 3, 3]);
        me.SetVertexWeightValueBuffer(Fs(0.75, 0.25, 1, 0, 0.3, 0.3));
        me.SetFaceBuffer([2, 1, 0]);
        me.SetMaterial(12345);

        me = mdl.CreateMesh();
        me.SetName("empty");

        me = mdl.CreateMesh();
        me.SetVertexPositionBuffer(Fs(-1, -2, -3, 4, 5, 6, 0.5, 0.5, 0.5));
        me.SetFaceBuffer([0, 1, 2]);
        return c;
    }

    private static CastFile Bones300()
    {
        var c = new CastFile();
        var mdl = c.CreateRoot().CreateModel();
        mdl.SetName("chain");
        var sk = mdl.CreateSkeleton();
        for (int i = 0; i < 300; i++)
        {
            var b = sk.CreateBone();
            b.SetName($"b{i}");
            b.SetParentIndex(i - 1);
            b.SetLocalPosition(Fs(0, 0.5, 0));
            b.SetLocalRotation(Fs(0, 0, 0, 1));
        }
        var me = mdl.CreateMesh();
        me.SetName("strip");
        me.SetVertexPositionBuffer(Fs(0, 0, 0, 1, 0, 0, 0, 149.5, 0));
        me.SetMaximumWeightInfluence(4);
        me.SetVertexWeightBoneBuffer([299, 150, 0, 0, 0, 0, 0, 0, 256, 257, 0, 0]);
        me.SetVertexWeightValueBuffer(Fs(0.5, 0.5, 0, 0, 1, 0, 0, 0, 0.25, 0.25, 0, 0));
        me.SetFaceBuffer([0, 1, 2]);
        return c;
    }

    private static CastFile Static()
    {
        var c = new CastFile();
        var mdl = c.CreateRoot().CreateModel();
        var me = mdl.CreateMesh();
        me.SetName("static");
        me.SetVertexPositionBuffer(Fs(0, 0, 0, 3, 0, 0, 0, 3, 0, 3, 3, 0));
        me.SetVertexNormalBuffer(Fs(0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1));
        me.SetUVLayerCount(1);
        me.SetVertexUVLayerBuffer(0, Fs(0, 0, 1, 0, 0, 1, 1, 1));
        me.SetMaximumWeightInfluence(4);
        me.SetVertexWeightBoneBuffer(new uint[16]);
        me.SetVertexWeightValueBuffer(Fs(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0));
        me.SetFaceBuffer([0, 1, 2, 2, 1, 3]);
        me.CreateProperty("bp_note", "s").SetString("café \"q\" \\ ☃");
        return c;
    }

    // ---- the foreign glTF (the script's write_foreign(), with its buffer as a data: URI) ----------------------------

    private const string ForeignData =
        "AAAAAAAAAAAAAAAAAACAPwAAAAAAAAAAAAAAAAAAgD8AAAAAAACAPwAAgD8AAAAAAAAAAAAAAAAAAIA/AAAAAAAAAAAAAIA/AAAA" +
        "AAAAAAAAAIA/AAAAAAAAAAAAAIA/AACAPwAAAAAAAAAAAACAvwAAgD8AAAAAAAAAAAAAgD8AAIA/AAAAAAAAAAAAAIC/AACAPwAA" +
        "AAAAAAAAAACAPwAA/wAA/4BAAAABAAIAAgABAAMAAAEAAAABAAAAAQAAAAEAAAAAAD8AAAA/AAAAAAAAAAAAAAA/AAAAPwAAAAAA" +
        "AAAAAAAAPwAAAD8AAAAAAAAAAAAAAD8AAAA/AAAAAAAAAAABAAAAAAAAAAEAAAAAAAAAAQAAAAAAAAABAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAKBAAADAQAAA4EAAAAhBAAAA" +
        "AAAAAAAAAAAAAACAPgAAAD8AAABAAAAAAAAAAAAAAIA+AAAAPwAAAAAAAABAAAAAAAAAgD4AAAA/AAAAQAAAAEAAAAAAAACAPgAA" +
        "AD8AAIA/AAAAAAAAAAAAAAAAAAAAAAAAgD8AAAAAAAAAAAAAAAAAAAAAAACAPwAAAAAAAAAAAAAAAAAAAAAAAIA/AACAPwAAAAAA" +
        "AAAAAAAAAAAAAAAAAIA/AAAAAAAAAAAAAAAAAAAAAAAAgD8AAAAAAAAAAAAAAAAAAAAAAACAPwAAgD8AAAAAAAAAAAAAAAAAAAAA" +
        "AACAPwAAAAAAAAAAAAAAAAAAAAAAAIA/AAAAAAAAAAAAAAAAAAAAAAAAgD8=" +
        "";

    public static string ForeignJson() => ForeignTemplate.Replace("@DATA@", ForeignData);

    /// <summary>The same document as a GLB whose buffer is the BIN chunk.</summary>
    public static byte[] ForeignGlb()
    {
        var doc = (Json)GltfJson.Parse(ForeignJson())!;
        var blob = Convert.FromBase64String(ForeignData);
        doc["buffers"] = new List<object?> { new Json { ["byteLength"] = blob.Length } };
        var js = Encoding.ASCII.GetBytes(GltfJson.Compact(doc));
        int jp = -js.Length & 3, bp = -blob.Length & 3;
        using var ms = new MemoryStream();
        var h = new byte[8];
        void U32x2(uint a, uint b) { BinaryPrimitives.WriteUInt32LittleEndian(h, a); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), b); ms.Write(h); }
        U32x2(Gltf.GlbMagic, 2);
        ms.Write(BitConverter.GetBytes((uint)(28 + js.Length + jp + blob.Length + bp)));
        U32x2((uint)(js.Length + jp), Gltf.ChunkJson);
        ms.Write(js);
        for (int i = 0; i < jp; i++) ms.WriteByte(0x20);
        U32x2((uint)(blob.Length + bp), Gltf.ChunkBin);
        ms.Write(blob);
        for (int i = 0; i < bp; i++) ms.WriteByte(0);
        return ms.ToArray();
    }

    private const string ForeignTemplate = """
{
 "asset": {"version": "2.0", "generator": 42},
 "scene": 0,
 "scenes": [{"nodes": [0, 6]}],
 "nodes": [
  {"name": "Armature", "translation": [0, 0, 1], "rotation": [0, 0.7071068, 0, 0.7071068], "scale": [2, 2, 2], "children": [1]},
  {"name": "hips", "translation": [0, 1, 0], "children": [2, 3], "extras": {"bp_entity": 4, "bp_flag": true, "bp_list": [1, 2.5], "bp_obj": {"a": [1, null]}, "other": 1, "bp_s": "text", "bp_bools": [true, false], "bp_f": 0.1}},
  {"matrix": [1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0.5, 0.25, 0, 1]},
  {"name": "hand", "rotation": [0, 0, 0.3826834, 0.9238795], "scale": [1, 3, 1]},
  {"name": "body", "mesh": 0, "skin": 0, "extras": {"bp_node": "n"}},
  {"name": "body2", "mesh": 0, "skin": 1},
  {"name": "prop", "translation": [5, 0, 0], "rotation": [0, 0, 0.7071068, 0.7071068], "scale": [1, 2, 1], "children": [7]},
  {"mesh": 1, "translation": [0, 1, 0]}
 ],
 "skins": [
  {"joints": [1, 2, 3], "inverseBindMatrices": 13},
  {"joints": [3, 1, 2]}
 ],
 "meshes": [
  {"name": "bodymesh", "extras": {"bp_format": 3, "bp_vertex_id": 1, "bp_nonfinite_uv": {"0": {"rows": [1, 3], "hex": "0000807f000000000000003f0000c07f"}}}, "primitives": [{"attributes": {"POSITION": 0, "NORMAL": 1, "TANGENT": 2, "TEXCOORD_0": 3, "TEXCOORD_1": 12, "JOINTS_0": 5, "WEIGHTS_0": 6, "JOINTS_1": 7, "WEIGHTS_1": 8, "_BP_VERTEX_ID": 9}, "indices": 4, "material": 1}, {"attributes": {"POSITION": 0}, "mode": 1}, {"attributes": {"POSITION": 10, "TEXCOORD_0": 11}, "material": 5}]},
  {"primitives": [{"attributes": {"POSITION": 0, "NORMAL": 1, "TANGENT": 2}, "indices": 4}]}
 ],
 "materials": [
  {"name": "m0", "extras": {"bp_alpha_mode": "mask"}},
  {"extras": {"bp_material_slot": 2}}
 ],
 "accessors": [
  {"bufferView": 0, "componentType": 5126, "count": 4, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0]},
  {"bufferView": 1, "componentType": 5126, "count": 4, "type": "VEC3"},
  {"bufferView": 2, "componentType": 5126, "count": 4, "type": "VEC4"},
  {"bufferView": 3, "componentType": 5121, "count": 4, "type": "VEC2", "normalized": true},
  {"bufferView": 4, "componentType": 5123, "count": 6, "type": "SCALAR"},
  {"bufferView": 5, "componentType": 5121, "count": 4, "type": "VEC4"},
  {"bufferView": 6, "componentType": 5126, "count": 4, "type": "VEC4"},
  {"bufferView": 7, "componentType": 5123, "count": 4, "type": "VEC4"},
  {"bufferView": 8, "componentType": 5126, "count": 4, "type": "VEC4"},
  {"bufferView": 9, "componentType": 5126, "count": 4, "type": "SCALAR"},
  {"bufferView": 10, "componentType": 5126, "count": 4, "type": "VEC3"},
  {"bufferView": 10, "byteOffset": 12, "componentType": 5126, "count": 4, "type": "VEC2"},
  {"componentType": 5126, "count": 4, "type": "VEC2"},
  {"bufferView": 11, "componentType": 5126, "count": 3, "type": "MAT4"}
 ],
 "bufferViews": [
  {"buffer": 0, "byteOffset": 0, "byteLength": 48},
  {"buffer": 0, "byteOffset": 48, "byteLength": 48},
  {"buffer": 0, "byteOffset": 96, "byteLength": 64},
  {"buffer": 0, "byteOffset": 160, "byteLength": 8},
  {"buffer": 0, "byteOffset": 168, "byteLength": 12},
  {"buffer": 0, "byteOffset": 180, "byteLength": 16},
  {"buffer": 0, "byteOffset": 196, "byteLength": 64},
  {"buffer": 0, "byteOffset": 260, "byteLength": 32},
  {"buffer": 0, "byteOffset": 292, "byteLength": 64},
  {"buffer": 0, "byteOffset": 356, "byteLength": 16},
  {"buffer": 0, "byteOffset": 372, "byteLength": 80, "byteStride": 20},
  {"buffer": 0, "byteOffset": 452, "byteLength": 192}
 ],
 "buffers": [
  {"byteLength": 644, "uri": "data:application/octet-stream;base64,@DATA@"}
 ]
}
""";
}

/// <summary>
/// Structural checks of a written glTF / GLB (the parts of the Khronos validator's rules this writer can break), and
/// the comparisons the fixture tier uses when bytes differ.
/// </summary>
internal static class GltfCheck
{
    private static readonly Dictionary<string, int> Components = new() { ["SCALAR"] = 1, ["VEC2"] = 2, ["VEC3"] = 3, ["VEC4"] = 4, ["MAT4"] = 16 };
    private static readonly Dictionary<long, int> Sizes = new() { [5120] = 1, [5121] = 1, [5122] = 2, [5123] = 2, [5125] = 4, [5126] = 4 };

    public static Json ReadJson(string path) => Load(path).Doc;

    /// <summary>The document and its one buffer (GLB: header and chunks checked on the way).</summary>
    public static (Json Doc, byte[] Bin, List<string> Problems) Load(string path)
    {
        var raw = System.IO.File.ReadAllBytes(path);
        var problems = new List<string>();
        if (path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(raw) != Gltf.GlbMagic) problems.Add("bad GLB magic");
            if (BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4)) != 2) problems.Add("GLB version is not 2");
            if (BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8)) != raw.Length) problems.Add("GLB length field differs from the file");
            int jl = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12));
            if (BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(16)) != Gltf.ChunkJson) problems.Add("first chunk is not JSON");
            if (jl % 4 != 0) problems.Add("JSON chunk not 4-aligned");
            var doc = (Json)GltfJson.Parse(Encoding.UTF8.GetString(raw, 20, jl))!;
            byte[] bin = [];
            int off = 20 + jl;
            if (off < raw.Length)
            {
                int bl = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(off));
                if (BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(off + 4)) != Gltf.ChunkBin) problems.Add("second chunk is not BIN");
                if (bl % 4 != 0) problems.Add("BIN chunk not 4-aligned");
                if (off + 8 + bl != raw.Length) problems.Add("bytes after the BIN chunk");
                bin = raw.AsSpan(off + 8, bl).ToArray();
            }
            return (doc, bin, problems);
        }
        var d = (Json)GltfJson.Parse(Encoding.UTF8.GetString(raw))!;
        var buffers = (List<object?>)d["buffers"]!;
        var uri = (string)((Json)buffers[0]!)["uri"]!;
        return (d, System.IO.File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(path)!, Uri.UnescapeDataString(uri))), problems);
    }

    private static List<object?> L(Json o, string k) => o.TryGetValue(k, out var v) ? (List<object?>)v! : [];
    private static long I(object? v) => (long)(BigInteger)v!;

    public static List<string> Validate(string path)
    {
        var (doc, bin, p) = Load(path);
        if (((Json)doc["asset"]!)["version"] as string != "2.0") p.Add("asset.version is not 2.0");
        var buffers = L(doc, "buffers");
        if (buffers.Count != 1 || I(((Json)buffers[0]!)["byteLength"]) > bin.Length) p.Add("buffer length");
        var views = L(doc, "bufferViews").Cast<Json>().ToList();
        foreach (var (v, i) in views.Select((v, i) => (v, i)))
        {
            long o = I(v["byteOffset"]), n = I(v["byteLength"]);
            if (I(v["buffer"]) != 0 || o < 0 || o + n > bin.Length) p.Add($"bufferView {i} out of the buffer");
            if (o % 4 != 0) p.Add($"bufferView {i} not 4-aligned");
            if (v.TryGetValue("target", out var t) && I(t) is not (34962 or 34963)) p.Add($"bufferView {i} target");
        }
        var accessors = L(doc, "accessors").Cast<Json>().ToList();
        var data = new List<double[]>();
        foreach (var (a, i) in accessors.Select((a, i) => (a, i)))
        {
            long ct = I(a["componentType"]), count = I(a["count"]);
            int nc = Components[(string)a["type"]!], size = Sizes[ct];
            var v = views[(int)I(a["bufferView"])];
            long start = I(v["byteOffset"]);
            if (count * nc * size > I(v["byteLength"])) p.Add($"accessor {i} runs past its bufferView");
            if (start % size != 0) p.Add($"accessor {i} misaligned");
            var d = new double[count * nc];
            for (int k = 0; k < d.Length; k++)
            {
                int at = (int)(start + k * size);
                d[k] = ct switch
                {
                    5121 => bin[at],
                    5123 => BinaryPrimitives.ReadUInt16LittleEndian(bin.AsSpan(at)),
                    5125 => BinaryPrimitives.ReadUInt32LittleEndian(bin.AsSpan(at)),
                    _ => BinaryPrimitives.ReadSingleLittleEndian(bin.AsSpan(at)),
                };
                if (ct == 5126 && !double.IsFinite(d[k])) { p.Add($"accessor {i} holds a non-finite float"); break; }
            }
            if (a.TryGetValue("min", out var mn))
            {
                var min = ((List<object?>)mn!).Select(Convert.ToDouble).ToArray();
                var max = ((List<object?>)a["max"]!).Select(Convert.ToDouble).ToArray();
                for (int c = 0; c < nc; c++)
                {
                    var col = Enumerable.Range(0, (int)count).Select(r => d[r * nc + c]).ToList();
                    if (col.Count > 0 && (col.Min() != min[c] || col.Max() != max[c])) p.Add($"accessor {i} min/max differ from the data");
                }
            }
            data.Add(d);
        }
        var nodes = L(doc, "nodes").Cast<Json>().ToList();
        var skins = L(doc, "skins").Cast<Json>().ToList();
        var meshes = L(doc, "meshes").Cast<Json>().ToList();
        var parentOf = new Dictionary<long, int>();
        foreach (var (n, i) in nodes.Select((n, i) => (n, i)))
        {
            foreach (var c in L(n, "children"))
            {
                if (I(c) < 0 || I(c) >= nodes.Count) p.Add($"node {i} child {c} out of range");
                else if (!parentOf.TryAdd(I(c), i)) p.Add($"node {c} has two parents");
                if (I(c) == i) p.Add($"node {i} is its own child");
            }
            if (n.TryGetValue("mesh", out var m) && I(m) >= meshes.Count) p.Add($"node {i} mesh out of range");
            if (n.TryGetValue("skin", out var s) && I(s) >= skins.Count) p.Add($"node {i} skin out of range");
            if (n.TryGetValue("rotation", out var r))
            {
                var q = ((List<object?>)r!).Select(Convert.ToDouble).ToArray();
                if (Math.Abs(Math.Sqrt(q.Sum(x => x * x)) - 1) > 1e-6) p.Add($"node {i} rotation not unit");
            }
        }
        foreach (var start in parentOf.Keys)
        {
            long x = start;
            for (int steps = 0; parentOf.TryGetValue(x, out int up); steps++)
            {
                if (steps > nodes.Count) { p.Add($"node {start} is in a cycle"); break; }
                x = up;
            }
        }
        foreach (var root in L((Json)L(doc, "scenes")[0]!, "nodes"))
            if (parentOf.ContainsKey(I(root))) p.Add($"scene root {root} has a parent");
        foreach (var (s, i) in skins.Select((s, i) => (s, i)))
        {
            var joints = L(s, "joints").Select(I).ToList();
            if (joints.Any(j => j < 0 || j >= nodes.Count)) p.Add($"skin {i} joint out of range");
            var ibm = accessors[(int)I(s["inverseBindMatrices"])];
            if ((string)ibm["type"]! != "MAT4" || I(ibm["count"]) != joints.Count) p.Add($"skin {i} inverse bind matrices");
            long sk = I(s["skeleton"]);
            foreach (var j in joints)
            {
                long x = j;
                while (x != sk && parentOf.TryGetValue(x, out int up)) x = up;
                if (x != sk) { p.Add($"skin {i} joint {j} not under the skeleton root"); break; }
            }
        }
        var materials = L(doc, "materials").Cast<Json>().ToList();
        var textures = L(doc, "textures").Cast<Json>().ToList();
        foreach (var t in textures)
            if (I(t["source"]) >= L(doc, "images").Count || I(t["sampler"]) >= L(doc, "samplers").Count) p.Add("texture reference out of range");
        foreach (var img in L(doc, "images").Cast<Json>())
            if (!(img.ContainsKey("uri") || img.ContainsKey("bufferView") && img.ContainsKey("mimeType"))) p.Add("image without source");
        foreach (var m in materials)
        {
            var refs = new List<object?>();
            if (((Json)m["pbrMetallicRoughness"]!).TryGetValue("baseColorTexture", out var bc)) refs.Add(((Json)bc!)["index"]);
            if (m.TryGetValue("normalTexture", out var nt)) refs.Add(((Json)nt!)["index"]);
            if (refs.Any(r => I(r) >= textures.Count)) p.Add("material texture out of range");
        }
        foreach (var (n, ni) in nodes.Select((n, i) => (n, i)))
        {
            if (!n.TryGetValue("mesh", out var mref)) continue;
            foreach (var prim in L(meshes[(int)I(mref)], "primitives").Cast<Json>())
            {
                var at = (Json)prim["attributes"]!;
                var pos = accessors[(int)I(at["POSITION"])];
                long vc = I(pos["count"]);
                if (!pos.ContainsKey("min")) p.Add($"node {ni}: POSITION without min/max");
                foreach (var (name, idx) in at)
                    if (I(accessors[(int)I(idx)]["count"]) != vc) p.Add($"node {ni}: {name} count differs from POSITION");
                if (prim.TryGetValue("material", out var mat) && I(mat) >= materials.Count) p.Add($"node {ni}: material out of range");
                var ia = accessors[(int)I(prim["indices"])];
                if ((string)ia["type"]! != "SCALAR" || I(ia["componentType"]) != 5125) p.Add($"node {ni}: indices type");
                if (data[(int)I(prim["indices"])].Any(x => x >= vc)) p.Add($"node {ni}: index out of range");
                if (at.TryGetValue("NORMAL", out var na) && Rows(data[(int)I(na)], 3).Any(r => Math.Abs(Len(r) - 1) > 1e-3))
                    p.Add($"node {ni}: NORMAL not unit");
                if (at.TryGetValue("TANGENT", out var ta) && Rows(data[(int)I(ta)], 4).Any(r => Math.Abs(Len(r[..3]) - 1) > 1e-3 || Math.Abs(r[3]) != 1))
                    p.Add($"node {ni}: TANGENT not unit with w = ±1");
                if (at.TryGetValue("JOINTS_0", out var ja))
                {
                    if (!n.TryGetValue("skin", out var sref)) { p.Add($"node {ni}: JOINTS_0 without a skin"); continue; }
                    int joints = L(skins[(int)I(sref)], "joints").Count;
                    if (data[(int)I(ja)].Any(j => j >= joints)) p.Add($"node {ni}: joint index out of range");
                    if (Rows(data[(int)I(at["WEIGHTS_0"])], 4).Any(r => r.Any(x => x < 0) || Math.Abs(r.Sum() - 1) > 2e-6))
                        p.Add($"node {ni}: WEIGHTS_0 rows do not sum to 1");
                }
            }
        }
        return p;
    }

    private static IEnumerable<double[]> Rows(double[] d, int w)
    {
        for (int i = 0; i + w <= d.Length; i += w) yield return d[i..(i + w)];
    }

    private static double Len(double[] r) => Math.Sqrt(r.Sum(x => x * x));

    /// <summary>Distance in units in the last place (NaN = NaN, ±0 equal).</summary>
    public static long Ulp(float a, float b)
    {
        if (float.IsNaN(a) && float.IsNaN(b)) return 0;
        if (float.IsNaN(a) || float.IsNaN(b)) return long.MaxValue;
        static long Ordered(float x) { int i = BitConverter.SingleToInt32Bits(x); return i < 0 ? int.MinValue - (long)i : i; }
        return Math.Abs(Ordered(a) - Ordered(b));
    }

    /// <summary>Two written files: the same JSON (floats exactly), the same bytes outside float accessors, and float
    /// accessor data within <paramref name="maxUlp"/>.</summary>
    public static (List<string> Problems, string Summary) Compare(string theirsPath, string oursPath, long maxUlp)
    {
        var (a, ab, _) = Load(theirsPath);
        var (b, bb, _) = Load(oursPath);
        var problems = new List<string>();
        if (GltfJson.Compact(a) != GltfJson.Compact(b)) problems.Add("JSON differs");
        if (ab.Length != bb.Length) { problems.Add("buffer length differs"); return (problems, ""); }
        var floatBytes = new bool[ab.Length];
        var views = L(a, "bufferViews").Cast<Json>().ToList();
        foreach (var acc in L(a, "accessors").Cast<Json>())
            if (I(acc["componentType"]) == 5126)
            {
                var v = views[(int)I(acc["bufferView"])];
                for (long k = I(v["byteOffset"]); k < I(v["byteOffset"]) + I(v["byteLength"]); k++) floatBytes[k] = true;
            }
        long worst = 0, differing = 0;
        for (int i = 0; i < ab.Length; i++)
        {
            if (!floatBytes[i])
            {
                if (ab[i] != bb[i]) { problems.Add($"byte {i} differs outside float data"); break; }
                continue;
            }
            if (i % 4 != 0) continue;
            long u = Ulp(BinaryPrimitives.ReadSingleLittleEndian(ab.AsSpan(i)), BinaryPrimitives.ReadSingleLittleEndian(bb.AsSpan(i)));
            if (u > 0) differing++;
            worst = Math.Max(worst, u);
        }
        if (worst > maxUlp) problems.Add($"float data differs by {worst} ulp");
        return (problems, $"{differing} floats differ, max {worst} ulp");
    }

    /// <summary>Two Cast trees node for node: same identifiers, hashes, property names, types and counts, the same
    /// non-float values; float values within <paramref name="maxUlp"/>, except bone rotations (<c>lr</c>/<c>wr</c>, an
    /// eigenvector: numpy's LAPACK and the C# Jacobi agree to ~1e-15 absolute, not in ulp near 0) which must match as
    /// rotations (q or −q) within <paramref name="rotationTolerance"/>. Returns the problems and a summary.</summary>
    public static (List<string> Problems, string Summary) CompareCasts(CastFile a, CastFile b, long maxUlp, double rotationTolerance)
    {
        var problems = new List<string>();
        long worst = 0, floats = 0, differing = 0, flips = 0;
        double worstRotation = 0;
        void Walk(CastNode x, CastNode y, string path)
        {
            if (problems.Count > 5) return;
            if (x.Identifier != y.Identifier || x.Hash != y.Hash || x.Properties.Count != y.Properties.Count || x.ChildNodes.Count != y.ChildNodes.Count)
            {
                problems.Add($"{path}: node differs");
                return;
            }
            foreach (var (k, p) in x.Properties)
            {
                var q = y.Property(k);
                if (q is null || q.Type != p.Type || q.ValueCount != p.ValueCount)
                {
                    problems.Add($"{path}.{k}: property differs");
                    return;
                }
                if (p.Values is float[] f)
                {
                    var g = (float[])q.Values;
                    if (k is "lr" or "wr" && f.Length == 4)
                    {
                        double same = 0, flipped = 0;
                        for (int i = 0; i < 4; i++) { same = Math.Max(same, Math.Abs(f[i] - g[i])); flipped = Math.Max(flipped, Math.Abs(f[i] + g[i])); }
                        if (flipped < same) flips++;
                        worstRotation = Math.Max(worstRotation, Math.Min(same, flipped));
                        if (Math.Min(same, flipped) > rotationTolerance) problems.Add($"{path}.{k}: rotation differs by {Math.Min(same, flipped)}");
                        continue;
                    }
                    for (int i = 0; i < f.Length; i++)
                    {
                        long u = Ulp(f[i], g[i]);
                        floats++;
                        if (u > 0 || BitConverter.SingleToUInt32Bits(f[i]) != BitConverter.SingleToUInt32Bits(g[i])) differing++;
                        worst = Math.Max(worst, u);
                        if (u > maxUlp) { problems.Add($"{path}.{k}[{i}]: {f[i]} vs {g[i]}"); break; }
                    }
                }
                else if (p.Type == CastPropertyType.String ? p.StringValue != q.StringValue
                         : !p.ToDoubleArray().AsSpan().SequenceEqual(q.ToDoubleArray()))
                    problems.Add($"{path}.{k}: values differ");
            }
            for (int i = 0; i < x.ChildNodes.Count; i++) Walk(x.ChildNodes[i], y.ChildNodes[i], $"{path}/{i}");
        }
        if (a.RootNodes.Count != b.RootNodes.Count) return (["root count differs"], "");
        for (int i = 0; i < a.RootNodes.Count; i++) Walk(a.RootNodes[i], b.RootNodes[i], $"{i}");
        return (problems, $"{differing}/{floats} floats differ (max {worst} ulp), rotations within {worstRotation:G3}, {flips} sign flips");
    }
}
