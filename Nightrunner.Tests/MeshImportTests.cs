using System.Text.Json.Nodes;
using Nightrunner.Core.Cast;
using Nightrunner.Core.Mesh;
using CastModel = Nightrunner.Core.Cast.Model;
using File = System.IO.File;

namespace Nightrunner.Tests;

// Port of nightrunner-main/tests/test_mesh_import.py (cast/import_.py): parsing, matching against the sidecar,
// Blender-round-trip tolerance, refusals. The prototype runs on its extracted sample tree; here each sample is exported
// from the DLTB install with CastExport.WriteFiles (byte-identical sidecars, see docs/porting.md) into a temp folder.
//
// Tiers: NameRule and DegenerateTangents are synthetic and always run; the rest skip without a DLTB install.
public class MeshImportTests
{
    internal const string Pants = "npc_b_man_pants_b_holster_bag_c";
    internal const string Box = "dummybox_025m";

    internal static readonly string[] Samples =
    [
        "sh_npc_ft_crane_hair_a", "sh2_player_tpp_phx_skeleton", "dlc_ft_freak_banshee_clothes_matriarch",
        "gas_tank_pistol_anm", "wn_pistol_b_b", "dlc_ft_safe_zone_cable_e", "sh2_npc_aiden_beast", "sh2_npc_crane",
        "dummybox_025m", "npc_b_man_pants_b_holster_bag_c", "sh2_npc_ft_crane_beard_a", "anim_hammer_a",
        "bdp_ce_a_ornament_str_d", "alarm_siren_anm", "barrier", "ui_bg_ph_ft", "ui_bg_the_beast",
    ];

    /// <summary>Simulates the official Blender plugin round trip: custom properties gone, no tangents, `.001` suffixes.</summary>
    internal static void StripBlenderLike(CastModel mdl)
    {
        foreach (var mesh in mdl.Meshes())
        {
            foreach (var k in mesh.Properties.Keys.Where(k => k.StartsWith("bp_", StringComparison.Ordinal)).ToList()) mesh.RemoveProperty(k);
            mesh.RemoveProperty("vt");
            mesh.SetName(mesh.Name() + ".001");
        }
        if (mdl.Skeleton() is { } skel)
            foreach (var b in skel.Bones())
                foreach (var k in b.Properties.Keys.Where(k => k.StartsWith("bp_", StringComparison.Ordinal)).ToList()) b.RemoveProperty(k);
        foreach (var m in mdl.Materials())
            foreach (var k in m.Properties.Keys.Where(k => k.StartsWith("bp_", StringComparison.Ordinal)).ToList()) m.RemoveProperty(k);
    }

    // ---- synthetic ------------------------------------------------------------------------------------------------

    [Fact]
    public void NameRule()
    {
        var m = CastImport.MeshNameRe().Match("x.e0.s1");
        Assert.Equal(("0", "1"), (m.Groups[1].Value, m.Groups[2].Value));
        m = CastImport.MeshNameRe().Match("a.b.e12.s3.001");
        Assert.Equal(("12", "3"), (m.Groups[1].Value, m.Groups[2].Value));
        Assert.DoesNotMatch(CastImport.MeshNameRe(), "a.e1s2");
        Assert.DoesNotMatch(CastImport.MeshNameRe(), "a.e1.s2.abc");
    }

    [Fact]
    public void DegenerateTangents()
    {
        var (t, s) = Vertex.TangentsFromUv(new float[9], [0, 0, 1, 0, 0, 1, 0, 0, 1], new float[6], [0, 1, 2]);
        Assert.Equal(9, t.Length);
        for (int i = 0; i < 3; i++)
            Assert.Equal(1.0, Math.Sqrt(t[i * 3] * t[i * 3] + t[i * 3 + 1] * t[i * 3 + 1] + t[i * 3 + 2] * t[i * 3 + 2]), 6);
        Assert.Equal([1, 1, 1], s.Select(x => (int)x));
    }

    [Fact]
    public void SidecarSchemaAcceptsLegacySpelling()
    {
        Assert.True(CastImport.SchemaMatches(JsonValue.Create("nightrunner.mesh/1")));
        Assert.True(CastImport.SchemaMatches(JsonValue.Create("beastpack.mesh/1")));
        Assert.False(CastImport.SchemaMatches(JsonValue.Create("nightrunner.model_cast/2")));
        Assert.False(CastImport.SchemaMatches(null));
    }

    // ---- install-backed -------------------------------------------------------------------------------------------

    [Fact]
    public void EverySampleResolvesByBpProperties()
    {
        foreach (var name in Samples)
        {
            using var w = MeshWork.Export(name);
            var scene = CastImport.Read(w.CastPath);
            Assert.Equal("y", scene.UpAxis);
            Assert.StartsWith("nightrunner", scene.Software);
            Assert.Equal(w.Sidecar["entities"]!.AsArray().Count, scene.Bones.Count);
            Assert.Equal(w.Sidecar["cast"]!["meshes"]!.AsArray().Count, scene.Meshes.Count);
            var imp = CastImport.Resolve(scene, w.Sidecar);
            Assert.True(imp.Warnings.Count == 0, name + ": " + string.Join("; ", imp.Warnings));
            Assert.Empty(imp.BoneChanges);
            Assert.Empty(imp.NewMaterials);
            Assert.Equal("identity", imp.BonesMatchedBy);
            foreach (var m in imp.Meshes)
            {
                Assert.Equal("bp", m.MatchedBy);
                Assert.Equal("bp", m.MaterialMatchedBy);
                Assert.NotNull(m.VertexIds);
                Assert.Empty(m.Derived);
                var e = w.Model.GeometryEntries[m.Entry];
                Assert.Equal(e.Format, m.Format);
                Assert.Equal(e.Submeshes[m.Submesh].MaterialSlot, m.MaterialSlot);
                Assert.Equal(e.Submeshes[m.Submesh].IndexCount, m.Faces.Length);
                if (m.Weights is { } wt)
                {
                    for (int i = 0; i < m.VertexCount; i++) Assert.Equal(1.0, wt[i * 4] + wt[i * 4 + 1] + wt[i * 4 + 2] + wt[i * 4 + 3], 5);
                    Assert.All(m.Bones!, b => Assert.True(b < w.Model.Entities.Length));
                }
            }
        }
    }

    [Fact]
    public void BlenderLikeCastMatchesByName()
    {
        using var w = MeshWork.Export(Pants);
        var (c, mdl) = w.LoadCast();
        StripBlenderLike(mdl);
        var imp = w.Resolve(c);
        Assert.Equal(["name", "name"], imp.Meshes.Select(m => m.MatchedBy));
        Assert.Equal(["exact", "exact"], imp.Meshes.Select(m => m.MaterialMatchedBy));
        Assert.All(imp.Meshes, m => Assert.Null(m.VertexIds));
        Assert.All(imp.Meshes, m => Assert.Contains("tangents (from UV derivatives)", m.Derived));
        Assert.Equal("identity", imp.BonesMatchedBy);
        Assert.Empty(imp.BoneChanges);
    }

    [Fact]
    public void UnmatchableNameRefused()
    {
        using var w = MeshWork.Export(Pants);
        var (c, mdl) = w.LoadCast();
        StripBlenderLike(mdl);
        mdl.Meshes()[0].SetName("Cube");
        var e = Assert.Throws<MeshBuildException>(() => w.Resolve(c));
        Assert.Contains("'Cube'", e.Message);
        Assert.Contains(".e<entry>.s<submesh>", e.Message);
    }

    [Fact]
    public void DuplicateTargetRefused()
    {
        using var w = MeshWork.Export(Pants);
        var (c, mdl) = w.LoadCast();
        mdl.Meshes()[1].CreateProperty("bp_submesh", CastPropertyType.Integer).SetValues([0u]);
        var e = Assert.Throws<MeshBuildException>(() => w.Resolve(c));
        Assert.Contains("both map to entry 0 submesh 0", e.Message);
    }

    [Fact]
    public void BoneCountMismatchRefused()
    {
        using var w = MeshWork.Export(Pants);
        var (c, mdl) = w.LoadCast();
        var skel = mdl.Skeleton()!;
        skel.RemoveChild(skel.Bones()[^1]);
        var e = Assert.Throws<MeshUnsupportedException>(() => w.Resolve(c));
        Assert.Contains("85 bones", e.Message);
        Assert.Contains("86 entities", e.Message);
    }

    [Fact]
    public void BoneReorderMatchedByName()
    {
        using var w = MeshWork.Export(Pants);
        var (c, mdl) = w.LoadCast();
        var skel = mdl.Skeleton()!;
        var bones = skel.Bones();
        var parents = bones.Select(b => b.ParentIndex()).ToHashSet();
        var leaves = Enumerable.Range(0, bones.Count).Where(i => !parents.Contains(i)).ToList();
        int i0 = leaves[0], j0 = leaves[1];
        // swap two bones that are nobody's parent: rebuild the skeleton's children in the new order
        var order = bones.ToList();
        (order[i0], order[j0]) = (order[j0], order[i0]);
        foreach (var b in bones) skel.RemoveChild(b);
        foreach (var b in order) skel.CreateChild(b);
        foreach (var b in skel.Bones())
        {
            int p = b.ParentIndex();
            b.SetParentIndex(p == i0 ? j0 : p == j0 ? i0 : p);
            foreach (var k in b.Properties.Keys.Where(k => k.StartsWith("bp_", StringComparison.Ordinal)).ToList()) b.RemoveProperty(k);
        }
        var imp = w.Resolve(c);
        Assert.Equal("name", imp.BonesMatchedBy);
        Assert.Equal(j0, imp.EntityMap![i0]);
        Assert.Equal(i0, imp.EntityMap[j0]);
        Assert.Empty(imp.BoneChanges);
        foreach (var m in imp.Meshes)
        {
            var pal = w.Model.GeometryEntries[m.Entry].Submeshes[m.Submesh].Palette;
            for (int i = 0; i < m.Weights!.Length; i++)
                if (m.Weights[i] > 0) Assert.Contains((ushort)m.Bones![i], pal);
        }
    }

    [Fact]
    public void BoneTransformChangeDetected()
    {
        using var w = MeshWork.Export(Pants);
        var (c, mdl) = w.LoadCast();
        var b = mdl.Skeleton()!.Bones()[3];
        var lp = b.LocalPosition()!;
        lp[1] += 0.02f;
        b.SetLocalPosition(lp);
        var imp = w.Resolve(c);
        Assert.Single(imp.BoneChanges);
        Assert.Contains(b.Name()!, imp.BoneChanges[0]);
    }

    [Fact]
    public void MaterialNewAndCaseFold()
    {
        using var w = MeshWork.Export(Box);
        var (c, mdl) = w.LoadCast();
        var mat = mdl.Materials()[0];
        mat.SetName("dummybox.mat");        // the sidecar has 'DUMMYBOX.MAT'
        mat.RemoveProperty("bp_material_slot");
        var imp = w.Resolve(c);
        Assert.Equal("fold", imp.Meshes[0].MaterialMatchedBy);
        Assert.Equal(0, imp.Meshes[0].MaterialSlot);
        Assert.Empty(imp.NewMaterials);
        mat.SetName("brand_new.mat");
        imp = w.Resolve(c);
        Assert.Equal("new", imp.Meshes[0].MaterialMatchedBy);
        Assert.Null(imp.Meshes[0].MaterialSlot);
        Assert.Equal(["brand_new.mat"], imp.NewMaterials);
    }

    [Fact]
    public void WeightRefusals()
    {
        using var w = MeshWork.Export(Pants);
        var (c, mdl) = w.LoadCast();
        var mesh = mdl.Meshes()[0];
        int n = mesh.VertexCount()!.Value;
        var wv = mesh.VertexWeightValueBuffer()!;
        var wb = mesh.VertexWeightBoneBuffer()!;
        var zero = (float[])wv.Clone();
        zero[0] = zero[1] = zero[2] = zero[3] = 0;
        mesh.SetVertexWeightValueBuffer(zero);
        var e = Assert.Throws<MeshBuildException>(() => w.Resolve(c));
        Assert.Contains("vertex 0 has no bone weights", e.Message);
        // five influences on vertex 0
        var wv5 = new List<float>();
        var wb5 = new List<uint>();
        for (int i = 0; i < n; i++)
        {
            if (i == 0) { wv5.AddRange([0.2f, 0.2f, 0.2f, 0.2f, 0.2f]); wb5.AddRange([0u, 1, 2, 3, 4]); }
            else { wv5.AddRange([.. wv[(i * 4)..(i * 4 + 4)], 0f]); wb5.AddRange([.. wb[(i * 4)..(i * 4 + 4)], 0u]); }
        }
        mesh.SetVertexWeightValueBuffer([.. wv5]);
        mesh.SetVertexWeightBoneBuffer([.. wb5]);
        mesh.SetMaximumWeightInfluence(5);
        e = Assert.Throws<MeshBuildException>(() => w.Resolve(c));
        Assert.Contains("5 non-zero weights", e.Message);
    }

    [Fact]
    public void MoreThanFourLanesButFourActiveIsFine()
    {
        using var w = MeshWork.Export(Pants);
        var (c, mdl) = w.LoadCast();
        var mesh = mdl.Meshes()[1];
        int n = mesh.VertexCount()!.Value;
        var wv = mesh.VertexWeightValueBuffer()!;
        var wb = mesh.VertexWeightBoneBuffer()!;
        mesh.SetVertexWeightValueBuffer(Enumerable.Range(0, n).SelectMany(i => wv[(i * 4)..(i * 4 + 4)].Concat([0f, 0f])).ToArray());
        mesh.SetVertexWeightBoneBuffer(Enumerable.Range(0, n).SelectMany(i => wb[(i * 4)..(i * 4 + 4)].Concat([0u, 0u])).ToArray());
        mesh.SetMaximumWeightInfluence(6);
        var m = w.Resolve(c).Meshes[1];
        Assert.Equal(n * 4, m.Weights!.Length);
        for (int i = 0; i < n; i++) Assert.Equal(1.0, m.Weights[i * 4] + m.Weights[i * 4 + 1] + m.Weights[i * 4 + 2] + m.Weights[i * 4 + 3], 5);
    }

    /// <summary>Invariant: new geometry may only use bones the mesh already has — a weight on any other bone is refused.</summary>
    [Fact]
    public void WeightOnABoneTheMeshLacksRefused()
    {
        using var w = MeshWork.Export(Pants);
        var (c, mdl) = w.LoadCast();
        var mesh = mdl.Meshes()[0];
        var wb = mesh.VertexWeightBoneBuffer()!;
        var wv = mesh.VertexWeightValueBuffer()!;
        wb[0] = (uint)w.Model.Entities.Length;       // one past the skeleton
        wv[0] = 1; wv[1] = wv[2] = wv[3] = 0;
        mesh.SetVertexWeightBoneBuffer(wb);
        mesh.SetVertexWeightValueBuffer(wv);
        var e = Assert.Throws<MeshBuildException>(() => w.Build(c));
        Assert.Contains("which the mesh does not have", e.Message);
        Assert.Contains("new geometry may only use bones the mesh already has", e.Message);
    }

    [Fact]
    public void UvTangentsAgreeWithStoredFramesOnOutfitMesh()
    {
        using var w = MeshWork.Export(Pants);
        var scene = CastImport.Read(w.CastPath);
        foreach (var m in scene.Meshes)
        {
            var (t, s) = Vertex.TangentsFromUv(m.Positions, m.Normals!, m.Uv0!, m.Faces);
            int n = m.VertexCount, close = 0, same = 0;
            for (int i = 0; i < n; i++)
            {
                double dot = Math.Abs(t[i * 3] * m.Tangents![i * 3] + t[i * 3 + 1] * m.Tangents[i * 3 + 1] + t[i * 3 + 2] * m.Tangents[i * 3 + 2]);
                if (dot > 0.9) close++;
                if (s[i] == m.TangentSign![i]) same++;
            }
            Assert.True(close > 0.99 * n, m.Name);
            Assert.True(same > 0.99 * n, m.Name);
        }
    }
}

/// <summary>
/// A DLTB mesh exported (Cast + sidecar) into a temp folder, for building edited scenes from it. Skips without an install.
/// </summary>
internal sealed class MeshWork : IDisposable
{
    public required MeshModel Model { get; init; }
    public required MeshParts Parts { get; init; }
    public required string Dir { get; init; }
    public required string CastPath { get; init; }
    public required JsonObject Sidecar { get; init; }

    public string Name => Parts.Name;

    public static MeshWork Export(string name)
    {
        var (m, p) = MeshFixtures.Load("dltb", name);
        string dir = Path.Combine(Path.GetTempPath(), "nightrunner-meshbuild", Guid.NewGuid().ToString("N"));
        string cast = Path.Combine(dir, "model.cast");
        var (_, side) = CastExport.WriteFiles(m, p.Name, cast);
        return new MeshWork
        {
            Model = m, Parts = p, Dir = dir, CastPath = cast, Sidecar = JsonNode.Parse(File.ReadAllText(side!))!.AsObject(),
        };
    }

    public (CastFile Cast, CastModel Model) LoadCast()
    {
        var c = CastFile.Load(CastPath);
        return (c, c.Roots()[0].ChildOfType<CastModel>()!);
    }

    public string Save(CastFile cast, string file = "edited.cast")
    {
        string p = Path.Combine(Dir, file);
        cast.Save(p);
        return p;
    }

    public ResolvedCastImport Resolve(CastFile cast) => CastImport.Resolve(CastImport.Read(Save(cast)), Sidecar);

    public MeshBuildResult Build(CastFile cast, string? name = null, MeshBuildOptions? options = null) =>
        MeshBuild.Build(Parts.ByType, name ?? Name, Save(cast), Sidecar, options);

    public void Dispose()
    {
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); }
        catch (IOException) { }
    }
}
