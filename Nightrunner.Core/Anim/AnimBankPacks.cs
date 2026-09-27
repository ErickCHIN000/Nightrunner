using Nightrunner.Core.Rpack;

namespace Nightrunner.Core.Anim;

/// <summary>
/// Reading the three bank families out of a pack. The engine registers them from every rpack
/// (<c>RegisterAnimations</c> 0x180cf3b10); shipped DLTB has them only in the anim packs and one lang pack.
/// </summary>
public static class AnimBankPacks
{
    public const byte TypeScr = 0x42, TypeGraph = 0x47, TypeCustom = 0x49;

    public static readonly byte[] ScrShape = [0x42, 0x43];
    public static readonly byte[] GraphShape = [0x47, 0x48];
    public static readonly byte[] CustomShape = [0x49, 0x4A, 0x49, 0x4A];

    /// <summary>Logical indices of one resource type, in pack order.</summary>
    public static IEnumerable<int> OfType(RpackFile pack, byte type)
    {
        for (int i = 0; i < pack.Count; i++)
            if (pack.Logicals[i].Type == type) yield return i;
    }

    /// <summary>The parts of a resource in order, refused unless their types are exactly <paramref name="shape"/>.</summary>
    public static byte[][] ReadParts(RpackFile pack, int logical, byte[] shape)
    {
        var lg = pack.Logicals[logical];
        string name = pack.Name(logical);
        var types = Enumerable.Range(0, lg.PartCount).Select(k => pack.PartType((int)lg.FirstPart + k)).ToArray();
        if (!types.SequenceEqual(shape))
            throw new AnimBankFormatException(
                $"'{name}': parts [{string.Join(", ", types.Select(t => $"0x{t:X2}"))}], expected " +
                $"[{string.Join(", ", shape.Select(t => $"0x{t:X2}"))}]");
        var parts = new byte[shape.Length][];
        for (int k = 0; k < shape.Length; k++)
        {
            int phys = (int)lg.FirstPart + k;
            if (pack.PartUnreadableReason(phys) is { } why)
                throw new AnimBankFormatException($"'{name}': part {k} (0x{shape[k]:X2}) unreadable — {why}");
            parts[k] = pack.ReadPart(phys);
        }
        return parts;
    }

    public static AnimBank ReadBank(RpackFile pack, int logical)
    {
        var p = ReadParts(pack, logical, ScrShape);
        return AnimBank.Parse(p[0], p[1]);
    }

    public static AnimGraphBank ReadGraphBank(RpackFile pack, int logical)
    {
        var p = ReadParts(pack, logical, GraphShape);
        return AnimGraphBank.Parse(p[0], p[1]);
    }

    public static AnimCustomResource ReadCustom(RpackFile pack, int logical)
    {
        var p = ReadParts(pack, logical, CustomShape);
        return AnimCustomResource.Parse(p[0], p[1], p[2], p[3]);
    }

    /// <summary>First logical index of a resource of <paramref name="type"/> named <paramref name="name"/> (engine folding), or −1.</summary>
    public static int Find(RpackFile pack, byte type, string name)
    {
        foreach (int i in pack.IndicesOf(RpackFile.EngineFold(System.Text.Encoding.UTF8.GetBytes(name))))
            if (pack.Logicals[i].Type == type) return i;
        return -1;
    }
}
