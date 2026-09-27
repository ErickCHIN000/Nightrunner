// Port of nightrunner-main/tests/test_rp6l_writer.py: shipped-pack rebuild identity, grouped placement computed by
// hand, storage.size bookkeeping, u16 wraps and the refusals. The layouts (contiguous, preserve), storage order,
// name blob order / name index and parity with the prototype are in RpackLayoutTests.
//
// Not ported:
//   TestShippedIdentity.test_render_table_identity — covered by the file-level identity tests.
//   TestGroupedPlacement.test_validates, the validate() halves of TestWrapsAndLimits and
//     TestAlignmentAbove16.test_alignment_below_16_* — no validate() / ondemand_slices() in C#.
//   TestPartSource (all) — C# PartSpec carries a byte[]; no file-slice sources.
using Nightrunner.Core.Rpack;

namespace Nightrunner.Tests;

public class Rp6lWriterTests
{
    private static int? FirstDiff(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
            if (a[i] != b[i]) return i;
        return a.Length == b.Length ? null : n;
    }

    // ---- shipped identity (install-backed) ----------------------------------------------------------------

    /// <summary>
    /// extract → build byte identity for the small shipped packs whose name blob is in
    /// resource order. The two
    /// packs with a permuted blob (reg1, cb_region_0) are reproduced only when the recorded blob order is passed
    /// (RpackLayoutTests): without it only the name table differs (test_name_blob_order_not_derived).
    /// </summary>
    [Fact]
    public void ShippedRebuildIdentity()
    {
        var paths = Rp6lReaderTests.SmallPackPaths();
        using var tmp = new TempDir();
        foreach (var src in paths)
        {
            string name = Path.GetFileName(src);
            var orig = File.ReadAllBytes(src);
            byte[] rebuilt;
            int[]? blobOrder;
            long nameOffsetOffset, tableEnd;
            List<string> names;
            using (var pk = RpackFile.Open(src))
            {
                var dst = tmp.File(name);
                var rep = RpackWriter.Write(dst, Synth.SpecsFromPack(pk), pk.Header.Field08, pk.Header.Flags);
                rebuilt = File.ReadAllBytes(dst);
                Assert.Equal(rebuilt.Length, rep.Size);
                blobOrder = Synth.NameBlobOrder(pk);
                nameOffsetOffset = pk.NameOffsetOffset;
                tableEnd = pk.TableEnd;
                names = [.. Enumerable.Range(0, pk.Count).Select(pk.Name)];
            }

            Assert.Equal(orig.Length, rebuilt.Length);
            if (blobOrder!.SequenceEqual(Enumerable.Range(0, blobOrder!.Length)))
            {
                Assert.True(FirstDiff(orig, rebuilt) is null, $"{name}: first difference at {FirstDiff(orig, rebuilt)}");
                continue;
            }
            // permuted blob: everything but the name table is identical, and the rebuilt blob is in resource order
            Assert.Equal(orig[..(int)nameOffsetOffset], rebuilt[..(int)nameOffsetOffset]);
            Assert.Equal(orig[(int)tableEnd..], rebuilt[(int)tableEnd..]);
            Assert.NotEqual(orig[(int)nameOffsetOffset..(int)tableEnd], rebuilt[(int)nameOffsetOffset..(int)tableEnd]);
            var back = tmp.OpenPack(rebuilt, "back_" + name);
            Assert.Equal(names, Enumerable.Range(0, back.Count).Select(back.Name));
            Assert.Equal(Enumerable.Range(0, back.Count), Synth.NameBlobOrder(back)!);
        }
    }

    [Fact]
    public void NameBlobOrderNotDerived()
    {
        var src = Rp6lReaderTests.SmallPackPaths().FirstOrDefault(p => Path.GetFileName(p) == "reg1_pc.rpack");
        if (src is null) Assert.Skip("reg1_pc.rpack missing");
        using var tmp = new TempDir();
        using var pk = RpackFile.Open(src);
        var back = tmp.OpenPack(Synth.SpecsFromPack(pk), pk.Header.Field08);
        Assert.Equal([3, 2, 1, 0, 4, 5], Synth.NameBlobOrder(pk)!);
        Assert.Equal(Enumerable.Range(0, pk.Physicals.Length).Select(pk.PartOffset),
                     Enumerable.Range(0, back.Physicals.Length).Select(back.PartOffset));
        var split = Synth.SplitNul(back.NameBlob);
        Assert.Equal(Enumerable.Range(0, pk.Count).Select(i => pk.NameBytes(i).ToArray()), split);
    }

    // ---- storage order ------------------------------------------------------------------------------------

    [Fact]
    public void WriterUsesStockStorageOrder()
    {
        using var tmp = new TempDir();
        var pk = tmp.OpenPack([Synth.Texture("t"), Synth.Mesh("m"), Synth.Single("a", 0x5A, 32)], field08: 0x1000);
        Assert.Equal([0x10, 0x11, 0x12, 0xF0, 0xF1, 0x20, 0x21, 0x5A], pk.Storages.Select(s => (int)s.Type));
    }

    // ---- grouped placement --------------------------------------------------------------------------------

    // field08 == 0: every storage group is one contiguous region, groups in storage-table order.
    //
    // Resources: texA(hdr 80, bmp 1000), texB(hdr 80, bmp 500), area(700), prefab(600, 220).
    // storages: 0x20, 0x21, 0x5A, 0x61, 0x62 → 5; parts 7; names Tex_A/tex_b/dlc_area_x/Prefabs = 31 bytes.
    // table_end = 36 + 5·20 + 7·16 + 4·12 + 4·4 + 31 = 343 → payload start 352.
    //   0x20 @ 352 (units 22): texA.hdr 352 (+0), texB.hdr 432 (+5)  size 160 → cursor 512
    //   0x21 @ 512 (units 32): texA.bmp 512 (+0), texB.bmp 1520 (+63) size 1008+512 = 1520 → cursor 2020 → 2032
    //   0x5A @ 2032 (127): area 2032, size 704 → 2736
    //   0x61 @ 2736 (171): prefab 2736, size 608 → 3344
    //   0x62 @ 3344 (209): fixups 3344, size 224 → 3564 → file padded to 3568

    [Fact]
    public void GroupedOffsets()
    {
        using var tmp = new TempDir();
        var data = Synth.BuildBytes(Rp6lReaderTests.BasicResources());
        var pk = tmp.OpenPack(data);
        Assert.Equal(343, pk.TableEnd);
        Assert.Equal([352L, 512, 432, 1520, 2032, 2736, 3344], Enumerable.Range(0, 7).Select(pk.PartOffset));
        Assert.Equal([22u, 32, 127, 171, 209], pk.Storages.Select(s => s.BaseUnits));
        Assert.Equal([0u, 0, 5, 63, 0, 0, 0], pk.Physicals.Select(p => p.OffsetUnits));
        Assert.Equal([160L, 1520, 704, 608, 224], pk.Storages.Select(s => s.Size));
        Assert.Equal([2, 2, 1, 1, 1], pk.Storages.Select(s => (int)s.Count));
        Assert.True(data.Length >= 3564);
        // gaps are zero-filled
        Assert.All(data[1512..1520], b => Assert.Equal(0, b));
        Assert.All(data[343..352], b => Assert.Equal(0, b));
    }

    [Fact]
    public void GroupedTailPaddedTo16()
    {
        var data = Synth.BuildBytes(Rp6lReaderTests.BasicResources());
        Assert.Equal(3568, data.Length);
        Assert.Equal(new byte[4], data[3564..]);
    }

    [Fact]
    public void StorageSizeIsSumOfAlignedSizes()
    {
        using var tmp = new TempDir();
        var pk = tmp.OpenPack(Rp6lReaderTests.BasicResources());
        for (int si = 0; si < pk.Storages.Length; si++)
        {
            var members = pk.Physicals.Where(p => p.StorageIndex == si).ToArray();
            Assert.Equal(members.Sum(p => (long)Synth.AlignUp((int)p.Size, 16)), pk.Storages[si].Size);
            Assert.Equal(members.Length, pk.Storages[si].Count);
        }
    }

    [Fact]
    public void GroupedPayloadBytes()
    {
        using var tmp = new TempDir();
        var pk = tmp.OpenPack(Rp6lReaderTests.BasicResources());
        Assert.Equal(Synth.Payload(500, 3), pk.ReadPart(3));
        Assert.Equal(Synth.Payload(220, 4), pk.ReadPart(6));
    }

    [Fact]
    public void GroupedA64()
    {
        // grouped layout honours A=64 for group bases and part starts, storage.size = Σ align_up(size, 64)
        // table_end = 36 + 20 + 32 + 24 + 8 + 4 = 124 → 128 → align 64 → 128
        using var tmp = new TempDir();
        var pk = tmp.OpenPack([Synth.Single("a", 0x5A, 100, seed: 1, alignRaw: 12), Synth.Single("b", 0x5A, 100, seed: 2, alignRaw: 12)]);
        Assert.Equal(124, pk.TableEnd);
        Assert.Equal([128L, 256], Enumerable.Range(0, 2).Select(pk.PartOffset));
        Assert.Equal(256, pk.Storages[0].Size);
        Assert.Equal(64, pk.Storages[0].Alignment);
    }

    // ---- wraps and limits ---------------------------------------------------------------------------------

    [Fact]
    public void OwnerWraps()
    {
        // 65,537 logical resources: the owner field (u16) wraps (dlc_frontier_envprobes: 124,398 logicals)
        var res = Enumerable.Range(0, 65537)
            .Select(i => new ResourceSpec(Synth.Bytes($"e{i}"), 0x55, 1, [new PartSpec(0x55, [1], 8, 0x21, 0)]))
            .ToList();
        using var tmp = new TempDir();
        var pk = tmp.OpenPack(res);
        Assert.Equal(0, pk.Physicals[65536].Owner);
        Assert.Equal(65535, pk.Physicals[65535].Owner);
        Assert.Equal(65537u, pk.Header.LogicalCount);
        Assert.Equal(65536u, pk.Logicals[65536].FirstPart);
    }

    [Fact]
    public void CountWraps()
    {
        // 65,550 parts in one storage: the count field (u16) wraps to 14 (dlc_frontier_envprobes: 123,777 → 58,241)
        var res = Enumerable.Range(0, 4370)
            .Select(i => new ResourceSpec(Synth.Bytes($"a{i}"), 0x40, 1,
                                          [.. Enumerable.Range(0, 15).Select(_ => new PartSpec(0x40, [1], 8, 0x40, 0))]))
            .ToList();
        using var tmp = new TempDir();
        var pk = tmp.OpenPack(res);
        Assert.Equal(65550 & 0xFFFF, pk.Storages[0].Count);
        Assert.Equal(65550L * 16, pk.Storages[0].Size);
    }

    [Fact]
    public void PartCountLimits()
    {
        using var tmp = new TempDir();
        Assert.Throws<RpackBuildException>(() =>
            RpackWriter.Write(tmp.File("zero.rpack"), [new ResourceSpec("x"u8.ToArray(), 0x40, 1, [])]));
        Assert.Throws<RpackBuildException>(() =>
            RpackWriter.Write(tmp.File("sixteen.rpack"),
                [new ResourceSpec("x"u8.ToArray(), 0x40, 1,
                                  [.. Enumerable.Range(0, RpackFormat.MaxParts + 1).Select(_ => Synth.Part(0x40, Synth.Payload(8)))])]));
        var pk = tmp.OpenPack(
            [new ResourceSpec("x"u8.ToArray(), 0x40, 1,
                              [.. Enumerable.Range(0, RpackFormat.MaxParts).Select(_ => Synth.Part(0x40, Synth.Payload(8)))])]);
        Assert.Equal(15u, pk.Header.PhysicalCount);
        Assert.Equal(15u, pk.Logicals[0].Packed & 0xFFFF);
    }

    [Fact]
    public void UnknownPartTypeRefused()
    {
        using var tmp = new TempDir();
        Assert.Throws<RpackBuildException>(() =>
            RpackWriter.Write(tmp.File("x.rpack"), [new ResourceSpec("x"u8.ToArray(), 0x40, 1, [new PartSpec(0x99, [1], 8, 0x21, 0)])]));
    }

    [Fact]
    public void StorageGroupLimit()
    {
        static List<ResourceSpec> Res(int n) =>
        [
            .. Enumerable.Range(0, n).Select(m =>
            {
                var (f, meta) = m < 256 ? ((byte)0x40, (byte)m) : ((byte)0x41, (byte)0);
                return new ResourceSpec(Synth.Bytes($"r{m}"), 0x40, 1, [new PartSpec(0x40, [1], 8, f, meta)]);
            }),
        ];
        using var tmp = new TempDir();
        var pk = tmp.OpenPack(Res(RpackFormat.MaxStorages));
        Assert.Equal(256u, pk.Header.StorageCount);
        var ex = Assert.Throws<RpackBuildException>(() => RpackWriter.Write(tmp.File("over.rpack"), Res(RpackFormat.MaxStorages + 1)));
        Assert.Contains("257 storage groups", ex.Message);
    }

    [Fact]
    public void NulInNameRefused()
    {
        using var tmp = new TempDir();
        var ex = Assert.Throws<RpackBuildException>(() =>
            RpackWriter.Write(tmp.File("nul.rpack"), [Synth.Single("bad\0name"u8.ToArray(), 0x55, 16)]));
        Assert.Contains("NUL", ex.Message);
    }

    [Fact]
    public void EmptyWriterRefused()
    {
        using var tmp = new TempDir();
        Assert.Throws<RpackBuildException>(() => RpackWriter.Write(tmp.File("empty.rpack"), []));
    }

    [Fact]
    public void FlagBitsRange()
    {
        using var tmp = new TempDir();
        Assert.Throws<RpackBuildException>(() =>
            RpackWriter.Write(tmp.File("flags.rpack"),
                [new ResourceSpec("x"u8.ToArray(), 0x55, 1, [new PartSpec(0x55, [1], 8, 0x21, 0, FlagBits: 0x10000)])]));
    }

    [Fact]
    public void PhysicalWordsCarryOwnerFlagBitsAndFc()
    {
        using var tmp = new TempDir();
        var pk = tmp.OpenPack([Synth.Texture("t"), Synth.Mesh("m"),
                               new ResourceSpec("f"u8.ToArray(), 0x55, 1, [new PartSpec(0x55, [1, 2, 3], 8, 0x21, 0, Fc: 0xDEADBEEF)])]);
        for (int r = 0; r < pk.Count; r++)
        {
            for (int k = 0; k < pk.Logicals[r].PartCount; k++)
            {
                var p = pk.Physicals[pk.Logicals[r].FirstPart + k];
                Assert.Equal(r, (int)(p.Packed >> RpackFormat.PhysOwnerShift));
                Assert.Equal(pk.Logicals[r].Type == 0x10 ? RpackFormat.PhysBit8 : 0u, p.FlagBits);
                Assert.Equal(r == 2 ? 0xDEADBEEF : 0u, p.Fc);
            }
        }
        Assert.Equal([1, 0x81, 1], pk.Logicals.Select(l => (int)l.Flags));
        Assert.Equal(0x10, pk.Logicals[1].Type);
    }
}
