using Nightrunner.Core.Rpack;

namespace Nightrunner.Core.Texture;

/// <summary>
/// A texture resource inside a pack: its IMGC header (part 0x20) and the bitmap part (0x21) it points at.
/// </summary>
public sealed class TextureResource
{
    public RpackFile Pack { get; }
    public int LogicalIndex { get; }
    public ImgcHeader Header { get; }

    /// <summary>Physical index of the bitmap part, or null for a header-only record.</summary>
    public int? BitmapPart { get; }

    public long BitmapSize { get; }

    /// <summary>16 for the stock per-level stride, 0 for the third-party tight layout, null when unknown.</summary>
    public int? LevelPadding { get; }

    public IReadOnlyList<ImgcLevel> Levels { get; }

    /// <summary>Why the payload cannot be read (compressed storage, child pack, size mismatch), or null.</summary>
    public string? PayloadProblem { get; }

    private TextureResource(RpackFile pack, int logicalIndex, ImgcHeader header, int? bitmapPart, long bitmapSize,
                            int? levelPadding, IReadOnlyList<ImgcLevel> levels, string? problem)
    {
        Pack = pack;
        LogicalIndex = logicalIndex;
        Header = header;
        BitmapPart = bitmapPart;
        BitmapSize = bitmapSize;
        LevelPadding = levelPadding;
        Levels = levels;
        PayloadProblem = problem;
    }

    public string Name => Pack.Name(LogicalIndex);
    public bool HasBitmap => BitmapPart is not null && PayloadProblem is null;

    /// <summary>Read a texture's header and work out its level layout. Throws only on a bad header.</summary>
    public static TextureResource Open(RpackFile pack, int logicalIndex)
    {
        var lg = pack.Logicals[logicalIndex];
        int first = (int)lg.FirstPart;
        int? headerPart = null, bitmapPart = null;
        for (int k = 0; k < lg.PartCount; k++)
        {
            byte t = pack.PartType(first + k);
            if (t == 0x20 && headerPart is null) headerPart = first + k;
            else if (t == 0x21 && bitmapPart is null) bitmapPart = first + k;
        }
        if (headerPart is null)
            throw new ImgcException($"{pack.Name(logicalIndex)}: no 0x20 header part");

        if (pack.PartUnreadableReason(headerPart.Value) is { } headerWhy)
            throw new ImgcException($"{pack.Name(logicalIndex)}: header part unreadable — {headerWhy}");

        var header = ImgcHeader.Parse(pack.ReadPart(headerPart.Value));

        if (bitmapPart is null)
            return new TextureResource(pack, logicalIndex, header, null, 0, null, [],
                header.HeaderOnly ? null : "no 0x21 bitmap part");

        long size = pack.Physicals[bitmapPart.Value].Size;
        if (pack.PartUnreadableReason(bitmapPart.Value) is { } why)
            return new TextureResource(pack, logicalIndex, header, bitmapPart, size, null, [], why);

        try
        {
            int padding = header.DetectLevelPadding(size);
            return new TextureResource(pack, logicalIndex, header, bitmapPart, size, padding,
                                       header.LevelLayout(padding), null);
        }
        catch (ImgcException e)
        {
            return new TextureResource(pack, logicalIndex, header, bitmapPart, size, null, [], e.Message);
        }
    }

    /// <summary>The tight bytes of one surface.</summary>
    public byte[] ReadLevel(ImgcLevel level)
    {
        if (BitmapPart is null || PayloadProblem is not null)
            throw new ImgcException(PayloadProblem ?? "this texture has no bitmap");
        return Pack.ReadPart(BitmapPart.Value, level.Offset, level.Size);
    }

    /// <summary>Decode one surface to BGRA32.</summary>
    public DecodedImage Decode(ImgcLevel level, bool rebuildNormalZ = true) =>
        TextureDecoder.Decode(Header.Format, ReadLevel(level), level.Width, level.Height, rebuildNormalZ);

    /// <summary>The largest surface of face 0 — what a preview shows by default.</summary>
    public ImgcLevel? TopLevel => Levels.Count == 0 ? null : Levels[0];
}
