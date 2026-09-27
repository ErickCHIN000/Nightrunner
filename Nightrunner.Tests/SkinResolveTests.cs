using Nightrunner.Core.Mesh;
using Nightrunner.Core.Rpack;
using Nightrunner.Core.Sdb;

namespace Nightrunner.Tests;

/// <summary>
/// Skin → material → SDB → texture: what the Viewport draws per skin. Order (UseSkin targets first, then the skin's
/// own Replace pairs) is inferred from the DL2 DevTools sources (C).
/// </summary>
public class SkinResolveTests
{
    [Fact]
    public void UseSkinTargetsApplyFirst()
    {
        var (m, p) = MeshFixtures.Load("dl2", "dummy_box");
        var s = MeshSkins.Decode(p.Skin!);
        var table = m.FullMaterialTable();
        int red = s.Skins.FindIndex(k => k.NameStr == "Red"), def = s.Skins.FindIndex(k => k.NameStr == "common_default");
        Assert.Contains(def, s.Skins[red].UseSkin.Select(u => u.Record));
        Assert.Empty(s.Skins[red].Replace);                       // Red replaces nothing itself ...
        Assert.Equal("dummy.mat", table[s.MaterialsFor(red, m.Materials.Length)[0]]);   // ... common_default supplies it
        int nullWood = s.Skins.FindIndex(k => k.NameStr == "NullWood");
        Assert.Equal("null.mat", table[s.MaterialsFor(nullWood, m.Materials.Length)[0]]);
    }

    [Fact]
    public void OwnReplaceWinsOverUseSkin()
    {
        var own = new SkinReplace(0, 2);
        var skins = MeshSkins.Decode(SkinPart([[new SkinReplace(0, 1)], [own]], [[], [0]]));
        Assert.Null(skins.Error);
        Assert.Equal([2, 1], skins.MaterialsFor(1, 2));
        Assert.Equal([1, 1], skins.MaterialsFor(0, 2));
    }

    /// <summary>
    /// Materials a shipped skin names that neither DLTB database contains (checked dx11 and dx12, 2026-09-23): the
    /// sedan's CLEAN skin and a survivor-sense debug variant. The Viewport draws those slots untextured and says so.
    /// </summary>
    private static readonly Dictionary<string, string[]> NotInSdb = new()
    {
        ["veh_sedan_a"] = ["veh_sedan_body_a_c.mat", "veh_sedan_glass_a_c.mat", "sur_sens$npp.mat"],
        ["int_ce_a_elevator_2x3_body_a"] = [],
    };

    [Theory]
    [InlineData("veh_sedan_a", 26)]
    [InlineData("int_ce_a_elevator_2x3_body_a", 230)]
    public async Task EverySkinResolvesThroughTheSdb(string mesh, int skinCount)
    {
        var install = Installs.Require("dltb");
        var (m, p) = MeshFixtures.Load("dltb", mesh);
        var skins = MeshSkins.Decode(p.Skin!);
        Assert.Equal(skinCount, skins.Skins.Count);
        using var sdb = SdbFile.Open(install.Sdb("dx11"));
        using var catalog = new RpackCatalog();
        await catalog.LoadAsync(install.Rpacks(), install.Assets, TestContext.Current.CancellationToken);
        var table = m.FullMaterialTable();
        var used = m.GeometryEntries.SelectMany(e => e.Submeshes).Select(s => s.MaterialSlot).Distinct().ToList();
        var problems = new List<string>();
        var missing = new List<string>();
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in skins.Skins)
        {
            var map = skins.MaterialsFor(k.Index, m.Materials.Length);
            foreach (int slot in used)
            {
                string name = table[map[slot]];
                if (!distinct.Add(name)) continue;
                var hits = sdb.FindMaterial(name);
                if (hits.Count == 0) { missing.Add(name); continue; }
                if (hits.Count != 1) { problems.Add($"{k.NameStr}: {name} has {hits.Count} SDB matches"); continue; }
                var mat = sdb.Material(hits[0]);
                foreach (var tex in mat.Textures)
                    if (catalog.Lookup(tex, 0x20).Length == 0) problems.Add($"{k.NameStr}: {name}: {tex} not in any pack");
            }
        }
        Assert.True(problems.Count == 0, $"{distinct.Count} materials:\n" + string.Join("\n", problems));
        Assert.Equal(NotInSdb[mesh].Order(), missing.Order());
    }

    /// <summary>A minimal part: one record per entry, each with its own pairs and UseSkin refs.</summary>
    private static byte[] SkinPart(SkinReplace[][] pairs, int[][] uses)
    {
        int n = pairs.Length;
        var payload = new List<byte>();
        var records = new byte[n * 32];
        int payloadStart = 8 + n * 32;
        var color = new byte[] { 0, 0, 0, 0xFF, 0, 0, 0, 0xFF };
        int colorAt = payloadStart;
        payload.AddRange(color);
        for (int k = 0; k < n; k++)
        {
            int rec = 8 + k * 32;
            int pairsAt = payloadStart + payload.Count;
            foreach (var r in pairs[k]) payload.AddRange([.. BitConverter.GetBytes(r.Slot), .. BitConverter.GetBytes(r.Material)]);
            int usesAt = payloadStart + payload.Count;
            foreach (var u in uses[k]) payload.AddRange([.. BitConverter.GetBytes((uint)u | 0x10000000), .. BitConverter.GetBytes(0x81000000u)]);
            BitConverter.GetBytes(0x10000000u).CopyTo(records, k * 32 + 4);
            BitConverter.GetBytes((uint)(colorAt - rec)).CopyTo(records, k * 32 + 8);
            if (pairs[k].Length > 0) BitConverter.GetBytes((uint)(pairsAt - rec)).CopyTo(records, k * 32 + 0x10);
            if (uses[k].Length > 0) BitConverter.GetBytes((uint)(usesAt - rec)).CopyTo(records, k * 32 + 0x18);
            records[k * 32 + 0x1C] = (byte)pairs[k].Length;
            records[k * 32 + 0x1F] = (byte)uses[k].Length;
        }
        var all = new List<byte>();
        all.AddRange(BitConverter.GetBytes((uint)n));
        all.AddRange(BitConverter.GetBytes(8u));
        all.AddRange(records);
        all.AddRange(payload);
        while (all.Count % 16 != 0) all.Add(0);
        return [.. all];
    }
}
