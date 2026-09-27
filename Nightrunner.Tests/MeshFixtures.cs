using System.Buffers.Binary;
using System.Collections.Concurrent;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Mesh.ClassReader;
using Record = Nightrunner.Core.Mesh.ClassReader.Record;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Tests;

/// <summary>
/// Real meshes by name from the detected installs (the prototype's sample pack and DL2 fixtures are cut from the
/// same shipped resources). Skips the test when the install or the mesh is missing.
/// </summary>
public static class MeshFixtures
{
    private static readonly ConcurrentDictionary<string, Dictionary<string, (string Pack, int Index)>> Index = new();

    public static (MeshModel Model, MeshParts Parts) Load(string game, string name)
    {
        var parts = Parts(game, name);
        return (MeshDecoder.Decode(parts), parts);
    }

    public static MeshParts Parts(string game, string name)
    {
        var (pack, index) = Locate(game, name);
        using var pk = RpackFile.Open(pack);
        return MeshDecoder.ReadParts(pk, index);
    }

    /// <summary>The stock pack and resource index of a mesh, by trimmed name (first stock pack in path order).</summary>
    public static (string Pack, int Index) Locate(string game, string name)
    {
        var install = Installs.Require(game);
        var map = Index.GetOrAdd(game, _ =>
        {
            var d = new Dictionary<string, (string, int)>(StringComparer.Ordinal);
            foreach (var p in install.Rpacks())
            {
                using var pk = RpackFile.Open(p);
                for (int i = 0; i < pk.Count; i++)
                    if (pk.Logicals[i].Type == 0x10) d.TryAdd(pk.Name(i).Trim(), (p, i));
            }
            return d;
        });
        if (!map.TryGetValue(name, out var at)) Assert.Skip($"{name} not in the {game} install");
        return at;
    }

    /// <summary>Every mesh of one pack of an install (the first <paramref name="limit"/>).</summary>
    public static List<MeshParts> Pack(string game, string packName, int limit = int.MaxValue)
    {
        var install = Installs.Require(game);
        var path = install.Rpacks().FirstOrDefault(p => Path.GetFileName(p).Equals(packName, StringComparison.OrdinalIgnoreCase));
        if (path is null) Assert.Skip($"{packName} not in the {game} install");
        using var pack = RpackFile.Open(path);
        return Enumerable.Range(0, pack.Count).Where(i => pack.Logicals[i].Type == 0x10).Take(limit)
            .Select(i => MeshDecoder.ReadParts(pack, i)).ToList();
    }
}

/// <summary>A synthetic skinned DL2 mesh laid out as shipped DL2 meshes are (port of test_mesh_dl2.build_skinned_dl2).</summary>
public static class MeshSynth
{
    private sealed class Img
    {
        public readonly List<byte> Buf = [];
        public readonly List<Record> Records = [];

        public int Obj(uint classId, int count, byte[] data, int align = 8)
        {
            while (Buf.Count % align != 0) Buf.Add(0);
            int off = Buf.Count;
            Buf.AddRange(data);
            Records.Add(new Record((uint)off, (0xB0u << 24) | classId, (uint)count));
            return off;
        }

        public int Str(string s) => Obj(0, 1, [.. System.Text.Encoding.UTF8.GetBytes(s), 0]);
    }

    public static (byte[] Image, byte[] Fixups, byte[] Vertex, byte[] Index) SkinnedDl2(int fmt = 6)
    {
        var im = new Img();
        int root = im.Obj(3, 1, new byte[0x68]);
        int name = im.Str("synth_dl2.msh");
        int ents = im.Obj(4, 3, new byte[3 * 0xD0]);
        int[] names = [im.Str("root_bone"), im.Str("child_bone"), im.Str("synth_dl2")];
        int aux = im.Obj(5, 1, new byte[0x20]);
        int geo = im.Obj(6, 1, new byte[0x30]);
        var streamData = new byte[0x28];
        BinaryPrimitives.WriteUInt32LittleEndian(streamData.AsSpan(0x20), 3);
        int stream = im.Obj(8, 1, streamData);
        var palData = new byte[0x18];
        BinaryPrimitives.WriteUInt16LittleEndian(palData.AsSpan(0x12), 1);
        int pal = im.Obj(7, 1, palData);
        int mhdr = im.Obj(10, 1, new byte[0x10]);
        int ment = im.Obj(11, 1, new byte[0x20]);
        int mname = im.Str("synth.mat");
        while (im.Buf.Count % 8 != 0) im.Buf.Add(0);
        var b = im.Buf.ToArray();
        var slots = new List<Slot>();
        void Ptr(int at, int target)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at), (ulong)target + 1);
            slots.Add(new Slot((uint)at, 0));
        }
        void U32(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
        void F32s(int at, params float[] v)
        {
            for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at + i * 4), v[i]);
        }
        Ptr(root, name);
        Ptr(root + 0x08, ents);
        Ptr(root + 0x18, mhdr);
        U32(root + 0x58, 3);
        U32(root + 0x5C, 1);
        (short Parent, byte Type, byte Count)[] spec = [(-1, 8, 0), (0, 8, 0), (-1, 1, 1)];
        for (int i = 0; i < 3; i++)
        {
            int e = ents + i * 0xD0;
            F32s(e, 1, 0, 0, 0, 0, 1, 0, i, 0, 0, 1, 0);
            F32s(e + 0x30, 1, 0, 0, 0, 0, 1, 0, -(float)i, 0, 0, 1, 0);   // -float(i): -0.0 for entity 0, as the prototype packs it
            F32s(e + 0x60, 0, 0.5f, 0, 1, 0.5f, 0.1f);
            Ptr(e + 0x78, names[i]);
            U32(e + 0xC0, 0x4700);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(e + 0xC4), (ushort)i);
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(e + 0xC6), spec[i].Parent);
            b[e + 0xC8] = spec[i].Type;
            b[e + 0xC9] = spec[i].Count;
            if (spec[i].Count > 0)
            {
                Ptr(e + 0x80, aux);
                Ptr(e + 0x88, geo);
            }
        }
        Ptr(geo + 0x08, stream);
        Ptr(geo + 0x10, stream + 0x24);
        Ptr(geo + 0x18, pal);
        U32(geo + 0x20, 1);
        U32(geo + 0x24, 0x6E5A4632);
        Ptr(stream, stream + 0x20);
        U32(stream + 0x0C, 3);
        U32(stream + 0x14, 1);
        U32(stream + 0x18, (uint)fmt);
        Ptr(pal, pal + 0x10);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(pal + 0x08), 2);
        Ptr(mhdr, ment);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(mhdr + 0x08), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(mhdr + 0x0A), 1);
        Ptr(ment + 0x08, mname);
        U32(ment + 0x18, 4);

        var fx = new Fixups((uint)b.Length, 1, im.Records.OrderBy(r => r.Offset).ToList(), slots.OrderBy(s => s.Offset).ToList());
        int body = fx.ToBytes().Length;
        fx.Trailing = new byte[(16 - body % 16) % 16];
        var image = new byte[(b.Length + 15) / 16 * 16];
        b.CopyTo(image, 0);

        int stride = Vertex.Stride(fmt);
        var vb = new byte[(int)Vertex.AlignTo(3 * stride, 160)];
        float[][] pos = [[0, 0, 0], [1, 0, 0], [0, 1, 0]];
        byte[][] w = [[255, 0, 0, 0], [128, 127, 0, 0], [0, 255, 0, 0]];
        byte[][] j = [[0, 0, 0, 0], [0, 1, 0, 0], [1, 0, 0, 0]];
        float[][] uv = [[0, 0], [1, 0], [0, 1]];
        for (int v = 0; v < 3; v++)
        {
            var r = vb.AsSpan(v * stride, stride);
            for (int k = 0; k < 3; k++) BinaryPrimitives.WriteSingleLittleEndian(r[(k * 4)..], pos[v][k]);
            w[v].CopyTo(r[12..]);
            j[v].CopyTo(r[16..]);
            BinaryPrimitives.WriteInt16LittleEndian(r[26..], 32767);   // qtan (0, 0, 0, 32767)
            for (int k = 0; k < 2; k++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(r[(28 + k * 2)..], Vertex.FloatToHalf(uv[v][k]));
                BinaryPrimitives.WriteUInt16LittleEndian(r[(32 + k * 2)..], Vertex.FloatToHalf(uv[v][k]));
            }
            BinaryPrimitives.WriteUInt32LittleEndian(r[36..], 0xFFFFFFFF);
        }
        var ib = new byte[16];
        ib[2] = 1;
        ib[4] = 2;
        return (image, fx.ToBytes(), vb, ib);
    }
}
