using System.Buffers.Binary;
using System.Text;

namespace Nightrunner.Core.Mesh;

/// <summary>
/// Mesh part 0x12 <c>_SKIN_</c>: the compiled <c>.skn</c> skins of a mesh. Port of <c>mesh/variants.py</c>, which calls
/// them "variants"; the names here are the source-language ones (DL2 DevTools <c>.skn</c> files, checked against
/// their compiled parts).
/// </summary>
/// <remarks>
/// <code>
/// 0x00 u32 count, 0x04 u32 entries_rel (8), 0x08 32-byte records; payload after, in no fixed order, possibly
/// shared between records and with dead bytes; zero padding to 16.
///
/// record (every offset relative to the record start)
/// +0x00 u32  name_rel:20 | n_nib:4 | a_hi:8     Skin("name"); n_nib, a_hi raw (C)
/// +0x04 u32  flags                              0x10000000 always; 0x40000000 = FilterInEditor()
/// +0x08 u32  tail_rel                           8-byte colour object
/// +0x0C u32  tail_flags                         raw (C)
/// +0x10 u32  pairs_rel:21 | c_hi:11             Replace(slot material, material)   {u16 slot, u16 material}
/// +0x14 u32  d_lo:8 | remap_rel:20 | e_hi:4     ReplaceSurface(old, new, flags)    {u8 old, u8 new, u16 flags};
///                                               e_hi = 1 when the skin sets ColorI (C for other values)
/// +0x18 u32  refs_rel                           UseSkin(name, 1)                   {u32 record:24 | hi:8, u32 raw}
/// +0x1C u8 pair_count, u8 f1 (raw), u8 remap_count, u8 ref_count
/// </code>
/// A Replace material indexes the FULL class-11 table: entries <c>count..capacity−1</c> are the skin-only materials.
/// </remarks>
public sealed class MeshSkins
{
    public const int HeaderSize = 8, RecordSize = 0x20, PairSize = 4, SurfaceSize = 4, UseSize = 8, ColorSize = 8;
    public const int MaxRecords = 65535, MaxName = 4096;
    public const uint FlagBase = 0x10000000, FlagFilterInEditor = 0x40000000;

    public int Size { get; private init; }
    public uint EntriesRel { get; private init; } = HeaderSize;
    public List<Skin> Skins { get; } = [];
    /// <summary>Bytes no record references (dead allocations, C), kept verbatim.</summary>
    public List<(int Offset, byte[] Bytes)> Unreferenced { get; } = [];
    public byte[] Trailing { get; private set; } = [];
    public int Consumed { get; private set; }
    /// <summary>True when everything after the last object is zero padding to a 16-byte multiple.</summary>
    public bool Complete { get; private set; }
    public List<string> Notes { get; } = [];
    /// <summary>Set when the part could not be decoded; nothing else is then meaningful.</summary>
    public string? Error { get; private init; }

    private sealed class Bad(string m) : Exception(m);

    /// <summary>Decode a part. Never throws: garbage gives <see cref="Error"/>.</summary>
    public static MeshSkins Decode(byte[] data)
    {
        try { return DecodeCore(data); }
        catch (Bad e) { return new MeshSkins { Error = e.Message, Size = data.Length }; }
        catch (Exception e) { return new MeshSkins { Error = $"{e.GetType().Name}: {e.Message}", Size = 0 }; }
    }

    private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
    private static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));

    private static void Span(byte[] d, string what, long off, long size)
    {
        if (off < HeaderSize || off + size > d.Length) throw new Bad($"{what}: span 0x{off:X}+{size} outside the part ({d.Length} bytes)");
    }

    private static byte[] CStr(byte[] d, int off)
    {
        int end = Math.Min(d.Length, off + MaxName);
        int n = off < end ? d.AsSpan(off, end - off).IndexOf((byte)0) : -1;
        if (n < 0) throw new Bad($"unterminated name at 0x{off:X}");
        return d.AsSpan(off, n).ToArray();
    }

    private static void CountPtr(string what, uint rel, int n, List<string> notes, int idx)
    {
        if (n != 0 && rel == 0) throw new Bad($"record {idx}: {what} count {n} with a null offset");
        if (rel != 0 && n == 0) notes.Add($"record {idx}: {what} offset set with count 0");
    }

    private static MeshSkins DecodeCore(byte[] d)
    {
        int size = d.Length;
        if (size < HeaderSize) throw new Bad($"part too small ({size} bytes)");
        uint count = U32(d, 0), rel = U32(d, 4);
        var s = new MeshSkins { Size = size, EntriesRel = rel };
        if (count > MaxRecords) throw new Bad($"record count {count} exceeds {MaxRecords}");
        if (rel < HeaderSize || rel % 4 != 0) throw new Bad($"record array offset {rel} invalid");
        if (rel != HeaderSize) s.Notes.Add($"record array at {rel} (8 on every known mesh)");
        if (count > 0) Span(d, "record array", rel, count * RecordSize);
        var used = new bool[size];
        used.AsSpan(0, HeaderSize).Fill(true);
        used.AsSpan((int)rel, (int)(count * RecordSize)).Fill(true);

        for (int k = 0; k < count; k++)
        {
            int b = (int)rel + k * RecordSize;
            uint w0 = U32(d, b), w4 = U32(d, b + 0x10), w5 = U32(d, b + 0x14);
            var r = new Skin
            {
                Index = k, Offset = b, RawRecord = d.AsSpan(b, RecordSize).ToArray(),
                NameRel = w0 & 0xFFFFF, NNib = (w0 >> 20) & 0xF, AHi = w0 >> 24,
                Flags = U32(d, b + 4), ColorRel = U32(d, b + 8), ColorFlags = U32(d, b + 0x0C),
                PairsRel = w4 & 0x1FFFFF, CHi = w4 >> 21,
                DLo = w5 & 0xFF, SurfacesRel = (w5 >> 8) & 0xFFFFF, EHi = w5 >> 28,
                UsesRel = U32(d, b + 0x18), F1 = d[b + 0x1D],
            };
            int nPairs = d[b + 0x1C], nSurf = d[b + 0x1E], nUses = d[b + 0x1F];
            r.OrigPairs = nPairs; r.OrigSurfaces = nSurf; r.OrigUses = nUses;
            if (r.NameRel != 0)
            {
                long no = b + r.NameRel;
                Span(d, $"record {k} name", no, 1);
                var nb = CStr(d, (int)no);
                used.AsSpan((int)no, nb.Length + 1).Fill(true);
                r.Name = nb;
                r.NameOffset = (int)no;
            }
            if ((r.Flags & FlagBase) == 0 || (r.Flags & ~(FlagBase | FlagFilterInEditor)) != 0)
                s.Notes.Add($"record {k}: unseen flags 0x{r.Flags:X8}");
            if (r.ColorRel != 0)
            {
                long to = b + r.ColorRel;
                Span(d, $"record {k} tail", to, ColorSize);
                used.AsSpan((int)to, ColorSize).Fill(true);
                r.Color = d.AsSpan((int)to, ColorSize).ToArray();
            }
            else s.Notes.Add($"record {k}: null tail object");

            CountPtr("pair", r.PairsRel, nPairs, s.Notes, k);
            if (nPairs > 0)
            {
                long po = b + r.PairsRel;
                Span(d, $"record {k} pairs", po, nPairs * PairSize);
                used.AsSpan((int)po, nPairs * PairSize).Fill(true);
                for (int j = 0; j < nPairs; j++)
                    r.Replace.Add(new SkinReplace(U16(d, (int)po + j * 4), U16(d, (int)po + j * 4 + 2)));
            }
            CountPtr("remap", r.SurfacesRel, nSurf, s.Notes, k);
            if (nSurf > 0)
            {
                long ro = b + r.SurfacesRel;
                Span(d, $"record {k} remap", ro, nSurf * SurfaceSize);
                used.AsSpan((int)ro, nSurf * SurfaceSize).Fill(true);
                for (int j = 0; j < nSurf; j++)
                {
                    int o = (int)ro + j * 4;
                    r.ReplaceSurface.Add(new SkinSurface(d[o], d[o + 1], U16(d, o + 2)));
                }
            }
            CountPtr("ref", r.UsesRel, nUses, s.Notes, k);
            if (nUses > 0)
            {
                long fo = b + r.UsesRel;
                Span(d, $"record {k} refs", fo, nUses * UseSize);
                used.AsSpan((int)fo, nUses * UseSize).Fill(true);
                for (int j = 0; j < nUses; j++)
                {
                    uint lo = U32(d, (int)fo + j * 8), hi = U32(d, (int)fo + j * 8 + 4);
                    int idx = (int)(lo & 0xFFFFFF);
                    if (idx >= count) s.Notes.Add($"record {k}: ref {j} to record {idx} out of range");
                    r.UseSkin.Add(new SkinUse(idx, (byte)(lo >> 24), hi));
                }
            }
            s.Skins.Add(r);
        }

        int consumed = Math.Max(Array.LastIndexOf(used, true) + 1, HeaderSize);
        s.Consumed = consumed;
        s.Trailing = d.AsSpan(consumed).ToArray();
        for (int i = 0; i < consumed;)
        {
            if (used[i]) { i++; continue; }
            int j = i;
            while (j < consumed && !used[j]) j++;
            s.Unreferenced.Add((i, d.AsSpan(i, j - i).ToArray()));
            i = j;
        }
        s.Complete = !s.Trailing.Any(x => x != 0) && s.Trailing.Length < 16 && size % 16 == 0;
        if (!s.Complete) s.Notes.Add($"trailing {s.Trailing.Length} bytes after the last object are not zero padding to 16");
        if (s.Unreferenced.Count > 0)
            s.Notes.Add($"{s.Unreferenced.Count} unreferenced span(s), {s.Unreferenced.Sum(u => u.Bytes.Length)} bytes (kept verbatim; C: dead allocations)");
        return s;
    }

    /// <summary>The skin a viewer starts on: <c>Default</c> when there is one, else the first; −1 when there are none.</summary>
    public int DefaultIndex
    {
        get
        {
            int i = Skins.FindIndex(k => k.NameStr == "Default");
            return i >= 0 ? i : Skins.Count > 0 ? 0 : -1;
        }
    }

    /// <summary>
    /// The full-table material index each slot draws with under a skin: start from the mesh's own slots (slot i →
    /// entry i), apply every <c>UseSkin</c> target first, in the order listed (recursively — 31 DLTB and 500 DL2
    /// targets have <c>UseSkin</c> of their own; a cycle stops at the skin already being applied), then the skin's own
    /// <c>Replace</c> pairs. The order is inferred (C) from the DL2 DevTools sources, not from the engine.
    /// </summary>
    public int[] MaterialsFor(int skinIndex, int slotCount)
    {
        var map = Enumerable.Range(0, slotCount).ToArray();
        if (skinIndex < 0 || skinIndex >= Skins.Count) return map;
        var active = new HashSet<int>();
        void Apply(int index)
        {
            if (index < 0 || index >= Skins.Count || !active.Add(index)) return;
            var skin = Skins[index];
            foreach (var use in skin.UseSkin) Apply(use.Record);
            foreach (var r in skin.Replace)
                if (r.Slot < slotCount) map[r.Slot] = r.Material;
            active.Remove(index);
        }
        Apply(skinIndex);
        return map;
    }

    /// <summary>
    /// Re-serialise into the same layout. Values may be edited; lists may shrink, never grow; names may get shorter.
    /// A byte claimed twice with different values throws (nothing is ever moved).
    /// </summary>
    public byte[] Encode()
    {
        if (Error is not null) throw new InvalidOperationException("encode needs a successful decode");
        var buf = new byte[Size];
        var owner = new string?[Size];
        void Put(long off, ReadOnlySpan<byte> blob, string what)
        {
            if (off < 0 || off + blob.Length > Size) throw new MeshFormatException($"{what}: 0x{off:X}+{blob.Length} outside {Size} bytes");
            for (int i = 0; i < blob.Length; i++)
            {
                long p = off + i;
                if (owner[p] is { } o && buf[p] != blob[i]) throw new MeshFormatException($"{what}: byte 0x{p:X} collides with {o}");
                buf[p] = blob[i];
                owner[p] = what;
            }
        }
        Span<byte> w = stackalloc byte[RecordSize];
        BinaryPrimitives.WriteUInt32LittleEndian(w, (uint)Skins.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(w[4..], EntriesRel);
        Put(0, w[..8], "header");
        for (int k = 0; k < Skins.Count; k++)
        {
            var r = Skins[k];
            long b = EntriesRel + k * RecordSize;
            int[] counts = [r.Replace.Count, r.ReplaceSurface.Count, r.UseSkin.Count];
            (uint Rel, int Orig, string What)[] arrays =
                [(r.PairsRel, r.OrigPairs, "pairs"), (r.SurfacesRel, r.OrigSurfaces, "remap"), (r.UsesRel, r.OrigUses, "refs")];
            for (int i = 0; i < 3; i++)
            {
                if (counts[i] > 255) throw new MeshFormatException($"record {k}: {arrays[i].What} count {counts[i]} > 255");
                if (counts[i] > 0 && arrays[i].Rel == 0)
                    throw new MeshFormatException($"record {k}: cannot add a {arrays[i].What} array to a record without one");
                if (counts[i] > arrays[i].Orig)
                    throw new MeshFormatException($"record {k}: {arrays[i].What} array grows ({arrays[i].Orig} → {counts[i]}); layout changes are unsupported");
            }
            BinaryPrimitives.WriteUInt32LittleEndian(w, (r.NameRel & 0xFFFFF) | ((r.NNib & 0xF) << 20) | (r.AHi << 24));
            BinaryPrimitives.WriteUInt32LittleEndian(w[4..], r.Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(w[8..], r.ColorRel);
            BinaryPrimitives.WriteUInt32LittleEndian(w[12..], r.ColorFlags);
            BinaryPrimitives.WriteUInt32LittleEndian(w[16..], (r.PairsRel & 0x1FFFFF) | (r.CHi << 21));
            BinaryPrimitives.WriteUInt32LittleEndian(w[20..], (r.DLo & 0xFF) | ((r.SurfacesRel & 0xFFFFF) << 8) | ((r.EHi & 0xF) << 28));
            BinaryPrimitives.WriteUInt32LittleEndian(w[24..], r.UsesRel);
            w[28] = (byte)counts[0]; w[29] = r.F1; w[30] = (byte)counts[1]; w[31] = (byte)counts[2];
            Put(b, w, $"record {k}");
            if (r.NameRel != 0) Put(b + r.NameRel, [.. r.Name, 0], $"record {k} name");
            if (r.ColorRel != 0 && r.Color is not null) Put(b + r.ColorRel, r.Color, $"record {k} tail");
            if (counts[0] > 0)
            {
                var a = new byte[counts[0] * PairSize];
                for (int j = 0; j < counts[0]; j++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(j * 4), r.Replace[j].Slot);
                    BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(j * 4 + 2), r.Replace[j].Material);
                }
                Put(b + r.PairsRel, a, $"record {k} pairs");
            }
            if (counts[1] > 0)
            {
                var a = new byte[counts[1] * SurfaceSize];
                for (int j = 0; j < counts[1]; j++)
                {
                    a[j * 4] = r.ReplaceSurface[j].Old;
                    a[j * 4 + 1] = r.ReplaceSurface[j].New;
                    BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(j * 4 + 2), r.ReplaceSurface[j].SurfaceFlags);
                }
                Put(b + r.SurfacesRel, a, $"record {k} remap");
            }
            if (counts[2] > 0)
            {
                var a = new byte[counts[2] * UseSize];
                for (int j = 0; j < counts[2]; j++)
                {
                    var u = r.UseSkin[j];
                    BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(j * 8), ((uint)u.Record & 0xFFFFFF) | ((uint)u.Hi << 24));
                    BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(j * 8 + 4), u.Raw);
                }
                Put(b + r.UsesRel, a, $"record {k} refs");
            }
        }
        foreach (var (off, bytes) in Unreferenced) Put(off, bytes, "unreferenced");
        Put(Size - Trailing.Length, Trailing, "trailing");
        return buf;
    }
}

/// <summary>One compiled <c>Skin("name") { … }</c> block. Raw words are kept so it re-encodes byte for byte.</summary>
public sealed class Skin
{
    public int Index { get; init; }
    public int Offset { get; init; }
    public byte[] RawRecord { get; init; } = [];
    public byte[] Name { get; set; } = [];
    public int? NameOffset { get; set; }
    public string NameStr => Encoding.UTF8.GetString(Name);

    public uint Flags { get; set; }
    /// <summary><c>FilterInEditor()</c>: the skin is hidden in the editor (flag 0x40000000).</summary>
    public bool FilterInEditor => (Flags & MeshSkins.FlagFilterInEditor) != 0;

    public List<SkinReplace> Replace { get; } = [];
    public List<SkinSurface> ReplaceSurface { get; } = [];
    public List<SkinUse> UseSkin { get; } = [];

    /// <summary>The 8-byte colour object (<c>00 00 00 ff 00 00 00 ff</c> when unset); records may share one.</summary>
    public byte[]? Color { get; set; }
    /// <summary>High nibble of word 0x14: 1 when the source sets <c>ColorI</c> (DL2 DevTools sources vs compiled parts).</summary>
    public uint EHi { get; set; }
    public bool HasColor => EHi == 1;

    // raw words and bit fields, re-emitted as they came (C)
    public uint NameRel { get; set; }
    public uint NNib { get; set; }
    public uint AHi { get; set; }
    public uint ColorRel { get; set; }
    public uint ColorFlags { get; set; }
    public uint PairsRel { get; set; }
    public uint CHi { get; set; }
    public uint DLo { get; set; }
    public uint SurfacesRel { get; set; }
    public uint UsesRel { get; set; }
    public byte F1 { get; set; }
    internal int OrigPairs, OrigSurfaces, OrigUses;
}

/// <summary><c>Replace(slot material, material)</c>: slot indexes the visible table, material the full one.</summary>
public readonly record struct SkinReplace(ushort Slot, ushort Material);

/// <summary><c>ReplaceSurface(old surface id, new surface id, surface flags)</c>.</summary>
public readonly record struct SkinSurface(byte Old, byte New, ushort SurfaceFlags);

/// <summary><c>UseSkin(name, 1)</c>: another record by index. <see cref="Hi"/> 0x10 and <see cref="Raw"/> 0x81000000 on every seen ref (C).</summary>
public readonly record struct SkinUse(int Record, byte Hi, uint Raw);
