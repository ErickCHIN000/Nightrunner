using System.Buffers.Binary;
using Nightrunner.Core.Mesh.ClassReader;

namespace Nightrunner.Core.Mesh;

/// <summary>
/// Carries a mesh's cloth data through a geometry rebuild (<see cref="MeshRebuild"/>).
/// </summary>
/// <remarks>
/// What the cloth references in the render mesh (measured on all 58 DLTB parts / 63 mappings, <c>MeshCheck --cloth</c>):
/// only the render vertices of the cloth entity's LOD entries, through the visual mappings. Per render vertex: a bit-exact
/// copy of its position (+0x10), mask bits (+0x28, +0x38 = bound to the simulation mesh), the rank among bound vertices
/// (+0x3C), full-precision normal / tangent / weights (+0x40/+0x44/+0x48), joints (+0x4C, equal to the vertex buffer's)
/// and its submesh (+0x50). Per bound vertex, in vertex order: the simulation triangle (+0x14), three quantised binding
/// values (+0x18/+0x1C/+0x20/+0x24) and a mask (+0x2C); per marked bound vertex, its vertex index (+0x30) and a vector
/// (+0x34). Nothing references render triangles, submesh order or other entries; the simulation mesh (entity arrays)
/// references no render vertex.
/// <para/>
/// So a rebuild where every output vertex of a cloth entry comes from one original vertex (<see cref="EntryPlan.RawIds"/>
/// ≥ 0: kept, reordered, deleted or duplicated) is carried exactly: every per-vertex datum moves with its vertex, ranks,
/// marked lists and counts are re-derived, positions / joints / submesh re-copied from the rebuilt entry, normal / tangent /
/// weights re-copied only where the vertex's own frame / weight bytes changed. A new vertex (no source) is refused: its
/// binding to the simulation mesh would have to be computed. A moved bound vertex keeps its binding (not recomputed) and
/// is reported.
/// </remarks>
public static class ClothRebuild
{
    public const string RefusalPrefix = "cloth: ";

    public sealed record Outcome(bool Changed, List<string> Warnings, int RemappedEntries);

    /// <summary>Updates <paramref name="cloth"/> in place for the planned entries; refuses (MeshBuildException) what it cannot carry.</summary>
    public static Outcome Apply(MeshModel model, ClothData cloth, RebuildPlan plan)
    {
        var warnings = new List<string>();
        bool changed = false;
        int remapped = 0;
        foreach (var m in cloth.Mappings)
        {
            var ep = plan.Entries[m.Entry];
            var e = model.GeometryEntries[m.Entry];
            if (!ep.Changed || ep.Records is null) continue;
            if (e.Vertices is not { } oldV) throw new MeshBuildException($"{RefusalPrefix}entry {m.Entry} has no decodable vertices");
            var ids = ep.RawIds ?? throw new MeshBuildException($"{RefusalPrefix}entry {m.Entry}: no vertex ids (internal)");
            var newV = new VertexData(e.Format, ep.Records);
            int n = newV.Count;
            for (int i = 0; i < n; i++)
                if (ids[i] < 0 || ids[i] >= oldV.Count)
                    throw new MeshBuildException($"{RefusalPrefix}entry {m.Entry} (cloth mapping {m.Index}): vertex {i} is new (no source vertex); "
                                                 + "its binding to the cloth simulation mesh cannot be computed. Keep every cloth vertex traceable "
                                                 + "(bp_vertex_id, or an exact position match): reordering, deleting and duplicating vertices are supported");
            bool identity = n == oldV.Count && ids.Select((x, i) => x == i).All(x => x);
            if (Remap(cloth, m, e, oldV, newV, ids, ep, warnings)) changed = true;
            if (!identity) remapped++;
        }
        return new Outcome(changed, warnings, remapped);
    }

    private static bool Remap(ClothData cloth, ClothMapping m, GeometryEntry e, VertexData oldV, VertexData newV, long[] ids, EntryPlan ep,
                              List<string> warnings)
    {
        int n = newV.Count, k = m.Index;
        string at = $"entry {m.Entry} (cloth mapping {k})";
        var before = cloth.Arrays.Where(a => a.Owner == k).Select(a => a.Data).ToList();
        uint oldCount = m.VertexCount, oldUnmapped = m.Unmapped, oldMarked = m.Marked;
        var mappedOld = cloth.Mapped(m);
        var compactOld = new int[oldV.Count];
        int c = 0;
        for (int i = 0; i < mappedOld.Length; i++) compactOld[i] = mappedOld[i] ? c++ : -1;
        bool[]? markedOld = cloth.Array(k, 0x2C) is { } mk ? ClothData.ReadBits(mk, m.MappedCount) : null;
        var markedRowOld = new int[m.MappedCount];
        if (markedOld is not null)
        {
            int r = 0;
            for (int i = 0; i < markedOld.Length; i++) markedRowOld[i] = markedOld[i] ? r++ : -1;
        }

        var o = Vertex.OffsetsOf(e.Format);
        int stride = Vertex.Stride(e.Format);
        bool FieldChanged(int i, int off, int len) =>
            !newV.Raw.AsSpan(i * stride + off, len).SequenceEqual(oldV.Raw.AsSpan((int)ids[i] * stride + off, len));

        int[]? sub = null;
        if (cloth.Array(k, 0x50) is not null)
        {
            sub = ClothData.SubmeshOfVertex(ep.Submeshes.OrderBy(s => s.Index).Select(s => s.Indices).ToList(), n, out string? why);
            if (sub is null) throw new MeshBuildException($"{RefusalPrefix}{at}: {why}; the cloth stores one submesh per vertex");
        }
        if (cloth.Array(k, 0x4C) is not null && newV.Joints is null)
            throw new MeshBuildException($"{RefusalPrefix}{at}: the cloth stores joints but vertex format {e.Format} has none");

        // new vertex → mapped / compact source
        var mappedNew = new bool[n];
        var compactSrc = new List<int>();
        for (int i = 0; i < n; i++)
        {
            int src = (int)ids[i];
            mappedNew[i] = mappedOld[src];
            if (mappedNew[i]) compactSrc.Add(compactOld[src]);
        }
        int moved = 0;
        for (int i = 0; i < n; i++)
            if (mappedNew[i] && FieldChanged(i, o.Pos, e.Format == 0 ? 6 : 12)) moved++;

        foreach (var a in cloth.Arrays.Where(a => a.Owner == k))
        {
            int f = a.Spec.Field;
            a.Data = f switch
            {
                0x10 => F32(newV.Positions),
                0x28 => ClothData.WriteBits(Gather(ClothData.ReadBits(a, oldCount), ids)),
                0x38 => ClothData.WriteBits(mappedNew),
                0x3C => U32(Rank(mappedNew)),
                0x40 => Rows(a, 3, ids, i => FieldChanged(i, o.Qtan, 8), i => newV.Normals.AsSpan(i * 3, 3)),
                0x44 => Rows(a, 3, ids, i => FieldChanged(i, o.Qtan, 8), i => newV.Tangents.AsSpan(i * 3, 3)),
                0x48 => Rows(a, 4, ids, i => FieldChanged(i, o.Weights, 4), i => newV.Weights!.AsSpan(i * 4, 4)),
                0x4C => U32(newV.Joints!.Select(x => (uint)x).ToArray()),
                // the submesh that uses the vertex; an unused vertex (not drawn) keeps its source's value
                0x50 => U32(sub!.Select((s, i) => s >= 0 ? (uint)s : BinaryPrimitives.ReadUInt32LittleEndian(a.Data.AsSpan((int)ids[i] * 4))).ToArray()),
                0x14 => Elements(a, 0, 2, compactSrc),
                0x18 or 0x20 or 0x24 => Elements(a, 8, 2, compactSrc),
                0x1C => Elements(a, 8, 1, compactSrc),
                0x2C => ClothData.WriteBits(compactSrc.Select(s => markedOld![s]).ToArray()),
                0x30 => U32(ClothData.MarkedVertices(mappedNew, compactSrc.Select(s => markedOld![s]).ToArray())),
                0x34 => MarkedRows(a, compactSrc.Where(s => markedOld![s]).Select(s => markedRowOld[s]).ToList()),
                _ => throw new MeshBuildException($"{RefusalPrefix}{at}: array {a.Name} has no remap rule (internal)"),
            };
        }
        m.VertexCount = (uint)n;
        m.Unmapped = (uint)mappedNew.Count(x => !x);
        m.Marked = markedOld is null ? 0 : (uint)compactSrc.Count(s => markedOld[s]);
        if (cloth.Array(k, 0x38) is null && m.Unmapped != 0) throw new MeshBuildException($"{RefusalPrefix}{at}: unmapped vertices without a mask (internal)");

        if (moved > 0)
            warnings.Add($"{RefusalPrefix}{at}: {moved} moved vertices are bound to the simulation mesh; their binding is kept (not recomputed), "
                         + "so while the cloth simulates they follow their old place on it");
        var after = cloth.Arrays.Where(a => a.Owner == k).Select(a => a.Data).ToList();
        return m.VertexCount != oldCount || m.Unmapped != oldUnmapped || m.Marked != oldMarked
               || before.Zip(after).Any(p => !p.First.AsSpan().SequenceEqual(p.Second));
    }

    private static bool[] Gather(bool[] src, long[] ids) => ids.Select(i => src[i]).ToArray();

    private static uint[] Rank(bool[] mapped)
    {
        var r = new uint[mapped.Length];
        uint k = 0;
        for (int i = 0; i < r.Length; i++) r[i] = mapped[i] ? k++ : ClothData.Absent;
        return r;
    }

    private static byte[] U32(uint[] v)
    {
        var d = new byte[v.Length * 4];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(i * 4), v[i]);
        return d;
    }

    private static byte[] F32(float[] v)
    {
        var d = new byte[v.Length * 4];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(i * 4), v[i]);
        return d;
    }

    /// <summary>Per-vertex float rows: the source vertex's row, or the rebuilt vertex's value where its own bytes changed.</summary>
    private static byte[] Rows(ClothArray a, int w, long[] ids, Func<int, bool> changed, Func<int, ReadOnlySpan<float>> fresh)
    {
        var d = new byte[ids.Length * w * 4];
        for (int i = 0; i < ids.Length; i++)
        {
            if (changed(i))
            {
                var v = fresh(i);
                for (int j = 0; j < w; j++) BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan((i * w + j) * 4), v[j]);
            }
            else a.Data.AsSpan((int)ids[i] * w * 4, w * 4).CopyTo(d.AsSpan(i * w * 4));
        }
        return d;
    }

    /// <summary>Header (kept) + elements of <paramref name="size"/> bytes gathered by compact source index, zero-padded to a dword.</summary>
    private static byte[] Elements(ClothArray a, int header, int size, List<int> src)
    {
        int len = header + src.Count * size;
        var d = new byte[(len + 3) / 4 * 4];
        a.Data.AsSpan(0, header).CopyTo(d);
        for (int i = 0; i < src.Count; i++) a.Data.AsSpan(header + src[i] * size, size).CopyTo(d.AsSpan(header + i * size));
        return d;
    }

    private static byte[] MarkedRows(ClothArray a, List<int> rows)
    {
        var d = new byte[rows.Count * 12];
        for (int i = 0; i < rows.Count; i++) a.Data.AsSpan(rows[i] * 12, 12).CopyTo(d.AsSpan(i * 12));
        return d;
    }

    /// <summary>
    /// Lays out the cloth part and writes its offsets / mapping counts into a rebuilt image (whose cloth objects may have
    /// moved): the objects are found again through the cloth entity's +0xD0 pointer.
    /// </summary>
    public static (byte[] Part, byte[] Image) Write(ClothData cloth, byte[] image, byte[] fixups)
    {
        var part = cloth.EncodePart();
        var img = new Image(image, Fixups.Parse(fixups));
        var g = new MeshGraph(img);
        int ent = g.EntityAt(cloth.EntityIndex);
        int inFile = img.Pointer(ent + ClothData.EntityField).Target ?? throw new MeshBuildException($"{RefusalPrefix}rebuilt image lost the cloth pointer");
        int cef = img.Pointer(inFile + 0x10).Target ?? throw new MeshBuildException($"{RefusalPrefix}rebuilt image lost ClothEntityInFile");
        var mapOffsets = new List<int>();
        if (cloth.Mappings.Count > 0)
        {
            int mo = img.Pointer(cef + 0x118).Target ?? throw new MeshBuildException($"{RefusalPrefix}rebuilt image lost the visual mappings");
            for (int k = 0; k < cloth.Mappings.Count; k++) mapOffsets.Add(mo + k * ClothData.MappingSize);
        }
        var outImage = (byte[])image.Clone();
        cloth.PatchImage(outImage, cef, mapOffsets);
        return (part, outImage);
    }
}
