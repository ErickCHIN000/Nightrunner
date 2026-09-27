using System.Buffers.Binary;
using System.Text;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Mesh.ClassReader;
using Record = Nightrunner.Core.Mesh.ClassReader.Record;

namespace Nightrunner.Tests;

// Port of nightrunner-main/tests/test_classreader.py (decode side). The prototype's sample pack is cut from
// common_meshes_pc, so the sample-based cases run over that pack directly.
//
// not ported: test_rename_roundtrip, test_patch_add_slot_and_record — identity rename and ImagePatch write meshes
// (mesh writing is out of scope for the C# port so far).
public class ClassReaderTests
{
    [Fact]
    public void ParseSerialiseIdentityAndEmbeddedNames()
    {
        foreach (var p in MeshFixtures.Pack("dltb", "common_meshes_pc.rpack", 500))
        {
            var fx = Fixups.Parse(p.Fixups);
            Assert.Equal(p.Fixups, fx.ToBytes());
            Assert.False(fx.SecondaryPresentFlag);
            Assert.Equal(3u, fx.Records[0].ClassId);
            Assert.Equal(0u, fx.Records[0].Offset);
            var offs = fx.Records.Select(r => r.Offset).ToList();
            Assert.Equal(offs.Order(), offs);
            Assert.All(fx.Slots, s => Assert.True(s.Kind is 0 or 2 && s.Offset % 8 == 0));
            Assert.True(fx.PrimarySize <= p.Image!.Length);
            Assert.Equal(Encoding.UTF8.GetBytes(p.Name + ".msh"), Image.EmbeddedName(p.Image, p.Fixups));
        }
    }

    [Fact]
    public void SyntheticSecondaryStream()
    {
        var fx = new Fixups(64, 0x80000001, [new Record(0, 0xB0000003, 1)], [new Slot(0, 0), new Slot(8, 2)],
                            Enumerable.Repeat((byte)0x11, 20).ToArray(), new byte[4]);
        var blob = fx.ToBytes();
        var back = Fixups.Parse(blob);
        Assert.True(back.SecondaryPresentFlag);
        Assert.Equal(Enumerable.Repeat((byte)0x11, 20), back.Secondary!);
        Assert.Equal(blob, back.ToBytes());
        Assert.Equal([0, 2], back.Slots.Select(s => (int)s.Kind));
    }

    [Fact]
    public void Bounds()
    {
        Assert.Throws<MeshFormatException>(() => Fixups.Parse(new byte[8]));
        var five = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(five, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(five.AsSpan(4), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(five.AsSpan(8), 1);
        Assert.Throws<MeshFormatException>(() => Fixups.Parse(five));   // 5 records do not fit
    }

    [Fact]
    public void EmbeddedNameAndPointers()
    {
        foreach (var p in MeshFixtures.Pack("dltb", "common_meshes_pc.rpack", 200))
        {
            var img = Image.FromParts(p.Image!, p.Fixups);
            var ptr = img.Pointer(0);
            Assert.Equal(0, ptr.Kind);
            Assert.Equal((ulong)ptr.Target! + 1, img.U64(0));
            var g = new MeshGraph(img);
            Assert.Equal((uint)g.EntityCount, g.RootEntityCount);
            Assert.Equal(g.EntityOffset, img.Pointer(g.RootOffset + 0x08).Target);
            for (int i = 0; i < g.EntityCount; i++)
            {
                int e = g.EntityAt(i);
                Assert.Equal(i, img.U16(e + 0xC4));
                int count = img.U8(e + 0xC9);
                if (count == 0) continue;
                var gp = g.OptPointer(e + 0x88);
                Assert.NotNull(gp);
                var ga = g.GeometryArrayAt(gp.Value.Target!.Value);
                Assert.NotNull(ga);
                Assert.Equal(count, ga.Value.Count);
            }
            var m = MeshDecoder.Decode(p);
            foreach (var e in m.GeometryEntries)
            {
                Assert.Contains(e.Format, new[] { 0, 3, 6, 8 });
                Assert.Equal(e.Submeshes.Length, img.U16(e.Offset + 0x10));
            }
            Assert.All(m.Materials, x => Assert.EndsWith(".mat", x.NameStr, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void UnslottedNonzeroWordIsRejected()
    {
        var p = MeshFixtures.Pack("dltb", "common_meshes_pc.rpack", 2)[1];
        var img = Image.FromParts(p.Image!, p.Fixups);
        Assert.Throws<MeshFormatException>(() => img.Pointer(0x58));   // root entity count: a plain non-zero word
    }
}
