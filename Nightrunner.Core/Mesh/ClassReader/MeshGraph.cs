namespace Nightrunner.Core.Mesh.ClassReader;

/// <summary>Where a geometry field lives: in the class-6 entry itself, or in the class-8 stream object it points to.</summary>
public readonly record struct GeoField(bool InStream, int Offset, int Size);

/// <summary>
/// The fixed object sizes and geometry field positions of one engine generation. DLTB and DL2 differ only in these
/// numbers, so the decoder reads through a layout instead of branching on a game id. Port of
/// <c>classreader/graph.py</c> <c>MeshLayout</c> + <c>GeometryEntryView</c> / <c>Dl2GeometryEntryView</c>.
/// </summary>
/// <param name="StreamPointer">Entry offset of the class-8 stream pointer; null when the entry holds everything.</param>
/// <param name="Raw34">The (C) trailing words of the entry: DLTB +0x34..+0x3F, DL2 +0x24..+0x2F.</param>
/// <param name="EntryRaws">DLTB-only (C) holes +0x12 u16, +0x14 u16, +0x17 u8; empty on DL2 (they read as 0).</param>
public sealed record MeshLayout(
    string Name, int RootSize, int EntitySize, int GeometrySize, int? StreamPointer, int StreamSize,
    GeoField SubmeshCount, GeoField Format, GeoField VertexBase, GeoField VertexCount, GeoField IndexBase,
    GeoField MaterialSlots, GeoField IndexCounts, GeoField PaletteDescs, GeoField? StreamSubmeshCount,
    GeoField Raw34, GeoField[] EntryRaws)
{
    /// <summary>Root 0x70, entity 0xE0, class-6 entry 0x40 holding format/bases/counts itself.</summary>
    public static readonly MeshLayout Dltb = new(
        "dltb", 0x70, 0xE0, 0x40, StreamPointer: null, StreamSize: 0,
        SubmeshCount: new(false, 0x10, 2), Format: new(false, 0x16, 1), VertexBase: new(false, 0x28, 4),
        VertexCount: new(false, 0x2C, 4), IndexBase: new(false, 0x30, 4),
        MaterialSlots: new(false, 0x08, 8), IndexCounts: new(false, 0x18, 8), PaletteDescs: new(false, 0x20, 8),
        StreamSubmeshCount: null, Raw34: new(false, 0x34, 12),
        EntryRaws: [new(false, 0x12, 2), new(false, 0x14, 2), new(false, 0x17, 1)]);

    /// <summary>
    /// Root 0x68, entity 0xD0, class-6 entry 0x30 that points (+0x08) at a 0x20-byte class-8 stream holding the
    /// index-count pointer, vertex/index bases, vertex count, an nsub copy and the vertex format.
    /// </summary>
    public static readonly MeshLayout Dl2 = new(
        "dl2", 0x68, 0xD0, 0x30, StreamPointer: 0x08, StreamSize: 0x20,
        SubmeshCount: new(false, 0x20, 4), Format: new(true, 0x18, 4), VertexBase: new(true, 0x08, 4),
        VertexCount: new(true, 0x0C, 4), IndexBase: new(true, 0x10, 4),
        MaterialSlots: new(false, 0x10, 8), IndexCounts: new(true, 0x00, 8), PaletteDescs: new(false, 0x18, 8),
        StreamSubmeshCount: new(true, 0x14, 4), Raw34: new(false, 0x24, 12), EntryRaws: []);

    public static readonly MeshLayout[] All = [Dltb, Dl2];

    public static MeshLayout ByName(string name) =>
        All.FirstOrDefault(l => l.Name == name) ?? throw new MeshFormatException($"unknown mesh layout hint '{name}'");

    public bool HasStream => StreamPointer is not null;

    /// <summary>
    /// The layout of an image, from the data only: class-3 root span, class-4 span per element, and class-8 records.
    /// All present signals must agree; a hint is used only when nothing decides, and a contradicting hint throws.
    /// </summary>
    public static MeshLayout Detect(Fixups fx, string? hint = null)
    {
        if (hint is not null) ByName(hint);
        var votes = new HashSet<string>();
        var recs = fx.Records;
        if (recs.Count > 0 && !recs[0].Secondary)
        {
            var (a, b) = fx.RecordSpan(0);
            foreach (var l in All) if (b - a == l.RootSize) votes.Add(l.Name);
        }
        for (int i = 0; i < recs.Count; i++)
        {
            var r = recs[i];
            if (r.Secondary || r.Count == 0) continue;
            if (r.ClassId == MeshGraph.ClassEntity)
            {
                var (a, b) = fx.RecordSpan(i);
                foreach (var l in All) if (b - a == (long)r.Count * l.EntitySize) { votes.Add(l.Name); break; }
            }
            else if (r.ClassId == MeshGraph.ClassStream)
                foreach (var l in All) if (l.HasStream) votes.Add(l.Name);
        }
        if (votes.Count == 1)
        {
            var found = votes.First();
            if (hint is not null && hint != found)
                throw new MeshFormatException($"mesh layout hint '{hint}' contradicts the data ({found})");
            return ByName(found);
        }
        if (votes.Count > 1)
            throw new MeshFormatException("mesh layout is ambiguous: the root/entity/class-8 records match both DLTB and DL2");
        if (hint is not null) return ByName(hint);
        throw new MeshFormatException("mesh layout not recognised: record 0 is neither a 0x70 (DLTB) nor a 0x68 (DL2) root");
    }
}

/// <summary>One class-6 record: <see cref="Count"/> consecutive geometry entries (a LOD chain) at an offset.</summary>
public readonly record struct GeometryArray(int Record, int Offset, int Count);

/// <summary>
/// Locates the known objects of a mesh image. Throws when the mandatory shape (record 0 = class-3 root, exactly one
/// class-4 array) is violated; everything else is optional.
/// </summary>
public sealed class MeshGraph
{
    public const uint ClassRoot = 3, ClassEntity = 4, ClassGeometryAux = 5, ClassGeometry = 6, ClassPalette = 7,
                      ClassStream = 8, ClassMaterialHeader = 10, ClassMaterialEntry = 11;
    public const int PaletteDescSize = 0x10, MaterialHeaderSize = 0x10, MaterialEntrySize = 0x20, GeometryAuxSize = 0x20;
    public const int MaxEntities = 65535, MaxSubmeshes = 4096, MaxMaterials = 4096, MaxPalette = 256;

    public Image Img { get; }
    public MeshLayout Layout { get; }
    public int RootOffset { get; }
    public int EntityRecord { get; }
    public int EntityOffset { get; }
    public int EntityCount { get; }
    public GeometryArray[] GeometryArrays { get; }
    public int? MaterialRecord { get; }
    public int? MaterialHeaderOffset { get; }

    public MeshGraph(Image img, string? layoutHint = null)
    {
        Img = img;
        var fx = img.Fixups;
        if (fx.Records.Count == 0) throw new MeshFormatException("empty record table");
        Layout = MeshLayout.Detect(fx, layoutHint);
        var r0 = fx.Records[0];
        if (r0.ClassId != ClassRoot || r0.Secondary || r0.Count != 1)
            throw new MeshFormatException($"record 0 is class {r0.ClassId} count {r0.Count}; expected the class-3 root");
        RootOffset = (int)r0.Offset;
        var ents = fx.RecordsOfClass(ClassEntity).ToList();
        if (ents.Count != 1)
            throw new MeshFormatException($"expected exactly one class-4 entity array, found {ents.Count}");
        (EntityRecord, var erec) = ents[0];
        if (erec.Count > MaxEntities) throw new MeshFormatException($"{erec.Count} entities exceed the u16 own-index range");
        EntityOffset = (int)erec.Offset;
        EntityCount = erec.Count;
        if ((long)EntityOffset + (long)EntityCount * Layout.EntitySize > img.Size)
            throw new MeshFormatException("entity array runs past the primary image");
        GeometryArrays = fx.RecordsOfClass(ClassGeometry).Select(t => new GeometryArray(t.Index, (int)t.Record.Offset, t.Record.Count)).ToArray();
        foreach (var ga in GeometryArrays)
            if ((long)ga.Offset + (long)ga.Count * Layout.GeometrySize > img.Size)
                throw new MeshFormatException($"geometry array record {ga.Record} runs past the primary image");
        var mats = fx.RecordsOfClass(ClassMaterialHeader).FirstOrDefault(t => true, (-1, default));
        if (mats.Index >= 0)
        {
            MaterialRecord = mats.Index;
            MaterialHeaderOffset = (int)mats.Record.Offset;
        }
    }

    // ---- root ----------------------------------------------------------------------------------------------

    public byte[]? RootName => Img.StringAt(RootOffset);
    public byte[]? ScrName => Img.IsSlot(RootOffset + 0x40) ? Img.StringAt(RootOffset + 0x40) : null;
    public uint RootEntityCount => Img.U32(RootOffset + 0x58);
    public byte[] RootRaw => Img.Raw(RootOffset, Layout.RootSize);

    public int EntityAt(int i)
    {
        if (i < 0 || i >= EntityCount) throw new MeshFormatException($"entity {i} out of range ({EntityCount})");
        return EntityOffset + i * Layout.EntitySize;
    }

    public GeometryArray? GeometryArrayAt(int offset)
    {
        foreach (var ga in GeometryArrays) if (ga.Offset == offset) return ga;
        return null;
    }

    /// <summary>Optional pointer: the field is a relocation slot, else null (the field holds a plain word).</summary>
    public Pointer? OptPointer(int off) => Img.IsSlot(off) ? Img.Pointer(off) : null;

    // ---- geometry entry fields through the layout -----------------------------------------------------------

    /// <summary>Image offset of an entry's class-8 stream, or null on a layout without one.</summary>
    public int? StreamOffset(int entryOff)
    {
        if (Layout.StreamPointer is not { } sp) return null;
        var t = Img.Pointer(entryOff + sp).Target
                ?? throw new MeshFormatException($"{Layout.Name} geometry entry 0x{entryOff:X}: null class-8 stream pointer");
        Img.Check(t, Layout.StreamSize);
        return t;
    }

    public ulong Field(int entryOff, int? streamOff, GeoField f)
    {
        int at = (f.InStream ? streamOff!.Value : entryOff) + f.Offset;
        return f.Size switch
        {
            1 => Img.U8(at), 2 => Img.U16(at), 4 => Img.U32(at), 8 => Img.U64(at),
            _ => throw new ArgumentOutOfRangeException(nameof(f)),
        };
    }

    public int? FieldPointer(int entryOff, int? streamOff, GeoField f) =>
        Img.Pointer((f.InStream ? streamOff!.Value : entryOff) + f.Offset).Target;

    public byte[] FieldRaw(int entryOff, int? streamOff, GeoField f) =>
        Img.Raw((f.InStream ? streamOff!.Value : entryOff) + f.Offset, f.Size);

    // ---- materials ------------------------------------------------------------------------------------------

    public ushort MaterialCount => MaterialHeaderOffset is { } h ? Img.U16(h + 0x08) : (ushort)0;
    public ushort MaterialCapacity => MaterialHeaderOffset is { } h ? Img.U16(h + 0x0A) : (ushort)0;

    /// <summary>Offset of the class-11 entry array, or null (no header / null pointer).</summary>
    public int? MaterialEntries => MaterialHeaderOffset is { } h ? Img.Pointer(h).Target : null;

    /// <summary>
    /// A class-11 name: a tagged pointer to a heap string, or (no slot) up to 7 characters stored inline in the
    /// 8-byte field (engine_pc <c>sky.mat</c>).
    /// </summary>
    public static (byte[]? Name, bool Inline, ushort Tag) MaterialName(Image img, int entryOff)
    {
        int f = entryOff + 0x08;
        if (!img.IsSlot(f))
        {
            var raw = img.Span(f, 8);
            if (raw[7] != 0)
                throw new MeshFormatException($"material entry 0x{entryOff:X}: inline name without terminator ({Convert.ToHexString(raw)})");
            int n = raw.IndexOf((byte)0);
            return (raw[..n].ToArray(), true, 0);
        }
        var p = img.Pointer(f);
        return (p.Target is { } t ? img.CString(t) : null, false, p.Tag);
    }

    /// <summary>(record index, class id, start, end) of every primary record that is not a decoded class.</summary>
    public IEnumerable<(int Record, uint ClassId, int Start, int End)> OpaqueRecords()
    {
        var recs = Img.Records;
        for (int i = 0; i < recs.Count; i++)
        {
            var r = recs[i];
            if (r.Secondary) continue;
            switch (r.ClassId)
            {
                case ClassRoot or ClassEntity or ClassGeometry or ClassPalette or ClassMaterialHeader or ClassMaterialEntry:
                    continue;
                case ClassStream when Layout.HasStream:
                    continue;
            }
            var (a, b) = Img.RecordSpan(i);
            yield return (i, r.ClassId, a, b);
        }
    }
}
