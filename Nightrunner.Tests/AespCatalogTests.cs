using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Nightrunner.Core.Audio;
using Nightrunner.Core.Games;

namespace Nightrunner.Tests;

public class AespCatalogTests
{
    [Fact]
    public void WemExportCopiesLooseAndBankMediaWithoutOverwriting()
    {
        var dir = Directory.CreateTempSubdirectory("nr-audio-export-");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            string archive = Path.Combine(dir.FullName, "source.aesp");
            byte[] wem = CreatePcmWem(2, 70_000);
            WriteFixture(archive, [("101", 101UL, wem), ("weapons_pre", 2UL, CreateBank(wem))]);
            var catalog = AespCatalog.ScanSources([new AespSource(archive, "source.aesp", "", false)], ct);
            Assert.Equal(2, catalog.Entries.Count);

            for (int i = 0; i < catalog.Entries.Count; i++)
            {
                string output = Path.Combine(dir.FullName, $"{i}.wem");
                Assert.Equal(wem.Length, AudioExporter.ExportWem(catalog, i, output, ct: ct));
                Assert.Equal(wem, File.ReadAllBytes(output));
                Assert.Throws<IOException>(() => AudioExporter.ExportWem(catalog, i, output, ct: ct));
                Assert.Equal(wem, File.ReadAllBytes(output));
            }

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            string stopped = Path.Combine(dir.FullName, "cancelled.wem");
            Assert.Throws<OperationCanceledException>(() =>
                AudioExporter.ExportWem(catalog, 0, stopped, ct: cancelled.Token));
            Assert.False(File.Exists(stopped));
            Assert.Throws<IOException>(() => AudioExporter.ExportWem(catalog, 0, archive, overwrite: true, ct: ct));
            using (var changed = new FileStream(archive, FileMode.Append, FileAccess.Write)) changed.WriteByte(0);
            Assert.Throws<IOException>(() => AudioExporter.ExportWem(catalog, 0, Path.Combine(dir.FullName, "stale.wem"), ct: ct));
            Assert.False(File.Exists(Path.Combine(dir.FullName, "stale.wem")));
            Assert.Empty(Directory.GetFiles(dir.FullName, "*.tmp"));
        }
        finally { Directory.Delete(dir.FullName, recursive: true); }
    }

    [Fact]
    public void WavExportKeepsAllFourPcmChannels()
    {
        var dir = Directory.CreateTempSubdirectory("nr-audio-export-");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            string archive = Path.Combine(dir.FullName, "source.aesp");
            byte[] wem = CreatePcmWem(4, 100);
            WriteFixture(archive, [("101", 101UL, wem)]);
            var catalog = AespCatalog.ScanSources([new AespSource(archive, "source.aesp", "", false)], ct);
            using var decoder = new VgmstreamDecoder(catalog, 0);
            string output = Path.Combine(dir.FullName, "101.wav");
            var metadata = new WavMetadata("four_channel_test", "play_test_sound", "Dying Light: The Beast - source",
                                           "Sound ID: 101; Bank: test_bank");

            var result = AudioExporter.ExportWav(catalog, 0, output, ct: ct, metadata: metadata);
            byte[] wav = File.ReadAllBytes(output);
            Assert.Equal(4, result.Channels);
            Assert.Equal(16000, result.SampleRate);
            Assert.Equal(800, result.PcmBytes);
            Assert.True(wav.Length > 868);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal((uint)wav.Length - 8, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(4)));
            Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(wav, 8, 8));
            Assert.Equal((ushort)0xFFFE, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(20)));
            Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)));
            Assert.Equal(decoder.ChannelMask, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40)));
            Assert.Equal("data", Encoding.ASCII.GetString(wav, 60, 4));
            Assert.Equal((uint)800, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(64)));
            Assert.Equal(wem.AsSpan(44).ToArray(), wav.AsSpan(68, 800).ToArray());
            Assert.Equal(metadata.Title, ShellProperty(output, "System.Title"));
            Assert.Equal(metadata.Artist, ShellProperty(output, "System.Music.Artist"));
            Assert.Equal(metadata.Album, ShellProperty(output, "System.Music.AlbumTitle"));
        }
        finally { Directory.Delete(dir.FullName, recursive: true); }
    }

    [Fact]
    public void WavExportEmbedsIdentityAndPreservesInclusiveLoopEnd()
    {
        var dir = Directory.CreateTempSubdirectory("nr-audio-export-");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            string archive = Path.Combine(dir.FullName, "source.aesp");
            WriteFixture(archive, [("101", 101UL, CreateLoopedPcmWem())]);
            var catalog = AespCatalog.ScanSources([new AespSource(archive, "source.aesp", "", false)], ct);
            using var decoder = new VgmstreamDecoder(catalog, 0);
            Assert.True(decoder.HasLoop);
            Assert.Equal(10, decoder.LoopStartSample);
            Assert.Equal(80, decoder.LoopEndSample);
            string output = Path.Combine(dir.FullName, "101.wav");
            var metadata = new WavMetadata("play_test_sound", "play_test_sound", "Dying Light: The Beast - source",
                                           "Sound ID: 101; Bank: test_bank; AESP: source.aesp");

            var result = AudioExporter.ExportWav(catalog, 0, output, ct: ct, metadata: metadata);
            byte[] wav = File.ReadAllBytes(output);
            Assert.Equal(200, result.PcmBytes);
            Assert.Equal((uint)wav.Length - 8, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(4)));
            int smpl = FindChunk(wav, "smpl");
            Assert.Equal(60U, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(smpl + 4)));
            Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(smpl + 8 + 28)));
            Assert.Equal(10U, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(smpl + 8 + 44)));
            Assert.Equal(79U, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(smpl + 8 + 48)));
            int list = FindChunk(wav, "LIST");
            Assert.Equal("INFO", Encoding.ASCII.GetString(wav, list + 8, 4));
            var fields = ReadInfo(wav, list);
            Assert.Equal(metadata.Title, fields["INAM"]);
            Assert.Equal(metadata.Artist, fields["IART"]);
            Assert.Equal(metadata.Album, fields["IPRD"]);
            Assert.Equal(metadata.Comment, fields["ICMT"]);
            int data = FindChunk(wav, "data");
            Assert.Equal((uint)result.PcmBytes, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(data + 4)));
            Assert.Equal(data + 8 + result.PcmBytes, list);
            Assert.Equal(list + 8 + BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(list + 4)), wav.Length);
            Assert.Equal(metadata.Title, ShellProperty(output, "System.Title"));
            Assert.Equal(metadata.Artist, ShellProperty(output, "System.Music.Artist"));
            Assert.Equal(metadata.Album, ShellProperty(output, "System.Music.AlbumTitle"));
        }
        finally { Directory.Delete(dir.FullName, recursive: true); }
    }

    [Fact]
    public void ListsOnlyCompleteLooseAndBankEmbeddedWems()
    {
        var dir = Directory.CreateTempSubdirectory("nr-aesp-");
        try
        {
            var path = Path.Combine(dir.FullName, "test.aesp");
            byte[] wem = CreateWem();
            WriteFixture(path, [
                ("553201780", 553201780UL, wem),
                ("904242063", 904242063UL, wem),
                ("wwisepinhead", 1UL, "<xml/>"u8.ToArray()),
                ("weapons_pre", 2UL, CreateBank(wem)),
                ("broken", 3UL, "not audio"u8.ToArray()),
            ]);
            var catalog = AespCatalog.ScanSources([new AespSource(path, "test.aesp", "", false)], TestContext.Current.CancellationToken);

            Assert.Equal(0, catalog.ErrorCount);
            Assert.Empty(catalog.Warnings);
            Assert.Equal(5, catalog.Archives[0].TableRowCount);
            Assert.Equal(3, catalog.Entries.Count);
            Assert.Equal("test", catalog.Archives[0].HeaderName);
            Assert.Equal(42UL, catalog.Archives[0].Priority);
            Assert.Equal(catalog.Entries[0].Offset, catalog.Entries[1].Offset);
            Assert.Equal(AespEntryKind.LooseWem, catalog.Entries[0].Kind);
            Assert.Equal(AespEntryKind.BankWem, catalog.Entries[2].Kind);
            Assert.Equal("weapons_pre", catalog.Entries[2].BankName);
            Assert.Equal("101.wem", catalog.Entries[2].Name);
            Assert.Equal(wem.Length, catalog.Entries[2].Size);
            Assert.DoesNotContain(catalog.Entries, e => e.WemId == 202);
            Assert.DoesNotContain(catalog.Entries, e => e.WemId == 303);
            var ct = TestContext.Current.CancellationToken;
            Assert.Equal([1], catalog.Search("904242063", ct: ct).EntryIndices);
            Assert.Equal([2], catalog.Search("weapons_pre", AespEntryKind.BankWem, ct: ct).EntryIndices);
            Assert.Empty(catalog.Search("wwisepinhead", ct: ct).EntryIndices);
            Assert.Equal(3, catalog.Search("", ct: ct).Count);
            Assert.Empty(catalog.Search("", archiveIds: [99], ct: ct).EntryIndices);
        }
        finally { Directory.Delete(dir.FullName, recursive: true); }
    }

    [Fact]
    public void BadBankIsReportedWithoutHidingLooseWem()
    {
        var dir = Directory.CreateTempSubdirectory("nr-aesp-");
        try
        {
            var path = Path.Combine(dir.FullName, "test.aesp");
            WriteFixture(path, [("11", 11UL, CreateWem()), ("bad_bank", 12UL, "BKHD"u8.ToArray())]);
            var catalog = AespCatalog.ScanSources([new AespSource(path, "test.aesp", "", false)], TestContext.Current.CancellationToken);
            Assert.Equal(0, catalog.ErrorCount);
            Assert.Single(catalog.Entries);
            Assert.Single(catalog.Warnings);
            Assert.Contains("bad_bank", catalog.Warnings[0]);
        }
        finally { Directory.Delete(dir.FullName, recursive: true); }
    }

    [Fact]
    public void BadArchiveIsReportedWithoutHidingHealthyArchive()
    {
        var dir = Directory.CreateTempSubdirectory("nr-aesp-");
        try
        {
            var good = Path.Combine(dir.FullName, "good.aesp");
            var bad = Path.Combine(dir.FullName, "bad.aesp");
            WriteFixture(good, [("11", 11UL, CreateWem())]);
            var bytes = File.ReadAllBytes(good);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(AespCatalog.HeaderSize + 0x88), (ulong)bytes.Length + 1);
            File.WriteAllBytes(bad, bytes);

            var catalog = AespCatalog.ScanSources([
                new AespSource(bad, "bad.aesp", "", false),
                new AespSource(good, "good.aesp", "", false),
            ], TestContext.Current.CancellationToken);
            Assert.Single(catalog.Entries);
            Assert.Equal(1, catalog.ErrorCount);
            Assert.Contains("row 0", catalog.Archives[0].Error);
            Assert.Equal(1, catalog.Entries[0].ArchiveId);
        }
        finally { Directory.Delete(dir.FullName, recursive: true); }
    }

    [Fact]
    public void RejectsImpossibleTableAndHonorsCancellation()
    {
        var dir = Directory.CreateTempSubdirectory("nr-aesp-");
        try
        {
            var path = Path.Combine(dir.FullName, "bad.aesp");
            WriteFixture(path, [("11", 11UL, CreateWem())]);
            var bytes = File.ReadAllBytes(path);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x90), ulong.MaxValue);
            File.WriteAllBytes(path, bytes);
            var source = new AespSource(path, "bad.aesp", "", false);
            var catalog = AespCatalog.ScanSources([source], TestContext.Current.CancellationToken);
            Assert.Equal(1, catalog.ErrorCount);
            Assert.Empty(catalog.Entries);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => AespCatalog.ScanSources([source], cts.Token));
        }
        finally { Directory.Delete(dir.FullName, recursive: true); }
    }

    [Fact]
    public void DiscoversLocalizedAndOptInCustomArchives()
    {
        var dir = Directory.CreateTempSubdirectory("nr-aesp-");
        try
        {
            var audio = Path.Combine(dir.FullName, "ph_ft", "work", "data", "audio");
            var custom = Path.Combine(audio, "custom_audio");
            var language = Path.Combine(dir.FullName, "ph_ft", "work", "data_lang", "speech_en", "data", "audio");
            Directory.CreateDirectory(custom);
            Directory.CreateDirectory(language);
            WriteFixture(Path.Combine(audio, "streams.aesp"), [("11", 11UL, CreateWem())]);
            WriteFixture(Path.Combine(custom, "mod.aesp"), [("12", 12UL, CreateWem())]);
            WriteFixture(Path.Combine(language, "streams_en.aesp"), [("13", 13UL, CreateWem())]);
            var install = new GameInstall(dir.FullName, GameProfile.Dltb);

            var stock = AespCatalog.FindSources(install);
            Assert.Equal(2, stock.Length);
            Assert.DoesNotContain(stock, source => source.IsCustom);
            Assert.Contains(stock, source => source.Language == "speech_en");
            var all = AespCatalog.FindSources(install, includeCustom: true);
            Assert.Equal(3, all.Length);
            Assert.Contains(all, source => source.IsCustom && Path.GetFileName(source.Path) == "mod.aesp");
        }
        finally { Directory.Delete(dir.FullName, recursive: true); }
    }

    [Fact]
    public void ResolvesOnlyVerifiedEventToPlayableSourceLinks()
    {
        Assert.Equal(42155730U, AudioNameIndex.WwiseHash("ambience_bandit_area"));
        var dir = Directory.CreateTempSubdirectory("nr-aesp-names-");
        try
        {
            const string eventName = "play_test_sound";
            uint eventId = AudioNameIndex.WwiseHash(eventName);
            var path = Path.Combine(dir.FullName, "names.aesp");
            WriteFixture(path, [
                ("101", 101UL, CreateWem()),
                ("202", 202UL, CreateWem()),
                ("wwisepinhead", 0UL, Encoding.UTF8.GetBytes($"<Mapping><Event name=\"{eventName}\" id=\"999\" /></Mapping>")),
                ("test_bank", 1UL, CreateNamedBank(CreateWem(), eventId)),
            ]);
            var ct = TestContext.Current.CancellationToken;
            var catalog = AespCatalog.ScanSources([new AespSource(path, "names.aesp", "", false)], ct);
            var names = AudioNameIndex.Build(catalog, ct);
            Assert.Equal(1, names.EventCount);
            Assert.Equal(1, names.MediaCount);
            Assert.Equal(eventName, names.NameFor(catalog.Entries[0]));
            Assert.Equal(["test_bank"], names.BanksFor(catalog.Entries[0]));
            Assert.Equal(["test_bank"], names.BanksFor(catalog.Entries[2]));
            Assert.Empty(names.BanksFor(catalog.Entries[1]));
            Assert.Equal("—", names.NameFor(catalog.Entries[1]));
            Assert.Equal([0, 2], catalog.Search("play_test", ct: ct, names: names).EntryIndices);
            Assert.Empty(catalog.Search("unrelated", ct: ct, names: names).EntryIndices);
        }
        finally { Directory.Delete(dir.FullName, recursive: true); }
    }

    [Fact]
    public void InstalledDltbNameIndexReadsStockBanks()
    {
        var install = Installs.Require("dltb");
        var catalog = AespCatalog.Scan(install, ct: TestContext.Current.CancellationToken);
        var clock = Stopwatch.StartNew();
        var names = AudioNameIndex.Build(catalog, TestContext.Current.CancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine($"Names: {names.EventCount:N0} events, {names.BankCount:N0} banks, {names.MediaCount:N0} media in {clock.Elapsed.TotalSeconds:0.00}s; {names.Diagnostics}");
        var named = catalog.Entries.Where(e => names.NameFor(e) != "—").ToArray();
        TestContext.Current.TestOutputHelper?.WriteLine($"Catalog: {named.Length:N0}/{catalog.Entries.Count:N0} named; examples: {string.Join(", ", named.Take(8).Select(e => $"{e.WemId}={names.NameFor(e)}"))}");
        foreach (var id in new ulong[] { 553201780, 904242063, 796795086 })
            TestContext.Current.TestOutputHelper?.WriteLine($"Menu {id}: {string.Join(" | ", catalog.Entries.Where(e => e.WemId == id).Select(e => names.NameFor(e)))}; banks: {string.Join(", ", names.BanksFor(catalog.Entries.First(e => e.WemId == id)))}");
        Assert.True(names.EventCount > 20_000);
        Assert.True(names.BankCount > 100);
        Assert.True(names.MediaCount > 0);
        Assert.Empty(names.Warnings);
        Assert.True(named.Length > catalog.Entries.Count * 9 / 10);
        foreach (var id in new ulong[] { 553201780, 904242063, 796795086 })
        {
            var matching = catalog.Entries.Where(e => e.WemId == id).ToArray();
            Assert.NotEmpty(matching);
            Assert.All(matching, e => Assert.Equal("start_music_main_dlc_ft", names.NameFor(e)));
            Assert.All(matching, e => Assert.Contains("cnt_dlcft_music_pre", names.BanksFor(e)));
        }
    }

    private static byte[] CreateNamedBank(byte[] wem, uint eventId)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        WriteChunk(writer, "BKHD", [150, 0, 0, 0]);
        using var didx = new MemoryStream();
        using (var index = new BinaryWriter(didx, Encoding.ASCII, leaveOpen: true))
        {
            index.Write(101U); index.Write(0U); index.Write((uint)wem.Length);
        }
        WriteChunk(writer, "DIDX", didx.ToArray());
        WriteChunk(writer, "DATA", wem);
        using var hirc = new MemoryStream();
        using (var objects = new BinaryWriter(hirc, Encoding.ASCII, leaveOpen: true))
        {
            objects.Write(3U);
            WriteSound(objects, 101U, 0, (uint)wem.Length);
            objects.Write((byte)3); objects.Write(11U);
            objects.Write(300U); objects.Write((ushort)0x0403); objects.Write(1101U); objects.Write((byte)0);
            objects.Write((byte)4); objects.Write(9U);
            objects.Write(eventId); objects.Write((byte)1); objects.Write(300U);
        }
        WriteChunk(writer, "HIRC", hirc.ToArray());
        return stream.ToArray();
    }

    [Fact]
    public void InstalledDltbCatalogContainsWeaponWemsButNotMusicPrefetch()
    {
        var install = Installs.Require("dltb");
        var sources = AespCatalog.FindSources(install, includeCustom: false);
        Assert.Contains(sources, s => Path.GetFileName(s.Path).Equals("streams.aesp", StringComparison.OrdinalIgnoreCase));
        var clock = Stopwatch.StartNew();
        var catalog = AespCatalog.ScanSources(sources, TestContext.Current.CancellationToken);
        clock.Stop();
        TestContext.Current.TestOutputHelper?.WriteLine($"Audio scan: {catalog.Entries.Count:N0} WEMs in {clock.Elapsed.TotalSeconds:0.00}s; {catalog.Warnings.Count} warnings");
        Assert.Equal(0, catalog.ErrorCount);
        Assert.True(catalog.Entries.Count > 3000);
        foreach (var id in new ulong[] { 553201780, 904242063, 796795086 })
            Assert.Contains(catalog.Entries, e => e.WemId == id);
        Assert.Equal(2526, catalog.Entries.Count(e => e.BankName == "weapons_pre"));
        Assert.Equal(1196, catalog.Entries.Count(e => e.BankName == "cnt_dlcft_weapon_pre"));
        Assert.DoesNotContain(catalog.Entries, e => e.BankName == "cnt_dlcft_music_pre");
        Assert.DoesNotContain(catalog.Entries, e => e.Name.Contains("wwisepinhead", StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] CreateWem()
    {
        byte[] bytes = new byte[46];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 16000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 32000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 2);
        return bytes;
    }

    private static byte[] CreatePcmWem(int channels, int frames)
    {
        byte[] bytes = new byte[44 + channels * frames * 2];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 16000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), (uint)(16000 * channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), (uint)(frames * channels * 2));
        for (int i = 44; i < bytes.Length; i++) bytes[i] = (byte)(i * 13);
        return bytes;
    }

    private static byte[] CreateLoopedPcmWem()
    {
        byte[] pcm = CreatePcmWem(1, 100);
        byte[] loop = new byte[60];
        BinaryPrimitives.WriteUInt32LittleEndian(loop.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(loop.AsSpan(44), 10);
        BinaryPrimitives.WriteUInt32LittleEndian(loop.AsSpan(48), 79);
        byte[] wem = new byte[pcm.Length + 68];
        pcm.CopyTo(wem, 0);
        "smpl"u8.CopyTo(wem.AsSpan(pcm.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(wem.AsSpan(pcm.Length + 4), 60);
        loop.CopyTo(wem, pcm.Length + 8);
        BinaryPrimitives.WriteUInt32LittleEndian(wem.AsSpan(4), (uint)wem.Length - 8);
        return wem;
    }

    private static int FindChunk(byte[] wav, string id)
    {
        for (int offset = 12; offset <= wav.Length - 8;)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(offset + 4));
            if (size > wav.Length - offset - 8) break;
            if (Encoding.ASCII.GetString(wav, offset, 4) == id) return offset;
            offset = checked(offset + 8 + (int)size + (int)(size & 1));
        }
        throw new InvalidDataException($"Missing WAV chunk {id}");
    }

    private static Dictionary<string, string> ReadInfo(byte[] wav, int list)
    {
        Dictionary<string, string> fields = [];
        int end = checked(list + 8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(list + 4)));
        for (int offset = list + 12; offset <= end - 8;)
        {
            string id = Encoding.ASCII.GetString(wav, offset, 4);
            int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(offset + 4)));
            Assert.InRange(offset + 8 + size, 0, end);
            fields[id] = Encoding.UTF8.GetString(wav, offset + 8, size).TrimEnd('\0');
            offset += 8 + size + (size & 1);
        }
        return fields;
    }

    internal static string ShellProperty(string path, string propertyName)
    {
        Type shellType = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Windows Shell is unavailable");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic folder = shell.Namespace(Path.GetDirectoryName(path)!)!;
        dynamic file = folder.ParseName(Path.GetFileName(path))!;
        object? value = file.ExtendedProperty(propertyName);
        return value is Array values
            ? string.Join("; ", values.Cast<object?>().Select(item => item?.ToString()))
            : value?.ToString() ?? string.Empty;
    }

    private static byte[] CreateBank(byte[] fullWem)
    {
        byte[] prefetch = fullWem[..12];
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        byte[] version = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(version, 150);
        WriteChunk(writer, "BKHD", version);

        using var didx = new MemoryStream();
        using (var index = new BinaryWriter(didx, Encoding.ASCII, leaveOpen: true))
        {
            index.Write(101U); index.Write(0U); index.Write((uint)fullWem.Length);
            index.Write(202U); index.Write((uint)fullWem.Length); index.Write((uint)prefetch.Length);
            index.Write(303U); index.Write((uint)(fullWem.Length + prefetch.Length)); index.Write((uint)prefetch.Length);
        }
        WriteChunk(writer, "DIDX", didx.ToArray());
        WriteChunk(writer, "DATA", [.. fullWem, .. prefetch, .. prefetch]);

        using var hirc = new MemoryStream();
        using (var objects = new BinaryWriter(hirc, Encoding.ASCII, leaveOpen: true))
        {
            objects.Write(3U);
            WriteSound(objects, 101U, 0, (uint)fullWem.Length);
            WriteSound(objects, 202U, 1, (uint)prefetch.Length);
            WriteSound(objects, 303U, 0, (uint)prefetch.Length);
        }
        WriteChunk(writer, "HIRC", hirc.ToArray());
        return stream.ToArray();
    }

    private static void WriteSound(BinaryWriter writer, uint mediaId, byte streamType, uint memorySize)
    {
        writer.Write((byte)2);
        writer.Write(17U);
        writer.Write(mediaId + 1000);
        writer.Write(0U);
        writer.Write(streamType);
        writer.Write(mediaId);
        writer.Write(memorySize);
    }

    private static void WriteChunk(BinaryWriter writer, string name, byte[] bytes)
    {
        writer.Write(Encoding.ASCII.GetBytes(name));
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteFixture(string path, (string Name, ulong TableKey, byte[] Payload)[] rows)
    {
        int payloadStart = AespCatalog.HeaderSize + rows.Length * AespCatalog.RowSize;
        Dictionary<byte[], int> payloadOffsets = new(ReferenceEqualityComparer.Instance);
        int length = payloadStart;
        foreach (var row in rows)
            if (payloadOffsets.TryAdd(row.Payload, length)) length += row.Payload.Length;

        var bytes = new byte[length];
        Encoding.UTF8.GetBytes("test", bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x88), AespCatalog.HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x90), (ulong)rows.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x98), 42);
        for (int i = 0; i < rows.Length; i++)
        {
            var row = bytes.AsSpan(AespCatalog.HeaderSize + i * AespCatalog.RowSize, AespCatalog.RowSize);
            Encoding.UTF8.GetBytes(rows[i].Name, row);
            BinaryPrimitives.WriteUInt64LittleEndian(row[0x80..], rows[i].TableKey);
            BinaryPrimitives.WriteUInt64LittleEndian(row[0x88..], (ulong)payloadOffsets[rows[i].Payload]);
            BinaryPrimitives.WriteUInt64LittleEndian(row[0x90..], (ulong)rows[i].Payload.Length);
        }
        foreach (var (payload, offset) in payloadOffsets)
            payload.CopyTo(bytes.AsSpan(offset));
        File.WriteAllBytes(path, bytes);
    }
}
