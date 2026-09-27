using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Nightrunner.Core.Games;

namespace Nightrunner.Core.Audio;

public enum AespEntryKind { LooseWem, BankWem }

public sealed record AespSource(string Path, string RelativePath, string Language);

/// <param name="Unknown98">The header's u64 at 0x98, kept raw: 0 in all 14 shipped archives (DLTB and DL2), meaning unknown.</param>
public sealed record AespArchive(int Id, AespSource Source, string HeaderName, ulong Unknown98,
                                 long FileLength, long TableOffset, int FirstEntry, int EntryCount,
                                 int TableRowCount, string? Error)
{
    public string Label => System.IO.Path.GetFileName(Source.Path);
}

public readonly record struct AespEntry(int ArchiveId, int RowIndex, string Name, ulong WemId,
                                        long Offset, long Size, AespEntryKind Kind, string? BankName = null);

public readonly record struct AespAsset(int ArchiveId, string Name, long Offset, long Size);

public sealed record AespSearchResult(int[] EntryIndices, TimeSpan Elapsed)
{
    public int Count => EntryIndices.Length;
}

public sealed class AespFormatException(string message) : IOException(message);

public sealed class AespCatalog
{
    public const int HeaderSize = 0xB8;
    public const int RowSize = 0x98;
    private const int MaxRowsPerArchive = 1_000_000;

    private readonly AespArchive[] _archives;
    private readonly AespEntry[] _entries;
    private readonly string[] _warnings;
    private readonly AespAsset[] _nameAssets;

    private AespCatalog(AespArchive[] archives, AespEntry[] entries, string[] warnings, AespAsset[] nameAssets) =>
        (_archives, _entries, _warnings, _nameAssets) = (archives, entries, warnings, nameAssets);

    public IReadOnlyList<AespArchive> Archives => _archives;
    public IReadOnlyList<AespEntry> Entries => _entries;
    public IReadOnlyList<string> Warnings => _warnings;
    public IReadOnlyList<AespAsset> NameAssets => _nameAssets;
    public int ErrorCount => _archives.Count(a => a.Error is not null);

    public static AespCatalog Scan(GameInstall install, CancellationToken ct = default) =>
        ScanSources(FindSources(install), ct);

    /// <summary>
    /// Every AESP under the audio folder (recursive, so an archive left in an old loader folder is just one more
    /// archive, as packs are) and under each language pack. NightrunnerProxy does not load audio, so nothing comes
    /// from its mods.
    /// </summary>
    public static AespSource[] FindSources(GameInstall install)
    {
        List<AespSource> found = [];
        if (install.Audio is { } audio && Directory.Exists(audio))
        {
            foreach (var path in Directory.EnumerateFiles(audio, "*.aesp", SearchOption.AllDirectories))
                found.Add(new(path, Path.GetRelativePath(install.Root, path), ""));
        }

        // Language packs keep their AESP files outside the main audio tree.
        var languageRoot = Path.Combine(install.Data, "work", "data_lang");
        if (Directory.Exists(languageRoot))
        {
            foreach (var languageDir in Directory.EnumerateDirectories(languageRoot))
            {
                var languageAudio = Path.Combine(languageDir, "data", "audio");
                if (!Directory.Exists(languageAudio)) continue;
                var language = Path.GetFileName(languageDir);
                foreach (var path in Directory.EnumerateFiles(languageAudio, "*.aesp", SearchOption.AllDirectories))
                    found.Add(new(path, Path.GetRelativePath(install.Root, path), language));
            }
        }

        return [.. found.OrderBy(s => s.RelativePath, StringComparer.OrdinalIgnoreCase)];
    }

    public static AespCatalog ScanSources(IEnumerable<AespSource> sources, CancellationToken ct = default)
    {
        List<AespArchive> archives = [];
        List<AespEntry> entries = [];
        List<string> warnings = [];
        List<AespAsset> nameAssets = [];
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            int first = entries.Count;
            try
            {
                var parsed = ReadTable(source.Path, archives.Count, ct);
                entries.AddRange(parsed.Entries);
                nameAssets.AddRange(parsed.NameAssets);
                warnings.AddRange(parsed.Warnings.Select(w => $"{source.RelativePath}: {w}"));
                archives.Add(new(archives.Count, source, parsed.HeaderName, parsed.Unknown98,
                                 parsed.Length, parsed.TableOffset, first, parsed.Entries.Length,
                                 parsed.TableRowCount, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
            {
                archives.Add(new(archives.Count, source, "", 0, 0, 0, first, 0, 0, ex.Message));
            }
        }
        return new([.. archives], [.. entries], [.. warnings], [.. nameAssets]);
    }

    private readonly record struct TableRow(int Index, string Name, ulong TableKey, long Offset, long Size, bool Numbered);

    private static (string HeaderName, ulong Unknown98, long Length, long TableOffset,
                    int TableRowCount, AespEntry[] Entries, string[] Warnings, AespAsset[] NameAssets)
        ReadTable(string path, int archiveId, CancellationToken ct)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                                        bufferSize: 64 * 1024, FileOptions.SequentialScan);
        long length = file.Length;
        if (length < HeaderSize) throw new AespFormatException("AESP header is truncated");
        Span<byte> header = stackalloc byte[HeaderSize];
        file.ReadExactly(header);
        ulong table = BinaryPrimitives.ReadUInt64LittleEndian(header[0x88..]);
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(header[0x90..]);
        ulong unknown98 = BinaryPrimitives.ReadUInt64LittleEndian(header[0x98..]);
        // The prototype reads the table offset as the u32 at 0xA0; both hold 0xB8 in all 14 shipped archives.
        uint tableA0 = BinaryPrimitives.ReadUInt32LittleEndian(header[0xA0..]);
        if (table != tableA0)
            throw new AespFormatException($"AESP table offset fields disagree: 0x88 holds 0x{table:X}, 0xA0 holds 0x{tableA0:X}");
        if (table < HeaderSize || table > (ulong)length)
            throw new AespFormatException($"AESP table offset 0x{table:X} is outside the file");
        if (count > (ulong)MaxRowsPerArchive || count > ((ulong)length - table) / RowSize)
            throw new AespFormatException($"AESP row count {count} exceeds the table or safety limit");

        var headerName = DecodeName(header.Slice(8, 128));
        file.Position = checked((long)table);
        var rows = new TableRow[(int)count];
        Span<byte> row = stackalloc byte[RowSize];
        for (int i = 0; i < rows.Length; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            file.ReadExactly(row);
            ulong tableKey = BinaryPrimitives.ReadUInt64LittleEndian(row[0x80..]);
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(row[0x88..]);
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(row[0x90..]);
            if (offset > (ulong)length || size > (ulong)length - offset)
                throw new AespFormatException($"row {i}: payload 0x{offset:X}+0x{size:X} is outside the file");
            string name = DecodeName(row[..0x80]);
            rows[i] = new(i, name, tableKey, (long)offset, (long)size, ulong.TryParse(name, out _));
        }

        List<AespEntry> entries = [];
        List<string> warnings = [];
        List<AespAsset> nameAssets = [];
        Span<byte> signature = stackalloc byte[4];
        foreach (var item in rows)
        {
            if ((item.Index & 1023) == 0) ct.ThrowIfCancellationRequested();
            // Numbered rows are playable only when they contain a complete WEM.
            if (item.Numbered)
            {
                if (BankMediaReader.IsCompleteWem(file, item.Offset, item.Size))
                    entries.Add(new(archiveId, item.Index, $"{item.Name}.wem", item.TableKey,
                                    item.Offset, item.Size, AespEntryKind.LooseWem));
                continue;
            }
            // Keep lookup assets without exposing the raw bank or pinhead as audio.
            if (item.Name.Equals("wwisepinhead", StringComparison.OrdinalIgnoreCase))
            {
                nameAssets.Add(new(archiveId, item.Name, item.Offset, item.Size));
                continue;
            }
            if (item.Size < signature.Length) continue;
            BankMediaReader.ReadExactlyAt(file.SafeFileHandle, signature, item.Offset);
            if (!signature.SequenceEqual("BKHD"u8)) continue;
            nameAssets.Add(new(archiveId, item.Name, item.Offset, item.Size));
            try
            {
                foreach (var media in BankMediaReader.Read(file, item.Offset, item.Size, ct))
                    entries.Add(new(archiveId, media.Index, $"{media.Id}.wem", media.Id,
                                    media.Offset, media.Size, AespEntryKind.BankWem, item.Name));
            }
            catch (AespFormatException ex)
            {
                warnings.Add($"bank {item.Name}: {ex.Message}");
            }
            catch (IOException ex)
            {
                warnings.Add($"bank {item.Name}: {ex.Message}");
            }
        }
        return (headerName, unknown98, length, (long)table, rows.Length, [.. entries], [.. warnings], [.. nameAssets]);
    }

    private static string DecodeName(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }

    public AespSearchResult Search(string? query, AespEntryKind? kind = null, int[]? archiveIds = null,
                                   CancellationToken ct = default, AudioNameIndex? names = null)
    {
        var sw = Stopwatch.StartNew();
        string[] words = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ulong requestedWemId = 0;
        bool exactId = words.Length == 1 && ulong.TryParse(words[0].TrimStart('#'), out requestedWemId);
        bool[]? enabled = null;
        if (archiveIds is { Length: > 0 })
        {
            enabled = new bool[_archives.Length];
            foreach (int id in archiveIds)
                if ((uint)id < (uint)enabled.Length) enabled[id] = true;
        }
        List<int> result = [];
        for (int i = 0; i < _entries.Length; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            var entry = _entries[i];
            if (kind is not null && entry.Kind != kind) continue;
            if (enabled is not null && !enabled[entry.ArchiveId]) continue;
            if (words.Length > 0 && !(exactId && entry.WemId == requestedWemId))
            {
                var archive = _archives[entry.ArchiveId];
                bool matches = true;
                foreach (var word in words)
                    if (!entry.Name.Contains(word, StringComparison.OrdinalIgnoreCase) &&
                        !(names?.Matches(entry, word) ?? false) &&
                        !(entry.BankName?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false) &&
                        !archive.Source.RelativePath.Contains(word, StringComparison.OrdinalIgnoreCase))
                    {
                        matches = false;
                        break;
                    }
                if (!matches) continue;
            }
            result.Add(i);
        }
        sw.Stop();
        return new([.. result], sw.Elapsed);
    }
}
