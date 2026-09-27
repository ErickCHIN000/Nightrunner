using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Nightrunner.Core.Logging;

namespace Nightrunner.Core.Rpack;

public sealed class RpackBuildException(string message) : Exception(message);

/// <summary>One physical part to write: its bytes plus the storage words it belongs to.</summary>
/// <param name="AlignRaw">Storage align byte — alignment is <c>1 &lt;&lt; ((AlignRaw >> 1) &amp; 15)</c>.</param>
/// <param name="FlagBits">physical.packed bits 8..15, preserved from the source resource.</param>
/// <param name="Fc">physical.fc — an unknown word, preserved.</param>
public sealed record PartSpec(byte Type, byte[] Data, byte AlignRaw, byte StorageFlags, byte StorageMetadata,
                              uint FlagBits = 0, uint Fc = 0)
{
    public (byte, byte, byte, byte) StorageKey => (Type, AlignRaw, StorageFlags, StorageMetadata);
    public int Alignment => 1 << ((AlignRaw >> 1) & 0xF);
    public bool Stream => (StorageFlags & RpackFormat.StorageFlagStream) != 0;

    /// <summary>Original storage index — only the <see cref="RpackLayout.Preserve"/> layout reads it.</summary>
    public int? StorageIndex { get; init; }

    /// <summary>Original physical.offset_units — only the <see cref="RpackLayout.Preserve"/> layout reads it.</summary>
    public uint? OffsetUnits { get; init; }

    /// <summary>
    /// Part <paramref name="physIndex"/> of <paramref name="pack"/> with its storage words, flag bits, fc and original
    /// placement (prototype <c>PartSpec.from_pack</c>). <paramref name="data"/> replaces the bytes; otherwise they
    /// are read from the pack, which refuses compressed and child-pack parts by name.
    /// </summary>
    public static PartSpec FromPack(RpackFile pack, int physIndex, byte[]? data = null)
    {
        var ph = pack.Physicals[physIndex];
        var st = pack.Storages[ph.StorageIndex];
        return new PartSpec(st.Type, data ?? pack.ReadPart(physIndex), st.AlignRaw, st.Flags, st.Metadata,
                            ph.FlagBits, ph.Fc)
        {
            StorageIndex = ph.StorageIndex,
            OffsetUnits = ph.OffsetUnits,
        };
    }
}

/// <summary>One logical resource to write.</summary>
/// <param name="Flags">logical.flags byte (bits 24..31). Copied from the source resource; see
/// <see cref="RpackFormat.LogicalFlagsDefault"/> / <see cref="RpackFormat.LogicalFlagsOnDemandMesh"/>.</param>
public sealed record ResourceSpec(byte[] Name, byte Type, byte Flags, IReadOnlyList<PartSpec> Parts)
{
    /// <summary>
    /// Original logical.name_index. Kept only when every resource carries one and together they are a permutation
    /// of the resource indices; otherwise name_index = resource index.
    /// </summary>
    public int? NameIndex { get; init; }

    /// <summary>
    /// Logical resource <paramref name="logicalIndex"/> of <paramref name="pack"/> (prototype
    /// <c>ResourceSpec.from_pack</c>). <paramref name="overrides"/> maps a physical index to replacement bytes.
    /// </summary>
    public static ResourceSpec FromPack(RpackFile pack, int logicalIndex, IReadOnlyDictionary<int, byte[]>? overrides = null)
    {
        var lg = pack.Logicals[logicalIndex];
        var parts = new PartSpec[lg.PartCount];
        for (int k = 0; k < parts.Length; k++)
        {
            int pi = (int)lg.FirstPart + k;
            parts[k] = PartSpec.FromPack(pack, pi, overrides is not null && overrides.TryGetValue(pi, out var d) ? d : null);
        }
        return new ResourceSpec(pack.NameBytes(logicalIndex).ToArray(), lg.Type, lg.Flags, parts)
        {
            NameIndex = (int)lg.NameIndex,
        };
    }
}

/// <summary>Payload placement. Ported from the prototype's <c>PackWriter(layout=…)</c>.</summary>
public enum RpackLayout
{
    /// <summary><see cref="Contiguous"/> when field08 bit 12 (0x1000) is set, else <see cref="Grouped"/>.</summary>
    Auto,

    /// <summary>
    /// The field08 == 0 family: every storage group is one contiguous region, groups in storage-table order.
    /// </summary>
    Grouped,

    /// <summary>
    /// The field08 bit-12 (on-demand) family: non-stream storage groups first, each one contiguous region in
    /// storage-table order with its own base; then every resource whose parts all live in stream (flags bit 3)
    /// storages, back to back in logical order, each part aligned to its storage alignment, the resource start
    /// aligned to its largest part alignment; those storages keep base_units 0 and offset_units is absolute.
    /// A resource mixing stream and non-stream storages is refused.
    /// </summary>
    Contiguous,

    /// <summary>
    /// Reuse the template storages (base, size, compressed, count verbatim) and each part's original
    /// <see cref="PartSpec.StorageIndex"/> / <see cref="PartSpec.OffsetUnits"/>: requires unchanged part sizes.
    /// </summary>
    Preserve,
}

/// <summary>Everything besides the resources that decides the bytes of a pack.</summary>
public sealed record RpackWriteOptions
{
    public uint Field08 { get; init; }
    public uint Flags { get; init; } = 1;
    public RpackLayout Layout { get; init; } = RpackLayout.Auto;

    /// <summary>Explicit storage-table order; keys not listed are appended in the stock order. Null or empty = stock.</summary>
    public IReadOnlyList<(byte, byte, byte, byte)>? StorageOrder { get; init; }

    /// <summary>The source pack's storage records — what <see cref="RpackLayout.Preserve"/> writes back.</summary>
    public IReadOnlyList<Storage>? TemplateStorages { get; init; }

    /// <summary>Resource indices in the order their names go into the blob (a permutation). Null = resource order.</summary>
    public IReadOnlyList<int>? NameBlobOrder { get; init; }

    /// <summary>
    /// A file whose bytes fill the gaps between parts (and the tail up to <see cref="FinalSize"/>) instead of zeros;
    /// read per gap and zero-padded past its end. Meant for <see cref="RpackLayout.Preserve"/> against the source pack.
    /// </summary>
    public string? FillPath { get; init; }

    /// <summary>Pad the file to this size instead of to the next 16-byte boundary (ignored when smaller).</summary>
    public long? FinalSize { get; init; }

    /// <summary>
    /// Options that reproduce <paramref name="pack"/>: its header words, storage order, template storages and name
    /// blob order (prototype <c>PackWriter.from_pack</c>).
    /// </summary>
    public static RpackWriteOptions FromPack(RpackFile pack, RpackLayout layout = RpackLayout.Auto) => new()
    {
        Field08 = pack.Header.Field08,
        Flags = pack.Header.Flags,
        Layout = layout,
        StorageOrder = [.. pack.Storages.Select(s => (s.Type, s.AlignRaw, s.Flags, s.Metadata))],
        TemplateStorages = pack.Storages,
        NameBlobOrder = RpackWriter.NameBlobOrder(pack),
    };
}

public sealed record BuildReport(string Path, int Resources, int Parts, int Storages, long Size)
{
    /// <summary>The layout actually used (never <see cref="RpackLayout.Auto"/>).</summary>
    public RpackLayout Layout { get; init; } = RpackLayout.Grouped;

    /// <summary>What the prototype warns about: layout / field08 mismatch, owner and count wraps.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string Summary => $"{Resources:N0} resources, {Parts:N0} parts, {Storages} storages, {Size:N0} bytes";
}

/// <summary>Header, tables and placement of a pack, before any payload is written.</summary>
public sealed record RpackRender(RpackHeader Header, byte[] Tables, Storage[] Storages, long[] PartOffsets,
                                 long PayloadStart, long PayloadEnd, RpackLayout Layout, IReadOnlyList<string> Warnings);

/// <summary>
/// Writes an RP6L v4 pack. Port of the prototype's <c>PackWriter</c> (nightrunner/container/rp6l.py): the
/// <see cref="RpackLayout.Grouped"/>, <see cref="RpackLayout.Contiguous"/> and <see cref="RpackLayout.Preserve"/>
/// layouts, <see cref="RpackLayout.Auto"/> choosing by field08 bit 12. Storage table in the stock order (or an
/// explicit one), file padded to 16, part types checked against the catalogue, and nothing replaces the destination
/// until the whole file is written.
/// </summary>
/// <remarks>
/// The storage words (type / align / flags / metadata), the physical flag bits and `fc` are supplied per part and
/// copied from whatever resource the content came from, so unknown bits keep their original values rather than
/// being invented; so is the logical flags byte. Sizes follow the stock rule: a storage's size is the sum of its
/// parts each rounded up to the storage alignment (never below 16), and the part count field keeps only its low 16
/// bits. Byte-identical to the prototype for the same resource list and options.
/// </remarks>
public static class RpackWriter
{
    private const long MaxOffset = 1L << 36;   // (u32 units) << 4

    /// <summary>Write with the <see cref="RpackLayout.Auto"/> layout (grouped unless field08 has bit 12).</summary>
    public static BuildReport Write(string path, IReadOnlyList<ResourceSpec> resources,
                                    uint field08 = 0, uint flags = 1) =>
        Write(path, resources, new RpackWriteOptions { Field08 = field08, Flags = flags });

    public static BuildReport Write(string path, IReadOnlyList<ResourceSpec> resources, RpackWriteOptions options)
    {
        var render = Render(resources, options);
        using var op = Log.Start("build", $"write {Path.GetFileName(path)}");

        // Written to <path>.partial and moved into place only when complete.
        string partial = path + ".partial";
        try
        {
            long size = WritePayload(partial, render, resources, options);
            File.Move(partial, path, overwrite: true);
            var report = new BuildReport(path, resources.Count, render.PartOffsets.Length, render.Storages.Length, size)
            {
                Layout = render.Layout,
                Warnings = render.Warnings,
            };
            op.Result = report.Summary;
            foreach (var w in render.Warnings) Log.Warn("build", w);
            return report;
        }
        catch
        {
            try { File.Delete(partial); } catch (IOException) { }
            throw;
        }
    }

    // ---- policy (prototype build.py / project.py) ------------------------------------------------------------

    /// <summary>
    /// The layout a request resolves to: <see cref="RpackLayout.Contiguous"/> for Auto when field08 has bit 12,
    /// <see cref="RpackLayout.Grouped"/> otherwise; an explicit layout is kept.
    /// </summary>
    public static RpackLayout EffectiveLayout(RpackLayout layout, uint field08) => layout switch
    {
        RpackLayout.Auto => (field08 & RpackFormat.Field08OnDemand) != 0 ? RpackLayout.Contiguous : RpackLayout.Grouped,
        RpackLayout.Grouped or RpackLayout.Contiguous or RpackLayout.Preserve => layout,
        _ => throw new RpackBuildException($"unknown layout {layout}"),
    };

    /// <summary>
    /// build.py's layout policy. <see cref="RpackLayout.Preserve"/> is refused unless the resource set is complete
    /// (every resource of the source, <paramref name="complete"/>), no part size changed and the source storages are
    /// at hand; Auto becomes Preserve under the same conditions when field08 is the source's own.
    /// </summary>
    public static RpackLayout ChooseLayout(RpackLayout requested, bool complete, bool sizesUnchanged,
                                           bool haveTemplateStorages, uint field08, uint sourceField08)
    {
        bool preservable = complete && sizesUnchanged && haveTemplateStorages;
        if (requested == RpackLayout.Preserve && !preservable)
            throw new RpackBuildException("'preserve' layout requires a complete spec with unchanged part sizes");
        if (requested == RpackLayout.Auto && preservable && field08 == sourceField08)
            return RpackLayout.Preserve;
        return requested;
    }

    /// <summary>
    /// project.py's header field08 for a mod pack: the project's own value when set, else 0x1000 when the mod
    /// contains a mesh, else the source packs' field08 when they all agree, else 0x1000.
    /// </summary>
    public static uint ProjectField08(uint? projectField08, bool hasMesh, IEnumerable<uint> sourceField08s)
    {
        if (projectField08 is { } f) return f;
        if (hasMesh) return RpackFormat.Field08OnDemand;
        var distinct = sourceField08s.Distinct().ToList();
        return distinct.Count == 1 ? distinct[0] : RpackFormat.Field08OnDemand;
    }

    /// <summary>
    /// What the prototype's <c>select.py::write_selection</c> warns about when it composes a mod pack (the writer
    /// itself does not): a duplicate (type, folded name), and — in a field08 bit-12 pack — a mesh part that is not
    /// on-demand compatible (storage method other than 1, or physical bit 12 set) or a resource mixing stream and
    /// non-stream storages (the contiguous layout refuses it).
    /// </summary>
    public static List<string> SelectionWarnings(IReadOnlyList<ResourceSpec> resources, uint field08)
    {
        var warnings = new List<string>();
        bool ondemand = (field08 & RpackFormat.Field08OnDemand) != 0;
        var seen = new Dictionary<(byte, string), int>();
        for (int i = 0; i < resources.Count; i++)
        {
            var r = resources[i];
            var key = (r.Type, Convert.ToHexString(RpackFile.EngineFold(r.Name)));
            if (seen.TryGetValue(key, out int first))
                warnings.Add($"duplicate (type 0x{r.Type:X2}, name {Show(r.Name)}) at indices {first} and {i}: "
                             + "the engine's name lookup returns the lower index");
            else
                seen[key] = i;
            if (!ondemand) continue;
            for (int k = 0; k < r.Parts.Count; k++)
            {
                var p = r.Parts[k];
                if (r.Type == 0x10 && ((p.StorageFlags & 3) != 1 || (p.FlagBits & RpackFormat.PhysSpecial) != 0))
                    warnings.Add($"{Show(r.Name)}: mesh part {k} is not on-demand compatible (method {p.StorageFlags & 3}, "
                                 + $"flag_bits 0x{p.FlagBits:X4}) — a field08 bit-12 pack needs method 1 without 0x1000");
            }
            if (r.Parts.Any(p => p.Stream) && !r.Parts.All(p => p.Stream))
                warnings.Add($"{Show(r.Name)}: mixes stream (flags bit 3) and non-stream storages; the contiguous layout refuses it");
        }
        return warnings;
    }

    /// <summary>
    /// Stock order of the storage table: stream storages (flags bit 3) first, then the rest, each run sorted by
    /// type id, then align, flags, metadata (47/47 shipped packs).
    /// </summary>
    public static List<(byte, byte, byte, byte)> StockStorageOrder(IEnumerable<(byte, byte, byte, byte)> keys) =>
        [.. keys.OrderBy(k => (k.Item3 & RpackFormat.StorageFlagStream) != 0 ? 0 : 1)
                .ThenBy(k => k.Item1).ThenBy(k => k.Item2).ThenBy(k => k.Item3).ThenBy(k => k.Item4)];

    /// <summary>
    /// Resource indices in the order their names sit in the blob, or null when the name indices are not a bijection
    /// onto the resources (then the name table cannot be reproduced byte for byte anyway).
    /// </summary>
    public static int[]? NameBlobOrder(RpackFile pack)
    {
        int n = pack.Count;
        if (n != (int)pack.Header.NameCount) return null;
        var byNameIndex = new int[n];
        Array.Fill(byNameIndex, -1);
        for (int i = 0; i < n; i++)
        {
            uint ni = pack.Logicals[i].NameIndex;
            if (ni >= (uint)n || byNameIndex[ni] >= 0) return null;
            byNameIndex[ni] = i;
        }
        // name indices sorted by offset (ties keep name-index order), mapped to their resource
        return [.. Enumerable.Range(0, n).OrderBy(ni => pack.NameStart[byNameIndex[ni]]).Select(ni => byNameIndex[ni])];
    }

    // ---- render ----------------------------------------------------------------------------------------------

    private sealed record Plan(Storage[] Storages, int[] PartStorage, long[] PartOffsetUnits, long PayloadStart,
                               long PayloadEnd);

    /// <summary>Plan the layout and render header + tables. No payload is read or written.</summary>
    public static RpackRender Render(IReadOnlyList<ResourceSpec> resources, RpackWriteOptions options)
    {
        foreach (var r in resources)
        {
            if (r.Parts.Count is 0 or > RpackFormat.MaxParts)
                throw new RpackBuildException($"{Show(r.Name)}: {r.Parts.Count} parts (runtime allows 1..{RpackFormat.MaxParts})");
            foreach (var p in r.Parts)
                if (ResTypes.Get(p.Type) is null)
                    throw new RpackBuildException($"{Show(r.Name)}: unknown part type 0x{p.Type:X2}");
        }
        if (resources.Count == 0) throw new RpackBuildException("nothing to write");
        var layout = EffectiveLayout(options.Layout, options.Field08);

        var (nameOffsets, blob, nameIndex) = BuildNames(resources, options.NameBlobOrder);
        int nphys = resources.Sum(r => r.Parts.Count);
        long tableEnd = RpackFormat.HeaderSize + (long)nphys * RpackFormat.PhysicalSize
                        + (long)resources.Count * RpackFormat.LogicalSize + nameOffsets.Length * 4L + blob.Length;
        var keys = StorageKeys(resources, options, layout);
        tableEnd += (long)keys.Count * RpackFormat.StorageSize;
        var plan = layout == RpackLayout.Preserve
            ? PlanPreserve(resources, options, AlignUp(tableEnd, 16))
            : PlanPlaced(resources, keys, layout, AlignUp(tableEnd, 16));

        var header = new RpackHeader
        {
            Magic = RpackFormat.Magic,
            Version = RpackFormat.Version,
            Field08 = options.Field08,
            PhysicalCount = (uint)nphys,
            StorageCount = (uint)plan.Storages.Length,
            NameCount = (uint)nameOffsets.Length,
            NameBytes = (uint)blob.Length,
            LogicalCount = (uint)resources.Count,
            Flags = options.Flags,
        };

        var warnings = new List<string>();
        bool ondemand = (options.Field08 & RpackFormat.Field08OnDemand) != 0;
        if (layout == RpackLayout.Contiguous && !ondemand)
            warnings.Add("contiguous layout written into a pack without field08 bit 12");
        if (layout == RpackLayout.Grouped && ondemand)
            warnings.Add("grouped layout written into a pack WITH field08 bit 12 — mesh resources will not satisfy the one-read contract");
        if (resources.Count > 0x10000)
            warnings.Add($"{resources.Count} logical resources: owner index wraps at 65536 (stock packs do the same; "
                         + "GetPhysicalResourceParentName is wrong for those parts)");
        if (resources.SelectMany(r => r.Parts).GroupBy(p => p.StorageKey).Any(g => g.Count() > 0xFFFF))
            warnings.Add("a storage group holds more than 65535 parts: count field wraps (stock packs do the same)");

        var tables = new byte[tableEnd];
        var span = tables.AsSpan();
        int pos = 0;
        MemoryMarshal.Write(span[pos..], in header);
        pos += RpackFormat.HeaderSize;
        foreach (var s in plan.Storages)
        {
            if (s.Size >= 1L << 40 || s.Compressed >= 1L << 40)
                throw new RpackBuildException("storage size exceeds 40 bits");
            MemoryMarshal.Write(span[pos..], in s);
            pos += RpackFormat.StorageSize;
        }
        int k = 0;
        for (int ri = 0; ri < resources.Count; ri++)
        {
            foreach (var p in resources[ri].Parts)
            {
                if ((p.FlagBits & ~RpackFormat.PhysFlagBits) != 0)
                    throw new RpackBuildException("flag bits must fit in bits 8..15");
                var physical = new Physical
                {
                    Packed = (uint)plan.PartStorage[k] | p.FlagBits | ((uint)(ri & 0xFFFF) << RpackFormat.PhysOwnerShift),
                    OffsetUnits = (uint)plan.PartOffsetUnits[k],
                    Size = (uint)p.Data.Length,
                    Fc = p.Fc,
                };
                MemoryMarshal.Write(span[pos..], in physical);
                pos += RpackFormat.PhysicalSize;
                k++;
            }
        }
        k = 0;
        for (int ri = 0; ri < resources.Count; ri++)
        {
            var r = resources[ri];
            var logical = new Logical
            {
                Packed = (uint)(r.Parts.Count & 0xFFFF) | ((uint)r.Type << 16) | ((uint)r.Flags << 24),
                NameIndex = (uint)nameIndex[ri],
                FirstPart = (uint)k,
            };
            MemoryMarshal.Write(span[pos..], in logical);
            pos += RpackFormat.LogicalSize;
            k += r.Parts.Count;
        }
        foreach (var off in nameOffsets)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[pos..], off);
            pos += 4;
        }
        blob.CopyTo(span[pos..]);
        pos += blob.Length;
        if (pos != tableEnd) throw new RpackBuildException($"table end {pos} != planned {tableEnd}");

        var offsets = new long[nphys];
        for (int i = 0; i < nphys; i++)
            offsets[i] = (plan.Storages[plan.PartStorage[i]].BaseUnits + plan.PartOffsetUnits[i]) << RpackFormat.UnitShift;
        return new RpackRender(header, tables, plan.Storages, offsets, plan.PayloadStart, plan.PayloadEnd, layout, warnings);
    }

    /// <summary>
    /// Name table: offsets indexed by name index, the blob in <paramref name="blobOrder"/> (resource indices), and
    /// each resource's name index — its own when all of them form a permutation, else its resource index.
    /// </summary>
    private static (uint[] Offsets, byte[] Blob, int[] NameIndex) BuildNames(IReadOnlyList<ResourceSpec> resources,
                                                                           IReadOnlyList<int>? blobOrder)
    {
        int n = resources.Count;
        bool keep = resources.All(r => r.NameIndex is not null)
                    && resources.Select(r => r.NameIndex!.Value).Order().SequenceEqual(Enumerable.Range(0, n));
        var nameIndex = new int[n];
        for (int ri = 0; ri < n; ri++) nameIndex[ri] = keep ? resources[ri].NameIndex!.Value : ri;
        var order = blobOrder ?? [.. Enumerable.Range(0, n)];
        if (!order.Order().SequenceEqual(Enumerable.Range(0, n)))
            throw new RpackBuildException("name blob order must be a permutation of the resource indices");
        var offsets = new uint[n];
        var blob = new List<byte>();
        foreach (int ri in order)
        {
            var name = resources[ri].Name;
            if (Array.IndexOf(name, (byte)0) >= 0) throw new RpackBuildException("resource name contains NUL");
            offsets[nameIndex[ri]] = (uint)blob.Count;
            blob.AddRange(name);
            blob.Add(0);
        }
        return (offsets, [.. blob], nameIndex);
    }

    /// <summary>Storage-table order: the template's (preserve), an explicit order plus extras in stock order, or stock.</summary>
    private static List<(byte, byte, byte, byte)> StorageKeys(IReadOnlyList<ResourceSpec> resources,
                                                              RpackWriteOptions options, RpackLayout layout)
    {
        if (layout == RpackLayout.Preserve && options.TemplateStorages is { } template)
            return [.. template.Select(s => (s.Type, s.AlignRaw, s.Flags, s.Metadata))];
        var used = resources.SelectMany(r => r.Parts).Select(p => p.StorageKey).Distinct().ToList();
        if (options.StorageOrder is not { Count: > 0 } order) return StockStorageOrder(used);
        var seen = used.ToHashSet();
        var listed = order.ToHashSet();
        return [.. order.Distinct().Where(seen.Contains), .. StockStorageOrder(used.Where(k => !listed.Contains(k)))];
    }

    /// <summary>The grouped and contiguous layouts.</summary>
    private static Plan PlanPlaced(IReadOnlyList<ResourceSpec> resources, List<(byte, byte, byte, byte)> keys,
                                   RpackLayout layout, long payloadStart)
    {
        if (keys.Count > RpackFormat.MaxStorages)
            throw new RpackBuildException($"{keys.Count} storage groups exceed {RpackFormat.MaxStorages}");
        var parts = new List<(int Res, PartSpec Spec)>();
        for (int ri = 0; ri < resources.Count; ri++)
            foreach (var p in resources[ri].Parts) parts.Add((ri, p));
        int n = parts.Count;
        var index = new Dictionary<(byte, byte, byte, byte), int>();
        for (int g = 0; g < keys.Count; g++) index[keys[g]] = g;
        var partStorage = new int[n];
        for (int i = 0; i < n; i++) partStorage[i] = index[parts[i].Spec.StorageKey];
        var offsetUnits = new long[n];
        var groupBase = new long[keys.Count];
        var groupSize = new long[keys.Count];
        var groupCount = new long[keys.Count];

        // storage.size is the sum of the parts' sizes each rounded up to the storage alignment; payload units are
        // 16 bytes, so the effective alignment is never below 16.
        long LayGroup(int g, long cursor, IEnumerable<int> members)
        {
            bool first = true;
            foreach (int i in members)
            {
                var p = parts[i].Spec;
                if (first)
                {
                    cursor = AlignUp(cursor, Math.Max(16, p.Alignment));
                    groupBase[g] = cursor >> RpackFormat.UnitShift;
                    first = false;
                }
                int a = Math.Max(16, p.Alignment);
                cursor = AlignUp(cursor, a);
                offsetUnits[i] = (cursor >> RpackFormat.UnitShift) - groupBase[g];
                groupSize[g] += AlignUp(p.Data.Length, a);
                groupCount[g]++;
                cursor += p.Data.Length;
            }
            return cursor;
        }

        long cursor = payloadStart;
        if (layout == RpackLayout.Contiguous)
        {
            var streamRes = resources.Select(r => r.Parts.All(p => p.Stream)).ToArray();
            // phase 1: non-stream groups as contiguous regions, in storage-table order
            for (int g = 0; g < keys.Count; g++)
            {
                if ((keys[g].Item3 & RpackFormat.StorageFlagStream) != 0) continue;
                int gg = g;
                cursor = LayGroup(g, cursor, Enumerable.Range(0, n).Where(i => partStorage[i] == gg && !streamRes[parts[i].Res]));
            }
            // phase 2: stream resources back to back in logical order; their storages keep base_units 0
            int at = 0;
            while (at < n)
            {
                int ri = parts[at].Res;
                int cnt = resources[ri].Parts.Count;
                if (!streamRes[ri])
                {
                    if (resources[ri].Parts.Any(p => p.Stream))
                        throw new RpackBuildException($"{Show(resources[ri].Name)}: mixes stream and non-stream storages");
                    at += cnt;
                    continue;
                }
                cursor = AlignUp(cursor, Math.Max(16, resources[ri].Parts.Max(p => p.Alignment)));
                for (int j = 0; j < cnt; j++, at++)
                {
                    var p = parts[at].Spec;
                    int a = Math.Max(16, p.Alignment);
                    cursor = AlignUp(cursor, a);
                    offsetUnits[at] = cursor >> RpackFormat.UnitShift;
                    int g = partStorage[at];
                    groupSize[g] += AlignUp(p.Data.Length, a);
                    groupCount[g]++;
                    cursor += p.Data.Length;
                }
            }
        }
        else
        {
            for (int g = 0; g < keys.Count; g++)
            {
                int gg = g;
                cursor = LayGroup(g, cursor, Enumerable.Range(0, n).Where(i => partStorage[i] == gg));
            }
        }
        if (cursor > MaxOffset) throw new RpackBuildException("payload exceeds the 36-bit addressable range");

        var storages = new Storage[keys.Count];
        for (int g = 0; g < keys.Count; g++)
        {
            var (type, alignRaw, sflags, metadata) = keys[g];
            storages[g] = new Storage
            {
                Type = type,
                AlignRaw = alignRaw,
                Flags = sflags,
                Metadata = metadata,
                BaseUnits = (uint)groupBase[g],
                SizeLo = (uint)(groupSize[g] & 0xFFFFFFFF),
                SizeHi = (byte)(groupSize[g] >> 32),
                Count = (ushort)(groupCount[g] & 0xFFFF),   // the stock tool keeps the low 16 bits
            };
            if (groupSize[g] >= 1L << 40) throw new RpackBuildException("storage size exceeds 40 bits");
        }
        return new Plan(storages, partStorage, offsetUnits, payloadStart, cursor);
    }

    /// <summary>
    /// The preserve layout: the template storages verbatim and every part at its original storage / offset.
    /// </summary>
    private static Plan PlanPreserve(IReadOnlyList<ResourceSpec> resources, RpackWriteOptions options, long payloadStart)
    {
        if (options.TemplateStorages is not { } template)
            throw new RpackBuildException("'preserve' layout needs template storages from the source pack");
        var storages = template.ToArray();
        var partStorage = new List<int>();
        var offsetUnits = new List<long>();
        long end = payloadStart;
        foreach (var r in resources)
        {
            foreach (var p in r.Parts)
            {
                if (p.StorageIndex is not { } si || p.OffsetUnits is not { } ou)
                    throw new RpackBuildException("'preserve' layout needs original storage index / offset units on every part");
                if (si < 0 || si >= storages.Length)
                    throw new RpackBuildException($"{Show(r.Name)}: 'preserve' layout: storage index {si} of {storages.Length}");
                partStorage.Add(si);
                offsetUnits.Add(ou);
                long off = (storages[si].BaseUnits + (long)ou) << RpackFormat.UnitShift;
                if (off < payloadStart)
                    throw new RpackBuildException("'preserve' layout: original payload would overlap the (larger) tables");
                end = Math.Max(end, off + p.Data.Length);
            }
        }
        return new Plan(storages, [.. partStorage], [.. offsetUnits], payloadStart, end);
    }

    // ---- payload ---------------------------------------------------------------------------------------------

    private static long WritePayload(string partial, RpackRender render, IReadOnlyList<ResourceSpec> resources,
                                     RpackWriteOptions options)
    {
        var specs = resources.SelectMany(r => r.Parts).ToArray();
        using var fill = options.FillPath is { } fp
            ? new FileStream(fp, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16)
            : null;
        using var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        stream.Write(render.Tables);
        long pos = stream.Position;
        // in file order; parts at equal offsets keep their physical order
        foreach (int i in Enumerable.Range(0, specs.Length).OrderBy(i => render.PartOffsets[i]))
        {
            long off = render.PartOffsets[i];
            if (off < pos)
                throw new RpackBuildException($"part {i} overlaps previous payload (0x{off:X} < 0x{pos:X})");
            Gap(stream, fill, pos, off);
            stream.Write(specs[i].Data);
            pos = off + specs[i].Data.Length;
        }
        long end = options.FinalSize ?? AlignUp(pos, 16);   // every shipped pack ends on a 16-byte boundary
        if (end > pos) Gap(stream, fill, pos, end);
        return Math.Max(end, pos);
    }

    /// <summary>Bytes [from, to): from the fill file (zero past its end) or zeros.</summary>
    private static void Gap(Stream stream, FileStream? fill, long from, long to)
    {
        long bytes = to - from;
        if (bytes <= 0) return;
        Span<byte> buf = stackalloc byte[4096];
        if (fill is not null) fill.Position = from;
        while (bytes > 0)
        {
            var chunk = buf[..(int)Math.Min(bytes, buf.Length)];
            int got = 0;
            if (fill is not null)
                while (got < chunk.Length && fill.Read(chunk[got..]) is var r && r > 0) got += r;
            chunk[got..].Clear();
            stream.Write(chunk);
            bytes -= chunk.Length;
        }
    }

    private static long AlignUp(long value, long to) => (value + to - 1) / to * to;

    private static string Show(byte[] name) => "'" + Encoding.UTF8.GetString(name) + "'";
}
