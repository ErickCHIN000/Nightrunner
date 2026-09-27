using System.Text;
using Nightrunner.Core.Mesh.ClassReader;

namespace Nightrunner.Core.Mesh;

/// <summary>
/// Embedded <c>.msh</c> identity: read, verify, rename. Port of <c>mesh/identity.py</c>. MeshMgr registers a mesh under the
/// string at root+0x00 of the image, not under the pack's logical name (A: ResourceManagement GetFileName / Create), so a
/// duplicated or renamed mesh must carry <c>&lt;logical name&gt;.msh</c> there, exact case.
/// </summary>
public static class MeshIdentity
{
    private static readonly byte[] Suffix = ".msh"u8.ToArray();

    /// <summary>
    /// <c>&lt;logical&gt;.msh</c>; a logical name that already ends in <c>.msh</c> (any ASCII case) is kept as it is.
    /// </summary>
    public static byte[] ExpectedEmbeddedName(byte[] logicalName)
    {
        bool has = logicalName.Length >= Suffix.Length;
        for (int i = 0; has && i < Suffix.Length; i++)
        {
            byte c = logicalName[logicalName.Length - Suffix.Length + i];
            if (c is >= (byte)'A' and <= (byte)'Z') c += 32;
            has = c == Suffix[i];
        }
        return has ? (byte[])logicalName.Clone() : [.. logicalName, .. Suffix];
    }

    public static byte[] ExpectedEmbeddedName(string logicalName) => ExpectedEmbeddedName(Encoding.UTF8.GetBytes(logicalName));

    /// <summary>(ok, embedded, expected) for a mesh's parts under a logical name.</summary>
    public static (bool Ok, byte[] Embedded, byte[] Expected) Verify(byte[] image, byte[] fixups, string logicalName)
    {
        var emb = Image.EmbeddedName(image, fixups);
        var exp = ExpectedEmbeddedName(logicalName);
        return (emb.AsSpan().SequenceEqual(exp), emb, exp);
    }

    /// <summary>
    /// Rewrites the embedded name to <c>&lt;logical&gt;.msh</c> by appending a new string at the end of the primary image
    /// and retargeting the root's name slot (shared string storage is never overwritten; every other byte is kept).
    /// Returns the parts unchanged when the name already matches.
    /// </summary>
    public static (byte[] Image, byte[] Fixups, bool Changed) Rename(byte[] image, byte[] fixups, string logicalName)
    {
        var exp = ExpectedEmbeddedName(logicalName);
        if (exp.Length == 0 || exp.AsSpan().IndexOf((byte)0) >= 0 || exp.Length > 65535)
            throw new MeshFormatException("invalid embedded mesh name");
        var before = Image.EmbeddedName(image, fixups);
        if (before.AsSpan().SequenceEqual(exp)) return (image, fixups, false);
        var img = new Image(image, Fixups.Parse(fixups));
        var patch = new ImagePatch(img);
        int off = patch.AppendString(exp);
        patch.Retarget((int)img.Records[0].Offset, off);
        var (ni, nf) = patch.Finish();
        if (!Image.EmbeddedName(ni, nf).AsSpan().SequenceEqual(exp)) throw new MeshFormatException("embedded name rename verification failed");
        return (ni, nf, true);
    }
}
