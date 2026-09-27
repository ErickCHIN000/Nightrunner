using Nightrunner.Core.Audio;

namespace Nightrunner.Tests;

public class VgmstreamDecoderTests
{
    /// <summary>Decoding is optional: the native decoder is not in the repository (third_party/vgmstream/README.md).</summary>
    internal static void RequireDecoder()
    {
        if (AudioDecoding.Status is { Available: false } status)
            Assert.Skip($"audio decoder not available: {status.Reason}");
    }

    [Fact]
    public void ProbeNamesWhatIsMissing()
    {
        var dir = Directory.CreateTempSubdirectory("nr-decoder-probe-");
        try
        {
            var none = AudioDecoding.Probe(dir.FullName);
            Assert.False(none.Available);
            Assert.Equal("libvgmstream.dll not found", none.Reason);

            File.WriteAllBytes(Path.Combine(dir.FullName, AudioDecoding.DecoderFile), [0x4D, 0x5A]);
            var noVorbis = AudioDecoding.Probe(dir.FullName);
            Assert.False(noVorbis.Available);
            Assert.Equal("libvorbis.dll not found", noVorbis.Reason);

            // Both present but not loadable (not a real DLL): a load failure, never a crash.
            File.WriteAllBytes(Path.Combine(dir.FullName, AudioDecoding.VorbisFile), [0x4D, 0x5A]);
            var broken = AudioDecoding.Probe(dir.FullName);
            Assert.False(broken.Available);
            Assert.Contains("libvgmstream.dll", broken.Detail);
        }
        finally { Directory.Delete(dir.FullName, recursive: true); }
    }

    [Fact]
    public void UndisposedDecoderIsReclaimed()
    {
        RequireDecoder();
        var catalog = AespCatalog.Scan(Installs.Require("dltb"), ct: TestContext.Current.CancellationToken);
        int index = -1;
        for (int i = 0; i < catalog.Entries.Count && index < 0; i++)
            if (catalog.Entries[i].Size is > 20_000 and < 300_000) index = i;
        var dropped = Abandon(catalog, index, 50);
        for (int k = 0; k < 3; k++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        // A strong GCHandle to itself used to keep every undisposed decoder (native state, archive handle) alive forever.
        Assert.DoesNotContain(dropped, w => w.IsAlive);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference[] Abandon(AespCatalog catalog, int index, int count)
    {
        var weak = new WeakReference[count];
        for (int k = 0; k < count; k++)
        {
            var decoder = new VgmstreamDecoder(catalog, index);
            decoder.Read(new byte[decoder.BlockAlign * 1024]);
            weak[k] = new WeakReference(decoder);
        }
        return weak;
    }

    [Fact]
    public void ExportsInstalledWeaponVorbisAsWav()
    {
        RequireDecoder();
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
    public void ArchiveReadFailureMidDecodeThrowsInsteadOfSilence()
    {
        RequireDecoder();
        var catalog = AespCatalog.Scan(Installs.Require("dltb"), ct: TestContext.Current.CancellationToken);
        int index = -1;
        for (int i = 0; i < catalog.Entries.Count && index < 0; i++)
            if (catalog.Entries[i].Size is > 1_000_000 and < 4_000_000) index = i;
        using var decoder = new VgmstreamDecoder(catalog, index);
        byte[] pcm = new byte[decoder.BlockAlign * 4096];
        Assert.True(decoder.Read(pcm) > 0);
        // The archive goes away under the decoder (a removed drive, a replaced file). vgmstream fills the gap with
        // silence; the read must fail instead, or an export writes a full-length WAV of silence.
        ((FileStream)typeof(VgmstreamDecoder).GetField("_file", System.Reflection.BindingFlags.NonPublic |
                                                                System.Reflection.BindingFlags.Instance)!.GetValue(decoder)!).Dispose();
        Assert.Throws<IOException>(() => { while (decoder.Read(pcm) > 0) { } });
    }

    [Fact]
    public void PlayerDownmixKeepsSourceChannelCountAvailable()
    {
        RequireDecoder();
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
        RequireDecoder();
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
