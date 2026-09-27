using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Nightrunner.Core.Mesh;

namespace Nightrunner.Tests;

// Port of nightrunner-main/tests/test_mesh_variants.py ("variants" = skins). The corpus is the two small DLTB packs
// the prototype used plus its sample meshes, read from the install.
//
// not ported: test_sample_skin_bin_files (extracted sample tree), the model-free variant_names() helper (Skin.NameStr
// covers it).
public class MeshSkinsTests
{
    private static readonly string[] Named =
    [
        "wn_pistol_b_b", "bdp_ce_a_ornament_str_d", "dummybox_025m", "barrier", "man_basic_skeleton", "anim_hammer_a",
        "npc_b_man_pants_b_holster_bag_c", "dlc_ft_safe_zone_cable_e", "sh2_npc_aiden_beast", "sh2_npc_crane",
        "sh_npc_ft_crane_hair_a", "sh2_npc_ft_crane_beard_a", "dlc_ft_freak_banshee_clothes_matriarch",
        "alarm_siren_anm", "ui_bg_ph_ft", "ui_bg_the_beast", "gas_tank_pistol_anm",
    ];

    private static readonly HashSet<string> IdentityDefault =
    [
        "npc_b_man_pants_b_holster_bag_c", "dlc_ft_safe_zone_cable_e", "sh2_npc_aiden_beast", "sh2_npc_crane",
        "sh_npc_ft_crane_hair_a", "sh2_npc_ft_crane_beard_a", "dlc_ft_freak_banshee_clothes_matriarch",
        "anim_hammer_a", "bdp_ce_a_ornament_str_d", "alarm_siren_anm", "barrier", "ui_bg_ph_ft", "ui_bg_the_beast",
    ];

    private static List<(string Name, byte[] Raw, MeshModel Model)> Corpus()
    {
        var parts = MeshFixtures.Pack("dltb", "dlc_ft_prologue_pc.rpack").Concat(MeshFixtures.Pack("dltb", "menu_level_ft_pc.rpack")).ToList();
        foreach (var n in Named) parts.Add(MeshFixtures.Parts("dltb", n));
        return parts.Where(p => p.Skin is not null).Select(p => (p.Name.Trim(), p.Skin!, MeshDecoder.Decode(p))).ToList();
    }

    private static (byte[] Raw, MeshModel Model) One(string name)
    {
        var (m, p) = MeshFixtures.Load("dltb", name);
        return (p.Skin!, m);
    }

    [Fact]
    public void EveryMeshParsesToItsEnd()
    {
        foreach (var (name, raw, _) in Corpus())
        {
            var s = MeshSkins.Decode(raw);
            Assert.True(s.Error is null, name);
            Assert.True(s.Complete, $"{name}: {string.Join("; ", s.Notes)}");
            Assert.Equal(raw.Length, s.Size);
            Assert.Equal((s.Consumed + 15) & ~15, raw.Length);
            Assert.All(raw[s.Consumed..], b => Assert.Equal(0, b));
            Assert.Equal((int)BinaryPrimitives.ReadUInt32LittleEndian(raw), s.Skins.Count);
        }
    }

    [Fact]
    public void RoundTrip()
    {
        foreach (var (name, raw, _) in Corpus()) Assert.True(raw.AsSpan().SequenceEqual(MeshSkins.Decode(raw).Encode()), name);
    }

    [Fact]
    public void NamesMatchStringBlob()
    {
        foreach (var (name, raw, _) in Corpus())
        {
            var names = MeshSkins.Decode(raw).Skins.Select(s => s.NameStr).ToList();
            // every maximal NUL-terminated printable string in the part is a skin name (suffix sharing aside)
            var blob = Regex.Matches(System.Text.Encoding.Latin1.GetString(raw), @"(?<![\x20-\x7e])([\x20-\x7e]{3,})\x00")
                .Select(m => m.Groups[1].Value).ToHashSet();
            var expected = names.Where(n => n != "" && !names.Any(o => o != n && o.EndsWith(n))).ToHashSet();
            Assert.True(expected.SetEquals(blob), name);
        }
    }

    [Fact]
    public void DefaultPresentAndCoversUsedSlots()
    {
        foreach (var (name, raw, m) in Corpus())
        {
            var s = MeshSkins.Decode(raw);
            var used = m.GeometryEntries.SelectMany(g => g.Submeshes).Select(x => x.MaterialSlot).ToHashSet();
            if (used.Count == 0)
            {
                Assert.Empty(s.Skins);
                continue;
            }
            var def = Assert.Single(s.Skins, x => x.NameStr == "Default");
            Assert.Subset(def.Replace.Select(r => (int)r.Slot).ToHashSet(), used);
            if (IdentityDefault.Contains(name)) Assert.All(def.Replace, r => Assert.Equal(r.Slot, r.Material));
        }
    }

    [Fact]
    public void ReplaceMaterialsInsideFullTable()
    {
        foreach (var (name, raw, m) in Corpus())
        {
            var s = MeshSkins.Decode(raw);
            var table = m.FullMaterialTable();
            var used = new HashSet<int>();
            foreach (var k in s.Skins)
            {
                var slots = k.Replace.Select(r => (int)r.Slot).ToList();
                Assert.Equal(slots.Distinct().Order(), slots);
                foreach (var r in k.Replace)
                {
                    Assert.True(r.Material < table.Length, name);
                    Assert.True(r.Slot < m.Materials.Length, name);
                    used.Add(r.Material);
                }
            }
            // every skin-only material (count..capacity) is used by some skin
            Assert.Subset(used, Enumerable.Range(m.Materials.Length, table.Length - m.Materials.Length).ToHashSet());
        }
    }

    [Fact]
    public void WnPistol()
    {
        var (raw, m) = One("wn_pistol_b_b");
        var s = MeshSkins.Decode(raw);
        var table = m.FullMaterialTable();
        Assert.Equal(["silencer", "no_silencer", "Default", "loot", "olive_plastic", "sand_plastic", "", "Default_hl", "",
                      "olive_plastic_hl", "", "sand_plastic_hl"], s.Skins.Select(x => x.NameStr));
        var by = s.Skins.Where(x => x.NameStr != "").ToDictionary(x => x.NameStr);
        (string, (int, string)[])[] pairs =
        [
            ("silencer", [(3, "null.mat")]),
            ("no_silencer", []),
            ("Default", [(0, "wn_pistol_b_frame.mat"), (1, "shadow_caster.mat"), (2, "wn_pistol_b_slide.mat"), (3, "wn_gunsilencer_a_tpp.mat")]),
            ("loot", [(0, "loot.mat"), (1, "loot.mat"), (2, "loot.mat")]),
            ("olive_plastic", [(0, "wn_pistol_b_frame_olive_plastic.mat"), (1, "shadow_caster.mat"), (2, "wn_pistol_b_slide.mat")]),
            ("Default_hl", [(0, "wn_pistol_b_frame_hl.mat"), (1, "shadow_caster.mat"), (2, "wn_pistol_b_slide_hl.mat"), (3, "wn_gunsilencer_a_tpp_hl.mat")]),
        ];
        foreach (var (skin, want) in pairs)
            Assert.Equal(want, by[skin].Replace.Select(r => ((int)r.Slot, table[r.Material])));
        Assert.Equal([0, 1, 6, 8, 10], s.Skins.Where(x => x.FilterInEditor).Select(x => x.Index));
        Assert.Equal([0, 1], by["Default"].UseSkin.Select(u => u.Record));
        Assert.Equal([10, 1], by["sand_plastic_hl"].UseSkin.Select(u => u.Record));
        Assert.Equal([(3, "null_hl.mat")], s.Skins[6].Replace.Select(r => ((int)r.Slot, table[r.Material])));
        var silencerSlot = by["silencer"].Replace[0].Slot;
        Assert.Equal([(1, 0)], m.GeometryEntries.SelectMany(e => e.Submeshes.Where(x => x.MaterialSlot == silencerSlot).Select(x => (e.Index, x.Index))));
        Assert.Equal(3, s.Unreferenced.Count);
        Assert.Equal(raw.Length, s.Consumed);
    }

    [Fact]
    public void Ornament()
    {
        var (raw, _) = One("bdp_ce_a_ornament_str_d");
        var s = MeshSkins.Decode(raw);
        Assert.Equal(116, s.Skins.Count);
        Assert.Equal("Default", s.Skins[0].NameStr);
        Assert.Contains(s.Skins, x => x.NameStr == "bld_wall_rustic_planks_dark_a");
        Assert.Equal(116, s.Skins.Select(x => x.NameStr).Distinct().Count());
        Assert.All(s.Skins.Skip(1), x => Assert.Empty(x.Replace));
        Assert.Equal([(0, 13), (1, 13), (2, 13), (3, 13), (13, 13)], s.Skins[0].ReplaceSurface.Select(r => ((int)r.Old, (int)r.New)));
    }

    [Fact]
    public void DefaultOnlyAndSkinOnlyMaterial()
    {
        var (raw, m) = One("dummybox_025m");
        var s = MeshSkins.Decode(raw);
        var k = Assert.Single(s.Skins);
        Assert.Equal("Default", k.NameStr);
        var r = k.Replace[0];
        var table = m.FullMaterialTable();
        Assert.Equal((0, "DUMMYBOX.MAT", 2, "dummy.mat", true), (r.Slot, table[r.Slot], r.Material, table[r.Material], r.Material >= m.Materials.Length));
    }

    [Fact]
    public void BarrierDiffersOnlyInSurfaces()
    {
        var (raw, _) = One("barrier");
        var s = MeshSkins.Decode(raw);
        Assert.Equal(["Default", "no_climbing"], s.Skins.Select(x => x.NameStr));
        Assert.Equal(s.Skins[0].Replace[0].Material, s.Skins[1].Replace[0].Material);
        Assert.Equal([new SkinSurface(0x12, 0x12, 0x1000)], s.Skins[0].ReplaceSurface);
        Assert.Equal([new SkinSurface(0x12, 0x12, 0x1110)], s.Skins[1].ReplaceSurface);
    }

    [Fact]
    public void SkeletonIsEmpty()
    {
        var (raw, _) = One("man_basic_skeleton");
        var s = MeshSkins.Decode(raw);
        Assert.Equal((0, 8, 16, true), (s.Skins.Count, s.Consumed, s.Size, s.Complete));
    }

    [Fact]
    public void EditMaterialValue()
    {
        var (raw, m) = One("anim_hammer_a");
        var s = MeshSkins.Decode(raw);
        s.Skins[1].Replace[0] = s.Skins[1].Replace[0] with { Material = 0 };
        var outb = s.Encode();
        Assert.Single(Enumerable.Range(0, raw.Length), i => raw[i] != outb[i]);
        Assert.Equal("anim_hammer_a.mat", m.FullMaterialTable()[MeshSkins.Decode(outb).Skins[1].Replace[0].Material]);
        s.Skins[1].Replace.Add(new SkinReplace(1, 1));
        Assert.Throws<MeshFormatException>(() => s.Encode());
    }

    [Fact]
    public void GarbageReturnsError()
    {
        var rnd = new Random(7);
        List<byte[]> cases =
        [
            [], [1], [1, 0, 0, 0], U32s(0xFFFFFFFF, 8), [.. U32s(1, 8), .. Enumerable.Repeat((byte)0xFF, 32)],
            [.. U32s(1, 3), .. new byte[32]], [.. U32s(2, 8), .. new byte[16]],
        ];
        for (int i = 0; i < 300; i++)
        {
            var b = new byte[rnd.Next(300)];
            rnd.NextBytes(b);
            cases.Add(b);
        }
        foreach (var c in cases) MeshSkins.Decode(c);   // never throws
        foreach (var c in cases.Take(7)) Assert.NotNull(MeshSkins.Decode(c).Error);
        Assert.Throws<InvalidOperationException>(() => MeshSkins.Decode([]).Encode());
    }

    [Fact]
    public void TruncatedRealPart()
    {
        var (raw, _) = One("wn_pistol_b_b");
        foreach (var cut in new[] { 8, 40, 300, 600, 700 }) Assert.NotNull(MeshSkins.Decode(raw[..cut]).Error);
    }

    [Fact]
    public void SurfaceDefsParse()
    {
        var d = SurfaceDefs.Parse("""
            $SRF_UNKNOWN        ( i,   0 )
            $SRF_METAL          ( i,  17 )
            $SRFS_UNKNOWN        ( s,  "Unknown" )
            $SRFS_METAL          ( s,  "Metal" )
            $SRF_FLAG_SHOOT_THROUGH	        ( i,   8 )      // 0x0008
            $SRF_FLAG_BLOCK_CLIMBING        ( i, 256 )      // 0x0100
            $SRFS_FLAG_SHOOT_THROUGH            ( s,   "SHOOT_THROUGH")
            $SRFS_FLAG_BLOCK_CLIMBING           ( s,   "BLOCK_CLIMBING")
            """);
        Assert.Equal("Metal", d.Surfaces[17]);
        Assert.Equal("Unknown", d.Surfaces[0]);
        Assert.Equal("BLOCK_CLIMBING", d.Flags[0x100]);
        Assert.Equal(2, d.Flags.Count);
    }

    [Fact]
    public void SknText()
    {
        var (raw, m) = One("barrier");
        var defs = SurfaceDefs.Load(Installs.Require("dltb"));
        var text = SknWriter.Write(MeshSkins.Decode(raw), m, defs);
        Assert.Contains("Skin(\"no_climbing\")", text);
        Assert.Contains("ReplaceSurface(\"Stone\", \"\", \"BLOCK_SIDE_WALLRUN | BLOCK_CLIMBING | BLOCK_ATTACH\")", text);
        var bare = SknWriter.Write(MeshSkins.Decode(raw), m, null);
        Assert.Contains("ReplaceSurface(18, \"\", 0x1110)", bare);
    }

    private static byte[] U32s(uint a, uint b)
    {
        var r = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(r, a);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4), b);
        return r;
    }
}
