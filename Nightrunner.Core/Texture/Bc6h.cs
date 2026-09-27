using System.Buffers.Binary;

namespace Nightrunner.Core.Texture;

/// <summary>
/// BC6H (UF16 and SF16) block encoder, written here because BCnEncoder.Net's one wraps endpoint deltas: measured on
/// shipped probes it turns up to a whole block into 65,504 (the half maximum), whatever the quality setting.
/// </summary>
/// <remarks>
/// <para>Only the four one-region modes are written — 11 (10-bit endpoints), 12 (11-bit base + 9-bit delta),
/// 13 (12 + 8) and 14 (16 + 4). They are the only modes in the shipped corpus (every mip-0 block of all 1,874
/// BC6H textures of both games), so the stock data is exactly representable by what this writes. Two-region modes
/// (1-10) are neither written nor decoded here; the library decodes them for display.</para>
/// <para>Per block and mode: nine starts on the pixels' principal axis (the extreme pixels taken as palette entries
/// 0-2 and 13-15, since a decoded block rarely uses both ends), least-squares refinement on the chosen indices, then
/// a local search of ±1 moves per channel (either end, both together, both apart). Error is the squared difference of the decoded half-float
/// bit patterns (roughly relative error), measured with the decoder's own integer maths, so what is chosen is what
/// a GPU returns. A delta is kept within its signed range (symmetric, so swapping endpoints for the anchor bit
/// never overflows) — a delta that wraps is the library's failure, not a trade-off.</para>
/// <para>UF16 takes [0, 65504]: negatives and NaN become 0, larger values and +inf 65504. SF16 takes ±65504.</para>
/// </remarks>
public static class Bc6h
{
    private static readonly int[] Weights = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    /// <summary>A one-region mode: its 5-bit code, endpoint precision and delta precision (0 = stored whole).</summary>
    public readonly record struct Mode(int Code, int Bits, int Delta)
    {
        public int XBits => Delta == 0 ? Bits : Delta;
        public int MaxDelta => Delta == 0 ? int.MaxValue : (1 << (Delta - 1)) - 1;
        public override string ToString() => $"0x{Code:X2} ({Bits}{(Delta > 0 ? "." + Delta : "")})";
    }

    public static readonly Mode[] OneRegion = [new(0x03, 10, 0), new(0x07, 11, 9), new(0x0B, 12, 8), new(0x0F, 16, 4)];

    /// <summary>Header bit order after the 5 mode bits: (field, bit), field 0-2 = base R/G/B, 3-5 = second R/G/B.</summary>
    private static readonly (byte Field, byte Bit)[][] Fields = OneRegion.Select(BuildFields).ToArray();

    private static (byte, byte)[] BuildFields(Mode m)
    {
        var list = new List<(byte, byte)>(60);
        for (byte c = 0; c < 3; c++)
            for (byte b = 0; b < 10; b++) list.Add((c, b));
        for (byte c = 0; c < 3; c++)
        {
            for (byte b = 0; b < m.XBits; b++) list.Add(((byte)(3 + c), b));
            for (int b = m.Bits - 1; b >= 10; b--) list.Add((c, (byte)b));      // high base bits, reversed
        }
        return [.. list];
    }

    // ---- decoder maths (the D3D11 spec, integer exact) -------------------------------------------------------

    internal static int Unquantize(int comp, int bits, bool signed)
    {
        if (!signed)
        {
            if (bits >= 15) return comp;
            if (comp == 0) return 0;
            if (comp == (1 << bits) - 1) return 0xFFFF;
            return ((comp << 16) + 0x8000) >> bits;
        }
        if (bits >= 16) return comp;
        bool negative = comp < 0;
        if (negative) comp = -comp;
        int u = comp == 0 ? 0 : comp >= (1 << (bits - 1)) - 1 ? 0x7FFF : ((comp << 15) + 0x4000) >> (bits - 1);
        return negative ? -u : u;
    }

    /// <summary>Interpolated 16-bit value → the half-float bit pattern, as a signed int (−0x7BFF..0x7BFF).</summary>
    internal static int Finish(int u, bool signed) =>
        !signed ? (u * 31) >> 6 : u < 0 ? -(((-u) * 31) >> 5) : (u * 31) >> 5;

    private static int Interpolate(int e0, int e1, int weight) => (e0 * (64 - weight) + e1 * weight + 32) >> 6;

    private static int SignExtend(int v, int bits) => (v << (32 - bits)) >> (32 - bits);

    /// <summary>The mode of a block: its index in <see cref="OneRegion"/>, or -1 for a two-region/reserved mode.</summary>
    public static int ModeOf(ReadOnlySpan<byte> block)
    {
        if ((block[0] & 3) < 2) return -1;
        int code = block[0] & 31;
        for (int i = 0; i < OneRegion.Length; i++)
            if (OneRegion[i].Code == code) return i;
        return -1;
    }

    /// <summary>
    /// Decode a one-region block to half-float bit patterns (16 pixels × RGB). False for the modes this does not
    /// write — the caller decodes those with the library.
    /// </summary>
    public static bool TryDecodeBlock(ReadOnlySpan<byte> block, bool signed, Span<ushort> rgbHalf)
    {
        int mi = ModeOf(block);
        if (mi < 0) return false;
        var m = OneRegion[mi];
        UInt128 v = BinaryPrimitives.ReadUInt128LittleEndian(block);
        Span<int> w = stackalloc int[3], x = stackalloc int[3], e0 = stackalloc int[3], e1 = stackalloc int[3];
        int pos = 5;
        foreach (var (field, bit) in Fields[mi])
        {
            int b = (int)(v >> pos++) & 1;
            if (field < 3) w[field] |= b << bit;
            else x[field - 3] |= b << bit;
        }
        int mask = (int)((1L << m.Bits) - 1);
        for (int c = 0; c < 3; c++)
        {
            int a = w[c], b = x[c];
            if (m.Delta > 0)
            {
                b = (a + SignExtend(b, m.Delta)) & mask;
                if (signed) b = SignExtend(b, m.Bits);
            }
            else if (signed) b = SignExtend(b, m.Bits);
            if (signed) a = SignExtend(a, m.Bits);
            e0[c] = Unquantize(a, m.Bits, signed);
            e1[c] = Unquantize(b, m.Bits, signed);
        }
        pos = 65;
        for (int p = 0; p < 16; p++)
        {
            int n = p == 0 ? 3 : 4;
            int k = (int)(v >> pos) & ((1 << n) - 1);
            pos += n;
            for (int c = 0; c < 3; c++)
            {
                int f = Finish(Interpolate(e0[c], e1[c], Weights[k]), signed);
                rgbHalf[p * 3 + c] = (ushort)(f < 0 ? 0x8000 | -f : f);
            }
        }
        return true;
    }

    // ---- encoder ---------------------------------------------------------------------------------------------

    /// <summary>Encode one surface; edge blocks repeat the last row/column. Alpha is ignored.</summary>
    public static byte[] Encode(FloatImage image, bool signed)
    {
        int width = image.Width, height = image.Height;
        int bw = (width + 3) / 4, bh = (height + 3) / 4;
        var dst = new byte[bw * bh * 16];
        var src = image.Rgba;
        Parallel.For(0, bh, by =>
        {
            var enc = new BlockEncoder(signed);
            for (int bx = 0; bx < bw; bx++)
            {
                for (int y = 0; y < 4; y++)
                {
                    int sy = Math.Min(by * 4 + y, height - 1);
                    for (int x = 0; x < 4; x++)
                    {
                        int sx = Math.Min(bx * 4 + x, width - 1);
                        int at = (sy * width + sx) * 4, p = (y * 4 + x) * 3;
                        for (int c = 0; c < 3; c++) enc.Target[p + c] = ToHalfBits(src[at + c], signed);
                    }
                }
                enc.Encode(dst.AsSpan((by * bw + bx) * 16, 16));
            }
        });
        return dst;
    }

    /// <summary>A float as the signed half bit pattern the encoder aims for (magnitude ≤ 0x7BFF).</summary>
    internal static int ToHalfBits(float v, bool signed)
    {
        if (float.IsNaN(v)) return 0;
        if (!signed && v <= 0) return 0;
        float a = Math.Abs(v);
        int m = a >= 65504f ? 0x7BFF : BitConverter.HalfToUInt16Bits((Half)a) & 0x7FFF;
        return v < 0 ? -m : m;
    }

    /// <summary>How many channel values a surface loses to the format's range (NaN, ±inf, beyond ±65504, UF16 negatives).</summary>
    public static long OutOfRange(FloatImage image, bool signed)
    {
        long n = 0;
        var s = image.Rgba;
        for (int i = 0; i < s.Length; i += 4)
            for (int c = 0; c < 3; c++)
            {
                float v = s[i + c];
                if (float.IsNaN(v) || Math.Abs(v) > 65504f || (!signed && v < 0)) n++;
            }
        return n;
    }

    /// <summary>One block's worth of state, reused along a row.</summary>
    private sealed class BlockEncoder(bool signed)
    {
        public readonly int[] Target = new int[48];
        private readonly double[] _u = new double[48];
        private readonly int[] _palette = new int[48];
        private readonly Candidate _best = new(), _modeBest = new(), _trial = new();

        private sealed class Candidate
        {
            public long Error = long.MaxValue;
            public readonly int[] Q0 = new int[3], Q1 = new int[3];
            public readonly byte[] Index = new byte[16];

            public void CopyFrom(Candidate o)
            {
                Error = o.Error;
                o.Q0.CopyTo(Q0, 0);
                o.Q1.CopyTo(Q1, 0);
                o.Index.CopyTo(Index, 0);
            }
        }

        /// <summary>
        /// Local search moves on one channel's quantised endpoints: either end alone, both together (keeps a delta
        /// that is at its limit), and both apart/together (range).
        /// </summary>
        private static readonly (int D0, int D1)[] Moves = [(-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (1, 1), (-1, 1), (1, -1)];

        private const int SearchPasses = 8;

        public void Encode(Span<byte> dst)
        {
            double scale = signed ? 32.0 / 31.0 : 64.0 / 31.0;
            for (int i = 0; i < 48; i++) _u[i] = Target[i] * scale;

            _best.Error = long.MaxValue;
            int bestMode = 0;
            Span<double> a = stackalloc double[3], b = stackalloc double[3];
            Span<double> pa = stackalloc double[3], pb = stackalloc double[3];
            PrincipalAxis(pa, pb);
            for (int mi = 0; mi < OneRegion.Length && _best.Error > 0; mi++)
            {
                var m = OneRegion[mi];
                _modeBest.Error = long.MaxValue;
                // the extreme pixels may sit on palette entries 0-2 / 13-15: extend the axis to where the ends would be
                for (int i0 = 0; i0 <= 2; i0++)
                    for (int i1 = 15; i1 >= 13; i1--)
                    {
                        double w0 = Weights[i0] / 64.0, w1 = Weights[i1] / 64.0;
                        for (int c = 0; c < 3; c++)
                        {
                            double d = (pb[c] - pa[c]) / (w1 - w0), start = pa[c] - d * w0;
                            a[c] = Clamp(start);
                            b[c] = Clamp(start + d);
                        }
                        Try(m, a, b, _modeBest);
                    }
                for (int it = 0; it < 4; it++)
                {
                    if (!LeastSquares(_modeBest.Index, a, b)) break;
                    if (!Try(m, a, b, _modeBest)) break;
                }
                for (int pass = 0; pass < SearchPasses && _modeBest.Error > 0; pass++)
                {
                    bool improved = false;
                    for (int c = 0; c < 3; c++)
                        foreach (var (d0, d1) in Moves)
                        {
                            _trial.CopyFrom(_modeBest);
                            _trial.Q0[c] += d0;
                            _trial.Q1[c] += d1;
                            if (Evaluate(m, _trial, _modeBest.Error) && _trial.Error < _modeBest.Error)
                            {
                                _modeBest.CopyFrom(_trial);
                                improved = true;
                            }
                        }
                    if (!improved) break;
                }
                if (_modeBest.Error < _best.Error)
                {
                    _best.CopyFrom(_modeBest);
                    bestMode = mi;
                }
            }
            Write(bestMode, _best, dst);
        }

        /// <summary>Quantise float endpoints for a mode and keep them when they beat <paramref name="into"/>.</summary>
        private bool Try(Mode m, ReadOnlySpan<double> a, ReadOnlySpan<double> b, Candidate into)
        {
            for (int c = 0; c < 3; c++)
            {
                _trial.Q0[c] = Quantize(a[c], m.Bits);
                _trial.Q1[c] = Quantize(b[c], m.Bits);
            }
            if (!Evaluate(m, _trial, into.Error) || _trial.Error >= into.Error) return false;
            into.CopyFrom(_trial);
            return true;
        }

        private int Quantize(double u, int bits)
        {
            if (!signed)
            {
                int max = (1 << bits) - 1;
                if (bits >= 15) return (int)Math.Clamp(Math.Round(u), 0, max);
                int g = (int)Math.Floor(u * (1 << bits) / 65536.0);
                int best = 0;
                double bestErr = double.MaxValue;
                for (int c = g - 1; c <= g + 1; c++)
                {
                    int q = Math.Clamp(c, 0, max);
                    double e = Math.Abs(Unquantize(q, bits, false) - u);
                    if (e < bestErr) (bestErr, best) = (e, q);
                }
                return best;
            }
            else
            {
                int max = bits >= 16 ? 32767 : (1 << (bits - 1)) - 1;
                if (bits >= 16) return (int)Math.Clamp(Math.Round(u), -max, max);
                double au = Math.Abs(u);
                int g = (int)Math.Floor(au * (1 << (bits - 1)) / 32768.0);
                int best = 0;
                double bestErr = double.MaxValue;
                for (int c = g - 1; c <= g + 1; c++)
                {
                    int q = Math.Clamp(c, 0, max);
                    double e = Math.Abs(Unquantize(q, bits, true) - au);
                    if (e < bestErr) (bestErr, best) = (e, q);
                }
                return u < 0 ? -best : best;
            }
        }

        /// <summary>
        /// Clamp the candidate into what the mode can store, pick every pixel's index against the decoded palette,
        /// fix the anchor bit, and set the error. False when a component left the endpoint range.
        /// </summary>
        private bool Evaluate(Mode m, Candidate cand, long bound = long.MaxValue)
        {
            int lo = signed ? -((1 << (m.Bits - 1)) - 1) : 0;
            int hi = signed ? (1 << (m.Bits - 1)) - 1 : (int)((1L << m.Bits) - 1);
            if (m.Bits >= 16 && signed) (lo, hi) = (-32767, 32767);
            for (int c = 0; c < 3; c++)
            {
                if (cand.Q0[c] < lo || cand.Q0[c] > hi || cand.Q1[c] < lo || cand.Q1[c] > hi) return false;
                int d = cand.Q1[c] - cand.Q0[c];
                if (d > m.MaxDelta) cand.Q1[c] = cand.Q0[c] + m.MaxDelta;
                else if (d < -m.MaxDelta) cand.Q1[c] = cand.Q0[c] - m.MaxDelta;
            }
            for (int c = 0; c < 3; c++)
            {
                int e0 = Unquantize(cand.Q0[c], m.Bits, signed), e1 = Unquantize(cand.Q1[c], m.Bits, signed);
                for (int k = 0; k < 16; k++) _palette[k * 3 + c] = Finish(Interpolate(e0, e1, Weights[k]), signed);
            }
            long total = 0;
            for (int p = 0; p < 16; p++)
            {
                int t0 = Target[p * 3], t1 = Target[p * 3 + 1], t2 = Target[p * 3 + 2];
                long best = long.MaxValue;
                int bestK = 0;
                for (int k = 0; k < 16; k++)
                {
                    long d0 = _palette[k * 3] - t0, d1 = _palette[k * 3 + 1] - t1, d2 = _palette[k * 3 + 2] - t2;
                    long e = d0 * d0 + d1 * d1 + d2 * d2;
                    if (e < best) (best, bestK) = (e, k);
                }
                cand.Index[p] = (byte)bestK;
                total += best;
                if (total >= bound)                 // cannot win: stop early (most search trials end here)
                {
                    cand.Error = long.MaxValue;
                    return false;
                }
            }
            cand.Error = total;
            if (cand.Index[0] >= 8)                 // anchor: the first index's top bit is implicit 0
            {
                for (int c = 0; c < 3; c++) (cand.Q0[c], cand.Q1[c]) = (cand.Q1[c], cand.Q0[c]);
                for (int p = 0; p < 16; p++) cand.Index[p] = (byte)(15 - cand.Index[p]);
            }
            return true;
        }

        /// <summary>Endpoints at the extremes of the pixels' projection on their principal axis.</summary>
        private void PrincipalAxis(Span<double> a, Span<double> b)
        {
            Span<double> mean = stackalloc double[3];
            for (int p = 0; p < 16; p++)
                for (int c = 0; c < 3; c++) mean[c] += _u[p * 3 + c] / 16;
            Span<double> cov = stackalloc double[9];
            for (int p = 0; p < 16; p++)
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        cov[i * 3 + j] += (_u[p * 3 + i] - mean[i]) * (_u[p * 3 + j] - mean[j]);
            int start = cov[0] >= cov[4] && cov[0] >= cov[8] ? 0 : cov[4] >= cov[8] ? 1 : 2;
            Span<double> axis = [cov[start * 3], cov[start * 3 + 1], cov[start * 3 + 2]];
            Span<double> next = stackalloc double[3];
            double len = 0;
            for (int it = 0; it < 8; it++)
            {
                for (int i = 0; i < 3; i++) next[i] = cov[i * 3] * axis[0] + cov[i * 3 + 1] * axis[1] + cov[i * 3 + 2] * axis[2];
                len = Math.Sqrt(next[0] * next[0] + next[1] * next[1] + next[2] * next[2]);
                if (len < 1e-12) break;
                for (int i = 0; i < 3; i++) axis[i] = next[i] / len;
            }
            if (len < 1e-12)
            {
                mean.CopyTo(a);
                mean.CopyTo(b);
                return;
            }
            double tmin = double.MaxValue, tmax = double.MinValue;
            for (int p = 0; p < 16; p++)
            {
                double t = 0;
                for (int c = 0; c < 3; c++) t += (_u[p * 3 + c] - mean[c]) * axis[c];
                tmin = Math.Min(tmin, t);
                tmax = Math.Max(tmax, t);
            }
            for (int c = 0; c < 3; c++)
            {
                a[c] = Clamp(mean[c] + tmin * axis[c]);
                b[c] = Clamp(mean[c] + tmax * axis[c]);
            }
        }

        /// <summary>Endpoints that minimise the squared error for fixed indices (per channel, 2×2 normal equations).</summary>
        private bool LeastSquares(ReadOnlySpan<byte> index, Span<double> a, Span<double> b)
        {
            double saa = 0, sab = 0, sbb = 0;
            Span<double> sau = stackalloc double[3], sbu = stackalloc double[3];
            for (int p = 0; p < 16; p++)
            {
                double beta = Weights[index[p]] / 64.0, alpha = 1 - beta;
                saa += alpha * alpha;
                sab += alpha * beta;
                sbb += beta * beta;
                for (int c = 0; c < 3; c++)
                {
                    sau[c] += alpha * _u[p * 3 + c];
                    sbu[c] += beta * _u[p * 3 + c];
                }
            }
            double det = saa * sbb - sab * sab;
            if (Math.Abs(det) < 1e-9) return false;
            for (int c = 0; c < 3; c++)
            {
                a[c] = Clamp((sau[c] * sbb - sbu[c] * sab) / det);
                b[c] = Clamp((sbu[c] * saa - sau[c] * sab) / det);
            }
            return true;
        }

        private double Clamp(double u) => signed ? Math.Clamp(u, -32767, 32767) : Math.Clamp(u, 0, 65535);

        private static void Write(int modeIndex, Candidate cand, Span<byte> dst)
        {
            var m = OneRegion[modeIndex];
            int baseMask = (int)((1L << m.Bits) - 1), xMask = (1 << m.XBits) - 1;
            Span<int> w = stackalloc int[3], x = stackalloc int[3];
            for (int c = 0; c < 3; c++)
            {
                w[c] = cand.Q0[c] & baseMask;
                x[c] = (m.Delta > 0 ? cand.Q1[c] - cand.Q0[c] : cand.Q1[c]) & xMask;
            }
            UInt128 v = (UInt128)(uint)m.Code;
            int pos = 5;
            foreach (var (field, bit) in Fields[modeIndex])
            {
                int value = field < 3 ? w[field] : x[field - 3];
                v |= (UInt128)(uint)((value >> bit) & 1) << pos++;
            }
            pos = 65;
            for (int p = 0; p < 16; p++)
            {
                v |= (UInt128)cand.Index[p] << pos;
                pos += p == 0 ? 3 : 4;
            }
            BinaryPrimitives.WriteUInt128LittleEndian(dst, v);
        }
    }
}
