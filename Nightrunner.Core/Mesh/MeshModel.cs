using System.Text;
using Nightrunner.Core.Mesh.ClassReader;

namespace Nightrunner.Core.Mesh;

public sealed class MeshFormatException(string message) : Exception(message);

public sealed class MeshUnsupportedException(string message) : Exception(message);

/// <summary>
/// Everything the decoder knows about a type-0x10 resource, every unknown byte carried raw. Port of
/// <c>mesh/model.py</c>. Image offsets of every decoded object are kept next to the values.
/// </summary>
public sealed class MeshModel
{
    public required string Name { get; init; }
    public required string Layout { get; init; }
    /// <summary>root+0x00: the <c>.msh</c> name MeshMgr registers (A).</summary>
    public required byte[] EmbeddedName { get; init; }
    public byte[]? ScrName { get; init; }
    public required Image Image { get; init; }
    public Fixups Fixups => Image.Fixups;
    public required MeshEntity[] Entities { get; init; }
    public required GeometryEntry[] GeometryEntries { get; init; }
    /// <summary>The visible table: class-11 entries 0..count−1.</summary>
    public required MeshMaterial[] Materials { get; init; }
    public int MaterialCapacity { get; init; }
    public int? MaterialHeaderOffset { get; init; }
    public required byte[] RootRaw { get; init; }
    public required OpaqueObject[] Opaque { get; init; }
    public byte[]? VertexBuffer { get; init; }
    public byte[]? IndexBuffer { get; init; }
    /// <summary>Part 0x12 (skins; <see cref="MeshSkins"/>).</summary>
    public byte[]? SkinRaw { get; init; }
    /// <summary>Part 0xF3 <c>_CLOTH_DATA_</c>, kept raw; <see cref="ClothData.Decode"/> reads it with its image objects.</summary>
    public byte[]? ClothRaw { get; init; }
    public required List<string> Warnings { get; init; }

    public bool IsDl2 => Layout == MeshLayout.Dl2.Name;
    public bool Skinned => GeometryEntries.Any(e => e.Vertices is { Skinned: true });
    public int SubmeshCount => GeometryEntries.Sum(e => e.Submeshes.Length);
    public long VertexCount => GeometryEntries.Sum(e => (long)e.VertexCount);
    public long TriangleCount => GeometryEntries.Sum(e => e.Submeshes.Sum(s => (long)s.IndexCount / 3));

    public string MaterialName(int slot) =>
        slot >= 0 && slot < Materials.Length ? Materials[slot].NameStr : $"material_{slot}";

    /// <summary>
    /// The full class-11 table, including the skin-only materials between <c>count</c> and <c>capacity</c> that
    /// <see cref="Materials"/> does not list (they are what skins replace slots with — not free spares).
    /// </summary>
    public string[] FullMaterialTable()
    {
        if (MaterialHeaderOffset is not { } h) return [];
        try
        {
            if (Image.Pointer(h).Target is not { } first) return [];
            var names = new string[MaterialCapacity];
            for (int i = 0; i < names.Length; i++)
            {
                try { names[i] = MeshGraph.MaterialName(Image, first + i * MeshGraph.MaterialEntrySize).Name is { } b ? Text(b) : ""; }
                catch (Exception e) when (e is MeshFormatException or MeshUnsupportedException) { names[i] = $"material_{i}"; }
            }
            return names;
        }
        catch (Exception e) when (e is MeshFormatException or MeshUnsupportedException) { return []; }
    }

    /// <summary>4×4 row-major globals (parent · local), parents may be forward references.</summary>
    public double[][] EntityGlobals()
    {
        int n = Entities.Length;
        var outm = new double[n][];
        var state = new byte[n];
        for (int start = 0; start < n; start++)
        {
            var chain = new List<int>();
            int i = start;
            while (i >= 0 && state[i] != 2)
            {
                if (state[i] == 1) throw new MeshFormatException("entity parent cycle");
                state[i] = 1;
                chain.Add(i);
                i = Entities[i].Parent;
            }
            for (int k = chain.Count - 1; k >= 0; k--)
            {
                int e = chain[k], p = Entities[e].Parent;
                var local = Entities[e].Local4x4();
                outm[e] = p >= 0 ? Mul(outm[p], local) : local;
                state[e] = 2;
            }
        }
        return outm;
    }

    internal static double[] Mul(double[] a, double[] b)
    {
        var r = new double[16];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                double s = 0;
                for (int k = 0; k < 4; k++) s += a[i * 4 + k] * b[k * 4 + j];
                r[i * 4 + j] = s;
            }
        return r;
    }

    /// <summary>UTF-8 with U+FFFD for invalid bytes (common_meshes has an entity name ending in 0x8C).</summary>
    public static string Text(byte[] raw) => Encoding.UTF8.GetString(raw);
}

public sealed class Submesh
{
    public int Index { get; init; }
    /// <summary>u16 index into the visible material table.</summary>
    public int MaterialSlot { get; init; }
    public int IndexCount { get; init; }
    /// <summary>Byte offset in the index buffer (entry index base + Σ previous counts × 2).</summary>
    public long IndexBase { get; init; }
    /// <summary>u16 entity ordinals; joints index into this (empty for static formats).</summary>
    public required ushort[] Palette { get; init; }
    public int PaletteDescOffset { get; init; }
    public int? PaletteOffset { get; init; }
    /// <summary>u16 triangle list, relative to the entry's vertex window.</summary>
    public required ushort[] Indices { get; init; }
    public int TriangleCount => IndexCount / 3;
}

public sealed class GeometryEntry
{
    public int Index { get; init; }
    public int ArrayRecord { get; init; }
    public int Element { get; init; }
    public int Offset { get; init; }
    public int? OwnerEntity { get; set; }
    public int Format { get; init; }
    public long VertexBase { get; init; }
    public int VertexCount { get; init; }
    public long IndexBase { get; init; }
    public required Submesh[] Submeshes { get; init; }
    public required byte[] Raw00 { get; init; }
    public int Raw12 { get; init; }
    public int Raw14 { get; init; }
    public int Raw17 { get; init; }
    /// <summary>DLTB +0x34..+0x3F / DL2 +0x24..+0x2F (C).</summary>
    public required byte[] Raw34 { get; init; }
    public int? MaterialSlotsOffset { get; init; }
    public int? IndexCountsOffset { get; init; }
    public VertexData? Vertices { get; init; }
    /// <summary>DL2: image offset of the class-8 stream object.</summary>
    public int? StreamOffset { get; init; }
    public byte[] RawStream { get; init; } = [];

    public int IndexCount => Submeshes.Sum(s => s.IndexCount);
}

public sealed class MeshEntity
{
    public int Index { get; init; }
    public int Offset { get; init; }
    public required byte[] Name { get; init; }
    public int Parent { get; init; }
    /// <summary>3×4 row-major local-to-parent, last column translation.</summary>
    public required float[] Local { get; init; }
    public required float[] InvBind { get; init; }
    public required float[] BoundsCenter { get; init; }
    public required float[] BoundsHalf { get; init; }
    public uint Flags { get; init; }
    public int Type { get; init; }
    public int GeometryCount { get; init; }
    public int? GeometryArrayRecord { get; init; }
    public required int[] GeometryEntries { get; init; }
    public int? AuxOffset { get; init; }
    public required byte[] RawAux { get; init; }
    public required byte[] Raw90 { get; init; }
    /// <summary>+0xCA to the end of the record: 22 bytes DLTB, 6 bytes DL2 (C).</summary>
    public required byte[] RawCa { get; init; }

    public string NameStr => MeshModel.Text(Name);

    public double[] Local4x4()
    {
        var m = new double[16];
        for (int i = 0; i < 12; i++) m[i] = Local[i];
        m[15] = 1;
        return m;
    }
}

public sealed record MeshMaterial(int Index, int Offset, byte[] Name, ushort NameTag, byte[] Raw, bool NameInline)
{
    public string NameStr => MeshModel.Text(Name);
}

public sealed record OpaqueObject(int Record, uint ClassId, int Offset, int Size, byte[] Data);
