using Nightrunner.Core.Mesh;
using Nightrunner.Core.Mesh.ClassReader;

namespace Nightrunner.Core.Anim;

/// <summary>
/// AnimCustomResource (resource type 0x49, parts 0x49 0x4A 0x49 0x4A): a baked plugin resource. Only the header pair
/// is decoded — <c>CAnimCustomResourceBank {string m_BankName, m_BakerDll, m_BakerFunc; u64 bakerVersion}</c>, one
/// class-4 record at image offset 0 with three packed strings and the version (0x180cf2d10). The body pair is kept
/// as bytes and never interpreted.
/// </summary>
public sealed class AnimCustomResource
{
    public const byte PartImage = 0x49, PartFixups = 0x4A;
    public const uint ClassHeader = 0x04;

    public Image Header { get; }
    public byte[] BodyImage { get; }
    public byte[] BodyFixups { get; }

    public string BankName { get; }
    public string BakerDll { get; }
    public string BakerFunc { get; }
    public ulong BakerVersion { get; }

    private AnimCustomResource(Image header, byte[] bodyImage, byte[] bodyFixups)
    {
        Header = header;
        BodyImage = bodyImage;
        BodyFixups = bodyFixups;
        var recs = header.Records;
        if (recs.Count == 0 || recs[0].ClassId != ClassHeader || recs[0].Offset != 0 || recs[0].Secondary)
            throw new AnimBankFormatException(
                $"custom resource header: record 0 is {(recs.Count == 0 ? "missing" : $"class 0x{recs[0].ClassId:X2} at 0x{recs[0].Offset:X}")}" +
                $" (expected class 0x{ClassHeader:X2} at 0)");
        BankName = AnimGraphBank.PackedString(header, 0x00) ?? "";
        BakerDll = AnimGraphBank.PackedString(header, 0x08) ?? "";
        BakerFunc = AnimGraphBank.PackedString(header, 0x10) ?? "";
        BakerVersion = header.U64(0x18);
    }

    /// <summary>The four parts in resource order: header image, header fixups, body image, body fixups.</summary>
    public static AnimCustomResource Parse(byte[] headerImage, ReadOnlySpan<byte> headerFixups, byte[] bodyImage, byte[] bodyFixups)
    {
        try
        {
            return new AnimCustomResource(Image.FromParts(headerImage, headerFixups), bodyImage, bodyFixups);
        }
        catch (Exception e) when (e is MeshFormatException or MeshUnsupportedException)
        {
            throw new AnimBankFormatException($"custom resource header: {e.Message}", e);
        }
    }

    /// <summary>The header fixups re-serialised (the header image and the body pair are kept as given).</summary>
    public byte[] HeaderFixupsBytes() => Header.Fixups.ToBytes();

    public override string ToString() => $"{BankName} ({BakerDll}!{BakerFunc} v{BakerVersion})";
}
