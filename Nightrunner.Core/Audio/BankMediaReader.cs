using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace Nightrunner.Core.Audio;

internal readonly record struct BankMedia(uint Id, int Index, long Offset, long Size);

internal static class BankMediaReader
{
    private const int MaxMetadataBytes = 64 * 1024 * 1024;

    private readonly record struct Chunk(long Offset, uint Size);
    private readonly record struct IndexedMedia(uint Id, uint Offset, uint Size, int Index);

    public static BankMedia[] Read(FileStream file, long bankOffset, long bankSize, CancellationToken ct)
    {
        long end = checked(bankOffset + bankSize);
        Chunk? didx = null;
        Chunk? data = null;
        Chunk? hirc = null;
        uint version = 0;
        Span<byte> header = stackalloc byte[8];
        Span<byte> number = stackalloc byte[4];

        for (long position = bankOffset; position < end;)
        {
            ct.ThrowIfCancellationRequested();
            if (end - position < header.Length)
                throw new AespFormatException("bank has a truncated chunk header");
            ReadExactlyAt(file.SafeFileHandle, header, position);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            long payload = position + header.Length;
            if (size > end - payload)
                throw new AespFormatException("bank chunk exceeds its AESP entry");

            var chunk = new Chunk(payload, size);
            if (header[..4].SequenceEqual("BKHD"u8))
            {
                if (size < 4) throw new AespFormatException("BKHD is truncated");
                ReadExactlyAt(file.SafeFileHandle, number, payload);
                version = BinaryPrimitives.ReadUInt32LittleEndian(number);
            }
            else if (header[..4].SequenceEqual("DIDX"u8)) didx = chunk;
            else if (header[..4].SequenceEqual("DATA"u8)) data = chunk;
            else if (header[..4].SequenceEqual("HIRC"u8)) hirc = chunk;

            position = payload + size;
        }

        if (version != 150)
            throw new AespFormatException($"unsupported Wwise bank version {version}");
        if (didx is null || data is null || hirc is null) return [];
        if (didx.Value.Size % 12 != 0)
            throw new AespFormatException("DIDX size is not a multiple of 12");

        byte[] indexBytes = ReadMetadata(file, didx.Value);
        byte[] hircBytes = ReadMetadata(file, hirc.Value);
        Dictionary<uint, IndexedMedia> media = new(indexBytes.Length / 12);
        for (int i = 0; i < indexBytes.Length; i += 12)
        {
            var row = indexBytes.AsSpan(i, 12);
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(row);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(row[8..]);
            if ((ulong)offset + size > data.Value.Size)
                throw new AespFormatException($"DIDX media {id} exceeds DATA");
            if (!media.TryAdd(id, new(id, offset, size, i / 12)))
                throw new AespFormatException($"duplicate DIDX media ID {id}");
        }

        if (hircBytes.Length < 4) throw new AespFormatException("HIRC is truncated");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(hircBytes);
        if (count > (uint)((hircBytes.Length - 4) / 5))
            throw new AespFormatException("HIRC object count exceeds its chunk");

        HashSet<uint> completeIds = [];
        int cursor = 4;
        for (uint i = 0; i < count; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            if (hircBytes.Length - cursor < 5)
                throw new AespFormatException("HIRC object header is truncated");
            byte type = hircBytes[cursor];
            uint itemSize = BinaryPrimitives.ReadUInt32LittleEndian(hircBytes.AsSpan(cursor + 1, 4));
            cursor += 5;
            if (itemSize < 4 || itemSize > hircBytes.Length - cursor)
                throw new AespFormatException("HIRC object exceeds its chunk");

            // Prefetch fragments stay hidden; only complete embedded media is exposed.
            if (type == 2 && itemSize >= 17)
            {
                var source = hircBytes.AsSpan(cursor + 4, 13);
                byte streamType = source[4];
                uint mediaId = BinaryPrimitives.ReadUInt32LittleEndian(source[5..]);
                uint memorySize = BinaryPrimitives.ReadUInt32LittleEndian(source[9..]);
                if (streamType == 0 && media.TryGetValue(mediaId, out var indexed) && memorySize == indexed.Size)
                    completeIds.Add(mediaId);
            }
            cursor += (int)itemSize;
        }

        List<BankMedia> result = new(completeIds.Count);
        foreach (var indexed in media.Values.OrderBy(m => m.Index))
        {
            if (!completeIds.Contains(indexed.Id)) continue;
            long offset = data.Value.Offset + indexed.Offset;
            if (IsCompleteWem(file, offset, indexed.Size, exactSize: true))
                result.Add(new(indexed.Id, indexed.Index, offset, indexed.Size));
        }
        return [.. result];
    }

    internal static bool IsCompleteWem(FileStream file, long offset, long size, bool exactSize = false)
    {
        if (size < 12) return false;
        Span<byte> riff = stackalloc byte[12];
        ReadExactlyAt(file.SafeFileHandle, riff, offset);
        if (!riff[..4].SequenceEqual("RIFF"u8) || !riff[8..12].SequenceEqual("WAVE"u8)) return false;
        long declaredSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(riff[4..]) + 8;
        return declaredSize >= 12 && (exactSize ? declaredSize == size : declaredSize <= size);
    }

    internal static void ReadExactlyAt(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        while (!buffer.IsEmpty)
        {
            int read = RandomAccess.Read(handle, buffer, offset);
            if (read == 0) throw new EndOfStreamException();
            buffer = buffer[read..];
            offset += read;
        }
    }

    private static byte[] ReadMetadata(FileStream file, Chunk chunk)
    {
        if (chunk.Size > MaxMetadataBytes)
            throw new AespFormatException($"bank metadata chunk is larger than {MaxMetadataBytes:N0} bytes");
        var bytes = new byte[checked((int)chunk.Size)];
        ReadExactlyAt(file.SafeFileHandle, bytes, chunk.Offset);
        return bytes;
    }
}
