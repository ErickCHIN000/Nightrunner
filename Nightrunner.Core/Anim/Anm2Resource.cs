using Nightrunner.Core.Rpack;

namespace Nightrunner.Core.Anim;

/// <summary>
/// ANM2 clips in a pack: logical type 0x40 with either one 0x40 part (plain, static layer) or a 0x44 header part and
/// a 0x45 payload part (stream pair, logical flags 0x21, the 0x45 part has physical bit 8). The two forms are the same
/// bytes: plain = 0x44 ‖ 0x45.
/// </summary>
public static class Anm2Resource
{
    public const byte TypeAnimation = 0x40, PartHeader = 0x44, PartPayload = 0x45;

    public static bool IsClip(RpackFile pack, int logicalIndex) => pack.Logicals[logicalIndex].Type == TypeAnimation;

    /// <summary>The clip's bytes in the plain form; a stream pair is joined after checking the parts' sizes.</summary>
    public static byte[] Read(RpackFile pack, int logicalIndex) => Read(pack, logicalIndex, out _);

    public static byte[] Read(RpackFile pack, int logicalIndex, out bool streamPair)
    {
        var lg = pack.Logicals[logicalIndex];
        string name = pack.Name(logicalIndex);
        if (lg.Type != TypeAnimation) throw new Anm2FormatException($"'{name}' is type 0x{lg.Type:X2}, not an animation");
        int first = (int)lg.FirstPart;
        byte ReadType(int k) => pack.PartType(first + k);
        byte[] Part(int k) => pack.PartUnreadableReason(first + k) is { } why
            ? throw new Anm2FormatException($"'{name}': part 0x{ReadType(k):X2} unreadable — {why}")
            : pack.ReadPart(first + k);
        if (lg.PartCount == 1 && ReadType(0) == TypeAnimation)
        {
            streamPair = false;
            return Part(0);
        }
        if (lg.PartCount == 2 && ReadType(0) == PartHeader && ReadType(1) == PartPayload)
        {
            streamPair = true;
            var hd = Part(0);
            var pd = Part(1);
            var all = new byte[hd.Length + pd.Length];
            hd.CopyTo(all, 0);
            pd.CopyTo(all, hd.Length);
            if (Anm2Header.PeekVersion(hd) is not (0 or 1 or 2 or 3)) return all;   // unknown versions: refused at decode
            var h = Anm2Header.Parse(hd, pd.Length);
            if (hd.Length != h.HeaderSize) throw new Anm2FormatException($"'{name}': 0x44 part is {hd.Length} bytes, header16 says {h.HeaderSize}");
            if (pd.Length != h.PayloadSize) throw new Anm2FormatException($"'{name}': 0x45 part is {pd.Length} bytes, payload16 says {h.PayloadSize}");
            return all;
        }
        var shape = string.Join(" ", Enumerable.Range(0, lg.PartCount).Select(k => $"0x{ReadType(k):X2}"));
        throw new Anm2FormatException($"'{name}': unexpected part shape [{shape}]");
    }

    /// <summary>Plain clip → (0x44 header part, 0x45 payload part).</summary>
    public static (byte[] Header, byte[] Payload) SplitStreamPair(byte[] clip)
    {
        var h = Anm2Header.Parse(clip);
        if (clip.Length != h.HeaderSize + h.PayloadSize) throw new Anm2FormatException($"clip is {clip.Length} bytes, header + payload are {h.HeaderSize + h.PayloadSize}");
        return (clip[..h.HeaderSize], clip[h.HeaderSize..]);
    }

    public static Anm2Clip Decode(RpackFile pack, int logicalIndex) => Anm2Clip.Decode(Read(pack, logicalIndex));
}
