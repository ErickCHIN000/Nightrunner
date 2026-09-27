using Nightrunner.Core.Mesh;

namespace Nightrunner.Tests;

// Port of nightrunner-main/tests/test_mesh_decode.py (decode side). The prototype's sample meshes are looked up by
// name in the DLTB install.
//
// not ported:
//   test_qtangent_roundtrip_identity (encode half), test_quantize_weights — requantising belongs to mesh writing;
//     the C# encoder refuses an edited frame or weight row instead (EditedNormalIsRefused below).
//   test_multi_entry_lod plan_new_layout part — new layouts are mesh writing; the 160-byte rule is checked by hand.
//   ExtractedTreeTests.test_sidecar_matches_decode — no mesh.json/Cast extraction in C# yet.
//   CorpusTests — tools/MeshCheck re-encodes every mesh of both installs; a 300-mesh slice and engine_pc stay here.
public class MeshDecodeTests
{
    private static readonly string[] Samples =
    [
        "sh_npc_ft_crane_hair_a", "sh2_player_tpp_phx_skeleton", "dlc_ft_freak_banshee_clothes_matriarch",
        "gas_tank_pistol_anm", "wn_pistol_b_b", "dlc_ft_safe_zone_cable_e", "sh2_npc_aiden_beast", "sh2_npc_crane",
        "dummybox_025m", "npc_b_man_pants_b_holster_bag_c", "sh2_npc_ft_crane_beard_a", "anim_hammer_a",
        "bdp_ce_a_ornament_str_d", "alarm_siren_anm", "barrier", "ui_bg_ph_ft", "ui_bg_the_beast",
    ];

    [Fact]
    public void Strides()
    {
        Assert.Equal([16, 32, 40, 80], new[] { 0, 3, 6, 8 }.Select(Vertex.Stride));
        Assert.Equal(0x14, Vertex.OffsetsOf(6).Qtan);
        Assert.Equal(0x1C, Vertex.OffsetsOf(6).Uv0);
        Assert.Equal(0x08, Vertex.OffsetsOf(0).Qtan);
        Assert.Equal(0x18, Vertex.OffsetsOf(3).Uv1);
        Assert.True(Vertex.IsSkinned(6) && Vertex.IsSkinned(8) && !Vertex.IsSkinned(0) && !Vertex.IsSkinned(3));
    }

    [Fact]
    public void QtangentSignRule()
    {
        // largest |component| picks the sign; ties resolve z > y > x > w
        int[][] q = [[0, 0, 0, 32767], [0, 0, 0, -32767], [-100, 5, 5, 5], [7, 7, -7, 7], [7, -7, 7, 7], [-7, 7, 7, 7], [7, 7, 7, -7]];
        Assert.Equal([1, -1, -1, -1, 1, 1, 1], q.Select(x => (int)Vertex.QtangentSign(x[0], x[1], x[2], x[3])));
    }

    [Fact]
    public void QtangentDecodeIdentity()
    {
        Span<float> t = stackalloc float[3], n = stackalloc float[3];
        Vertex.DecodeQtangent(0, 0, 0, 32767, 32767f, t, n);
        Assert.Equal([1f, 0f, 0f], t.ToArray());
        Assert.Equal([0f, 0f, 1f], n.ToArray());
    }

    [Fact]
    public void AlignTo()
    {
        Assert.Equal(39840, Vertex.AlignTo(39720, 160));
        Assert.Equal(960, Vertex.AlignTo(960, 160));
        Assert.Equal(0, Vertex.AlignTo(0, 160));
    }

    [Fact]
    public void HalfConversionKeepsNanPayload()
    {
        foreach (ushort h in new ushort[] { 0x7C01, 0x7D55, 0xFE00, 0x3C00, 0x0001, 0x7C00, 0xFBFF })
            Assert.Equal(h, Vertex.FloatToHalf(Vertex.HalfToFloat(h)));
    }

    [Fact]
    public void SamplesDecodeAndReencode()
    {
        var formats = new HashSet<int>();
        foreach (var name in Samples)
        {
            var (m, p) = MeshFixtures.Load("dltb", name);
            Assert.Equal(p.Name + ".msh", MeshModel.Text(m.EmbeddedName));
            Assert.Empty(m.Warnings);
            foreach (var e in m.GeometryEntries)
            {
                formats.Add(e.Format);
                Assert.NotNull(e.OwnerEntity);
                Assert.Contains(e.Index, m.Entities[e.OwnerEntity!.Value].GeometryEntries);
                foreach (var s in e.Submeshes)
                {
                    Assert.Equal(0, s.IndexCount % 3);
                    Assert.Equal(s.IndexCount, s.Indices.Length);
                    if (e.Vertices is { Skinned: true })
                    {
                        Assert.NotEmpty(s.Palette);
                        Assert.All(s.Palette, x => Assert.True(x < m.Entities.Length));
                    }
                }
            }
            foreach (var (t, bytes) in MeshEncoder.Reencode(m)) Assert.Equal(p.ByType[t], bytes);
        }
        Assert.Equal([0, 3, 6, 8], formats.Order());
    }

    [Fact]
    public void MultiEntryLod()
    {
        var (m, _) = MeshFixtures.Load("dltb", "sh_npc_ft_crane_hair_a");
        Assert.Equal(2, m.GeometryEntries.Length);
        var (e0, e1) = (m.GeometryEntries[0], m.GeometryEntries[1]);
        Assert.Equal(e0.OwnerEntity, e1.OwnerEntity);
        Assert.Equal(2, m.Entities[e0.OwnerEntity!.Value].GeometryCount);
        Assert.Equal((23291, 0L, 0L), (e0.VertexCount, e0.VertexBase, e0.IndexBase));
        Assert.Equal((9821, 931680L, 91764L), (e1.VertexCount, e1.VertexBase, e1.IndexBase));
        Assert.Equal(e1.VertexBase, Vertex.AlignTo(e0.VertexCount * Vertex.Stride(e0.Format), Vertex.BlockAlign));
    }

    [Fact]
    public void SkeletonOnly()
    {
        var (m, _) = MeshFixtures.Load("dltb", "sh2_player_tpp_phx_skeleton");
        Assert.Equal(475, m.Entities.Length);
        Assert.Empty(m.GeometryEntries);
        Assert.Null(m.VertexBuffer);
        var g = m.EntityGlobals();
        Assert.Equal(475, g.Length);
        Assert.All(g, x => Assert.All(x, v => Assert.True(double.IsFinite(v))));
    }

    [Fact]
    public void ClothAndSkinPartsCarried()
    {
        var (m, _) = MeshFixtures.Load("dltb", "dlc_ft_freak_banshee_clothes_matriarch");
        Assert.NotNull(m.ClothRaw);
        Assert.NotNull(m.SkinRaw);
        Assert.True(m.Skinned);
    }

    [Fact]
    public void NonFiniteMeshesDecode()
    {
        foreach (var name in new[] { "gas_tank_pistol_anm", "wn_pistol_b_b" })
        {
            var (m, _) = MeshFixtures.Load("dltb", name);
            var v = m.GeometryEntries[0].Vertices!;
            Assert.False(v.Positions.All(float.IsFinite) && v.Uv0.All(float.IsFinite), name);
            Assert.Equal(m.VertexBuffer, MeshEncoder.EncodeVertexBuffer(m));
        }
    }

    [Fact]
    public void FormatFields()
    {
        var (m, _) = MeshFixtures.Load("dltb", "dlc_ft_safe_zone_cable_e");
        var v = m.GeometryEntries[0].Vertices!;
        Assert.Equal(0, v.Format);
        for (int i = 0; i < v.Count; i++) Assert.Equal(0x3C00, BitConverter.ToUInt16(v.Raw, i * 16 + 6));
        Assert.Null(v.Uv1);
        (m, _) = MeshFixtures.Load("dltb", "sh2_npc_aiden_beast");
        v = m.GeometryEntries[0].Vertices!;
        Assert.Equal(8, v.Format);
        Assert.Equal(15376, v.Count);
        Assert.All(Enumerable.Range(0, v.Count), i => Assert.Equal(255, v.WeightSum(i)));
    }

    [Fact]
    public void EditPositionChangesOnlyThatVertex()
    {
        var (m, _) = MeshFixtures.Load("dltb", "sh2_npc_crane");
        var v = m.GeometryEntries[0].Vertices!;
        var orig = m.VertexBuffer!;
        float keep = v.Positions[30];
        v.Positions[30] += 0.25f;
        var outb = MeshEncoder.EncodeVertexBuffer(m);
        int stride = Vertex.Stride(v.Format);
        var diff = Enumerable.Range(0, orig.Length).Where(i => orig[i] != outb[i]).ToList();
        Assert.NotEmpty(diff);
        Assert.All(diff, i => Assert.InRange(i, 10 * stride, 10 * stride + 11));
        v.Positions[30] = keep;
        Assert.Equal(orig, MeshEncoder.EncodeVertexBuffer(m));
    }

    /// <summary>Divergence by scope: the prototype requantises an edited frame; the C# encoder refuses it.</summary>
    [Fact]
    public void EditedNormalIsRefused()
    {
        var (m, _) = MeshFixtures.Load("dltb", "sh2_npc_crane");
        var v = m.GeometryEntries[0].Vertices!;
        v.Normals[60] = 0; v.Normals[61] = 1; v.Normals[62] = 0;
        Assert.Throws<MeshUnsupportedException>(() => MeshEncoder.EncodeVertexBuffer(m));
    }

    [Fact]
    public void IndexBufferReencodeAfterTopologyEdit()
    {
        var (m, _) = MeshFixtures.Load("dltb", "dummybox_025m");
        var idx = m.GeometryEntries[0].Submeshes[0].Indices;
        idx[0] = idx[3]; idx[1] = idx[4]; idx[2] = idx[5];
        var outb = MeshEncoder.EncodeIndexBuffer(m);
        Assert.Equal(idx[3..6], new[] { BitConverter.ToUInt16(outb, 0), BitConverter.ToUInt16(outb, 2), BitConverter.ToUInt16(outb, 4) });
        Assert.Equal(m.IndexBuffer!.Length, outb.Length);
    }

    [Theory]
    [InlineData("common_meshes_pc.rpack", 300)]
    [InlineData("engine_pc.rpack", int.MaxValue)]
    public void CorpusRoundTrip(string pack, int limit)
    {
        foreach (var p in MeshFixtures.Pack("dltb", pack, limit))
        {
            var m = MeshDecoder.Decode(p);
            foreach (var (t, bytes) in MeshEncoder.Reencode(m)) Assert.True(p.ByType[t].AsSpan().SequenceEqual(bytes), $"{p.Name} 0x{t:X2}");
        }
    }
}
