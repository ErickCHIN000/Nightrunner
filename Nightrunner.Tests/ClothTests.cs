using System.Buffers.Binary;
using Nightrunner.Core.Cast;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Rpack;
using CastMesh = Nightrunner.Core.Cast.Mesh;

namespace Nightrunner.Tests;

// Cloth data (mesh part 0xF3 + its ClassReader objects): no prototype equivalent (the prototype keeps the part opaque).
// Synthetic tests pin the layout rules; install-backed ones decode / re-encode every shipped part and rebuild edited
// cloth meshes (DLTB only — DL2 ships no part 0xF3).
public class ClothTests
{
    private const string Clothes = "dlc_ft_freak_banshee_clothes_matriarch";   // 1 mapping, skinning block (+0x38..+0x50), 2 submeshes
    private const string Hair = "npc_ft_kehinde_hairs_a";                     // 2 mappings (LOD 0 and 1)

    // ---- synthetic --------------------------------------------------------------------------------------------------

    [Fact]
    public void ElementSizes()
    {
        Assert.Equal(166u, ClothData.ElementDwords(ClothElement.U16, 332));
        Assert.Equal(167u, ClothData.ElementDwords(ClothElement.U16, 333));
        Assert.Equal(3u * 8, ClothData.ElementDwords(ClothElement.F32x3, 8));
        Assert.Equal(11u, ClothData.ElementDwords(ClothElement.Bits, 332));   // ceil(332/32)
        Assert.Equal(239u, ClothData.ElementDwords(ClothElement.Bits, 7616));  // a multiple of 32 takes a whole extra dword (npc_ft_obasi_torso_a)
        Assert.Equal(1u, ClothData.ElementDwords(ClothElement.Bits, 0));
        Assert.Equal(2u + 166, ClothData.ElementDwords(ClothElement.Quant16, 332));
        Assert.Equal(2u + 83, ClothData.ElementDwords(ClothElement.Quant8, 332));
        Assert.Equal(2u + 84, ClothData.ElementDwords(ClothElement.Quant8, 333));
    }

    [Fact]
    public void BitsRoundTrip()
    {
        var bits = Enumerable.Range(0, 70).Select(i => i % 3 == 0 || i == 69).ToArray();
        var d = ClothData.WriteBits(bits);
        Assert.Equal(12, d.Length);
        var a = new ClothArray { Owner = 0, Spec = ClothData.MappingFields[6], Data = d };
        Assert.Equal(bits, ClothData.ReadBits(a, 70));
    }

    [Fact]
    public void SubmeshOfVertexAndMarkedVertices()
    {
        var sub = ClothData.SubmeshOfVertex([[0, 1, 2], [3, 4, 2 + 3]], 7, out var why);
        Assert.Null(why);
        Assert.Equal([0, 0, 0, 1, 1, 1, -1], sub!);
        Assert.Null(ClothData.SubmeshOfVertex([[0, 1, 2], [2, 3, 4]], 5, out why));
        Assert.Equal("vertex 2 is used by submeshes 0 and 1", why);
        // marked flags are per mapped vertex; the list holds render vertex indices
        Assert.Equal([2u, 5u], ClothData.MarkedVertices([true, false, true, false, true, true], [false, true, false, true]));
    }

    /// <summary>Layout rule: contiguous arrays in field order from dword 0, zero padding to 16 bytes; offsets written into the image.</summary>
    [Fact]
    public void LayoutAndImagePatch()
    {
        var map = new ClothMapping { Index = 0, Offset = 0x100, Lod = 0, Entry = 0, VertexCount = 3, Unmapped = 0, Marked = 0 };
        var cd = new ClothData
        {
            EntityIndex = 0, InFileOffset = 0, EntityInFileOffset = 0x10, ProxyPalettes = [], Name = null, Simulation = new float[12],
            Surface = ClothData.Absent, IndexCount = 5, ParticleCount = 3, Count44 = 0, Count48 = 5, Bounds = new float[6],
            Sets = [new(0x98, 0, 0, new float[3]), new(0xB8, 0, 0, new float[3]), new(0xD8, 0, 0, new float[3])],
            Set3Count = 0, Set3Batches = 0, LodMapping = new uint[5], Mappings = [map], Colliders = [], Tail = [],
            Arrays =
            [
                new() { Owner = -1, Spec = ClothData.EntityFields[0], Data = new byte[12] },         // 5 × u16 → 3 dwords
                new() { Owner = 0, Spec = ClothData.MappingFields[0], Data = new byte[36] },          // 3 × f32x3
                new() { Owner = 0, Spec = ClothData.MappingFields[6], Data = new byte[4] },           // 3 bits → 1 dword
            ],
        };
        var part = cd.EncodePart();
        Assert.Equal(64, part.Length);                                     // 3 + 9 + 1 dwords = 52 bytes → 64
        Assert.Equal([0u, 3u, 12u], cd.Arrays.Select(a => a.Offset));
        Assert.Equal(12, cd.Tail.Length);
        var img = new byte[0x200];
        cd.PatchImage(img, 0x10, [0x100]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(0x10 + 0x4C)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(0x104)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(0x110)));
        Assert.Equal(ClothData.Absent, BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(0x114)));   // absent Triangles
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(0x128)));
        // a derived-size array of the wrong length is refused by name
        cd.Arrays[1].Data = new byte[40];
        Assert.Contains("M0.Positions", Assert.Throws<MeshFormatException>(() => cd.EncodePart()).Message);
    }

    // ---- install-backed ---------------------------------------------------------------------------------------------

    private static IEnumerable<MeshParts> ClothParts(string game)
    {
        var install = Installs.Require(game);
        foreach (var path in install.Rpacks())
        {
            using var pack = RpackFile.Open(path);
            for (int i = 0; i < pack.Count; i++)
            {
                var lg = pack.Logicals[i];
                if (lg.Type != 0x10) continue;
                bool has = false;
                for (int k = 0; k < lg.PartCount && !has; k++) has = pack.PartType((int)lg.FirstPart + k) == MeshDecoder.PartCloth;
                if (has) yield return MeshDecoder.ReadParts(pack, i);
            }
        }
    }

    /// <summary>Every shipped cloth part decodes, re-encodes byte for byte, re-derives the image offsets and holds every render-mesh invariant.</summary>
    [Fact]
    public void EveryShippedClothPartRoundTrips()
    {
        int n = 0;
        foreach (var p in ClothParts("dltb"))
        {
            var m = MeshDecoder.Decode(p);
            var c = ClothData.Decode(m)!;
            Assert.True(c.EncodePart().AsSpan().SequenceEqual(p.Cloth), p.Name);
            var img = (byte[])p.Image!.Clone();
            c.PatchImage(img, c.EntityInFileOffset, c.Mappings.Select(x => x.Offset).ToList());
            Assert.True(img.AsSpan().SequenceEqual(p.Image), p.Name);
            Assert.Empty(c.Check(m));
            Assert.Equal(p.Name.Trim() + ".msh", c.Name);
            n++;
        }
        Assert.True(n > 0);
    }

    [Fact]
    public void Dl2ShipsNoCloth()
    {
        var install = Installs.Require("dl2");
        foreach (var path in install.Rpacks())
        {
            using var pack = RpackFile.Open(path);
            for (int i = 0; i < pack.Physicals.Length; i++) Assert.NotEqual(MeshDecoder.PartCloth, pack.PartType(i));
        }
    }

    [Fact]
    public void ClothDecodeView()
    {
        var (m, _) = MeshFixtures.Load("dltb", Hair);
        var c = ClothData.Decode(m)!;
        Assert.Equal(2, c.Mappings.Count);
        Assert.Equal([0u, 1u], c.Mappings.Select(x => x.Lod));
        Assert.Equal([0u, 0u, 0u, 0u, 1u], c.LodMapping);
        Assert.True(c.Simulation[1] < 0);                                     // gravity points down
        var tri = ClothData.ReadU16(c.Array(-1, 0x4C)!, c.IndexCount);
        Assert.True(tri.All(t => t < c.ParticleCount));
        Assert.Contains("particles", c.Summary());
    }

    private static ClothData Cloth(IReadOnlyDictionary<byte, byte[]> parts, string name) =>
        ClothData.Decode(MeshDecoder.Decode(name, parts[0x10], parts[0x11], parts[0xF0], parts[0xF1], parts.GetValueOrDefault((byte)0x12), parts[0xF3]))!;

    private static MeshModel Model(IReadOnlyDictionary<byte, byte[]> parts, string name) =>
        MeshDecoder.Decode(name, parts[0x10], parts[0x11], parts[0xF0], parts[0xF1], parts.GetValueOrDefault((byte)0x12), parts[0xF3]);

    /// <summary>An unedited scene reproduces the cloth part and the image byte for byte.</summary>
    [Fact]
    public void UneditedClothMeshIsIdentical()
    {
        using var w = MeshWork.Export(Clothes);
        var (c, _) = w.LoadCast();
        var r = w.Build(c);
        foreach (var t in new byte[] { 0x10, 0x11, 0xF0, 0xF1, 0xF3 }) Assert.True(w.Parts.ByType[t].AsSpan().SequenceEqual(r.Parts[t]), $"0x{t:X2}");
        Assert.False(r.Report["cloth"]!["changed"]!.GetValue<bool>());
    }

    /// <summary>
    /// A moved vertex: the cloth's copy of the position follows (it equals the vertex buffer in every shipped part), every
    /// other cloth byte is kept, and a moved bound vertex is reported (its binding is not recomputed).
    /// </summary>
    [Fact]
    public void PositionEditSyncsTheCopy()
    {
        using var w = MeshWork.Export(Clothes);
        var (c, mdl) = w.LoadCast();
        var mesh = mdl.Meshes()[0];
        var vp = mesh.VertexPositionBuffer()!;
        vp[1] += 0.005f;
        mesh.SetVertexPositionBuffer(vp);
        var r = w.Build(c);
        Assert.False(r.Report["layout_changed"]!.GetValue<bool>());
        var before = ClothData.Decode(w.Model)!;
        var after = Cloth(r.Parts, w.Name);
        Assert.Equal(before.Arrays.Count, after.Arrays.Count);
        var m2 = Model(r.Parts, w.Name);
        Assert.Empty(after.Check(m2));
        int changed = 0;
        foreach (var (a, b) in before.Arrays.Zip(after.Arrays))
        {
            Assert.Equal(a.Offset, b.Offset);
            if (!a.Data.AsSpan().SequenceEqual(b.Data)) { changed++; Assert.Equal("M0.Positions", b.Name); }
        }
        Assert.Equal(1, changed);
        int v0 = (int)mesh.Property("bp_vertex_id")!.ToUInt32Array()[0];
        bool bound = before.Mapped(before.Mappings[0])[v0];
        Assert.Equal(bound, r.Warnings.Any(x => x.StartsWith(MeshRebuild.ClothRefusal) && x.Contains("binding is kept")));
    }

    /// <summary>A duplicated vertex (the copy recovers the source's id): every per-vertex cloth datum is duplicated with it.</summary>
    [Fact]
    public void DuplicatedVertexRemaps()
    {
        using var w = MeshWork.Export(Clothes);
        var (c, mdl) = w.LoadCast();
        var mesh = mdl.Meshes()[0];
        int n = mesh.VertexCount()!.Value;
        int src = (int)mesh.Property("bp_vertex_id")!.ToUInt32Array()[0];
        Keep(mesh, [.. Enumerable.Range(0, n), 0], [.. mesh.FaceBuffer()!, (uint)n, mesh.FaceBuffer()![1], mesh.FaceBuffer()![2]]);
        mesh.RemoveProperty("bp_vertex_id");
        var r = w.Build(c);
        var before = ClothData.Decode(w.Model)!;
        var after = Cloth(r.Parts, w.Name);
        var m2 = Model(r.Parts, w.Name);
        Assert.Empty(after.Check(m2));
        var map = after.Mappings[0];
        Assert.Equal(w.Model.GeometryEntries[0].VertexCount + 1, (int)map.VertexCount);
        Assert.Equal(m2.GeometryEntries[0].VertexCount, (int)map.VertexCount);
        // the new vertex sits at the end of submesh 0's run in the rebuilt window
        int dup = n;
        var mb = before.Mapped(before.Mappings[0]);
        var ma = after.Mapped(map);
        Assert.Equal(mb[src], ma[dup]);
        Assert.True(r.Report["cloth"]!["changed"]!.GetValue<bool>());
        Assert.Equal(1, r.Report["cloth"]!["remapped_entries"]!.GetValue<int>());
    }

    /// <summary>
    /// Deleting faces and the vertices only they used, and reversing the vertex order: every per-vertex datum follows its
    /// source vertex (gathered by the plan's ids), per-bound-vertex data follows the bound ones, ranks and counts re-derive.
    /// </summary>
    [Theory]
    [InlineData(Clothes, 0)]
    [InlineData(Hair, 1)]                                                      // the LOD-1 mapping of a two-mapping part
    public void ReorderAndDeleteRemaps(string name, int entry)
    {
        using var w = MeshWork.Export(name);
        var (c, mdl) = w.LoadCast();
        var mesh = mdl.Meshes().First(x => x.Property("bp_entry")!.NumberAt(0) == entry);
        var faces = mesh.FaceBuffer()!;
        var keptFaces = faces[..(faces.Length - 30)];
        var used = keptFaces.Distinct().Select(x => (int)x).OrderByDescending(x => x).ToArray();   // reversed order
        var newIndex = new Dictionary<int, uint>();
        for (int i = 0; i < used.Length; i++) newIndex[used[i]] = (uint)i;
        Keep(mesh, used, keptFaces.Select(f => newIndex[(int)f]).ToArray());
        mesh.RemoveProperty("bp_vertex_id");
        var imp = w.Resolve(c);
        var result = MeshRebuild.Rebuild(w.Model, imp, w.Name, ignoreBoneChanges: true);
        Assert.Empty(MeshRebuild.Verify(result, w.Model, w.Name));
        var ids = result.Plan.Entries[entry].RawIds!;
        Assert.True(ids.All(x => x >= 0));
        var parts = new Dictionary<byte, byte[]>(w.Parts.ByType)
        {
            [0x10] = result.Image, [0x11] = result.Fixups, [0xF0] = result.Vertex!, [0xF1] = result.Index!, [0xF3] = result.Cloth!,
        };
        var before = ClothData.Decode(w.Model)!;
        var after = Cloth(parts, w.Name);
        Assert.Empty(after.Check(Model(parts, w.Name)));
        var bm = before.Mappings.Single(x => x.Entry == entry);
        var am = after.Mappings.Single(x => x.Entry == entry);
        int k = bm.Index;
        Assert.Equal(ids.Length, (int)am.VertexCount);
        Assert.True(am.VertexCount < bm.VertexCount);
        // the other mapping (if any) is untouched
        foreach (var a in before.Arrays.Where(a => a.Owner != k))
            Assert.True(a.Data.AsSpan().SequenceEqual(after.Array(a.Owner, a.Spec.Field)!.Data), a.Name);
        // per-vertex copies gather by id
        foreach (int f in new[] { 0x28, 0x38 })
        {
            if (before.Array(k, f) is not { } ba) continue;
            var ob = ClothData.ReadBits(ba, bm.VertexCount);
            Assert.Equal(ids.Select(i => ob[i]), ClothData.ReadBits(after.Array(k, f)!, am.VertexCount));
        }
        foreach (var (f, width) in new[] { (0x40, 3), (0x44, 3), (0x48, 4) })
        {
            if (before.Array(k, f) is not { } ba) continue;
            var ob = ClothData.ReadF32(ba);
            var oa = ClothData.ReadF32(after.Array(k, f)!);
            for (int i = 0; i < ids.Length; i++)
                Assert.True(ob.AsSpan((int)ids[i] * width, width).SequenceEqual(oa.AsSpan(i * width, width)), $"+0x{f:X2} vertex {i}");
        }
        // per-bound-vertex data: the bound vertices in new order, each with its source's binding
        var mappedB = before.Mapped(bm);
        var rankB = new int[mappedB.Length];
        for (int i = 0, r = 0; i < mappedB.Length; i++) rankB[i] = mappedB[i] ? r++ : -1;
        foreach (var (f, size) in new[] { (0x14, 2), (0x18, 2), (0x1C, 1), (0x20, 2), (0x24, 2) })
        {
            int hdr = f == 0x14 ? 0 : 8;
            var ob = before.Array(k, f)!.Data;
            var oa = after.Array(k, f)!.Data;
            Assert.True(ob.AsSpan(0, hdr).SequenceEqual(oa.AsSpan(0, hdr)));
            var expect = ids.Where(i => mappedB[i]).SelectMany(i => ob.AsSpan(hdr + rankB[i] * size, size).ToArray()).ToArray();
            Assert.True(expect.AsSpan().SequenceEqual(oa.AsSpan(hdr, expect.Length)), $"+0x{f:X2}");
        }
        Assert.Equal(am.VertexCount - (uint)ids.Count(i => mappedB[i]), am.Unmapped);
    }

    /// <summary>A vertex with no source (not recoverable) cannot be bound to the simulation mesh: refused by name.</summary>
    [Fact]
    public void NewVertexIsRefused()
    {
        using var w = MeshWork.Export(Clothes);
        var (c, mdl) = w.LoadCast();
        var mesh = mdl.Meshes()[0];
        int n = mesh.VertexCount()!.Value;
        Keep(mesh, [.. Enumerable.Range(0, n), 0], [.. mesh.FaceBuffer()!, (uint)n, mesh.FaceBuffer()![1], mesh.FaceBuffer()![2]]);
        var vp = mesh.VertexPositionBuffer()!;
        vp[n * 3] += 0.25f;                                                   // the copy moves away: no exact match
        mesh.SetVertexPositionBuffer(vp);
        mesh.RemoveProperty("bp_vertex_id");
        var e = Assert.Throws<MeshBuildException>(() => w.Build(c));
        Assert.StartsWith(MeshRebuild.ClothRefusal, e.Message);
        Assert.Contains($"vertex {n} is new", e.Message);
    }

    /// <summary>Reorders / subsets every per-vertex buffer of a Cast mesh (positions, normals, tangents, UVs, weights, bp_ properties).</summary>
    private static void Keep(CastMesh mesh, int[] keep, uint[] faces)
    {
        static float[] G(float[] a, int[] keep, int w) => keep.SelectMany(i => a.AsSpan(i * w, w).ToArray()).ToArray();
        int n = mesh.VertexCount()!.Value;
        mesh.SetVertexPositionBuffer(G(mesh.VertexPositionBuffer()!, keep, 3));
        mesh.SetVertexNormalBuffer(G(mesh.VertexNormalBuffer()!, keep, 3));
        mesh.SetVertexTangentBuffer(G(mesh.VertexTangentBuffer()!, keep, 3));
        for (int l = 0; l < mesh.UVLayerCount(); l++) mesh.SetVertexUVLayerBuffer(l, G(mesh.VertexUVLayerBuffer(l)!, keep, 2));
        if (mesh.VertexWeightValueBuffer() is { } wv)
        {
            int mi = wv.Length / n;
            mesh.SetVertexWeightValueBuffer(G(wv, keep, mi));
            var wb = mesh.VertexWeightBoneBuffer()!;
            mesh.SetVertexWeightBoneBuffer(keep.SelectMany(i => wb.AsSpan(i * mi, mi).ToArray()).ToArray());
        }
        var sign = G(mesh.Property("bp_tangent_sign")!.ToSingleArray(), keep, 1);
        mesh.CreateProperty("bp_tangent_sign", CastPropertyType.Float).SetValues(sign);
        if (mesh.Property("bp_vertex_id") is { } ids)
        {
            var v = ids.ToUInt32Array();
            mesh.CreateProperty("bp_vertex_id", CastPropertyType.Integer).SetValues(keep.Select(i => v[i]).ToArray());
        }
        mesh.SetFaceBuffer(faces);
    }
}
