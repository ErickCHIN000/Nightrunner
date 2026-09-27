using System.Text.RegularExpressions;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Core.Export;

/// <summary>What an export did.</summary>
public sealed record ExportResult(int Files, long Bytes, int Skipped, int Unreadable, IReadOnlyList<string> Errors)
{
    public string Summary =>
        $"{Files:N0} files, {Bytes:N0} bytes" +
        (Skipped > 0 ? $", {Skipped} already there" : "") +
        (Unreadable > 0 ? $", {Unreadable} with no readable payload" : "") +
        (Errors.Count > 0 ? $", {Errors.Count} errors" : "");
}

/// <summary>How far an export has got, for a progress bar.</summary>
public readonly record struct ExportProgress(int Files, long Bytes, long TotalBytes, string Current);

/// <summary>
/// Writes the raw part bytes of resources to disk. Ported from nightrunner/extract.py's layout so an export can
/// be read back by the prototype (and later by this project's importer).
/// </summary>
/// <remarks>
/// Layout: <c>&lt;out&gt;/&lt;pack&gt;/&lt;family&gt;/&lt;index&gt;_&lt;name&gt;/&lt;part file&gt;</c>. Existing
/// files are skipped, never overwritten, so an interrupted export can simply be run again. Compressed storages
/// and `.rpacz` child parts have no payload in the file: they are counted, not invented.
/// </remarks>
public static partial class RawExporter
{
    /// <summary>Part file names per storage type, exactly as the prototype writes them.</summary>
    private static readonly Dictionary<byte, string> PartFileNames = new()
    {
        [0x10] = "image.bin", [0x11] = "fixups.bin", [0x12] = "skin.bin",
        [0xF0] = "vertex.bin", [0xF1] = "index.bin", [0xF3] = "cloth.bin",
        [0x20] = "header.imgc", [0x21] = "bitmap.bin",
        [0x61] = "prefab.bin", [0x62] = "prefab_fixups.bin",
        [0x40] = "anim.bin", [0x44] = "anm2_header.bin", [0x45] = "anm2_payload.bin",
        [0x42] = "animscr.bin", [0x43] = "animscr_fixups.bin",
        [0x47] = "animgraph.bin", [0x48] = "animgraph_fixups.bin",
        [0x49] = "animcustom.bin", [0x4A] = "animcustom_fixups.bin",
        [0x55] = "envprobe.bin", [0x56] = "voxelizer.bin", [0x5A] = "area.bin",
    };

    public static string PartFileName(byte typeId, int seenBefore)
    {
        string name = PartFileNames.TryGetValue(typeId, out var n) ? n : $"part_{typeId:X2}.bin";
        if (seenBefore == 0) return name;
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        return $"{stem}.{seenBefore}{ext}";
    }

    /// <summary>The folder one resource's parts go in: <c>&lt;family&gt;/&lt;index&gt;_&lt;name&gt;</c>.</summary>
    public static string ResourceFolder(byte type, int logicalIndex, string name) =>
        Path.Combine(ResTypes.FamilyDir(type), $"{logicalIndex:D6}_{SafeName(name)}");

    /// <summary>Filesystem-safe rendering of a resource name (the exact bytes stay in the pack).</summary>
    public static string SafeName(string name, int maxLength = 120)
    {
        var s = BadChars().Replace(name, "_").Trim(' ', '.');
        if (s.Length == 0) s = "_";
        string stem = s.Split('.')[0].ToUpperInvariant();
        if (Reserved.Contains(stem)) s = "_" + s;
        return s.Length > maxLength ? s[..maxLength] : s;
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Export whole packs. Each pack gets its own folder under <paramref name="outDir"/>. The export logs its own
    /// start and end (with the elapsed time), so every caller is recorded in the Log window.
    /// </summary>
    public static ExportResult ExportPacks(IEnumerable<PackEntry> packs, string outDir,
                                           IProgress<ExportProgress>? progress = null,
                                           CancellationToken ct = default, string? label = null)
    {
        var list = packs.Where(p => p.Pack is not null).ToArray();
        using var op = Log.Start("export",
            $"{label ?? $"export {list.Length} pack(s)"} -> {outDir}");
        try
        {
            var state = new State(outDir, progress);
            foreach (var entry in list) state.AddTotal(entry.Pack!, Enumerable.Range(0, entry.Pack!.Count));
            foreach (var entry in list)
                ExportInto(state, entry, entry.Pack!, Enumerable.Range(0, entry.Pack!.Count), ct);
            return Finish(op, state);
        }
        catch (OperationCanceledException)
        {
            op.Result = "cancelled";
            op.Level = LogLevel.Warn;
            throw;
        }
        catch (Exception e)
        {
            op.Failed(e.Message);
            throw;
        }
    }

    /// <summary>Export a set of resources addressed by global id. Logs its own start and end.</summary>
    public static ExportResult ExportResources(RpackCatalog catalog, IEnumerable<int> gids, string outDir,
                                               IProgress<ExportProgress>? progress = null,
                                               CancellationToken ct = default, string? label = null)
    {
        var ids = gids as int[] ?? gids.ToArray();
        using var op = Log.Start("export", $"{label ?? $"export {ids.Length} resource(s)"} -> {outDir}");
        try
        {
            var byPack = new Dictionary<int, (PackEntry Entry, List<int> Indices)>();
            foreach (int gid in ids)
            {
                var (entry, index) = catalog.Split(gid);
                if (!byPack.TryGetValue(entry.Id, out var bucket))
                    byPack[entry.Id] = bucket = (entry, []);
                bucket.Indices.Add(index);
            }

            var state = new State(outDir, progress);
            foreach (var (entry, indices) in byPack.Values)
                if (entry.Pack is { } pack)
                    state.AddTotal(pack, indices);
            foreach (var (entry, indices) in byPack.Values)
                if (entry.Pack is { } pack)
                    ExportInto(state, entry, pack, indices, ct);
            return Finish(op, state);
        }
        catch (OperationCanceledException)
        {
            op.Result = "cancelled";
            op.Level = LogLevel.Warn;
            throw;
        }
        catch (Exception e)
        {
            op.Failed(e.Message);
            throw;
        }
    }

    private static ExportResult Finish(Log.Operation op, State state)
    {
        var result = state.Result();
        op.Result = result.Summary;
        if (result.Errors.Count > 0)
        {
            op.Level = LogLevel.Warn;
            foreach (var e in result.Errors.Take(5)) Log.Error("export", e);
        }
        return result;
    }

    private static void ExportInto(State state, PackEntry entry, RpackFile pack, IEnumerable<int> indices,
                                   CancellationToken ct)
    {
        string packDir = Path.Combine(state.OutDir, SafeName(Path.GetFileNameWithoutExtension(entry.Label)));
        foreach (int index in indices)
        {
            ct.ThrowIfCancellationRequested();
            var lg = pack.Logicals[index];
            string dir = Path.Combine(packDir, ResourceFolder(lg.Type, index, pack.Name(index)));
            var seen = new Dictionary<byte, int>();
            for (int k = 0; k < lg.PartCount; k++)
            {
                int pi = (int)lg.FirstPart + k;
                byte type = pack.PartType(pi);
                seen.TryGetValue(type, out int before);
                seen[type] = before + 1;

                if (pack.PartUnreadableReason(pi) is not null)
                {
                    state.Unreadable++;
                    continue;
                }
                string dest = Path.Combine(dir, PartFileName(type, before));
                long size = pack.Physicals[pi].Size;
                if (File.Exists(dest))
                {
                    state.Skipped++;
                    state.Advance(size, dest);
                    continue;
                }
                try
                {
                    Directory.CreateDirectory(dir);
                    using var source = pack.OpenRange(pack.PartOffset(pi), size);
                    using var target = File.Create(dest);
                    source.CopyTo(target, 1 << 20);
                    state.Files++;
                    state.Bytes += size;
                    state.Advance(size, dest);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or RpackFormatException)
                {
                    state.Errors.Add($"{entry.Label} #{index}: {e.Message}");
                }
            }
        }
    }

    private sealed class State(string outDir, IProgress<ExportProgress>? progress)
    {
        public readonly string OutDir = outDir;
        public int Files, Skipped, Unreadable;
        public long Bytes, Total, Done;
        public readonly List<string> Errors = [];
        private DateTime _last = DateTime.MinValue;

        public void AddTotal(RpackFile pack, IEnumerable<int> indices)
        {
            foreach (int index in indices)
            {
                var lg = pack.Logicals[index];
                for (int k = 0; k < lg.PartCount; k++) Total += pack.Physicals[(int)lg.FirstPart + k].Size;
            }
        }

        public void Advance(long bytes, string current)
        {
            Done += bytes;
            if (progress is null) return;
            var now = DateTime.UtcNow;
            if ((now - _last).TotalMilliseconds < 100) return;
            _last = now;
            progress.Report(new ExportProgress(Files, Done, Total, current));
        }

        public ExportResult Result()
        {
            progress?.Report(new ExportProgress(Files, Done, Total, ""));
            return new ExportResult(Files, Bytes, Skipped, Unreadable, Errors);
        }
    }

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1f]")]
    private static partial Regex BadChars();
}

/// <summary>Export helpers that write into a project folder.</summary>
public static class ProjectExport
{
    /// <summary>Copy a set of resources into a project's folder.</summary>
    public static ExportResult ToProject(RpackCatalog catalog, IEnumerable<int> gids, string projectFolder,
                                         string what, CancellationToken ct = default) =>
        RawExporter.ExportResources(catalog, gids, projectFolder, null, ct, what);
}
