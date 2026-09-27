using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Nightrunner.Core.Mesh.ClassReader;
using Nightrunner.Core.Model;
using Nightrunner.Core.Prefab;
using Nightrunner.Core.Rpack;
using Record = Nightrunner.Core.Mesh.ClassReader.Record;

namespace Nightrunner.Tests;

// Text prefabs (JSON, MessagePack, YAML pak members) into the prefab model, the Dying Light 2 binary layout, and the
// catalog that holds both. Synthetic tests always run; install-backed ones skip without an install.
public class PrefabTextTests
{
    // ---- synthetic: JSON -----------------------------------------------------------------------------------------

    private const string Json = """
        {
          "__filename__": {
            "Version": 4,
            "RuntimeData": {
              "Components": [
                {
                  "Class": "CEntity",
                  "PrefabFieldsNative": {
                    "m_EntityComponentsExtents": [69, [0.0, 0.5, 0.0, 1.0, 1.5, 2.0]],
                    "m_Name": [12, "Seat"],
                    "m_ParentComponent": [6, 3],
                    "m_PcId": [6, 2],
                    "m_PrefabName": [12, "mesh"],
                    "m_PresetNames": [12, "Meshes;chair_a"],
                    "m_Rotate": [14, [0.0, 90.0, 0.0]],
                    "m_Translate": [14, [1.5, 0.0, -2.0]]
                  },
                  "Fields": { "SkinName": "red", "m_SelfActive": "1" }
                },
                {
                  "Class": "CoHierarchy",
                  "PrefabFieldsNative": { "m_PcId": [6, 3], "m_RootXform": [11, true], "m_Translate": [14, [0.0, 1.0, 0.0]] }
                },
                {
                  "Class": "CoMeshRender",
                  "PrefabFieldsNative": { "m_PcId": [6, 4], "m_XformComponent": [6, 3] },
                  "Fields": { "m_MeshName": "table_a.msh" },
                  "EmbeddedObject": { "Class": "A", "DestinationField": "m_X" },
                  "EmbeddedObject": { "Class": "B", "DestinationField": "m_Y" }
                }
              ],
              "Interface": {
                "VirtualFields": [ { "Name": "SkinName", "Init": "f:EDIT;", "DestinationFields": [ { "Uuid": "2", "Name": "SkinName" } ] } ]
              },
              "Bindings": { "Properties": [ { "src_uuid": "3", "src": "this", "dest_uuid": "4", "dest": "m_Target" } ], "Pipes": [] },
              "PrefabInterfaces": [ "IThing" ],
              "Extents": {}
            },
            "PrefabEditorData": { "DataVersion": "2" }
          },
          "table_part": {
            "RuntimeData": {
              "Components": [ { "Class": "CEntity", "PrefabFields": { "m_PcId": "1", "m_PrefabName": "mesh", "m_Scale": "2.000000 2.000000 2.000000" } } ]
            }
          }
        }
        """;

    private static void AssertModel(PrefabDocument doc)
    {
        Assert.Equal(["table", "table_part"], doc.Prefabs.Select(p => p.Name));
        var p = doc.Prefabs[0];
        Assert.Equal(4, p.TextVersion);
        Assert.Equal(3, p.Components.Count);
        var seat = Assert.Single(p.Entities);
        Assert.True(seat.IsText);
        Assert.Equal((PrefabClasses.EntityComponent, 2u, "CEntity"), (seat.ClassId, seat.Pcid, seat.ComponentClass));
        var e = seat.Entity!;
        Assert.Equal(("Seat", "mesh", "Meshes;chair_a", 3u), (e.Name, e.PrefabName, e.PresetNames, e.ParentPcid));
        Assert.Equal([0f, 0.5f, 0f, 1f, 1.5f, 2f], e.ExtentsA);
        Assert.Equal((new Vec3(1.5f, 0, -2), new Vec3(0, 90, 0), new Vec3(1, 1, 1)), (seat.Xform!.Translate, seat.Xform.Rotate, seat.Xform.Scale));
        Assert.Equal(["SkinName", "m_SelfActive"], seat.Values!.Entries.Select(v => v.Key));
        Assert.True(seat.SelfActive);
        Assert.Equal((ushort)14, seat.NativeFields.Single(v => v.Key == "m_Translate").Type);
        var hier = p.Components[1];
        Assert.Equal(0u, hier.ClassId);
        Assert.Equal(new Vec3(0, 1, 0), hier.Xform!.Translate);
        Assert.Null(hier.HierarchyParent);
        var mesh = p.Components[2];
        Assert.Null(mesh.Xform);
        Assert.Equal(3u, mesh.XformComponent);
        Assert.Equal("table_a.msh", Assert.Single(mesh.Values!.Entries).Text);
        Assert.Equal(["EmbeddedObject", "EmbeddedObject"], mesh.TextOther.Select(kv => kv.Key));   // duplicate keys kept, in order
        Assert.Contains("\"B\"", mesh.TextOther[1].Value);
        var vf = Assert.Single(p.VirtualFields);
        Assert.Equal(("SkinName", "f:EDIT;", "SkinName", 2ul), (vf.Name, vf.Init, vf.Destinations[0].Field, vf.Destinations[0].Pcid));
        Assert.Equal((3u, 4u, "this", "m_Target"), (p.PropertyBindings[0].SourcePcid, p.PropertyBindings[0].TargetPcid, p.PropertyBindings[0].Source, p.PropertyBindings[0].Target));
        Assert.Equal("IThing", Assert.Single(p.Interfaces).Class);
        Assert.Equal(["RuntimeData.Extents", "PrefabEditorData"], p.TextOther.Select(kv => kv.Key));
        var part = doc.Prefabs[1];
        Assert.Null(part.TextVersion);
        Assert.Equal(new Vec3(2, 2, 2), part.Components[0].Xform!.Scale);          // the older PrefabFields text form
        Assert.Equal(1u, part.Components[0].Pcid);
    }

    [Fact]
    public void JsonPrefabReadsIntoTheModel()
    {
        var doc = PrefabTextReader.Read(Encoding.UTF8.GetBytes(Json), "table", "data0.pak", "prefabs/table.prefab");
        Assert.Equal(new PrefabTextSource("data0.pak", "prefabs/table.prefab", PrefabTextFormat.Json, 4), doc.TextSource);
        AssertModel(doc);
        Assert.True(doc.Coverage.Typed > 0 && doc.Coverage.Typed < doc.Coverage.Total);
        Assert.Throws<PrefabFormatException>(() => PrefabTextReader.Read("{\"__filename__\": [1]}"u8, "x"));     // no prefab object
        Assert.Throws<PrefabFormatException>(() => PrefabTextReader.Read("{\"__filename__\": {"u8, "x"));        // malformed
    }

    // ---- synthetic: MessagePack ----------------------------------------------------------------------------------

    /// <summary>The DOM of <paramref name="n"/> as MessagePack (maps keep duplicate keys), with the <c>MsgP</c> magic and a trailing NUL as shipped.</summary>
    private static byte[] Pack(PrefabTextNode n)
    {
        var o = new List<byte>("MsgP"u8.ToArray());
        void Str(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            if (b.Length < 32) o.Add((byte)(0xA0 | b.Length)); else { o.Add(0xD9); o.Add((byte)b.Length); }
            o.AddRange(b);
        }
        void W(PrefabTextNode x)
        {
            switch (x.Kind)
            {
                case PrefabTextKind.Null: o.Add(0xC0); break;
                case PrefabTextKind.Bool: o.Add(x.Text == "true" ? (byte)0xC3 : (byte)0xC2); break;
                case PrefabTextKind.String: Str(x.Text!); break;
                case PrefabTextKind.Number:
                    if (int.TryParse(x.Text, out int i) && i is >= 0 and < 128) o.Add((byte)i);
                    else
                    {
                        o.Add(0xCA);
                        var f = new byte[4];
                        BinaryPrimitives.WriteSingleBigEndian(f, float.Parse(x.Text!, System.Globalization.CultureInfo.InvariantCulture));
                        o.AddRange(f);
                    }
                    break;
                case PrefabTextKind.Array: o.Add((byte)(0x90 | x.Items!.Count)); foreach (var it in x.Items) W(it); break;
                default: o.Add((byte)(0x80 | x.Members!.Count)); foreach (var (k, v) in x.Members) { Str(k); W(v); } break;
            }
        }
        W(n);
        o.Add(0);
        return [.. o];
    }

    [Fact]
    public void MessagePackPrefabReadsLikeJson()
    {
        var bytes = Pack(PrefabTextNode.ParseJson(Encoding.UTF8.GetBytes(Json)));
        Assert.Equal(PrefabTextFormat.MessagePack, PrefabTextReader.Detect(bytes));
        var doc = PrefabTextReader.Read(bytes, "table");
        Assert.Equal(PrefabTextFormat.MessagePack, doc.TextSource!.Format);
        AssertModel(doc);
        bytes[^1] = 7;                                                                  // a non-NUL byte after the root
        Assert.Contains("after the root value", Assert.Throws<PrefabFormatException>(() => PrefabTextReader.Read(bytes, "table")).Message);
        Assert.Contains("0xC4", Assert.Throws<PrefabFormatException>(() => PrefabTextReader.Read([.. "MsgP"u8, 0xC4, 1, 0], "x")).Message);   // bin: refused by name
    }

    // ---- synthetic: YAML -----------------------------------------------------------------------------------------

    [Fact]
    public void YamlPrefabsReadBothShippedForms()
    {
        // the oldest form: document 1 = RuntimeData members, 2 = editor data, 3 = editor nodes
        const string old = "## GENERATED ##\r\n# From X\r\n---\r\nInterface:\r\n  Properties: null\r\n  VirtualFields:\r\n  - Name: m_Hint\r\n" +
                           "    Init: f:EDIT;d:Text displayed when the player\r\n      - only one is used;\r\n    DestinationFields:\r\n    - Uuid: 1\r\n      Name: m_Hint\r\n" +
                           "  - Name: m_End\r\n    Init: 'f:EDIT;d: Defines the ''end''\r\n      offset.;'\r\n    DestinationFields: []\r\nBindings: null\r\n" +
                           "Components:\r\n- Class: CoMeshRender\r\n  PrefabFields:\r\n    m_PcId: 6\r\n    m_PcId: 6\r\n  Fields:\r\n    m_MeshName: chair_folding_a.msh\r\n" +
                           "...\r\n---\r\nGenerated: 1\r\n...\r\n---\r\n- Type: 0\r\n  X: 165.00\r\n...\r\n";
        var doc = PrefabTextReader.Read(Encoding.UTF8.GetBytes(old), "chair");
        Assert.Equal(PrefabTextFormat.Yaml, doc.TextSource!.Format);
        var p = Assert.Single(doc.Prefabs);
        Assert.Equal(["m_Hint", "m_End"], p.VirtualFields.Select(v => v.Name));
        Assert.Equal("f:EDIT;d:Text displayed when the player - only one is used;", p.VirtualFields[0].Init);   // folded plain scalar
        Assert.Equal("f:EDIT;d: Defines the 'end' offset.;", p.VirtualFields[1].Init);                          // folded single-quoted
        Assert.Equal((1ul, "m_Hint"), (p.VirtualFields[0].Destinations[0].Pcid, p.VirtualFields[0].Destinations[0].Field));
        var c = Assert.Single(p.Components);
        Assert.Equal((6u, "chair_folding_a.msh"), (c.Pcid, c.Values!.Entries[0].Text));
        Assert.Equal(2, c.NativeFields.Count);                                                                   // the repeated key stays
        Assert.Equal(["Interface.Properties", "PrefabEditorData", "YamlDocument3"], p.TextOther.Select(kv => kv.Key));   // "Bindings: null" holds nothing

        // the newer form: __filename__ at the top, PrefabFieldsNative as [type, value] sequences with flow sequences
        const string wrapped = "---\n__filename__:\n  Version: 3\n  RuntimeData:\n    Components:\n    - Class: CEntity\n      PrefabFieldsNative:\n" +
                               "        m_PcId:\n        - 6\n        - 1\n        m_PrefabName:\n        - 12\n        - empty\n        m_Translate:\n        - 14\n" +
                               "        - [-9.60000610e+00, -9.99999997e-07, +0.00000000e+00]\n...\n";
        var w = Assert.Single(PrefabTextReader.Read(Encoding.UTF8.GetBytes(wrapped), "x").Prefabs);
        Assert.Equal(3, w.TextVersion);
        Assert.Equal(("empty", -9.6000061f), (w.Components[0].Entity!.PrefabName, w.Components[0].Xform!.Translate.X));

        Assert.Contains("anchor", Assert.Throws<PrefabFormatException>(() => PrefabTextReader.Read("---\nComponents: &a []\n"u8, "x")).Message);
        Assert.Contains("flow map", Assert.Throws<PrefabFormatException>(() => PrefabTextReader.Read("---\nExtents: {a: 1}\n"u8, "x")).Message);
    }

    // ---- synthetic: Dying Light 2 binary layout -------------------------------------------------------------------

    /// <summary>
    /// Two Dying Light 2 prefab roots (0x228 stride) named by pstring elements whose text is reached through untagged
    /// kind-12 slots, one with an entity component (0x120) whose value blob ends with {0, 0}; plus the zero-word empty
    /// string DL2 writes.
    /// </summary>
    internal static PrefabContainer SyntheticDl2()
    {
        const int root = 0x228, ent = 2 * root, blob = ent + 0x120;
        var primary = new byte[blob + 0x30];
        var sec = new List<byte>();
        var records = new List<Record> { new(0, PrefabClasses.Prefab, 1), new(root, PrefabClasses.Prefab, 1), new((uint)ent, PrefabClasses.EntityComponent, 1) };
        var direct = new List<Slot>();
        var indirect = new List<Slot>();
        int Pstring(string text)
        {
            int e = sec.Count;
            var t = Encoding.UTF8.GetBytes(text);
            var el = new byte[t.Length == 0 ? 0x10 : (0x18 + t.Length + 1 + 7) / 8 * 8];
            if (t.Length > 0)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(el.AsSpan(8), (ulong)(e + 0x18 + 1));   // untagged: kind 12
                BinaryPrimitives.WriteUInt32LittleEndian(el.AsSpan(0x10), (uint)t.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(el.AsSpan(0x14), (uint)t.Length);
                t.CopyTo(el, 0x18);
                direct.Add(new Slot((uint)(e + 8), 12));
            }
            sec.AddRange(el);
            records.Add(new Record((uint)e, PrefabClasses.PstringElement, PrefabContainer.RecReverse | PrefabContainer.RecInSecondary | 1));
            return e;
        }
        void Field(int at, int element)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(primary.AsSpan(at), (ulong)element + 1);
            indirect.Add(new Slot((uint)at, 9));
        }
        void Pointer(int at, int target, byte kind = 0, ushort tag = 0)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(primary.AsSpan(at), ((ulong)tag << 48) | (uint)(target + 1));
            direct.Add(new Slot((uint)at, kind));
        }
        int alpha = Pstring("alpha"), beta = Pstring("beta"), empty = Pstring(""), child = Pstring("child");
        Field(0x10, alpha);
        Field(root + 0x10, beta);
        primary[PrefabLayout.Dl2.RootDomFormat] = primary[root + PrefabLayout.Dl2.RootDomFormat] = 2;
        // beta.m_Components = [entity]
        int vec = ent + 0x120 + 0x20;   // pointer storage after the blob
        Pointer(root + PrefabLayout.Dl2.RootComponents, vec, 2, 0x93);
        BinaryPrimitives.WriteUInt32LittleEndian(primary.AsSpan(root + PrefabLayout.Dl2.RootComponents + 8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(primary.AsSpan(root + PrefabLayout.Dl2.RootComponents + 12), 1);
        Pointer(vec, ent);
        BinaryPrimitives.WriteUInt32LittleEndian(primary.AsSpan(ent + 8), 3);          // pcid inline form
        BinaryPrimitives.WriteUInt32LittleEndian(primary.AsSpan(ent + 0xC), 7);
        Pointer(ent + 0x18, root);                                                       // owner
        Pointer(ent + 0x30, blob);                                                       // values
        BinaryPrimitives.WriteSingleLittleEndian(primary.AsSpan(ent + 0x40), 4f);       // translate x
        BinaryPrimitives.WriteSingleLittleEndian(primary.AsSpan(ent + 0x58), 1f);       // scale
        Field(ent + 0x68, empty);
        Field(ent + 0x70, child);
        Pointer(ent + 0x98, 0);                                                          // m_EntityPrefab → alpha
        BinaryPrimitives.WriteUInt32LittleEndian(primary.AsSpan(ent + 0xB8), 9);        // parent pcid
        BinaryPrimitives.WriteUInt32LittleEndian(primary.AsSpan(blob), 0x18);           // total, align; no entries, {0, 0}
        BinaryPrimitives.WriteUInt32LittleEndian(primary.AsSpan(blob + 4), 8);
        var c = new PrefabContainer(2, 0, 0, 0, records, [.. direct, .. indirect], primary, [.. sec]);
        c.Sync();
        return c;
    }

    [Fact]
    public void Dl2LayoutDecodesAndStaysRefusedForEdits()
    {
        var c = SyntheticDl2();
        Assert.Contains("Dying Light 2", c.LayoutProblem());                             // the DLTB check still names it
        Assert.Same(PrefabLayout.Dl2, PrefabLayout.Detect(c));
        var doc = PrefabDecoder.Decode(c);
        Assert.Same(PrefabLayout.Dl2, doc.Layout);
        Assert.Empty(doc.Warnings);                                                     // the {0, 0} terminator
        Assert.Equal(["alpha", "beta"], doc.Prefabs.Select(p => p.Name));
        var e = Assert.Single(doc.Prefabs[1].Entities);
        Assert.Equal((7u, 1, 0), (e.Pcid, e.OwnerPrefab, e.Entity!.EntityPrefab));
        Assert.Equal(("", "child", 9u, 0L), (e.Entity.Name, e.Entity.PrefabName, e.Entity.ParentPcid, e.Entity.ForcedGuid));
        Assert.Equal(4f, e.Xform!.Translate.X);
        Assert.Throws<PrefabFormatException>(() => PrefabEdits.Rename(c, 0, "gamma"));
        c.Records[0] = c.Records[0] with { Offset = 8 };                                   // not the 0x228 stride
        Assert.Throws<PrefabFormatException>(() => PrefabLayout.Detect(c));
    }

    // ---- synthetic: catalog ---------------------------------------------------------------------------------------

    private static string Pak(string folder, string file, params (string Member, string Text)[] members)
    {
        string path = Path.Combine(folder, file);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (m, text) in members)
        {
            using var w = new StreamWriter(zip.CreateEntry(m).Open());
            w.Write(text);
        }
        return path;
    }

    [Fact]
    public void CatalogListsTextMembersLazilyWithOverridesAndSubPrefabs()
    {
        var dir = Directory.CreateTempSubdirectory("nr_prefab_");
        try
        {
            const string one = """{"__filename__": {"RuntimeData": {"Components": [{"Class": "CEntity", "PrefabFieldsNative": {"m_PcId": [6, 1], "m_PrefabName": [12, "inner"]}}]}}, "inner": {"RuntimeData": {"Components": []}}}""";
            const string two = """{"__filename__": {"RuntimeData": {"Components": []}}}""";
            var p0 = Pak(dir.FullName, "data0.pak", ("prefabs/Outer.prefab", one), ("prefabs/shared.prefab", two), ("prefabs/bad.prefab", "{"), ("readme.txt", "x"));
            var p1 = Pak(dir.FullName, "data1.pak", ("maps/shared.prefab", two));
            using var paks = new ModelCatalog([p0, p1]);
            using var rpacks = new RpackCatalog();
            var cat = PrefabCatalog.Build(rpacks, paks);
            Assert.Equal((0, 4), (cat.BinaryCount, cat.TextCount));
            Assert.All(cat.Prefabs, p => Assert.False(p.IsLoaded));                      // listing reads no member
            var outer = cat.Find("OUTER.prefab")!;                                        // engine lookup: lowercase, no extension
            Assert.Equal(("outer", PrefabSource.Pak, "prefabs/Outer.prefab"), (outer.Name, outer.Source, outer.Member));
            Assert.Null(cat.Find("inner"));                                               // sub-prefabs register when their file is read
            Assert.Single(outer.Root.Components);
            Assert.Equal("inner", cat.Find("inner")!.Name);
            Assert.Equal(1, cat.Find("inner")!.Index);
            var shared = cat.Prefabs.Where(p => p.Name == "shared").ToList();
            Assert.Equal([false, true], shared.Select(p => p.Wins));                     // the later pak's member wins
            Assert.Same(shared[1], shared[0].ShadowedBy);
            Assert.Same(shared[1], cat.Find("shared"));
            var bad = cat.Find("bad")!;
            Assert.Null(bad.TryRoot(out var why));
            Assert.Contains("prefabs/bad.prefab", why);
            Assert.Contains("prefabs/bad.prefab", Assert.Throws<PrefabFormatException>(() => bad.Root).Message);
        }
        finally { dir.Delete(true); }
    }

    // ---- install-backed -------------------------------------------------------------------------------------------

    private static (PrefabContainer C, string Path) Pack(string game, string packName)
    {
        var install = Installs.Require(game);
        var path = install.Rpacks().FirstOrDefault(p => Path.GetFileName(p).Equals(packName, StringComparison.OrdinalIgnoreCase));
        if (path is null) Assert.Skip($"{packName} not in the {game} install");
        using var pack = RpackFile.Open(path);
        return (PrefabContainer.Read(pack, Assert.Single(PrefabContainer.ResourcesIn(pack))), path);
    }

    [Fact]
    public void Dl2CommonPrefabsDecode()
    {
        var (c, _) = Pack("dl2", "common_prefabs_pc.rpack");
        var doc = PrefabDecoder.Decode(c);
        Assert.Same(PrefabLayout.Dl2, doc.Layout);
        Assert.Empty(doc.Warnings);
        Assert.Equal((1_524, 242), (doc.Prefabs.Count, doc.PresetSets.Count));
        Assert.All(doc.Prefabs, p => Assert.Equal("CEntity", p.BaseClass));
        var mesh = doc.Find("mesh")!;
        Assert.Equal(["CoWorldXform", "CoMeshRender", "CoMeshLogic"], mesh.Components.Select(x => x.ComponentClass));
        Assert.Equal("nomesh.msh", mesh.Components[1].Values!.Entries.Single(v => v.Key!.EndsWith("m_MeshName")).Text);
        Assert.Equal(1u, mesh.Components[1].XformComponent);                              // +0x40 → the CoWorldXform
        Assert.Equal([("m_MeshName", 2ul), ("m_MeshName", 3ul)], mesh.VirtualFields.Single(v => v.Name == "MeshName").Destinations.Select(d => (d.Field!, d.Pcid)));
        var meshes = doc.PresetSets.Single(s => s.ClassName == "mesh").Groups.Single(g => g.Key == "Meshes");
        Assert.Equal(23_358, meshes.Presets.Count);
    }

    [Fact]
    public void Dl2TextAndBinaryAgree()
    {
        var install = Installs.Require("dl2");
        var (c, _) = Pack("dl2", "city_persistent_pc.rpack");
        var bin = PrefabDecoder.Decode(c).Find("city_cb_terrain_70834") ?? throw new InvalidOperationException("city_cb_terrain_70834 missing");
        using var paks = new ModelCatalog(install.Paks());
        var member = paks.PakPaths.Select(paks.Pak).SelectMany(p => p.Members.Select(m => (p, m)))
                         .FirstOrDefault(x => x.m.Name.EndsWith("/city_cb_terrain_70834.prefab", StringComparison.OrdinalIgnoreCase));
        if (member.m is null) Assert.Skip("city_cb_terrain_70834.prefab not in the dl2 paks");
        var text = PrefabTextReader.Read(member.p.Read(member.m), "city_cb_terrain_70834").Prefabs[0];
        Assert.Equal(bin.Components.Select(x => (x.Pcid, x.ComponentClass)), text.Components.Select(x => (x.Pcid, x.ComponentClass)));
        foreach (var (b, t) in bin.Components.Zip(text.Components))
        {
            Assert.Equal(b.XformComponent, t.XformComponent);
            if (t.Xform is { } tx) Assert.Equal(tx.Translate, b.Xform!.Translate);
        }
    }

    [Fact]
    public async Task Dl2CatalogShadowsTextAndPlacesThroughPresets()
    {
        var install = Installs.Require("dl2");
        using var rpacks = new RpackCatalog();
        await rpacks.LoadAsync(install.Rpacks(), install.Assets, TestContext.Current.CancellationToken);
        using var paks = new ModelCatalog(install.Paks());
        var cat = PrefabCatalog.Build(rpacks, paks);
        Assert.Empty(cat.Errors);
        Assert.True(cat.BinaryCount > 40_000 && cat.TextCount > 100_000);
        // a name both provide: the binary prefab is registered; the text member is kept, shadowed, and not read
        var text = cat.Prefabs.First(p => p.Source == PrefabSource.Pak && p.Name == "city_cb_terrain_70834");
        Assert.False(text.Wins);
        Assert.Equal(PrefabSource.Rpack, text.ShadowedBy!.Source);
        Assert.Same(text.ShadowedBy, cat.Find("city_cb_terrain_70834"));
        Assert.False(text.IsLoaded);
        // terrain meshes sit at their CoWorldXformStatic (m_XformComponent)
        var terrain = PrefabPlacement.Meshes(cat, cat.Find("city_cb_terrain_70834")!);
        Assert.Equal([("city_buildterrain_reg-05c_0", 480d, 448d), ("city_buildterrain_reg-05c_2", 672d, 448d)],
                     terrain.Select(m => (m.Mesh, m.Transform[3], m.Transform[11])));
        // a vehicle: an entity instancing the generic "mesh" prefab with preset Meshes;veh_sedan_a
        var sedan = PrefabPlacement.Meshes(cat, cat.Find("veh_sedan_a")!);
        Assert.Contains(sedan, m => m.Mesh == "veh_sedan_a");
        Assert.DoesNotContain(sedan, m => m.Mesh == "nomesh");
        Assert.NotEmpty(rpacks.Lookup("veh_sedan_a", 0x10));
        // a text-only prefab reads on first use
        var flag = cat.Find("anim_flag_a_yellow")!;
        Assert.Equal(PrefabSource.Pak, flag.Source);
        Assert.Equal("mesh_no_collision", Assert.Single(flag.Root.Entities).Entity!.PrefabName);
        Assert.True(flag.IsLoaded);
    }

    [Fact]
    public void DltbGamePrefabIsTheYamlForm()
    {
        var install = Installs.Require("dltb");
        using var paks = new ModelCatalog(install.Paks());
        var hit = paks.PakPaths.Select(paks.Pak).SelectMany(p => p.Members.Select(m => (p, m)))
                      .FirstOrDefault(x => x.m.Name.Equals("prefabs/game.prefab", StringComparison.OrdinalIgnoreCase));
        if (hit.m is null) Assert.Skip("prefabs/game.prefab not in the dltb paks");
        var doc = PrefabTextReader.Read(hit.p.Read(hit.m), "game");
        Assert.Equal(PrefabTextFormat.Yaml, doc.TextSource!.Format);
        Assert.Empty(Assert.Single(doc.Prefabs).Components);
    }
}
