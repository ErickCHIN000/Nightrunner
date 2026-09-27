using System.Buffers.Binary;
using Nightrunner.Core.Mesh.ClassReader;

namespace Nightrunner.Core.Mesh;

/// <summary>Element layout of one array of the cloth part (dword-aligned, see <see cref="ClothData"/>).</summary>
public enum ClothElement
{
    /// <summary>u16 per element, the array padded with zero to a dword.</summary>
    U16,
    U32,
    F32x3,
    /// <summary>4 × u32 per element (joint indices, one per lane).</summary>
    U32x4,
    F32x4,
    /// <summary>One bit per element, LSB first; <c>(n &gt;&gt; 5) + 1</c> dwords (a full extra dword when n is a multiple of 32).</summary>
    Bits,
    /// <summary>Header (f32 min, f32 step) + u16 per element, padded to a dword.</summary>
    Quant16,
    /// <summary>Header (f32 min, f32 step) + u8 per element, padded to a dword.</summary>
    Quant8,
    /// <summary>Length not derivable from any count: carried as stored (up to the next array).</summary>
    Raw,
}

/// <summary>An offset field of a cloth object: byte offset of the u32 field in its image object, a name, the element layout.</summary>
public sealed record ClothFieldSpec(int Field, string Name, ClothElement Element);

/// <summary>One array of the cloth part: which object and field hold its offset, and its bytes.</summary>
public sealed class ClothArray
{
    /// <summary>−1: the ClothEntityInFile object (simulation mesh); k ≥ 0: visual mapping k.</summary>
    public required int Owner { get; init; }
    public required ClothFieldSpec Spec { get; init; }
    /// <summary>Offset in dwords into the part, as stored (or as laid out by <see cref="ClothData.Layout"/>).</summary>
    public uint Offset { get; set; }
    /// <summary>Exactly the array's bytes (a multiple of 4; derived length for every non-Raw element).</summary>
    public required byte[] Data { get; set; }

    public string Name => Owner < 0 ? Spec.Name : $"M{Owner}.{Spec.Name}";
}

/// <summary>A constraint set of ClothEntityInFile (+0x98 / +0xB8 / +0xD8): count, batch count, three parameters, three arrays.</summary>
public sealed record ClothConstraintSet(int Field, uint Count, uint Batches, float[] Parameters);

/// <summary>A collider (class 24, 0x70 bytes). <see cref="Raw"/> is the whole record; the rest is a view.</summary>
public sealed record ClothCollider(int Offset, uint Type, float[] Shape, float[] Matrix, string? Bone, uint BoneHash, byte[] Raw);

/// <summary>
/// A visual mapping (class 27, 0x54 bytes): binds the render vertices of one LOD of the cloth entity to the simulation
/// mesh. Offsets of its arrays live at +0x10..+0x50 (see <see cref="ClothData.MappingFields"/>).
/// </summary>
public sealed class ClothMapping
{
    public required int Index { get; init; }
    /// <summary>Image offset of the 0x54-byte object.</summary>
    public required int Offset { get; init; }
    /// <summary>+0x00: LOD, an index into the cloth entity's geometry entries.</summary>
    public required uint Lod { get; init; }
    /// <summary>+0x04: render vertex count of that entry.</summary>
    public uint VertexCount { get; set; }
    /// <summary>+0x08: render vertices not bound to the simulation mesh (= VertexCount − popcount(Mapped)).</summary>
    public uint Unmapped { get; set; }
    /// <summary>+0x0C: popcount of the per-mapped-vertex mask at +0x2C.</summary>
    public uint Marked { get; set; }
    /// <summary>The geometry entry index in the mesh.</summary>
    public required int Entry { get; init; }

    public uint MappedCount => VertexCount - Unmapped;
}

/// <summary>
/// Cloth data of a DLTB mesh: the ClassReader objects in the mesh image plus part 0xF3 <c>_CLOTH_DATA_</c>. The part is
/// a flat, dword-addressed blob of arrays (the engine uploads it whole as the <c>ClothFile</c> GPU buffer); every array's
/// offset (in dwords) lives in a u32 field of one of two image objects:
/// <list type="bullet">
/// <item>the cloth entity (entity +0xD0 → ClothInFile, class 23, 0x18 bytes: proxy-bone palettes, +0x10 → ClothEntityInFile);</item>
/// <item>ClothEntityInFile (class 32, 0x198 bytes): simulation parameters, simulation-mesh counts and array offsets;</item>
/// <item>visual mappings (class 27, 0x54 bytes each, +0x118/+0x120): one per LOD, render-vertex binding arrays;</item>
/// <item>colliders (class 24, 0x70 bytes each, +0x128/+0x130).</item>
/// </list>
/// Layout rule (58/58 DLTB parts): arrays in field order (entity fields ascending, then each mapping's fields ascending),
/// absent ones (offset 0xFFFFFFFF) skipped, contiguous from dword 0, each exactly its derived size; zero padding to 16
/// bytes at the end. Decode keeps every array's bytes; <see cref="EncodePart"/> reproduces the part byte for byte.
/// Anything outside that rule is refused by name (<see cref="MeshFormatException"/>).
/// </summary>
public sealed class ClothData
{
    public const uint ClassInFile = 23, ClassEntityInFile = 32, ClassMapping = 27, ClassCollider = 24;
    public const int EntityField = 0xD0, InFileSize = 0x18, EntityInFileSize = 0x198, MappingSize = 0x54, ColliderSize = 0x70;
    public const uint Absent = 0xFFFF_FFFF;
    public const int LodLevels = 5;

    /// <summary>ClothEntityInFile offset fields, in part order. Names: see docs/formats.md (Cloth) for what is known of each.</summary>
    public static readonly ClothFieldSpec[] EntityFields =
    [
        new(0x4C, "Indices", ClothElement.U16), new(0x50, "Positions", ClothElement.F32x3), new(0x54, "Normals", ClothElement.F32x3),
        new(0x58, "E58", ClothElement.Raw), new(0x5C, "E5C", ClothElement.Raw), new(0x60, "E60", ClothElement.U32),
        new(0x64, "E64", ClothElement.U32), new(0x68, "E68", ClothElement.U32), new(0x6C, "E6C", ClothElement.Raw),
        new(0x70, "E70", ClothElement.Raw), new(0x74, "Joints", ClothElement.U32x4), new(0x78, "Weights", ClothElement.F32x4),
        new(0x7C, "E7C", ClothElement.U32),
        new(0xAC, "S0.Pairs", ClothElement.U32), new(0xB0, "S0.B0", ClothElement.Raw), new(0xB4, "S0.Batches", ClothElement.U32),
        new(0xCC, "S1.Pairs", ClothElement.U32), new(0xD0, "S1.B0", ClothElement.Raw), new(0xD4, "S1.Batches", ClothElement.U32),
        new(0xEC, "S2.Pairs", ClothElement.U32), new(0xF0, "S2.B0", ClothElement.Raw), new(0xF4, "S2.Batches", ClothElement.U32),
        new(0x100, "S3.Pairs", ClothElement.U32), new(0x104, "S3.104", ClothElement.Raw), new(0x108, "S3.108", ClothElement.Raw),
        new(0x10C, "S3.10C", ClothElement.Raw), new(0x110, "S3.110", ClothElement.Raw),
    ];

    /// <summary>Visual-mapping offset fields, in part order.</summary>
    public static readonly ClothFieldSpec[] MappingFields =
    [
        new(0x10, "Positions", ClothElement.F32x3), new(0x14, "Triangles", ClothElement.U16), new(0x18, "Q18", ClothElement.Quant16),
        new(0x1C, "Q1C", ClothElement.Quant8), new(0x20, "Q20", ClothElement.Quant16), new(0x24, "Q24", ClothElement.Quant16),
        new(0x28, "Mask28", ClothElement.Bits), new(0x2C, "Marked", ClothElement.Bits), new(0x30, "MarkedVertices", ClothElement.U32),
        new(0x34, "MarkedVectors", ClothElement.F32x3), new(0x38, "Mapped", ClothElement.Bits), new(0x3C, "Rank", ClothElement.U32),
        new(0x40, "Normals", ClothElement.F32x3), new(0x44, "Tangents", ClothElement.F32x3), new(0x48, "Weights", ClothElement.F32x4),
        new(0x4C, "Joints", ClothElement.U32x4), new(0x50, "Submesh", ClothElement.U32),
    ];

    /// <summary>Mapping fields with one element per render vertex / per mapped vertex / per marked vertex.</summary>
    internal static readonly int[] PerVertex = [0x10, 0x28, 0x38, 0x3C, 0x40, 0x44, 0x48, 0x4C, 0x50];
    internal static readonly int[] PerMapped = [0x14, 0x18, 0x1C, 0x20, 0x24, 0x2C];
    internal static readonly int[] PerMarked = [0x30, 0x34];

    public required int EntityIndex { get; init; }
    public required int InFileOffset { get; init; }
    public required int EntityInFileOffset { get; init; }
    /// <summary>ClothInFile +0x00/+0x08: u16 palettes (GetClothProxyBoneIndices; one per LOD in every shipped mesh).</summary>
    public required ushort[][] ProxyPalettes { get; init; }
    /// <summary>ClothEntityInFile +0x00 (the .msh name in every shipped part).</summary>
    public required string? Name { get; init; }
    /// <summary>+0x08..+0x37 as 12 floats: gravity xyz, then max displacement, skin width, viscosity, elasticity,
    /// max distance scale, max distance gameplay scale, backstop offset scale, spherical backstop radius scale, velocity damping (B).</summary>
    public required float[] Simulation { get; init; }
    /// <summary>+0x38 (surface used when there is no visual mapping); 0xFFFFFFFF in every shipped part.</summary>
    public required uint Surface { get; init; }
    /// <summary>+0x3C: simulation-mesh triangle index count.</summary>
    public required uint IndexCount { get; init; }
    /// <summary>+0x40: particle (simulation vertex) count.</summary>
    public required uint ParticleCount { get; init; }
    /// <summary>+0x44: element count of E60.</summary>
    public required uint Count44 { get; init; }
    /// <summary>+0x48: element count of E64 (= IndexCount in every shipped part).</summary>
    public required uint Count48 { get; init; }
    /// <summary>+0x80: six floats (centre, half extents of the simulation mesh; B).</summary>
    public required float[] Bounds { get; init; }
    /// <summary>+0x98 / +0xB8 / +0xD8.</summary>
    public required ClothConstraintSet[] Sets { get; init; }
    /// <summary>+0xF8 count, +0xFC batch count of set 3.</summary>
    public required uint Set3Count { get; init; }
    public required uint Set3Batches { get; init; }
    /// <summary>+0x144 + 16·k: the visual mapping drawn at cloth LOD level k (5 levels; InitializeBuffers / constants).</summary>
    public required uint[] LodMapping { get; init; }
    public required List<ClothMapping> Mappings { get; init; }
    public required List<ClothCollider> Colliders { get; init; }
    /// <summary>Every present array, in part order.</summary>
    public required List<ClothArray> Arrays { get; init; }
    /// <summary>Bytes after the last array (zero padding to 16).</summary>
    public required byte[] Tail { get; set; }

    public ClothArray? Array(int owner, int field) => Arrays.FirstOrDefault(a => a.Owner == owner && a.Spec.Field == field);

    // ---- decode ----------------------------------------------------------------------------------------------------

    /// <summary>The cloth data of a model, or null when it has no part 0xF3. Refuses (MeshFormatException) anything outside
    /// the measured layout.</summary>
    public static ClothData? Decode(MeshModel model)
    {
        if (model.ClothRaw is not { } part) return null;
        var img = model.Image;
        string Where(string s) => $"cloth: {s}";
        if (model.Layout != MeshLayout.Dltb.Name)
            throw new MeshFormatException(Where($"part 0xF3 on a {model.Layout.ToUpperInvariant()} mesh (only the DLTB layout is known)"));
        if (part.Length % 16 != 0) throw new MeshFormatException(Where($"part size {part.Length} is not a multiple of 16"));

        var owners = model.Entities.Where(e => img.IsSlot(e.Offset + EntityField) || img.U64(e.Offset + EntityField) != 0).ToList();
        if (owners.Count != 1)
            throw new MeshFormatException(Where($"{owners.Count} entities carry a cloth pointer at +0x{EntityField:X} (exactly one expected)"));
        var ent = owners[0];
        int inFile = Object(img, img.Pointer(ent.Offset + EntityField).Target, ClassInFile, InFileSize, 1, "entity +0xD0");
        int cef = Object(img, img.Pointer(inFile + 0x10).Target, ClassEntityInFile, EntityInFileSize, 1, "ClothInFile +0x10");

        // proxy palettes: {u16*, u64 count} descriptors
        var palettes = new List<ushort[]>();
        ulong nPal = img.U64(inFile + 8);
        if (nPal > 64) throw new MeshFormatException(Where($"{nPal} proxy palettes"));
        if (nPal > 0)
        {
            int d = img.Pointer(inFile).Target ?? throw new MeshFormatException(Where("proxy palette pointer is null with a count"));
            for (int k = 0; k < (int)nPal; k++)
            {
                ulong c = img.U64(d + k * 16 + 8);
                if (c > MeshGraph.MaxPalette) throw new MeshFormatException(Where($"proxy palette {k}: {c} entries"));
                palettes.Add(c == 0 ? [] : img.U16s(img.Pointer(d + k * 16).Target
                                                    ?? throw new MeshFormatException(Where($"proxy palette {k}: null data")), (int)c));
            }
        }

        uint U(int o) => img.U32(cef + o);
        if (U(0x70) != Absent) throw new MeshFormatException(Where("ClothEntityInFile +0x70 is set (never seen in the corpus; its array is unknown)"));
        if (U(0x114) != 0) throw new MeshFormatException(Where($"ClothEntityInFile +0x114 is 0x{U(0x114):X8} (0 expected)"));
        // +0x138: 5 × 16 bytes, one per cloth LOD level (runtime dispatch list pointer/count) with the visual mapping index
        // of that level at +0x0C (file data); +0x188/+0x190: runtime GPU buffer handles
        var lodMapping = new uint[LodLevels];
        for (int o = 0x138; o < EntityInFileSize; o += 4)
        {
            if (o is >= 0x138 and < 0x188 && (o - 0x138) % 16 == 0xC) { lodMapping[(o - 0x138) / 16] = U(o); continue; }
            if (U(o) != 0) throw new MeshFormatException(Where($"ClothEntityInFile +0x{o:X} (runtime field) is not zero"));
        }
        if (U(0x3C) != U(0x48)) throw new MeshFormatException(Where($"ClothEntityInFile +0x3C ({U(0x3C)}) != +0x48 ({U(0x48)})"));

        // visual mappings
        var mappings = new List<ClothMapping>();
        ulong nMap = img.U64(cef + 0x120);
        if (nMap > 16) throw new MeshFormatException(Where($"{nMap} visual mappings"));
        if (nMap > 0)
        {
            int mo = Object(img, img.Pointer(cef + 0x118).Target, ClassMapping, MappingSize, (int)nMap, "ClothEntityInFile +0x118");
            for (int k = 0; k < (int)nMap; k++)
            {
                int o = mo + k * MappingSize;
                uint lod = img.U32(o);
                if (lod >= ent.GeometryEntries.Length)
                    throw new MeshFormatException(Where($"mapping {k}: LOD {lod} outside the cloth entity's {ent.GeometryEntries.Length} geometry entries"));
                int entry = ent.GeometryEntries[lod];
                var m = new ClothMapping
                {
                    Index = k, Offset = o, Lod = lod, Entry = entry,
                    VertexCount = img.U32(o + 4), Unmapped = img.U32(o + 8), Marked = img.U32(o + 12),
                };
                if (m.VertexCount != model.GeometryEntries[entry].VertexCount)
                    throw new MeshFormatException(Where($"mapping {k}: {m.VertexCount} vertices, entry {entry} has {model.GeometryEntries[entry].VertexCount}"));
                if (m.Unmapped > m.VertexCount) throw new MeshFormatException(Where($"mapping {k}: {m.Unmapped} unmapped of {m.VertexCount}"));
                mappings.Add(m);
            }
            if (mappings.Select(m => m.Lod).Distinct().Count() != mappings.Count)
                throw new MeshFormatException(Where("two visual mappings name the same LOD"));
        }
        else if (U(0x38) == Absent) throw new MeshFormatException(Where("no visual mapping and no surface (+0x38)"));
        foreach (var lm in lodMapping)
            if (lm >= Math.Max(1UL, nMap)) throw new MeshFormatException(Where($"LOD level mapping index {lm} with {nMap} mappings"));

        // colliders
        var colliders = new List<ClothCollider>();
        ulong nCol = img.U64(cef + 0x130);
        if (nCol > 256) throw new MeshFormatException(Where($"{nCol} colliders"));
        if (nCol > 0)
        {
            int co = Object(img, img.Pointer(cef + 0x128).Target, ClassCollider, ColliderSize, (int)nCol, "ClothEntityInFile +0x128");
            for (int k = 0; k < (int)nCol; k++)
            {
                int o = co + k * ColliderSize;
                var bone = img.Pointer(o + 0x58).Target is { } bt ? MeshModel.Text(img.CString(bt)) : null;
                colliders.Add(new ClothCollider(o, img.U32(o), img.F32s(o + 4, 7), img.F32s(o + 0x20, 12), bone, img.U32(o + 0x60),
                                                img.Raw(o, ColliderSize)));
            }
        }

        var sets = new[] { 0x98, 0xB8, 0xD8 }.Select(f => new ClothConstraintSet(f, U(f), U(f + 4), img.F32s(cef + f + 8, 3))).ToArray();
        var cd = new ClothData
        {
            EntityIndex = ent.Index, InFileOffset = inFile, EntityInFileOffset = cef, ProxyPalettes = palettes.ToArray(),
            Name = img.Pointer(cef).Target is { } nt ? MeshModel.Text(img.CString(nt)) : null,
            Simulation = img.F32s(cef + 8, 12), Surface = U(0x38), IndexCount = U(0x3C), ParticleCount = U(0x40),
            Count44 = U(0x44), Count48 = U(0x48), Bounds = img.F32s(cef + 0x80, 6), Sets = sets, Set3Count = U(0xF8), Set3Batches = U(0xFC),
            Mappings = mappings, Colliders = colliders, Arrays = [], Tail = [], LodMapping = lodMapping,
        };

        // arrays: canonical order, contiguous
        var fields = new List<(int Owner, ClothFieldSpec Spec, uint Offset)>();
        foreach (var s in EntityFields)
            if (U(s.Field) is var off && off != Absent) fields.Add((-1, s, off));
        foreach (var m in mappings)
            foreach (var s in MappingFields)
                if (img.U32(m.Offset + s.Field) is var off && off != Absent) fields.Add((m.Index, s, off));
        uint dwords = (uint)(part.Length / 4), cur = 0;
        for (int i = 0; i < fields.Count; i++)
        {
            var (owner, spec, off) = fields[i];
            string nm = owner < 0 ? spec.Name : $"M{owner}.{spec.Name}";
            if (off < cur) throw new MeshFormatException(Where($"array {nm} at dword {off} is out of field order"));
            if (off > dwords) throw new MeshFormatException(Where($"array {nm} at dword {off} is past the part ({dwords} dwords)"));
            uint next = i + 1 < fields.Count ? fields[i + 1].Offset : dwords;
            if (next < off) throw new MeshFormatException(Where($"array {nm}: the next array starts before it"));
            uint size;
            if (spec.Element == ClothElement.Raw) size = next - off;
            else
            {
                size = cd.Dwords(owner, spec);
                bool last = i + 1 == fields.Count;
                if (off + size > dwords || (!last && off + size != next))
                    throw new MeshFormatException(Where($"array {nm}: {next - off} dwords stored, {size} derived (layout rule broken)"));
            }
            if (off != cur) throw new MeshFormatException(Where($"array {nm} at dword {off}, {cur} expected (gap between arrays)"));
            cd.Arrays.Add(new ClothArray { Owner = owner, Spec = spec, Offset = off, Data = part.AsSpan((int)off * 4, (int)size * 4).ToArray() });
            cur = off + size;
        }
        cd.Tail = part.AsSpan((int)cur * 4).ToArray();
        if (cd.Tail.Length >= 16 || cd.Tail.Any(b => b != 0) || (cur * 4 + cd.Tail.Length) % 16 != 0)
            throw new MeshFormatException(Where($"{cd.Tail.Length} bytes after the last array (zero padding to 16 expected)"));
        cd.CheckPadding();
        return cd;
    }

    /// <summary>Image offset of an object of an expected class that spans at least count × size bytes.</summary>
    private static int Object(Image img, int? target, uint cls, int size, int count, string from)
    {
        if (target is not { } t) throw new MeshFormatException($"cloth: {from} is null");
        if (img.RecordAt(t) is not { } ri || img.Records[ri].ClassId != cls)
            throw new MeshFormatException($"cloth: {from} → 0x{t:X} is not a class-{cls} record");
        var (a, b) = img.RecordSpan(ri);
        if (b - a < size * count || b - a >= size * count + 16)
            throw new MeshFormatException($"cloth: class-{cls} record at 0x{t:X} spans {b - a} bytes, {count} × 0x{size:X} expected");
        return t;
    }

    /// <summary>Element count of an array from the object counts; null for Raw.</summary>
    public uint? Count(int owner, ClothFieldSpec spec)
    {
        if (owner < 0)
            return spec.Field switch
            {
                0x4C => IndexCount, 0x50 or 0x54 or 0x74 or 0x78 or 0x7C => ParticleCount, 0x60 => Count44, 0x64 => Count48,
                0x68 => ParticleCount + 1, 0xAC => Sets[0].Count, 0xB4 => Sets[0].Batches, 0xCC => Sets[1].Count, 0xD4 => Sets[1].Batches,
                0xEC => Sets[2].Count, 0xF4 => Sets[2].Batches, 0x100 => Set3Count, _ => null,
            };
        var m = Mappings[owner];
        if (System.Array.IndexOf(PerVertex, spec.Field) >= 0) return m.VertexCount;
        if (System.Array.IndexOf(PerMapped, spec.Field) >= 0) return m.MappedCount;
        return m.Marked;
    }

    public uint Dwords(int owner, ClothFieldSpec spec) =>
        Count(owner, spec) is { } n ? ElementDwords(spec.Element, n) : throw new InvalidOperationException($"{spec.Name}: raw array");

    public static uint ElementDwords(ClothElement e, uint n) => e switch
    {
        ClothElement.U16 => (n + 1) / 2,
        ClothElement.U32 => n,
        ClothElement.F32x3 => n * 3,
        ClothElement.U32x4 or ClothElement.F32x4 => n * 4,
        ClothElement.Bits => (n >> 5) + 1,
        ClothElement.Quant16 => 2 + (n + 1) / 2,
        ClothElement.Quant8 => 2 + (n + 3) / 4,
        _ => throw new InvalidOperationException("raw array"),
    };

    /// <summary>Unused bytes inside a derived-size array (u16/u8 tails, bits past n) must be zero; else the layout is unknown.</summary>
    private void CheckPadding()
    {
        foreach (var a in Arrays)
        {
            if (Count(a.Owner, a.Spec) is not { } n) continue;
            int used = a.Spec.Element switch
            {
                ClothElement.U16 => (int)n * 2, ClothElement.Quant16 => 8 + (int)n * 2, ClothElement.Quant8 => 8 + (int)n,
                ClothElement.Bits => -1, _ => a.Data.Length,
            };
            if (used >= 0)
            {
                if (a.Data.AsSpan(used).ContainsAnyExcept((byte)0))
                    throw new MeshFormatException($"cloth: array {a.Name}: non-zero padding after {n} elements");
            }
            else
            {
                for (long bit = n; bit < a.Data.Length * 8L; bit++)
                    if ((a.Data[bit >> 3] >> (int)(bit & 7) & 1) != 0)
                        throw new MeshFormatException($"cloth: array {a.Name}: bit {bit} set past {n} elements");
            }
        }
    }

    // ---- encode ----------------------------------------------------------------------------------------------------

    /// <summary>Assigns every array its offset by the layout rule (contiguous in part order from dword 0); returns the dword total.</summary>
    public uint Layout()
    {
        uint cur = 0;
        foreach (var a in Arrays)
        {
            if (a.Data.Length % 4 != 0) throw new MeshFormatException($"cloth: array {a.Name} is not a whole number of dwords");
            if (a.Spec.Element != ClothElement.Raw && a.Data.Length != Dwords(a.Owner, a.Spec) * 4)
                throw new MeshFormatException($"cloth: array {a.Name} holds {a.Data.Length / 4} dwords, {Dwords(a.Owner, a.Spec)} derived");
            a.Offset = cur;
            cur += (uint)a.Data.Length / 4;
        }
        return cur;
    }

    /// <summary>Part 0xF3 from the arrays (layout rule, zero padding to 16). Reproduces every shipped part byte for byte.</summary>
    public byte[] EncodePart()
    {
        uint total = Layout();
        long size = Vertex.AlignTo(total * 4L, 16);
        var outb = new byte[size];
        foreach (var a in Arrays) a.Data.CopyTo(outb, a.Offset * 4);
        Tail = outb.AsSpan((int)total * 4).ToArray();
        return outb;
    }

    /// <summary>
    /// Writes the array offsets and mapping counts into an image laid out like the decoded one (same object offsets):
    /// ClothEntityInFile offset fields and each mapping's +0x04/+0x08/+0x0C and offset fields. Call after <see cref="Layout"/>.
    /// </summary>
    public void PatchImage(byte[] image, int entityInFileOffset, IReadOnlyList<int> mappingOffsets)
    {
        void W(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at, 4), v);
        foreach (var s in EntityFields)
            if (Array(-1, s.Field) is { } a) W(entityInFileOffset + s.Field, a.Offset);
        foreach (var m in Mappings)
        {
            int o = mappingOffsets[m.Index];
            W(o + 4, m.VertexCount);
            W(o + 8, m.Unmapped);
            W(o + 12, m.Marked);
            foreach (var s in MappingFields)
                W(o + s.Field, Array(m.Index, s.Field) is { } a ? a.Offset : Absent);
        }
    }

    // ---- typed views -----------------------------------------------------------------------------------------------

    public static ushort[] ReadU16(ClothArray a, uint n)
    {
        var r = new ushort[n];
        for (int i = 0; i < n; i++) r[i] = BinaryPrimitives.ReadUInt16LittleEndian(a.Data.AsSpan(i * 2));
        return r;
    }

    public static uint[] ReadU32(ClothArray a)
    {
        var r = new uint[a.Data.Length / 4];
        for (int i = 0; i < r.Length; i++) r[i] = BinaryPrimitives.ReadUInt32LittleEndian(a.Data.AsSpan(i * 4));
        return r;
    }

    public static float[] ReadF32(ClothArray a)
    {
        var r = new float[a.Data.Length / 4];
        for (int i = 0; i < r.Length; i++) r[i] = BinaryPrimitives.ReadSingleLittleEndian(a.Data.AsSpan(i * 4));
        return r;
    }

    public static bool[] ReadBits(ClothArray a, uint n)
    {
        var r = new bool[n];
        for (int i = 0; i < n; i++) r[i] = (a.Data[i >> 3] >> (i & 7) & 1) != 0;
        return r;
    }

    public static byte[] WriteBits(IReadOnlyList<bool> bits)
    {
        var d = new byte[ElementDwords(ClothElement.Bits, (uint)bits.Count) * 4];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) d[i >> 3] |= (byte)(1 << (i & 7));
        return d;
    }

    /// <summary>The mapped mask of a mapping (all true when the mapping has no +0x38 array).</summary>
    public bool[] Mapped(ClothMapping m) =>
        Array(m.Index, 0x38) is { } a ? ReadBits(a, m.VertexCount) : Enumerable.Repeat(true, (int)m.VertexCount).ToArray();

    // ---- invariants ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The measured relations between the cloth arrays and the render mesh (every one holds on every shipped part; the
    /// remap relies on them). Empty when all hold.
    /// </summary>
    public List<string> Check(MeshModel model)
    {
        var p = new List<string>();
        foreach (var m in Mappings)
        {
            string at = $"mapping {m.Index} (entry {m.Entry})";
            var e = model.GeometryEntries[m.Entry];
            if (e.VertexCount != m.VertexCount) { p.Add($"{at}: {m.VertexCount} vertices, entry has {e.VertexCount}"); continue; }
            var mapped = Mapped(m);
            int nm = mapped.Count(x => x);
            if (nm != m.MappedCount) p.Add($"{at}: {nm} mapped vertices, {m.MappedCount} declared");
            if (Array(m.Index, 0x38) is null && m.Unmapped != 0) p.Add($"{at}: unmapped count {m.Unmapped} without a mapped mask");
            if (Array(m.Index, 0x3C) is { } rank)
            {
                var r = ReadU32(rank);
                uint k = 0;
                for (int i = 0; i < r.Length && i < mapped.Length; i++)
                    if (r[i] != (mapped[i] ? k++ : Absent)) { p.Add($"{at}: rank of vertex {i} is {r[i]}"); break; }
            }
            if (Array(m.Index, 0x2C) is { } mk)
            {
                var bits = ReadBits(mk, m.MappedCount);
                if (bits.Count(x => x) != m.Marked) p.Add($"{at}: {bits.Count(x => x)} marked, {m.Marked} declared");
                if (Array(m.Index, 0x30) is { } mv)
                {
                    var want = MarkedVertices(mapped, bits);
                    if (!ReadU32(mv).SequenceEqual(want)) p.Add($"{at}: marked vertex list is not the marked mapped vertices");
                }
            }
            else if (m.Marked != 0) p.Add($"{at}: {m.Marked} marked without a mask");
            if (Array(m.Index, 0x14) is { } tri)
            {
                uint tris = IndexCount / 3;
                if (ReadU16(tri, m.MappedCount).Any(t => t >= tris)) p.Add($"{at}: a triangle index >= {tris}");
            }
            if (e.Vertices is not { } v) { p.Add($"{at}: entry has no decodable vertices"); continue; }
            if (Array(m.Index, 0x10) is { } pos && !ReadF32(pos).AsSpan().SequenceEqual(v.Positions))
                p.Add($"{at}: positions copy differs from the vertex buffer");
            if (Array(m.Index, 0x4C) is { } j)
            {
                var jj = ReadU32(j);
                if (v.Joints is null || jj.Where((x, i) => x != v.Joints[i]).Any()) p.Add($"{at}: joints copy differs from the vertex buffer");
            }
            if (Array(m.Index, 0x50) is { } sm)
            {
                var sub = SubmeshOfVertex(e.Submeshes.Select(s => s.Indices).ToList(), (int)m.VertexCount, out string? why);
                var stored = ReadU32(sm);
                if (sub is null) p.Add($"{at}: {why}");
                else if (sub.Where((s, i) => s >= 0 && stored[i] != s).Any()) p.Add($"{at}: submesh array differs from the index buffer");
            }
        }
        return p;
    }

    /// <summary>Vertex index of every marked mapped vertex, ascending.</summary>
    internal static uint[] MarkedVertices(bool[] mapped, bool[] marked)
    {
        var r = new List<uint>();
        int k = 0;
        for (int i = 0; i < mapped.Length; i++)
        {
            if (!mapped[i]) continue;
            if (marked[k++]) r.Add((uint)i);
        }
        return r.ToArray();
    }

    /// <summary>
    /// The one submesh that references each vertex (−1: none — every shipped cloth entry references all its vertices);
    /// null (with the reason) when a vertex is used by several submeshes.
    /// </summary>
    internal static int[]? SubmeshOfVertex(IReadOnlyList<ushort[]> submeshIndices, int vertexCount, out string? why)
    {
        var sub = new int[vertexCount];
        System.Array.Fill(sub, -1);
        for (int s = 0; s < submeshIndices.Count; s++)
            foreach (var ix in submeshIndices[s])
            {
                if (ix >= vertexCount) { why = $"index {ix} >= {vertexCount}"; return null; }
                if (sub[ix] >= 0 && sub[ix] != s) { why = $"vertex {ix} is used by submeshes {sub[ix]} and {s}"; return null; }
                sub[ix] = s;
            }
        why = null;
        return sub;
    }

    /// <summary>One-line summary: particles, triangles, constraint counts, mappings, colliders.</summary>
    public string Summary() =>
        $"{ParticleCount} particles, {IndexCount / 3} triangles, {Sets.Sum(s => (long)s.Count) + Set3Count} constraints, "
        + $"{Mappings.Count} mapping(s), {Colliders.Count} collider(s)";
}
