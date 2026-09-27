using System.Buffers.Binary;
using Nightrunner.Core.Mesh.ClassReader;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Core.Mesh;

/// <summary>
/// Mesh resource (parts 0x10/0x11/0x12/0xF0/0xF1/0xF3) → <see cref="MeshModel"/>. Port of <c>mesh/decode.py</c>.
/// </summary>
/// <remarks>
/// Decodes every class-6 array and every entry in it (multi-entry arrays are LOD chains), every submesh, every
/// entity and the material table, and keeps every other record as an opaque span. Nothing is skipped or guessed: an
/// entry in an unsupported vertex format stays in the model with <c>Vertices = null</c> and a warning; a resource
/// without vertex/index parts (skeleton-only) decodes with empty buffers.
/// </remarks>
public static class MeshDecoder
{
    public const byte PartImage = 0x10, PartFixups = 0x11, PartSkin = 0x12, PartVertex = 0xF0, PartIndex = 0xF1, PartCloth = 0xF3;
    public const int MaxVerticesPerEntry = 1 << 24;

    /// <summary>The first part of each mesh part type, read out of the pack (copies: the model outlives the map).</summary>
    public static MeshParts ReadParts(RpackFile pack, int logicalIndex)
    {
        var lg = pack.Logicals[logicalIndex];
        string name = pack.Name(logicalIndex);
        if (lg.Type != 0x10) throw new MeshFormatException($"'{name}' is type 0x{lg.Type:X2}, not a mesh");
        var parts = new Dictionary<byte, byte[]>();
        for (int k = 0; k < lg.PartCount; k++)
        {
            int phys = (int)lg.FirstPart + k;
            byte t = pack.PartType(phys);
            if (parts.ContainsKey(t)) continue;
            if (pack.PartUnreadableReason(phys) is { } why)
                throw new MeshFormatException($"'{name}': part 0x{t:X2} unreadable — {why}");
            parts[t] = pack.ReadPart(phys);
        }
        foreach (var t in new[] { PartImage, PartFixups })
            if (!parts.ContainsKey(t)) throw new MeshFormatException($"'{name}': missing part 0x{t:X2}");
        return new MeshParts(name, parts);
    }

    /// <summary>
    /// Only the material table (all <c>capacity</c> entries, skin-only included) — reads parts 0x10 and 0x11 and nothing
    /// else, for scans over every mesh ("used by"). Empty when the mesh has no material header.
    /// </summary>
    public static string[] MaterialTable(RpackFile pack, int logicalIndex)
    {
        var lg = pack.Logicals[logicalIndex];
        byte[]? image = null, fixups = null;
        for (int k = 0; k < lg.PartCount && (image is null || fixups is null); k++)
        {
            int phys = (int)lg.FirstPart + k;
            byte t = pack.PartType(phys);
            if (t == PartImage && image is null) image = pack.ReadPart(phys);
            else if (t == PartFixups && fixups is null) fixups = pack.ReadPart(phys);
        }
        if (image is null || fixups is null) return [];
        var img = new Image(image, Fixups.Parse(fixups));
        var g = new MeshGraph(img);
        if (g.MaterialEntries is not { } first) return [];
        var names = new string[g.MaterialCapacity];
        for (int i = 0; i < names.Length; i++)
            names[i] = MeshGraph.MaterialName(img, first + i * MeshGraph.MaterialEntrySize).Name is { } n ? MeshModel.Text(n) : "";
        return names;
    }

    public static MeshModel Decode(RpackFile pack, int logicalIndex, string? layoutHint = null) =>
        Decode(ReadParts(pack, logicalIndex), layoutHint);

    public static MeshModel Decode(MeshParts p, string? layoutHint = null) =>
        Decode(p.Name, p.Image!, p.Fixups!, p.Vertex, p.Index, p.Skin, p.Cloth, layoutHint);

    public static MeshModel Decode(string name, byte[] imageBytes, byte[] fixupsBytes, byte[]? vertex = null,
                                   byte[]? index = null, byte[]? skin = null, byte[]? cloth = null, string? layoutHint = null)
    {
        var fx = Fixups.Parse(fixupsBytes);
        var img = new Image(imageBytes, fx);
        var g = new MeshGraph(img, layoutHint);
        var warnings = new List<string>();
        if (fx.SecondaryPresentFlag) warnings.Add("fixups declare a secondary image (parsed, not interpreted)");
        var bad = fx.UnsupportedSlots().ToList();
        if (bad.Count > 0)
            warnings.Add($"{bad.Count} relocation slots of kinds [{string.Join(", ", bad.Select(s => s.Kind).Distinct().Order())}] are not resolvable offline");

        var embedded = g.RootName ?? throw new MeshFormatException("root name pointer is null");
        var scr = g.ScrName;

        // ---- geometry arrays → flat entry list -------------------------------------------------------------
        var entries = new List<GeometryEntry>();
        var arrayFirst = new Dictionary<int, int>();
        foreach (var ga in g.GeometryArrays)
        {
            arrayFirst[ga.Record] = entries.Count;
            for (int k = 0; k < ga.Count; k++)
                entries.Add(DecodeEntry(g, ga.Offset + k * g.Layout.GeometrySize, entries.Count, ga.Record, k, vertex, index, warnings));
        }
        if (g.GeometryArrays.Length == 0 && vertex is { Length: > 0 })
            warnings.Add("vertex buffer present but no class-6 geometry array");

        // ---- entities ---------------------------------------------------------------------------------------
        var entities = new MeshEntity[g.EntityCount];
        var owned = new Dictionary<int, int>();
        for (int i = 0; i < g.EntityCount; i++)
        {
            int e = g.EntityAt(i);
            byte geometryCount = img.U8(e + 0xC9);
            var gp = g.OptPointer(e + 0x88);
            int? rec = null;
            var ownedEntries = new List<int>();
            if (gp is { Target: { } gt })
            {
                if (g.GeometryArrayAt(gt) is not { } ga)
                    warnings.Add($"entity {i}: +0x88 → 0x{gt:X} is not the start of a class-6 record");
                else
                {
                    rec = ga.Record;
                    int first = arrayFirst[ga.Record];
                    for (int k = first; k < first + ga.Count; k++) ownedEntries.Add(k);
                    if (ga.Count != geometryCount)
                        warnings.Add($"entity {i}: geometry_count {geometryCount} != class-6 record count {ga.Count}");
                    foreach (var k in ownedEntries)
                    {
                        if (owned.TryGetValue(k, out int prev)) warnings.Add($"geometry entry {k} owned by entities {prev} and {i}");
                        owned[k] = i;
                    }
                }
            }
            else if (geometryCount != 0)
                warnings.Add($"entity {i}: geometry_count {geometryCount} but no geometry pointer");

            var ap = g.OptPointer(e + 0x80);
            int? auxOff = null;
            byte[] rawAux = [];
            if (ap is { Target: { } at })
            {
                auxOff = at;
                if (img.RecordAt(at) is { } ri && img.Records[ri].ClassId == MeshGraph.ClassGeometryAux)
                {
                    var (a, b) = img.RecordSpan(ri);
                    rawAux = img.Raw(a, b - a);
                }
                else
                {
                    rawAux = img.Raw(at, MeshGraph.GeometryAuxSize * Math.Max(1, (int)geometryCount));
                    warnings.Add($"entity {i}: +0x80 → 0x{at:X} is not a class-5 record");
                }
            }
            ushort own = img.U16(e + 0xC4);
            if (own != i) warnings.Add($"entity {i}: own index field is {own}");
            short parent = img.I16(e + 0xC6);
            if (parent < -1 || parent >= g.EntityCount || parent == i)
                throw new MeshFormatException($"entity {i}: invalid parent {parent}");
            var entName = img.StringAt(e + 0x78) ?? [];
            entities[i] = new MeshEntity
            {
                Index = i, Offset = e, Name = entName, Parent = parent,
                Local = img.F32s(e, 12), InvBind = img.F32s(e + 0x30, 12),
                BoundsCenter = img.F32s(e + 0x60, 3), BoundsHalf = img.F32s(e + 0x6C, 3),
                Flags = img.U32(e + 0xC0), Type = img.U8(e + 0xC8), GeometryCount = geometryCount,
                GeometryArrayRecord = rec, GeometryEntries = ownedEntries.ToArray(), AuxOffset = auxOff, RawAux = rawAux,
                Raw90 = img.Raw(e + 0x90, 0x30), RawCa = img.Raw(e + 0xCA, g.Layout.EntitySize - 0xCA),
            };
        }
        foreach (var en in entries)
        {
            en.OwnerEntity = owned.TryGetValue(en.Index, out int o) ? o : null;
            if (en.OwnerEntity is null)
                warnings.Add($"geometry entry {en.Index} (record {en.ArrayRecord}) is not owned by any entity");
        }
        if (g.RootEntityCount != g.EntityCount)
            warnings.Add($"root entity count {g.RootEntityCount} != class-4 record count {g.EntityCount}");

        // ---- materials ----------------------------------------------------------------------------------------
        var materials = new List<MeshMaterial>();
        int capacity = 0;
        if (g.MaterialHeaderOffset is { } mh)
        {
            capacity = g.MaterialCapacity;
            int n = g.MaterialCount;
            if (n > MeshGraph.MaxMaterials) throw new MeshFormatException($"material table: {n} entries exceed {MeshGraph.MaxMaterials}");
            if (n > 0)
            {
                int first = g.MaterialEntries ?? throw new MeshFormatException("material table: null entry pointer with a non-zero count");
                for (int k = 0; k < n; k++)
                {
                    int off = first + k * MeshGraph.MaterialEntrySize;
                    var (nm, inline, tag) = MeshGraph.MaterialName(img, off);
                    if (nm is null)
                    {
                        warnings.Add($"material {k}: null name pointer");
                        nm = [];
                    }
                    materials.Add(new MeshMaterial(k, off, nm, tag, img.Raw(off, MeshGraph.MaterialEntrySize), inline));
                }
            }
        }
        foreach (var en in entries)
            foreach (var s in en.Submeshes)
            {
                if (s.MaterialSlot >= materials.Count)
                    warnings.Add($"entry {en.Index} submesh {s.Index}: material slot {s.MaterialSlot} outside the table ({materials.Count})");
                foreach (var pe in s.Palette)
                    if (pe >= entities.Length)
                        throw new MeshFormatException($"entry {en.Index} submesh {s.Index}: palette entity {pe} >= {entities.Length}");
            }

        // ---- opaque records (class 5 is carried on its entity) --------------------------------------------------
        var opaque = g.OpaqueRecords().Where(t => t.ClassId != MeshGraph.ClassGeometryAux)
            .Select(t => new OpaqueObject(t.Record, t.ClassId, t.Start, t.End - t.Start, img.Raw(t.Start, t.End - t.Start)))
            .ToArray();

        return new MeshModel
        {
            Name = name, Layout = g.Layout.Name, EmbeddedName = embedded, ScrName = scr, Image = img,
            Entities = entities, GeometryEntries = entries.ToArray(), Materials = materials.ToArray(),
            MaterialCapacity = capacity, MaterialHeaderOffset = g.MaterialHeaderOffset, RootRaw = g.RootRaw,
            Opaque = opaque, VertexBuffer = vertex, IndexBuffer = index, SkinRaw = skin, ClothRaw = cloth,
            Warnings = warnings,
        };
    }

    private static GeometryEntry DecodeEntry(MeshGraph g, int off, int index, int record, int element,
                                             byte[]? vbuf, byte[]? ibuf, List<string> warnings)
    {
        var L = g.Layout;
        var img = g.Img;
        int? stream = g.StreamOffset(off);
        ulong nsubRaw = g.Field(off, stream, L.SubmeshCount);
        if (nsubRaw > MeshGraph.MaxSubmeshes)
            throw new MeshFormatException($"{L.Name} geometry entry 0x{off:X}: {nsubRaw} submeshes exceed {MeshGraph.MaxSubmeshes}");
        int nsub = (int)nsubRaw;
        ulong fmtRaw = g.Field(off, stream, L.Format);
        if (fmtRaw > 0xFF) throw new MeshFormatException($"{L.Name} geometry entry 0x{off:X}: vertex format {fmtRaw} out of range");
        int fmt = (int)fmtRaw;
        long vb = (long)g.Field(off, stream, L.VertexBase);
        int nv = (int)g.Field(off, stream, L.VertexCount);
        long ib = (long)g.Field(off, stream, L.IndexBase);
        if (L.StreamSubmeshCount is { } ssc && g.Field(off, stream, ssc) is var sn && sn != (ulong)nsub)
            warnings.Add($"entry {index}: class-8 submesh count {sn} != entry count {nsub}");

        int? slotsOff = nsub > 0 ? g.FieldPointer(off, stream, L.MaterialSlots) : null;
        int? countsOff = nsub > 0 ? g.FieldPointer(off, stream, L.IndexCounts) : null;
        int? descsOff = nsub > 0 ? g.FieldPointer(off, stream, L.PaletteDescs) : null;
        if (nsub > 0)
        {
            if (slotsOff is null) throw new MeshFormatException($"geometry entry 0x{off:X}: null material-slot pointer with {nsub} submeshes");
            if (countsOff is null) throw new MeshFormatException($"geometry entry 0x{off:X}: null index-count pointer with {nsub} submeshes");
            if (descsOff is null) throw new MeshFormatException($"geometry entry 0x{off:X}: null palette pointer with {nsub} submeshes");
        }
        var slots = nsub > 0 ? img.U16s(slotsOff!.Value, nsub) : [];
        var counts = nsub > 0 ? img.U32s(countsOff!.Value, nsub) : [];

        var submeshes = new Submesh[nsub];
        long cursor = ib;
        for (int k = 0; k < nsub; k++)
        {
            int d = descsOff!.Value + k * MeshGraph.PaletteDescSize;
            var dptr = img.Pointer(d);
            ulong pcount = img.U64(d + 8);
            ushort[] pal = [];
            if (pcount > 0)
            {
                if (pcount > MeshGraph.MaxPalette) throw new MeshFormatException($"palette at 0x{d:X}: {pcount} entries exceed {MeshGraph.MaxPalette}");
                if (dptr.Target is not { } pt) throw new MeshFormatException($"palette at 0x{d:X}: count {pcount} with a null pointer");
                pal = img.U16s(pt, (int)pcount);
            }
            int cnt = (int)counts[k];
            ushort[] idx;
            if (ibuf is not null)
            {
                if (cursor + cnt * 2L > ibuf.Length)
                    throw new MeshFormatException($"entry {index} submesh {k}: indices [{cursor}, +{cnt * 2L}) exceed the index buffer ({ibuf.Length})");
                idx = new ushort[cnt];
                var src = ibuf.AsSpan((int)cursor, cnt * 2);
                for (int i = 0; i < cnt; i++) idx[i] = BinaryPrimitives.ReadUInt16LittleEndian(src[(i * 2)..]);
            }
            else
            {
                idx = [];
                if (cnt != 0) warnings.Add($"entry {index} submesh {k}: {cnt} indices declared but no index part");
            }
            if (cnt % 3 != 0) warnings.Add($"entry {index} submesh {k}: index count {cnt} is not a multiple of 3");
            if (idx.Length > 0 && nv > 0 && idx.Max() is var mx && mx >= nv)
                warnings.Add($"entry {index} submesh {k}: index {mx} >= vertex count {nv}");
            submeshes[k] = new Submesh
            {
                Index = k, MaterialSlot = slots[k], IndexCount = cnt, IndexBase = cursor, Palette = pal,
                PaletteDescOffset = d, PaletteOffset = dptr.Target, Indices = idx,
            };
            cursor += cnt * 2L;
        }

        VertexData? vertices = null;
        if (!Vertex.Supported(fmt))
            warnings.Add($"entry {index}: unsupported vertex format {fmt} (vertices kept raw in the buffer)");
        else if (vbuf is not null)
        {
            if (nv > MaxVerticesPerEntry) throw new MeshFormatException($"entry {index}: {nv} vertices exceed the offline bound");
            vertices = Vertex.Decode(vbuf, vb, nv, fmt);
        }
        else if (nv != 0)
            warnings.Add($"entry {index}: {nv} vertices declared but no vertex part");

        int Raw(int k) => k < L.EntryRaws.Length ? (int)g.Field(off, stream, L.EntryRaws[k]) : 0;
        return new GeometryEntry
        {
            Index = index, ArrayRecord = record, Element = element, Offset = off, Format = fmt,
            VertexBase = vb, VertexCount = nv, IndexBase = ib, Submeshes = submeshes,
            Raw00 = img.Raw(off, 8), Raw12 = Raw(0), Raw14 = Raw(1), Raw17 = Raw(2),
            Raw34 = g.FieldRaw(off, stream, L.Raw34),
            MaterialSlotsOffset = slotsOff, IndexCountsOffset = countsOff, Vertices = vertices,
            StreamOffset = stream, RawStream = stream is { } so ? img.Raw(so, L.StreamSize) : [],
        };
    }
}

/// <summary>A mesh resource's parts by type (first part of each type).</summary>
public sealed record MeshParts(string Name, IReadOnlyDictionary<byte, byte[]> ByType)
{
    public byte[]? Image => ByType.GetValueOrDefault(MeshDecoder.PartImage);
    public byte[]? Fixups => ByType.GetValueOrDefault(MeshDecoder.PartFixups);
    public byte[]? Skin => ByType.GetValueOrDefault(MeshDecoder.PartSkin);
    public byte[]? Vertex => ByType.GetValueOrDefault(MeshDecoder.PartVertex);
    public byte[]? Index => ByType.GetValueOrDefault(MeshDecoder.PartIndex);
    public byte[]? Cloth => ByType.GetValueOrDefault(MeshDecoder.PartCloth);
}
