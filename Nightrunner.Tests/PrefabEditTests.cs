using System.Buffers.Binary;
using System.Text;
using Nightrunner.Core.Mesh.ClassReader;
using Nightrunner.Core.Prefab;
using Nightrunner.Core.Project;
using Nightrunner.Core.Rpack;
using Record = Nightrunner.Core.Mesh.ClassReader.Record;

namespace Nightrunner.Tests;

// Value edits of a Prefabs resource (transform, text, scalar, m_SelfActive, entity removal), the project prefab item,
// and the placement mechanisms (m_XformComponent, virtual-field overrides, the vehicle rig). Edits are structural
// only: nothing they produce has been loaded in the game.
public class PrefabEditTests
{
    // ---- synthetic --------------------------------------------------------------------------------------------

    /// <summary>
    /// One prefab "alpha" with three components, laid out as the shipped writer does: a hierarchy (pcid 1), a
    /// MeshRender on it (pcid 2; m_XformComponent 1; a value blob with <c>ICoMeshRender::m_MeshName</c> = "a.msh") and a
    /// child entity (pcid 3) at (1, 2, 3). The component vector's storage is a pointer-array record; the secondary
    /// image holds the name and mesh pstrings and two field elements (m_MeshName, m_SelfActive).
    /// </summary>
    internal static PrefabContainer Synthetic(bool selfActiveElement = true)
    {
        var p = new byte[0x4A0];
        var sec = new List<byte>();
        var records = new List<Record>
        {
            new(0, PrefabClasses.Prefab, 1), new(0x260, PrefabClasses.PointerArray, 3), new(0x280, PrefabClasses.HierarchyComponent, 1),
            new(0x2F8, PrefabClasses.MeshRender, 1), new(0x370, PrefabClasses.EntityComponent, 1),
        };
        var direct = new List<Slot>();
        var secSlots = new List<Slot>();
        var indirect = new List<Slot>();
        void Ptr(int at, long target, byte kind = 0, ushort tag = 0)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(at), (ulong)(target + 1) | (ulong)tag << 48);
            (kind == 9 ? indirect : direct).Add(new Slot((uint)at, kind));
        }
        int Text(string s)          // {len, cap} text NUL pad8 in the secondary; returns the text offset
        {
            var b = Encoding.UTF8.GetBytes(s);
            int at = sec.Count;
            var block = new byte[(8 + b.Length + 1 + 7) / 8 * 8];
            BinaryPrimitives.WriteUInt32LittleEndian(block, (uint)b.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), (uint)b.Length);
            b.CopyTo(block, 8);
            sec.AddRange(block);
            return at + 8;
        }
        void StringBase(int at, int text)
        {
            var w = BitConverter.GetBytes((0x2100UL << 48) | (uint)(text + 1));
            for (int i = 0; i < 8; i++) sec[at + i] = w[i];
            secSlots.Add(new Slot((uint)at, 14));
        }
        int Pstring(string s)
        {
            int e = sec.Count;
            sec.AddRange(new byte[0x10]);
            records.Add(new Record((uint)e, PrefabClasses.PstringElement, PrefabContainer.RecReverse | PrefabContainer.RecInSecondary | 1));
            StringBase(e + 8, Text(s));
            return e;
        }
        int Field(string cls, string field)
        {
            int e = sec.Count;
            sec.AddRange(new byte[0x20]);
            records.Add(new Record((uint)e, PrefabClasses.FieldElement, PrefabContainer.RecReverse | PrefabContainer.RecInSecondary | 1));
            StringBase(e + 8, Text(cls));
            StringBase(e + 0x10, Text(field));
            return e;
        }
        int name = Pstring("alpha"), mesh = Pstring("a.msh");
        int meshField = Field("ICoMeshRender", "m_MeshName");
        if (selfActiveElement) Field(PrefabEdits.SelfActiveClass, PrefabEdits.SelfActiveField);

        Ptr(0x10, name, 9);
        p[0x258] = 2;
        Ptr(0x1A8, 0x260, 2, 0x0095);                              // m_Components: heap vector, allocator tag 0x95
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0x1B0), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0x1B4), 3);
        int[] comps = [0x280, 0x2F8, 0x370];
        for (int i = 0; i < 3; i++)
        {
            Ptr(0x260 + 8 * i, comps[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(comps[i] + 8), 3);                 // inline pcid form
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(comps[i] + 0xC), (uint)(i + 1));
            Ptr(comps[i] + 0x18, 0);                                                               // owner prefab
        }
        foreach (int o in new[] { 0x280, 0x370 })
            for (int k = 0; k < 3; k++) BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(o + 0x58 + 4 * k), 1f);
        for (int k = 0; k < 3; k++) BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(0x370 + 0x40 + 4 * k), k + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0x2F8 + 0x40), 1);                     // m_XformComponent
        int blob = 0x2F8 + 0x50;
        Ptr(0x2F8 + 0x30, blob);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(blob), 0x28);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(blob + 4), 8);
        Ptr(blob + 8, meshField, 9);
        Ptr(blob + 0x10, mesh, 9);
        p.AsSpan(blob + 0x18, 16).Fill(0xFF);
        BinaryPrimitives.WriteInt64LittleEndian(p.AsSpan(0x370 + 0x120), -1);

        var c = new PrefabContainer(1, 0, 0, 0, records, [.. direct, .. secSlots, .. indirect], p, [.. sec]);
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

    private static int[] Changed(byte[] a, byte[] b) => Enumerable.Range(0, Math.Min(a.Length, b.Length)).Where(i => a[i] != b[i]).ToArray();

    [Fact]
    public void SyntheticDecodesWithXformPointer()
    {
        var c = Synthetic();
        Assert.Empty(c.Problems());
        var root = PrefabDecoder.Decode(Reparse(c)).Prefabs.Single();
        Assert.Equal("alpha", root.Name);
        Assert.Equal([1u, 2u, 3u], root.Components.Select(x => x.Pcid));
        Assert.Equal(1u, root.Components[1].XformComponent);
        Assert.Null(root.Components[0].XformComponent);                              // hierarchies are not xform-pointer classes
        Assert.Equal("a.msh", root.Components[1].Values!.Entries.Single().Text);
        Assert.Equal(new Vec3(1, 2, 3), root.Components[2].Xform!.Translate);
    }

    [Fact]
    public void TransformEditWritesOnlyItsFloats()
    {
        var c = Synthetic();
        var before = (byte[])c.Primary.Clone();
        PrefabEdits.SetTransform(c, 0, 1, new PrefabXform(new Vec3(0.5f, 0, -2), new Vec3(0, 90, 0), new Vec3(1, 2, 1)));
        var back = Reparse(c);
        Assert.All(Changed(before, back.Primary), i => Assert.InRange(i, 0x280 + 0x40, 0x280 + 0x63));
        var x = PrefabDecoder.Decode(back).Prefabs[0].Components[0].Xform!;
        Assert.Equal((new Vec3(0.5f, 0, -2), new Vec3(0, 90, 0), new Vec3(1, 2, 1)), (x.Translate, x.Rotate, x.Scale));
        Assert.Throws<PrefabFormatException>(() => PrefabEdits.SetTransform(c, 0, 2, x));          // a MeshRender has none
    }

    [Fact]
    public void TextEditRetargetsTheSlotWord()
    {
        var c = Synthetic();
        var before = (byte[])c.Primary.Clone();
        int recs = c.Records.Count, slots = c.Slots.Count;
        PrefabEdits.SetText(c, 0, 2, "m_MeshName", "a_much_longer_mesh_name_than_before.msh");
        var back = Reparse(c);
        Assert.Equal((recs + 1, slots + 1), (back.Records.Count, back.Slots.Count));     // one appended pstring element
        Assert.All(Changed(before, back.Primary), i => Assert.InRange(i, 0x348 + 0x10, 0x348 + 0x17));
        Assert.Equal("a_much_longer_mesh_name_than_before.msh", PrefabDecoder.Decode(back).Prefabs[0].Components[1].Values!.Entries[0].Text);

        PrefabEdits.SetText(back, 0, 2, "m_MeshName", "alpha");                          // an existing element is shared
        var again = Reparse(back);
        Assert.Equal(back.Records.Count, again.Records.Count);
        Assert.Equal("alpha", PrefabDecoder.Decode(again).Prefabs[0].Components[1].Values!.Entries[0].Text);

        Assert.Throws<PrefabFormatException>(() => PrefabEdits.SetText(c, 0, 2, "m_MeshName", ""));
        Assert.Throws<PrefabFormatException>(() => PrefabEdits.SetText(c, 0, 2, "m_SkinName", "x"));
        Assert.Throws<PrefabFormatException>(() => PrefabEdits.SetScalar(c, 0, 2, "m_MeshName", 1));
    }

    [Fact]
    public void ActiveIsInsertedThenToggled()
    {
        var c = Synthetic();
        var before = (byte[])c.Primary.Clone();
        PrefabEdits.SetActive(c, 0, 2, false);
        var back = Reparse(c);
        Assert.Equal(before.Length + 16, back.Primary.Length);
        int term = 0x348 + 0x18;
        // before the insertion only the blob's total_size and the pointer to the entity (now 16 bytes later) change
        Assert.All(Changed(before.AsSpan(0, term).ToArray(), back.Primary.AsSpan(0, term).ToArray()),
                   i => Assert.True(i is >= 0x270 and < 0x278 or >= 0x348 and < 0x34C, $"byte 0x{i:X}"));
        Assert.Equal(before.AsSpan(term).ToArray(), back.Primary.AsSpan(term + 16, before.Length - term).ToArray());   // shifted verbatim
        var doc = PrefabDecoder.Decode(back);
        var mr = doc.Prefabs[0].Components[1];
        Assert.False(mr.SelfActive);
        Assert.Equal(["ICoMeshRender::m_MeshName", "cbs::CComponent::m_SelfActive"], mr.Values!.Entries.Select(v => v.Key));
        Assert.Equal(0x38u, mr.Values.TotalSize);
        Assert.Equal(new Vec3(1, 2, 3), doc.Prefabs[0].Components[2].Xform!.Translate);

        var mid = (byte[])back.Primary.Clone();
        PrefabEdits.SetActive(back, 0, 2, true);                                          // now it exists: one byte
        var again = Reparse(back);
        Assert.Single(Changed(mid, again.Primary));
        Assert.True(PrefabDecoder.Decode(again).Prefabs[0].Components[1].SelfActive);
        Assert.Throws<PrefabFormatException>(() => PrefabEdits.SetActive(again, 0, 1, false));  // a hierarchy with no blob
    }

    [Fact]
    public void ActiveInsertAppendsTheFieldElementWhenMissing()
    {
        var c = Synthetic(selfActiveElement: false);
        int recs = c.Records.Count, slots = c.Slots.Count;
        PrefabEdits.SetActive(c, 0, 2, false);
        var back = Reparse(c);
        Assert.Equal((recs + 1, slots + 3), (back.Records.Count, back.Slots.Count));   // element + its two texts + the key
        var last = back.Records[^1];
        Assert.Equal(PrefabClasses.FieldElement, last.ClassRaw);
        Assert.Equal("cbs::CComponent::m_SelfActive", new PrefabText(back).ElementText(back.Records.Count - 1));
        Assert.False(PrefabDecoder.Decode(back).Prefabs[0].Components[1].SelfActive);
    }

    [Fact]
    public void EntityRemovalShrinksTheVector()
    {
        var c = Synthetic();
        Assert.Throws<PrefabFormatException>(() => PrefabEdits.RemoveEntity(c, 0, 2));    // not an entity
        PrefabEdits.RemoveEntity(c, 0, 3);
        var back = Reparse(c);
        var root = PrefabDecoder.Decode(back).Prefabs[0];
        Assert.Equal([1u, 2u], root.Components.Select(x => x.Pcid));
        Assert.Equal(2, back.Records[1].Count);                                         // the pointer array record
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(back.Primary.AsSpan(0x270)));
    }

    [Fact]
    public void ApplyChecksAndNamesTheRefusedEdit()
    {
        var c = Synthetic();
        PrefabEditOp[] ops =
        [
            new("transform", "alpha", 3) { Translate = new Vec3(4, 5, 6) },
            new("text", "alpha", 2) { Field = "m_MeshName", Text = "b.msh" },
            new("active", "alpha", 2) { Active = false },
        ];
        PrefabEdits.Apply(c, ops);
        var doc = PrefabDecoder.Decode(Reparse(c));
        Assert.All(ops, op => Assert.Null(PrefabEdits.Check(doc, op)));
        Assert.Equal(new Vec3(1, 1, 1), doc.Prefabs[0].Components[2].Xform!.Scale);       // untouched parts of the transform

        var e = Assert.Throws<PrefabFormatException>(() => PrefabEdits.Apply(Synthetic(), [new("scalar", "alpha", 2) { Field = "m_MeshName", Value = 1 }]));
        Assert.StartsWith("alpha #2: m_MeshName = 1:", e.Message);
        foreach (var op in ops)
            Assert.Equal(op, ProjectAssets.EditOp(ProjectAssets.EditJson(op)), new OpComparer());
    }

    private sealed class OpComparer : IEqualityComparer<PrefabEditOp>
    {
        public bool Equals(PrefabEditOp? a, PrefabEditOp? b) => a is not null && b is not null && a.ToString() == b.ToString() && a.Field == b.Field;
        public int GetHashCode(PrefabEditOp o) => o.ToString().GetHashCode();
    }

    private sealed class FakeRig(Dictionary<string, string> scripts) : IPrefabRigSource
    {
        public double[]? Element(string mesh, string element, out string? why) { why = "none"; return null; }
        public string? Script(string name) => scripts.GetValueOrDefault(name);
    }

    [Fact]
    public void VehicleScriptParamsFollowLoadParams()
    {
        var rig = new FakeRig(new()
        {
            ["car.scr"] = "Seat(0, \"DDriver\")\nLoadParams(\"vis.scr\")\n// ParamString(\"mesh_wheel_fl_helper\", \"commented\")\nLoadParams(\"missing.scr\")",
            ["vis.scr"] = "ParamString(\"mesh_wheel_fl_helper\", \"Bone_wheel_fl\")\n/* ParamString(\"mesh_wheel_fr_helper\", \"x\") */ ParamFloat(\"a\", 1)",
        });
        var notes = new List<string>();
        var p = PrefabVehicleRig.Params(rig, "car.scr", notes)!;
        Assert.Equal("Bone_wheel_fl", p["mesh_wheel_fl_helper"]);
        Assert.False(p.ContainsKey("mesh_wheel_fr_helper"));
        Assert.Contains("script missing.scr not found", notes);
        Assert.Null(PrefabVehicleRig.Params(rig, "none.scr"));
        Assert.Equal(("mesh_wheel_fr_helper", "axis_fr"), PrefabVehicleRig.Slots["m_WheelFRHierarchy"]);
    }

    // ---- install-backed ---------------------------------------------------------------------------------------

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static RpackCatalog? _dltb;

    private static async Task<RpackCatalog> Dltb()
    {
        var install = Installs.Require("dltb");
        await Gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            if (_dltb is null)
            {
                var c = new RpackCatalog();
                await c.LoadAsync(install.Rpacks(), install.Assets, TestContext.Current.CancellationToken);
                _dltb = c;
            }
            return _dltb;
        }
        finally { Gate.Release(); }
    }

    [Fact]
    public async Task TruckWheelsFollowTheVehicleRig()
    {
        var catalog = await Dltb();
        var install = Installs.Require("dltb");
        var prefabs = PrefabCatalog.Build(catalog);
        var truck = prefabs.Find("dlc_ft_vehicle_truck")!;
        using var rig = new PrefabRigSource(catalog, install.Paks());

        var notes = new List<string>();
        var bare = PrefabPlacement.Meshes(prefabs, truck, notes, rig);
        Assert.Contains(notes, n => n.Contains("no vehicle script"));                  // the bare prefab names no script: refused, not guessed
        Assert.DoesNotContain(bare, m => m.Placed == "rig");

        var rigNotes = new List<string>();
        var baron = PrefabPlacement.Meshes(prefabs, truck, rigNotes, rig, "Preset;Vehicle_Baron");
        var wheels = baron.Where(m => m.Mesh.Contains("_wheel_")).ToList();
        Assert.Equal(4, wheels.Count);
        Assert.All(wheels, w => Assert.Equal("rig", w.Placed));
        // the pickup model's bone_wheel_* elements (vehicle_pickup_vis_params.scr names Bone_wheel_*)
        Assert.Equal([(-0.717, -1.452), (-0.717, 1.708), (0.717, -1.452), (0.717, 1.708)],
                     wheels.Select(w => (Math.Round(w.Transform[3], 3), Math.Round(w.Transform[11], 3))).Order());
        Assert.Contains(baron, m => m.Mesh == "dlc_ft_veh_drivable_pickup_b_base_ch");   // the preset's MeshName reaches pcid 27
        // doors hang on their hinge elements (Bone_door_*); the closed pose is the door mesh's own child entity
        var door = baron.First(m => m.Mesh == "dlc_ft_veh_drivable_pickup_b_door_fl");
        Assert.Equal("rig", door.Placed);
        Assert.Equal((0.979, 1.18, 0.919), (Math.Round(door.Transform[3], 3), Math.Round(door.Transform[7], 3), Math.Round(door.Transform[11], 3)));
        // the Default skin preset's PrefabsToLoadStrings: body panels and lights
        foreach (var part in new[] { "hood", "tailgate", "fender_fl", "bumper_f", "bumper_r", "light_front" })
            Assert.Contains(baron, m => m.Mesh == "dlc_ft_veh_drivable_pickup_b_" + part);
    }

    [Fact]
    public async Task VirtualFieldsCarryInstanceMeshes()
    {
        var catalog = await Dltb();
        var prefabs = PrefabCatalog.Build(catalog);
        // prefabs that instance the generic "mesh" prefab name their mesh through its MeshName virtual field
        static bool Plain(PrefabComponent e) => (e.Entity!.PrefabName ?? e.Entity.EntityPrefabClass) == "mesh" && e.SelfActive != false &&
            e.Values?.Entries.Any(v => v.Key == "mesh::MeshName" && v.Text is { Length: > 0 } t && t != "nomesh.msh") == true;
        var user = prefabs.Prefabs.First(p => p.Wins && p.Root.Entities.Any(Plain));
        var ent = user.Root.Entities.First(Plain);
        string want = ent.Values!.Entries.First(v => v.Key == "mesh::MeshName").Text![..^4];
        var meshes = PrefabPlacement.Meshes(prefabs, user);
        Assert.Contains(meshes, m => m.Mesh.Equals(want, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EditsOnCommonPrefabsKeepEverythingElse()
    {
        var catalog = await Dltb();
        var entry = catalog.Packs.First(p => Path.GetFileName(p.Path).Equals("common_prefabs_pc.rpack", StringComparison.OrdinalIgnoreCase));
        var c = PrefabContainer.Read(entry.Pack!, PrefabContainer.ResourcesIn(entry.Pack!).Single());
        var original = PrefabDecoder.Decode(c);
        var e = c.Clone();
        int truck = PrefabEdits.Index(e, "dlc_ft_vehicle_truck");
        PrefabEditOp[] ops =
        [
            new("transform", "dlc_ft_vehicle_truck", 502) { Translate = new Vec3(0.7f, 0.4f, 1.7f) },
            new("text", "dlc_ft_vehicle_truck", 492) { Field = "m_MeshName", Text = "dlc_ft_veh_drivable_pickup_a_wheel_r.msh" },
            new("active", "dlc_ft_vehicle_truck", 865) { Active = false },
            new("active", "dlc_ft_vehicle_truck", 492) { Active = false },                  // inserted: the image grows by 16
            new("scalar", "dlc_ft_vehicle_truck", 747) { Field = "m_ActiveRadius", Value = 25 },
        ];
        PrefabEdits.Apply(e, ops);
        var back = Reparse(e);
        Assert.Equal(c.Primary.Length + 16, back.Primary.Length);
        var doc = PrefabDecoder.Decode(back);
        Assert.All(ops, op => Assert.Null(PrefabEdits.Check(doc, op)));
        // every other prefab decodes exactly as before (the insertion relocated what follows it)
        for (int i = 0; i < original.Prefabs.Count; i++)
        {
            if (i == truck) continue;
            var (a, b) = (original.Prefabs[i], doc.Prefabs[i]);
            Assert.Equal(a.Name, b.Name);
            Assert.Equal(a.Components.Select(x => (x.Pcid, x.ClassId, x.ComponentClass, x.Xform, x.Entity?.PrefabName, x.Values?.Entries.Count)),
                         b.Components.Select(x => (x.Pcid, x.ClassId, x.ComponentClass, x.Xform, x.Entity?.PrefabName, x.Values?.Entries.Count)));
        }
        var ex = Assert.Throws<PrefabFormatException>(() => PrefabEdits.RemoveEntity(c.Clone(), truck, 921));
        Assert.Contains("m_SyncSlotFront", ex.Message);
        Assert.Throws<PrefabFormatException>(() => PrefabEdits.SetScalar(c.Clone(), truck, 634, "m_Power", 2));   // engine-keyed: width unknown
    }

    [Fact]
    public async Task ProjectBuildsAnEditedPrefabsResource()
    {
        var catalog = await Dltb();
        var install = Installs.Require("dltb");
        // the smallest prefab-bearing pack keeps the written rpack small
        var (entry, c, target) = catalog.IndexedPacks.Where(p => PrefabContainer.ResourcesIn(p.Pack!).Length == 1).OrderBy(p => p.FileSize)
            .Select(p => (p, PrefabContainer.Read(p.Pack!, PrefabContainer.ResourcesIn(p.Pack!).Single())))
            .Where(x => x.Item2.LayoutProblem() is null)
            .Select(x => (x.p, x.Item2, PrefabDecoder.Decode(x.Item2).Prefabs.FirstOrDefault(r => r.Components.Any(y => y.Xform is not null))))
            .First(x => x.Item3 is not null);
        var comp = target!.Components.First(x => x.Xform is not null && target.Components.Count(y => y.Pcid == x.Pcid) == 1);
        using var tmp = new TempDir();
        var project = ModProject.Create(tmp.File("mod"), "mod", "dltb");
        var op = new PrefabEditOp("transform", target.Name!, comp.Pcid) { Translate = new Vec3(1, 2, 3) };
        Assert.Throws<ProjectException>(() => ProjectAssets.AddPrefabEdits(project, catalog, entry.Label,
                                                                           [new PrefabEditOp("transform", target.Name!, 999_999)]));
        var item = ProjectAssets.AddPrefabEdits(project, catalog, entry.Label, [op]);
        Assert.Single(ProjectAssets.Scan(project).Prefabs);
        Assert.Single(item.Edits);

        var result = ProjectBuild.Build(project, new BuildEnv(install, catalog, null, _ => throw new InvalidOperationException(),
                                                             "assets_2_pc.rpack", "data9.pak"), ct: TestContext.Current.CancellationToken);
        Assert.True(result.Verified, result.Verdict);
        using var pack = RpackFile.Open(result.RpackPath!);
        int i = Assert.Single(Enumerable.Range(0, pack.Count));
        Assert.Equal("Prefabs", pack.Name(i));
        var built = PrefabContainer.Read(pack, i);
        var doc = PrefabDecoder.Decode(built);
        Assert.Null(PrefabEdits.Check(doc, op));
        Assert.Equal(c.Primary.Length, built.Primary.Length);
        Assert.All(Changed(c.Primary, built.Primary), k => Assert.InRange(k, comp.Offset + 0x40, comp.Offset + 0x4B));   // the translate floats only
    }
}
