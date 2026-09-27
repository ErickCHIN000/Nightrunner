using Nightrunner.Core.Cast;
using Nightrunner.Core.Mesh;
using CastModel = Nightrunner.Core.Cast.Model;

namespace Nightrunner.BuildCheck;

/// <summary>
/// The edited scenes BuildCheck derives from a mesh's own Cast export, each an edit of the loaded Cast (false: the edit
/// does not apply to this mesh). Shared with Nightrunner.Tests (MeshBuildTests.EditedBuildsAreFrozen), which freezes
/// the port's rebuilt parts for the same scenes.
/// </summary>
public static class MeshBuildVariants
{
    public static IEnumerable<(string Name, Func<CastModel, MeshModel, bool> Edit)> All()
    {
        yield return ("unedited", (_, _) => true);
        yield return ("rename", (_, _) => true);
        yield return ("gltf", (_, _) => true);
        yield return ("gltfmoved", (mdl, _) =>
        {
            if (mdl.Meshes().FirstOrDefault() is not { } m || m.VertexPositionBuffer() is not { Length: > 0 } vp) return false;
            for (int i = 0; i < vp.Length / 3; i += 97) vp[i * 3 + 1] += 0.01f;
            m.SetVertexPositionBuffer(vp);
            return true;
        });
        yield return ("moved", (mdl, _) =>
        {
            if (mdl.Meshes().FirstOrDefault() is not { } m || m.VertexPositionBuffer() is not { Length: > 0 } vp) return false;
            for (int i = 0; i < vp.Length / 3; i += 97) vp[i * 3] += 0.01f;
            m.SetVertexPositionBuffer(vp);
            return true;
        });
        yield return ("deltri", (mdl, _) =>
        {
            if (mdl.Meshes().FirstOrDefault(x => x.FaceCount() >= 2) is not { } m) return false;
            m.SetFaceBuffer(m.FaceBuffer()![3..]);
            return true;
        });
        yield return ("blender", (mdl, _) =>
        {
            foreach (var m in mdl.Meshes())
            {
                foreach (var k in m.Properties.Keys.Where(k => k.StartsWith("bp_", StringComparison.Ordinal)).ToList()) m.RemoveProperty(k);
                m.RemoveProperty("vt");
                m.SetName(m.Name() + ".001");
            }
            if (mdl.Skeleton() is { } skel)
                foreach (var b in skel.Bones())
                    foreach (var k in b.Properties.Keys.Where(k => k.StartsWith("bp_", StringComparison.Ordinal)).ToList()) b.RemoveProperty(k);
            foreach (var mat in mdl.Materials())
                foreach (var k in mat.Properties.Keys.Where(k => k.StartsWith("bp_", StringComparison.Ordinal)).ToList()) mat.RemoveProperty(k);
            return mdl.Meshes().Count > 0;
        });
        yield return ("newmat", (mdl, _) =>
        {
            if (mdl.Meshes().FirstOrDefault() is not { } m) return false;
            var mat = mdl.CreateMaterial();
            mat.Hash = NextHash(mdl);
            mat.SetName("nr_buildcheck_new.mat");
            mat.SetType("pbr");
            m.SetMaterial(mat.Hash);
            m.RemoveProperty("bp_material_slot");
            return true;
        });
        yield return ("normals", (mdl, _) =>
        {
            if (mdl.Meshes().FirstOrDefault() is not { } m || m.VertexNormalBuffer() is not { Length: > 0 } vn) return false;
            for (int i = 0; i < vn.Length / 3; i += 50)
            {
                float x = vn[i * 3] + 0.1f, y = vn[i * 3 + 1], z = vn[i * 3 + 2];
                float l = MathF.Sqrt(x * x + y * y + z * z);
                (vn[i * 3], vn[i * 3 + 1], vn[i * 3 + 2]) = (x / l, y / l, z / l);
            }
            m.SetVertexNormalBuffer(vn);
            return true;
        });
        yield return ("weights", (mdl, _) =>
        {
            bool any = false;
            foreach (var m in mdl.Meshes().Where(x => x.MaximumWeightInfluence() == 4))
            {
                var wv = m.VertexWeightValueBuffer()!;
                bool edited = false;
                for (int i = 0, seen = 0; i < wv.Length / 4; i++)
                {
                    int act = 0;
                    for (int k = 0; k < 4; k++) if (wv[i * 4 + k] > 0) act++;
                    if (act < 2 || seen++ % 5 != 0) continue;
                    for (int k = 0; k < 4; k++) if (wv[i * 4 + k] > 0) wv[i * 4 + k] = 1f / act;
                    edited = true;
                }
                if (edited) m.SetVertexWeightValueBuffer(wv);
                any |= edited;
            }
            return any;
        });
        yield return ("palette", (mdl, model) =>
        {
            // vertex 0 of the first skinned mesh weighted fully to an entity outside its submesh palette (palette growth)
            if (mdl.Meshes().FirstOrDefault(x => x.MaximumWeightInfluence() == 4) is not { } m) return false;
            if (m.Property("bp_entry") is not { } pe || m.Property("bp_submesh") is not { } ps) return false;
            var pal = model.GeometryEntries[(int)pe.NumberAt(0)].Submeshes[(int)ps.NumberAt(0)].Palette;
            int bone = Enumerable.Range(0, model.Entities.Length).FirstOrDefault(i => !pal.Contains((ushort)i), -1);
            if (bone < 0) return false;
            var wb = m.VertexWeightBoneBuffer()!;
            var wv = m.VertexWeightValueBuffer()!;
            wb[0] = (uint)bone;
            wv[0] = 1; wv[1] = wv[2] = wv[3] = 0;
            m.SetVertexWeightBoneBuffer(wb);
            m.SetVertexWeightValueBuffer(wv);
            return true;
        });
        yield return ("addvert", (mdl, _) =>
        {
            if (mdl.Meshes().FirstOrDefault(x => x.FaceCount() >= 1) is not { } m) return false;
            int n = m.VertexCount() ?? 0;
            float[] Add(float[] a, int w, float dx = 0) { var r = new float[a.Length + w]; a.CopyTo(r, 0); for (int k = 0; k < w; k++) r[a.Length + k] = a[k] + (k == 0 ? dx : 0); return r; }
            m.SetVertexPositionBuffer(Add(m.VertexPositionBuffer()!, 3, 0.001f));
            m.SetVertexNormalBuffer(Add(m.VertexNormalBuffer()!, 3));
            if (m.VertexTangentBuffer() is { } vt) m.SetVertexTangentBuffer(Add(vt, 3));
            for (int l = 0; l < m.UVLayerCount(); l++) m.SetVertexUVLayerBuffer(l, Add(m.VertexUVLayerBuffer(l)!, 2));
            if (m.VertexWeightValueBuffer() is { } wv)
            {
                m.SetVertexWeightValueBuffer(Add(wv, 4));
                var wb = m.VertexWeightBoneBuffer()!;
                m.SetVertexWeightBoneBuffer([.. wb, .. wb[..4]]);
            }
            if (m.Property("bp_tangent_sign") is { } ts) m.CreateProperty("bp_tangent_sign", CastPropertyType.Float).SetValues([.. ts.ToSingleArray(), ts.ToSingleArray()[0]]);
            m.RemoveProperty("bp_vertex_id");
            var f = m.FaceBuffer()!;
            m.SetFaceBuffer([.. f, (uint)n, f[1], f[2]]);
            return true;
        });
    }

    private static ulong NextHash(CastNode node)
    {
        ulong max = 0;
        void Walk(CastNode n) { max = Math.Max(max, n.Hash); foreach (var c in n.ChildNodes) Walk(c); }
        var top = node;
        while (top.ParentNode is { } p) top = p;
        Walk(top);
        return max + 1;
    }
}
