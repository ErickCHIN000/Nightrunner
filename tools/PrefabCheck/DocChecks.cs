using System.Buffers.Binary;
using Nightrunner.Core.Prefab;

/// <summary>The [DATA] numbers of the prefab format notes, measured on one resource.</summary>
static class DocChecks
{
    public static void Run(PrefabContainer c, PrefabDocument doc)
    {
        var spans = c.Spans();
        int firstReverse = c.Records.FindIndex(r => r.Reverse);
        int firstIndirect = c.Slots.FindIndex(s => (s.Kind & 1) != 0);
        var sec = c.Records.Where(r => r.Secondary).GroupBy(r => r.ClassRaw).ToDictionary(g => g.Key, g => g.Count());
        Line("partition: roots / other primary / reverse", $"0..{c.RootCount - 1} / {c.RootCount}..{firstReverse - 1} / {firstReverse}..{c.Records.Count - 1}");
        Line("secondary classes", string.Join(" ", sec.Select(kv => $"{kv.Key:X8}:{kv.Value:N0}")));
        Line("first indirect slot", firstIndirect.ToString("N0"));
        Line("pads (align 4 / align 16)", $"{c.PadAfterKinds.Length} B / {c.PadAfterSize.Length} B");
        Line("primary size mod 16 / secondary size mod 8", $"{c.Primary.Length % 16} / {c.SecondarySize % 8}");
        Line("B0000002 records", c.Records.Count(r => r.ClassRaw == PrefabClasses.FieldDefaults).ToString("N0"));

        int tag2100 = 0, lenCap = 0, k6 = 0, vectors = 0, heapCapEq = 0;
        var tags = new Dictionary<ushort, int>();
        var recStart = new Dictionary<uint, int>();
        var secStart = new Dictionary<uint, int>();
        for (int i = 0; i < c.Records.Count; i++) (c.Records[i].Secondary ? secStart : recStart).TryAdd(c.Records[i].Offset, i);
        var primKeys = recStart.Keys.Order().ToArray();
        var secKeys = secStart.Keys.Order().ToArray();
        int intoTyped = 0, intoTails = 0, tailsFromOther = 0, intoB2 = 0;
        var b2At = new Dictionary<int, int>();
        var tailTargeted = new HashSet<int>();
        foreach (var s in c.Slots)
        {
            var t = c.Target(s);
            if ((s.Kind & 2) != 0) tags[t.Tag] = tags.GetValueOrDefault(t.Tag) + 1;
            if (s.Kind == 6) k6++;
            if (t.Tag == 0x2100)
            {
                tag2100++;
                var img = c.ImageOf(t.ToSecondary);
                uint len = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)t.Target - 8));
                uint cap = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)t.Target - 4));
                if (len == cap && img[(int)t.Target + (int)len] == 0) lenCap++;
            }
            else if ((s.Kind & 2) != 0 && !t.ToSecondary && !t.InSecondary)
            {
                vectors++;
                int at = t.Offset;
                if (c.Primary[at + 7] == 0 && BinaryPrimitives.ReadUInt32LittleEndian(c.Primary.AsSpan(at + 8))
                    == BinaryPrimitives.ReadUInt32LittleEndian(c.Primary.AsSpan(at + 12))) heapCapEq++;
            }
            var keys = t.ToSecondary ? secKeys : primKeys;
            var starts = t.ToSecondary ? secStart : recStart;
            int k = Array.BinarySearch(keys, (uint)t.Target);
            if (k >= 0) continue;
            k = ~k - 1;
            int rec = starts[keys[k]];
            var r = c.Records[rec];
            int rel = (int)(t.Target - r.Offset);
            if (r.ClassRaw == PrefabClasses.FieldDefaults) { intoB2++; b2At[rel] = b2At.GetValueOrDefault(rel) + 1; continue; }
            if (PrefabClasses.Stride(r.ClassRaw) is { } stride && rel < stride * r.Count) { intoTyped++; continue; }
            intoTails++;
            tailTargeted.Add(rec);
            var sk = t.InSecondary ? secKeys : primKeys;
            int ks = Array.BinarySearch(sk, (uint)t.Offset);
            if (ks < 0) ks = ~ks - 1;
            if ((t.InSecondary ? secStart : recStart)[sk[ks]] != rec) tailsFromOther++;
        }
        Line("0x2100 string slots / with {len == cap} and NUL", $"{tag2100:N0} / {lenCap:N0}");
        Line("kind-6 slots", k6.ToString("N0"));
        Line("tags", string.Join(" ", tags.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key:X4}:{kv.Value:N0}")));
        Line("other tagged primary words (vectors) / heap mode, cap == size", $"{vectors:N0} / {heapCapEq:N0}");
        Line("targets inside typed elements (doc strides)", intoTyped.ToString("N0"));
        Line("targets in tails / records with a targeted tail / from another record", $"{intoTails:N0} / {tailTargeted.Count:N0} / {tailsFromOther:N0}");
        Line("targets inside B0000002", $"{intoB2:N0}: " + string.Join(" ", b2At.Select(kv => $"+0x{kv.Key:X}:{kv.Value}")));

        int known = 0, fits = 0;
        for (int i = 0; i < c.Records.Count; i++)
            if (PrefabClasses.Stride(c.Records[i].ClassRaw) is { } st)
            {
                known++;
                if ((long)st * c.Records[i].Count <= spans[i].End - spans[i].Start) fits++;
            }
        Line("count x stride fits the span (doc strides)", $"{fits:N0}/{known:N0}");

        var dom = doc.Prefabs.GroupBy(p => p.DomFormat).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count():N0}");
        var flags = doc.Prefabs.GroupBy(p => p.Flags).OrderBy(g => g.Key).Select(g => $"0x{g.Key:X2}:{g.Count():N0}");
        Line("+0x258 DOM format", string.Join(" ", dom));
        Line("+0x259 flags", string.Join(" ", flags));
        Line("base class", string.Join(" ", doc.Prefabs.GroupBy(p => p.BaseClass ?? "(none)").Select(g => $"{g.Key}:{g.Count():N0}")));
        var odd = doc.Prefabs.Where(p => p.Name is null || p.Name.Any(ch => !(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))).ToList();
        Line("names outside [a-z0-9_]", $"{odd.Count}: " + string.Join(", ", odd.Take(5).Select(p => $"#{p.Index} '{p.Name}'")));
        var names = doc.Prefabs.Select(p => p.Name ?? "").ToList();
        Line("names byte-sorted", names.SequenceEqual(names.Order(StringComparer.Ordinal)).ToString());
        if (doc.Prefabs.Count > 0)
            Line("roots at 0x260 * i", doc.Prefabs.Count(p => p.Offset == 0x260 * p.Index).ToString("N0"));

        var comps = doc.Prefabs.SelectMany(p => p.Components.Select(x => (p, x))).ToList();
        int inline = comps.Count(t => (BinaryPrimitives.ReadUInt64LittleEndian(c.Primary.AsSpan(t.x.Offset + 8)) & 3) != 0);
        Line("components / owner == listing prefab / pcid inline", $"{comps.Count:N0} / {comps.Count(t => t.x.OwnerPrefab == t.p.Index):N0} / {inline:N0}");
        var fields = doc.Prefabs.SelectMany(p => p.Fields).ToList();
        Line("field objects C004 / C005 ; m_Type == low 16 bits of the id",
             $"{fields.Count(f => PrefabClasses.IsFieldVirtual(f.ClassId)):N0} / {fields.Count(f => PrefabClasses.IsExternalField(f.ClassId)):N0} ; {fields.Count(f => f.Type == (ushort)f.ClassId):N0}");
        Line("field records in the table", c.Records.Count(r => PrefabClasses.IsField(r.ClassRaw)).ToString("N0"));

        var blobs = doc.AllValues.ToList();
        var vals = blobs.SelectMany(b => b.Entries).ToList();
        Line("blobs / values / alignment", $"{blobs.Count:N0} / {vals.Count:N0} / " + string.Join(" ", blobs.GroupBy(b => b.Alignment).Select(g => $"{g.Key}:{g.Count():N0}")));
        var eng = vals.Where(v => v.EngineKey).ToList();
        Line("engine-keyed values: all / text / object / raw",
             $"{eng.Count:N0} / {eng.Count(v => v.Form == PrefabValueForm.Text):N0} / {eng.Count(v => v.Form == PrefabValueForm.Object):N0} / {eng.Count(v => v.Form == PrefabValueForm.Raw):N0}");
        var typed = vals.Where(v => !v.EngineKey).ToList();
        Line("field-keyed values by form", $"{typed.Count:N0}: " + string.Join(" ", typed.GroupBy(v => v.Form).Select(g => $"{g.Key}:{g.Count():N0}")));
        Line("field-keyed raw values by ERTTIType", string.Join(" ", typed.Where(v => v.Form == PrefabValueForm.Raw).GroupBy(v => v.Type).OrderByDescending(g => g.Count()).Select(g => $"{g.Key:X2}:{g.Count()}")));
    }

    static void Line(string what, string value) => Console.WriteLine($"    {what,-72} {value}");
}
