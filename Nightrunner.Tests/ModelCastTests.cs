using Nightrunner.Core.Cast;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;
using Nightrunner.Core.Rpack;
using CastMesh = Nightrunner.Core.Cast.Mesh;
using CastModel = Nightrunner.Core.Cast.Model;

namespace Nightrunner.Tests;

/// <summary>
/// The single-file model Cast (port of <c>gui/modelcast.py</c>) with the corrected rest pose: the head's vertices, read
/// back by <c>bp_vertex_id</c>, sit where the mesh stores them.
/// </summary>
public class ModelCastTests
{
    [Fact]
    public async Task PlayerHeadKeepsItsShape()
    {
        var install = Installs.Require("dltb");
        using var rpacks = new RpackCatalog();
        await rpacks.LoadAsync(install.Rpacks(), install.Assets, TestContext.Current.CancellationToken);
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
        string dir = Directory.CreateTempSubdirectory("nr-modelcast-").FullName;
        try
        {
            var result = ModelCast.Write(res, rpacks, Decode, dir, "player_kc_basic_tpp", textures: false,
                                         ct: TestContext.Current.CancellationToken);
            Assert.Equal(ModelCast.Format, result.Report["format"]!.GetValue<string>());
            Assert.NotEmpty(result.Report["rest_overrides"]!.AsArray());

            var head = Decode(res.Slots.SelectMany(s => s.Meshes).Single(m => m.Entry.Name == "sh2_npc_crane.msh").Gids[0]);
            var cast = CastFile.Load(result.CastPath);
            var model = cast.Roots()[0].ChildrenOfType<CastModel>().Single();
            var heads = model.ChildrenOfType<CastMesh>().Where(m => m.Property("bp_source_mesh")?.StringValue == "sh2_npc_crane.msh").ToList();
            Assert.NotEmpty(heads);
            double worst = 0;
            int checkedVertices = 0;
            foreach (var mesh in heads)
            {
                int entry = (int)mesh.Property("bp_entry")!.Integers[0];
                var v = head.GeometryEntries.Single(e => e.Index == entry).Vertices!;
                var ids = mesh.Property("bp_vertex_id")!.Integers;
                var pos = mesh.VertexPositionBuffer()!;
                for (int i = 0; i < ids.Length; i++)
                    for (int c = 0; c < 3; c++)
                        worst = Math.Max(worst, Math.Abs(pos[i * 3 + c] - v.Positions[ids[i] * 3 + c]));
                checkedVertices += ids.Length;
            }
            Assert.True(checkedVertices > 1000, $"{checkedVertices} vertices");
            Assert.True(worst < 1e-4, $"head vertex moved {worst * 1000:F3} mm");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
