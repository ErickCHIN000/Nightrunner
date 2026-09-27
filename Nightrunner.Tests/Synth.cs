using System.Buffers.Binary;
using System.Text;
using Nightrunner.Core.Rpack;
using Nightrunner.Core.Sdb;

namespace Nightrunner.Tests;

/// <summary>
/// Synthetic inputs for the unit tests. Ported from nightrunner-main/tests/synth.py (packs, byte patching) plus the
/// IMGC header builder of test_texture.py and the SDB stream builder of test_sdb_layout.py. No game data.
/// </summary>
public static class Synth
{
    // ---- stock storage attributes (align_raw, flags, metadata) per part type ----------------------------------
    // census 2026-09-15 (47/47 shipped packs): every storage has align_raw 8 (16-byte alignment); flags/metadata
    // per type as below. Mesh family: on-demand packs (method 1, bit 3) vs engine_pc (method 0).

    public static readonly IReadOnlyDictionary<byte, (byte Align, byte Flags, byte Meta)> Stock =
        new Dictionary<byte, (byte, byte, byte)>
        {
            [0x10] = (8, 0xC9, 0x03), [0x11] = (8, 0xC9, 0x03), [0x12] = (8, 0xD9, 0x00),
            [0xF0] = (8, 0x59, 0x00), [0xF1] = (8, 0x49, 0x00), [0xF3] = (8, 0x29, 0x00),
            [0x20] = (8, 0xB1, 0x00), [0x21] = (8, 0xB1, 0x00),
            [0x40] = (8, 0x40, 0x00), [0x42] = (8, 0x40, 0x00), [0x43] = (8, 0x40, 0x00),
            [0x44] = (8, 0x20, 0x00), [0x45] = (8, 0x29, 0x00),
            [0x47] = (8, 0xC0, 0x08), [0x48] = (8, 0xC0, 0x08), [0x49] = (8, 0x40, 0x00), [0x4A] = (8, 0x40, 0x00),
            [0x61] = (8, 0x80, 0x00), [0x62] = (8, 0x80, 0x00),
            [0x5A] = (8, 0x21, 0x00), [0x55] = (8, 0x21, 0x00), [0x56] = (8, 0x21, 0x00),
        };

    public static readonly IReadOnlyDictionary<byte, (byte Align, byte Flags, byte Meta)> Method0Mesh =
        new Dictionary<byte, (byte, byte, byte)>
        {
            [0x10] = (8, 0xC0, 0x03), [0x11] = (8, 0xC0, 0x03), [0x12] = (8, 0xD0, 0x00),
            [0xF0] = (8, 0x50, 0x00), [0xF1] = (8, 0x40, 0x00),
        };

    /// <summary>Stock logical part order of a mesh: image, skin, fixups, vertex, index.</summary>
    public static readonly byte[] MeshShape = [0x10, 0x12, 0x11, 0xF0, 0xF1];

    public const byte LogicalFlagsDefault = 0x01;
    public const byte LogicalFlagsOnDemandMesh = 0x81;

    // ---- resources ----------------------------------------------------------------------------------------

    /// <summary>Deterministic non-zero bytes so parts are distinguishable (seed byte + running counter).</summary>
    public static byte[] Payload(int n, int seed = 0)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++)
        {
            int v = (seed * 37 + i * 7 + 1) & 0xFF;
            b[i] = (byte)(v == 0 ? 1 : v);
        }
        return b;
    }

    public static PartSpec Part(byte type, byte[] data, byte? alignRaw = null, byte? flags = null,
                                byte? metadata = null, uint flagBits = 0, uint fc = 0,
                                IReadOnlyDictionary<byte, (byte Align, byte Flags, byte Meta)>? table = null)
    {
        var (a, f, m) = (table ?? Stock)[type];
        return new PartSpec(type, data, alignRaw ?? a, flags ?? f, metadata ?? m, flagBits, fc);
    }

    /// <summary>A 5-part mesh resource in stock logical order (image, skin, fixups, vertex, index).</summary>
    public static ResourceSpec Mesh(string name, int[]? sizes = null, int seed = 1, bool ondemand = true,
                                    uint? flagBits = null, byte alignRaw = 8)
    {
        sizes ??= [200, 40, 100, 1000, 300];
        var table = ondemand ? Stock : Method0Mesh;
        uint fb = flagBits ?? (ondemand ? RpackFormat.PhysBit8 : 0u);
        var parts = MeshShape.Select((t, k) => Part(t, Payload(sizes[k], seed + k), alignRaw, flagBits: fb, table: table))
                             .ToArray();
        return new ResourceSpec(Bytes(name), 0x10, ondemand ? LogicalFlagsOnDemandMesh : LogicalFlagsDefault, parts);
    }

    public static ResourceSpec Texture(string name, int headerSize = 80, int bitmapSize = 1000, int seed = 9) =>
        new(Bytes(name), 0x20, LogicalFlagsDefault,
            [Part(0x20, Payload(headerSize, seed)), Part(0x21, Payload(bitmapSize, seed + 1))]);

    /// <summary>A texture resource from real IMGC header bytes and a bitmap payload.</summary>
    public static ResourceSpec Texture(string name, byte[] header, byte[]? bitmap) =>
        new(Bytes(name), 0x20, LogicalFlagsDefault,
            bitmap is null ? [Part(0x20, header)] : [Part(0x20, header), Part(0x21, bitmap)]);

    /// <summary>One-part resource (area 0x5A, envprobe 0x55, voxelizer 0x56, anim 0x40).</summary>
    public static ResourceSpec Single(string name, byte type, int size, int seed = 5, byte? alignRaw = null) =>
        Single(Bytes(name), type, size, seed, alignRaw);

    public static ResourceSpec Single(byte[] name, byte type, int size, int seed = 5, byte? alignRaw = null) =>
        new(name, type, LogicalFlagsDefault, [Part(type, Payload(size, seed), alignRaw)]);

    public static ResourceSpec Prefab(string name = "Prefabs", int size0 = 600, int size1 = 220, int seed = 3) =>
        new(Bytes(name), 0x61, LogicalFlagsDefault,
            [Part(0x61, Payload(size0, seed)), Part(0x62, Payload(size1, seed + 1))]);

    /// <summary>
    /// Every resource of <paramref name="pack"/> as a writer spec, carrying the storage words, physical flag bits
    /// and fc of each part (what a from-pack rebuild needs to reproduce the tables).
    /// </summary>
    public static List<ResourceSpec> SpecsFromPack(RpackFile pack)
    {
        var specs = new List<ResourceSpec>(pack.Count);
        for (int i = 0; i < pack.Count; i++)
        {
            var lg = pack.Logicals[i];
            var parts = new PartSpec[lg.PartCount];
            for (int k = 0; k < lg.PartCount; k++)
            {
                int pi = (int)lg.FirstPart + k;
                var ph = pack.Physicals[pi];
                var st = pack.PartStorage(pi);
                parts[k] = new PartSpec(st.Type, pack.ReadPart(pi), st.AlignRaw, st.Flags, st.Metadata,
                                        ph.FlagBits, ph.Fc);
            }
            specs.Add(new ResourceSpec(pack.NameBytes(i).ToArray(), lg.Type, lg.Flags, parts));
        }
        return specs;
    }

    public static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    // ---- packs on disk ------------------------------------------------------------------------------------

    /// <summary>Write a synthetic pack in a temp folder and return its bytes.</summary>
    public static byte[] BuildBytes(IReadOnlyList<ResourceSpec> resources, uint field08 = 0, uint flags = 1)
    {
        using var tmp = new TempDir();
        var path = tmp.File("synth.rpack");
        RpackWriter.Write(path, resources, field08, flags);
        return File.ReadAllBytes(path);
    }

    public static int AlignUp(int value, int to) => (value + to - 1) / to * to;

    public static long ExpectedTableEnd(int storages, int parts, int resources, IEnumerable<string> names) =>
        RpackFormat.HeaderSize + storages * RpackFormat.StorageSize + parts * RpackFormat.PhysicalSize
        + resources * RpackFormat.LogicalSize + names.Sum(n => 4 + Encoding.UTF8.GetByteCount(n) + 1);

    /// <summary>
    /// Resource indices in name-blob order (the order the name strings sit in the blob), or null when the names
    /// do not map one-to-one onto the blob.
    /// </summary>
    public static int[]? NameBlobOrder(RpackFile pack)
    {
        var starts = pack.NameStart;
        if (starts.Distinct().Count() != starts.Length) return null;
        return [.. Enumerable.Range(0, starts.Length).OrderBy(i => starts[i])];
    }

    /// <summary>The NUL-terminated strings of a name blob (the trailing empty piece dropped).</summary>
    public static List<byte[]> SplitNul(byte[] blob)
    {
        var parts = new List<byte[]>();
        int start = 0;
        for (int i = 0; i < blob.Length; i++)
        {
            if (blob[i] != 0) continue;
            parts.Add(blob[start..i]);
            start = i + 1;
        }
        return parts;
    }

    // ---- byte patching ------------------------------------------------------------------------------------

    public static byte[] Patch(byte[] data, int offset, ReadOnlySpan<byte> value)
    {
        var copy = (byte[])data.Clone();
        value.CopyTo(copy.AsSpan(offset));
        return copy;
    }

    public static byte[] PatchU32(byte[] data, long offset, uint value)
    {
        var copy = (byte[])data.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan((int)offset), value);
        return copy;
    }

    public static byte[] PatchU8(byte[] data, long offset, byte value)
    {
        var copy = (byte[])data.Clone();
        copy[offset] = value;
        return copy;
    }

    public static long StorageOff(int i) => RpackFormat.HeaderSize + i * RpackFormat.StorageSize;
    public static long PhysicalOff(RpackFile pk, int i) => pk.PhysicalOffset + i * RpackFormat.PhysicalSize;
    public static long LogicalOff(RpackFile pk, int i) => pk.LogicalOffset + i * RpackFormat.LogicalSize;
    public static long NameOffsetOff(RpackFile pk, int i) => pk.NameOffsetOffset + i * 4;

    // ---- IMGC ---------------------------------------------------------------------------------------------

    public const byte Tex2D = 0, TexCube = 1, TexVolume = 2;

    /// <summary>The stats test_texture.synth_header writes: min (.1,.2,.3,1), max (.9,.8,.7,1), mean (.5,.5,.5,1).</summary>
    public static byte[] DefaultStats()
    {
        float[] v = [0.1f, 0.2f, 0.3f, 1.0f, 0.9f, 0.8f, 0.7f, 1.0f, 0.5f, 0.5f, 0.5f, 1.0f];
        var raw = new byte[48];
        for (int i = 0; i < 12; i++) BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * 4), v[i]);
        return raw;
    }

    /// <summary>Stored IMGC header bytes (16-byte padded), laid out as imgc.pack_header writes them.</summary>
    public static byte[] ImgcHeaderBytes(int width, int height, int depth, byte texType, int mips, byte format,
                                    uint flags = 0x44, byte reserved = 0, ulong mipSplit = 0, byte[]? stats = null,
                                    byte[]? extension = null, byte[]? tail = null)
    {
        extension ??= [];
        int headerSize = 80 + extension.Length;
        int stored = AlignUp(headerSize, 16);
        var raw = new byte[stored];
        BinaryPrimitives.WriteUInt32LittleEndian(raw, Core.Texture.ImgcHeader.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), Core.Texture.ImgcHeader.KnownVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(8), (uint)headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(12), flags);
        (stats ?? DefaultStats()).CopyTo(raw, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(64), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(66), (ushort)height);
        raw[68] = (byte)depth;
        raw[69] = reserved;
        raw[70] = format;
        raw[71] = (byte)((texType & 3) | (mips << 2));
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(72), mipSplit);
        extension.CopyTo(raw, 80);
        tail?.CopyTo(raw, headerSize);
        return raw;
    }

    /// <summary>Random tight bytes for every level of <paramref name="header"/>'s layout.</summary>
    public static List<byte[]> RandomLevels(Core.Texture.ImgcHeader header, int seed = 1234)
    {
        var rng = new Random(seed);
        return [.. header.LevelLayout().Select(lv =>
        {
            var b = new byte[lv.Size];
            rng.NextBytes(b);
            return b;
        })];
    }

    /// <summary>Inverse of a level split: tight levels concatenated with the given per-level padding (imgc.join_payload).</summary>
    public static byte[] JoinLevels(Core.Texture.ImgcHeader header, IReadOnlyList<byte[]> levels, int levelPadding = 16)
    {
        var layout = header.LevelLayout(levelPadding);
        var output = new byte[layout[^1].Offset + layout[^1].PaddedSize];
        for (int i = 0; i < layout.Count; i++) levels[i].CopyTo(output, layout[i].Offset);
        return output;
    }

    // ---- SDB ----------------------------------------------------------------------------------------------

    /// <summary>The stream's packed integer: tag in the low two bits, width 1 / 2 / 4.</summary>
    public static byte[] Compact(int v)
    {
        if (v < 1 << 6) return [(byte)(v << 2)];
        var b = new byte[v < 1 << 14 ? 2 : 4];
        if (b.Length == 2) BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)((v << 2) | 1));
        else BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)((v << 2) | 2));
        return b;
    }

    /// <summary>A vectors table: compact count, then per record compact n and n units.</summary>
    public static byte[] Vec(IReadOnlyList<byte[]> items, int unit)
    {
        var o = new List<byte>(Compact(items.Count));
        foreach (var it in items)
        {
            if (it.Length % unit != 0) throw new ArgumentException("record is not a whole number of units");
            o.AddRange(Compact(it.Length / unit));
            o.AddRange(it);
        }
        return [.. o];
    }

    /// <summary>A flat table: compact count, then the fixed-width records.</summary>
    public static byte[] Flat(IReadOnlyList<byte[]> items, int width)
    {
        if (items.Any(i => i.Length != width)) throw new ArgumentException("record width mismatch");
        return [.. Compact(items.Count), .. items.SelectMany(i => i)];
    }

    public static readonly string[] SdbSharedNames =
        ["", "dif_0_tex", "roughness", "default_dif.png", "noise.dds", "override_dif.png"];

    public static readonly byte[] SdbProgramBlob = "DXBC"u8.ToArray();

    /// <summary>
    /// One material `test_mat.mat` → route 0 → preset `opaque` (dif_0_tex: string, roughness: float) → shader 1
    /// (texture array 1: dif_0_tex [override] + an unnamed binding [shader default noise.dds]), written in
    /// <paramref name="layout"/>'s table order and labelled with <paramref name="version"/> (default: the layout's).
    /// </summary>
    public static byte[] Sdb(SdbLayout layout, uint? version = null)
    {
        static byte[][] Str(params string[] xs) => [.. xs.Select(x => Encoding.UTF8.GetBytes(x))];
        static byte[] U16(params int[] v)
        {
            var b = new byte[v.Length * 2];
            for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 2), (ushort)v[i]);
            return b;
        }
        static byte[] U32(params uint[] v)
        {
            var b = new byte[v.Length * 4];
            for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), v[i]);
            return b;
        }
        static byte[] F32(float f)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(b, f);
            return b;
        }

        var shader = new byte[25];
        BinaryPrimitives.WriteUInt16LittleEndian(shader.AsSpan(0x08), 0);   // pass 0
        BinaryPrimitives.WriteUInt16LittleEndian(shader.AsSpan(0x12), 1);   // texture array 1
        shader[0x17] = 1;                                                    // one render pass
        byte[] d2 = [.. U16(5), .. F32(0.25f)];

        var preset = new List<byte>();
        var head = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(head, 0x1122334455667788);
        preset.AddRange(head);                           // key, name id 0 ("opaque"), flags 0
        preset.AddRange(new byte[7]);                    // masks
        preset.AddRange(Compact(2));
        preset.AddRange(U16(1, 1, 2, 0x8007));
        preset.AddRange(Compact(1));
        preset.Add(0);
        preset.AddRange(U16(2, 1, 2, 2));
        preset.AddRange(Compact(4));
        preset.AddRange(F32(0.5f));
        preset.AddRange(Compact(1));
        preset.AddRange(U32(7));
        preset.AddRange(Compact(2));
        preset.AddRange(U32(1, 2));

        var content = new Dictionary<int, byte[]>
        {
            [0x9A] = Flat([new byte[25], shader], 25),
            [0xA2] = Vec([[], U16(1, 3, 0, 4)], 4),
            [0xC2] = Vec([BitConverter.GetBytes((1UL << 48) | 0xABC)], 8),
            [0xCA] = Flat([U16(0, 0, 0, 0, 0)], 10),
            [0xCE] = Vec([U32(1 | (0 << 16), 2 | (2 << 16))], 4),
            [0xD2] = Vec([d2], 1),
            [0xD6] = Flat([new byte[8]], 8),
            [0xAE] = Vec(Str(SdbSharedNames), 1),
            [0xB2] = Vec(Str("test_mat.mat"), 1),
            [0xB6] = Vec(Str("opaque;dif_0_tex;"), 1),
            [0xBA] = Vec(Str("opaque", "IN.x", "display(X)"), 1),
            [0xAA] = [.. Compact(1), .. preset],
        };

        var a = new List<byte>();
        foreach (var def in layout.OrderA)
        {
            if (def.Key == 0x62)          // width matters for the walk
                a.AddRange(Flat([.. Enumerable.Range(0, 3).Select(i => Enumerable.Repeat((byte)i, def.Width).ToArray())], def.Width));
            else if (def.Key == 0x28)
                a.AddRange(Flat([.. Enumerable.Range(0, 2).Select(i => Enumerable.Repeat((byte)i, def.Width).ToArray())], def.Width));
            else
                a.AddRange(content.GetValueOrDefault(def.Key) ?? Compact(0));
        }
        foreach (int kind in SdbFormat.ProgramKinds)
        {
            a.AddRange(Compact(1));
            a.AddRange(U32(kind == 2 ? (uint)SdbProgramBlob.Length : 0));
        }
        foreach (var def in layout.OrderB) a.AddRange(content.GetValueOrDefault(def.Key) ?? Compact(0));

        byte[] blockB = SdbProgramBlob;
        byte[] blockA = [.. "MDB "u8, .. U32(version ?? layout.InnerVersion), .. new byte[28], .. a];
        return [.. "MDBR"u8, .. U32(SdbFormat.OuterVersion, (uint)blockA.Length, (uint)blockB.Length), .. blockA, .. blockB];
    }
}

/// <summary>
/// A throwaway folder under the system temp path. Packs and databases opened through it are disposed before the
/// folder is deleted (a memory map keeps its file locked on Windows).
/// </summary>
public sealed class TempDir : IDisposable
{
    private readonly List<IDisposable> _open = [];

    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nightrunner-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Write(string name, byte[] data)
    {
        var p = File(name);
        System.IO.File.WriteAllBytes(p, data);
        return p;
    }

    /// <summary>Write <paramref name="data"/> to a file here and open it as a pack.</summary>
    public RpackFile OpenPack(byte[] data, string name = "synth.rpack") => Track(RpackFile.Open(Write(name, data)));

    public RpackFile OpenPack(IReadOnlyList<ResourceSpec> resources, uint field08 = 0, string name = "synth.rpack")
    {
        var p = File(name);
        RpackWriter.Write(p, resources, field08);
        return Track(RpackFile.Open(p));
    }

    public SdbFile OpenSdb(byte[] data, string name = "synth.sdb") => Track(SdbFile.Open(Write(name, data)));

    public T Track<T>(T item) where T : IDisposable
    {
        _open.Add(item);
        return item;
    }

    public void Dispose()
    {
        foreach (var d in _open) d.Dispose();
        _open.Clear();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
