using System.Buffers.Binary;

namespace Nightrunner.Core.Mesh;

/// <summary>
/// Model → vertex (0xF0) / index (0xF1) / fixups (0x11) bytes in the ORIGINAL layout: every entry keeps its bases and
/// the buffer sizes; bytes outside every entry window come from the source buffer. Port of the in-memory half of
/// <c>mesh/encode.py</c> — the corpus gate is that this reproduces every shipped part byte for byte. New layouts
/// (<c>plan_new_layout</c>) belong to mesh writing and are not ported.
/// </summary>
public static class MeshEncoder
{
    public static byte[] EncodeVertexBuffer(MeshModel model)
    {
        var src = model.VertexBuffer ?? [];
        var outb = (byte[])src.Clone();
        foreach (var e in model.GeometryEntries)
        {
            if (e.Vertices is null) continue;
            var data = Vertex.Encode(e.Vertices);
            long end = e.VertexBase + data.Length;
            if (end > outb.Length)
                throw new MeshFormatException($"entry {e.Index}: window [{e.VertexBase}, {end}) exceeds the buffer ({outb.Length})");
            data.CopyTo(outb, e.VertexBase);
        }
        return outb;
    }

    public static byte[] EncodeIndexBuffer(MeshModel model)
    {
        var src = model.IndexBuffer ?? [];
        var outb = (byte[])src.Clone();
        foreach (var e in model.GeometryEntries)
            foreach (var s in e.Submeshes)
            {
                if (s.Indices.Length != s.IndexCount)
                    throw new MeshFormatException($"entry {e.Index} submesh {s.Index}: {s.Indices.Length} indices != declared {s.IndexCount}");
                long end = s.IndexBase + s.IndexCount * 2L;
                if (end > outb.Length)
                    throw new MeshFormatException($"entry {e.Index} submesh {s.Index}: indices exceed the buffer ({outb.Length})");
                var dst = outb.AsSpan((int)s.IndexBase, s.IndexCount * 2);
                for (int i = 0; i < s.Indices.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(dst[(i * 2)..], s.Indices[i]);
            }
        return outb;
    }

    /// <summary>Regenerated parts by type, for every part the model has (fixups always; vertex/index when present).</summary>
    public static Dictionary<byte, byte[]> Reencode(MeshModel model)
    {
        var d = new Dictionary<byte, byte[]> { [MeshDecoder.PartFixups] = model.Fixups.ToBytes() };
        if (model.VertexBuffer is not null) d[MeshDecoder.PartVertex] = EncodeVertexBuffer(model);
        if (model.IndexBuffer is not null) d[MeshDecoder.PartIndex] = EncodeIndexBuffer(model);
        return d;
    }
}
