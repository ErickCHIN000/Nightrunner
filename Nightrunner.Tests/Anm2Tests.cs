using System.Buffers.Binary;
using System.Numerics;
using Nightrunner.Core.Anim;
using Nightrunner.Core.Games;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Tests;

// ANM2 clip codec (Core/Anim/Anm2*). No prototype tests to port: nightrunner-main only dumps the header words
// (types/anim.py). tools/AnimCheck runs the whole-install census; here a synthetic set plus a slice of shipped clips.
public partial class Anm2Tests
{
    // ---- h41 -----------------------------------------------------------------------------------------------

    [Fact]
    public void H41KnownValues()
    {
        Assert.Equal(0xCCC3CDDFu, Anm2Hash.H41("OffsetHelper"));
        Assert.Equal(0x30u, Anm2Hash.H41("0"));                       // facial clips' numbered tracks
        Assert.Equal(0u, Anm2Hash.H41(""));
        Assert.Equal(Anm2Hash.H41("offsethelper"), Anm2Hash.H41("OFFSETHELPER"));
        Assert.Equal(unchecked((uint)(0x61 * 41 - 23)), Anm2Hash.H41([0x61, 0xE9]));   // 0xE9 adds as −23
    }

    [Fact]
    public void BindReportsMisses()
    {
        uint[] tracks = [Anm2Hash.H41("pelvis"), Anm2Hash.H41("OffsetHelper"), 0x12345678, Anm2Hash.H41("L_Thigh")];
        var b = Anm2Binding.Bind(tracks, ["pelvis", "l_thigh"]);
        Assert.Equal([0, -1, -1, 1], b.TrackToBone);
        Assert.Equal(2, b.Bound);
        Assert.Equal([(1, Anm2Hash.H41("OffsetHelper"), "OffsetHelper"), (2, 0x12345678u, null)], b.Misses);
    }

    // ---- synthetic codec -----------------------------------------------------------------------------------

    private static Anm2Clip Synthetic(int tracks, int keys, int seed, double noise = 0)
    {
        var rng = new Random(seed);
        var q = new Quaternion[keys * tracks];
        var t = new Vector3[keys * tracks];
        var s = new Vector3[keys * tracks];
        var phase = Enumerable.Range(0, tracks).Select(_ => rng.NextDouble() * 6).ToArray();
        for (int k = 0; k < keys; k++)
            for (int j = 0; j < tracks; j++)
            {
                double a = phase[j] + 0.05 * k + noise * rng.NextDouble();
                var axis = Vector3.Normalize(new Vector3((float)Math.Sin(a), 1, (float)Math.Cos(2 * a)));
                q[k * tracks + j] = Quaternion.CreateFromAxisAngle(axis, (float)(3 * Math.Sin(a + j)));   // crosses w = 0
                t[k * tracks + j] = j == 0 ? new Vector3(0, (float)(0.9 + 0.1 * Math.Sin(a)), (float)(0.02 * k))
                                           : new Vector3(0.1f * j, 0, (float)(noise * rng.NextDouble()));
                s[k * tracks + j] = j == 2 ? new Vector3((float)(1 + 0.2 * Math.Sin(a)), 1, 1) : Vector3.One;
            }
        return Anm2Encoder.FromTrs(Enumerable.Range(0, tracks).Select(i => Anm2Hash.H41($"bone{i}")).ToArray(), keys, q, t, s);
    }

    [Fact]
    public void FreshEncodeRoundTripsWithinQuantisation()
    {
        var src = Synthetic(tracks: 12, keys: 101, seed: 1);
        var bytes = Anm2Encoder.Encode(src);
        var check = Anm2Encoder.SelfCheck(src, bytes);
        Assert.True(check.HeaderOk, check.Problem);
        Assert.True(check.MaxRotationDegrees < 0.01, $"rotation error {check.MaxRotationDegrees}°");
        Assert.True(check.MaxTranslation <= 2.1 / 65534, $"translation error {check.MaxTranslation}");   // range 2.1 m on track 0
        Assert.True(check.MaxScale <= 1e-4 + 1e-9, $"scale error {check.MaxScale}");

        var back = Anm2Clip.Decode(bytes);
        Assert.Equal(100, back.FrameBound);
        Assert.Equal(src.TrackHashes, back.TrackHashes);
        var p = Assert.Single(back.Source!.Segments);
        // track 1 translation: x constant 0.1, y constant 0, z constant 0 → bits 3..5; scale constant → bit 6
        Assert.Equal(0x80 | 0x38 | 0x40, p.Flags[1]);
        Assert.Equal(0x80, p.Flags[2] & 0xC0);                       // track 2's scale is animated
        Assert.Equal(new Vector3(0.1f, 0, 0), back.Translation(50, 1));
        // re-encoding the decode reproduces the bytes
        Assert.Equal(bytes, Anm2Encoder.Encode(back));
        Assert.Equal(bytes, Anm2Encoder.Encode(back, Anm2EncodeOptions.Recipe));
    }

    [Fact]
    public void MultiSegmentAndSeams()
    {
        // noisy streams need wide rows: 40 tracks × 9 animated streams overflow 64 KiB within a few key blocks
        var src = Synthetic(tracks: 40, keys: 301, seed: 2, noise: 0.5);
        var bytes = Anm2Encoder.Encode(src);
        var back = Anm2Clip.Decode(bytes);
        var h = back.Source!.Header;
        Assert.True(h.NumBlocks > 1, "expected several segments");
        Assert.Equal(300, h.SegmentFrames.Sum(x => x));
        Assert.All(h.SegmentFrames.SkipLast(1), f => Assert.Equal(0, f % 15));
        Assert.Equal((long)Anm2Header.SegmentBytes * (h.NumBlocks - 1), bytes.Length - h.HeaderSize - Anm2Payload.SegmentSize(back.Source.Segments[^1], 40));
        // every seam key is stored twice, each copy quantised by its own segment
        int F = 0;
        foreach (var f in h.SegmentFrames.SkipLast(1)) { F += f; Assert.True(back.EndCopies.ContainsKey(F)); }
        Assert.True(back.MaxSeamJump() < 1e-4);
        Assert.Equal(bytes, Anm2Encoder.Encode(back));
        var check = Anm2Encoder.SelfCheck(src, bytes);
        Assert.True(check.HeaderOk && check.MaxRotationDegrees < 0.01, $"{check}");
    }

    [Fact]
    public void HardCutSplitsTheSegment()
    {
        var src = Synthetic(tracks: 3, keys: 41, seed: 3);
        // a cut at frame 20: the stored copy of key 20 in the segment before the cut is 5 m away
        var copy = src.Values.AsSpan(20 * src.StreamCount, src.StreamCount).ToArray();
        copy[3] += 5;
        src.EndCopies[20] = copy;
        var bytes = Anm2Encoder.Encode(src);
        var back = Anm2Clip.Decode(bytes);
        Assert.Equal([20, 20], back.Source!.Header.SegmentFrames);
        Assert.Equal(5, back.EndCopies[20][3] - back.Values[20 * back.StreamCount + 3], 3);
        Assert.Equal(bytes, Anm2Encoder.Encode(back));

        // a cut on a key-block boundary stays inside the segment, in row 15 (the "row-15 quirk")
        src.EndCopies.Clear();
        src.EndCopies[15] = copy;
        back = Anm2Clip.Decode(Anm2Encoder.Encode(src));
        Assert.Equal([40], back.Source!.Header.SegmentFrames);
        Assert.Equal(5, back.EndCopies[15][3] - back.Values[15 * back.StreamCount + 3], 3);
    }

    [Fact]
    public void SinglePoseIsTwoKeys()
    {
        var bytes = Anm2Encoder.Encode(Anm2Encoder.FromTrs([Anm2Hash.H41("root")], 1,
            [Quaternion.CreateFromYawPitchRoll(0.3f, 0.2f, 0.1f)], [new Vector3(1, 2, 3)], [Vector3.One]));
        var back = Anm2Clip.Decode(bytes);
        Assert.Equal(1, back.FrameBound);
        Assert.Equal(back.Rotation(0, 0), back.Rotation(1, 0));
        Assert.Equal(new Vector3(1, 2, 3), back.Translation(1, 0));
        Assert.Equal(0xFF, back.Source!.Segments[0].Flags[0]);   // everything constant
    }

    [Fact]
    public void PoseWeightsDecodeAsWeights()
    {
        const int T = 3, keys = 20;
        var poses = Enumerable.Range(0, 9 * T - 2).Select(i => (uint)(1000 + i)).ToArray();   // 9T−9 < n ≤ 9T
        var v = new double[keys * 9 * T];
        for (int k = 0; k < keys; k++)
            for (int i = 0; i < 9 * T; i++) v[k * 9 * T + i] = i < poses.Length ? Math.Clamp(Math.Sin(0.2 * k + i), 0, 1) : 0;
        var src = Anm2Encoder.FromStreams([0x30, 0x31, 0x32], poses, keys - 1, v);
        var bytes = Anm2Encoder.Encode(src);
        var back = Anm2Clip.Decode(bytes);
        Assert.True(back.IsPoseWeights);
        Assert.Equal(poses, back.PoseHashes);
        for (int k = 0; k < keys; k++)
            for (int i = 0; i < poses.Length; i++)
                Assert.True(Math.Abs(v[k * 9 * T + i] - back.Weight(k, i)) <= (i % 9 >= 6 ? 1e-4 : 1e-5) + 1e-9);   // half the scale floor
        Assert.Throws<ArgumentOutOfRangeException>(() => back.Weight(0, poses.Length));
        Assert.Equal(bytes, Anm2Encoder.Encode(back));
    }

    [Fact]
    public void StereographicInverse()
    {
        var q = Quaternion.Normalize(new Quaternion(0.3f, -0.5f, 0.1f, -0.8f));   // w < 0 is flipped
        var (x, y, z) = Anm2Clip.ToStereo(q);
        var back = Anm2Clip.FromStereo(x, y, z);
        Assert.True(back.W >= 0);
        Assert.True(Math.Abs(Math.Abs(Quaternion.Dot(q, back)) - 1) < 1e-6);
        Assert.Equal(Quaternion.Identity, Anm2Clip.FromStereo(0, 0, 0));
    }

    [Fact]
    public void StreamPairSplitJoins()
    {
        var bytes = Anm2Encoder.Encode(Synthetic(4, 31, 5));
        var (hd, pd) = Anm2Resource.SplitStreamPair(bytes);
        var h = Anm2Header.Parse(hd, pd.Length);
        Assert.Equal(h.HeaderSize, hd.Length);
        Assert.Equal(h.PayloadSize, pd.Length);
        Assert.Equal(bytes, hd.Concat(pd).ToArray());
    }

    // ---- validator ----------------------------------------------------------------------------------------

    private static byte[] Small() => Anm2Encoder.Encode(Synthetic(2, 16, 7));

    private static byte[] With(byte[] b, int at, ushort v)
    {
        var c = (byte[])b.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(at), v);
        return c;
    }

    [Fact]
    public void ValidatorRefusals()
    {
        var ok = Small();
        var h = Anm2Header.Parse(ok);
        Assert.Equal(3, h.Version);
        Assert.Equal(15, h.FrameBound);
        Assert.True(h.IdentityVfr);

        void Refuses<T>(byte[] b, string text) where T : Exception
        {
            var e = Assert.Throws<T>(() => Anm2Payload.Decode(b));
            Assert.Contains(text, e.Message);
        }
        var bad = (byte[])ok.Clone();
        bad[0] = (byte)'X';
        Refuses<Anm2FormatException>(bad, "magic");
        Refuses<Anm2FormatException>(With(ok, 4, 43), "endian");
        Refuses<Anm2UnsupportedException>(With(ok, 4, 0x2A00), "byte-swapped");
        Refuses<Anm2FormatException>(With(ok, 6, 0), "V0");                     // a V3 header read as V0 or V1 fails their checks
        Refuses<Anm2FormatException>(With(ok, 6, 1), "V1");
        Refuses<Anm2UnsupportedException>(With(ok, 6, 4), "version 4");
        Refuses<Anm2FormatException>(With(ok, 0x0E, 0x800), "blockSize16");
        Refuses<Anm2FormatException>(With(ok, 0x10, 2), "numBlocks");
        Refuses<Anm2FormatException>(With(ok, 0x12, 0), "zero bound");
        Refuses<Anm2FormatException>(With(ok, 0x14, 0), "zero bound");
        Refuses<Anm2FormatException>(With(ok, 0x16, 0), "numVfr");
        Refuses<Anm2FormatException>(With(ok, 0x1A, 19), "numStatic");
        Refuses<Anm2FormatException>(With(ok, 0x1C, 5), "numPose");
        Refuses<Anm2FormatException>(With(ok, 0x0C, (ushort)(h.Header16 + 1)), "header16");
        int seg = 0x20 + 4 * h.NumTracks;
        Refuses<Anm2FormatException>(With(ok, seg, 14), "segFrames");
        Refuses<Anm2FormatException>(With(ok, seg + 4, 14), "VFR");               // len != timeBound
        Refuses<Anm2FormatException>(With(ok, seg + 2, 0), "VFR");                // den 0
        Refuses<Anm2FormatException>(ok[..^16], "payload");                      // file shorter than header + payload
        // the payload is never checked by the engine; the decoder does check it
        var broken = (byte[])ok.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(broken.AsSpan(h.HeaderSize + 2), 0x7FF);   // key block 0 offset
        Refuses<Anm2FormatException>(broken, "key block 0");
    }

    // ---- shipped clips ---------------------------------------------------------------------------------------

    private static RpackFile? OpenPack(GameInstall gi, string file)
    {
        var path = gi.Rpacks().FirstOrDefault(p => Path.GetFileName(p).Equals(file, StringComparison.OrdinalIgnoreCase));
        return path is null ? null : RpackFile.Open(path);
    }

    private static RpackFile Need(GameInstall gi, string file)
    {
        var pack = OpenPack(gi, file);
        if (pack is null) Assert.Skip($"no {file} in the install");
        return pack!;
    }

    private static int Find(RpackFile pack, string name) =>
        pack.IndicesOf(RpackFile.EngineFold(System.Text.Encoding.Latin1.GetBytes(name))).First(i => pack.Logicals[i].Type == 0x40);

    private static void AssertRoundTrip(byte[] bytes, string name)
    {
        var p = Anm2Payload.Decode(bytes);
        Assert.True(p.Encode().AsSpan().SequenceEqual(bytes), $"{name}: re-serialise differs");
        var clip = Anm2Clip.FromPayload(p);
        Assert.True(Anm2Encoder.Encode(clip).AsSpan().SequenceEqual(bytes), $"{name}: re-encode differs");
    }

    [Fact]
    public void ShippedHeaderAndBinding()
    {
        var gi = Installs.Require("dltb");
        using var pack = Need(gi, "player_anims_pc.rpack");
        var bytes = Anm2Resource.Read(pack, Find(pack, "m_tpp_menu_main_stairs_idle_01"), out bool pair);
        Assert.False(pair);
        var h = Anm2Header.Parse(bytes);
        Assert.Equal(3, h.Version);
        Assert.Equal(67, h.NumTracks);
        Assert.Equal(h.FrameBound, h.TimeBound);
        Assert.True(h.IdentityVfr);
        Assert.Equal(h.HeaderSize + h.PayloadSize, bytes.Length);
        Assert.Contains(Anm2Hash.H41("OffsetHelper"), h.TrackHashes);
        Assert.Contains(Anm2Hash.H41("pelvis"), h.TrackHashes);
        Assert.Contains(Anm2Hash.H41("l_thigh"), h.TrackHashes);
        Assert.Contains(Anm2Hash.H41("r_finger43"), h.TrackHashes);

        using var meshes = Need(gi, "common_meshes_pc.rpack");
        int mi = meshes.IndicesOf("sh2_player_tpp_phx_skeleton"u8).First(i => meshes.Logicals[i].Type == 0x10);
        var skel = MeshDecoder.Decode(meshes, mi);
        var clip = Anm2Clip.Decode(bytes);
        var bind = Anm2Binding.Bind(clip.TrackHashes, skel);
        Assert.Equal(66, bind.Bound);
        Assert.Equal("OffsetHelper", Assert.Single(bind.Misses).Name);
        // frame-0 translations are the bind locals (parent-local metres) for the bones the idle does not move
        int close = 0;
        for (int t = 0; t < clip.TrackCount; t++)
        {
            int b = bind.TrackToBone[t];
            if (b < 0) continue;
            var L = skel.Entities[b].Local;
            var tr = clip.Translation(0, t);
            if (Math.Abs(tr.X - L[3]) < 1e-5 && Math.Abs(tr.Y - L[7]) < 1e-5 && Math.Abs(tr.Z - L[11]) < 1e-5) close++;
            Assert.True(Math.Abs(clip.Rotation(0, t).Length() - 1) < 1e-6);
        }
        Assert.True(close >= 55, $"{close} of {bind.Bound} bound tracks sit at the bind translation");
    }

    [Fact]
    public void ShippedStreamPairsAreThePlainBytes()
    {
        var gi = Installs.Require("dltb");
        foreach (var (plainFile, streamFile) in new[] { ("player_anims_static_pc.rpack", "player_anims_stream_pc.rpack"), ("common_anims_pc.rpack", "common_anims_stream_pc.rpack") })
        {
            using var plain = OpenPack(gi, plainFile);
            using var stream = OpenPack(gi, streamFile);
            if (plain is null || stream is null) continue;
            int n = 0;
            for (int i = 0; i < plain.Count && n < 150; i++)
            {
                if (plain.Logicals[i].Type != 0x40) continue;
                var name = plain.Name(i);
                var a = Anm2Resource.Read(plain, i, out bool pa);
                var b = Anm2Resource.Read(stream, Find(stream, name), out bool pb);
                Assert.False(pa);
                Assert.True(pb);
                Assert.True(a.AsSpan().SequenceEqual(b), $"{name}: plain != 0x44 ‖ 0x45");
                n++;
            }
            Assert.True(n > 0);
        }
    }

    [Fact]
    public void ShippedClipsRoundTripBitExact()
    {
        var gi = Installs.Require("dltb");
        using var player = Need(gi, "player_anims_pc.rpack");
        int n = 0, multi = 0;
        for (int i = 0; i < player.Count && n < 250; i++)
        {
            if (player.Logicals[i].Type != 0x40) continue;
            var bytes = Anm2Resource.Read(player, i);
            AssertRoundTrip(bytes, player.Name(i));
            if (Anm2Header.Parse(bytes).NumBlocks > 1) multi++;
            n++;
        }
        Assert.True(multi > 0, "the slice has no multi-segment clip");

        var relaxed = Anm2Resource.Read(player, Find(player, "m_fpp_stick_idle_relaxed"));
        Assert.Equal([255, 300, 115], Anm2Header.Parse(relaxed).SegmentFrames.Select(x => (int)x));
        AssertRoundTrip(relaxed, "m_fpp_stick_idle_relaxed");
        // the plain recipe lays this one out differently (the shipped encoder stopped a block early)
        var greedy = Anm2Encoder.Quantize(Anm2Clip.Decode(relaxed), new Anm2EncodeOptions { ReuseSourceLayout = false });
        Assert.Equal([270, 300, 100], greedy.Header.SegmentFrames.Select(x => (int)x));
    }

    [Fact]
    public void ShippedSpecialClips()
    {
        var gi = Installs.Require("dltb");
        using var lang = OpenPack(gi, "lang_speech_en_pc.rpack");
        if (lang is not null)
        {
            int i = Enumerable.Range(0, lang.Count).First(x => lang.Logicals[x].Type == 0x40);
            var bytes = Anm2Resource.Read(lang, i);
            var clip = Anm2Clip.Decode(bytes);
            Assert.True(clip.IsPoseWeights);
            Assert.InRange(clip.PoseHashes.Length, 9 * clip.TrackCount - 8, 9 * clip.TrackCount);
            Assert.Equal(Anm2Hash.H41("0"), clip.TrackHashes[0]);
            AssertRoundTrip(bytes, lang.Name(i));
        }

        using var common = Need(gi, "common_anims_pc.rpack");
        // V2 clips (no numPose; one carries left-over bytes in its static block pad) re-encode as V2
        foreach (var name in new[] { "solid_head_empty", "editor_anim_preview_dummy", "dev_proto_m_hmf_bow_idle_calm" })
        {
            var bytes = Anm2Resource.Read(common, Find(common, name));
            Assert.Equal(2, Anm2Header.Parse(bytes).Version);
            AssertRoundTrip(bytes, name);
        }
        // V0 demo clips decode (the V0 census is in ShippedV0Clips)
        var v0 = Anm2Resource.Read(common, Find(common, "fighter_attack_right_hand_up_demo"));
        Assert.Equal(0, Anm2Clip.Decode(v0).Version);
        // cuts: at a block boundary inside a segment (row 15) and at seams
        var quirk = Anm2Clip.Decode(Anm2Resource.Read(common, Find(common, "assasination_meeting_colonel_dlg_04a_crossbow_00")));
        Assert.Equal([423], quirk.Source!.Header.SegmentFrames.Select(x => (int)x));
        Assert.Equal([300, 420], Anm2Encoder.Cuts(quirk));
        Assert.Equal([423], Anm2Encoder.Quantize(quirk, new Anm2EncodeOptions { ReuseSourceLayout = false }).Header.SegmentFrames.Select(x => (int)x));
        var cut = Anm2Resource.Read(common, Find(common, "broadcast_part_b_start_dlg_branch02_elevator_00"));
        var cc = Anm2Clip.Decode(cut);
        Assert.Equal([1266, 2858], Anm2Encoder.Cuts(cc));
        Assert.True(cc.MaxSeamJump() > 45);
        AssertRoundTrip(cut, "broadcast_part_b_start_dlg_branch02_elevator_00");
    }
}
