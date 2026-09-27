using System.Buffers.Binary;
using Nightrunner.Core.Prefab;

static class Explore
{
    public static void Run(PrefabContainer c, string pack)
    {
        var spans = c.Spans();
        var byClass = new SortedDictionary<uint, (int n, long elems, int minPer, int maxCount, int sec, int rev)>();
        for (int i = 0; i < c.Records.Count; i++)
        {
            var r = c.Records[i];
            int per = (spans[i].End - spans[i].Start) / Math.Max(1, r.Count);
            var e = byClass.GetValueOrDefault(r.ClassRaw, (0, 0, int.MaxValue, 0, 0, 0));
            e.n++; e.elems += r.Count; e.minPer = Math.Min(e.minPer, per); e.maxCount = Math.Max(e.maxCount, r.Count);
            if (r.Secondary) e.sec++;
            if (r.Reverse) e.rev++;
            byClass[r.ClassRaw] = e;
        }
        Console.WriteLine($"  classes ({byClass.Count}):");
        foreach (var (id, e) in byClass)
            Console.WriteLine($"    0x{id:X8} {PrefabClasses.Name(id),-40} n={e.n,7} elems={e.elems,7} minSpan/elem=0x{e.minPer:X} maxCount={e.maxCount} sec={e.sec} rev={e.rev} stride={PrefabClasses.Stride(id)?.ToString("X") ?? "-"}");
        int firstRev = c.Records.FindIndex(r => r.Reverse);
        int firstInd = c.Slots.FindIndex(s => (s.Kind & 1) != 0);
        Console.WriteLine($"  firstReverse={firstRev} firstIndirect={firstInd} objRaw=0x{c.ObjectCountRaw:X8}");
    }

    /// <summary>Annotated qword dump of one prefab's closure.</summary>
    public static void Dump(PrefabContainer c, string name, int maxBytes = 0x400)
    {
        var tx = new PrefabText(c);
        int idx = -1;
        for (int i = 0; i < c.PrefabCount; i++)
            if (tx.PstringAt(false, (int)c.Records[i].Offset + 0x10) == name) { idx = i; break; }
        if (idx < 0) { Console.WriteLine($"no prefab '{name}'"); return; }
        var spans = c.Spans();
        var starts = new SortedList<int, int>();
        for (int i = 0; i < c.Records.Count; i++) if (!c.Records[i].Secondary) starts[(int)c.Records[i].Offset] = i;
        var keys = starts.Keys.ToArray();
        int RecOf(long off)
        {
            int lo = Array.BinarySearch(keys, (int)off);
            if (lo < 0) lo = ~lo - 1;
            return starts.Values[lo];
        }
        var seen = new HashSet<int> { idx };
        var q = new Queue<int>();
        q.Enqueue(idx);
        var order = new List<int>();
        while (q.Count > 0)
        {
            int ri = q.Dequeue();
            order.Add(ri);
            for (int o = spans[ri].Start; o < spans[ri].End; o += 8)
            {
                if (tx.Pointer(false, o) is not { } p || p.Target.ToSecondary) continue;
                int t = RecOf(p.Target.Target);
                if (t < c.RootCount && t != idx) continue;
                if (seen.Add(t)) q.Enqueue(t);
            }
        }
        Console.WriteLine($"prefab {idx} '{name}': closure {order.Count} records");
        foreach (int ri in order.OrderBy(x => x)) DumpRecord(c, tx, spans, ri, RecOf, maxBytes);
    }

    public static void DumpRecords(PrefabContainer c, IEnumerable<int> recs, int maxBytes = 0x400)
    {
        var tx = new PrefabText(c);
        var spans = c.Spans();
        var starts = new SortedList<int, int>();
        for (int i = 0; i < c.Records.Count; i++) if (!c.Records[i].Secondary) starts[(int)c.Records[i].Offset] = i;
        var keys = starts.Keys.ToArray();
        int RecOf(long off)
        {
            int lo = Array.BinarySearch(keys, (int)off);
            if (lo < 0) lo = ~lo - 1;
            return starts.Values[lo];
        }
        foreach (var ri in recs) DumpRecord(c, tx, spans, ri, RecOf, maxBytes);
    }

    static void DumpRecord(PrefabContainer c, PrefabText tx, (int Start, int End)[] spans, int ri, Func<long, int> RecOf, int maxBytes)
    {
        {
            var r = c.Records[ri];
            Console.WriteLine($"-- record {ri} 0x{r.ClassRaw:X8} {PrefabClasses.Name(r.ClassRaw)} x{r.Count} span [0x{spans[ri].Start:X}, 0x{spans[ri].End:X}) len 0x{spans[ri].End - spans[ri].Start:X}");
            int end = Math.Min(spans[ri].End, spans[ri].Start + maxBytes);
            for (int o = spans[ri].Start; o < end; o += 8)
            {
                ulong w = BinaryPrimitives.ReadUInt64LittleEndian(c.Primary.AsSpan(o));
                string note = "";
                if (tx.Pointer(false, o) is { } p)
                {
                    var t = p.Target;
                    note = $"slot k{c.Slots[p.Index].Kind} tag 0x{t.Tag:X4} -> {(t.ToSecondary ? "S" : "P")}:0x{t.Target:X}";
                    if (t.ToSecondary)
                    {
                        var (text, cls, _) = tx.ResolvedWith(false, o);
                        note += $" [{PrefabClasses.Name(cls)}] \"{text}\"";
                        if (text is null) note += $" sb=\"{tx.StringBase(true, (int)t.Target)}\"";
                    }
                    else
                    {
                        int tr = RecOf(t.Target);
                        note += $" rec {tr} 0x{c.Records[tr].ClassRaw:X8}+0x{t.Target - c.Records[tr].Offset:X}";
                        if (t.Tag == 0x2100) note += $" \"{tx.LengthPrefixed(false, (int)t.Target)}\"";
                    }
                }
                else if (w != 0)
                {
                    float f0 = BitConverter.Int32BitsToSingle((int)w), f1 = BitConverter.Int32BitsToSingle((int)(w >> 32));
                    note = $"u32 {(uint)w} {(uint)(w >> 32)} f {f0:G5} {f1:G5}";
                }
                Console.WriteLine($"   +0x{o - spans[ri].Start:X3} {w:X16} {note}");
            }
        }
    }

    /// <summary>Per class and field offset: slot kinds and what they point at.</summary>
    public static void FieldCensus(PrefabContainer c, uint[] classes)
    {
        var tx = new PrefabText(c);
        var spans = c.Spans();
        var recStart = new Dictionary<uint, int>();
        for (int i = 0; i < c.Records.Count; i++) if (!c.Records[i].Secondary) recStart[c.Records[i].Offset] = i;
        var starts = recStart.Keys.OrderBy(x => x).ToArray();
        var slotsSorted = c.Slots.Where(s => (s.Kind & 4) == 0).Select(s => s.Offset).OrderBy(x => x).ToArray();
        var targets = new HashSet<long>();
        foreach (var s in c.Slots) { var t = c.Target(s); if (!t.ToSecondary) targets.Add(t.Target); }
        var tsorted = targets.OrderBy(x => x).ToArray();
        var census = new SortedDictionary<(uint, int), Dictionary<string, int>>();
        var extents = new Dictionary<uint, SortedDictionary<int,int>>();
        for (int i = 0; i < c.Records.Count; i++)
        {
            var r = c.Records[i];
            if (r.Secondary || Array.IndexOf(classes, r.ClassRaw) < 0 && !(classes.Length == 0)) continue;
            int start = spans[i].Start, end = spans[i].End;
            int? stride = PrefabClasses.Stride(r.ClassRaw);
            int ext;
            if (stride is { } st) ext = st * r.Count;
            else
            {
                int k = Array.BinarySearch(tsorted, (long)start + 1);
                if (k < 0) k = ~k;
                ext = (k < tsorted.Length && tsorted[k] < end ? (int)tsorted[k] : end) - start;
                // string header lies 8 bytes before its text
            }
            var ex = extents.GetValueOrDefault(r.ClassRaw) ?? (extents[r.ClassRaw] = new());
            ex[ext] = ex.GetValueOrDefault(ext) + 1;
            int per = stride ?? ext;
            int a = Array.BinarySearch(slotsSorted, (uint)start); if (a < 0) a = ~a;
            for (; a < slotsSorted.Length && slotsSorted[a] < start + ext; a++)
            {
                int o = (int)slotsSorted[a];
                var p = tx.Pointer(false, o)!.Value;
                var t = p.Target;
                string d;
                if (t.ToSecondary) d = $"k{c.Slots[p.Index].Kind}->{PrefabClasses.Name(tx.ResolvedWith(false, o).ClassId)}";
                else
                {
                    int ti = Array.BinarySearch(starts, (uint)t.Target);
                    if (ti >= 0) d = $"k{c.Slots[p.Index].Kind} t{t.Tag:X}->{PrefabClasses.Name(c.Records[recStart[starts[ti]]].ClassRaw)}";
                    else d = $"k{c.Slots[p.Index].Kind} t{t.Tag:X}->inline";
                }
                var key = (r.ClassRaw, (o - start) % per);
                var h = census.GetValueOrDefault(key) ?? (census[key] = new());
                h[d] = h.GetValueOrDefault(d) + 1;
            }
        }
        foreach (var (id, ex) in extents)
            Console.WriteLine($"0x{id:X8} {PrefabClasses.Name(id)} extents: {string.Join(" ", ex.Take(12).Select(kv => $"0x{kv.Key:X}x{kv.Value}"))}");
        foreach (var ((id, off), h) in census)
            Console.WriteLine($"0x{id:X8} +0x{off:X3}: {string.Join(", ", h.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} x{kv.Value}"))}");
    }

    /// <summary>Untyped slots grouped by (containing record class, offset in record, kind, target).</summary>
    public static void Untyped(PrefabContainer c, PrefabDocument doc, int top = 400)
    {
        var tx = new PrefabText(c);
        var prim = new SortedList<uint, int>();
        var sec = new SortedList<uint, int>();
        for (int i = 0; i < c.Records.Count; i++) (c.Records[i].Secondary ? sec : prim)[c.Records[i].Offset] = i;
        var pk = prim.Keys.ToArray(); var sk = sec.Keys.ToArray();
        (int rec, int rel) Find(bool s, long off)
        {
            var keys = s ? sk : pk;
            int k = Array.BinarySearch(keys, (uint)off); if (k < 0) k = ~k - 1;
            int r = (s ? sec : prim).Values[k];
            return (r, (int)(off - c.Records[r].Offset));
        }
        var h = new Dictionary<string, int>();
        foreach (int i in doc.Coverage.UntypedSlots)
        {
            var sl = c.Slots[i]; var t = c.Target(sl);
            var (r, rel) = Find(t.InSecondary, t.Offset);
            var (tr, trel) = Find(t.ToSecondary, t.Target);
            string relS = rel >= 0x300 ? "+tail" : $"+0x{rel:X}";
            string key = $"{PrefabClasses.Name(c.Records[r].ClassRaw)} {relS} k{sl.Kind} t{t.Tag:X} -> {PrefabClasses.Name(c.Records[tr].ClassRaw)}{(trel == 0 ? "" : "+in")}";
            h[key] = h.GetValueOrDefault(key) + 1;
        }
        foreach (var kv in h.OrderByDescending(kv => kv.Value).Take(top)) Console.WriteLine($"    {kv.Value,7} {kv.Key}");
    }
}
