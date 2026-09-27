using System.Runtime.InteropServices;

namespace Nightrunner.Core.Rpack;

/// <summary>On-disk record layout of an RP6L v4 container. Ported from nightrunner/container/rp6l.py.</summary>
/// <remarks>
/// Header 36 B, Storage 20 B, Physical 16 B, Logical 12 B, u32 name offsets, NUL-terminated name blob, payload.
/// Part offset = (storage.BaseUnits + physical.OffsetUnits) &lt;&lt; 4. Every record keeps its raw packed words;
/// derived fields are properties so unknown bits survive a read/write round trip.
/// </remarks>
public static class RpackFormat
{
    public const uint Magic = 0x4C365052;   // 'RP6L'
    public const uint Version = 4;
    public const int HeaderSize = 36;
    public const int StorageSize = 20;
    public const int PhysicalSize = 16;
    public const int LogicalSize = 12;
    public const int UnitShift = 4;         // payload addresses are in 16-byte units
    public const int MaxParts = 15;
    public const int MaxStorages = 256;

    public const uint Field08OnDemand = 0x1000;      // header.Field08 bit 12 — single contiguous on-demand read
    public const byte StorageFlagStream = 0x08;      // storage.Flags bit 3

    // logical.Flags byte values seen in shipped packs (bit 0 always set; otherwise unknown). A writer copies the
    // source resource's byte; these name the stock values.
    public const byte LogicalFlagsDefault = 0x01;        // textures, anims, prefabs, areas, envprobes, method-0 meshes
    public const byte LogicalFlagsOnDemandMesh = 0x81;   // type 0x10 in field08 bit-12 packs
    public const byte LogicalFlagsAnm2Stream = 0x21;     // type 0x40 stored as 0x44 + 0x45 in *_stream packs

    // physical.Packed bits
    public const uint PhysStorageMask = 0xFF;
    public const uint PhysBit8 = 0x0100;             // preload scan skips
    public const int PhysPriorityShift = 9;          // bits 9..11
    public const uint PhysPriorityMask = 0x7;
    public const uint PhysSpecial = 0x1000;          // bit 12
    public const uint PhysChild = 0x2000;            // bit 13 — payload lives in the .rpacz child pack
    public const uint PhysBit14 = 0x4000;
    public const uint PhysBit15 = 0x8000;
    public const int PhysOwnerShift = 16;            // bits 16..31 — owning logical index
    public const uint PhysFlagBits = 0xFF00;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = RpackFormat.HeaderSize)]
public struct RpackHeader
{
    public uint Magic;
    public uint Version;
    public uint Field08;
    public uint PhysicalCount;
    public uint StorageCount;
    public uint NameCount;
    public uint NameBytes;
    public uint LogicalCount;
    public uint Flags;

    public readonly bool OnDemand => (Field08 & RpackFormat.Field08OnDemand) != 0;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = RpackFormat.StorageSize)]
public struct Storage
{
    public byte Type;
    public byte AlignRaw;
    public byte Flags;
    public byte Metadata;
    public uint BaseUnits;
    public uint SizeLo;
    public uint CompressedLo;
    public ushort Count;
    public byte SizeHi;
    public byte CompressedHi;

    public readonly long Size => SizeLo | ((long)SizeHi << 32);
    public readonly long Compressed => CompressedLo | ((long)CompressedHi << 32);
    public readonly int Alignment => 1 << ((AlignRaw >> 1) & 0xF);
    public readonly int Method => Flags & 3;
    public readonly int FormatVersion => (Flags >> 4) | ((Metadata & 0xF) << 4);
    public readonly int Codec => Metadata >> 4;
    public readonly bool Stream => (Flags & RpackFormat.StorageFlagStream) != 0;
    public readonly long BaseOffset => (long)BaseUnits << RpackFormat.UnitShift;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = RpackFormat.PhysicalSize)]
public struct Physical
{
    public uint Packed;
    public uint OffsetUnits;
    public uint Size;
    public uint Fc;

    public readonly int StorageIndex => (int)(Packed & RpackFormat.PhysStorageMask);
    public readonly int Owner => (int)(Packed >> RpackFormat.PhysOwnerShift);
    public readonly uint FlagBits => Packed & RpackFormat.PhysFlagBits;
    public readonly bool Bit8 => (Packed & RpackFormat.PhysBit8) != 0;
    public readonly int Priority => (int)((Packed >> RpackFormat.PhysPriorityShift) & RpackFormat.PhysPriorityMask);
    public readonly bool Special => (Packed & RpackFormat.PhysSpecial) != 0;
    public readonly bool Child => (Packed & RpackFormat.PhysChild) != 0;
    public readonly bool Bit14 => (Packed & RpackFormat.PhysBit14) != 0;
    public readonly bool Bit15 => (Packed & RpackFormat.PhysBit15) != 0;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = RpackFormat.LogicalSize)]
public struct Logical
{
    public uint Packed;
    public uint NameIndex;
    public uint FirstPart;

    public readonly int PartCount => (int)(Packed & 0xFFFF);
    public readonly byte Type => (byte)((Packed >> 16) & 0xFF);
    public readonly byte Flags => (byte)(Packed >> 24);
}
