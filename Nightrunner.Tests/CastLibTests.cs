using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Nightrunner.Core.Cast;
using CastFileNode = Nightrunner.Core.Cast.File;
using CastMesh = Nightrunner.Core.Cast.Mesh;
using CastModel = Nightrunner.Core.Cast.Model;

namespace Nightrunner.Tests;

// Port of castlib.py (the vendored Cast library) checked against the prototype's own output.
//
// Always-run tier: CastSynth rebuilds, call for call, the trees a prototype script builds with castlib (every node type,
// every property type, custom and non-ASCII names, empty arrays, a 70,000-vertex mesh, 300 bones, hand-made edge
// streams); the SHA-256 of each file the prototype wrote is recorded below, so the C# bytes are compared with the
// prototype's on every run. The script is tools/parity/cast_fixtures.py (historical; not needed to run these tests).
// Install-backed tier: real meshes exported by the port read back and rewrite byte for byte.
//
// Ported from tests/test_cast_export.py: test_identifiers_per_spec. Not ported: test_quaternion_helpers and ExportTests
// (they test cast/export.py, not castlib).
public class CastLibTests
{
    // sha256 of the files the prototype wrote (castlib, fresh hash counter), and how many hashes each build took.
    private static readonly Dictionary<string, (string Sha, int Size, int NextHash)> Python = new()
    {
        ["synth_types"] = ("3100747b41b61c07684a1c1d189502f4cf7afad882cffbc944e52d586d6d6900", 1161, 3),
        ["synth_model"] = ("a3769e07a6dc77d0ffbac2e18b725894a9a2199fa8c75220e95954d6327c02fa", 8221391, 324),
        ["synth_anim"] = ("eeca0a46ea50d6dedc29908b83c1a80e8b2e87714c1c714e23f7ffab377d59fb", 19669, 10),
        ["synth_misc"] = ("343809c0d48dd97e6f93d6d7a9661d33a6310ed1efa5c1312851ea54b930c824", 550, 13),
        ["synth_extended"] = ("c2f0ef566e84623bcd258ad4b13d3ece33388d93c3be4b5c43afd1c4c7ff1308", 19791, 14),
    };

    // hand-made streams: sha256 of the input, and of castlib's load → save of it.
    private static readonly Dictionary<string, (string Input, string Resaved)> PythonEdges = new()
    {
        ["edge_header"] = ("c376e492d66201567f010807b79d02f5af77b4f204d0c97896720a220ac0751d", "fc1b71667e45d24f5efb7bd1624c4dd91206f63d050c29d14c6941f72f130968"),
        ["edge_length"] = ("2a4c7dcdf137ca036c2c1d85897fb5cbc0306f9915c5fc8dc85169d5853d5b2c", "8bc23f2a3716b14827f413bdddc1e0682a0267bedce08bacae7942c00450403e"),
        ["edge_duplicates"] = ("febf0559af9b371c820b565280c57647da8c8276b5f5316374fdd8cacd286323", "f8ee1d9974ff94cde19e6f8ee7340d79777f979e83a36f1802d964a02935c410"),
        ["edge_strings"] = ("60ca3a79a045d6b99b1c8c5662aa8f31a3f2ce964c292762dc1e6643cf6004fb", "4f433cfb65bfc6139bdad2af55f146b096f538dfbeabdbc5e67845aedcac70ea"),
        ["edge_typeids"] = ("d66af2aad99fccebc78c46828eb6a3246af524c681778042cb4f7c7d17c4b6f2", "554667b00928e4b1a47319ce830d68579f571f9815834ec0b49c5c10f460945a"),
        ["edge_nodes"] = ("59831c686d688ac05274b9e309f26a4a7d72b05c378a13effa27f8cf03cf427d", "59831c686d688ac05274b9e309f26a4a7d72b05c378a13effa27f8cf03cf427d"),
    };

    // castlib cannot read these (KeyError 'zz' / UnicodeDecodeError on the type); sha256 of the input only.
    private static readonly Dictionary<string, string> PythonUnknown = new()
    {
        ["unknown_prop"] = "ba96d5974902b4adb08c088f990c95bf2c4d1748879224110dacf7e735f4a18c",
        ["unknown_prop_root"] = "3920f42529a9bb7b272075e2ca523bc3d2c9f0d3957a3e0ef741a590aa24d0fd",
    };

    // castlib: a model named "a\0b", saved (reads back as "a"); a string property with no terminator (castlib hangs).
    private const string PythonNulInString = "b11ec30cc20c9508b47d5ce6e644411d0303d9beaef1ac33294e8b2df83e6c74";
    private const string PythonUnterminated = "f8a5aa023d59ad4324152ce17164c7f927423d2a94c81eb4ad7ef9c7db3edbaa";

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    // ---- always-run: the prototype's bytes --------------------------------------------------------------------

    [Theory]
    [InlineData("synth_types")]
    [InlineData("synth_model")]
    [InlineData("synth_anim")]
    [InlineData("synth_misc")]
    [InlineData("synth_extended")]
    public void BuiltTreeMatchesPrototype(string name)
    {
        var cast = CastSynth.Build(name);
        var bytes = cast.ToBytes();
        var (sha, size, next) = Python[name];
        Assert.Equal(size, bytes.Length);
        Assert.Equal(sha, Sha(bytes));
        Assert.Equal((ulong)next, cast.Hashes.Next - CastHashSequence.Base);
        using var ms = new MemoryStream();
        cast.Write(ms);
        Assert.Equal(bytes, ms.ToArray());
        // read back → write again: identical
        Assert.Equal(bytes, CastFile.Read(bytes).ToBytes());
    }

    [Theory]
    [InlineData("edge_header")]
    [InlineData("edge_length")]
    [InlineData("edge_duplicates")]
    [InlineData("edge_strings")]
    [InlineData("edge_typeids")]
    [InlineData("edge_nodes")]
    public void EdgeStreamResavesLikePrototype(string name)
    {
        var input = CastSynth.Edges()[name];
        Assert.Equal(PythonEdges[name].Input, Sha(input));
        var resaved = CastFile.Read(input).ToBytes();
        Assert.Equal(PythonEdges[name].Resaved, Sha(resaved));
        Assert.Equal(resaved, CastFile.Read(resaved).ToBytes());   // castlib's own resave reads back and writes unchanged
    }

    /// <summary>
    /// castlib writes <c>SetName("a\0b")</c> as <c>a\0b\0</c> (this stream, byte for byte) and reads the name back as "a";
    /// so does the port. The port refuses to write such a string (<see cref="TypedBuffersAndAccessors"/>).
    /// </summary>
    [Fact]
    public void NulInStringReadsAsPrototype()
    {
        var bytes = CastSynth.File([CastSynth.Node(Root.Id, CastHashSequence.Base, [],
            [CastSynth.Node(CastModel.Id, CastHashSequence.Base + 1, [CastSynth.Prop("s\0"u8, "n", 1, "a\0b\0"u8.ToArray())], [])])]);
        Assert.Equal(PythonNulInString, Sha(bytes));
        var cast = CastFile.Read(bytes);
        Assert.Single(cast.RootNodes);
        Assert.Equal("a", cast.Roots()[0].ChildOfType<CastModel>()!.Name());
    }

    [Fact]
    public void EdgeStreamsReadAsPrototype()
    {
        var e = CastSynth.Edges();
        var dup = CastFile.Read(e["edge_duplicates"]).RootNodes[0];
        Assert.Equal(["n", "x", "y"], dup.Properties.Keys);
        Assert.Equal("two", dup.Property("n")!.StringValue);
        Assert.Equal(new uint[] { 3, 4 }, dup.Property("x")!.Integers);

        var strings = CastFile.Read(e["edge_strings"]).RootNodes[0];
        Assert.Equal("counted zero", strings.Property("zero")!.StringValue);
        Assert.Equal(1, strings.Property("three")!.Count);
        Assert.Equal("é", strings.Property("after")!.StringValue);

        var ids = CastFile.Read(e["edge_typeids"]).RootNodes[0];
        Assert.Equal(CastPropertyType.Byte, ids.Property("lead")!.Type);
        Assert.Equal([2.5f], ids.Property("trail")!.Floats);

        var nodes = CastFile.Read(e["edge_nodes"]);
        Assert.IsType<CastMesh>(nodes.RootNodes[0]);
        Assert.Equal(ulong.MaxValue, nodes.RootNodes[0].Hash);
        Assert.Equal(typeof(CastNode), nodes.RootNodes[1].GetType());
        Assert.Equal(0x64636261u, nodes.RootNodes[1].ChildNodes[0].Identifier);
        Assert.Equal(-1, ((Bone)nodes.RootNodes[2].ChildNodes[0]).ParentIndex());
    }

    [Theory]
    [InlineData("unknown_prop")]
    [InlineData("unknown_prop_root")]
    public void UnknownPropertyTypeKeptVerbatim(string name)
    {
        var input = CastSynth.UnknownProperty()[name];
        Assert.Equal(PythonUnknown[name], Sha(input));
        var cast = CastFile.Read(input);
        Assert.Equal(input, cast.ToBytes());
        var opaque = cast.RootNodes[0].IsOpaque ? cast.RootNodes[0] : cast.RootNodes[0].ChildNodes[0];
        Assert.True(opaque.IsOpaque);
        Assert.Throws<InvalidOperationException>(() => opaque.CreateProperty("x", "b"));
        Assert.Throws<InvalidOperationException>(() => opaque.RemoveProperty("n"));
        if (name == "unknown_prop")
        {
            Assert.IsType<CastModel>(opaque);
            Assert.Equal("before", ((CastModel)opaque).Name());   // what parsed before the unknown type is readable
            var sibling = (CastModel)cast.RootNodes[0].ChildNodes[1];
            Assert.False(sibling.IsOpaque);
            Assert.Equal([2], sibling.Property("ok")!.Bytes);
            // the tree around it stays editable, and the opaque node is re-emitted inside the new lengths
            sibling.SetName("renamed");
            var edited = CastFile.Read(cast.ToBytes());
            Assert.Equal(opaque.RawBody, edited.RootNodes[0].ChildNodes[0].RawBody);
            Assert.Equal("renamed", ((CastModel)edited.RootNodes[0].ChildNodes[1]).Name());
        }
        else Assert.Empty(cast.RootNodes[1].Properties);
    }

    [Fact]
    public void UnknownPropertyTypeUnboundedIsRefused()
    {
        // unknown type in a node whose length field runs past the end: nothing bounds the undecodable bytes
        var bytes = CastSynth.File([CastSynth.Node(Root.Id, 1, [CastSynth.Prop("zz"u8, "q", 1, [1, 2, 3])], [], length: 4096)]);
        Assert.Throws<CastFormatException>(() => CastFile.Read(bytes));
    }

    [Fact]
    public void IdentifiersPerSpec()
    {
        foreach (var (name, id, type) in new (string, uint, Type)[]
                 {
                     ("root", Root.Id, typeof(Root)), ("modl", CastModel.Id, typeof(CastModel)), ("skel", Skeleton.Id, typeof(Skeleton)),
                     ("bone", Bone.Id, typeof(Bone)), ("mesh", CastMesh.Id, typeof(CastMesh)), ("matl", Material.Id, typeof(Material)),
                     ("file", CastFileNode.Id, typeof(CastFileNode)), ("meta", Metadata.Id, typeof(Metadata)), ("hair", Hair.Id, typeof(Hair)),
                     ("blsh", BlendShape.Id, typeof(BlendShape)), ("anim", Animation.Id, typeof(Animation)), ("curv", Curve.Id, typeof(Curve)),
                     ("CMOV", CurveModeOverride.Id, typeof(CurveModeOverride)), ("ntif", NotificationTrack.Id, typeof(NotificationTrack)),
                     ("ikhd", IKHandle.Id, typeof(IKHandle)), ("cnst", Constraint.Id, typeof(Constraint)), ("colr", Color.Id, typeof(Color)),
                     ("inst", Instance.Id, typeof(Instance)),
                 })
        {
            var le = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(le, id);
            Assert.Equal(name, Encoding.ASCII.GetString(le));
            var read = CastFile.Read(CastSynth.File([CastSynth.Node(id, 1, [], [])])).RootNodes[0];
            Assert.Equal(type, read.GetType());
            Assert.Equal(id, read.Identifier);
        }
    }

    // ---- always-run: API semantics ----------------------------------------------------------------------------

    [Fact]
    public void PropertyOrderFollowsPythonDict()
    {
        var c = new CastFile();
        var m = c.CreateRoot().CreateModel();
        m.SetName("first");
        m.SetScale([2f, 2f, 2f]);
        m.CreateProperty("bp_x", "i").SetValues(new uint[] { 1 });
        m.SetName("second");                       // replaced in place
        Assert.Equal(["n", "s", "bp_x"], m.Properties.Keys);
        m.RemoveProperty("s");
        m.SetScale([3f, 3f, 3f]);                  // re-created at the end
        Assert.Equal(["n", "bp_x", "s"], m.Properties.Keys);
        Assert.Equal("second", m.Name());
        m.CreateProperty("bp_x", "4v");            // same name, new type
        Assert.Equal(CastPropertyType.Vector4, m.Property("bp_x")!.Type);
        Assert.Equal(1, m.Properties.Keys.ToList().IndexOf("bp_x"));
    }

    [Fact]
    public void TypedBuffersAndAccessors()
    {
        var c = new CastFile();
        var mdl = c.CreateRoot().CreateModel();
        var mesh = mdl.CreateMesh();
        mesh.SetFaceBuffer([0, 1, 255]);
        Assert.Equal("b", mesh.Property("f")!.Identifier);
        mesh.SetFaceBuffer([0, 1, 256]);
        Assert.Equal("h", mesh.Property("f")!.Identifier);
        mesh.SetFaceBuffer([0, 65536, 2]);
        Assert.Equal("i", mesh.Property("f")!.Identifier);
        Assert.Equal(new uint[] { 0, 65536, 2 }, mesh.FaceBuffer());
        Assert.Equal(1, mesh.FaceCount());
        Assert.Throws<ArgumentException>(() => mesh.SetFaceBuffer([]));   // castTypeForMaximum([]) raises too

        mesh.SetVertexColorBuffer(0, Array.Empty<uint>());
        Assert.Equal("4v", mesh.Property("c0")!.Identifier);             // castlib's choice for an empty list
        mesh.SetVertexColorBuffer(1, new uint[] { 0xFF0000FF });
        Assert.True(mesh.VertexColorLayerBufferPacked(1));
        Assert.False(mesh.VertexColorLayerBufferPacked(0));
        Assert.True(mesh.VertexColorLayerBufferPacked(5));

        Assert.Throws<ArgumentException>(() => mesh.SetVertexPositionBuffer(new float[4]));
        mesh.SetVertexPositionBuffer([new System.Numerics.Vector3(1, 2, 3), new System.Numerics.Vector3(4, 5, 6)]);
        Assert.Equal(2, mesh.VertexCount());
        Assert.Equal([1f, 2f, 3f, 4f, 5f, 6f], mesh.VertexPositionBuffer()!);
        Assert.Equal("linear", mesh.SkinningMethod());
        Assert.Equal(0, mesh.ColorLayerCount());
        mesh.CreateProperty("vc", "i");
        Assert.Equal(1, mesh.ColorLayerCount());

        var mat = mdl.CreateMaterial();
        var file = mat.CreateFile();
        file.SetPath("t.png");
        mat.SetName("m");
        mat.SetSlot("albedo", file.Hash);
        mat.CreateProperty("bp_material_slot", "i").SetValues(new uint[] { 3 });
        mesh.SetMaterial(mat.Hash);
        Assert.Same(mat, mesh.Material());
        var slots = mat.Slots();
        Assert.Equal(["albedo", "bp_material_slot"], slots.Keys);         // castlib lists custom properties too
        Assert.Same(file, slots["albedo"]);
        Assert.Null(slots["bp_material_slot"]);

        var bone = mdl.CreateSkeleton().CreateBone();
        Assert.Equal(-1, bone.ParentIndex());
        Assert.True(bone.SegmentScaleCompensate());
        bone.SetParentIndex(-1);
        Assert.Equal(new uint[] { 0xFFFFFFFF }, bone.Property("p")!.Integers);
        Assert.Equal(-1, bone.ParentIndex());
        bone.SetParentIndex(299);
        Assert.Equal(299, bone.ParentIndex());

        var p = mesh.CreateProperty("s", "s");
        Assert.Throws<ArgumentException>(() => p.SetString("a\0b"));   // castlib writes it and reads back "a"
        Assert.Throws<InvalidOperationException>(() => p.Integers);
        Assert.Throws<InvalidOperationException>(() => c.ToBytes());      // string property with no value
        p.SetString("ok");
        Assert.Throws<ArgumentException>(() => mesh.CreateProperty("x", "q"));
        Assert.Throws<InvalidOperationException>(() => mesh.CreateProperty("x", "b").SetValues(new uint[] { 1 }));

        var back = CastFile.Read(c.ToBytes());
        var mesh2 = back.RootNodes[0].ChildOfType<CastModel>()!.Meshes()[0];
        Assert.Equal(mesh.Hash, mesh2.Hash);
        Assert.Equal(mat.Hash, mesh2.Material()!.Hash);
    }

    [Fact]
    public void ConstraintOffsetOtherLengthsWriteNothing()
    {
        var cn = new CastFile().CreateRoot().CreateModel().CreateSkeleton().CreateConstraint();
        cn.SetCustomOffset([1f, 2f]);
        Assert.Null(cn.CustomOffset());
        cn.SetCustomOffset([1f, 2f, 3f, 4f]);
        Assert.Equal("4v", cn.Property("co")!.Identifier);
        Assert.Equal(1.0f, cn.Weight());
    }

    [Fact]
    public void HashSequence()
    {
        var c = new CastFile();
        var root = c.CreateRoot();
        var model = root.CreateModel();
        Assert.Equal(CastHashSequence.Base, root.Hash);
        Assert.Equal(CastHashSequence.Base + 1, model.Hash);
        // shared sequence: a second file continues where the first stopped, as castlib's global counter does
        var d = new CastFile(c.Hashes);
        Assert.Equal(CastHashSequence.Base + 2, d.CreateRoot().Hash);
        // loading takes one hash per node read
        var loaded = CastFile.Read(c.ToBytes());
        Assert.Equal(CastHashSequence.Base + 2, loaded.Hashes.Next);
        Assert.Equal(CastHashSequence.Base + 2, loaded.CreateRoot().Hash);
    }

    [Fact]
    public void CastColorMatchesPrototype()
    {
        // values printed by the prototype (castlib.CastColor)
        Assert.Equal(1.7491535384383408, CastColor.LinearToSRGB(0.5), 15);
        Assert.Equal(0.21404114048223255, CastColor.SRGBToLinear(0.5), 15);
        Assert.Equal(16744217u, CastColor.ToInteger((0.1, 0.5, 1.2, -0.3)));
        Assert.Equal((0.00392156862745098, 0.25098039215686274, 1.0, 0.5019607843137255), CastColor.FromInteger(0x80FF4001));
    }

    [Fact]
    public void MalformedFilesAreRefused()
    {
        var good = CastSynth.File([CastSynth.Node(Root.Id, 1, [CastSynth.Prop("s\0"u8, "n", 1, "abc\0"u8.ToArray())], [])]);
        Assert.Equal("abc", ((Root)CastFile.Read(good).RootNodes[0]).Property("n")!.StringValue);

        var unterminated = good[..^1];                                            // castlib loops forever on this one
        Assert.Throws<CastFormatException>(() => CastFile.Read(unterminated));
        // the prototype fixture script's own unterminated stream (node length fitted to it)
        var proto = CastSynth.File([CastSynth.Node(Root.Id, 1, [CastSynth.Prop("s\0"u8, "n", 1, "abc"u8.ToArray())], [])]);
        Assert.Equal(PythonUnterminated, Sha(proto));
        Assert.Throws<CastFormatException>(() => CastFile.Read(proto));
        Assert.Throws<CastFormatException>(() => CastFile.Read(good[..20]));      // truncated node header
        Assert.Throws<CastFormatException>(() => CastFile.Read(good[..10]));      // truncated file header
        var magic = (byte[])good.Clone(); magic[0] = (byte)'x';
        Assert.Throws<CastFormatException>(() => CastFile.Read(magic));
        var version = (byte[])good.Clone(); version[4] = 2;
        Assert.Throws<CastFormatException>(() => CastFile.Read(version));
        var roots = (byte[])good.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(roots.AsSpan(8), 4097);
        Assert.Throws<CastFormatException>(() => CastFile.Read(roots));
        var name = CastSynth.File([CastSynth.Node(Root.Id, 1, [CastSynth.Prop("b\0"u8, [0xFF], 1, [1])], [])]);
        Assert.Throws<CastFormatException>(() => CastFile.Read(name));            // name not UTF-8
        var overrun = CastSynth.File([CastSynth.Node(Root.Id, 1, [CastSynth.Prop("i\0"u8, "v", 1000, [1, 2, 3, 4])], [])]);
        Assert.Throws<CastFormatException>(() => CastFile.Read(overrun));
        var children = CastSynth.File([CastSynth.Node(Root.Id, 1, [], [], childCount: 1_000_000)]);
        Assert.Throws<CastFormatException>(() => CastFile.Read(children));
    }

    [Fact]
    public void SaveAndLoadThroughAFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nr-cast-{Guid.NewGuid():N}.cast");
        try
        {
            var cast = CastSynth.Build("synth_anim");
            cast.Save(path);
            Assert.Equal(Python["synth_anim"].Sha, Sha(System.IO.File.ReadAllBytes(path)));
            var back = CastFile.Load(path);
            var anim = back.Roots()[0].ChildOfType<Animation>()!;
            Assert.Equal("walk", anim.Name());
            Assert.Equal(30f, anim.Framerate());
            Assert.True(anim.Looping());
            Assert.Equal(3, anim.Curves().Count);
            Assert.Equal("h", anim.Curves()[1].Property("kb")!.Identifier);
            Assert.Equal(0.5f, anim.Curves()[0].AdditiveBlendWeight());
            Assert.Equal(1.0f, anim.Curves()[1].AdditiveBlendWeight());
            Assert.Equal(new byte[] { 1, 0 }, anim.Curves()[2].KeyValueBuffer());
            Assert.Equal(new uint[] { 5, 25, 45 }, anim.Notifications()[0].KeyFrameBuffer());
            Assert.True(anim.CurveModeOverrides()[0].OverrideScaleCurves());
            Assert.False(anim.CurveModeOverrides()[0].OverrideRotationCurves());
        }
        finally { System.IO.File.Delete(path); }
    }

    // ---- install-backed: real meshes exported by the port --------------------------------------------------------

    /// <summary>
    /// A shipped mesh exported to Cast (what `nr mesh export` writes) reads back and writes the same bytes, and has the
    /// shape the importer relies on: y up, the model named after the mesh, one bone per entity in order, per-mesh vertex
    /// ids, UVs, in-range faces and a material.
    /// </summary>
    [Theory]
    [MemberData(nameof(CastExportTests.ExportedMeshes), MemberType = typeof(CastExportTests))]
    public void MeshExportsReadAndRewrite(string game, string name)
    {
        var (m, p) = MeshFixtures.Load(game, name);
        var input = CastExport.Build(m, p.Name).Cast.ToBytes();
        var cast = CastFile.Read(input);
        Assert.Equal(input, cast.ToBytes());
        var root = cast.Roots()[0];
        Assert.Equal("y", root.ChildOfType<Metadata>()!.UpAxis());
        var mdl = root.ChildOfType<CastModel>()!;
        Assert.Equal(p.Name, mdl.Name());
        var bones = mdl.Skeleton()!.Bones();
        Assert.NotEmpty(bones);
        for (int i = 0; i < bones.Count; i++)
        {
            Assert.Equal((ulong)i, bones[i].Property("bp_entity")!.IntegerAt(0));
            Assert.InRange(bones[i].ParentIndex(), -1, i - 1);
            Assert.False(bones[i].SegmentScaleCompensate());
        }
        foreach (var mesh in mdl.Meshes())
        {
            int nv = mesh.VertexCount()!.Value;
            Assert.Equal(nv, mesh.Property("bp_vertex_id")!.ValueCount);
            Assert.Equal(nv * 2, mesh.VertexUVLayerBuffer(0)!.Length);
            Assert.All(mesh.FaceBuffer()!, f => Assert.InRange(f, 0u, (uint)nv - 1));
            Assert.IsType<Material>(mesh.Material());
        }
    }
}

/// <summary>
/// The prototype fixture script's builders, call for call (castlib → this port), and its hand-made byte streams.
/// Values come from integer arithmetic divided by a power-free constant in double, then rounded to float — the same
/// IEEE operations in both languages.
/// </summary>
internal static class CastSynth
{
    private static double Fv(long i, long k = 0) => ((i * 7919 + k * 104729) % 20011) / 1000.0 - 10.0;
    private static double Uv(long i, long k = 0) => ((i * 7919 + k * 104729) % 20011) / 20011.0;
    private static float F(long i, long k = 0) => (float)Fv(i, k);
    private static float U(long i, long k = 0) => (float)Uv(i, k);
    private static uint Iv(long i) => unchecked((uint)((ulong)i * 2654435761UL));
    private static readonly float NanF = BitConverter.Int32BitsToSingle(0x7FC00000);        // Python float('nan') packed 'f'
    private static readonly double NanD = BitConverter.Int64BitsToDouble(0x7FF8000000000000); // and 'd'

    public static CastFile Build(string name) => name switch
    {
        "synth_types" => Types(),
        "synth_model" => Model(),
        "synth_anim" => Anim(),
        "synth_misc" => Misc(),
        "synth_extended" => Extended(),
        _ => throw new ArgumentException(name),
    };

    private static float[] Flat(int n, int arity, Func<int, int, float> f)
    {
        var r = new float[n * arity];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < arity; j++) r[i * arity + j] = f(i, j);
        return r;
    }

    private static CastFile Types()
    {
        var c = new CastFile();
        var r = c.CreateRoot();
        var n = r.CreateChild(new CastNode(0x65707974, r.Hashes));
        n.CreateProperty("b1", "b").SetValues(new byte[] { 0, 1, 127, 128, 255 });
        n.CreateProperty("h1", "h").SetValues(new ushort[] { 0, 1, 0x7FFF, 0x8000, 0xFFFF });
        n.CreateProperty("i1", "i").SetValues(new uint[] { 0, 1, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF });
        n.CreateProperty("l1", "l").SetValues(new ulong[] { 0, 1, long.MaxValue, 1UL << 63, ulong.MaxValue });
        n.CreateProperty("f1", "f").SetValues(new[]
        {
            0f, -0f, 1f, -1.5f, (float)0.1, (float)1e-45, (float)3.4028234663852886e38, float.PositiveInfinity,
            float.NegativeInfinity, NanF, (float)16777217.0, (float)1.0000000596046448,
        });
        n.CreateProperty("d1", "d").SetValues(new[] { 0.0, -0.0, 0.1, 1e308, 5e-324, double.PositiveInfinity, NanD, -2.5 });
        n.CreateProperty("s1", "s").SetString("plain");
        n.CreateProperty("s2", "s").SetString("");
        n.CreateProperty("s3", "s").SetString("héllo wörld — ✓ 日本語 \U0001F389");
        n.CreateProperty("v2", "2v").SetValues(Flat(10, 1, (i, _) => F(i, 1)));
        n.CreateProperty("v3", "3v").SetValues(Flat(12, 1, (i, _) => F(i, 2)));
        n.CreateProperty("v4", "4v").SetValues(Flat(16, 1, (i, _) => F(i, 3)));
        foreach (var t in new[] { "b", "h", "i", "l", "f", "d", "2v", "3v", "4v" }) n.CreateProperty("empty_" + t, t);
        n.CreateProperty("bp_ñame ✓", "i").SetValues(new uint[] { 7 });
        n.CreateProperty("", "b").SetValues(new byte[] { 1 });
        n.CreateProperty("long_" + new string('x', 300), "h").SetValues(new ushort[] { 1, 2, 3 });
        var m = r.CreateMetadata();
        m.SetAuthor("Ångström");
        m.SetSoftware("castport synth");
        m.SetUpAxis("y");
        m.SetSceneRoot("scenes/üñí");
        return c;
    }

    private static CastFile Model()
    {
        var c = new CastFile();
        var r = c.CreateRoot();
        var meta = r.CreateMetadata();
        meta.SetUpAxis("y");
        meta.SetSoftware("nightrunner castport");
        var mdl = r.CreateModel();
        mdl.SetName("synth_model");
        mdl.SetPosition([1f, 2f, 3f]);
        mdl.SetRotation([0f, 0f, 0f, 1f]);
        mdl.SetScale([1f, 1f, 1f]);
        var skel = mdl.CreateSkeleton();
        var bones = new List<Bone>();
        for (int i = 0; i < 300; i++)
        {
            var b = skel.CreateBone();
            b.SetName($"bone_{i:000}");
            b.SetParentIndex(i % 100 == 0 ? -1 : (i * 7 + 3) % i);
            b.SetSegmentScaleCompensate(i % 2 == 0);
            b.SetLocalPosition([F(i, 1), F(i, 2), F(i, 3)]);
            b.SetLocalRotation([U(i, 4), U(i, 5), U(i, 6), U(i, 7)]);
            if (i % 7 != 0)
            {
                b.SetWorldPosition([F(i, 8), F(i, 9), F(i, 10)]);
                b.SetWorldRotation([U(i, 11), U(i, 12), U(i, 13), U(i, 14)]);
            }
            b.SetScale([1f, 1f, 1f]);
            b.CreateProperty("bp_entity", "i").SetValues(new uint[] { (uint)i });
            b.CreateProperty("bp_flags", "i").SetValues(new uint[] { Iv(i) });
            bones.Add(b);
        }
        var ik = skel.CreateIKHandle();
        ik.SetName("ik_leg");
        ik.SetStartBone(bones[1].Hash);
        ik.SetEndBone(bones[3].Hash);
        ik.SetTargetBone(bones[5].Hash);
        ik.SetTargetOffset([0.5f, -0.25f, 0.125f]);
        ik.SetPoleVectorBone(bones[7].Hash);
        ik.SetPoleBone(bones[9].Hash);
        ik.SetUseTargetRotation(true);
        var ik2 = skel.CreateIKHandle();
        ik2.SetName("ik_min");
        ik2.SetUseTargetRotation(false);
        var cn = skel.CreateConstraint();
        cn.SetName("pt");
        cn.SetConstraintType("pt");
        cn.SetConstraintBone(bones[10].Hash);
        cn.SetTargetBone(bones[11].Hash);
        cn.SetMaintainOffset(true);
        cn.SetCustomOffset([1f, 2f, 3f]);
        cn.SetWeight(0.75f);
        cn.SetSkipX(true);
        cn.SetSkipY(false);
        cn.SetSkipZ(true);
        var cn2 = skel.CreateConstraint();
        cn2.SetName("or");
        cn2.SetConstraintType("or");
        cn2.SetCustomOffset([0f, 0f, 0f, 1f]);
        cn2.SetMaintainOffset(false);
        var cn3 = skel.CreateConstraint();
        cn3.SetName("sc");
        cn3.SetCustomOffset([1f, 2f]);

        var m1 = mdl.CreateMaterial();
        m1.SetName("mat_a.mat");
        m1.SetType("pbr");
        var f1 = m1.CreateFile();
        f1.SetPath("textures/a_dif.png");
        m1.SetSlot("albedo", f1.Hash);
        var f2 = m1.CreateFile();
        f2.SetPath("textures/ä_nrm.png");
        m1.SetSlot("normal", f2.Hash);
        m1.CreateProperty("bp_material_slot", "i").SetValues(new uint[] { 0 });
        var col = m1.CreateChild(new Color(m1.Hashes));
        col.SetName("tint");
        col.SetColorSpace("linear");
        col.SetRgba([0.25f, 0.5f, 0.75f, 1f]);
        m1.SetSlot("tint", col.Hash);
        var m2 = mdl.CreateMaterial();
        m2.SetName("mat_b");

        var big = mdl.CreateMesh();
        big.SetName("big");
        big.SetMaterial(m1.Hash);
        big.SetUVLayerCount(2);
        big.SetColorLayerCount(2);
        const int n = 70000;
        big.SetVertexPositionBuffer(Flat(n, 3, (i, j) => F(i, 1 + j)));
        big.SetVertexNormalBuffer(Flat(n, 3, (i, j) => U(i, 4 + j)));
        big.SetVertexTangentBuffer(Flat(n, 3, (i, j) => U(i, 7 + j)));
        big.SetVertexUVLayerBuffer(0, Flat(n, 2, (i, j) => U(i, 10 + j)));
        big.SetVertexUVLayerBuffer(1, Flat(n, 2, (i, j) => U(i, 12 + j)));
        big.SetVertexColorBuffer(0, Enumerable.Range(0, n).Select(i => Iv(i)).ToArray());
        big.SetVertexColorBuffer(1, Flat(n, 4, (i, j) => j < 3 ? U(i, 14 + j) : 1f));
        var faces = new uint[(n - 2) * 3];
        for (int i = 0; i < n - 2; i++) for (int j = 0; j < 3; j++) faces[i * 3 + j] = (uint)(i + j);
        big.SetFaceBuffer(faces);
        big.SetMaximumWeightInfluence(4);
        big.SetSkinningMethod("linear");
        var wb = new uint[n * 4];
        for (int i = 0; i < wb.Length; i++) wb[i] = (uint)(i % 300);
        big.SetVertexWeightBoneBuffer(wb);
        big.SetVertexWeightValueBuffer(Flat(n * 4, 1, (i, _) => U(i, 17)));
        big.CreateProperty("bp_vertex_id", "i").SetValues(Enumerable.Range(0, n).Select(i => (uint)i).ToArray());
        big.CreateProperty("bp_tangent_sign", "f").SetValues(Flat(n, 1, (i, _) => i % 3 != 0 ? 1f : -1f));
        big.CreateProperty("bp_raw_00", "s").SetString("00112233445566778899aabbccddeeff");
        big.CreateProperty("bp_format", "b").SetValues(new byte[] { 3 });

        var small = mdl.CreateMesh();
        small.SetName("small_b");
        small.SetMaterial(m2.Hash);
        small.SetUVLayerCount(1);
        small.SetVertexPositionBuffer(Flat(10, 3, (i, j) => F(i, 1 + j)));
        small.SetVertexUVLayerBuffer(0, Flat(10, 2, (i, j) => U(i, 1 + j)));
        small.SetFaceBuffer([0, 1, 2, 2, 3, 9]);
        small.SetMaximumWeightInfluence(1);
        small.SetSkinningMethod("quaternion");
        small.SetVertexWeightBoneBuffer(Enumerable.Range(0, 10).Select(i => (uint)(i * 25)).ToArray());
        small.SetVertexWeightValueBuffer(Enumerable.Repeat(1f, 10).ToArray());

        var med = mdl.CreateMesh();
        med.SetName("medium_h");
        med.SetVertexPositionBuffer(Flat(1000, 3, (i, j) => F(i, 3 + j)));
        var mf = new uint[900 * 3];
        for (int i = 0; i < 900; i++) for (int j = 0; j < 3; j++) mf[i * 3 + j] = (uint)((i * 3 + j) % 1000);
        med.SetFaceBuffer(mf);
        med.SetVertexColorBuffer(0, Flat(1000, 4, (i, j) => U(i, 1 + j)));

        var empty = mdl.CreateMesh();
        empty.SetName("empty");
        empty.SetVertexColorBuffer(0, Array.Empty<uint>());
        empty.CreateProperty("vp", "3v");
        empty.CreateProperty("f", "i");

        var legacy = mdl.CreateMesh();
        legacy.SetName("legacy_vc");
        legacy.CreateProperty("vc", "i").SetValues(new uint[] { 0xFF00FF00, 0x80808080 });

        var hair = mdl.CreateHair();
        hair.SetName("hair");
        hair.SetSegmentBuffer([3, 4, 5]);
        hair.SetParticleBuffer(Flat(15, 3, (i, j) => F(i, 1 + j)));
        hair.SetMaterial(m2.Hash);
        var hair2 = mdl.CreateHair();
        hair2.SetName("hair_i");
        hair2.SetSegmentBuffer([1, 70000]);

        var bs = mdl.CreateBlendShape();
        bs.SetName("smile");
        bs.SetBaseShape(small.Hash);
        bs.SetTargetShapeVertexIndices([0, 2, 4]);
        bs.SetTargetShapeVertexPositions(Flat(3, 3, (i, j) => F(i, 6 + j)));
        bs.SetTargetWeightScale(1.5f);
        var bs2 = mdl.CreateBlendShape();
        bs2.SetName("frown");
        bs2.SetBaseShape(med.Hash);
        bs2.SetTargetShapeVertexIndices([0, 300, 999]);
        c.CreateRoot();
        return c;
    }

    private static CastFile Anim()
    {
        var c = new CastFile();
        var r = c.CreateRoot();
        var a = r.CreateAnimation();
        a.SetName("walk");
        a.SetFramerate(30f);
        a.SetLooping(true);
        var sk = a.CreateSkeleton();
        var b0 = sk.CreateBone();
        b0.SetName("root");
        b0.SetParentIndex(-1);
        var cv = a.CreateCurve();
        cv.SetNodeName("root");
        cv.SetKeyPropertyName("tx");
        cv.SetKeyFrameBuffer(Enumerable.Range(0, 200).Select(i => (uint)i).ToArray());
        cv.SetFloatKeyValueBuffer(Flat(200, 1, (i, _) => F(i)));
        cv.SetMode("absolute");
        cv.SetAdditiveBlendWeight(0.5f);
        var cv2 = a.CreateCurve();
        cv2.SetNodeName("root");
        cv2.SetKeyPropertyName("rq");
        cv2.SetKeyFrameBuffer(Enumerable.Range(0, 1000).Select(i => (uint)(i * 3)).ToArray());
        cv2.SetVec4KeyValueBuffer(Flat(1000, 4, (i, j) => U(i, 1 + j)));
        cv2.SetMode("relative");
        var cv3 = a.CreateCurve();
        cv3.SetNodeName("spine");
        cv3.SetKeyPropertyName("vis");
        cv3.SetKeyFrameBuffer([0, 70000]);
        cv3.SetByteKeyValueBuffer([1, 0]);
        cv3.SetMode("additive");
        var cmo = a.CreateCurveModeOverride();
        cmo.SetNodeName("spine");
        cmo.SetMode("additive");
        cmo.SetOverrideTranslationCurves(true);
        cmo.SetOverrideRotationCurves(false);
        cmo.SetOverrideScaleCurves(true);
        var nt = a.CreateNotification();
        nt.SetName("footstep");
        nt.SetKeyFrameBuffer([5, 25, 45]);
        var a2 = r.CreateAnimation();
        a2.SetName("idle");
        a2.SetLooping(false);
        return c;
    }

    private static CastFile Misc()
    {
        var c = new CastFile();
        var r1 = c.CreateRoot();
        var inst = r1.CreateInstance();
        inst.SetName("inst");
        var f = inst.CreateChild(new Nightrunner.Core.Cast.File(inst.Hashes));
        f.SetPath("other.cast");
        inst.SetReferenceFile(f.Hash);
        inst.SetPosition([1f, -2f, 3.5f]);
        inst.SetRotation([0f, (float)0.70710678, 0f, (float)0.70710678]);
        inst.SetScale([2f, 2f, 2f]);
        var unk = r1.CreateChild(new CastNode(0x6B6E7578, r1.Hashes));
        unk.CreateProperty("a", "b").SetValues(new byte[] { 1 });
        unk.CreateProperty("b", "h").SetValues(new ushort[] { 2 });
        unk.CreateProperty("a", "i").SetValues(new uint[] { 3 });
        var u2 = unk.CreateChild(new CastNode(0, unk.Hashes));
        u2.CreateProperty("z", "d").SetValues(new[] { 1.25 });
        u2.CreateChild(new Nightrunner.Core.Cast.Mesh(u2.Hashes)).SetName("under_unknown");
        unk.CreateChild(new CastNode(0xFFFFFFFF, unk.Hashes));
        var m = r1.CreateModel();
        m.SetName("first");
        m.SetScale([2f, 2f, 2f]);
        m.SetName("second");
        m.RemoveProperty("s");
        m.SetScale([3f, 3f, 3f]);
        var me1 = m.CreateMesh();
        me1.SetName("removed");
        var me2 = m.CreateMesh();
        me2.SetName("kept");
        m.RemoveChild(me1);
        c.CreateRoot();
        var r3 = c.CreateRoot();
        r3.CreateMetadata().SetUpAxis("z");

        Assert.Same(f, inst.ReferenceFile());
        return c;
    }

    private static CastFile Extended()
    {
        var c = CastFile.Read(Anim().ToBytes());
        var r = (Root)c.Roots()[0];
        var m = r.CreateModel();
        m.SetName("added");
        m.CreateSkeleton().CreateBone().SetName("b");
        c.CreateRoot();
        return c;
    }

    // ---- hand-made byte streams --------------------------------------------------------------------------------

    public static byte[] Prop(ReadOnlySpan<byte> typeId, string name, uint count, byte[] payload) =>
        Prop(typeId, Encoding.UTF8.GetBytes(name), count, payload);

    public static byte[] Prop(ReadOnlySpan<byte> typeId, byte[] name, uint count, byte[] payload)
    {
        var r = new byte[8 + name.Length + payload.Length];
        typeId.CopyTo(r);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(2), (ushort)name.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4), count);
        name.CopyTo(r, 8);
        payload.CopyTo(r, 8 + name.Length);
        return r;
    }

    public static byte[] Node(uint id, ulong hash, byte[][] props, byte[][] children, uint? length = null,
        uint? propertyCount = null, uint? childCount = null)
    {
        var body = props.Concat(children).SelectMany(x => x).ToArray();
        var r = new byte[24 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(r, id);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4), length ?? (uint)r.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(8), hash);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(16), propertyCount ?? (uint)props.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(20), childCount ?? (uint)children.Length);
        body.CopyTo(r, 24);
        return r;
    }

    public static byte[] File(byte[][] roots, uint reserved = 0, byte[]? trailing = null)
    {
        var head = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(head, CastFile.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8), (uint)roots.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), reserved);
        return [.. head, .. roots.SelectMany(x => x), .. trailing ?? []];
    }

    private static byte[] S0(string text) => [.. Encoding.UTF8.GetBytes(text), 0];
    private static byte[] F32(params float[] v) => [.. v.SelectMany(BitConverter.GetBytes)];

    public static Dictionary<string, byte[]> Edges()
    {
        const uint root = Root.Id;
        return new()
        {
            ["edge_header"] = File([Node(root, 5, [Prop("s\0"u8, "n", 1, S0("r"))], [])], reserved: 0xDEADBEEF,
                trailing: [1, 2, 3, 4, 5, 6, 7]),
            ["edge_length"] = File([Node(root, 6, [], [Node(CastModel.Id, 7, [Prop("b\0"u8, "x", 1, [9])], [], length: 0)],
                length: 0xFFFFFFFF)]),
            ["edge_duplicates"] = File([Node(root, 8, [Prop("s\0"u8, "n", 1, S0("one")), Prop("b\0"u8, "x", 1, [1]),
                Prop("h\0"u8, "y", 1, [2, 0]), Prop("s\0"u8, "n", 1, S0("two")), Prop("i\0"u8, "x", 2, [3, 0, 0, 0, 4, 0, 0, 0])], [])]),
            ["edge_strings"] = File([Node(root, 9, [Prop("s\0"u8, "zero", 0, S0("counted zero")),
                Prop("s\0"u8, "three", 3, S0("counted three")), Prop("s\0"u8, "after", 1, S0("é"))], [])]),
            ["edge_typeids"] = File([Node(root, 10, [Prop("\0b"u8, "lead", 1, [5]), Prop("f\0"u8, "trail", 1, F32(2.5f)),
                Prop("2v"u8, "v", 1, F32(1, 2))], [])]),
            ["edge_nodes"] = File([
                Node(CastMesh.Id, 0xFFFFFFFFFFFFFFFF, [Prop("s\0"u8, "n", 1, S0("mesh at root"))], []),
                Node(0, 0, [], [Node(0x64636261, 0x0123456789ABCDEF, [], [Node(0xFFFFFFFF, 1, [], [])])]),
                Node(root, 11, [], [Node(Bone.Id, 12, [Prop("i\0"u8, "p", 1, [0xFF, 0xFF, 0xFF, 0xFF])], [])]),
            ]),
        };
    }

    public static Dictionary<string, byte[]> UnknownProperty()
    {
        const uint root = Root.Id;
        var bad = Node(CastModel.Id, 21, [Prop("s\0"u8, "n", 1, S0("before")), Prop("zz"u8, "odd", 3, Enumerable.Repeat((byte)0xAA, 7).ToArray()),
            Prop("b\0"u8, "after", 1, [1])], [Node(CastMesh.Id, 22, [], [])]);
        return new()
        {
            ["unknown_prop"] = File([Node(root, 20, [], [bad, Node(CastModel.Id, 23, [Prop("b\0"u8, "ok", 1, [2])], [])])]),
            ["unknown_prop_root"] = File([Node(root, 30, [Prop([0xFF, 0xFE], "raw", 1, new byte[5])], []), Node(root, 31, [], [])]),
        };
    }
}
