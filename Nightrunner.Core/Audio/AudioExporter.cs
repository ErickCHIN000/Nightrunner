using System.Buffers;
using System.Text;

namespace Nightrunner.Core.Audio;

public readonly record struct WavExportResult(long PcmBytes, int Channels, int SampleRate);
public sealed record WavMetadata(string Title, string Artist, string Album, string Comment);

public static class AudioExporter
{
    private const int BufferSize = 128 * 1024;

    public static long ExportWem(AespCatalog catalog, int entryIndex, string destination,
                                 bool overwrite = false, CancellationToken ct = default,
                                 Action<long, long>? progress = null)
    {
        var entry = catalog.Entries[entryIndex];
        var archive = catalog.Archives[entry.ArchiveId];
        RejectArchiveDestination(destination, archive.Source.Path);
        using var source = new FileStream(archive.Source.Path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.RandomAccess);
        if (source.Length != archive.FileLength || entry.Offset < 0 || entry.Size < 12 ||
            entry.Offset > source.Length || entry.Size > source.Length - entry.Offset ||
            !BankMediaReader.IsCompleteWem(source, entry.Offset, entry.Size))
            throw new IOException("Audio archive changed after scanning; reload the Explorer");

        return WriteAtomically(destination, overwrite, ct, output =>
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                long copied = 0;
                long nextReport = 4L * 1024 * 1024;
                while (copied < entry.Size)
                {
                    ct.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(buffer.Length, entry.Size - copied);
                    int read = RandomAccess.Read(source.SafeFileHandle, buffer.AsSpan(0, count), entry.Offset + copied);
                    if (read == 0) throw new EndOfStreamException("WEM data ended during export");
                    output.Write(buffer, 0, read);
                    copied += read;
                    if (copied >= nextReport)
                    {
                        progress?.Invoke(copied, entry.Size);
                        nextReport = copied + 4L * 1024 * 1024;
                    }
                }
                progress?.Invoke(copied, entry.Size);
                return copied;
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        });
    }

    public static WavExportResult ExportWav(AespCatalog catalog, int entryIndex, string destination,
                                            bool overwrite = false, CancellationToken ct = default,
                                            Action<long, long>? progress = null, WavMetadata? metadata = null)
    {
        ct.ThrowIfCancellationRequested();
        var entry = catalog.Entries[entryIndex];
        RejectArchiveDestination(destination, catalog.Archives[entry.ArchiveId].Source.Path);
        using var decoder = new VgmstreamDecoder(catalog, entryIndex);
        byte[] infoChunk = BuildInfoChunk(metadata);
        int headerSize = checked((decoder.Channels > 2 ? 68 : 44) + (decoder.HasLoop ? 68 : 0));
        long limit = uint.MaxValue - (headerSize + (long)infoChunk.Length - 8L);
        if (decoder.TotalSamples > limit / decoder.BlockAlign)
            throw new NotSupportedException("Decoded audio exceeds the 4 GiB RIFF/WAV limit");
        long expected = decoder.TotalSamples * decoder.BlockAlign;

        return WriteAtomically(destination, overwrite, ct, output =>
        {
            output.Position = headerSize;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                long written = 0;
                long nextReport = 4L * 1024 * 1024;
                int read;
                while ((read = decoder.Read(buffer)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    if (read % decoder.BlockAlign != 0 || read > limit - written)
                        throw new InvalidDataException("Decoder returned invalid PCM length");
                    output.Write(buffer, 0, read);
                    written += read;
                    if (written >= nextReport)
                    {
                        progress?.Invoke(written, expected);
                        nextReport = written + 4L * 1024 * 1024;
                    }
                }
                ct.ThrowIfCancellationRequested();
                // Explorer reads LIST/INFO reliably when it follows the PCM data.
                output.Write(infoChunk);
                output.Position = 0;
                WriteWavHeader(output, decoder, (uint)written, headerSize, infoChunk.Length);
                progress?.Invoke(written, expected);
                return new WavExportResult(written, decoder.Channels, decoder.SampleRate);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        });
    }

    private static T WriteAtomically<T>(string destination, bool overwrite, CancellationToken ct,
                                        Func<FileStream, T> write)
    {
        // A cancelled export must not leave a partial destination behind.
        string target = Path.GetFullPath(destination);
        if (!overwrite && File.Exists(target)) throw new IOException($"Export file already exists: {target}");
        string temporary = Path.Combine(Path.GetDirectoryName(target)!, "." + Path.GetFileName(target) +
                                "." + Guid.NewGuid().ToString("N") + ".tmp");
        bool created = false;
        try
        {
            T result;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                                               FileShare.None, BufferSize, FileOptions.SequentialScan))
            {
                created = true;
                result = write(output);
                output.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite);
            return result;
        }
        finally
        {
            if (created && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void RejectArchiveDestination(string destination, string archive)
    {
        string target = Path.GetFullPath(destination);
        if (Path.GetExtension(target).Equals(".aesp", StringComparison.OrdinalIgnoreCase) ||
            target.Equals(Path.GetFullPath(archive), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Export destination cannot be an audio archive");
    }

    private static void WriteWavHeader(Stream output, VgmstreamDecoder decoder, uint dataBytes,
                                       int headerSize, int infoSize)
    {
        bool extensible = decoder.Channels > 2;
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(checked((uint)(headerSize + (long)dataBytes + infoSize - 8L)));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(extensible ? 40U : 16U);
        writer.Write((ushort)(extensible ? 0xFFFE : 1));
        writer.Write((ushort)decoder.Channels);
        writer.Write((uint)decoder.SampleRate);
        writer.Write(checked((uint)(decoder.SampleRate * decoder.BlockAlign)));
        writer.Write((ushort)decoder.BlockAlign);
        writer.Write((ushort)16);
        if (extensible)
        {
            writer.Write((ushort)22);
            writer.Write((ushort)16);
            writer.Write(decoder.ChannelMask);
            writer.Write(new Guid("00000001-0000-0010-8000-00AA00389B71").ToByteArray());
        }
        if (decoder.HasLoop)
        {
            writer.Write("smpl"u8);
            writer.Write(60U);
            writer.Write(0U);
            writer.Write(0U);
            writer.Write((uint)(1_000_000_000L / decoder.SampleRate));
            writer.Write(0U);
            writer.Write(0U);
            writer.Write(0U);
            writer.Write(0U);
            writer.Write(1U);
            writer.Write(0U);
            writer.Write(0U);
            writer.Write(0U);
            writer.Write(checked((uint)decoder.LoopStartSample!.Value));
            // Wwise's loop end is exclusive; WAV smpl stores an inclusive end.
            writer.Write(checked((uint)(decoder.LoopEndSample!.Value - 1)));
            writer.Write(0U);
            writer.Write(0U);
        }
        writer.Write("data"u8);
        writer.Write(dataBytes);
        if (output.Position != headerSize) throw new InvalidDataException("WAV header size mismatch");
    }

    private static byte[] BuildInfoChunk(WavMetadata? metadata)
    {
        if (metadata is null) return [];
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write("LIST"u8);
        writer.Write(0U);
        writer.Write("INFO"u8);
        WriteInfoField(writer, "INAM", metadata.Title);
        WriteInfoField(writer, "IART", metadata.Artist);
        WriteInfoField(writer, "IPRD", metadata.Album);
        WriteInfoField(writer, "ICMT", metadata.Comment);
        if (output.Length == 12) return [];
        output.Position = 4;
        writer.Write(checked((uint)(output.Length - 8)));
        return output.ToArray();
    }

    private static void WriteInfoField(BinaryWriter writer, string id, string value)
    {
        string clean = value.Replace('\0', ' ').Trim();
        if (clean.Length == 0) return;
        byte[] bytes = Encoding.UTF8.GetBytes(clean);
        int size = checked(bytes.Length + 1);
        writer.Write(Encoding.ASCII.GetBytes(id));
        writer.Write((uint)size);
        writer.Write(bytes);
        writer.Write((byte)0);
        if ((size & 1) != 0) writer.Write((byte)0);
    }
}
