using System.Buffers.Binary;
using System.Text;
using Nightrunner.Core.Backends;
using Nightrunner.Core.Games;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Mesh.ClassReader;
using Record = Nightrunner.Core.Mesh.ClassReader.Record;

namespace Nightrunner.Tests;

// Port of nightrunner-main/tests/test_mesh_dl2.py (decode side). The prototype's three DL2 fixtures are the shipped
// meshes of the same names; they are read from the DL2 install here.
//
// not ported: PackPathTests (GUI data path, Cast export), test_cast_has_bones_and_weights (no Cast in C# yet),
// test_import_refuses_before_reading_cast / test_imagepatch_refuses (no importer or ImagePatch in C#: the DL2 guard
// is the backend's CanEncode = false, checked below).
public class MeshDl2Tests
{
    private static readonly string[] SampleNames = ["prp_hotel_number_10_a", "alarm_lamp_a", "air_conditioner_wall_dest_a"];

    [Fact]
    public void SamplesAreDl2()
    {
        foreach (var n in SampleNames)
            Assert.Same(MeshLayout.Dl2, MeshLayout.Detect(Fixups.Parse(MeshFixtures.Parts("dl2", n).Fixups)));
    }

    [Fact]
    public void DltbPackIsDltb()
    {
        foreach (var p in MeshFixtures.Pack("dltb", "engine_pc.rpack"))
        {
            Assert.Same(MeshLayout.Dltb, MeshLayout.Detect(Fixups.Parse(p.Fixups)));
            Assert.Equal("dltb", MeshDecoder.Decode(p).Layout);
        }
    }

    [Fact]
    public void Hint()
    {
        var fx = Fixups.Parse(MeshFixtures.Parts("dl2", "alarm_lamp_a").Fixups);
        Assert.Same(MeshLayout.Dl2, MeshLayout.Detect(fx, "dl2"));
        Assert.Throws<MeshFormatException>(() => MeshLayout.Detect(fx, "dltb"));
        Assert.Throws<MeshFormatException>(() => MeshLayout.Detect(fx, "dl3"));
    }

    [Fact]
    public void Unrecognised()
    {
        var fx = new Fixups(0x60, 1, [new Record(0, (0xB0u << 24) | 3, 1)], []);
        Assert.Throws<MeshFormatException>(() => MeshLayout.Detect(fx));
        Assert.Same(MeshLayout.Dl2, MeshLayout.Detect(fx, "dl2"));   // the hint decides only when the data does not
    }

    [Fact]
    public void HotelNumber()
    {
        var (m, p) = MeshFixtures.Load("dl2", "prp_hotel_number_10_a");
        Assert.Equal("dl2", m.Layout);
        Assert.True(m.IsDl2);
        Assert.Equal(p.Name + ".msh", MeshModel.Text(m.EmbeddedName));
        Assert.Equal(p.Name + ".scr", MeshModel.Text(m.ScrName!));
        Assert.Equal(0x68, m.RootRaw.Length);
        var en = Assert.Single(m.Entities);
        Assert.Equal((-1, 1, 1, 0x24704u), (en.Parent, en.Type, en.GeometryCount, en.Flags));
        Assert.Equal(6, en.RawCa.Length);
        Assert.Equal(["prp_hotel_numbers.mat"], m.Materials.Select(x => x.NameStr));
        var e = Assert.Single(m.GeometryEntries);
        Assert.Equal((3, 0L, 28, 0L), (e.Format, e.VertexBase, e.VertexCount, e.IndexBase));
        Assert.Equal((0x198, 0x1C8, 0x1EC, 0x1E8), (e.Offset, e.StreamOffset!.Value, e.MaterialSlotsOffset!.Value, e.IndexCountsOffset!.Value));
        Assert.Equal(0x20, e.RawStream.Length);
        Assert.Equal(Convert.FromHexString("32465a6e"), e.Raw34[..4]);
        Assert.Equal([(0, 42, 0)], e.Submeshes.Select(s => (s.MaterialSlot, s.IndexCount, s.Palette.Length)));
        Assert.Equal(0, e.OwnerEntity);
        Assert.Empty(m.Warnings);
        var v = e.Vertices!;
        Assert.Equal(28, v.Count);
        for (int i = 0; i < v.Count; i++)
        {
            float len = MathF.Sqrt(v.Normals[i * 3] * v.Normals[i * 3] + v.Normals[i * 3 + 1] * v.Normals[i * 3 + 1] + v.Normals[i * 3 + 2] * v.Normals[i * 3 + 2]);
            Assert.InRange(len, 1 - 1e-4f, 1 + 1e-4f);
            for (int k = 0; k < 3; k++)
                Assert.InRange(v.Positions[i * 3 + k], en.BoundsCenter[k] - en.BoundsHalf[k] - 1e-3f, en.BoundsCenter[k] + en.BoundsHalf[k] + 1e-3f);
        }
    }

    [Fact]
    public void AlarmLampLodsAndSkinOnlyMaterials()
    {
        var (m, _) = MeshFixtures.Load("dl2", "alarm_lamp_a");
        Assert.Equal([(0, 0L, 74, 0L), (1, 2400L, 61, 540L)], m.GeometryEntries.Select(e => (e.Element, e.VertexBase, e.VertexCount, e.IndexBase)));
        Assert.Equal((1, 6), (m.Materials.Length, m.MaterialCapacity));
        Assert.Equal("alarm_lamp_b.mat", m.Materials[0].NameStr);
        Assert.Equal(0, m.Materials[0].NameTag);   // DL2 material names are plain (untagged) pointers
        Assert.Equal(["alarm_lamp_a", "sound_pos"], m.Entities.Select(x => x.NameStr));
        var s = MeshSkins.Decode(m.SkinRaw!);
        Assert.Null(s.Error);
        Assert.Equal(6, s.Skins.Count);
        Assert.Equal(6, m.FullMaterialTable().Length);
        Assert.Equal(0x308, m.GeometryEntries[1].StreamOffset);   // test_sidecar_records_layout
        Assert.Equal(0x20, m.GeometryEntries[1].RawStream.Length);
    }

    [Fact]
    public void ReencodeByteIdentical()
    {
        foreach (var n in SampleNames)
        {
            var (m, p) = MeshFixtures.Load("dl2", n);
            Assert.Equal(p.Vertex, MeshEncoder.EncodeVertexBuffer(m));
            Assert.Equal(p.Index, MeshEncoder.EncodeIndexBuffer(m));
            Assert.Equal(p.Fixups, m.Fixups.ToBytes());
            Assert.Equal(p.Skin, MeshSkins.Decode(p.Skin!).Encode());
        }
    }

    [Fact]
    public void SyntheticSkinnedDecode()
    {
        var (img, fx, vb, ib) = MeshSynth.SkinnedDl2();
        var m = MeshDecoder.Decode("synth_dl2", img, fx, vb, ib);
        Assert.Equal("dl2", m.Layout);
        Assert.Empty(m.Warnings);
        Assert.Equal("synth_dl2.msh", MeshModel.Text(m.EmbeddedName));
        Assert.Equal([-1, 0, -1], m.Entities.Select(x => x.Parent));
        Assert.True(m.Skinned);
        var e = m.GeometryEntries[0];
        Assert.Equal((6, 3, (int?)2), (e.Format, e.VertexCount, e.OwnerEntity));
        Assert.Equal([0, 1], e.Submeshes[0].Palette.Select(x => (int)x));
        Assert.Equal([0, 1, 2], e.Submeshes[0].Indices.Select(x => (int)x));
        Assert.Equal([0, 1, 0, 0], e.Vertices!.Joints![4..8]);
        for (int i = 0; i < 3; i++) Assert.Equal(1f, e.Vertices.Weights![(i * 4)..(i * 4 + 4)].Sum(), 5);
        var g = m.EntityGlobals()[1];
        Assert.Equal((0.0, 1.0, 0.0), (g[3], g[7], g[11]));
        Assert.Equal(vb, MeshEncoder.EncodeVertexBuffer(m));
        Assert.Equal(ib, MeshEncoder.EncodeIndexBuffer(m));
        Assert.Equal(fx, m.Fixups.ToBytes());
    }

    [Fact]
    public void BadStreamPointer()
    {
        var (img, fx, vb, ib) = MeshSynth.SkinnedDl2();
        var geo = (int)Fixups.Parse(fx).Records.First(r => r.ClassId == 6).Offset;
        BinaryPrimitives.WriteUInt64LittleEndian(img.AsSpan(geo + 0x08), 0);
        Assert.Throws<MeshFormatException>(() => MeshDecoder.Decode("x", img, fx, vb, ib));
    }

    [Fact]
    public void GraphViews()
    {
        var (img, fx, _, _) = MeshSynth.SkinnedDl2();
        var g = new MeshGraph(new Image(img, Fixups.Parse(fx)));
        Assert.Same(MeshLayout.Dl2, g.Layout);
        Assert.Equal(1, g.Img.U8(g.EntityAt(2) + 0xC9));
        var off = g.GeometryArrays[0].Offset;
        int? stream = g.StreamOffset(off);
        Assert.Equal(6ul, g.Field(off, stream, g.Layout.Format));
        Assert.Equal(1ul, g.Field(off, stream, g.Layout.SubmeshCount));
        Assert.Equal(1ul, g.Field(off, stream, g.Layout.StreamSubmeshCount!.Value));
        Assert.DoesNotContain(g.OpaqueRecords(), r => r.ClassId == 8);   // class 8 is decoded, not opaque
    }

    [Fact]
    public void Dl2IsReadOnly()
    {
        var meshes = GameBackends.For(GameProfile.Dl2).Meshes!;
        Assert.True(meshes.CanDecode);
        Assert.False(meshes.CanEncode);
        Assert.NotNull(meshes.EncodeRefusal);
    }

    [Fact]
    public void SkinSurfaceWordHighNibbleRoundTrips()
    {
        // DL2 record: +0x14 = 0x10002C00 (remap at +0x2C, bit 28 set)
        var d = Convert.FromHexString(
            "01000000080000003000000000000010380000000000001020000000002c0010" +
            "00000000030001000000000001000100020002000000000044656661756c7400" +
            "000017ff000000ff0000000000000000");
        var s = MeshSkins.Decode(d);
        Assert.Null(s.Error);
        Assert.Equal(0x2Cu, s.Skins[0].SurfacesRel);
        Assert.Equal(1u, s.Skins[0].EHi);
        Assert.Equal(d, s.Encode());
    }

    [Fact]
    public void SkinNameWordNibbleRoundTrips()
    {
        var d = MeshFixtures.Parts("dl2", "alarm_lamp_a").Skin!.ToArray();
        uint w = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(8), w | 0x00E00000);
        var s = MeshSkins.Decode(d);
        Assert.Null(s.Error);
        Assert.Equal(0xEu, s.Skins[0].NNib);
        Assert.Equal("Anthena", s.Skins[0].NameStr);
        Assert.Equal(d, s.Encode());
    }
}
