using System.Buffers.Binary;
using System.Text;
using Nightrunner.Core.Mesh.ClassReader;
using Nightrunner.Core.Prefab;
using Nightrunner.Core.Rpack;
using Record = Nightrunner.Core.Mesh.ClassReader.Record;

namespace Nightrunner.Tests;

// Binary Prefabs resources (type 0x61, parts 0x61 + 0x62). Ports tests/test_types.py::test_prefab_graph (reg_cst_pc and
// engine_pc: prefix sum == object_count, the stream ends exactly at the part end, byte-identical re-serialisation,
// every class element resolves) and adds the container rules of the prefab format notes (§9), the decoder and the
// two structural edits. The edits are structural only: nothing they produce has been loaded in the game.
public class PrefabTests
{
    // ---- install-backed -----------------------------------------------------------------------------------------

    private static (PrefabContainer C, byte[] Primary, byte[] Meta) Load(string game, string packName)
    {
        var install = Installs.Require(game);
        var path = install.Rpacks().FirstOrDefault(p => Path.GetFileName(p).Equals(packName, StringComparison.OrdinalIgnoreCase));
        if (path is null) Assert.Skip($"{packName} not in the {game} install");
        using var pack = RpackFile.Open(path);
        int li = Assert.Single(PrefabContainer.ResourcesIn(pack));
        Assert.Equal("Prefabs", pack.Name(li));
        var (img, meta) = PrefabContainer.ReadParts(pack, li);
        return (PrefabContainer.Parse(img, meta), img, meta);
    }

    [Fact]
    public void CommonPrefabsRoundTripAndNumbers()
    {
        var (c, img, meta) = Load("dltb", "common_prefabs_pc.rpack");
        Assert.Equal(meta, c.EncodeMetadata());
        Assert.Equal(img, c.EncodePrimary());
        c.Validate();
        // the notes' [DATA] numbers for common_prefabs_pc (2026-09-24)
        Assert.Equal(21_794_992, img.Length);
        Assert.Equal(15_145_212, meta.Length);
        Assert.Equal((7_449u, 207u), (c.PrefabCount, c.PresetCount));
        Assert.Equal(0x80001DE8u, c.ObjectCountRaw);
        Assert.Equal(286_950, c.Records.Count);
        Assert.Equal(1_047_689, c.Slots.Count);
        Assert.Equal(6_463_324, c.SecondarySize);
        Assert.Equal((3, 12), (c.PadAfterKinds.Length, c.PadAfterSize.Length));
        var kinds = c.Slots.GroupBy(s => s.Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(new Dictionary<byte, int> { [0] = 362_287, [2] = 106_986, [6] = 107, [9] = 468_777, [14] = 109_532 }, kinds);
        Assert.Equal(181_687, c.Records.FindIndex(r => r.Reverse));
        Assert.Equal(578_912, c.Slots.FindIndex(s => (s.Kind & 1) != 0));
    }

    [Fact]
    public void RegPackRoundTrip()
    {
        var (c, img, meta) = Load("dltb", "reg_cst_pc.rpack");
        Assert.Equal(meta, c.EncodeMetadata());
        Assert.Equal(img, c.EncodePrimary());
        c.Validate();
        Assert.Equal((66u, 0u), (c.PrefabCount, c.PresetCount));
        Assert.Equal(c.PrefabCount + c.PresetCount, c.ObjectCount);
    }

    [Theory]
    [InlineData("reg_cst_pc.rpack")]
    [InlineData("engine_pc.rpack")]
    public void PrototypeGraphChecks(string packName)
    {
        var (c, _, meta) = Load("dltb", packName);
        Assert.Equal(c.PrefabCount + c.PresetCount, c.ObjectCount);
        Assert.Empty(c.Trailing);                                    // the stream ends exactly at the part end
        Assert.Equal(meta, c.EncodeMetadata());
        var tx = new PrefabText(c);
        int pool = 0;
        for (int i = 0; i < c.Records.Count; i++)
        {
            if (!PrefabClasses.IsResolveElement(c.Records[i].ClassRaw)) continue;
            Assert.NotNull(tx.ElementText(i));                       // every element's text resolves (prototype: 174/175)
            if (c.Records[i].ClassRaw == PrefabClasses.PstringElement) pool++;
        }
        Assert.True(pool > 0);
    }

    [Fact]
    public void DecodeKnownPrefabs()
    {
        var (c, _, _) = Load("dltb", "common_prefabs_pc.rpack");
        var doc = PrefabDecoder.Decode(c);
        Assert.Empty(doc.Warnings);
        Assert.Equal(7_449, doc.Prefabs.Count);
        Assert.All(doc.Prefabs, p => Assert.Equal("CEntity", p.BaseClass));
        var comps = doc.Prefabs.SelectMany(p => p.Components).ToList();
        Assert.Equal(12_071, comps.Count);
        Assert.Equal(9_781, comps.Count(x => x.Entity is { EntityPrefab: >= 0 }));
        Assert.Equal((2_967, 7_535), (doc.AllValues.Count(), doc.AllValues.Sum(v => v.Entries.Count)));
        Assert.Equal(3_031, doc.AllValues.SelectMany(v => v.Entries).Count(v => v.EngineKey));

        var p = doc.Find("abandonedvehiclereplicator")!;
        Assert.Equal(6, p.Index);
        Assert.Equal((byte)1, p.DomFormat);
        Assert.Equal(["m_IdsBuffer", "m_CargoBuffer", "m_ObjectName"], p.Fields.Select(f => f.Name));
        Assert.All(p.Fields, f => Assert.Equal(6, f.OwnerPrefab));
        Assert.Equal("CoIGSObjectProxy::AbandonedVehicleReplicator::m_ObjectName", Assert.Single(p.Fields[2].Destinations!).Field);
        Assert.Equal([(PrefabClasses.GameObjectProxy, "CoIGSObjectProxy", 1u), (PrefabClasses.Replicator, "CoReplicator", 2u)],
                     p.Components.Select(x => (x.ClassId, x.ComponentClass, x.Pcid)));
        Assert.Empty(p.Entities);
        var v = Assert.Single(p.Components[0].ProxyValues!.Entries);
        Assert.Equal(("CoIGSObjectProxy::m_ControlEntityActivity", true, 1ul), (v.Key, v.EngineKey, v.Payload));
        Assert.Equal(["m_IdsBuffer", "m_CargoBuffer", "m_ObjectName"], p.VirtualFields.Select(f => f.Name));

        var bg = doc.Find("_previewbackground")!;
        var e = Assert.Single(bg.Entities);
        Assert.Equal(("mesh", "Meshes;menu_player_ft", "CEntity"), (e.Entity!.PrefabName, e.Entity.PresetNames, e.ComponentClass));
        Assert.Equal("mesh", doc.Prefabs[e.Entity.EntityPrefab].Name);
        Assert.Equal(0.324673f, e.Xform!.Scale.X, 5);
        Assert.Equal(-1L, e.Entity.ForcedGuid);

        Assert.Equal(207, doc.PresetSets.Count);
        Assert.Equal(75_892, doc.PresetSets.Sum(s => s.Groups.Sum(g => g.Presets.Count)));
        Assert.True(doc.Coverage.TypedFraction > 0.85);
    }

    [Fact]
    public void RenameAndDuplicateOnCommon()
    {
        var (c, _, _) = Load("dltb", "common_prefabs_pc.rpack");
        var r = c.Clone();
        PrefabEdits.Rename(r, 6, "abandonedvehiclereplicator_renamed");
        var back = Reparse(r);
        Assert.Equal((c.Records.Count + 1, c.Slots.Count + 1), (back.Records.Count, back.Slots.Count));
        var changed = Enumerable.Range(0, c.Primary.Length).Where(i => c.Primary[i] != back.Primary[i]).ToList();
        Assert.NotEmpty(changed);
        Assert.All(changed, i => Assert.InRange(i, 6 * 0x260 + 0x10, 6 * 0x260 + 0x17));   // only the name slot word
        Assert.Equal("abandonedvehiclereplicator_renamed", new PrefabText(back).PstringAt(false, 6 * 0x260 + 0x10));

        var d = c.Clone();
        var res = PrefabEdits.Duplicate(d, 6, "abandonedvehiclereplicator_copy");
        back = Reparse(d);
        Assert.Equal((7_450u, 7_450), (back.PrefabCount, res.NewIndex + 1));
        var doc = PrefabDecoder.Decode(back);
        var copy = doc.Prefabs[res.NewIndex];
        var orig = doc.Find("abandonedvehiclereplicator")!;
        Assert.Equal("abandonedvehiclereplicator_copy", copy.Name);
        Assert.Equal(orig.Fields.Select(f => (f.Name, f.ClassId)), copy.Fields.Select(f => (f.Name, f.ClassId)));
        Assert.Equal(orig.Components.Select(x => (x.ClassId, x.Pcid, x.ComponentClass)), copy.Components.Select(x => (x.ClassId, x.Pcid, x.ComponentClass)));
        Assert.All(copy.Components, x => Assert.Equal(res.NewIndex, x.OwnerPrefab));
        Assert.All(orig.Components, x => Assert.Equal(orig.Index, x.OwnerPrefab));
        Assert.DoesNotContain(doc.PresetSets, s => s.ClassPrefab == res.NewIndex);   // presets are not duplicated
    }

    [Fact]
    public void Dl2PacksRoundTripDecodeButEditsAreRefused()
    {
        var install = Installs.Require("dl2");
        var path = install.Rpacks().FirstOrDefault(p => Path.GetFileName(p).Equals("menu_level_persistent_pc.rpack", StringComparison.OrdinalIgnoreCase));
        if (path is null) Assert.Skip("menu_level_persistent_pc not in the dl2 install");
        using var pack = RpackFile.Open(path);
        var (img, meta) = PrefabContainer.ReadParts(pack, Assert.Single(PrefabContainer.ResourcesIn(pack)));
        var c = PrefabContainer.Parse(img, meta);
        Assert.Equal(meta, c.EncodeMetadata());
        Assert.Contains("Dying Light 2", c.LayoutProblem());                 // validate and edits keep the DLTB rules
        var doc = PrefabDecoder.Decode(c);                                   // decode reads the DL2 layout
        Assert.Same(PrefabLayout.Dl2, doc.Layout);
        Assert.Equal(2, doc.Prefabs.Count);
        Assert.Empty(doc.Warnings);
        Assert.Throws<PrefabFormatException>(() => PrefabEdits.Rename(c, 0, "x"));
    }

    // ---- synthetic --------------------------------------------------------------------------------------------

    /// <summary>
    /// Two prefab roots ("alpha", "beta") with their names as pstring resolve elements in the secondary image, laid out
    /// as the shipped writer does: roots first at a 0x260 stride, reverse secondary records last, slots
    /// [direct kind 14][indirect kind 9].
    /// </summary>
    internal static PrefabContainer Synthetic()
    {
        var primary = new byte[2 * 0x260];
        var sec = new List<byte>();
        var records = new List<Record> { new(0, PrefabClasses.Prefab, 1), new(0x260, PrefabClasses.Prefab, 1) };
        var direct = new List<Slot>();
        var indirect = new List<Slot>();
        foreach (var (name, root) in new[] { ("alpha", 0), ("beta", 0x260) })
        {
            int e = sec.Count;
            var text = Encoding.UTF8.GetBytes(name);
            var el = new byte[(0x18 + text.Length + 1 + 7) / 8 * 8];
            BinaryPrimitives.WriteUInt64LittleEndian(el.AsSpan(8), (0x2100UL << 48) | (uint)(e + 0x18 + 1));
            BinaryPrimitives.WriteUInt32LittleEndian(el.AsSpan(0x10), (uint)text.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(el.AsSpan(0x14), (uint)text.Length);
            text.CopyTo(el, 0x18);
            sec.AddRange(el);
            records.Add(new Record((uint)e, PrefabClasses.PstringElement, PrefabContainer.RecReverse | PrefabContainer.RecInSecondary | 1));
            direct.Add(new Slot((uint)(e + 8), 14));
            BinaryPrimitives.WriteUInt64LittleEndian(primary.AsSpan(root + 0x10), (ulong)e + 1);
            indirect.Add(new Slot((uint)(root + 0x10), 9));
            primary[root + 0x258] = 2;
        }
        var c = new PrefabContainer(2, 0, 0, 0, records, [.. direct, .. indirect], primary, [.. sec]);
        c.Sync();
        return c;
    }

    private static PrefabContainer Reparse(PrefabContainer c)
    {
        c.Validate();
        var meta = c.EncodeMetadata();
        var back = PrefabContainer.Parse(c.EncodePrimary(), meta);
        back.Validate();
        Assert.Equal(meta, back.EncodeMetadata());
        return back;
    }

    [Fact]
    public void SyntheticRoundTripAndDecode()
    {
        var c = Synthetic();
        Assert.Empty(c.Problems());
        var back = Reparse(c);
        Assert.Equal(0x80000002u, back.ObjectCountRaw);
        Assert.Equal((uint)back.Primary.Length, back.PrimarySizeField);
        var doc = PrefabDecoder.Decode(back);
        Assert.Equal(["alpha", "beta"], doc.Prefabs.Select(p => p.Name));
        Assert.Equal(4, doc.Coverage.Typed);                         // two name slots + their two text slots
        Assert.Equal(1.0, doc.Coverage.TypedFraction);
    }

    private static void Refused(PrefabContainer c, string expected)
    {
        var ex = Assert.Throws<PrefabFormatException>(c.Validate);
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void ValidateRefusesRootNotFirst()
    {
        var c = Synthetic();
        (c.Records[1], c.Records[2]) = (c.Records[2], c.Records[1]);   // an element record where root 1 must be
        Refused(c, "root 1: class 0xB1000000, expected 0xC0000033");
    }

    [Fact]
    public void ValidateRefusesDirectSlotAfterIndirect()
    {
        var c = Synthetic();
        var s = c.Slots[0];
        c.Slots.RemoveAt(0);
        c.Slots.Add(s);
        Refused(c, "direct kind 14 after the first indirect slot");
    }

    [Fact]
    public void ValidateRefusesKind8()
    {
        var c = Synthetic();
        BinaryPrimitives.WriteUInt64LittleEndian(c.Primary.AsSpan(0x20), 1);   // a persistent pointer into the secondary
        c.Slots.Insert(0, new Slot(0x20, 8));
        Refused(c, "kind 8 is not allowed");
    }

    [Fact]
    public void ValidateRefusesObjectCountMismatch()
    {
        var c = Synthetic();
        c.ObjectCountRaw = PrefabContainer.SecondaryPresent | 3;
        Refused(c, "object_count 3 != nPrefab 2 + nPresets 0");
    }

    [Fact]
    public void ValidateRefusesOtherMistakes()
    {
        var c = Synthetic();
        c.Primary = [.. c.Primary, .. new byte[0x40]];
        c.PrimarySizeField = (uint)c.Primary.Length;
        c.Records.Add(new Record(0x4C0, PrefabClasses.PrefabComponent, 1));   // a forward record after the reverse ones
        Refused(c, "forward record after the first reverse record");

        c = Synthetic();
        BinaryPrimitives.WriteUInt64LittleEndian(c.Primary.AsSpan(0x28), 0);
        c.Slots.Insert(0, new Slot(0x28, 0));
        Refused(c, "null word");

        c = Synthetic();
        c.Primary[0x259] = 0x80;
        Refused(c, "+0x259 flags 0x80");

        c = Synthetic();
        c.PrimarySizeField += 16;
        Refused(c, "primary_size");

        c = Synthetic();
        c.Records[2] = c.Records[2] with { FlagsRaw = PrefabContainer.RecInSecondary | 1 };
        Refused(c, "reverse bit 0 != secondary bit 1");
    }

    [Fact]
    public void RenameAndDuplicateSynthetic()
    {
        var c = Synthetic();
        PrefabEdits.Rename(c, 0, "gamma");
        var back = Reparse(c);
        Assert.Equal(["gamma", "beta"], PrefabDecoder.Decode(back).Prefabs.Select(p => p.Name));
        Assert.Equal((5, 5), (back.Records.Count, back.Slots.Count));

        var res = PrefabEdits.Duplicate(back, 1, "beta_copy");
        var again = Reparse(back);
        Assert.Equal((2, 3u), (res.NewIndex, again.PrefabCount));
        Assert.Equal(["gamma", "beta", "beta_copy"], PrefabDecoder.Decode(again).Prefabs.Select(p => p.Name));
        Assert.Equal(0x260u * 2, again.Records[2].Offset);

        Assert.Throws<PrefabFormatException>(() => PrefabEdits.Rename(again, 0, "beta"));        // names are unique
        Assert.Throws<PrefabFormatException>(() => PrefabEdits.Rename(again, 0, "Upper"));       // and lowercase
        Assert.Throws<PrefabFormatException>(() => PrefabEdits.Duplicate(again, 0, "x.prefab")); // without extension
    }
}
