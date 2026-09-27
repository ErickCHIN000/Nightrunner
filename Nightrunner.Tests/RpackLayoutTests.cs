// Port of the layout half of nightrunner-main/tests/test_rp6l_writer.py (contiguous / bit-12 placement, alignment
// above 16, stock and explicit storage order, name blob order and name index, preserve, layout warnings), the
// layout policy of build.py / project.py, shipped-pack rebuild identity in every layout, and the writer's output for
// the synthetic parity cases, frozen (SyntheticCasesAreFrozen) from a build that matched the prototype's PackWriter.
//
// Not ported: validate() / ondemand_slices() (no validator in C#), PartSource file slices (PartSpec carries bytes).
//
// The live byte parity with the prototype (needs Python and the prototype checkout) is opt-in:
// OptIn/RpackParityTests.cs, tools/parity/README.md.
using Nightrunner.Core.Rpack;

namespace Nightrunner.Tests;

public partial class RpackLayoutTests
{
    private static readonly (byte, byte, byte, byte)[] NoKeys = [];

    // ---- stock storage order ------------------------------------------------------------------------------

    [Fact]
    public void StockOrderRule()
    {
        // census 2026-09-15: bit-3 (stream) storages first, then the rest; each run sorted by type id
        (byte, byte, byte, byte)[] mixed = [(0x20, 8, 0xB1, 0), (0x21, 8, 0xB1, 0), (0xF1, 8, 0x49, 0), (0x10, 8, 0xC9, 3), (0x12, 8, 0xD9, 0), (0x11, 8, 0xC9, 3), (0xF0, 8, 0x59, 0)];
        Assert.Equal([0x10, 0x11, 0x12, 0xF0, 0xF1, 0x20, 0x21], RpackWriter.StockStorageOrder(mixed).Select(k => (int)k.Item1));
        (byte, byte, byte, byte)[] anims = [(0x4A, 8, 0x40, 0), (0x44, 8, 0x20, 0), (0x45, 8, 0x29, 0), (0x42, 8, 0x40, 0), (0x43, 8, 0x40, 0), (0x49, 8, 0x40, 0), (0x48, 8, 0xC0, 8), (0x47, 8, 0xC0, 8)];
        Assert.Equal([0x45, 0x42, 0x43, 0x44, 0x47, 0x48, 0x49, 0x4A], RpackWriter.StockStorageOrder(anims).Select(k => (int)k.Item1));
        (byte, byte, byte, byte)[] engine = [(0xF1, 8, 0x40, 0), (0x62, 8, 0x80, 0), (0x10, 8, 0xC0, 3), (0x40, 8, 0x40, 0), (0x21, 8, 0xB1, 0), (0x11, 8, 0xC0, 3), (0xF0, 8, 0x50, 0), (0x20, 8, 0xB1, 0), (0x12, 8, 0xD0, 0), (0x61, 8, 0x80, 0)];
        Assert.Equal([0x10, 0x11, 0x12, 0x20, 0x21, 0x40, 0x61, 0x62, 0xF0, 0xF1], RpackWriter.StockStorageOrder(engine).Select(k => (int)k.Item1));
        (byte, byte, byte, byte)[] same = [(0x40, 8, 0x40, 1), (0x40, 8, 0x40, 0), (0x40, 6, 0x40, 0)];
        Assert.Equal([(0x40, 6, 0x40, 0), (0x40, 8, 0x40, 0), (0x40, 8, 0x40, 1)], RpackWriter.StockStorageOrder(same));
        Assert.Empty(RpackWriter.StockStorageOrder(NoKeys));
    }

    [Fact]
    public void ExplicitStorageOrderWithExtras()
    {
        (byte, byte, byte, byte)[] order = [(0x20, 8, 0xB1, 0), (0x21, 8, 0xB1, 0), (0x10, 8, 0xC9, 3), (0x12, 8, 0xD9, 0), (0x11, 8, 0xC9, 3), (0xF0, 8, 0x59, 0), (0xF1, 8, 0x49, 0)];
        var r = RpackWriter.Render([Synth.Texture("t"), Synth.Mesh("m"), Synth.Single("a", 0x5A, 32)],
            new RpackWriteOptions { Field08 = 0x1000, Layout = RpackLayout.Contiguous, StorageOrder = order });
        // 0x5A is not in the explicit order: appended (stock order among extras)
        Assert.Equal([0x20, 0x21, 0x10, 0x12, 0x11, 0xF0, 0xF1, 0x5A], r.Storages.Select(s => (int)s.Type));
    }

    // ---- contiguous placement -----------------------------------------------------------------------------

    // field08 bit 12: non-stream groups first (grouped), then stream resources back-to-back in logical order.
    //
    // Resources: tex(hdr 80, bmp 1000), m0(200,40,100,1000,300), m1(50,20,30,500,100); logical order tex, m0, m1.
    // storages: 10,11,12,F0,F1 (bit 3) then 20,21 → 7; parts 12; names tex/m0/m1 = 10 bytes.
    // table_end = 36 + 140 + 192 + 36 + 12 + 10 = 426 → payload start 432.
    //   phase 1  0x20 @ 432 (27): tex.hdr 432;  0x21 @ 512 (32): tex.bmp 512 → cursor 1512
    //   phase 2  m0 start align 16 → 1520: image 1520, skin 1728, fixups 1776, vertex 1888, index 2896 → 3196
    //            m1 → 3200: image 3200, skin 3264, fixups 3296, vertex 3328, index 3840 → 3940 → file 3952
    //   mesh storages base_units 0, offset_units = absolute >> 4; sizes 0x10 208+64, 0x11 112+32, 0x12 48+32,
    //   0xF0 1008+512, 0xF1 304+112.
    private static List<ResourceSpec> ContiguousResources() =>
    [
        Synth.Texture("tex", 80, 1000),
        Synth.Mesh("m0", [200, 40, 100, 1000, 300], seed: 1),
        Synth.Mesh("m1", [50, 20, 30, 500, 100], seed: 7),
    ];

    [Fact]
    public void ContiguousOffsets()
    {
        using var tmp = new TempDir();
        var data = Synth.BuildBytes(ContiguousResources(), field08: 0x1000);
        var pk = tmp.OpenPack(data);
        Assert.Equal(426, pk.TableEnd);
        Assert.Equal([0x10, 0x11, 0x12, 0xF0, 0xF1, 0x20, 0x21], pk.Storages.Select(s => (int)s.Type));
        Assert.Equal([432L, 512, 1520, 1728, 1776, 1888, 2896, 3200, 3264, 3296, 3328, 3840],
                     Enumerable.Range(0, 12).Select(pk.PartOffset));
        Assert.Equal([0u, 0, 0, 0, 0, 27, 32], pk.Storages.Select(s => s.BaseUnits));
        Assert.Equal([95u, 108, 111, 118, 181], pk.Physicals.Skip(2).Take(5).Select(p => p.OffsetUnits));
        Assert.Equal([272L, 144, 80, 1520, 416, 80, 1008], pk.Storages.Select(s => s.Size));
        Assert.Equal([2, 2, 2, 2, 2, 1, 1], pk.Storages.Select(s => (int)s.Count));
        Assert.Equal(3952, data.Length);
        Assert.Equal(Synth.MeshShape.Select(t => (int)t), Enumerable.Range(2, 5).Select(i => (int)pk.PartType(i)));
    }

    [Fact]
    public void ContiguousPhysicalWords()
    {
        using var tmp = new TempDir();
        var pk = tmp.OpenPack(ContiguousResources(), field08: 0x1000);
        for (int r = 0; r < pk.Count; r++)
        {
            for (int k = 0; k < pk.Logicals[r].PartCount; k++)
            {
                var p = pk.Physicals[pk.Logicals[r].FirstPart + k];
                Assert.Equal(r, p.Owner);
                Assert.Equal(pk.Logicals[r].Type == 0x10 ? RpackFormat.PhysBit8 : 0u, p.FlagBits);
                Assert.Equal(0u, p.Fc);
            }
        }
        Assert.Equal([1, 0x81, 0x81], pk.Logicals.Select(l => (int)l.Flags));
    }

    [Fact]
    public void MixedStreamResourceRefusedInContiguous()
    {
        var r = new ResourceSpec("bad"u8.ToArray(), 0x40, 0x21,
            [Synth.Part(0x44, Synth.Payload(16)), Synth.Part(0x45, Synth.Payload(16), flagBits: RpackFormat.PhysBit8)]);
        var ex = Assert.Throws<RpackBuildException>(() => Synth.BuildBytes([r], field08: 0x1000));
        Assert.Contains("mixes stream and non-stream", ex.Message);
        // the same resource is fine in the grouped layout
        using var tmp = new TempDir();
        var pk = tmp.OpenPack([r]);
        Assert.Equal([0x44, 0x45], Enumerable.Range(0, 2).Select(i => (int)pk.PartType(i)));
    }

    [Fact]
    public void AutoLayoutSelectionAndWarnings()
    {
        using var tmp = new TempDir();
        List<ResourceSpec> m = [Synth.Mesh("m")];
        Assert.Equal(RpackLayout.Contiguous, RpackWriter.Write(tmp.File("a.rpack"), m, 0x1000).Layout);
        Assert.Equal(RpackLayout.Grouped, RpackWriter.Write(tmp.File("b.rpack"), m, 0).Layout);
        var c = RpackWriter.Write(tmp.File("c.rpack"), m, new RpackWriteOptions { Layout = RpackLayout.Contiguous });
        Assert.Equal(["contiguous layout written into a pack without field08 bit 12"], c.Warnings);
        var d = RpackWriter.Write(tmp.File("d.rpack"), m, new RpackWriteOptions { Field08 = 0x1000, Layout = RpackLayout.Grouped });
        Assert.Single(d.Warnings);
        Assert.Contains("grouped layout written into a pack WITH field08 bit 12", d.Warnings[0]);
        Assert.Empty(RpackWriter.Write(tmp.File("e.rpack"), m, 0x1000).Warnings);
        Assert.Throws<RpackBuildException>(() => RpackWriter.Render(m, new RpackWriteOptions { Layout = (RpackLayout)99 }));
    }

    [Fact]
    public void TextureOnlyPackIdenticalInBothLayouts()
    {
        // what the texture-only project build relies on: without stream storages contiguous == grouped
        var res = Rp6lReaderTests.BasicResources();
        var g = RpackWriter.Render(res, new RpackWriteOptions { Field08 = 0x1000, Layout = RpackLayout.Grouped });
        var c = RpackWriter.Render(res, new RpackWriteOptions { Field08 = 0x1000, Layout = RpackLayout.Contiguous });
        Assert.Equal(g.PartOffsets, c.PartOffsets);
        Assert.Equal(g.Tables, c.Tables);
    }

    // ---- alignment above 16 (no shipped storage has align_raw != 8; synthetic align_raw 12 → A = 64) ------

    // Two meshes, all five storages at A=64 → 5 storages, 10 parts, names m0/m1 (6 bytes):
    // table_end = 36 + 100 + 160 + 24 + 8 + 6 = 334 → payload 336 → m0 start align 64 → 384:
    //   image 384 (200) → 584 → 640 skin (40) → 680 → 704 fixups (100) → 804 → 832 vertex (1000) → 1832 → 1856 index (300) → 2156
    //   m1 → 2176: image 2176 → 2240 skin → 2304 fixups → 2368 vertex → 2880 index (100) → 2980 → file 2992
    [Fact]
    public void ContiguousA64()
    {
        using var tmp = new TempDir();
        var data = Synth.BuildBytes([Synth.Mesh("m0", [200, 40, 100, 1000, 300], alignRaw: 12),
                                     Synth.Mesh("m1", [50, 20, 30, 500, 100], seed: 7, alignRaw: 12)], field08: 0x1000);
        var pk = tmp.OpenPack(data);
        Assert.Equal(334, pk.TableEnd);
        Assert.All(pk.Storages, s => Assert.Equal(64, s.Alignment));
        Assert.Equal([384L, 640, 704, 832, 1856, 2176, 2240, 2304, 2368, 2880], Enumerable.Range(0, 10).Select(pk.PartOffset));
        Assert.Equal([256L + 64, 128 + 64, 64 + 64, 1024 + 512, 320 + 128], pk.Storages.Select(s => s.Size));
        Assert.Equal(2992, data.Length);
    }

    // image/skin/fixups at A=16, vertex/index at A=64: resource start aligned to 64, each part to its own A.
    // One mesh: table_end = 36 + 100 + 80 + 12 + 4 + 3 = 235 → 240 → start align 64 → 256:
    // image 256 (200) → 456 → 464 skin (40) → 504 → 512 fixups (100) → 612 → 640 vertex (1000) → 1640 → 1664 index.
    [Fact]
    public void ContiguousMixedAlignmentWithinResource()
    {
        var m = Synth.Mesh("m0", [200, 40, 100, 1000, 300]);
        var parts = m.Parts.Select((p, k) => k >= 3 ? p with { AlignRaw = 12 } : p).ToArray();
        using var tmp = new TempDir();
        var pk = tmp.OpenPack([m with { Parts = parts }], field08: 0x1000);
        Assert.Equal(235, pk.TableEnd);
        Assert.Equal([256L, 464, 512, 640, 1664], Enumerable.Range(0, 5).Select(pk.PartOffset));
    }

    // ---- names --------------------------------------------------------------------------------------------

    [Fact]
    public void NameBlobOrderPermutation()
    {
        List<ResourceSpec> res = [Synth.Single("a", 0x55, 16), Synth.Single("b", 0x55, 16)];
        foreach (int[] bad in new[] { new[] { 0, 0 }, [0], [0, 1, 2], [1, 2] })
            Assert.Throws<RpackBuildException>(() => RpackWriter.Render(res, new RpackWriteOptions { NameBlobOrder = bad }));
        using var tmp = new TempDir();
        RpackWriter.Write(tmp.File("p.rpack"), res, new RpackWriteOptions { NameBlobOrder = [1, 0] });
        var pk = tmp.Track(RpackFile.Open(tmp.File("p.rpack")));
        Assert.Equal("b\0a\0"u8.ToArray(), pk.NameBlob);
        Assert.Equal(["a", "b"], Enumerable.Range(0, 2).Select(pk.Name));
        Assert.Equal([1, 0], RpackWriter.NameBlobOrder(pk)!);
    }

    [Fact]
    public void NameIndexKeptOnlyWhenPermutation()
    {
        using var tmp = new TempDir();
        var a = Synth.Single("a", 0x55, 16);
        var b = Synth.Single("b", 0x55, 16);
        var pk = tmp.OpenPack([a with { NameIndex = 1 }, b with { NameIndex = 0 }], name: "p.rpack");
        Assert.Equal([1u, 0], pk.Logicals.Select(l => l.NameIndex));
        Assert.Equal(["a", "b"], Enumerable.Range(0, 2).Select(pk.Name));
        pk = tmp.OpenPack([a with { NameIndex = 1 }, b with { NameIndex = 1 }], name: "q.rpack");   // not a permutation
        Assert.Equal([0u, 1], pk.Logicals.Select(l => l.NameIndex));
        pk = tmp.OpenPack([a with { NameIndex = 1 }, b], name: "r.rpack");                           // not all carry one
        Assert.Equal([0u, 1], pk.Logicals.Select(l => l.NameIndex));
    }

    // ---- preserve -----------------------------------------------------------------------------------------

    private static (TempDir Tmp, string Src, byte[] Data) PreserveSource()
    {
        var tmp = new TempDir();
        var src = tmp.File("src.rpack");
        RpackWriter.Write(src, [Synth.Texture("t", 80, 1000), Synth.Single("a", 0x5A, 700), Synth.Prefab()]);
        return (tmp, src, File.ReadAllBytes(src));
    }

    private static List<ResourceSpec> AllFromPack(RpackFile pk, Func<int, IReadOnlyDictionary<int, byte[]>?>? overrides = null) =>
        [.. Enumerable.Range(0, pk.Count).Select(i => ResourceSpec.FromPack(pk, i, overrides?.Invoke(i)))];

    [Fact]
    public void PreserveRoundTripAndFill()
    {
        var (tmp, src, data) = PreserveSource();
        using var _ = tmp;
        var pk = tmp.Track(RpackFile.Open(src));
        var rep = RpackWriter.Write(tmp.File("p.rpack"), AllFromPack(pk),
            RpackWriteOptions.FromPack(pk, RpackLayout.Preserve) with { FillPath = src, FinalSize = data.Length });
        Assert.Equal(RpackLayout.Preserve, rep.Layout);
        Assert.Equal(data, File.ReadAllBytes(tmp.File("p.rpack")));
        RpackWriter.Write(tmp.File("q.rpack"), AllFromPack(pk), RpackWriteOptions.FromPack(pk, RpackLayout.Preserve));
        Assert.Equal(data, File.ReadAllBytes(tmp.File("q.rpack")));   // gaps and tail are zero anyway
    }

    [Fact]
    public void PreserveNeedsTemplateAndOriginalPlacement()
    {
        var (tmp, src, _) = PreserveSource();
        using var __ = tmp;
        var pk = tmp.Track(RpackFile.Open(src));
        Assert.Throws<RpackBuildException>(() =>
            RpackWriter.Render([Synth.Single("a", 0x55, 16)], new RpackWriteOptions { Layout = RpackLayout.Preserve }));
        Assert.Throws<RpackBuildException>(() =>
            RpackWriter.Render([Synth.Single("a", 0x5A, 16)],
                               new RpackWriteOptions { Layout = RpackLayout.Preserve, TemplateStorages = pk.Storages }));
    }

    [Fact]
    public void PreserveChangedSizeRefusedByOverlap()
    {
        var (tmp, src, _) = PreserveSource();
        using var __ = tmp;
        var pk = tmp.Track(RpackFile.Open(src));
        var res = AllFromPack(pk, i => i == 0 ? new Dictionary<int, byte[]> { [0] = Synth.Payload(96) } : null);  // header 80 → 96
        var ex = Assert.Throws<RpackBuildException>(() =>
            RpackWriter.Write(tmp.File("p.rpack"), res, RpackWriteOptions.FromPack(pk, RpackLayout.Preserve)));
        Assert.Contains("overlaps", ex.Message);
        Assert.False(File.Exists(tmp.File("p.rpack.partial")));
    }

    [Fact]
    public void PreserveTablesGrownRefused()
    {
        var (tmp, src, _) = PreserveSource();
        using var __ = tmp;
        var pk = tmp.Track(RpackFile.Open(src));
        var res = AllFromPack(pk).Select(r => r with { Name = [.. r.Name, .. Enumerable.Repeat((byte)'_', 64)] }).ToList();
        var ex = Assert.Throws<RpackBuildException>(() => RpackWriter.Render(res, RpackWriteOptions.FromPack(pk, RpackLayout.Preserve)));
        Assert.Contains("overlap the (larger) tables", ex.Message);
    }

    [Fact]
    public void FromPackClonesStorageAttributes()
    {
        using var tmp = new TempDir();
        var pk = tmp.OpenPack([Synth.Mesh("m", ondemand: false)]);
        var spec = ResourceSpec.FromPack(pk, 0);
        Assert.Equal("m"u8.ToArray(), spec.Name);
        Assert.Equal(0x01, spec.Flags);
        Assert.Equal(Synth.MeshShape.Select(t => (t, (byte)8, Synth.Method0Mesh[t].Flags, Synth.Method0Mesh[t].Meta)),
                     spec.Parts.Select(p => p.StorageKey));
        Assert.All(spec.Parts, p => Assert.Equal(0u, p.FlagBits));
        Assert.Equal([0, 2, 1, 3, 4], spec.Parts.Select(p => p.StorageIndex!.Value));
        Assert.Equal(0, spec.NameIndex);
    }

    // ---- policy (build.py / project.py) -------------------------------------------------------------------

    [Fact]
    public void LayoutPolicy()
    {
        Assert.Equal(RpackLayout.Contiguous, RpackWriter.EffectiveLayout(RpackLayout.Auto, 0x1000));
        Assert.Equal(RpackLayout.Grouped, RpackWriter.EffectiveLayout(RpackLayout.Auto, 0));
        Assert.Equal(RpackLayout.Grouped, RpackWriter.EffectiveLayout(RpackLayout.Auto, 0x0FFF));
        Assert.Equal(RpackLayout.Preserve, RpackWriter.ChooseLayout(RpackLayout.Auto, true, true, true, 0x1000, 0x1000));
        Assert.Equal(RpackLayout.Auto, RpackWriter.ChooseLayout(RpackLayout.Auto, true, true, true, 0, 0x1000));
        Assert.Equal(RpackLayout.Auto, RpackWriter.ChooseLayout(RpackLayout.Auto, false, true, true, 0, 0));
        Assert.Equal(RpackLayout.Auto, RpackWriter.ChooseLayout(RpackLayout.Auto, true, false, true, 0, 0));
        Assert.Equal(RpackLayout.Grouped, RpackWriter.ChooseLayout(RpackLayout.Grouped, true, true, true, 0, 0));
        Assert.Equal(RpackLayout.Preserve, RpackWriter.ChooseLayout(RpackLayout.Preserve, true, true, true, 0, 0x1000));
        Assert.Throws<RpackBuildException>(() => RpackWriter.ChooseLayout(RpackLayout.Preserve, true, false, true, 0, 0));
        Assert.Throws<RpackBuildException>(() => RpackWriter.ChooseLayout(RpackLayout.Preserve, true, true, false, 0, 0));
    }

    [Fact]
    public void SelectionWarningsLikeSelect()
    {
        var anim = new ResourceSpec("anim"u8.ToArray(), 0x40, 0x21,
            [Synth.Part(0x44, Synth.Payload(16)), Synth.Part(0x45, Synth.Payload(16), flagBits: RpackFormat.PhysBit8)]);
        List<ResourceSpec> res =
        [
            Synth.Mesh("Engine_Mesh", ondemand: false),
            Synth.Mesh("special", flagBits: RpackFormat.PhysBit8 | RpackFormat.PhysSpecial),
            Synth.Mesh("ok"),
            anim,
            Synth.Texture("engine_mesh"),
            Synth.Mesh("ENGINE_MESH"),
        ];
        var w = RpackWriter.SelectionWarnings(res, 0x1000);
        Assert.Equal(5 + 5 + 1 + 1 + 0, w.Count);   // 5 method-0 parts, 5 special parts, the anim, one duplicate
        Assert.Equal("'Engine_Mesh': mesh part 0 is not on-demand compatible (method 0, flag_bits 0x0000) — a field08 bit-12 pack needs method 1 without 0x1000", w[0]);
        Assert.Contains("'special': mesh part 4 is not on-demand compatible (method 1, flag_bits 0x1100) — a field08 bit-12 pack needs method 1 without 0x1000", w);
        Assert.Contains("'anim': mixes stream (flags bit 3) and non-stream storages; the contiguous layout refuses it", w);
        Assert.Contains("duplicate (type 0x10, name 'ENGINE_MESH') at indices 0 and 5: the engine's name lookup returns the lower index", w);
        Assert.Equal(["duplicate (type 0x10, name 'ENGINE_MESH') at indices 0 and 5: the engine's name lookup returns the lower index"],
                     RpackWriter.SelectionWarnings(res, 0));
    }

    [Fact]
    public void ProjectField08Policy()
    {
        Assert.Equal(0x1000u, RpackWriter.ProjectField08(null, hasMesh: true, [0u]));
        Assert.Equal(0u, RpackWriter.ProjectField08(null, hasMesh: false, [0u, 0u]));
        Assert.Equal(0x1000u, RpackWriter.ProjectField08(null, hasMesh: false, [0u, 0x1000u]));
        Assert.Equal(0x1000u, RpackWriter.ProjectField08(null, hasMesh: false, []));
        Assert.Equal(0u, RpackWriter.ProjectField08(0u, hasMesh: true, [0x1000u]));
    }

    // ---- shipped packs (install-backed) -------------------------------------------------------------------

    /// <summary>The six small shipped packs, rebuilt from their own resources in every layout: byte identical.</summary>
    [Fact]
    public void ShippedSmallPacksEveryLayout()
    {
        using var tmp = new TempDir();
        foreach (var src in Rp6lReaderTests.SmallPackPaths())
        {
            var orig = File.ReadAllBytes(src);
            using var pk = RpackFile.Open(src);
            var res = AllFromPack(pk);
            var runs = new (string, RpackWriteOptions, RpackLayout)[]
            {
                ("auto", RpackWriteOptions.FromPack(pk), RpackLayout.Grouped),
                ("grouped", RpackWriteOptions.FromPack(pk, RpackLayout.Grouped), RpackLayout.Grouped),
                ("preserve", RpackWriteOptions.FromPack(pk, RpackLayout.Preserve), RpackLayout.Preserve),
                ("fill", RpackWriteOptions.FromPack(pk, RpackLayout.Preserve) with { FillPath = src, FinalSize = orig.Length }, RpackLayout.Preserve),
                ("scratch", new RpackWriteOptions { Field08 = pk.Header.Field08, Flags = pk.Header.Flags, NameBlobOrder = RpackWriter.NameBlobOrder(pk) }, RpackLayout.Grouped),
            };
            foreach (var (label, options, expect) in runs)
            {
                var dst = tmp.File($"{label}_{Path.GetFileName(src)}");
                var rep = RpackWriter.Write(dst, res, options);
                Assert.Equal(expect, rep.Layout);
                Assert.Empty(rep.Warnings);
                Assert.True(orig.AsSpan().SequenceEqual(File.ReadAllBytes(dst)), $"{Path.GetFileName(src)} {label}");
            }
        }
    }

    public static TheoryData<string, string> WholePacks => new()
    {
        { "dltb", "menu_level_ft_pc.rpack" },      // field08 0x1000: 3 meshes + 33 textures, 80 MB
        { "dltb", "dlc_ft_prologue_pc.rpack" },    // field08 0x1000: 5 meshes + 18 textures, 91 MB
        { "dl2", "menu_level_pc.rpack" },          // field08 0x1000, textures only, 2 MB
    };

    /// <summary>A whole bit-12 pack rebuilt from its resources with the auto (contiguous) layout: byte identical.</summary>
    [Theory]
    [MemberData(nameof(WholePacks))]
    public void ShippedWholePackRebuild(string game, string rel)
    {
        var gi = Installs.Require(game);
        var src = Path.Combine(gi.Assets!, rel);
        if (!File.Exists(src)) Assert.Skip($"{rel} missing");
        using var tmp = new TempDir();
        using var pk = RpackFile.Open(src);
        var dst = tmp.File("out.rpack");
        var rep = RpackWriter.Write(dst, AllFromPack(pk), RpackWriteOptions.FromPack(pk));
        Assert.Equal(RpackLayout.Contiguous, rep.Layout);
        var scratch = RpackWriter.Render(AllFromPack(pk),
            new RpackWriteOptions { Field08 = pk.Header.Field08, Flags = pk.Header.Flags, NameBlobOrder = RpackWriter.NameBlobOrder(pk) });
        Assert.Equal(Enumerable.Range(0, pk.Physicals.Length).Select(pk.PartOffset), scratch.PartOffsets);
        using (var a = File.OpenRead(src))
        using (var b = File.OpenRead(dst))
            Assert.True(SameStream(a, b), $"{rel}: rebuild differs");
    }

    private static bool SameStream(Stream a, Stream b)
    {
        if (a.Length != b.Length) return false;
        var x = new byte[1 << 20];
        var y = new byte[1 << 20];
        while (true)
        {
            int n = a.ReadAtLeast(x, x.Length, throwOnEndOfStream: false);
            int m = b.ReadAtLeast(y, y.Length, throwOnEndOfStream: false);
            if (n != m || !x.AsSpan(0, n).SequenceEqual(y.AsSpan(0, m))) return false;
            if (n == 0) return true;
        }
    }

    // ---- frozen writer output for the synthetic cases --------------------------------------------------------

    /// <summary>
    /// Every synthetic parity case written by the port: SHA-256 of the pack with its layout and warnings, or the refusal.
    /// Taken 2026-09-27 from a build whose PrototypeParitySynthetic run (the prototype's PackWriter on the same case
    /// descriptions, OptIn/RpackParityTests.cs) passed: the same bytes, layout and warnings, and every refusal refused by
    /// both. The refusal texts are the port's. A mismatch message lists the current values in this table's form.
    /// </summary>
    private static readonly Dictionary<string, string> FrozenSynthetic = new()
    {
        ["grouped_basic"] = "c6e5e913cfdcecb8b3ef6a407d4b8f0cabee36ff293e2175dab960b13005f442 grouped",
        ["grouped_basic_f1000"] = "77983cd7be02105fe724c60bb55a90dadf42ef660b529076f73f845a9b8f51d1 contiguous",
        ["contiguous_basic"] = "eac6114dea1ba17f627dfe2da2f2a77c3a6e136997a011f4882f146f5f7c1d6f contiguous",
        ["contiguous_basic_grouped"] = "d5bd8762483431c6ff6a059bb53b76dfe2b729485a2ad32ae6ba33252658ea4f grouped | grouped layout written into a pack WITH field08 bit 12 — mesh resources will not satisfy the one-read contract",
        ["contiguous_on_f0"] = "766735a810ceef2b7de0bc514ff65eac00c3110caabf7b882e7652b5491327c2 contiguous | contiguous layout written into a pack without field08 bit 12",
        ["grouped_meshes_f0"] = "9a82e0bb2fc950443033b07ce1f07298699e02d09ee2aa5940f3de8898eed820 grouped",
        ["contiguous_a64"] = "5c1e5cb0eae2e9c341738546191be1d242b14c0c248b2559589026cff3def2a7 contiguous",
        ["contiguous_mixed_align"] = "f06b2a9b909dcb71ca5821fb285c13945cdbbc40010d21f106a0acf535ed2f61 contiguous",
        ["contiguous_align1"] = "6209e05fd6bf208282082fdc87b97d6ec71073fe99605a8e4ec1f6dd3d6a3b38 contiguous",
        ["grouped_align1"] = "7b68605382dba065049b05f3bcea16e69f6975fc46f34bf5e15b6119267702af grouped",
        ["grouped_a64"] = "b529d5ea9220aeacede26ad583f46532561d4bdfdf00fb96e9d07fd324c9ac78 grouped",
        ["contiguous_a4096"] = "afe8114b0e69b848572fb476d376c7da4b85c7637c2727d1f04d1f8d3842764d contiguous",
        ["mixed_stream_contiguous"] = "refused: 'bad': mixes stream and non-stream storages",
        ["mixed_stream_grouped"] = "11e81c4fae8d84628bb8d4f2edf5dcf1ce23884a62f653d0fde4413ad5fa25a9 grouped",
        ["explicit_order"] = "57ca29ef74be64f0b4a07e298ac92b10a08a6d8481eeead46b353af352bc9838 contiguous",
        ["explicit_order_grouped"] = "97472bb4df92736b7128ea95315acc1e0a0906381a2567d428778558fbfb5774 grouped",
        ["name_blob_10"] = "eea29dc98095d57f9bcdf41d4853be9ef76e32d9a9a667bfc5ab1f25624b3626 grouped",
        ["name_blob_bad_00"] = "refused: name blob order must be a permutation of the resource indices",
        ["name_blob_bad_0"] = "refused: name blob order must be a permutation of the resource indices",
        ["name_blob_bad_012"] = "refused: name blob order must be a permutation of the resource indices",
        ["name_index_perm"] = "c334ec11cb3c9eb382ec1dccbd6b84aa4b97256a19871789bc11d1982066371d grouped",
        ["name_index_dup"] = "1b49ff269e065229fea75fe51a92a70d411f764246e37790bea806969aa9a763 grouped",
        ["name_index_partial"] = "1b49ff269e065229fea75fe51a92a70d411f764246e37790bea806969aa9a763 grouped",
        ["flag_bits_contiguous"] = "472e6f771c3e3b14f2a661ecaaa1647699b2538a86253bd059767f8dd292afe7 contiguous",
        ["flag_bits_grouped"] = "refused: part 2 overlaps previous payload (0x180 < 0x187)",
        ["interleaved_contiguous"] = "ae3e21b42e12d4e21ca3ac6d4b7370b0f8b8fc5ca180e7b81696fe5778b6e856 contiguous",
        ["interleaved_grouped"] = "f57cee19729236efb3bda7c48db16ad7c800dacfbfcb781879c99d2057b5fa4b grouped",
        ["mesh_before_texture"] = "755aa9a5b5ba39b7ae192fdf0b909b1afdd64511a0db986bf9553090cc9e0bf1 contiguous",
        ["header_flags_and_field08"] = "087511ef735e38e7d0be20545de5b18b04387a24ca6094001ea994e8675f256b contiguous",
        ["utf8_names"] = "1f4358722c3da90725f838fd21104b6382ba79755590f88482bfb04a246ca878 grouped",
        ["refuse_16_parts"] = "refused: 'x': 16 parts (runtime allows 1..15)",
        ["refuse_0_parts"] = "refused: 'x': 0 parts (runtime allows 1..15)",
        ["refuse_unknown_type"] = "refused: 'x': unknown part type 0x99",
        ["refuse_nul_name"] = "refused: resource name contains NUL",
        ["refuse_flag_bits"] = "refused: flag bits must fit in bits 8..15",
        ["refuse_flag_bits_low"] = "refused: flag bits must fit in bits 8..15",
        ["refuse_empty"] = "refused: nothing to write",
        ["refuse_257_storages"] = "refused: 257 storage groups exceed 256",
        ["storages_256"] = "3b3802609e7ddca336c603fbc09d2643b5d42945e8fd61e46866307c4758a483 grouped",
        ["refuse_unknown_layout"] = "refused: unknown layout 99",
        ["refuse_preserve_no_template"] = "refused: 'preserve' layout needs template storages from the source pack",
        ["refuse_preserve_no_placement"] = "refused: 'preserve' layout needs original storage index / offset units on every part",
        ["preserve_from_pack"] = "e1018273ec7c21d6664c0010ce7aaf474bb63a63aac2c291ffaad680f90488a8 preserve",
        ["preserve_fill"] = "e1018273ec7c21d6664c0010ce7aaf474bb63a63aac2c291ffaad680f90488a8 preserve",
        ["preserve_final_size_larger"] = "a9c923aef3ff8b1b5dad2b4a3d53fb8f3fea045315aa001165e8a200227fb079 preserve",
        ["preserve_final_size_smaller"] = "c8eec4adf568e6358e9c38aada37223191da3457b3a47cff893d51d9874ceb6c preserve",
        ["preserve_slices"] = "e1018273ec7c21d6664c0010ce7aaf474bb63a63aac2c291ffaad680f90488a8 preserve",
        ["refuse_preserve_overlap"] = "refused: part 1 overlaps previous payload (0x170 < 0x180)",
        ["preserve_last_part_grown"] = "375edfdc698035f8c1dd270adb00c63b4ce8f7f12eee71c210cc6b8b12b8d279 preserve",
        ["auto_from_pack"] = "e1018273ec7c21d6664c0010ce7aaf474bb63a63aac2c291ffaad680f90488a8 grouped",
        ["contiguous_from_pack"] = "e1018273ec7c21d6664c0010ce7aaf474bb63a63aac2c291ffaad680f90488a8 contiguous | contiguous layout written into a pack without field08 bit 12",
        ["grouped_fill"] = "7ca41a239c11ed8f25f2c72fbcbc1372916e5a0225db4bd61658794cc9049a5d grouped",
    };

    [Fact]
    public void SyntheticCasesAreFrozen()
    {
        using var tmp = new TempDir();
        var cases = Parity.SyntheticCases(tmp);
        var packs = new Dictionary<string, RpackFile>();
        var actual = new List<(string Name, string Value)>();
        try
        {
            foreach (var c in cases)
            {
                var dst = tmp.File(c.Name + ".rpack");
                var r = Parity.Build(c, dst, packs);
                string v = r.Ok
                    ? $"{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(dst)))} {r.Layout}{string.Concat(r.Warnings.Select(w => " | " + w))}"
                    : "refused: " + r.Error;
                actual.Add((c.Name, v.Replace(tmp.Path, "<tmp>")));
            }
        }
        finally
        {
            foreach (var pk in packs.Values) pk.Dispose();
        }
        bool same = actual.Count == FrozenSynthetic.Count && actual.All(a => FrozenSynthetic.TryGetValue(a.Name, out var w) && w == a.Value);
        Assert.True(same, "now:\n" + string.Join("\n", actual.Select(a =>
            $"        [\"{a.Name}\"] = \"{a.Value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\",")));
    }

    /// <summary>A pack described as data, so the prototype and the port build it from the same description.</summary>
    internal static partial class Parity
    {
        public sealed record PartCase(byte Type, byte AlignRaw, byte StorageFlags, byte StorageMetadata, uint FlagBits, uint Fc,
                                      int? StorageIndex = null, uint? OffsetUnits = null, int Size = 0, int Seed = 0,
                                      string? File = null, long Offset = 0);

        public sealed record ResCase(string NameHex, byte Type, byte Flags, List<PartCase> Parts, int? NameIndex = null,
                                     string? FromPack = null, int Index = 0);

        public sealed record PackCase(string Name, List<ResCase> Resources, uint Field08 = 0, uint Flags = 1, string Layout = "auto",
                                      List<int[]>? StorageOrder = null, string? TemplateFrom = null, int[]? NameBlobOrder = null,
                                      string? OptionsFromPack = null, string? Fill = null, long? FinalSize = null);

        // ---- case builders (mirror Synth: same payloads, same stock storage words) ----

        public static string Hex(string s) => Convert.ToHexString(Synth.Bytes(s)).ToLowerInvariant();

        public static PartCase P(byte type, int size, int seed = 0, byte? alignRaw = null, uint flagBits = 0, uint fc = 0,
                                 IReadOnlyDictionary<byte, (byte Align, byte Flags, byte Meta)>? table = null,
                                 byte? flags = null, byte? meta = null)
        {
            var (a, f, m) = (table ?? Synth.Stock)[type];
            return new PartCase(type, alignRaw ?? a, flags ?? f, meta ?? m, flagBits, fc, Size: size, Seed: seed);
        }

        public static ResCase Mesh(string name, int[]? sizes = null, int seed = 1, bool ondemand = true, byte? alignRaw = null,
                                   uint? flagBits = null)
        {
            sizes ??= [200, 40, 100, 1000, 300];
            var table = ondemand ? Synth.Stock : Synth.Method0Mesh;
            uint fb = flagBits ?? (ondemand ? RpackFormat.PhysBit8 : 0u);
            return new ResCase(Hex(name), 0x10, ondemand ? RpackFormat.LogicalFlagsOnDemandMesh : RpackFormat.LogicalFlagsDefault,
                [.. Synth.MeshShape.Select((t, k) => P(t, sizes[k], seed + k, alignRaw, fb, table: table))]);
        }

        public static ResCase Texture(string name, int header = 80, int bitmap = 1000, int seed = 9) =>
            new(Hex(name), 0x20, 1, [P(0x20, header, seed), P(0x21, bitmap, seed + 1)]);

        public static ResCase Single(string name, byte type, int size, int seed = 5, byte? alignRaw = null) =>
            new(Hex(name), type, 1, [P(type, size, seed, alignRaw)]);

        public static ResCase Prefab(string name = "Prefabs") => new(Hex(name), 0x61, 1, [P(0x61, 600, 3), P(0x62, 220, 4)]);

        /// <summary>Resource <paramref name="index"/> of a pack as file slices with the storage words the C# reader reports.</summary>
        public static ResCase Slices(RpackFile pk, int index)
        {
            var lg = pk.Logicals[index];
            var parts = new List<PartCase>();
            for (int k = 0; k < lg.PartCount; k++)
            {
                int pi = (int)lg.FirstPart + k;
                var ph = pk.Physicals[pi];
                var st = pk.Storages[ph.StorageIndex];
                if (pk.PartUnreadableReason(pi) is { } why) throw new InvalidOperationException($"{pk.Path}:{index}: {why}");
                parts.Add(new PartCase(st.Type, st.AlignRaw, st.Flags, st.Metadata, ph.FlagBits, ph.Fc,
                                       ph.StorageIndex, ph.OffsetUnits, (int)ph.Size, File: pk.Path, Offset: pk.PartOffset(pi)));
            }
            return new ResCase(Convert.ToHexString(pk.NameBytes(index)).ToLowerInvariant(), lg.Type, lg.Flags, parts);
        }

        public static ResCase Whole(string pack, int index) => new("", 0, 0, [], FromPack: pack, Index: index);

        // ---- materialise on the C# side ----

        private static ResourceSpec Spec(ResCase r, Dictionary<string, RpackFile> packs)
        {
            if (r.FromPack is { } fp) return ResourceSpec.FromPack(Open(packs, fp), r.Index);
            var parts = r.Parts.Select(p =>
                new PartSpec(p.Type, p.File is { } f ? Slice(f, p.Offset, p.Size) : Synth.Payload(p.Size, p.Seed),
                             p.AlignRaw, p.StorageFlags, p.StorageMetadata, p.FlagBits, p.Fc)
                {
                    StorageIndex = p.StorageIndex,
                    OffsetUnits = p.OffsetUnits,
                }).ToList();
            return new ResourceSpec(Convert.FromHexString(r.NameHex), r.Type, r.Flags, parts) { NameIndex = r.NameIndex };
        }

        private static RpackFile Open(Dictionary<string, RpackFile> packs, string path)
        {
            if (!packs.TryGetValue(path, out var pk)) packs[path] = pk = RpackFile.Open(path);
            return pk;
        }

        private static byte[] Slice(string path, long offset, int size)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Position = offset;
            var b = new byte[size];
            fs.ReadExactly(b);
            return b;
        }

        private static RpackLayout ParseLayout(string s) => s switch
        {
            "auto" => RpackLayout.Auto,
            "grouped" => RpackLayout.Grouped,
            "contiguous" => RpackLayout.Contiguous,
            "preserve" => RpackLayout.Preserve,
            _ => (RpackLayout)99,
        };

        private static string LayoutName(RpackLayout l) => l.ToString().ToLowerInvariant();

        /// <summary>(ok, layout, warnings, error) of the C# writer for one case.</summary>
        internal static (bool Ok, string? Layout, string[] Warnings, string? Error) Build(PackCase c, string dst,
                                                                                       Dictionary<string, RpackFile> packs)
        {
            try
            {
                var res = c.Resources.Select(r => Spec(r, packs)).ToList();
                var options = c.OptionsFromPack is { } ofp
                    ? RpackWriteOptions.FromPack(Open(packs, ofp), ParseLayout(c.Layout))
                    : new RpackWriteOptions
                    {
                        Field08 = c.Field08,
                        Flags = c.Flags,
                        Layout = ParseLayout(c.Layout),
                        StorageOrder = c.StorageOrder?.Select(k => ((byte)k[0], (byte)k[1], (byte)k[2], (byte)k[3])).ToList(),
                        TemplateStorages = c.TemplateFrom is { } tf ? Open(packs, tf).Storages : null,
                        NameBlobOrder = c.NameBlobOrder,
                    };
                options = options with { FillPath = c.Fill, FinalSize = c.FinalSize };
                var rep = RpackWriter.Write(dst, res, options);
                return (true, LayoutName(rep.Layout), [.. rep.Warnings], null);
            }
            catch (RpackBuildException e)
            {
                return (false, null, [], e.Message);
            }
        }

        // ---- the cases ----

        public static List<PackCase> SyntheticCases(TempDir tmp)
        {
            List<ResCase> basic = [Texture("Tex_A", 80, 1000, 1), Texture("tex_b", 80, 500, 2), Single("dlc_area_x", 0x5A, 700), Prefab()];
            List<ResCase> contig = [Texture("tex", 80, 1000), Mesh("m0", [200, 40, 100, 1000, 300], 1), Mesh("m1", [50, 20, 30, 500, 100], 7)];
            var mixedStream = new ResCase(Hex("bad"), 0x40, 0x21, [P(0x44, 16), P(0x45, 16, flagBits: RpackFormat.PhysBit8)]);
            var alignMixed = Mesh("m0");
            alignMixed.Parts[3] = alignMixed.Parts[3] with { AlignRaw = 12 };
            alignMixed.Parts[4] = alignMixed.Parts[4] with { AlignRaw = 12 };
            var flagged = new ResCase(Hex("flagged"), 0x10, 0x81,
            [
                P(0x10, 33, 1, flagBits: 0x0100, fc: 0xDEADBEEF), P(0x12, 7, 2, flagBits: 0x1100),
                P(0x11, 0, 3, flagBits: 0x0E00, fc: 1), P(0xF0, 129, 4, flagBits: 0xFF00), P(0xF1, 17, 5, flagBits: 0x2100),
            ]);
            var interleaved = new List<ResCase>();
            for (int i = 0; i < 24; i++)
                interleaved.Add((i % 4) switch
                {
                    0 => Mesh($"Mesh_{i}", [17 + i, 3, 40 + i * 3, 1000 - i * 7, 90 + i], seed: i),
                    1 => Texture($"tex_{i}", 80 + (i % 3) * 16, 100 + i * 37, seed: i),
                    2 => Mesh($"engine_{i}", [31, 9, 12 + i, 500 + i, 64], seed: i, ondemand: false),
                    _ => Single($"area_{i}", 0x5A, 10 + i * 11, seed: i),
                });
            (byte, byte, byte, byte)[] dleOrder = [(0x20, 8, 0xB1, 0), (0x21, 8, 0xB1, 0), (0x10, 8, 0xC9, 3), (0x12, 8, 0xD9, 0), (0x11, 8, 0xC9, 3), (0xF0, 8, 0x59, 0), (0xF1, 8, 0x49, 0)];
            var order = dleOrder.Select(k => new[] { (int)k.Item1, k.Item2, k.Item3, k.Item4 }).ToList();
            List<ResCase> tooMany = [new(Hex("x"), 0x40, 1, [.. Enumerable.Range(0, 16).Select(_ => P(0x40, 8))])];
            List<ResCase> storages257 = [.. Enumerable.Range(0, 257).Select(m =>
                new ResCase(Hex($"r{m}"), 0x40, 1, [new PartCase(0x40, 8, m < 256 ? (byte)0x40 : (byte)0x41, (byte)(m < 256 ? m : 0), 0, 0, Size: 1)]))];

            // a source pack for the preserve cases (written by the port, read by both)
            var src = tmp.File("preserve_src.rpack");
            RpackWriter.Write(src, [Synth.Texture("t", 80, 1000), Synth.Single("a", 0x5A, 700), Synth.Prefab()]);
            long srcSize = new FileInfo(src).Length;
            var whole = Enumerable.Range(0, 3).Select(i => Whole(src, i)).ToList();
            ResCase ResizedLast()
            {
                using var pk = RpackFile.Open(src);
                int last = Enumerable.Range(0, pk.Physicals.Length).OrderBy(pk.PartOffset).Last();
                var r = Slices(pk, pk.Physicals[last].Owner);
                int k = last - (int)pk.Logicals[pk.Physicals[last].Owner].FirstPart;
                r.Parts[k] = r.Parts[k] with { File = null, Size = r.Parts[k].Size + 100, Seed = 3 };
                return r;
            }
            List<ResCase> sliced;
            using (var pk = RpackFile.Open(src))
                sliced = [.. Enumerable.Range(0, 3).Select(i => Slices(pk, i))];
            var resizedHeader = sliced.ToList();
            resizedHeader[0] = resizedHeader[0] with { Parts = [resizedHeader[0].Parts[0] with { File = null, Size = 96 }, resizedHeader[0].Parts[1]] };
            var resizedLast = sliced.ToList();
            var rl = ResizedLast();
            resizedLast[resizedLast.FindIndex(r => r.NameHex == rl.NameHex)] = rl;

            return
            [
                new("grouped_basic", basic),
                new("grouped_basic_f1000", basic, Field08: 0x1000),
                new("contiguous_basic", contig, Field08: 0x1000),
                new("contiguous_basic_grouped", contig, Field08: 0x1000, Layout: "grouped"),
                new("contiguous_on_f0", contig, Layout: "contiguous"),
                new("grouped_meshes_f0", contig),
                new("contiguous_a64", [Mesh("m0", alignRaw: 12), Mesh("m1", [50, 20, 30, 500, 100], 7, alignRaw: 12)], Field08: 0x1000),
                new("contiguous_mixed_align", [alignMixed], Field08: 0x1000),
                new("contiguous_align1", [Mesh("m", alignRaw: 0)], Field08: 0x1000),
                new("grouped_align1", [Mesh("m", alignRaw: 0), Single("a", 0x5A, 33, alignRaw: 0)]),
                new("grouped_a64", [Single("a", 0x5A, 100, 1, 12), Single("b", 0x5A, 100, 2, 12)]),
                new("contiguous_a4096", [Mesh("m", alignRaw: 24), Texture("t")], Field08: 0x1000),
                new("mixed_stream_contiguous", [mixedStream], Field08: 0x1000),
                new("mixed_stream_grouped", [mixedStream]),
                new("explicit_order", [Texture("t"), Mesh("m"), Single("a", 0x5A, 32)], Field08: 0x1000, Layout: "contiguous", StorageOrder: order),
                new("explicit_order_grouped", [Texture("t"), Mesh("m"), Single("a", 0x5A, 32)], StorageOrder: order),
                new("name_blob_10", [Single("a", 0x55, 16), Single("b", 0x55, 16)], NameBlobOrder: [1, 0]),
                new("name_blob_bad_00", [Single("a", 0x55, 16), Single("b", 0x55, 16)], NameBlobOrder: [0, 0]),
                new("name_blob_bad_0", [Single("a", 0x55, 16), Single("b", 0x55, 16)], NameBlobOrder: [0]),
                new("name_blob_bad_012", [Single("a", 0x55, 16), Single("b", 0x55, 16)], NameBlobOrder: [0, 1, 2]),
                new("name_index_perm", [Single("a", 0x55, 16) with { NameIndex = 1 }, Single("b", 0x55, 16) with { NameIndex = 0 }]),
                new("name_index_dup", [Single("a", 0x55, 16) with { NameIndex = 1 }, Single("b", 0x55, 16) with { NameIndex = 1 }]),
                new("name_index_partial", [Single("a", 0x55, 16) with { NameIndex = 1 }, Single("b", 0x55, 16)]),
                new("flag_bits_contiguous", [flagged, Texture("t")], Field08: 0x1000),
                new("flag_bits_grouped", [flagged, Texture("t")]),
                new("interleaved_contiguous", interleaved, Field08: 0x1000),
                new("interleaved_grouped", interleaved),
                new("mesh_before_texture", [Mesh("m"), Texture("t"), Mesh("n", seed: 4)], Field08: 0x1000),
                new("header_flags_and_field08", [Texture("t")], Field08: 0xFFFFFFFF, Flags: 0x12345678),
                new("utf8_names", [Single("tést/猫", 0x55, 16), Single("ABC", 0x55, 16)]),
                new("refuse_16_parts", tooMany),
                new("refuse_0_parts", [new(Hex("x"), 0x40, 1, [])]),
                new("refuse_unknown_type", [new(Hex("x"), 0x40, 1, [new PartCase(0x99, 8, 0x21, 0, 0, 0, Size: 1)])]),
                new("refuse_nul_name", [new(Hex("bad\0name"), 0x55, 1, [P(0x55, 16)])]),
                new("refuse_flag_bits", [new(Hex("x"), 0x55, 1, [P(0x55, 1) with { FlagBits = 0x10000 }])]),
                new("refuse_flag_bits_low", [new(Hex("x"), 0x55, 1, [P(0x55, 1) with { FlagBits = 0x00FF }])]),
                new("refuse_empty", []),
                new("refuse_257_storages", storages257),
                new("storages_256", storages257[..256]),
                new("refuse_unknown_layout", [Single("a", 0x55, 16)], Layout: "bogus"),
                new("refuse_preserve_no_template", [Single("a", 0x55, 16)], Layout: "preserve"),
                new("refuse_preserve_no_placement", [Single("a", 0x5A, 16)], Layout: "preserve", TemplateFrom: src),
                new("preserve_from_pack", whole, Layout: "preserve", OptionsFromPack: src),
                new("preserve_fill", whole, Layout: "preserve", OptionsFromPack: src, Fill: src, FinalSize: srcSize),
                new("preserve_final_size_larger", whole, Layout: "preserve", OptionsFromPack: src, FinalSize: srcSize + 100),
                new("preserve_final_size_smaller", whole, Layout: "preserve", OptionsFromPack: src, FinalSize: 64),
                new("preserve_slices", sliced, Layout: "preserve", TemplateFrom: src),
                new("refuse_preserve_overlap", resizedHeader, Layout: "preserve", TemplateFrom: src),
                new("preserve_last_part_grown", resizedLast, Layout: "preserve", TemplateFrom: src),
                new("auto_from_pack", whole, OptionsFromPack: src),
                new("contiguous_from_pack", whole, Layout: "contiguous", OptionsFromPack: src),
                new("grouped_fill", basic, Fill: src),
            ];
        }

    }
}
