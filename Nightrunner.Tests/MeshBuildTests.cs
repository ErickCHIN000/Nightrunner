using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Nightrunner.BuildCheck;
using Nightrunner.Core.Cast;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Mesh.ClassReader;
using File = System.IO.File;

namespace Nightrunner.Tests;

// Port of nightrunner-main/tests/test_mesh_build.py (mesh/rebuild.py, mesh/imagepatch.py, mesh/codec.py build) plus the
// mesh-writing invariants of the C# port. Samples are the prototype's, exported from the DLTB install (MeshWork).
//
// Tiers: synthetic tests always run; install-backed ones skip without a DLTB install. The rebuilt parts of BuildCheck's
// edited scenes are frozen (EditedBuildsAreFrozen); the live comparison with the prototype is tools/BuildCheck.
//
// Divergences from the prototype's tests:
//   test_new_material_within_capacity / test_new_material_refused_when_full — the prototype writes a new material into
//     entry `count` (a skin-only material) or refuses a full table; C# grows the class-11 table (NewMaterialGrowsTable,
//     NewMaterialOnFullTableGrows, NewMaterialKeepsSkins).
//   test_delete_triangle_relayout_and_pack_build — the rpack build + validate half belongs to the pack writer; the
//     layout half and the phase-1 re-encode consistency are here.
//   test_cli_import_and_diff — there is no C# CLI.
public class MeshBuildTests
{
    private const string Pants = MeshImportTests.Pants;
    private const string Box = MeshImportTests.Box;
    private const string Hair = "sh_npc_ft_crane_hair_a";
    private const string Aiden = "sh2_npc_aiden_beast";
    private const string Crane = "sh2_npc_crane";
    private const string Sedan = "veh_sedan_a";

    private static MeshModel Decode(string name, IReadOnlyDictionary<byte, byte[]> p) =>
        MeshDecoder.Decode(name, p[0x10], p[0x11], p.GetValueOrDefault((byte)0xF0), p.GetValueOrDefault((byte)0xF1),
                           p.GetValueOrDefault((byte)0x12), p.GetValueOrDefault((byte)0xF3));

    // ---- synthetic --------------------------------------------------------------------------------------------------

    [Fact]
    public void QtangentRoundTripIdentity()
    {
        Span<float> t = stackalloc float[3], n = stackalloc float[3];
        Vertex.DecodeQtangent(0, 0, 0, 32767, 32767f, t, n);
        Assert.Equal((0, 0, 0, 32767), Vertex.EncodeQtangent(t[0], t[1], t[2], n[0], n[1], n[2], 1, 6));
        // forcing the other handedness negates the quad and keeps the frame
        var q2 = Vertex.EncodeQtangent(t[0], t[1], t[2], n[0], n[1], n[2], -1, 6);
        Assert.Equal(-1, Vertex.QtangentSign(q2.X, q2.Y, q2.Z, q2.W));
        Span<float> t2 = stackalloc float[3], n2 = stackalloc float[3];
        Vertex.DecodeQtangent(q2.X, q2.Y, q2.Z, q2.W, 32767f, t2, n2);
        for (int k = 0; k < 3; k++)
        {
            Assert.Equal(t[k], t2[k], 4);
            Assert.Equal(n[k], n2[k], 4);
        }
    }

    [Fact]
    public void QuantizeWeights()
    {
        float[][] w = [[0.5f, 0.5f, 0, 0], [1, 0, 0, 0], [0.333f, 0.333f, 0.334f, 0], [0, 0, 0, 0], [0.1f, 0.2f, 0.3f, 0.4f]];
        var q = w.Select(r => { var d = new byte[4]; Vertex.QuantizeWeights(r, d); return d; }).ToList();
        Assert.Equal([255, 255, 255, 0, 255], q.Select(r => r.Sum(x => x)));
        Assert.Equal([255, 0, 0, 0], q[1]);
        Assert.Equal([128, 127, 0, 0], q[0]);
    }

    [Fact]
    public void HoleDefaults()
    {
        static byte[] Window(params uint[] tails)
        {
            var b = new byte[tails.Length * 32];
            for (int i = 0; i < tails.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 32 + 28), tails[i]);
            return b;
        }
        var h = Vertex.HoleDefaults(3, Window(7, 5, 7));
        Assert.Equal((0x3C00, 7u, 40), (h.Raw06, h.RawTail, h.RawExt.Length));
        Assert.Equal(5u, Vertex.HoleDefaults(3, Window(7, 5, 5)).RawTail);
        Assert.Equal(5u, Vertex.HoleDefaults(3, Window(7, 5)).RawTail);      // a tie takes the smallest value (np.unique order)
        Assert.Equal(0u, Vertex.HoleDefaults(3, null).RawTail);
    }

    [Fact]
    public void PlanNewLayoutRule()
    {
        var (bases, vs, isz) = MeshRebuild.PlanNewLayout([(6, 23291, 45882), (6, 9821, 13761)]);
        Assert.Equal([(0L, 0L), (931680L, 91764L)], bases);
        Assert.Equal(Vertex.AlignTo(931680 + 9821 * 40, 160), vs);
        Assert.Equal(Vertex.AlignTo(91764 + 13761 * 2, 16), isz);
        var (_, _, exact) = MeshRebuild.PlanNewLayout([(0, 3, 3)], padIndexPart: false);
        Assert.Equal(6, exact);
    }

    /// <summary>Invariant: the embedded name is <c>&lt;logical&gt;.msh</c>; a name already ending in .msh (any ASCII case) is kept.</summary>
    [Fact]
    public void EmbeddedNameRule()
    {
        Assert.Equal("my_box.msh"u8.ToArray(), MeshIdentity.ExpectedEmbeddedName("my_box"));
        Assert.Equal("A.MSH"u8.ToArray(), MeshIdentity.ExpectedEmbeddedName("A.MSH"));
        Assert.Equal("x.msh.msh"u8.ToArray(), MeshIdentity.ExpectedEmbeddedName("x.msh.msh"));
        Assert.Equal("b.mshx.msh"u8.ToArray(), MeshIdentity.ExpectedEmbeddedName("b.mshx"));
    }

    /// <summary>Invariant: DLTB only — a DL2 mesh is refused by name, before the scene is read.</summary>
    [Fact]
    public void Dl2MeshWritingRefused()
    {
        var (image, fixups, vertex, index) = MeshSynth.SkinnedDl2();
        var parts = new Dictionary<byte, byte[]> { [0x10] = image, [0x11] = fixups, [0xF0] = vertex, [0xF1] = index };
        var e = Assert.Throws<MeshBuildException>(() => MeshBuild.Build(parts, "synth_dl2", Path.Combine(Path.GetTempPath(), "no-such-scene.cast"), null));
        Assert.Contains("DL2 mesh writing is not supported", e.Message);
    }

    [Fact]
    public void ImagePatchAppendRetargetFinish()
    {
        var (image, fixups, _, _) = MeshSynth.SkinnedDl2();
        var img = new Image(image, Fixups.Parse(fixups));
        var patch = new ImagePatch(img);
        int root = (int)img.Records[0].Offset;
        int off = patch.AppendString("renamed.msh"u8);
        patch.Retarget(root, off);
        int arr = patch.Append(new byte[16], 8);
        patch.Retarget(root + 0x48, arr, kind: 0);                   // a new slot
        var (ni, nf) = patch.Finish();
        Assert.Equal(0, ni.Length % 16);
        Assert.Equal("renamed.msh"u8.ToArray(), Image.EmbeddedName(ni, nf));
        var fx = Fixups.Parse(nf);
        Assert.Equal(img.Fixups.Slots.Count + 1, fx.Slots.Count);
        Assert.Equal(fx.Slots.Select(s => s.Offset).Order(), fx.Slots.Select(s => s.Offset));
        Assert.Equal(0, nf.Length % 16);
        Assert.Equal(arr, new Image(ni, fx).Target(root + 0x48));
        for (int i = 0; i < img.Size; i++)                                   // only the two pointer words changed
            if (i < root || i >= root + 8 && (i < root + 0x48 || i >= root + 0x50)) Assert.Equal(image[i], ni[i]);
    }

    // ---- unedited round trip ----------------------------------------------------------------------------------------

    /// <summary>Invariant: export → build with no edits is byte-identical for every part (0x10/0x11/0xF0/0xF1, skins carried).</summary>
    [Fact]
    public void AllSamplesByteIdentical()
    {
        int n = 0;
        foreach (var name in MeshImportTests.Samples.Concat(["player_kc_basic_torso_a_tpp", Sedan, "wn_pistol_b_b"]).Distinct())
        {
            using var w = MeshWork.Export(name);
            var r = MeshBuild.Build(w.Parts.ByType, w.Name, w.CastPath, w.Sidecar);
            foreach (var (t, b) in w.Parts.ByType) Assert.True(b.AsSpan().SequenceEqual(r.Parts[t]), $"{name} 0x{t:X2}");
            Assert.Empty(r.Warnings);
            Assert.False(r.Report["layout_changed"]!.GetValue<bool>());
            Assert.All(r.Report["entries"]!.AsArray(), e => Assert.Contains(e!["path"]!.GetValue<string>(), new[] { "in-place", "untouched" }));
            n++;
        }
        Assert.Equal(19, n);
    }

    /// <summary>A glTF edit goes through the splitter's NormalizeMeshScene (the prototype's load_edit_scene).</summary>
    [Fact]
    public void GltfSceneBuilds()
    {
        foreach (var name in new[] { Box, Pants })
        {
            using var w = MeshWork.Export(name);
            var (c, mdl) = w.LoadCast();
            string glb = Path.Combine(w.Dir, "edit.glb");
            Gltf.Save(c, glb);
            var r = MeshBuild.Build(w.Parts.ByType, w.Name, glb, w.Sidecar);
            foreach (var (t, b) in w.Parts.ByType) Assert.True(b.AsSpan().SequenceEqual(r.Parts[t]), $"{name} 0x{t:X2}");
            var mesh = mdl.Meshes()[0];
            var vp = mesh.VertexPositionBuffer()!;
            vp[1] += 0.01f;
            mesh.SetVertexPositionBuffer(vp);
            Gltf.Save(c, glb);
            int vid = (int)mesh.Property("bp_vertex_id")!.NumberAt(0);
            r = MeshBuild.Build(w.Parts.ByType, w.Name, glb, w.Sidecar);
            var v = Decode("x", r.Parts).GeometryEntries[0].Vertices!;
            Assert.Equal(w.Model.GeometryEntries[0].Vertices!.Positions[vid * 3 + 1] + 0.01f, v.Positions[vid * 3 + 1], 2);
        }
    }

    [Fact]
    public void BuildWithoutSidecarDerivesIt()
    {
        using var w = MeshWork.Export(Hair);
        var r = MeshBuild.Build(w.Parts.ByType, w.Name, w.CastPath, null);
        foreach (var (t, b) in w.Parts.ByType) Assert.True(b.AsSpan().SequenceEqual(r.Parts[t]), $"0x{t:X2}");
    }

    [Fact]
    public void PlanIsNoopForUneditedCast()
    {
        using var w = MeshWork.Export(Hair);
        var imp = CastImport.Resolve(CastImport.Read(w.CastPath), w.Sidecar);
        var plan = MeshRebuild.MakePlan(w.Model, imp, w.Name);
        Assert.False(plan.LayoutChanged);
        Assert.All(plan.Entries, p => Assert.False(p.Changed));
        Assert.Null(plan.IdentityRename);
        var (bases, _, _) = MeshRebuild.PlanNewLayout(w.Model.GeometryEntries.Select(e => (e.Format, e.VertexCount, e.IndexCount)));
        Assert.Equal([(0L, 0L), (931680L, 91764L)], bases);
    }

    // ---- edits ------------------------------------------------------------------------------------------------------

    [Fact]
    public void MoveOneVertex()
    {
        using var w = MeshWork.Export(Pants);
        var p = w.Parts.ByType;
        var (c, mdl) = w.LoadCast();
        var mesh = mdl.Meshes()[0];
        var vp = mesh.VertexPositionBuffer()!;
        vp[0] += 0.01f;
        mesh.SetVertexPositionBuffer(vp);
        int vid = (int)mesh.Property("bp_vertex_id")!.NumberAt(0);
        var r = w.Build(c);
        Assert.Equal(p[0x10], r.Parts[0x10]);
        Assert.Equal(p[0x11], r.Parts[0x11]);
        Assert.Equal(p[0xF1], r.Parts[0xF1]);
        int stride = Vertex.Stride(6);
        var diff = Enumerable.Range(0, p[0xF0].Length).Where(i => p[0xF0][i] != r.Parts[0xF0][i]).ToList();
        Assert.NotEmpty(diff);
        Assert.All(diff, i => Assert.InRange(i, vid * stride, vid * stride + 11));      // unknown bytes kept verbatim
        var m2 = Decode("x", r.Parts);
        Assert.Equal(w.Model.GeometryEntries[0].Vertices!.Positions[vid * 3] + 0.01f, m2.GeometryEntries[0].Vertices!.Positions[vid * 3], 6);
        var s0 = r.Report["entries"]![0]!["submeshes"]![0]!;
        Assert.Equal((1, 1, 0), (s0["moved"]!.GetValue<int>(), s0["edited"]!.GetValue<int>(), s0["new_vertices"]!.GetValue<int>()));
        Assert.Equal("in-place", r.Report["entries"]![0]!["path"]!.GetValue<string>());
        Assert.Equal([0], r.Report["bounds_rewritten"]!.AsArray().Select(x => x!.GetValue<int>()));
    }

    [Fact]
    public void DeleteTriangleRelayout()
    {
        using var w = MeshWork.Export(Pants);
        var p = w.Parts.ByType;
        var (c, mdl) = w.LoadCast();
        var mesh = mdl.Meshes()[0];
        var f = mesh.FaceBuffer()!;
        mesh.SetFaceBuffer(f[3..]);
        var r = w.Build(c);
        var rep = r.Report;
        Assert.True(rep["layout_changed"]!.GetValue<bool>());
        Assert.Equal([3840, 3837], rep["entries"]![0]!["index_count"]!.AsArray().Select(x => x!.GetValue<int>()));
        Assert.Equal([993, 993], rep["entries"]![0]!["vertex_count"]!.AsArray().Select(x => x!.GetValue<int>()));
        Assert.Equal(p[0xF0], r.Parts[0xF0]);                                   // window untouched
        Assert.Equal(Vertex.AlignTo(3837 * 2, 16), r.Parts[0xF1].Length);       // index part padded to 16
        var m2 = Decode("x", r.Parts);
        Assert.Empty(m2.Warnings);
        var e = m2.GeometryEntries[0];
        int c0 = w.Model.GeometryEntries[0].Submeshes[0].IndexCount, c1 = w.Model.GeometryEntries[0].Submeshes[1].IndexCount;
        Assert.Equal([c0 - 3, c1], e.Submeshes.Select(s => s.IndexCount));
        Assert.Equal([0L, (c0 - 3) * 2L], e.Submeshes.Select(s => s.IndexBase));
        var ids = mesh.Property("bp_vertex_id")!.ToUInt32Array();
        Assert.Equal(f[3..].Select(x => (ushort)ids[x]), e.Submeshes[0].Indices);
        // the phase-1 re-encode of the rebuilt parts reproduces them (layout rule consistency)
        foreach (var (t, bytes) in MeshEncoder.Reencode(m2)) Assert.Equal(r.Parts[t], bytes);
    }

    [Fact]
    public void BlenderLikeCastRebuilds()
    {
        using var w = MeshWork.Export(Hair);                  // two LOD entries
        var p = w.Parts.ByType;
        var (c, mdl) = w.LoadCast();
        MeshImportTests.StripBlenderLike(mdl);
        var r = w.Build(c);
        var ents = r.Report["entries"]!.AsArray();
        Assert.Equal(["rebuild", "rebuild"], ents.Select(e => e!["path"]!.GetValue<string>()));
        Assert.Equal([23291, 23291, 9821, 9821], ents.SelectMany(e => e!["vertex_count"]!.AsArray().Select(x => x!.GetValue<int>())));
        Assert.Equal([45882, 45882, 13761, 13761], ents.SelectMany(e => e!["index_count"]!.AsArray().Select(x => x!.GetValue<int>())));
        Assert.Contains(r.Warnings, x => x.Contains("recovered by exact position match"));
        var subs = ents.SelectMany(e => e!["submeshes"]!.AsArray()).ToList();
        Assert.All(subs, s => Assert.Equal(0, s!["new_vertices"]!.GetValue<int>()));
        Assert.All(subs, s => Assert.Equal("name", s!["matched_by"]!.GetValue<string>()));
        Assert.Equal(p[0xF1], r.Parts[0xF1]);                 // compaction order == window order for this mesh
        Assert.Equal(p[0xF0].Length, r.Parts[0xF0].Length);
        var m2 = Decode("x", r.Parts);
        Assert.Empty(m2.Warnings);
        for (int k = 0; k < 2; k++)
        {
            var a = w.Model.GeometryEntries[k].Vertices!;
            var b = m2.GeometryEntries[k].Vertices!;
            Assert.Equal(a.Positions, b.Positions);
            Assert.Equal(a.Joints, b.Joints);
            var o = Vertex.OffsetsOf(6);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.True(a.Raw.AsSpan(i * 40 + o.Weights, 4).SequenceEqual(b.Raw.AsSpan(i * 40 + o.Weights, 4)));
                Assert.True(a.Raw.AsSpan(i * 40 + 36, 4).SequenceEqual(b.Raw.AsSpan(i * 40 + 36, 4)));
            }
            // frames re-quantised from UV-derived tangents: normals preserved closely
            int close = 0;
            for (int i = 0; i < a.Count; i++)
                if (a.Normals[i * 3] * b.Normals[i * 3] + a.Normals[i * 3 + 1] * b.Normals[i * 3 + 1] + a.Normals[i * 3 + 2] * b.Normals[i * 3 + 2] > 0.999) close++;
            Assert.True(close > 0.99 * a.Count);
        }
    }

    private static float[] Grow(float[] a, int w) => [.. a, .. a[..w]];

    [Fact]
    public void AddedVertexUsesHolePolicyAndRelayouts()
    {
        using var w = MeshWork.Export(Box);                   // format 0 (raw_06 hole), static
        var (c, mdl) = w.LoadCast();
        var mesh = mdl.Meshes()[0];
        int n = mesh.VertexCount()!.Value;
        mesh.SetVertexPositionBuffer(Grow(mesh.VertexPositionBuffer()!, 3));
        mesh.SetVertexNormalBuffer(Grow(mesh.VertexNormalBuffer()!, 3));
        mesh.SetVertexTangentBuffer(Grow(mesh.VertexTangentBuffer()!, 3));
        mesh.SetVertexUVLayerBuffer(0, Grow(mesh.VertexUVLayerBuffer(0)!, 2));
        mesh.CreateProperty("bp_tangent_sign", CastPropertyType.Float).SetValues([.. mesh.Property("bp_tangent_sign")!.ToSingleArray(), 1f]);
        var f = mesh.FaceBuffer()!;
        mesh.SetFaceBuffer([.. f, (uint)n, 1, 2]);
        mesh.RemoveProperty("bp_vertex_id");                 // no ids → recovery by position; the copy of vertex 0 recovers id 0
        var r = w.Build(c);
        var e0 = r.Report["entries"]![0]!;
        Assert.Equal("rebuild", e0["path"]!.GetValue<string>());
        Assert.Equal([24, 25], e0["vertex_count"]!.AsArray().Select(x => x!.GetValue<int>()));
        Assert.Equal([36, 39], e0["index_count"]!.AsArray().Select(x => x!.GetValue<int>()));
        Assert.True(r.Report["layout_changed"]!.GetValue<bool>());
        Assert.Equal(Vertex.AlignTo(25 * 16, 160), r.Parts[0xF0].Length);
        var m2 = Decode("x", r.Parts);
        Assert.Empty(m2.Warnings);
        var v = m2.GeometryEntries[0].Vertices!;
        for (int i = 0; i < v.Count; i++) Assert.Equal(0x3C00, BinaryPrimitives.ReadUInt16LittleEndian(v.Raw.AsSpan(i * 16 + 6)));
        Assert.Equal(v.Positions[..3], v.Positions[(24 * 3)..(24 * 3 + 3)]);
        Assert.Equal([0.125f, 0.125f, 0.125f], m2.Entities[0].BoundsHalf);    // grow-only: unchanged box
    }

    [Fact]
    public void Format8NewVertexGetsZeroExt()
    {
        using var w = MeshWork.Export(Aiden);
        var (c, mdl) = w.LoadCast();
        var mesh = mdl.Meshes()[0];
        int n = mesh.VertexCount()!.Value;
        var vp = Grow(mesh.VertexPositionBuffer()!, 3);
        vp[^3] += 0.001f;                                                     // a genuinely new position
        mesh.SetVertexPositionBuffer(vp);
        mesh.SetVertexNormalBuffer(Grow(mesh.VertexNormalBuffer()!, 3));
        mesh.SetVertexTangentBuffer(Grow(mesh.VertexTangentBuffer()!, 3));
        mesh.SetVertexUVLayerBuffer(0, Grow(mesh.VertexUVLayerBuffer(0)!, 2));
        mesh.SetVertexUVLayerBuffer(1, Grow(mesh.VertexUVLayerBuffer(1)!, 2));
        mesh.SetVertexWeightValueBuffer(Grow(mesh.VertexWeightValueBuffer()!, 4));
        var wb = mesh.VertexWeightBoneBuffer()!;
        mesh.SetVertexWeightBoneBuffer([.. wb, .. wb[..4]]);
        mesh.CreateProperty("bp_tangent_sign", CastPropertyType.Float).SetValues([.. mesh.Property("bp_tangent_sign")!.ToSingleArray(), 1f]);
        mesh.RemoveProperty("bp_vertex_id");
        var f = mesh.FaceBuffer()!;
        mesh.SetFaceBuffer([.. f, (uint)n, f[1], f[2]]);
        var r = w.Build(c);
        Assert.Contains(r.Warnings, x => x.Contains("format 8") && x.Contains("all zero"));
        var e = Decode("x", r.Parts).GeometryEntries[0];
        Assert.Equal(15377, e.VertexCount);
        int k = n;                                                            // submesh 0 comes first in the rebuilt window
        Assert.Equal(vp[^3..], e.Vertices!.Positions[(k * 3)..(k * 3 + 3)]);
        Assert.All(e.Vertices.Raw.AsSpan(k * 80 + 40, 40).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(e.Vertices.Raw.AsSpan(k * 80 + 36)));
        Assert.Equal(255, e.Vertices.WeightSum(k));
        Assert.Equal(1, r.Report["entries"]![0]!["submeshes"]![0]!["new_vertices"]!.GetValue<int>());
    }

    private static void NewMaterialOnMesh(Nightrunner.Core.Cast.Model mdl, int mesh, string name)
    {
        var mat = mdl.CreateMaterial();
        ulong max = 0;
        void Walk(CastNode x) { max = Math.Max(max, x.Hash); foreach (var ch in x.ChildNodes) Walk(ch); }
        Walk(mdl);
        mat.Hash = max + 1;
        mat.SetName(name);
        mat.SetType("pbr");
        mdl.Meshes()[mesh].SetMaterial(mat.Hash);
        mdl.Meshes()[mesh].RemoveProperty("bp_material_slot");
    }

    [Fact]
    public void NewMaterialGrowsTable()
    {
        using var w = MeshWork.Export(Box);                   // count 2, capacity 3: entry 2 is a skin-only material
        var p = w.Parts.ByType;
        var (c, mdl) = w.LoadCast();
        var full = w.Model.FullMaterialTable();
        Assert.Equal((2, 3), (w.Model.Materials.Length, w.Model.MaterialCapacity));
        ((Material)mdl.Meshes()[0].Material()!).SetName("my_box.mat");
        var r = w.Build(c, options: new MeshBuildOptions());
        var added = r.Report["materials_added"]!.AsArray();
        Assert.Equal("my_box.mat", added[0]!["name"]!.GetValue<string>());
        Assert.Equal(2, added[0]!["slot"]!.GetValue<int>());
        var m2 = Decode(w.Name, r.Parts);
        Assert.Equal(["DUMMYBOX.MAT", "auto_shadow_caster.mat", "my_box.mat"], m2.Materials.Select(m => m.NameStr));
        Assert.Equal(MeshImagePatch.MaterialNameTag, m2.Materials[2].NameTag);
        Assert.Equal(2, m2.GeometryEntries[0].Submeshes[0].MaterialSlot);
        Assert.Equal(4, m2.MaterialCapacity);
        Assert.Equal([.. full[..2], "my_box.mat", .. full[2..]], m2.FullMaterialTable());   // the skin-only entry kept, shifted
        Assert.Empty(m2.Warnings);
        Assert.Equal(p[0xF0], r.Parts[0xF0]);
        Assert.Equal(p[0xF1], r.Parts[0xF1]);
        Assert.Equal(0, r.Parts[0x10].Length % 16);
        int t = m2.Image.Target(m2.Materials[2].Offset + 8)!.Value;             // {len, cap} prefix in the shipped shape
        Assert.Equal((10u, 10u), (m2.Image.U32(t - 8), m2.Image.U32(t - 4)));
    }

    [Fact]
    public void NewMaterialOnFullTableGrows()
    {
        using var w = MeshWork.Export(Pants);                 // count 3 == capacity 3: the prototype refuses
        var (c, mdl) = w.LoadCast();
        Assert.Equal((3, 3), (w.Model.Materials.Length, w.Model.MaterialCapacity));
        NewMaterialOnMesh(mdl, 1, "my_new_material.mat");
        var r = w.Build(c);
        var m2 = Decode(w.Name, r.Parts);
        Assert.Equal((4, 4), (m2.Materials.Length, m2.MaterialCapacity));
        Assert.Equal("my_new_material.mat", m2.MaterialName(m2.GeometryEntries[0].Submeshes[1].MaterialSlot));
        Assert.Empty(m2.Warnings);
    }

    /// <summary>
    /// Invariant (prototype bug not ported): adding a material to a mesh with skins keeps every skin resolving to the same
    /// materials — the class-11 table grows instead of overwriting a skin-only entry — and the edited submesh reaches the
    /// new material.
    /// </summary>
    [Fact]
    public void NewMaterialKeepsSkins()
    {
        using var w = MeshWork.Export(Sedan);
        var skins = MeshSkins.Decode(w.Parts.Skin!);
        Assert.Null(skins.Error);
        int count = w.Model.Materials.Length;
        Assert.True(w.Model.MaterialCapacity > count);                      // skin-only materials exist
        var full = w.Model.FullMaterialTable();
        var before = skins.Skins.Select((_, k) => skins.MaterialsFor(k, count).Select(i => full[i]).ToArray()).ToList();

        var (c, mdl) = w.LoadCast();
        NewMaterialOnMesh(mdl, 0, "nr_test_new.mat");
        var r = w.Build(c);
        Assert.False(r.Parts[0x12].AsSpan().SequenceEqual(w.Parts.Skin));   // Replace indices shifted
        var m2 = Decode(w.Name, r.Parts);
        Assert.Empty(m2.Warnings);
        var skins2 = MeshSkins.Decode(r.Parts[0x12]);
        Assert.Null(skins2.Error);
        var full2 = m2.FullMaterialTable();
        Assert.Equal(count + 1, m2.Materials.Length);
        Assert.Equal(w.Model.MaterialCapacity + 1, m2.MaterialCapacity);
        for (int k = 0; k < skins.Skins.Count; k++)
        {
            var after = skins2.MaterialsFor(k, count + 1).Select(i => full2[i]).ToArray();
            Assert.Equal(before[k], after[..count]);
            Assert.Equal("nr_test_new.mat", after[count]);
        }
        int e0 = (int)mdl.Meshes()[0].Property("bp_entry")!.NumberAt(0), s0 = (int)mdl.Meshes()[0].Property("bp_submesh")!.NumberAt(0);
        var sub = m2.GeometryEntries[e0].Submeshes[s0];
        Assert.Equal(count, sub.MaterialSlot);
        Assert.Equal("nr_test_new.mat", m2.MaterialName(sub.MaterialSlot));
        Assert.Single(r.Report["materials_added"]!.AsArray());
    }

    [Fact]
    public void PaletteGrowth()
    {
        using var w = MeshWork.Export(Pants);
        var (c, mdl) = w.LoadCast();
        var mesh = mdl.Meshes()[0];
        var pal = w.Model.GeometryEntries[0].Submeshes[0].Palette;
        int newBone = Enumerable.Range(0, w.Model.Entities.Length).First(i => !pal.Contains((ushort)i));
        var wb = mesh.VertexWeightBoneBuffer()!;
        var wv = mesh.VertexWeightValueBuffer()!;
        wb[0] = (uint)newBone;
        wv[0] = 1; wv[1] = wv[2] = wv[3] = 0;
        mesh.SetVertexWeightBoneBuffer(wb);
        mesh.SetVertexWeightValueBuffer(wv);
        int vid = (int)mesh.Property("bp_vertex_id")!.NumberAt(0);
        var r = w.Build(c);
        Assert.Equal(1, r.Report["arrays_appended"]!.GetValue<int>());
        var m2 = Decode("x", r.Parts);
        var s0 = m2.GeometryEntries[0].Submeshes[0];
        Assert.Equal([.. pal, (ushort)newBone], s0.Palette);
        var v = m2.GeometryEntries[0].Vertices!;
        Assert.Equal(pal.Length, v.Joints![vid * 4]);
        Assert.Equal([255, 0, 0, 0], v.Raw.AsSpan(vid * 40 + 12, 4).ToArray());
        Assert.Empty(m2.Warnings);
        Assert.Equal(Fixups.Parse(r.Parts[0x11]).Slots.Count, m2.Fixups.Slots.Count);
    }

    [Fact]
    public void PaletteOverflowRefused()
    {
        using var w = MeshWork.Export(Crane);                 // 308 entities
        var (c, mdl) = w.LoadCast();
        var mesh = mdl.Meshes().MaxBy(m => m.VertexCount())!;
        int n = mesh.VertexCount()!.Value;
        mesh.SetVertexWeightBoneBuffer(Enumerable.Range(0, n * 4).Select(i => i % 4 == 0 ? (uint)(i / 4 % 300) : 0u).ToArray());
        mesh.SetVertexWeightValueBuffer(Enumerable.Range(0, n * 4).Select(i => i % 4 == 0 ? 1f : 0f).ToArray());
        var e = Assert.Throws<MeshBuildException>(() => w.Build(c));
        Assert.Contains("bones in one submesh", e.Message);
        Assert.Contains("256", e.Message);
    }

    [Fact]
    public void BoneChangeRefusedUnlessIgnored()
    {
        using var w = MeshWork.Export(Pants);
        var (c, mdl) = w.LoadCast();
        var b = mdl.Skeleton()!.Bones()[5];
        var lp = b.LocalPosition()!;
        lp[0] += 0.05f;
        b.SetLocalPosition(lp);
        var e = Assert.Throws<MeshBuildException>(() => w.Build(c));
        Assert.Contains("bone transforms differ", e.Message);
        Assert.Contains("'l_calf'", e.Message);
        var r = w.Build(c, options: new MeshBuildOptions { IgnoreBoneChanges = true });
        Assert.Equal(w.Parts.Image, r.Parts[0x10]);
        Assert.Contains(r.Warnings, x => x.Contains("native skeleton kept"));
    }

    [Fact]
    public void LodEntryRemovalRefused()
    {
        using var w = MeshWork.Export(Hair);
        var (c, mdl) = w.LoadCast();
        mdl.RemoveChild(mdl.Meshes()[1]);
        var e = Assert.Throws<MeshBuildException>(() => w.Build(c));
        Assert.Contains("entry 1", e.Message);
        Assert.Contains("removing a geometry entry is not supported", e.Message);
    }

    [Fact]
    public void SubmeshGapRefused()
    {
        using var w = MeshWork.Export(Pants);
        var (c, mdl) = w.LoadCast();
        mdl.Meshes()[1].CreateProperty("bp_submesh", CastPropertyType.Integer).SetValues([5u]);
        var e = Assert.Throws<MeshBuildException>(() => w.Build(c));
        Assert.Contains("[0, 5]", e.Message);
    }

    [Fact]
    public void AddedSubmesh()
    {
        using var w = MeshWork.Export(Box);
        var (c, mdl) = w.LoadCast();
        var m0 = mdl.Meshes()[0];
        var m1 = mdl.CreateMesh();
        foreach (var (k, prop) in m0.Properties)
        {
            var np = m1.CreateProperty(k, prop.Type);
            switch (prop.Values)
            {
                case byte[] a: np.SetValues((byte[])a.Clone()); break;
                case ushort[] a: np.SetValues((ushort[])a.Clone()); break;
                case uint[] a: np.SetValues((uint[])a.Clone()); break;
                case ulong[] a: np.SetValues((ulong[])a.Clone()); break;
                case float[] a: np.SetValues((float[])a.Clone()); break;
                case double[] a: np.SetValues((double[])a.Clone()); break;
                case string[] a: np.SetString(a[0]); break;
            }
        }
        m1.SetName("dummybox_025m.e0.s1");
        m1.CreateProperty("bp_submesh", CastPropertyType.Integer).SetValues([1u]);
        m1.SetMaterial(mdl.Materials()[0].Hash);
        var r = w.Build(c);
        Assert.Equal(1, r.Report["records_relocated"]!.GetValue<int>());
        var m2 = Decode(w.Name, r.Parts);
        Assert.Empty(m2.Warnings);
        var e = m2.GeometryEntries[0];
        Assert.Equal(2, e.Submeshes.Length);
        Assert.Equal([(36, 0), (36, 0)], e.Submeshes.Select(s => (s.IndexCount, s.MaterialSlot)));
        Assert.Equal(24, e.VertexCount);                                     // ids present ⇒ in-place window, shared
        Assert.Equal(Vertex.AlignTo(72 * 2, 16), r.Parts[0xF1].Length);
        var fx = Fixups.Parse(r.Parts[0x11]);
        Assert.Equal(fx.Records.Select(x => x.Offset).Order(), fx.Records.Select(x => x.Offset));
        Assert.Equal(Fixups.Parse(w.Parts.Fixups!).Records.Count, fx.Records.Count);
        var rec7 = fx.Records.Where(x => x.ClassId == 7).ToList();
        Assert.Single(rec7);
        Assert.Equal(2, rec7[0].Count);
        Assert.Equal((uint)e.Submeshes[0].PaletteDescOffset, rec7[0].Offset);
    }

    /// <summary>Invariant: the embedded <c>.msh</c> identity follows the output name; every other byte is kept.</summary>
    [Fact]
    public void IdentityRenameInBuild()
    {
        using var w = MeshWork.Export(Box);
        var p = w.Parts.ByType;
        var (c, _) = w.LoadCast();
        var r = w.Build(c, name: "my_box");
        Assert.True(r.Report["identity_renamed"]!.GetValue<bool>());
        Assert.Equal("my_box.msh"u8.ToArray(), Image.EmbeddedName(r.Parts[0x10], r.Parts[0x11]));
        Assert.Equal(p[0xF0], r.Parts[0xF0]);
        Assert.Equal(p[0xF1], r.Parts[0xF1]);
        int len = p[0x10].Length;
        Assert.Equal(p[0x10][8..(len - 14)], r.Parts[0x10][8..(len - 14)]);
        // the source name is kept for an output under the source's own name
        var same = w.Build(c);
        Assert.Equal(p[0x10], same.Parts[0x10]);
        Assert.Equal((false, true), (MeshIdentity.Verify(p[0x10], p[0x11], "my_box").Ok, MeshIdentity.Verify(r.Parts[0x10], r.Parts[0x11], "my_box").Ok));
        var (ni, nf, changed) = MeshIdentity.Rename(p[0x10], p[0x11], "my_box");
        Assert.True(changed);
        Assert.Equal(r.Parts[0x10], ni);
        Assert.Equal(r.Parts[0x11], nf);
    }

    [Fact]
    public void EditedNormalsAndWeightsRequantise()
    {
        using var w = MeshWork.Export(Crane);
        var (c, mdl) = w.LoadCast();
        var mesh = mdl.Meshes().MaxBy(m => m.VertexCount())!;
        var vn = mesh.VertexNormalBuffer()!;
        vn[0] = 0; vn[1] = 1; vn[2] = 0;
        mesh.SetVertexNormalBuffer(vn);
        var wv = mesh.VertexWeightValueBuffer()!;
        int row = Enumerable.Range(0, wv.Length / 4).First(i => wv[i * 4] > 0 && wv[i * 4 + 1] > 0);
        wv[row * 4] = 0.7f; wv[row * 4 + 1] = 0.3f; wv[row * 4 + 2] = 0; wv[row * 4 + 3] = 0;
        mesh.SetVertexWeightValueBuffer(wv);
        var ids = mesh.Property("bp_vertex_id")!.ToUInt32Array();
        int e = (int)mesh.Property("bp_entry")!.NumberAt(0);
        var r = w.Build(c);
        var v = Decode("x", r.Parts).GeometryEntries[e].Vertices!;
        int v0 = (int)ids[0];
        Assert.True(v.Normals[v0 * 3 + 1] > 0.99);
        int vr = (int)ids[row];
        Assert.Equal(255, v.WeightSum(vr));                                  // floor(0.7·255) + floor(0.3·255) = 254, +1 to the larger fraction
        int stride = Vertex.Stride(v.Format);
        Assert.InRange(v.Raw[vr * stride + 12], 178, 179);
        Assert.Equal(0, v.Raw[vr * stride + 14] + v.Raw[vr * stride + 15]);
    }

    // ---- frozen rebuilds of edited scenes ---------------------------------------------------------------------------

    /// <summary>
    /// Digest of the port's rebuilt parts (<see cref="Digest"/>) for every edited scene tools/BuildCheck derives from five
    /// DLTB meshes (MeshBuildVariants), or the refusal. Taken 2026-09-27 and checked against the prototype's rebuild of
    /// the same scenes (tools/BuildCheck with tools/BuildCheck/pybuild.py, 55 jobs, 0 mismatches): each digest is the
    /// prototype's parts, except "newmat", where by design (docs/porting.md, Divergences) the port grows the material table
    /// and the prototype overwrites a skin-only entry or refuses a full table: there 0xF0 / 0xF1 are the prototype's and
    /// 0x10 / 0x11 the port's. A mismatch message lists the current values in this table's form.
    /// </summary>
    private static readonly Dictionary<string, string> FrozenBuilds = new()
    {
        ["dummybox_025m/unedited"] = "997ed98e13b106642037e403d764c86ebdbaf6073567ccc697dd1989a0a46876",
        ["dummybox_025m/rename"] = "20d2070f3363b573c7781333d66cc9251f33dd39b1c3f60f32f95f6426ff89cf",
        ["dummybox_025m/gltf"] = "997ed98e13b106642037e403d764c86ebdbaf6073567ccc697dd1989a0a46876",
        ["dummybox_025m/gltfmoved"] = "f193189daa9d2e978ed2f5987436dc205533abb48917e98dafb6c3c914fdbad6",
        ["dummybox_025m/moved"] = "79e24a4ff1352b419fd096fa1f85f9efc0a53c4ceec1d6acdeab1661553c24ae",
        ["dummybox_025m/deltri"] = "02d83c4928f18ebdbacba53634437e24a7fb0635941f889c5e61cd535f23409f",
        ["dummybox_025m/blender"] = "dd71ad5c1ea2ec35211d46aad0f6af511acd5ae72926b97819e9e9f171e61ea9",
        ["dummybox_025m/newmat"] = "08e87e488807cac5a43c35e2f974670ef0e256ec98323faf14593a8d4f7bc918",
        ["dummybox_025m/normals"] = "00dab12b84d40757a9c25c53e13361c3c0e8f1eb73096e2c86227e200b6facf4",
        ["dummybox_025m/addvert"] = "078acc181f37f1d222bbb03b45ec97eab6eb04df84ede81587e55a7e32e36179",
        ["sh2_npc_crane/unedited"] = "62cf001bdff032442c89668acc9f066e779672b956824f56f901637ce7664d31",
        ["sh2_npc_crane/rename"] = "6d9b341a9070b57b7a9f0ac52b63be34535cb50b82a9fa186039948c20ed042d",
        ["sh2_npc_crane/gltf"] = "62cf001bdff032442c89668acc9f066e779672b956824f56f901637ce7664d31",
        ["sh2_npc_crane/gltfmoved"] = "46d590d8fe1e72fd76868d8c11adefbb1356e40ae7a867bd9089f24e9efe5321",
        ["sh2_npc_crane/moved"] = "abfb166b2e31e5e240594550087ee07b8a0113114a76006a013fb69e3fcfcbee",
        ["sh2_npc_crane/deltri"] = "11381269c017e27c6d6407d212f53add0909633b34a5b6a3fcceaa2d215747f7",
        ["sh2_npc_crane/blender"] = "4601a73b45b50d38a9c3836c3cc9103b2d447d58f6fe903671e7b9c00a610cb2",
        ["sh2_npc_crane/newmat"] = "ff5470ee8cae70ad88af478c4a254798f56b25e3db939c8c3ba9b2ea5622e8f7",
        ["sh2_npc_crane/normals"] = "1156d6b4535572965e7378e4719b89e5f6878948d05c02727d6edbd5bce68296",
        ["sh2_npc_crane/weights"] = "0aa6073e3ad90098642dd35d7c13866be84ab0c456b3babb74ff9c923bf15912",
        ["sh2_npc_crane/palette"] = "cb001a5d0c206cc7bda035e1d5cdfa104f33a7b3cbc65d038beeba815fce031a",
        ["sh2_npc_crane/addvert"] = "60748b12e3df4059373de537d8f871ac674ce5fd1f84d1bcd3809655da03e4b8",
        ["player_kc_basic_torso_a_tpp/unedited"] = "55d7bbdaa80f9ccd6bdfc7db28ef22d536a4cf91d359a4fe535877e4b8d5b676",
        ["player_kc_basic_torso_a_tpp/rename"] = "fd3c233666dc010ccc35805d2a3c86b3849633414ba4f3685be8ebd52985759b",
        ["player_kc_basic_torso_a_tpp/gltf"] = "55d7bbdaa80f9ccd6bdfc7db28ef22d536a4cf91d359a4fe535877e4b8d5b676",
        ["player_kc_basic_torso_a_tpp/gltfmoved"] = "ace05bd1f0092ffa4426ab0e8fc4e76be58b525536a57d2583ab86d6d73ee619",
        ["player_kc_basic_torso_a_tpp/moved"] = "aedaeaf2624312084e78bdd5b0a196c09ff6cb8a867d8f7b178c3682ef334171",
        ["player_kc_basic_torso_a_tpp/deltri"] = "473ba79a1dc20b343a3405df5ed46003ed5370f413fcc298be607ab217434b5b",
        ["player_kc_basic_torso_a_tpp/blender"] = "dce796acfc440e0f97f648983e2ba4354fcff62cbd6f30dc5caaef1b156ac43b",
        ["player_kc_basic_torso_a_tpp/newmat"] = "6b6f6fcdefd9926f45dcdddfe7da2942b6bbec847fb9801becd8035548ac3048",
        ["player_kc_basic_torso_a_tpp/normals"] = "acf32e36ccf073ac9845afa4bf649790cd5c592afadbb2982585887b79378a0f",
        ["player_kc_basic_torso_a_tpp/weights"] = "7a1e560bd17acf74d5e90580ecc0e97ca04910c5852ed6250efb17fc717ab450",
        ["player_kc_basic_torso_a_tpp/palette"] = "acb00da6be55a96cdb385a3f8dc7d7c0bdfeb9859f0e596db4ed6de906bcb1b3",
        ["player_kc_basic_torso_a_tpp/addvert"] = "dae1cc3630fe47ff885049bbb051c82566dacd2f8386460ed15ba19ee16ddc76",
        ["veh_sedan_a/unedited"] = "b1a1cfd3be17ae9c7e233d75781aa42d01e86f03d8d8f04be39308e35e962461",
        ["veh_sedan_a/rename"] = "4eb6fc44c7c1063ff615bfe20bd24cf466be485ed262257a35061e23b6cb90ed",
        ["veh_sedan_a/gltf"] = "b1a1cfd3be17ae9c7e233d75781aa42d01e86f03d8d8f04be39308e35e962461",
        ["veh_sedan_a/gltfmoved"] = "17c08b4a0228e6f85ae549aa616a17b3c029d61b87ebdda162da7c76af432aab",
        ["veh_sedan_a/moved"] = "bf21a2e35624627db3b93757a61ffd7ff7da09a0f0c5a4eac6f5d69576ce2ec6",
        ["veh_sedan_a/deltri"] = "8f8e7ba628e08fdab6fabd8d91aec84075306c28609f23a19b58c137750553f8",
        ["veh_sedan_a/blender"] = "40d6c7bb15e59ea8eedf4cf9363e8faa23b44d90050ae0492a25fce97e4b9d88",
        ["veh_sedan_a/newmat"] = "6000fc45e7db54f8b21b47a1d28519a163d1f530fa0a34e986bfe485f030c61f",
        ["veh_sedan_a/normals"] = "88459124bdc81d7f0c0b0dd2280c255b42c4c738b3cfd61a6eeece66ec9c4de5",
        ["veh_sedan_a/addvert"] = "6c40c0fd8b775f4c612a9faec64209239faf2acf7516c3d8071b678035de078c",
        ["wn_pistol_b_b/unedited"] = "3b4dc6d35bad001ab2e5b0ea7d1a98b86a3a753082e0d7ceaad5c0c776b5c0dd",
        ["wn_pistol_b_b/rename"] = "bb034ee4f82405a2d73e41af8cc21bf9bfbcfb87c9707fcb2cbf051068f63af7",
        ["wn_pistol_b_b/gltf"] = "3b4dc6d35bad001ab2e5b0ea7d1a98b86a3a753082e0d7ceaad5c0c776b5c0dd",
        ["wn_pistol_b_b/gltfmoved"] = "a65677e78b118d68e7661a4a7e217623a0604bf712c61949a7ac8627fc76bb64",
        ["wn_pistol_b_b/moved"] = "b4cd5c75cc99a16a5953d32643724f3304836320ccdc5dcb2f9f93a5dfcbaaea",
        ["wn_pistol_b_b/deltri"] = "cd00b8e06995902abad170a22d35c8f6174deab2979b9ea2e87897b047975da1",
        ["wn_pistol_b_b/blender"] = "ca85af31c0218aeffa628011e836d8fa217967eef049e8ba72d1a16e509fb270",
        ["wn_pistol_b_b/newmat"] = "aebc97166b5f81c4e0e965613bdd271eca4629d9fa7b7f9ebcf33a7ec8427a36",
        ["wn_pistol_b_b/normals"] = "3f962ae5253af4fbc3266712e51108d1a72e75838f5b7e5b6042c1ba925f9d93",
        ["wn_pistol_b_b/palette"] = "0def7196ab01a6e2128f087fc9c61fa446e254a3342015d0776356d1430dcc6e",
        ["wn_pistol_b_b/addvert"] = "d42466cb12bf521eeabe500ef66fbab60a5d6d6cbbedbb95795edd65ec6b8ae0",
    };

    public static TheoryData<string> FrozenBuildMeshes => new() { Crane, "player_kc_basic_torso_a_tpp", Sedan, "wn_pistol_b_b", Box };

    [Theory]
    [MemberData(nameof(FrozenBuildMeshes))]
    public void EditedBuildsAreFrozen(string mesh)
    {
        var p = MeshFixtures.Parts("dltb", mesh);
        var model = MeshDecoder.Decode(p);
        string dir = Path.Combine(Path.GetTempPath(), "nightrunner-tests", Guid.NewGuid().ToString("N"));
        var actual = new List<(string Key, string Value)>();
        try
        {
            string src = Path.Combine(dir, "src", "model.cast");
            var (_, side) = CastExport.WriteFiles(model, p.Name, src);
            foreach (var (variant, edit) in MeshBuildVariants.All())
            {
                string scene = Path.Combine(dir, variant, variant.StartsWith("gltf", StringComparison.Ordinal) ? "model.glb" : "model.cast");
                var cast = CastFile.Load(src);
                if (!edit(cast.Roots()[0].ChildOfType<Nightrunner.Core.Cast.Model>()!, model)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(scene)!);
                if (scene.EndsWith(".glb", StringComparison.Ordinal)) Gltf.Save(cast, scene);
                else cast.Save(scene);
                string value;
                try
                {
                    var r = MeshBuild.Build(p.ByType, variant == "rename" ? p.Name.Trim() + "_nr" : p.Name, scene,
                                            JsonNode.Parse(File.ReadAllText(side!))!.AsObject(), new MeshBuildOptions { IgnoreBoneChanges = true });
                    value = Digest(r.Parts);
                }
                catch (MeshBuildException e) { value = "refused: " + e.Message; }
                actual.Add(($"{mesh}/{variant}", value));
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        bool same = actual.Count == FrozenBuilds.Keys.Count(k => k.StartsWith(mesh + "/", StringComparison.Ordinal))
                    && actual.All(a => FrozenBuilds.TryGetValue(a.Key, out var w) && w == a.Value);
        Assert.True(same, $"{mesh}: the rebuild changed, or the install's copy of the mesh did (a game update). Now:\n" +
                          string.Join("\n", actual.Select(a => $"        [\"{a.Key}\"] = \"{a.Value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\",")));
    }

    /// <summary>SHA-256 over parts 0x10, 0x11, 0xF0, 0xF1 in that order, each as its type byte, its length (int64 LE) and its
    /// bytes: the parts both implementations regenerate.</summary>
    internal static string Digest(IReadOnlyDictionary<byte, byte[]> parts)
    {
        using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        Span<byte> len = stackalloc byte[8];
        foreach (byte t in new byte[] { 0x10, 0x11, 0xF0, 0xF1 })
        {
            var b = parts[t];
            h.AppendData([t]);
            BinaryPrimitives.WriteInt64LittleEndian(len, b.Length);
            h.AppendData(len);
            h.AppendData(b);
        }
        return Convert.ToHexStringLower(h.GetHashAndReset());
    }
}
