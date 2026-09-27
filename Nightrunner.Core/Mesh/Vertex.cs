using System.Buffers.Binary;

namespace Nightrunner.Core.Mesh;

/// <summary>
/// Vertex formats 0/3/6/8: layout, float decode, qtangent maths and the exact re-encode. Port of
/// <c>mesh/vertex.py</c> (the requantising half of the encoder is not ported: see <see cref="Encode"/>).
/// </summary>
/// <remarks>
/// <code>
/// fmt 0 (16 B)  0x00 f16[3] pos | 0x06 u16 raw_06 (always 0x3C00) | 0x08 i8[4] qtan (÷127) | 0x0C f16[2] uv0
/// fmt 3 (32 B)  0x00 f32[3] pos | 0x0C i16[4] qtan (÷32767) | 0x14 f16[2] uv0 | 0x18 f16[2] uv1 | 0x1C u32 raw_tail
/// fmt 6 (40 B)  0x00 f32[3] pos | 0x0C u8[4] weights | 0x10 u8[4] joints | 0x14 i16[4] qtan | 0x1C f16[2] uv0 |
///               0x20 f16[2] uv1 | 0x24 u32 raw_tail
/// fmt 8 (80 B)  fmt 6 | 0x28 u8[40] raw_ext
/// </code>
/// QTangent: q = (x, y, z, w)/scale normalised; tangent = R·X, normal = R·Z. The bitangent sign is the sign of the
/// largest-|component| of the stored quad, ties z &gt; y &gt; x &gt; w (A: native DXBC eye pass).
/// </remarks>
public static class Vertex
{
    public const int BlockAlign = 160;     // census: vertex windows are zero-padded to a multiple of 160 bytes
    public const int IndexBaseAlign = 4;
    public const int IndexBufferAlign = 16;

    public static bool Supported(int fmt) => fmt is 0 or 3 or 6 or 8;

    /// <summary>A: ((format − 6) &amp; 0xFD) == 0 (ResourceManagement CreateSurface).</summary>
    public static bool IsSkinned(int fmt) => ((fmt - 6) & 0xFD) == 0;

    public static int Stride(int fmt) => fmt switch
    {
        0 => 16, 3 => 32, 6 => 40, 8 => 80,
        _ => throw new MeshUnsupportedException($"vertex format {fmt} is not one of 0/3/6/8"),
    };

    public static float QtanScale(int fmt) => fmt == 0 ? 127f : 32767f;

    /// <summary>Field offsets within one record (−1 = absent).</summary>
    internal readonly record struct Offsets(int Pos, int Qtan, int Uv0, int Uv1, int Weights, int Joints);

    internal static Offsets OffsetsOf(int fmt) => fmt switch
    {
        0 => new(0, 8, 12, -1, -1, -1),
        3 => new(0, 12, 20, 24, -1, -1),
        6 or 8 => new(0, 20, 28, 32, 12, 16),
        _ => throw new MeshUnsupportedException($"vertex format {fmt} is not one of 0/3/6/8"),
    };

    public static VertexData Decode(ReadOnlySpan<byte> buffer, long vertexBase, int count, int fmt)
    {
        int stride = Stride(fmt);
        long end = vertexBase + (long)count * stride;
        if (vertexBase < 0 || end > buffer.Length)
            throw new MeshFormatException($"vertex window [{vertexBase}, {end}) exceeds the vertex buffer ({buffer.Length} bytes)");
        return new VertexData(fmt, buffer.Slice((int)vertexBase, count * stride).ToArray());
    }

    // ---- qtangent --------------------------------------------------------------------------------------------

    /// <summary>+1/−1: sign of the largest |component|, ties z &gt; y &gt; x &gt; w.</summary>
    public static sbyte QtangentSign(int x, int y, int z, int w)
    {
        int ax = Math.Abs(x), ay = Math.Abs(y), az = Math.Abs(z), aw = Math.Abs(w);
        int m = Math.Max(Math.Max(ax, ay), Math.Max(az, aw));
        int picked = az == m ? z : ay == m ? y : ax == m ? x : w;
        return (sbyte)(picked >= 0 ? 1 : -1);
    }

    /// <summary>Stored quad → (tangent, normal), float32 maths as the prototype does it.</summary>
    public static void DecodeQtangent(int x, int y, int z, int w, float scale, Span<float> tangent, Span<float> normal)
    {
        float qx = x / scale, qy = y / scale, qz = z / scale, qw = w / scale;
        float n = MathF.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
        n = MathF.Max(n, 1e-12f);
        qx /= n; qy /= n; qz /= n; qw /= n;
        tangent[0] = 1 - 2 * (qy * qy + qz * qz);
        tangent[1] = 2 * (qx * qy + qw * qz);
        tangent[2] = 2 * (qx * qz - qw * qy);
        normal[0] = 2 * (qx * qz + qw * qy);
        normal[1] = 2 * (qy * qz - qw * qx);
        normal[2] = 1 - 2 * (qx * qx + qy * qy);
    }

    // ---- half floats, NaN payload kept -------------------------------------------------------------------------

    /// <summary>f16 bits → f32. NaN keeps its payload bit for bit (System.Half would set the quiet bit).</summary>
    public static float HalfToFloat(ushort h)
    {
        if ((h & 0x7C00) == 0x7C00 && (h & 0x03FF) != 0)
            return BitConverter.UInt32BitsToSingle(((uint)(h & 0x8000) << 16) | 0x7F800000u | ((uint)(h & 0x03FF) << 13));
        return (float)BitConverter.UInt16BitsToHalf(h);
    }

    /// <summary>f32 → f16 bits, round to nearest even; NaN payload truncated as numpy does (never becomes infinity).</summary>
    public static ushort FloatToHalf(float f)
    {
        if (float.IsNaN(f))
        {
            uint b = BitConverter.SingleToUInt32Bits(f);
            uint r = 0x7C00u + ((b & 0x007FFFFF) >> 13);
            if (r == 0x7C00u) r++;
            return (ushort)(((b >> 16) & 0x8000) | r);
        }
        return BitConverter.HalfToUInt16Bits((Half)f);
    }

    // ---- encode ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Float attributes → on-disk records of <c>v.Format</c>, in the prototype's <c>encode_block</c> order: positions
    /// and UVs from the floats, and for the quantised fields (qtangent, weights) and the raw holes (raw_06,
    /// raw_tail, raw_ext) the vertex's raw record wherever the float value still equals its decode.
    /// </summary>
    /// <remarks>
    /// Requantising an edited tangent frame or weight row is mesh-writing work (<c>encode_qtangent</c>,
    /// <c>quantize_weights</c>) and is not ported: such a vertex is refused, never approximated.
    /// </remarks>
    public static byte[] Encode(VertexData v)
    {
        int fmt = v.Format, stride = Stride(fmt), n = v.Count;
        var o = OffsetsOf(fmt);
        var raw = v.Raw;
        var outb = (byte[])raw.Clone();          // holes, joints-lane bytes and quads start as the raw record
        Span<float> t0 = stackalloc float[3], n0 = stackalloc float[3];
        float scale = QtanScale(fmt);
        for (int i = 0; i < n; i++)
        {
            var rec = outb.AsSpan(i * stride, stride);
            var src = raw.AsSpan(i * stride, stride);
            if (fmt == 0)
                for (int k = 0; k < 3; k++)
                    BinaryPrimitives.WriteUInt16LittleEndian(rec[(k * 2)..], FloatToHalf(v.Positions[i * 3 + k]));
            else
                for (int k = 0; k < 3; k++)
                    BinaryPrimitives.WriteSingleLittleEndian(rec[(k * 4)..], v.Positions[i * 3 + k]);
            for (int k = 0; k < 2; k++)
                BinaryPrimitives.WriteUInt16LittleEndian(rec[(o.Uv0 + k * 2)..], FloatToHalf(v.Uv0[i * 2 + k]));
            if (o.Uv1 >= 0)
            {
                if (v.Uv1 is null) throw new MeshFormatException($"format {fmt} needs uv1");
                for (int k = 0; k < 2; k++)
                    BinaryPrimitives.WriteUInt16LittleEndian(rec[(o.Uv1 + k * 2)..], FloatToHalf(v.Uv1[i * 2 + k]));
            }

            var (qx, qy, qz, qw) = ReadQuad(src, o.Qtan, fmt);
            DecodeQtangent(qx, qy, qz, qw, scale, t0, n0);
            bool frameSame = QtangentSign(qx, qy, qz, qw) == v.TangentSign[i];
            for (int k = 0; k < 3 && frameSame; k++)
                frameSame = t0[k] == v.Tangents[i * 3 + k] && n0[k] == v.Normals[i * 3 + k];
            if (!frameSame)
                throw new MeshUnsupportedException($"vertex {i}: tangent frame changed; requantising is not ported (mesh writing is out of scope)");

            if (o.Weights >= 0)
            {
                if (v.Weights is null || v.Joints is null) throw new MeshFormatException($"format {fmt} needs weights and joints");
                for (int k = 0; k < 4; k++)
                    if (src[o.Weights + k] / 255f != v.Weights[i * 4 + k])
                        throw new MeshUnsupportedException($"vertex {i}: weights changed; requantising is not ported (mesh writing is out of scope)");
                for (int k = 0; k < 4; k++) rec[o.Joints + k] = v.Joints[i * 4 + k];
            }
        }
        return outb;
    }

    internal static (int X, int Y, int Z, int W) ReadQuad(ReadOnlySpan<byte> rec, int at, int fmt)
    {
        if (fmt == 0) return ((sbyte)rec[at], (sbyte)rec[at + 1], (sbyte)rec[at + 2], (sbyte)rec[at + 3]);
        return (BinaryPrimitives.ReadInt16LittleEndian(rec[at..]), BinaryPrimitives.ReadInt16LittleEndian(rec[(at + 2)..]),
                BinaryPrimitives.ReadInt16LittleEndian(rec[(at + 4)..]), BinaryPrimitives.ReadInt16LittleEndian(rec[(at + 6)..]));
    }

    private static void WriteQuad(Span<byte> rec, int at, int fmt, (int X, int Y, int Z, int W) q)
    {
        if (fmt == 0)
        {
            rec[at] = (byte)(sbyte)q.X; rec[at + 1] = (byte)(sbyte)q.Y; rec[at + 2] = (byte)(sbyte)q.Z; rec[at + 3] = (byte)(sbyte)q.W;
            return;
        }
        BinaryPrimitives.WriteInt16LittleEndian(rec[at..], (short)q.X);
        BinaryPrimitives.WriteInt16LittleEndian(rec[(at + 2)..], (short)q.Y);
        BinaryPrimitives.WriteInt16LittleEndian(rec[(at + 4)..], (short)q.Z);
        BinaryPrimitives.WriteInt16LittleEndian(rec[(at + 6)..], (short)q.W);
    }

    public static long AlignTo(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

    // ---- mesh writing: requantising encode (encode_qtangent, quantize_weights, hole_defaults, encode_block) ----------

    /// <summary>Hole byte offsets: raw_06 (format 0), raw_tail (3/6/8), raw_ext (8); −1 = absent.</summary>
    internal static (int Raw06, int RawTail, int RawExt) HoleOffsets(int fmt) => fmt switch
    {
        0 => (6, -1, -1), 3 => (-1, 28, -1), 6 => (-1, 36, -1), 8 => (-1, 36, 40),
        _ => throw new MeshUnsupportedException($"vertex format {fmt} is not one of 0/3/6/8"),
    };

    public const int RawExtSize = 40;

    /// <summary>
    /// Rotation frames with columns [t | n×t | n] → unit quaternions (x, y, z, w), branching on the largest diagonal term.
    /// Port of <c>quaternion_from_frames</c> operation for operation in double (n kept, t projected and normalised).
    /// </summary>
    public static (double X, double Y, double Z, double W) QuaternionFromFrame(double tx, double ty, double tz, double nx, double ny, double nz)
    {
        double nl = Math.Max(Math.Sqrt(nx * nx + ny * ny + nz * nz), 1e-12);
        nx /= nl; ny /= nl; nz /= nl;
        double d = tx * nx + ty * ny + tz * nz;
        tx -= nx * d; ty -= ny * d; tz -= nz * d;
        double tl = Math.Max(Math.Sqrt(tx * tx + ty * ty + tz * tz), 1e-12);
        tx /= tl; ty /= tl; tz /= tl;
        double bx = ny * tz - nz * ty, by = nz * tx - nx * tz, bz = nx * ty - ny * tx;   // n × t (numpy cross order)
        double m00 = tx, m10 = ty, m20 = tz, m01 = bx, m11 = by, m21 = bz, m02 = nx, m12 = ny, m22 = nz;
        Span<double> c = [1 + m00 - m11 - m22, 1 - m00 + m11 - m22, 1 - m00 - m11 + m22, 1 + m00 + m11 + m22];
        int k = 0;                                   // np.argmax: first maximum, a NaN wins
        if (!double.IsNaN(c[0]))
            for (int i = 1; i < 4; i++)
            {
                if (double.IsNaN(c[i])) { k = i; break; }
                if (c[i] > c[k]) k = i;
            }
        double s = Math.Sqrt(Math.Max(c[k], 1e-30)) * 2;
        double qx, qy, qz, qw;
        switch (k)
        {
            case 0: qx = s / 4; qy = (m01 + m10) / s; qz = (m02 + m20) / s; qw = (m21 - m12) / s; break;
            case 1: qy = s / 4; qx = (m01 + m10) / s; qz = (m12 + m21) / s; qw = (m02 - m20) / s; break;
            case 2: qz = s / 4; qx = (m02 + m20) / s; qy = (m12 + m21) / s; qw = (m10 - m01) / s; break;
            default: qw = s / 4; qx = (m21 - m12) / s; qy = (m02 - m20) / s; qz = (m10 - m01) / s; break;
        }
        double ql = Math.Max(Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw), 1e-12);
        return (qx / ql, qy / ql, qz / ql, qw / ql);
    }

    /// <summary>
    /// (tangent, normal, handedness) → the stored quad of the format's integer type: quantised with round-half-even, and
    /// negated when the quad's own sign rule disagrees with <paramref name="sign"/> (q and −q are the same rotation).
    /// A degenerate (non-finite) frame packs to a zero quad, as the prototype's integer cast leaves it.
    /// </summary>
    public static (int X, int Y, int Z, int W) EncodeQtangent(float tx, float ty, float tz, float nx, float ny, float nz, int sign, int fmt)
    {
        double scale = QtanScale(fmt);
        var q = QuaternionFromFrame(tx, ty, tz, nx, ny, nz);
        int P(double v) => double.IsNaN(v) ? 0 : (int)Math.Round(Math.Clamp(v, -1.0, 1.0) * scale, MidpointRounding.ToEven);
        int x = P(q.X), y = P(q.Y), z = P(q.Z), w = P(q.W);
        if (QtangentSign(x, y, z, w) != sign) { x = -x; y = -y; z = -z; w = -w; }
        return (x, y, z, w);
    }

    /// <summary>
    /// One float weight row (any positive scale) → u8 lanes summing to exactly 255 (every shipped skinned row does):
    /// floor of the normalised × 255, the remainder handed to the largest fractions (stable order). A zero row stays zero.
    /// </summary>
    public static void QuantizeWeights(ReadOnlySpan<float> w, Span<byte> dst)
    {
        double w0 = w[0], w1 = w[1], w2 = w[2], w3 = w[3];
        double s = w0 + w1 + w2 + w3;
        Span<double> frac = stackalloc double[4];
        Span<long> bse = stackalloc long[4];
        bool ok = s > 0;
        long sum = 0;
        for (int k = 0; k < 4; k++)
        {
            double scaled = (ok ? w[k] / s : 0.0) * 255.0;
            bse[k] = (long)Math.Floor(scaled);
            frac[k] = scaled - bse[k];
            sum += bse[k];
        }
        long remainder = ok ? 255 - sum : 0;
        Span<int> order = [0, 1, 2, 3];               // argsort(-frac, stable)
        for (int i = 1; i < 4; i++)
            for (int j = i; j > 0 && -frac[order[j]] < -frac[order[j - 1]]; j--) (order[j], order[j - 1]) = (order[j - 1], order[j]);
        for (int rank = 0; rank < 4; rank++)
            dst[order[rank]] = (byte)(bse[order[rank]] + (rank < remainder ? 1 : 0));
    }

    /// <summary>
    /// Raw-hole values for NEW vertices: raw_06 = 0x3C00 (every format-0 entry holds only that); raw_tail = the most common
    /// raw_tail of the entry's original window (the smallest on a tie; 0 without one); raw_ext = 40 zero bytes.
    /// </summary>
    public static VertexHoles HoleDefaults(int fmt, byte[]? window)
    {
        var (_, tailAt, _) = HoleOffsets(fmt);
        uint tail = 0;
        int stride = Stride(fmt);
        if (tailAt >= 0 && window is { Length: > 0 })
        {
            var counts = new Dictionary<uint, int>();
            for (int i = 0; i < window.Length / stride; i++)
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(window.AsSpan(i * stride + tailAt));
                counts[v] = counts.GetValueOrDefault(v) + 1;
            }
            int best = -1;
            foreach (var (v, c) in counts.OrderBy(kv => kv.Key))
                if (c > best) { best = c; tail = v; }
        }
        return new VertexHoles(0x3C00, tail, new byte[RawExtSize]);
    }

    /// <summary>
    /// Float attributes → on-disk records (port of <c>encode_block</c>). Positions and UVs come from the floats. For the
    /// quantised fields a vertex with a raw record (<paramref name="rawIds"/>[i] ≥ 0, indexing <paramref name="raw"/>)
    /// keeps the raw quad while its decoded frame still equals the floats, and the raw weights while raw/255 still equal
    /// them; otherwise the frame is re-quantised (<see cref="EncodeQtangent"/>) and the row re-quantised to sum 255. Raw
    /// holes come from the raw record, or from <paramref name="holes"/> for a vertex without one.
    /// </summary>
    public static byte[] EncodeBlock(VertexFloats v, byte[]? raw = null, long[]? rawIds = null, VertexHoles? holes = null)
    {
        int fmt = v.Format, stride = Stride(fmt), n = v.Count;
        var o = OffsetsOf(fmt);
        var (h06, hTail, hExt) = HoleOffsets(fmt);
        var outb = new byte[n * stride];
        if (rawIds is null)
        {
            rawIds = new long[n];
            bool identity = raw is not null && raw.Length / stride == n;
            for (int i = 0; i < n; i++) rawIds[i] = identity ? i : -1;
        }
        if (rawIds.Length != n) throw new MeshFormatException($"raw ids: {rawIds.Length} for {n} vertices");
        holes ??= HoleDefaults(fmt, raw);
        float scale = QtanScale(fmt);
        Span<float> t0 = stackalloc float[3], n0 = stackalloc float[3];
        Span<byte> wq = stackalloc byte[4];
        for (int i = 0; i < n; i++)
        {
            var rec = outb.AsSpan(i * stride, stride);
            long id = rawIds[i];
            var src = id >= 0 && raw is not null ? raw.AsSpan(checked((int)id * stride), stride) : default;
            bool has = id >= 0 && raw is not null;
            if (fmt == 0)
                for (int k = 0; k < 3; k++) BinaryPrimitives.WriteUInt16LittleEndian(rec[(k * 2)..], FloatToHalf(v.Positions[i * 3 + k]));
            else
                for (int k = 0; k < 3; k++) BinaryPrimitives.WriteSingleLittleEndian(rec[(k * 4)..], v.Positions[i * 3 + k]);
            for (int k = 0; k < 2; k++) BinaryPrimitives.WriteUInt16LittleEndian(rec[(o.Uv0 + k * 2)..], FloatToHalf(v.Uv0[i * 2 + k]));
            if (o.Uv1 >= 0)
            {
                if (v.Uv1 is null) throw new MeshFormatException($"format {fmt} needs uv1");
                for (int k = 0; k < 2; k++) BinaryPrimitives.WriteUInt16LittleEndian(rec[(o.Uv1 + k * 2)..], FloatToHalf(v.Uv1[i * 2 + k]));
            }

            bool keepQuad = false;
            if (has)
            {
                var q0 = ReadQuad(src, o.Qtan, fmt);
                DecodeQtangent(q0.X, q0.Y, q0.Z, q0.W, scale, t0, n0);
                keepQuad = QtangentSign(q0.X, q0.Y, q0.Z, q0.W) == v.TangentSign[i];
                for (int k = 0; k < 3 && keepQuad; k++)
                    keepQuad = t0[k] == v.Tangents[i * 3 + k] && n0[k] == v.Normals[i * 3 + k];
                if (keepQuad) src.Slice(o.Qtan, fmt == 0 ? 4 : 8).CopyTo(rec[o.Qtan..]);
            }
            if (!keepQuad)
                WriteQuad(rec, o.Qtan, fmt, EncodeQtangent(v.Tangents[i * 3], v.Tangents[i * 3 + 1], v.Tangents[i * 3 + 2],
                                                           v.Normals[i * 3], v.Normals[i * 3 + 1], v.Normals[i * 3 + 2], v.TangentSign[i], fmt));

            if (o.Weights >= 0)
            {
                if (v.Weights is null || v.Joints is null) throw new MeshFormatException($"format {fmt} needs weights and joints");
                var wrow = v.Weights.AsSpan(i * 4, 4);
                bool keepW = has;
                for (int k = 0; k < 4 && keepW; k++) keepW = src[o.Weights + k] / 255f == wrow[k];
                if (keepW) src.Slice(o.Weights, 4).CopyTo(rec[o.Weights..]);
                else
                {
                    QuantizeWeights(wrow, wq);
                    wq.CopyTo(rec[o.Weights..]);
                }
                v.Joints.AsSpan(i * 4, 4).CopyTo(rec[o.Joints..]);
            }

            if (h06 >= 0)
            {
                if (has) src.Slice(h06, 2).CopyTo(rec[h06..]);
                else BinaryPrimitives.WriteUInt16LittleEndian(rec[h06..], holes.Raw06);
            }
            if (hTail >= 0)
            {
                if (has) src.Slice(hTail, 4).CopyTo(rec[hTail..]);
                else BinaryPrimitives.WriteUInt32LittleEndian(rec[hTail..], holes.RawTail);
            }
            if (hExt >= 0)
            {
                if (has) src.Slice(hExt, RawExtSize).CopyTo(rec[hExt..]);
                else holes.RawExt.CopyTo(rec[hExt..]);
            }
        }
        return outb;
    }

    /// <summary>
    /// Per-vertex tangent + handedness from UV derivatives (port of <c>tangents_from_uv</c>, the standard per-triangle
    /// accumulation, in double and in the prototype's order): policy for scenes without a tangent buffer (the official
    /// Blender exporter writes none). A vertex without a usable triangle gets an arbitrary perpendicular of its normal
    /// and sign +1.
    /// </summary>
    public static (float[] Tangents, sbyte[] Sign) TangentsFromUv(float[] positions, float[] normals, float[] uv, long[] faces)
    {
        int n = positions.Length / 3, m = faces.Length / 3;
        var tan = new double[n * 3];
        var bit = new double[n * 3];
        if (m > 0)
        {
            var sdir = new double[m * 3];
            var tdir = new double[m * 3];
            for (int f = 0; f < m; f++)
            {
                long a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
                double d1x = (double)uv[b * 2] - uv[a * 2], d1y = (double)uv[b * 2 + 1] - uv[a * 2 + 1];
                double d2x = (double)uv[c * 2] - uv[a * 2], d2y = (double)uv[c * 2 + 1] - uv[a * 2 + 1];
                double det = d1x * d2y - d2x * d1y;
                double r = Math.Abs(det) > 1e-20 ? 1.0 / det : 0.0;
                for (int k = 0; k < 3; k++)
                {
                    double e1 = (double)positions[b * 3 + k] - positions[a * 3 + k];
                    double e2 = (double)positions[c * 3 + k] - positions[a * 3 + k];
                    sdir[f * 3 + k] = (e1 * d2y - e2 * d1y) * r;
                    tdir[f * 3 + k] = (e2 * d1x - e1 * d2x) * r;
                }
            }
            for (int corner = 0; corner < 3; corner++)          // np.add.at, one corner column after the other
                for (int f = 0; f < m; f++)
                {
                    long v = faces[f * 3 + corner];
                    for (int k = 0; k < 3; k++)
                    {
                        tan[v * 3 + k] += sdir[f * 3 + k];
                        bit[v * 3 + k] += tdir[f * 3 + k];
                    }
                }
        }
        var outT = new float[n * 3];
        var sign = new sbyte[n];
        for (int i = 0; i < n; i++)
        {
            double nx = normals[i * 3], ny = normals[i * 3 + 1], nz = normals[i * 3 + 2];
            double nl = Math.Max(Math.Sqrt(nx * nx + ny * ny + nz * nz), 1e-12);
            nx /= nl; ny /= nl; nz /= nl;
            double ax = tan[i * 3], ay = tan[i * 3 + 1], az = tan[i * 3 + 2];
            double d = ax * nx + ay * ny + az * nz;
            double tx = ax - nx * d, ty = ay - ny * d, tz = az - nz * d;
            double ln = Math.Sqrt(tx * tx + ty * ty + tz * tz);
            if (ln < 1e-12)
            {
                // arbitrary perpendicular: cross with the axis least aligned with the normal (np.argmin: first minimum)
                double[] abs = [Math.Abs(nx), Math.Abs(ny), Math.Abs(nz)];
                int axis = 0;
                if (!double.IsNaN(abs[0]))
                    for (int k = 1; k < 3; k++)
                    {
                        if (double.IsNaN(abs[k])) { axis = k; break; }
                        if (abs[k] < abs[axis]) axis = k;
                    }
                double ex = axis == 0 ? 1 : 0, ey = axis == 1 ? 1 : 0, ez = axis == 2 ? 1 : 0;
                tx = ny * ez - nz * ey; ty = nz * ex - nx * ez; tz = nx * ey - ny * ex;
                ln = Math.Sqrt(tx * tx + ty * ty + tz * tz);
            }
            ln = Math.Max(ln, 1e-12);
            tx /= ln; ty /= ln; tz /= ln;
            double cx = ny * tz - nz * ty, cy = nz * tx - nx * tz, cz = nx * ty - ny * tx;
            double dot = cx * bit[i * 3] + cy * bit[i * 3 + 1] + cz * bit[i * 3 + 2];
            sign[i] = (sbyte)(dot < 0 ? -1 : 1);
            outT[i * 3] = (float)tx; outT[i * 3 + 1] = (float)ty; outT[i * 3 + 2] = (float)tz;
        }
        return (outT, sign);
    }
}

/// <summary>Raw-hole values for vertices without a raw record (<see cref="Vertex.HoleDefaults"/>).</summary>
public sealed record VertexHoles(ushort Raw06, uint RawTail, byte[] RawExt);

/// <summary>
/// A float vertex set without raw records — the encoder input of mesh writing (the prototype's
/// <c>DecodedVertices.from_floats</c>). Flat arrays as in <see cref="VertexData"/>.
/// </summary>
public sealed class VertexFloats
{
    public int Format { get; }
    public int Count { get; }
    public float[] Positions { get; }
    public float[] Uv0 { get; }
    public float[]? Uv1 { get; }
    public float[] Normals { get; }
    public float[] Tangents { get; }
    public sbyte[] TangentSign { get; }
    public float[]? Weights { get; }
    public byte[]? Joints { get; set; }

    public VertexFloats(int fmt, float[] positions, float[] uv0, float[]? uv1, float[] normals, float[] tangents, sbyte[] tangentSign,
                        float[]? weights = null, byte[]? joints = null)
    {
        if (!Vertex.Supported(fmt)) throw new MeshUnsupportedException($"vertex format {fmt} is not one of 0/3/6/8");
        Format = fmt;
        Positions = positions;
        Count = positions.Length / 3;
        Uv0 = uv0; Uv1 = uv1; Normals = normals; Tangents = tangents; TangentSign = tangentSign;
        if (Vertex.IsSkinned(fmt))
        {
            if (weights is null || joints is null) throw new MeshFormatException($"format {fmt} needs weights and joints");
            Weights = weights;
            Joints = joints;
        }
        (string, int, int)[] check =
        [
            ("uv0", uv0.Length, 2), ("normals", normals.Length, 3), ("tangents", tangents.Length, 3), ("tangent_sign", tangentSign.Length, 1),
            ("uv1", uv1?.Length ?? -1, 2), ("weights", Weights?.Length ?? -1, 4), ("joints", Joints?.Length ?? -1, 4),
        ];
        foreach (var (name, len, w) in check)
            if (len >= 0 && len != Count * w) throw new MeshFormatException($"{name}: {len / w} rows for {Count} vertices");
    }

    /// <summary>A mutable copy of a decoded window's floats.</summary>
    public static VertexFloats Of(VertexData v) => new(v.Format, (float[])v.Positions.Clone(), (float[])v.Uv0.Clone(), (float[]?)v.Uv1?.Clone(),
        (float[])v.Normals.Clone(), (float[])v.Tangents.Clone(), (sbyte[])v.TangentSign.Clone(), (float[]?)v.Weights?.Clone(),
        (byte[]?)v.Joints?.Clone());
}

/// <summary>
/// Float view of one vertex window plus the exact raw records it came from (<see cref="Raw"/>, never modified).
/// Arrays are flat: positions/normals/tangents 3 per vertex, UVs 2, weights/joints 4.
/// </summary>
public sealed class VertexData
{
    public int Format { get; }
    public int Count { get; }
    public byte[] Raw { get; }
    public float[] Positions { get; }
    public float[] Uv0 { get; }
    public float[]? Uv1 { get; }
    public float[] Normals { get; }
    public float[] Tangents { get; }
    public sbyte[] TangentSign { get; }
    public float[]? Weights { get; }
    public byte[]? Joints { get; }

    public bool Skinned => Vertex.IsSkinned(Format);

    public VertexData(int fmt, byte[] raw)
    {
        int stride = Vertex.Stride(fmt);
        if (raw.Length % stride != 0) throw new MeshFormatException($"vertex window of {raw.Length} bytes is not a multiple of {stride}");
        Format = fmt;
        Raw = raw;
        int n = Count = raw.Length / stride;
        var o = Vertex.OffsetsOf(fmt);
        Positions = new float[n * 3];
        Uv0 = new float[n * 2];
        Uv1 = o.Uv1 >= 0 ? new float[n * 2] : null;
        Normals = new float[n * 3];
        Tangents = new float[n * 3];
        TangentSign = new sbyte[n];
        bool skinned = Vertex.IsSkinned(fmt);
        Weights = skinned ? new float[n * 4] : null;
        Joints = skinned ? new byte[n * 4] : null;
        float scale = Vertex.QtanScale(fmt);
        for (int i = 0; i < n; i++)
        {
            var rec = raw.AsSpan(i * stride, stride);
            for (int k = 0; k < 3; k++)
                Positions[i * 3 + k] = fmt == 0
                    ? Vertex.HalfToFloat(BinaryPrimitives.ReadUInt16LittleEndian(rec[(k * 2)..]))
                    : BinaryPrimitives.ReadSingleLittleEndian(rec[(k * 4)..]);
            for (int k = 0; k < 2; k++)
                Uv0[i * 2 + k] = Vertex.HalfToFloat(BinaryPrimitives.ReadUInt16LittleEndian(rec[(o.Uv0 + k * 2)..]));
            if (Uv1 is not null)
                for (int k = 0; k < 2; k++)
                    Uv1[i * 2 + k] = Vertex.HalfToFloat(BinaryPrimitives.ReadUInt16LittleEndian(rec[(o.Uv1 + k * 2)..]));
            var (qx, qy, qz, qw) = Vertex.ReadQuad(rec, o.Qtan, fmt);
            Vertex.DecodeQtangent(qx, qy, qz, qw, scale, Tangents.AsSpan(i * 3, 3), Normals.AsSpan(i * 3, 3));
            TangentSign[i] = Vertex.QtangentSign(qx, qy, qz, qw);
            if (skinned)
                for (int k = 0; k < 4; k++)
                {
                    Weights![i * 4 + k] = rec[o.Weights + k] / 255f;
                    Joints![i * 4 + k] = rec[o.Joints + k];
                }
        }
    }

    /// <summary>Sum of the raw u8 weights of vertex i (255 on every shipped skinned row).</summary>
    public int WeightSum(int i)
    {
        var o = Vertex.OffsetsOf(Format);
        int stride = Vertex.Stride(Format), s = 0;
        for (int k = 0; k < 4; k++) s += Raw[i * stride + o.Weights + k];
        return s;
    }
}
