using System.Buffers.Binary;
using System.Text;
using Nightrunner.Core.Mesh.ClassReader;

namespace Nightrunner.Core.Prefab;

/// <summary>What <see cref="PrefabEdits.Duplicate"/> copied.</summary>
public sealed record DuplicateResult(int NewIndex, int RecordsCopied, int Pieces, int BytesCopied, int SlotsAdded);

/// <summary>
/// Structural edits of a <see cref="PrefabContainer"/> that the prefab format notes (§11) describe as working offline:
/// rename a prefab, and duplicate a prefab under a new name. Both leave a container that passes
/// <see cref="PrefabContainer.Validate"/> and re-parses byte-exactly.
/// <para><b>Neither edit has been loaded in the game.</b> They are structural only (notes §11, hole PF9): nothing
/// produced here is known to load, and the engine validates nothing, so a mistake is a crash, not a refusal.</para>
/// </summary>
public static partial class PrefabEdits
{
    /// <summary>
    /// Renames prefab <paramref name="index"/> (not loaded in game): appends a pstring resolve element (0xB1000000,
    /// reverse, secondary) holding <paramref name="name"/> with its kind-14 text slot, then retargets the root's
    /// +0x10 kind-9 slot word at it. Records +1, slots +1; in the primary image only that 8-byte word changes. The
    /// old element stays (it may be shared). Entity components elsewhere that name the prefab by text (+0x70) are not
    /// touched; they reach it through +0x98.
    /// </summary>
    public static void Rename(PrefabContainer c, int index, string name)
    {
        c.RequireDltbLayout();
        if (index < 0 || index >= c.PrefabCount) throw new PrefabFormatException($"no prefab {index} (the resource has {c.PrefabCount})");
        CheckName(c, name, index);
        var tx = new PrefabText(c);
        int root = (int)c.Records[index].Offset;
        int nameSlot = tx.SlotAt(false, root + 0x10);
        if (nameSlot < 0 || c.Slots[nameSlot].Kind != 9)
            throw new PrefabFormatException($"prefab {index}: +0x10 has no kind-9 name slot to retarget");
        int element = AppendPstring(c, name);
        BinaryPrimitives.WriteUInt64LittleEndian(c.Primary.AsSpan(root + 0x10), (ulong)element + 1);
        c.Sync();
    }

    /// <summary>
    /// Duplicates prefab <paramref name="index"/> under <paramref name="name"/> (not loaded in game). The copy is the
    /// closed graph reachable from the root through primary pointers, stopping at every other root (entity
    /// components keep pointing at the original child prefabs). Resolve elements are shared, not copied; presets are
    /// not duplicated (the copy has none).
    /// </summary>
    /// <remarks>
    /// Layout: the new root is inserted at 0x260·nPrefab (roots must be the first records at a 0x260 stride), which
    /// moves everything after it by 0x260 — every record offset, slot offset and primary target past that point is
    /// relocated (0x260 is a multiple of 16, so alignment holds). The copied non-root bytes are appended at the end
    /// of the primary image, grouped by the record span they came from and keeping each chunk's offset mod 16.
    /// Pieces are cut at record starts and at every pointer target (a 0x2100 string target cuts 8 bytes early, at
    /// its {len, cap} header) — never at an assumed object end, since not every class's stride is known; no slot
    /// targets the inside of a typed element, so an object and the bytes up to the next target travel together.
    /// Bytes of pieces outside the graph inside a copied chunk are zeroed.
    /// </remarks>
    public static DuplicateResult Duplicate(PrefabContainer c, int index, string name)
    {
        c.RequireDltbLayout();
        if (index < 0 || index >= c.PrefabCount) throw new PrefabFormatException($"no prefab {index} (the resource has {c.PrefabCount})");
        CheckName(c, name, -1);
        c.Validate();
        var prim = c.Primary;
        int n = prim.Length;
        int roots = c.RootCount;
        int firstReverse = c.Records.FindIndex(r => (r.FlagsRaw & PrefabContainer.RecReverse) != 0);
        if (firstReverse < 0) firstReverse = c.Records.Count;

        // ---- pieces ----
        var targets = new SlotTarget[c.Slots.Count];
        var cuts = new SortedSet<int> { 0, n };
        var recordAt = new Dictionary<int, int>();
        for (int i = 0; i < firstReverse; i++)
        {
            var r = c.Records[i];
            recordAt[(int)r.Offset] = i;
            cuts.Add((int)r.Offset);
        }
        for (int i = 0; i < c.Slots.Count; i++)
        {
            var t = targets[i] = c.Target(c.Slots[i]);
            if (!t.ToSecondary) cuts.Add((int)PieceStart(t));
        }
        var cutArr = cuts.ToArray();
        int PieceOf(long off)
        {
            int k = Array.BinarySearch(cutArr, (int)off);
            return k >= 0 ? k : ~k - 1;
        }
        var slotsByPiece = new Dictionary<int, List<int>>();
        for (int i = 0; i < c.Slots.Count; i++)
        {
            if ((c.Slots[i].Kind & PrefabContainer.KindSlotSecondary) != 0) continue;
            int p = PieceOf(c.Slots[i].Offset);
            if (!slotsByPiece.TryGetValue(p, out var l)) slotsByPiece[p] = l = [];
            l.Add(i);
        }

        // ---- closure ----
        int rootOff = (int)c.Records[index].Offset;
        int rootPiece = PieceOf(rootOff);
        if (cutArr[rootPiece + 1] != rootOff + PrefabContainer.RootStride)
            throw new PrefabFormatException($"prefab {index}: the root is not a single 0x260 piece");
        var inGraph = new HashSet<int> { rootPiece };
        var queue = new Queue<int>([rootPiece]);
        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            if (!slotsByPiece.TryGetValue(p, out var sl)) continue;
            foreach (int si in sl)
            {
                var t = targets[si];
                if (t.ToSecondary) continue;
                int tp = PieceOf(PieceStart(t));
                if (recordAt.TryGetValue(cutArr[tp], out int rec) && rec < roots && rec != index) continue;   // other roots stay shared
                if (inGraph.Add(tp)) queue.Enqueue(tp);
            }
        }

        // ---- chunks: closure pieces grouped by the record span holding them ----
        var spans = c.Spans();
        var spanStarts = Enumerable.Range(0, firstReverse).Select(i => spans[i].Start).ToArray();
        int SpanOf(int off)
        {
            int k = Array.BinarySearch(spanStarts, off);
            return k >= 0 ? k : ~k - 1;
        }
        var chunks = new List<(int Start, int End, List<int> Pieces)>();
        foreach (var grp in inGraph.Where(p => p != rootPiece).OrderBy(p => p).GroupBy(p => SpanOf(cutArr[p])))
        {
            var ps = grp.ToList();
            chunks.Add((cutArr[ps[0]], cutArr[ps[^1] + 1], ps));
        }
        int insertAt = PrefabContainer.RootStride * (int)c.PrefabCount;
        const int Shift = PrefabContainer.RootStride;
        long Moved(long off) => off >= insertAt ? off + Shift : off;
        int cursor = n + Shift;
        var chunkNew = new int[chunks.Count];
        for (int k = 0; k < chunks.Count; k++)
        {
            int want = chunks[k].Start & 15;
            cursor += (want - cursor % 16 + 16) % 16;
            chunkNew[k] = cursor;
            cursor += chunks[k].End - chunks[k].Start;
        }
        int newLen = (int)PrefabContainer.Align(cursor, 16);
        var pieceNew = new Dictionary<int, int>();   // piece index → new start
        pieceNew[rootPiece] = insertAt;
        for (int k = 0; k < chunks.Count; k++)
            foreach (int p in chunks[k].Pieces) pieceNew[p] = chunkNew[k] + (cutArr[p] - chunks[k].Start);
        long Mapped(long off)
        {
            int p = PieceOf(off);
            return pieceNew.TryGetValue(p, out int ns) ? ns + (off - cutArr[p]) : Moved(off);
        }

        // ---- bytes ----
        var np = new byte[newLen];
        prim.AsSpan(0, insertAt).CopyTo(np);
        prim.AsSpan(insertAt).CopyTo(np.AsSpan(insertAt + Shift));
        prim.AsSpan(rootOff, Shift).CopyTo(np.AsSpan(insertAt));
        int bytesCopied = Shift;
        for (int k = 0; k < chunks.Count; k++)
            foreach (int p in chunks[k].Pieces)
            {
                int len = cutArr[p + 1] - cutArr[p];
                prim.AsSpan(cutArr[p], len).CopyTo(np.AsSpan(pieceNew[p]));
                bytesCopied += len;
            }
        var sec = c.Secondary is null ? null : (byte[])c.Secondary.Clone();

        // ---- records ----
        var records = new List<Record>(c.Records.Count + inGraph.Count);
        for (int i = 0; i < c.PrefabCount; i++) records.Add(c.Records[i]);
        records.Add(new Record((uint)insertAt, PrefabClasses.Prefab, 1));
        for (int i = (int)c.PrefabCount; i < firstReverse; i++)
            records.Add(c.Records[i] with { Offset = (uint)Moved(c.Records[i].Offset) });
        int copiedRecords = 1;
        for (int k = 0; k < chunks.Count; k++)
            foreach (int p in chunks[k].Pieces)
                if (recordAt.TryGetValue(cutArr[p], out int rec))
                {
                    records.Add(c.Records[rec] with { Offset = (uint)pieceNew[p] });
                    copiedRecords++;
                }
        for (int i = firstReverse; i < c.Records.Count; i++) records.Add(c.Records[i]);

        // ---- slots: relocate the old ones, then add the copies in their groups ----
        void Write(bool inSec, long at, SlotTarget t, long newTarget)
        {
            ulong w = (ulong)(newTarget + 1) | (ulong)t.Tag << 48;   // Tag is 0 on untagged slots
            BinaryPrimitives.WriteUInt64LittleEndian((inSec ? sec! : np).AsSpan((int)at), w);
        }
        var direct = new List<Slot>();
        var indirect = new List<Slot>();
        var newDirect = new List<Slot>();
        var newIndirect = new List<Slot>();
        for (int i = 0; i < c.Slots.Count; i++)
        {
            var s = c.Slots[i];
            var t = targets[i];
            bool inSec = (s.Kind & PrefabContainer.KindSlotSecondary) != 0;
            long at = inSec ? s.Offset : Moved(s.Offset);
            if (!t.ToSecondary) Write(inSec, at, t, Moved(t.Target));
            ((s.Kind & PrefabContainer.KindIndirect) != 0 ? indirect : direct).Add(new Slot((uint)at, s.Kind));
        }
        int slotsAdded = 0;
        foreach (int p in inGraph.OrderBy(p => pieceNew[p]))
        {
            if (!slotsByPiece.TryGetValue(p, out var sl)) continue;
            foreach (int si in sl)
            {
                var s = c.Slots[si];
                var t = targets[si];
                long at = Mapped(s.Offset);
                if (t.ToSecondary) BinaryPrimitives.WriteUInt64LittleEndian(np.AsSpan((int)at), t.Word);
                else
                {
                    int tp = PieceOf(PieceStart(t));
                    Write(false, at, t, inGraph.Contains(tp) ? Mapped(t.Target) : Moved(t.Target));
                }
                ((s.Kind & PrefabContainer.KindIndirect) != 0 ? newIndirect : newDirect).Add(new Slot((uint)at, s.Kind));
                slotsAdded++;
            }
        }
        // the copies' non-graph bytes inside a chunk stay zero; the root copy is the whole 0x260 piece
        var slots = new List<Slot>(c.Slots.Count + slotsAdded + 1);
        slots.AddRange(direct);          // shipped order kept; only [direct][indirect] matters to the loader (§9.4)
        slots.AddRange(newDirect);
        slots.AddRange(indirect);
        slots.AddRange(newIndirect);

        c.Primary = np;
        c.Secondary = sec;
        c.Records.Clear();
        c.Records.AddRange(records);
        c.Slots.Clear();
        c.Slots.AddRange(slots);
        c.PrefabCount++;
        c.Sync();
        int newIndex = (int)c.PrefabCount - 1;
        Rename(c, newIndex, name);
        return new DuplicateResult(newIndex, copiedRecords, inGraph.Count, bytesCopied, slotsAdded + 1);
    }

    /// <summary>Where the piece holding a target starts: a 0x2100 string target owns the 8-byte header before it.</summary>
    private static long PieceStart(SlotTarget t) => t.Tag == PrefabContainer.StringTag && t.Target >= 8 ? t.Target - 8 : t.Target;

    /// <summary>
    /// Appends a pstring resolve element {u64 0, string_base → text} + {u32 len, u32 cap} + text + NUL + pad to 8 at
    /// the end of the secondary image (8-aligned), with its record (reverse, secondary) at the end of the table and
    /// its kind-14 text slot at the end of the direct group. Returns the element's secondary offset.
    /// </summary>
    internal static int AppendPstring(PrefabContainer c, string text)
    {
        if (c.Secondary is null) throw new PrefabFormatException("the resource has no secondary image to hold a resolve element");
        var bytes = Encoding.UTF8.GetBytes(text);
        int e = (int)PrefabContainer.Align(c.Secondary.Length, 8);
        int size = (int)PrefabContainer.Align(0x18 + bytes.Length + 1, 8);
        var sec = new byte[e + size];
        c.Secondary.CopyTo(sec, 0);
        var s = sec.AsSpan(e);
        BinaryPrimitives.WriteUInt64LittleEndian(s[8..], ((ulong)PrefabContainer.StringTag << 48) | (uint)(e + 0x18 + 1));
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x10..], (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x14..], (uint)bytes.Length);
        bytes.CopyTo(s[0x18..]);
        c.Secondary = sec;
        c.Records.Add(new Record((uint)e, PrefabClasses.PstringElement,
                                 PrefabContainer.RecReverse | PrefabContainer.RecInSecondary | 1));
        int firstIndirect = c.Slots.FindIndex(x => (x.Kind & PrefabContainer.KindIndirect) != 0);
        c.Slots.Insert(firstIndirect < 0 ? c.Slots.Count : firstIndirect, new Slot((uint)(e + 8), 14));
        return e;
    }

    /// <summary>§9.11: lowercase, no <c>.prefab</c>/<c>.eds</c>, unique in this resource (other packs are not checked).</summary>
    private static void CheckName(PrefabContainer c, string name, int self)
    {
        if (string.IsNullOrEmpty(name)) throw new PrefabFormatException("a prefab name cannot be empty");
        if (name != name.ToLowerInvariant()) throw new PrefabFormatException($"prefab name '{name}' is not lowercase");
        if (name.EndsWith(".prefab", StringComparison.Ordinal) || name.EndsWith(".eds", StringComparison.Ordinal))
            throw new PrefabFormatException($"prefab name '{name}' keeps its extension");
        if (name.Contains('\0')) throw new PrefabFormatException("a prefab name cannot contain NUL");
        var tx = new PrefabText(c);
        for (int i = 0; i < c.PrefabCount; i++)
            if (i != self && tx.PstringAt(false, (int)c.Records[i].Offset + 0x10) == name)
                throw new PrefabFormatException($"prefab name '{name}' is already used by prefab {i}");
    }
}
