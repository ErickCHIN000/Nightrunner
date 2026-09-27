// Port of nightrunner-main/tests/test_rp6l_reader.py: header/table parsing, derived fields, names, part access,
// bounds errors. Synthetic packs come from Synth (built with RpackWriter, patched byte-wise); the six small shipped
// DLTB packs are install-backed.
//
// Not ported:
//   test_storage_40bit_sizes (to_json/from_json half) — the C# records have no JSON form.
//   test_storage_derived_fields (flag_bit2 / key) — Storage has no FlagBit2 or Key accessor; the rest is ported.
//   test_to_json_views — no JSON views of resources or tables in the C# reader.
//   test_find_engine_case_folding (fold=False) — the C# lookup (RpackCatalog.Lookup) always folds.
//   test_non_ascii_bytes_survive (find by raw bytes) — Lookup takes a string; non-UTF-8 names cannot be passed.
//     The byte-exact name survival half is ported.
//   test_read_part_bytes (memoryview type, part_by_type) — ReadPart returns a copy; there is no per-resource
//     part_by_type accessor. Byte content is ported.
//   test_name_offset_outside_blob_and_unterminated (lazy half) — C# resolves every name at open, so an offset
//     outside the blob is refused by Open instead of by the one name; the same holds for an unterminated
//     name (NameBlobWithoutFinalNul_IsRefused).
//   name_blob_order() — no accessor; derived here from NameStart (Synth.NameBlobOrder).
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Tests;

public class Rp6lReaderTests
{
    /// <summary>2 textures + 1 area + 1 prefab, grouped layout (field08 = 0).</summary>
    internal static List<ResourceSpec> BasicResources() =>
    [
        Synth.Texture("Tex_A", 80, 1000, seed: 1),
        Synth.Texture("tex_b", 80, 500, seed: 2),
        Synth.Single("dlc_area_x", 0x5A, 700),
        Synth.Prefab(),
    ];

    private static byte[] Bytes<T>(T value) where T : unmanaged
    {
        var b = new byte[Marshal.SizeOf<T>()];
        MemoryMarshal.Write(b, in value);
        return b;
    }

    private static Storage MakeStorage(byte type, byte align, byte flags, byte meta) =>
        new() { Type = type, AlignRaw = align, Flags = flags, Metadata = meta };

    // ---- records ------------------------------------------------------------------------------------------

    [Fact]
    public void HeaderPackUnpack()
    {
        var h = new RpackHeader
        {
            Magic = RpackFormat.Magic, Version = RpackFormat.Version, Field08 = 0x1000, PhysicalCount = 5,
            StorageCount = 2, NameCount = 3, NameBytes = 40, LogicalCount = 3, Flags = 1,
        };
        var b = Bytes(h);
        Assert.Equal(RpackFormat.HeaderSize, b.Length);
        Assert.Equal("RP6L"u8.ToArray(), b[..4]);
        Assert.Equal(RpackFormat.Version, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)));
        Assert.Equal(h, MemoryMarshal.Read<RpackHeader>(b));
        Assert.True(h.OnDemand);
        Assert.False(new RpackHeader().OnDemand);
    }

    [Fact]
    public void StorageDerivedFields()
    {
        // alignment = 1 << ((align_raw >> 1) & 15): word0 bits 9..12
        for (int raw = 0; raw < 256; raw++)
        {
            var s = MakeStorage(0x10, (byte)raw, 0xC9, 0x03);
            uint word0 = BinaryPrimitives.ReadUInt32LittleEndian(Bytes(s));
            Assert.Equal(1 << ((raw >> 1) & 15), s.Alignment);
            Assert.Equal(1 << (int)((word0 >> 9) & 15), s.Alignment);
        }
        Assert.Equal(16, MakeStorage(0x10, 8, 0xC9, 3).Alignment);   // every shipped storage
        Assert.Equal(64, MakeStorage(0x10, 12, 0xC9, 3).Alignment);
        Assert.Equal(1, MakeStorage(0x10, 0, 0xC9, 3).Alignment);

        // method = flags & 3, version = (flags >> 4) | ((metadata & 15) << 4), codec = metadata >> 4
        (byte F, byte M, int Method, int Version, int Codec, bool Stream)[] cases =
        [
            (0xC9, 0x03, 1, 60, 0, true),    // 0x10/0x11 on-demand mesh
            (0xC0, 0x03, 0, 60, 0, false),   // engine_pc mesh
            (0xD9, 0x00, 1, 13, 0, true),    // 0x12 skin
            (0x59, 0x00, 1, 5, 0, true),     // 0xF0
            (0x49, 0x00, 1, 4, 0, true),     // 0xF1
            (0x29, 0x00, 1, 2, 0, true),     // 0xF3 / 0x45
            (0xB1, 0x00, 1, 11, 0, false),   // textures
            (0x40, 0x00, 0, 4, 0, false),    // anims
            (0xC0, 0x08, 0, 140, 0, false),  // 0x47/0x48 animgraph (version 140 needs the metadata nibble)
            (0x20, 0x00, 0, 2, 0, false),    // 0x44
            (0x80, 0x00, 0, 8, 0, false),    // prefabs
            (0x21, 0x00, 1, 2, 0, false),    // area/envprobe/voxel
            (0x21, 0x50, 1, 2, 5, false),    // synthetic codec nibble
            (0x23, 0x00, 3, 2, 0, false),    // synthetic compressed method
        ];
        foreach (var c in cases)
        {
            var s = MakeStorage(0x10, 8, c.F, c.M);
            uint word0 = BinaryPrimitives.ReadUInt32LittleEndian(Bytes(s));
            Assert.Equal((c.Method, c.Version, c.Codec, c.Stream), (s.Method, s.FormatVersion, s.Codec, s.Stream));
            Assert.Equal((int)((word0 >> 20) & 0xFF), s.FormatVersion);
            Assert.Equal((int)((word0 >> 16) & 3), s.Method);
            Assert.Equal((int)(word0 >> 28), s.Codec);
        }
    }

    [Fact]
    public void Storage40BitSizes()
    {
        var s = new Storage
        {
            Type = 0x21, AlignRaw = 8, Flags = 0xB1, Metadata = 0, BaseUnits = 7,
            SizeLo = 0x11223344, SizeHi = 3, CompressedLo = 5, CompressedHi = 1, Count = 65535,
        };
        var b = Bytes(s);
        Assert.Equal(RpackFormat.StorageSize, b.Length);
        Assert.Equal(3, b[0x12]);          // size_hi
        Assert.Equal(1, b[0x13]);          // compressed_hi
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)));
        Assert.Equal((3L << 32) | 0x11223344, s.Size);
        Assert.Equal((1L << 32) | 5, s.Compressed);
        Assert.Equal(s, MemoryMarshal.Read<Storage>(b));
        Assert.Equal(7L << 4, s.BaseOffset);
    }

    [Fact]
    public void PhysicalDerivedFields()
    {
        var p = new Physical
        {
            Packed = (0x1234u << 16) | RpackFormat.PhysBit8 | RpackFormat.PhysBit15 | (5u << 9) | 0x07,
            OffsetUnits = 100, Size = 200, Fc = 0xDEAD,
        };
        Assert.Equal(RpackFormat.PhysicalSize, Bytes(p).Length);
        Assert.Equal(7, p.StorageIndex);
        Assert.Equal(0x1234, p.Owner);
        Assert.True(p.Bit8);
        Assert.Equal(5, p.Priority);
        Assert.True(p.Bit15);
        Assert.False(p.Bit14);
        Assert.False(p.Special);
        Assert.False(p.Child);
        Assert.Equal(RpackFormat.PhysBit8 | RpackFormat.PhysBit15 | (5u << 9), p.FlagBits);
        Assert.Equal(p, MemoryMarshal.Read<Physical>(Bytes(p)));
        var q = new Physical { Packed = RpackFormat.PhysSpecial | RpackFormat.PhysChild | RpackFormat.PhysBit14 };
        Assert.True(q.Special && q.Child && q.Bit14);
        Assert.Equal(0x7000u, q.FlagBits);
    }

    [Fact]
    public void LogicalDerivedFields()
    {
        var l = new Logical { Packed = 5 | (0x10u << 16) | (0x81u << 24), NameIndex = 3, FirstPart = 40 };
        Assert.Equal(RpackFormat.LogicalSize, Bytes(l).Length);
        Assert.Equal((5, (byte)0x10, (byte)0x81, 3u, 40u), (l.PartCount, l.Type, l.Flags, l.NameIndex, l.FirstPart));
        Assert.Equal(l, MemoryMarshal.Read<Logical>(Bytes(l)));
    }

    // ---- a synthetic pack ---------------------------------------------------------------------------------

    [Fact]
    public void HeaderAndTableBounds()
    {
        using var tmp = new TempDir();
        var data = Synth.BuildBytes(BasicResources());
        var pk = tmp.OpenPack(data);
        var h = pk.Header;
        Assert.Equal((RpackFormat.Magic, RpackFormat.Version, 0u, 1u), (h.Magic, h.Version, h.Field08, h.Flags));
        Assert.Equal(4u, h.LogicalCount);
        Assert.Equal(4u, h.NameCount);
        Assert.Equal(2u + 2 + 1 + 2, h.PhysicalCount);
        Assert.Equal(5u, h.StorageCount);   // 0x20, 0x21, 0x5A, 0x61, 0x62
        string[] names = ["Tex_A", "tex_b", "dlc_area_x", "Prefabs"];
        Assert.Equal((uint)names.Sum(n => n.Length + 1), h.NameBytes);
        Assert.Equal(Synth.ExpectedTableEnd(5, 7, 4, names), pk.TableEnd);
        Assert.Equal(RpackFormat.HeaderSize, pk.StorageOffset);
        Assert.Equal(RpackFormat.HeaderSize + 5 * RpackFormat.StorageSize, pk.PhysicalOffset);
        Assert.Equal(pk.PhysicalOffset + 7 * RpackFormat.PhysicalSize, pk.LogicalOffset);
        Assert.Equal(pk.LogicalOffset + 4 * RpackFormat.LogicalSize, pk.NameOffsetOffset);
        Assert.Equal(pk.NameOffsetOffset + 4 * 4, pk.NameBlobOffset);
        Assert.Equal(data.Length, pk.Length);
        Assert.Equal((pk.NameOffsetOffset, pk.TableEnd - pk.NameOffsetOffset), pk.TableRange(RpackTable.Names));
    }

    [Fact]
    public void StorageTable()
    {
        using var tmp = new TempDir();
        var pk = tmp.OpenPack(BasicResources());
        Assert.Equal([0x20, 0x21, 0x5A, 0x61, 0x62], pk.Storages.Select(s => (int)s.Type));
        foreach (var s in pk.Storages)
        {
            Assert.Equal(Synth.Stock[s.Type], (s.AlignRaw, s.Flags, s.Metadata));
            Assert.Equal(ResTypes.Get(s.Type)!.Value.Version, s.FormatVersion);
            Assert.Equal(0, s.Compressed);
        }
        Assert.Equal([2, 2, 1, 1, 1], pk.Storages.Select(s => (int)s.Count));
    }

    [Fact]
    public void PartIdentityByStorageType()
    {
        using var tmp = new TempDir();
        var pk = tmp.OpenPack(BasicResources());
        Assert.Equal([0x20, 0x21, 0x20, 0x21, 0x5A, 0x61, 0x62], Enumerable.Range(0, 7).Select(i => (int)pk.PartType(i)));
        for (int i = 0; i < pk.Physicals.Length; i++)
        {
            var p = pk.Physicals[i];
            Assert.Equal(((long)pk.Storages[p.StorageIndex].BaseUnits + p.OffsetUnits) << 4, pk.PartOffset(i));
            Assert.Equal(16, pk.PartStorage(i).Alignment);
            Assert.True(pk.PartIsDirect(i));
            Assert.Null(pk.PartUnreadableReason(i));
            Assert.Equal(0, pk.PartOffset(i) % 16);
            Assert.True(pk.PartOffset(i) >= Synth.AlignUp((int)pk.TableEnd, 16));
            Assert.True(pk.PartOffset(i) + p.Size <= pk.Length);
        }
    }

    [Fact]
    public void OwnerAndPartRanges()
    {
        using var tmp = new TempDir();
        var pk = tmp.OpenPack(BasicResources());
        Assert.Equal([0u, 2, 4, 5], pk.Logicals.Select(l => l.FirstPart));
        Assert.Equal([2, 2, 1, 2], pk.Logicals.Select(l => l.PartCount));
        for (int r = 0; r < pk.Count; r++)
            for (int k = 0; k < pk.Logicals[r].PartCount; k++)
                Assert.Equal(r, pk.Physicals[pk.Logicals[r].FirstPart + k].Owner);
        Assert.Equal(4, pk.Count);
        Assert.Equal([0x61, 0x62], new[] { 5, 6 }.Select(i => (int)pk.PartType(i)));
        Assert.Equal([0L + 80 + 1000, 80 + 500, 700, 600 + 220], pk.ResourceSize);
    }

    [Fact]
    public void ReadPartBytes()
    {
        using var tmp = new TempDir();
        var pk = tmp.OpenPack(BasicResources());
        Assert.Equal(Synth.Payload(80, 1), pk.ReadPart(0));
        Assert.Equal(Synth.Payload(1000, 2), pk.ReadPart(1));
        Assert.Equal(Synth.Payload(220, 4), pk.ReadPart(6));
        Assert.Equal(Synth.Payload(1000, 2)[100..150], pk.ReadPart(1, skip: 100, count: 50));
        Assert.Empty(pk.ReadPart(1, skip: 1000));
    }

    [Fact]
    public void Names()
    {
        using var tmp = new TempDir();
        var pk = tmp.OpenPack(BasicResources());
        Assert.Equal(["Tex_A", "tex_b", "dlc_area_x", "Prefabs"], Enumerable.Range(0, 4).Select(pk.Name));
        Assert.Equal("Tex_A"u8.ToArray(), pk.NameBytes(0).ToArray());
        Assert.Equal("tex_a"u8.ToArray(), pk.NameBytesLower(0).ToArray());
        Assert.Equal([0u, 1, 2, 3], pk.Logicals.Select(l => l.NameIndex));
        Assert.Equal([0, 1, 2, 3], Synth.NameBlobOrder(pk)!);
        Assert.Equal(0, pk.NameBlob[^1]);
        Assert.Equal(4, pk.NameBlob.Count(b => b == 0));
    }

    [Fact]
    public async Task FindEngineCaseFolding()
    {
        // FindLogicalResourceUsingName +0x19250: ASCII A..Z folded on both sides, linear scan, type filter
        using var tmp = new TempDir();
        var path = tmp.File("basic.rpack");
        RpackWriter.Write(path, BasicResources());
        using var cat = new RpackCatalog();
        await cat.LoadAsync([path], ct: TestContext.Current.CancellationToken);
        Assert.Equal([0], cat.Lookup("tex_a"));
        Assert.Equal([0], cat.Lookup("TEX_A"));
        Assert.Equal([1], cat.Lookup("TEX_B", 0x20));
        Assert.Empty(cat.Lookup("tex_b", 0x5A));
        Assert.Equal([3], cat.Lookup("prefabs"));
        Assert.Empty(cat.Lookup("nope"));
        Assert.Equal(new Dictionary<byte, int> { [0x20] = 2, [0x5A] = 1, [0x61] = 1 }, cat.TypeCounts());
        // only 'A'..'Z' move
        Assert.Equal(
            new byte[] { (byte)'a', (byte)'b', (byte)'z', (byte)'[', (byte)'\\', (byte)']', (byte)'^', (byte)'_',
                         (byte)'`', (byte)'{', (byte)'|', (byte)'}', (byte)'~', 0x80, 0xC4 },
            RpackFile.EngineFold([(byte)'A', (byte)'b', (byte)'Z', (byte)'[', (byte)'\\', (byte)']', (byte)'^',
                                  (byte)'_', (byte)'`', (byte)'{', (byte)'|', (byte)'}', (byte)'~', 0x80, 0xC4]));
    }

    // ---- names: edge cases --------------------------------------------------------------------------------

    [Fact]
    public async Task DuplicatesAndLeadingSpaces()
    {
        // 72 duplicate names and 8 leading-space names exist in the shipped corpus; both must survive byte-exact
        using var tmp = new TempDir();
        var path = tmp.File("dup.rpack");
        RpackWriter.Write(path,
        [
            Synth.Single("dup", 0x40, 16, seed: 1), Synth.Single("dup", 0x40, 32, seed: 2),
            Synth.Single(" leading", 0x40, 8, seed: 3), Synth.Single("leading", 0x40, 8, seed: 4),
            new ResourceSpec("DUP"u8.ToArray(), 0x42, 1,
                             [Synth.Part(0x42, Synth.Payload(8)), Synth.Part(0x43, Synth.Payload(8))]),
        ]);
        using var cat = new RpackCatalog();
        await cat.LoadAsync([path], ct: TestContext.Current.CancellationToken);
        var pk = cat.IndexedPacks[0].Pack!;
        Assert.Equal(["dup", "dup", " leading", "leading", "DUP"], Enumerable.Range(0, 5).Select(pk.Name));
        Assert.Equal([0, 1, 4], cat.Lookup("dup"));          // lowest index first (engine takes the first)
        Assert.Equal([0, 1], cat.Lookup("dup", 0x40));
        Assert.Equal([4], cat.Lookup("dup", 0x42));
        Assert.Equal([2], cat.Lookup(" LEADING"));
        Assert.Equal([3], cat.Lookup("leading"));
        Assert.Equal("dup\0dup\0 leading\0leading\0DUP\0"u8.ToArray(), pk.NameBlob);
    }

    [Fact]
    public void NameBlobOrderPermutation()
    {
        // the C# writer always writes the blob in resource order; permute it by hand: "a\0b\0c\0" -> "c\0a\0b\0"
        using var tmp = new TempDir();
        var data = Synth.BuildBytes([Synth.Single("a", 0x55, 16), Synth.Single("b", 0x55, 16), Synth.Single("c", 0x55, 16)]);
        var pk0 = tmp.OpenPack(data, "orig.rpack");
        data = Synth.Patch(data, (int)pk0.NameBlobOffset, "c\0a\0b\0"u8);
        data = Synth.PatchU32(data, Synth.NameOffsetOff(pk0, 0), 2);
        data = Synth.PatchU32(data, Synth.NameOffsetOff(pk0, 1), 4);
        data = Synth.PatchU32(data, Synth.NameOffsetOff(pk0, 2), 0);
        var pk = tmp.OpenPack(data, "perm.rpack");
        Assert.Equal("c\0a\0b\0"u8.ToArray(), pk.NameBlob);
        Assert.Equal([2, 4, 0], pk.NameStart);            // name_index == logical index, offsets permuted
        Assert.Equal(["a", "b", "c"], Enumerable.Range(0, 3).Select(pk.Name));
        Assert.Equal([2, 0, 1], Synth.NameBlobOrder(pk)!);
    }

    [Fact]
    public void NameBlobOrderNoneWhenNotBijective()
    {
        using var tmp = new TempDir();
        var data = Synth.BuildBytes([Synth.Single("a", 0x55, 16), Synth.Single("b", 0x55, 16)]);
        var pk = tmp.OpenPack(data, "orig.rpack");
        var pk2 = tmp.OpenPack(Synth.PatchU32(data, Synth.LogicalOff(pk, 1) + 4, 0), "same.rpack");   // both -> name 0
        Assert.Equal(["a", "a"], Enumerable.Range(0, 2).Select(pk2.Name));
        Assert.Null(Synth.NameBlobOrder(pk2));
    }

    [Fact]
    public void NonAsciiBytesSurvive()
    {
        byte[] raw = [(byte)'c', (byte)'a', (byte)'f', 0xC3, 0xA9, 0xFF];
        using var tmp = new TempDir();
        var pk = tmp.OpenPack([Synth.Single(raw, 0x55, 16)]);
        Assert.Equal(raw, pk.NameBytes(0).ToArray());
        Assert.Equal(raw, pk.NameBytesLower(0).ToArray());   // nothing above 'Z' folds
    }

    // ---- refusals and bounds ------------------------------------------------------------------------------

    [Fact]
    public void ReadPartRefusesChild()
    {
        using var tmp = new TempDir();
        var data = Synth.BuildBytes(BasicResources());
        var pk = tmp.OpenPack(data, "orig.rpack");
        var pk2 = tmp.OpenPack(Synth.PatchU32(data, Synth.PhysicalOff(pk, 4), pk.Physicals[4].Packed | RpackFormat.PhysChild), "child.rpack");
        Assert.True(pk2.Physicals[4].Child);
        Assert.False(pk2.PartIsDirect(4));
        Assert.NotNull(pk2.PartUnreadableReason(4));
        Assert.Throws<RpackFormatException>(() => pk2.ReadPart(4));
        Assert.NotEmpty(pk2.ReadPart(3));   // other parts unaffected
    }

    [Fact]
    public void ReadPartRefusesCompressedMethods()
    {
        using var tmp = new TempDir();
        var data = Synth.BuildBytes(BasicResources());
        var pk = tmp.OpenPack(data, "orig.rpack");
        foreach (int method in new[] { 2, 3 })
        {
            var s = pk.Storages[2];      // 0x5A area, flags 0x21
            var pk2 = tmp.OpenPack(Synth.PatchU8(data, Synth.StorageOff(2) + 2, (byte)((s.Flags & ~3) | method)), $"m{method}.rpack");
            Assert.Equal(method, pk2.Storages[2].Method);
            Assert.False(pk2.PartIsDirect(4));
            Assert.Throws<RpackFormatException>(() => pk2.ReadPart(4));
        }
    }

    [Fact]
    public void BadMagicAndVersion()
    {
        using var tmp = new TempDir();
        var data = Synth.BuildBytes(BasicResources());
        Assert.Throws<RpackFormatException>(() => tmp.OpenPack(Synth.Patch(data, 0, "RP5L"u8), "magic.rpack"));
        Assert.Throws<RpackFormatException>(() => tmp.OpenPack(Synth.PatchU32(data, 4, 3), "version.rpack"));
    }

    [Fact]
    public void TruncatedFiles()
    {
        using var tmp = new TempDir();
        var data = Synth.BuildBytes(BasicResources());
        var pk = tmp.OpenPack(data, "orig.rpack");
        // shorter than the header
        foreach (int n in new[] { 0, 1, 20, 35 })
            Assert.Throws<RpackFormatException>(() => tmp.OpenPack(data[..n], $"short{n}.rpack"));
        // tables cut: anywhere before table_end ("tables extend past end of file")
        foreach (long cut in new[] { RpackFormat.HeaderSize, RpackFormat.HeaderSize + 3, pk.PhysicalOffset + 5,
                                     pk.LogicalOffset, pk.NameOffsetOffset + 1, pk.TableEnd - 4, pk.TableEnd - 1 })
            Assert.Throws<RpackFormatException>(() => tmp.OpenPack(data[..(int)cut], $"cut{cut}.rpack"));
        // payload cut: tables parse, but reading the truncated span fails
        int last = Enumerable.Range(0, pk.Physicals.Length).MaxBy(pk.PartOffset);
        long lastCut = pk.PartOffset(last) + pk.Physicals[last].Size - 1;
        var pk2 = tmp.OpenPack(data[..(int)lastCut], "payload.rpack");
        Assert.Throws<RpackFormatException>(() => pk2.ReadPart(last));
        int first = Enumerable.Range(0, pk.Physicals.Length).MinBy(pk.PartOffset);
        Assert.Equal((int)pk.Physicals[first].Size, pk2.ReadPart(first).Length);
        // the whole file reads back
        Assert.Equal(Synth.Payload(80, 1), pk.ReadPart(0));
    }

    [Fact]
    public void StorageIndexOutOfRange()
    {
        using var tmp = new TempDir();
        var data = Synth.BuildBytes(BasicResources());
        var pk = tmp.OpenPack(data, "orig.rpack");
        var bad = Synth.PatchU32(data, Synth.PhysicalOff(pk, 0), (pk.Physicals[0].Packed & ~0xFFu) | 5);   // 5 storages → max 4
        Assert.Throws<RpackFormatException>(() => tmp.OpenPack(bad, "bad.rpack"));
    }

    [Fact]
    public void StorageCountOver256()
    {
        using var tmp = new TempDir();
        var data = Synth.BuildBytes(BasicResources());
        Assert.Throws<RpackFormatException>(() => tmp.OpenPack(Synth.PatchU32(data, 0x10, 257)));
    }

    [Fact]
    public void LogicalPartsOutOfRange()
    {
        using var tmp = new TempDir();
        var data = Synth.BuildBytes(BasicResources());
        var pk = tmp.OpenPack(data, "orig.rpack");
        var bad = Synth.PatchU32(data, Synth.LogicalOff(pk, 3) + 8, 6);    // first_part 6 + 2 parts > 7
        Assert.Throws<RpackFormatException>(() => tmp.OpenPack(bad, "bad.rpack"));
    }

    [Fact]
    public void NameIndexOutOfRange()
    {
        using var tmp = new TempDir();
        var data = Synth.BuildBytes(BasicResources());
        var pk = tmp.OpenPack(data, "orig.rpack");
        Assert.Throws<RpackFormatException>(() => tmp.OpenPack(Synth.PatchU32(data, Synth.LogicalOff(pk, 0) + 4, 4), "bad.rpack"));
    }

    [Fact]
    public void NameOffsetOutsideBlob_IsRefusedAtOpen()
    {
        using var tmp = new TempDir();
        var data = Synth.BuildBytes(BasicResources());
        var pk = tmp.OpenPack(data, "orig.rpack");
        Assert.Throws<RpackFormatException>(() => tmp.OpenPack(Synth.PatchU32(data, Synth.NameOffsetOff(pk, 1), 10_000), "bad.rpack"));
    }

    [Fact]
    public void NameBlobWithoutFinalNul_IsRefused()
    {
        using var tmp = new TempDir();
        var data = Synth.BuildBytes(BasicResources());
        var pk = tmp.OpenPack(data, "orig.rpack");
        var bad = Synth.PatchU8(data, pk.TableEnd - 1, (byte)'x');
        Assert.Throws<RpackFormatException>(() =>
        {
            var pk3 = tmp.OpenPack(bad, "bad.rpack");
            _ = pk3.Name(3);
        });
    }

    [Fact]
    public void PartSpanPastEof()
    {
        using var tmp = new TempDir();
        var data = Synth.BuildBytes(BasicResources());
        var pk = tmp.OpenPack(data, "orig.rpack");
        var pk2 = tmp.OpenPack(Synth.PatchU32(data, Synth.PhysicalOff(pk, 1) + 8, 1u << 30), "bad.rpack");
        Assert.NotNull(pk2.PartUnreadableReason(1));
        Assert.Throws<RpackFormatException>(() => pk2.ReadPart(1));
    }

    // ---- the six small shipped packs (install-backed) -----------------------------------------------------

    /// <summary>name: (field08, storage types, logical count, physical count, type histogram, table_end, size).</summary>
    internal static readonly Dictionary<string, (uint F08, byte[] Storages, int L, int P, Dictionary<byte, int> Hist, long TableEnd, long Size)> ShippedExpect = new()
    {
        ["dlc_ft_prologue_envprobes_pc.rpack"] = (0, [0x55], 146, 146, new() { [0x55] = 146 }, 9108, 1920832),
        ["menu_level_ft_persistent_pc.rpack"] = (0, [0x61, 0x62], 1, 2, new() { [0x61] = 1 }, 132, 559904),
        ["reg_buffer_cst_pa_pc.rpack"] = (0, [0x61, 0x62], 1, 2, new() { [0x61] = 1 }, 132, 976),
        ["reg_pc.rpack"] = (0, [0x5A, 0x61, 0x62], 2, 3, new() { [0x5A] = 1, [0x61] = 1 }, 212, 6848),
        ["dlc_frontier_cb_region_0_pc.rpack"] = (0, [0x5A], 4, 4, new() { [0x5A] = 4 }, 299, 326800),
        ["reg1_pc.rpack"] = (0, [0x5A, 0x61, 0x62], 6, 7, new() { [0x5A] = 5, [0x61] = 1 }, 595, 1516288),
    };

    /// <summary>Relative to the assets folder.</summary>
    internal static readonly string[] SmallPacks =
    [
        "dlc_ft_prologue_envprobes_pc.rpack",
        "menu_level_ft/menu_level_ft_persistent_pc.rpack",
        "dlc_frontier/reg_buffer_cst_pa_pc.rpack",
        "dlc_frontier/reg_pc.rpack",
        "dlc_frontier/dlc_frontier_cb_region_0_pc.rpack",
        "dlc_ft_prologue/reg1_pc.rpack",
    ];

    /// <summary>The small shipped packs of the detected DLTB install (skips when there is none).</summary>
    internal static List<string> SmallPackPaths()
    {
        var gi = Installs.Require("dltb");
        var found = SmallPacks.Select(rel => Path.Combine(gi.Assets!, rel)).Where(File.Exists).ToList();
        if (found.Count == 0) Assert.Skip("small shipped packs not available");
        return found;
    }

    [Fact]
    public void ShippedTables()
    {
        foreach (var path in SmallPackPaths())
        {
            string name = Path.GetFileName(path);
            var e = ShippedExpect[name];
            using var pk = RpackFile.Open(path);
            var h = pk.Header;
            Assert.Equal((e.F08, 1u, 4u), (h.Field08, h.Flags, h.Version));
            Assert.Equal(e.Storages, pk.Storages.Select(s => s.Type));
            Assert.Equal(((uint)e.L, (uint)e.P, (uint)e.L), (h.LogicalCount, h.PhysicalCount, h.NameCount));
            var hist = pk.Logicals.GroupBy(l => l.Type).ToDictionary(g => g.Key, g => g.Count());
            Assert.Equal(e.Hist, hist);
            Assert.Equal(e.TableEnd, pk.TableEnd);
            Assert.Equal(e.Size, pk.Length);
            foreach (var s in pk.Storages)
            {
                Assert.Equal(8, s.AlignRaw);
                Assert.Equal(16, s.Alignment);
                Assert.Equal(Synth.Stock[s.Type], (s.AlignRaw, s.Flags, s.Metadata));
                Assert.Equal(ResTypes.Get(s.Type)!.Value.Version, s.FormatVersion);
                Assert.Equal(0, s.Compressed);
                Assert.Contains(s.Method, new[] { 0, 1 });
            }
        }
    }

    [Fact]
    public void ShippedRelations()
    {
        foreach (var path in SmallPackPaths())
        {
            using var pk = RpackFile.Open(path);
            Assert.Equal(Enumerable.Range(0, pk.Count).Select(i => (uint)i), pk.Logicals.Select(l => l.NameIndex));
            var seen = new HashSet<int>();
            for (int r = 0; r < pk.Count; r++)
            {
                Assert.Equal(0x01, pk.Logicals[r].Flags);        // census: every non-mesh/non-ANM2 logical
                for (int k = 0; k < pk.Logicals[r].PartCount; k++)
                {
                    int i = (int)pk.Logicals[r].FirstPart + k;
                    Assert.True(seen.Add(i));
                    var ph = pk.Physicals[i];
                    Assert.Equal(r, ph.Owner);
                    Assert.Equal(0u, ph.FlagBits);               // bit 8 only on mesh-family/ANM2 parts
                    Assert.Equal(0u, ph.Fc);
                    Assert.Equal(0, pk.PartOffset(i) % 16);
                    Assert.True(pk.PartIsDirect(i));
                    Assert.Equal((int)ph.Size, pk.ReadPart(i).Length);
                }
            }
            Assert.Equal(pk.Physicals.Length, seen.Count);
            Assert.NotNull(Synth.NameBlobOrder(pk));
        }
    }

    [Fact]
    public async Task FindOnShipped()
    {
        var path = SmallPackPaths().FirstOrDefault(p => Path.GetFileName(p) == "reg1_pc.rpack");
        if (path is null) Assert.Skip("reg1_pc.rpack missing");
        using var cat = new RpackCatalog();
        await cat.LoadAsync([path], ct: TestContext.Current.CancellationToken);
        var pk = cat.IndexedPacks[0].Pack!;
        Assert.Equal([5], cat.Lookup("PREFABS"));
        Assert.Equal([5], cat.Lookup("Prefabs", 0x61));
        Assert.Empty(cat.Lookup("Prefabs", 0x5A));
        Assert.Equal([4], cat.Lookup("dlc_ft_prologue_genpin_9x2ee007_area"));
        Assert.Equal([3, 2, 1, 0, 4, 5], Synth.NameBlobOrder(pk)!);
        Assert.Equal([0x61, 0x62], new[] { 5, 6 }.Select(i => (int)pk.PartType(i)));
        Assert.Equal(608, pk.PartOffset(0));
        Assert.Equal(1515856, pk.PartOffset(6));
    }

    [Fact]
    public void NameBlobOrderShipped()
    {
        var path = SmallPackPaths().FirstOrDefault(p => Path.GetFileName(p) == "dlc_frontier_cb_region_0_pc.rpack");
        if (path is null) Assert.Skip("dlc_frontier_cb_region_0_pc.rpack missing");
        using var pk = RpackFile.Open(path);
        Assert.Equal([3, 0, 1, 2], Synth.NameBlobOrder(pk)!);
        Assert.Equal([34, 61, 88, 0], pk.NameStart);   // name_index == logical index, so these are the offsets
    }
}
