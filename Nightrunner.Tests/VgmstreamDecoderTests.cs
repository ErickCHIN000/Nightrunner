using Nightrunner.Core.Audio;

namespace Nightrunner.Tests;

public class VgmstreamDecoderTests
{
    [Fact]
    public void ExportsInstalledWeaponVorbisAsWav()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = AespCatalog.Scan(Installs.Require("dltb"), ct: ct);
        int index = -1;
        for (int i = 0; i < catalog.Entries.Count; i++)
            if (catalog.Entries[i].BankName == "weapons_pre") { index = i; break; }
        Assert.True(index >= 0);
        var dir = Directory.CreateTempSubdirectory("nr-vorbis-export-");
        try
        {
            string path = Path.Combine(dir.FullName, "weapon.wav");
            using var decoder = new VgmstreamDecoder(catalog, index);
            var metadata = new WavMetadata("wpn_smg_shot_tpp", "wpn_smg_shot_tpp",
                                           "Dying Light: The Beast - meta", $"Sound ID: {catalog.Entries[index].WemId}");
            var result = AudioExporter.ExportWav(catalog, index, path, ct: ct, metadata: metadata);
            Assert.True(result.PcmBytes > 0);
            Assert.Equal(decoder.Channels, result.Channels);
            Assert.Equal(decoder.SampleRate, result.SampleRate);
            int header = decoder.Channels > 2 ? 68 : 44;
            Assert.True(new FileInfo(path).Length > header + result.PcmBytes);
            Assert.Equal(metadata.Title, AespCatalogTests.ShellProperty(path, "System.Title"));
            Assert.Equal(metadata.Artist, AespCatalogTests.ShellProperty(path, "System.Music.Artist"));
            Assert.Equal(metadata.Album, AespCatalogTests.ShellProperty(path, "System.Music.AlbumTitle"));
            Assert.Equal(metadata.Comment, AespCatalogTests.ShellProperty(path, "System.Comment"));
            using var output = File.OpenRead(path);
            output.Position = header;
            byte[] actual = new byte[decoder.BlockAlign * 128];
            output.ReadExactly(actual);
            byte[] expected = new byte[actual.Length];
            Assert.Equal(actual.Length, decoder.Read(expected));
            Assert.Equal(expected, actual);
        }
        finally { Directory.Delete(dir.FullName, recursive: true); }
    }

    [Fact]
    public void PlayerDownmixKeepsSourceChannelCountAvailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var install = Installs.Require("dltb");
        var catalog = AespCatalog.Scan(install, ct: ct);
        var names = AudioNameIndex.Build(catalog, ct);
        const string eventName = "ambience_quest_opera_crowd_loop";
        int index = -1;
        for (int i = 0; i < catalog.Entries.Count; i++)
        {
            if (!names.EventsFor(catalog.Entries[i]).Contains(eventName)) continue;
            using var candidate = new VgmstreamDecoder(catalog, i);
            if (candidate.Channels <= 2) continue;
            index = i;
            break;
        }
        Assert.True(index >= 0, $"No multichannel WEM found for {eventName}");

        using var source = new VgmstreamDecoder(catalog, index);
        using var playback = new VgmstreamDecoder(catalog, index, downmixToStereo: true);
        Assert.True(source.Channels > 2);
        Assert.Equal(2, playback.Channels);
        Assert.Equal(source.SampleRate, playback.SampleRate);
        Assert.Equal(source.TotalSamples, playback.TotalSamples);
        byte[] pcm = new byte[playback.BlockAlign * 4096];
        int read = playback.Read(pcm);
        Assert.True(read > 0);
        Assert.Contains(pcm.AsSpan(0, read).ToArray(), b => b != 0);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{catalog.Entries[index].WemId}: source {source.Channels}ch, player {playback.Channels}ch, {playback.SampleRate}Hz, loop {source.LoopStartSample}-{source.LoopEndSample}");
    }

    [Fact]
    public void DecodesAndSeeksInstalledLooseAndWeaponWems()
    {
        var install = Installs.Require("dltb");
        var catalog = AespCatalog.Scan(install, ct: TestContext.Current.CancellationToken);
        int[] indices = [
            Find(e => e.WemId == 904242063 && e.Kind == AespEntryKind.LooseWem),
            Find(e => e.BankName == "weapons_pre"),
        ];

        foreach (int index in indices)
        {
            using var decoder = new VgmstreamDecoder(catalog, index);
            Assert.InRange(decoder.Channels, 1, 16);
            Assert.True(decoder.SampleRate >= 8000);
            Assert.True(decoder.Duration > TimeSpan.Zero);
            Assert.Contains("Vorbis", decoder.CodecName, StringComparison.OrdinalIgnoreCase);
            byte[] pcm = new byte[decoder.BlockAlign * 4096];
            int first = decoder.Read(pcm);
            Assert.True(first > 0);
            Assert.Contains(pcm.AsSpan(0, first).ToArray(), b => b != 0);
            decoder.Seek(TimeSpan.FromSeconds(Math.Min(1, decoder.Duration.TotalSeconds / 2)));
            Assert.True(decoder.Read(pcm) > 0);
            decoder.Seek(decoder.Duration - TimeSpan.FromMilliseconds(20));
            int tailBytes = 0;
            int read;
            while ((read = decoder.Read(pcm)) > 0)
                tailBytes += read;
            Assert.InRange(tailBytes, 1, decoder.SampleRate * decoder.BlockAlign);
            Assert.Equal(0, decoder.Read(pcm));
            decoder.Seek(TimeSpan.Zero);
            Assert.True(decoder.Read(pcm) > 0);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{catalog.Entries[index].Name}: {decoder.CodecName} {decoder.Channels}ch {decoder.SampleRate}Hz {decoder.Duration}, loop {decoder.LoopStartSample}-{decoder.LoopEndSample}");
        }

        int Find(Func<AespEntry, bool> predicate)
        {
            for (int i = 0; i < catalog.Entries.Count; i++)
                if (predicate(catalog.Entries[i])) return i;
            throw new InvalidOperationException("Required WEM was not found");
        }
    }
}
