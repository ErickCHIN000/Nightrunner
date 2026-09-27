using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Nightrunner.Core.Cast;
using Nightrunner.Core.Mesh.ClassReader;

namespace Nightrunner.Core.Mesh;

/// <summary>Patched image + fixups, the new skin part when materials were added (else null), and the patch report.</summary>
public sealed record PatchedImage(byte[] Image, byte[] Fixups, byte[]? Skin, JsonObject Report);

/// <summary>
/// Image (0x10) + fixups (0x11) edits for a <see cref="RebuildPlan"/>, through <see cref="ImagePatch"/>. Port of
/// <c>mesh/imagepatch.py</c>, except the material table (see below). Everything not listed is carried verbatim.
/// </summary>
/// <remarks>
/// <code>
/// geometry entry  +0x10 u16 submesh count; +0x28/+0x2C/+0x30 vertex base / count / index base;
///                 u16 material slot[nsub] and u32 index count[nsub] in place when nsub did not grow, else appended
///                 (2/4-aligned) and the +0x08 / +0x18 slot retargeted;
///                 class-7 palette descriptors reused when nsub did not grow (a changed palette gets a new u16 array),
///                 else a new descriptor array (8-aligned), the +0x20 slot retargeted and the class-7 record relocated
/// material table  GROWN, never overwritten (below)
/// entity          +0x60 f32[6] bounds when its geometry changed (skinned: exact AABB; static: grow-only)
/// root            +0x00 name pointer → appended "&lt;logical&gt;.msh" when the logical name differs
/// </code>
/// <b>Materials (deliberate divergence).</b> The prototype writes a new material into class-11 entry <c>count</c> and
/// increments <c>count</c>. Entries <c>count..capacity-1</c> are not spares: they are the skin-only materials that skins'
/// <c>Replace</c> pairs point at (every one of them is a Replace target), so that overwrites a skin's material. Here the
/// class-11 array is re-appended with <c>capacity + k</c> entries: the mesh's own entries, then the k new ones (so they
/// become slots <c>count..count+k-1</c>), then the skin-only entries; the class-10 header pointer is retargeted, count and
/// capacity rewritten, the class-11 record relocated, and every skin <c>Replace</c> index ≥ <c>count</c> shifted by k in
/// part 0x12 — each skin resolves to the same materials as before. Entry words are copied with their relocation slots
/// (census 2026-09-23, 21,408 DLTB meshes: slots only at +0x08 (tagged name) and +0x10 (plain pointer); no pointer from
/// elsewhere targets the array; the record count always equals capacity). A new entry is zero except its name (the
/// shipped string shape {u32 len, u32 len} chars NUL, tag 0x2100) and +0x18..+0x1F copied from the last own entry (C).
/// Anything else (a slot kind that cannot be copied, a pointer into the table, a record that does not match) is refused.
/// </remarks>
public static class MeshImagePatch
{
    public const ushort MaterialNameTag = 0x2100;

    public static PatchedImage Apply(MeshModel model, RebuildPlan plan, IReadOnlyList<(long Vertex, long Index)> bases)
    {
        if (model.Layout != MeshLayout.Dltb.Name)
            throw new MeshBuildException($"{CastImport.Repr(model.Name)}: the image patcher only supports the DLTB mesh layout (got {model.Layout})");
        var patch = new ImagePatch(model.Image);
        var materialsAdded = new JsonArray();
        int arraysAppended = 0, recordsRelocated = 0;
        var boundsRewritten = new JsonArray();
        int slotsBefore = patch.Fixups.Slots.Count;
        var rep = new JsonObject();

        // ---- materials ---------------------------------------------------------------------------------------------
        var newSlot = new Dictionary<string, int>(StringComparer.Ordinal);
        byte[]? skin = null;
        JsonObject? grown = null;
        if (plan.NewMaterials.Count > 0)
            (skin, grown) = GrowMaterials(model, plan.NewMaterials, patch, newSlot, materialsAdded);

        // ---- geometry entries --------------------------------------------------------------------------------------
        for (int k = 0; k < plan.Entries.Count; k++)
        {
            var ep = plan.Entries[k];
            var (vb, ib) = bases[k];
            var e = ep.Entry;
            patch.WriteU32(e.Offset + 0x28, checked((uint)vb));
            patch.WriteU32(e.Offset + 0x2C, (uint)(ep.Records is null ? e.VertexCount : ep.VertexCount));
            patch.WriteU32(e.Offset + 0x30, checked((uint)ib));
            int nsubNew = ep.Submeshes.Count, nsubOld = e.Submeshes.Length;
            if (nsubNew == 0 && nsubOld == 0) continue;
            if (nsubNew != nsubOld) patch.WriteU16(e.Offset + 0x10, (ushort)nsubNew);
            var slots = new ushort[nsubNew];
            var counts = new uint[nsubNew];
            for (int i = 0; i < nsubNew; i++)
            {
                var s = ep.Submeshes[i];
                int? slot = s.MaterialSlot ?? (newSlot.TryGetValue(s.MaterialName, out int ns) ? ns : null);
                if (slot is null) throw new MeshBuildException($"material {CastImport.Repr(s.MaterialName)} has no slot (internal)");
                s.MaterialSlot = slot;
                slots[i] = (ushort)slot.Value;
                counts[i] = (uint)s.IndexCount;
            }
            bool grow = nsubNew > nsubOld;
            if (!grow && e.MaterialSlotsOffset is { } mso)
            {
                patch.Write(mso, U16s(slots));
                patch.Write(e.IndexCountsOffset!.Value, U32s(counts));
            }
            else
            {
                int off = patch.Append(U16s(slots), 2);
                patch.Retarget(e.Offset + 0x08, off, kind: 0);
                off = patch.Append(U32s(counts), 4);
                patch.Retarget(e.Offset + 0x18, off, kind: 0);
                arraysAppended += 2;
            }
            if (!grow)
            {
                foreach (var s in ep.Submeshes)
                {
                    int d = e.Submeshes[s.Index].PaletteDescOffset;
                    if (!s.PaletteChanged) continue;
                    if (s.Palette.Length > 0)
                    {
                        int off = patch.Append(U16s(s.Palette), 2);
                        patch.Retarget(d, off, kind: 0);
                        arraysAppended++;
                    }
                    else patch.Retarget(d, null, kind: 0);
                    patch.WriteU64(d + 8, (ulong)s.Palette.Length);
                }
                if (nsubNew < nsubOld && patch.RecordAt(e.Submeshes[0].PaletteDescOffset) is { } ri
                    && patch.Fixups.Records[ri].ClassId == MeshGraph.ClassPalette)
                {
                    patch.RelocateRecord(ri, e.Submeshes[0].PaletteDescOffset, nsubNew);
                    recordsRelocated++;
                }
            }
            else
            {
                int offD = patch.Append(new byte[MeshGraph.PaletteDescSize * nsubNew], 8);
                foreach (var s in ep.Submeshes)
                {
                    int d = offD + MeshGraph.PaletteDescSize * s.Index;
                    int? target;
                    if (s.Index < nsubOld && !s.PaletteChanged) target = e.Submeshes[s.Index].PaletteOffset;
                    else
                    {
                        target = s.Palette.Length > 0 ? patch.Append(U16s(s.Palette), 2) : null;
                        if (target is not null) arraysAppended++;
                    }
                    if (target is not null) patch.Retarget(d, target, kind: 0);
                    patch.WriteU64(d + 8, (ulong)s.Palette.Length);
                }
                patch.Retarget(e.Offset + 0x20, offD, kind: 0);
                int? rix = nsubOld > 0 ? patch.RecordAt(e.Submeshes[0].PaletteDescOffset) : null;
                if (rix is { } r && patch.Fixups.Records[r].ClassId == MeshGraph.ClassPalette)
                {
                    patch.RelocateRecord(r, offD, nsubNew);
                    recordsRelocated++;
                }
                else patch.AddRecord(offD, MeshGraph.ClassPalette, nsubNew);
                arraysAppended++;
            }
        }

        // ---- bounds ------------------------------------------------------------------------------------------------
        var changedEntries = plan.Entries.Where(p => p.Changed).Select(p => p.Entry.Index).ToHashSet();
        var byIndex = plan.Entries.ToDictionary(p => p.Entry.Index);
        foreach (var en in model.Entities)
        {
            if (en.GeometryEntries.Length == 0 || !en.GeometryEntries.Any(changedEntries.Contains)) continue;
            double[] lo = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
            double[] hi = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
            bool anyPoint = false, allSkinned = true;
            foreach (int gi in en.GeometryEntries)
            {
                var ep = byIndex[gi];
                int fmt = ep.Entry.Format;
                allSkinned &= Vertex.IsSkinned(fmt);
                if (ep.Records is not { Length: > 0 } rec) continue;
                int stride = Vertex.Stride(fmt);
                for (int i = 0; i < rec.Length / stride; i++)
                {
                    double x = Pos(rec, i * stride, 0, fmt), y = Pos(rec, i * stride, 1, fmt), z = Pos(rec, i * stride, 2, fmt);
                    if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) continue;
                    anyPoint = true;
                    lo[0] = Math.Min(lo[0], x); lo[1] = Math.Min(lo[1], y); lo[2] = Math.Min(lo[2], z);
                    hi[0] = Math.Max(hi[0], x); hi[1] = Math.Max(hi[1], y); hi[2] = Math.Max(hi[2], z);
                }
            }
            if (!anyPoint) continue;
            if (!allSkinned)
                for (int c = 0; c < 3; c++)
                {
                    double ce = en.BoundsCenter[c], h = en.BoundsHalf[c];
                    lo[c] = NpMin(lo[c], ce - h);
                    hi[c] = NpMax(hi[c], ce + h);
                }
            var vals = new float[6];
            for (int c = 0; c < 3; c++)
            {
                vals[c] = (float)((lo[c] + hi[c]) / 2);
                vals[3 + c] = (float)((hi[c] - lo[c]) / 2);
            }
            patch.WriteF32s(en.Offset + 0x60, vals);
            boundsRewritten.Add(en.Index);
        }

        // ---- identity ----------------------------------------------------------------------------------------------
        bool renamed = false;
        if (plan.IdentityRename is { } rn)
        {
            int off = patch.AppendString(rn.After);
            patch.Retarget((int)model.Image.Records[0].Offset, off);
            renamed = true;
        }

        var (image, fixups) = patch.Finish();
        rep["materials_added"] = materialsAdded;
        rep["arrays_appended"] = arraysAppended;
        rep["records_relocated"] = recordsRelocated;
        rep["slots_added"] = patch.Fixups.Slots.Count - slotsBefore;
        rep["bounds_rewritten"] = boundsRewritten;
        rep["identity_renamed"] = renamed;
        if (grown is not null) rep["material_table"] = grown;
        return new PatchedImage(image, fixups, skin, rep);
    }

    /// <summary>np.minimum / np.maximum: NaN propagates.</summary>
    private static double NpMin(double a, double b) => double.IsNaN(a) || double.IsNaN(b) ? double.NaN : Math.Min(a, b);
    private static double NpMax(double a, double b) => double.IsNaN(a) || double.IsNaN(b) ? double.NaN : Math.Max(a, b);

    private static double Pos(byte[] rec, int at, int c, int fmt) => fmt == 0
        ? Vertex.HalfToFloat(BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(at + c * 2)))
        : BinaryPrimitives.ReadSingleLittleEndian(rec.AsSpan(at + c * 4));

    private static byte[] U16s(ushort[] v)
    {
        var b = new byte[v.Length * 2];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 2), v[i]);
        return b;
    }

    private static byte[] U32s(uint[] v)
    {
        var b = new byte[v.Length * 4];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), v[i]);
        return b;
    }

    /// <summary>
    /// Grows the class-11 table by the new names (inserted at <c>count</c>, skin-only entries shifted up) and remaps the skins'
    /// full-table indices. Returns the new skin part (null when the mesh has none) and a report block.
    /// </summary>
    private static (byte[]? Skin, JsonObject Report) GrowMaterials(MeshModel model, List<string> names, ImagePatch patch,
                                                                   Dictionary<string, int> newSlot, JsonArray added)
    {
        string list = CastImport.ReprList(names);
        if (model.MaterialHeaderOffset is not { } hdr)
            throw new MeshBuildException($"cannot add material(s) {list}: the mesh has no class-10 material table");
        var img = model.Image;
        int count = model.Materials.Length, cap = model.MaterialCapacity, k = names.Count;
        if (img.Pointer(hdr).Target is not { } oldBase) throw new MeshBuildException($"cannot add material(s) {list}: the material table pointer is null");
        if (count == 0)
            throw new MeshBuildException($"cannot add material(s) {list}: the mesh has no material of its own to take the entry's opaque words from");
        if (cap + k > MeshGraph.MaxMaterials)
            throw new MeshBuildException($"cannot add material(s) {list}: {cap + k} class-11 entries exceed {MeshGraph.MaxMaterials}");
        if (img.RecordAt(oldBase) is not { } rec || img.Records[rec].ClassId != MeshGraph.ClassMaterialEntry || img.Records[rec].Count != cap)
            throw new MeshBuildException($"cannot add material(s) {list}: the class-11 table at 0x{oldBase:X} is not one class-11 record of "
                                         + $"{cap} entries; growing it is not supported");
        int oldEnd = oldBase + cap * MeshGraph.MaterialEntrySize;
        foreach (var s in img.Fixups.Slots)
        {
            if (s.Offset >= oldBase && s.Offset < oldEnd && !s.Supported)
                throw new MeshBuildException($"cannot add material(s) {list}: class-11 slot 0x{s.Offset:X} has kind 0x{s.Kind:X}, which cannot be relocated");
            if (s.Offset == (uint)hdr || !s.Supported) continue;
            if (img.Pointer((int)s.Offset).Target is { } t && t >= oldBase && t < oldEnd)
                throw new MeshBuildException($"cannot add material(s) {list}: slot 0x{s.Offset:X} points into the class-11 table; growing it is not supported");
        }
        var nameBytes = new List<byte[]>();
        foreach (var n in names)
        {
            var raw = Encoding.UTF8.GetBytes(n);
            if (raw.Length == 0 || raw.AsSpan().IndexOf((byte)0) >= 0) throw new MeshBuildException($"invalid material name {CastImport.Repr(n)}");
            nameBytes.Add(raw);
        }
        MeshSkins? skins = null;
        if (model.SkinRaw is { } skinRaw)
        {
            skins = MeshSkins.Decode(skinRaw);
            if (skins.Error is not null)
                throw new MeshBuildException($"cannot add material(s) {list}: the skins part (0x12) does not decode ({skins.Error}); its material indices cannot be remapped");
        }

        int newCap = cap + k, esz = MeshGraph.MaterialEntrySize;
        int newBase = patch.Append(new byte[newCap * esz], 8);
        var slotsIn = img.Fixups.Slots.Where(s => s.Offset >= oldBase && s.Offset < oldEnd).ToList();
        for (int i = 0; i < newCap; i++)
        {
            int dst = newBase + i * esz;
            int src = i < count ? i : i < count + k ? -1 : i - k;
            if (src >= 0)
            {
                int so = oldBase + src * esz;
                patch.Write(dst, img.Span(so, esz));                  // words verbatim: a copied pointer keeps its target and tag
                foreach (var s in slotsIn.Where(s => s.Offset >= so && s.Offset < so + esz))
                    patch.AddSlot(dst + (int)(s.Offset - so), s.Kind);
                continue;
            }
            var raw = nameBytes[i - count];
            var blob = new byte[8 + raw.Length + 1];
            BinaryPrimitives.WriteUInt32LittleEndian(blob, (uint)raw.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), (uint)raw.Length);
            raw.CopyTo(blob, 8);
            int str = patch.Append(blob, 8) + 8;
            patch.Retarget(dst + 0x08, str, kind: Slot.Tagged, tag: MaterialNameTag);
            patch.Write(dst + 0x18, img.Span(oldBase + (count - 1) * esz + 0x18, 8));
            newSlot[names[i - count]] = i;
            added.Add(new JsonObject { ["name"] = names[i - count], ["slot"] = i });
        }
        patch.Retarget(hdr, newBase);
        patch.WriteU16(hdr + 0x08, (ushort)(count + k));
        patch.WriteU16(hdr + 0x0A, (ushort)newCap);
        patch.RelocateRecord(patch.RecordAt(oldBase) ?? throw new MeshBuildException("class-11 record vanished (internal)"), newBase, newCap);

        byte[]? newSkin = null;
        int remapped = 0;
        if (skins is not null)
        {
            ushort Map(ushort x) => x < count ? x : checked((ushort)(x + k));
            foreach (var s in skins.Skins)
                for (int j = 0; j < s.Replace.Count; j++)
                {
                    var r = s.Replace[j];
                    var nr = new SkinReplace(Map(r.Slot), Map(r.Material));
                    if (nr != r) { s.Replace[j] = nr; remapped++; }
                }
            newSkin = skins.Encode();
        }
        var report = new JsonObject
        {
            ["offset"] = new JsonArray(oldBase, newBase), ["count"] = new JsonArray(count, count + k), ["capacity"] = new JsonArray(cap, newCap),
            ["skin_indices_remapped"] = remapped,
        };
        return (newSkin, report);
    }
}
