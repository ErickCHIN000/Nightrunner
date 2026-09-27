using System.Buffers.Binary;
using Nightrunner.Core.Anim;
using Nightrunner.Core.Games;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Tests;

// ANM2 header versions 1 and 0 (Header_Version1: 13 DL2 clips; Header_Version0: the *_demo dev clips of both games).
// tools/AnimCheck --game dl2 --against dltb runs the whole census.
public partial class Anm2Tests
{
    private static int U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));

    /// <summary>
    /// Largest translation/scale |Δ| and rotation angle (degrees) over the tracks two clips share and the keys both
    /// have. Rotation streams are compared as rotations: near w = 0 the stereographic values of q and −q differ widely.
    /// </summary>
    private static (double TransScale, double Degrees) MaxDiff(Anm2Clip a, Anm2Clip b)
    {
        double m = 0, deg = 0;
        for (int t1 = 0; t1 < a.TrackCount; t1++)
        {
            int t2 = Array.IndexOf(b.TrackHashes, a.TrackHashes[t1]);
            if (t2 < 0) continue;
            for (int k = 0; k < Math.Min(a.KeyCount, b.KeyCount); k++)
            {
                for (int j = 3; j < 9; j++) m = Math.Max(m, Math.Abs(a.Stream(k, t1, j) - b.Stream(k, t2, j)));
                double dot = Math.Abs(System.Numerics.Quaternion.Dot(a.Rotation(k, t1), b.Rotation(k, t2)));
                deg = Math.Max(deg, 2 * Math.Acos(Math.Min(1.0, dot)) * 180 / Math.PI);
            }
        }
        return (m, deg);
    }

    // ---- synthetic ------------------------------------------------------------------------------------------

    [Fact]
    public void V1HeaderCarriesTheV3Payload()
    {
        var src = Synthetic(tracks: 5, keys: 41, seed: 11);
        var v3 = Anm2Encoder.Encode(src);
        var v1 = Anm2Encoder.Encode(src, new Anm2EncodeOptions { Version = 1 });
        var h3 = Anm2Header.Parse(v3);
        var h = Anm2Header.Parse(v1);
        Assert.Equal(1, h.Version);
        Assert.Equal(1, U16(v1, 6));
        Assert.Equal(41, U16(v1, 0x08));                     // numFrames = frameBound + 1
        Assert.Equal(5, U16(v1, 0x0A));                      // numTracks
        Assert.Equal(1, U16(v1, 0x0C));                      // numBlocks
        Assert.Equal(0x40, U16(v1, 0x0E));                   // headerSize: 0x20 + 5 hashes + segFrames + VFR, padded to 16
        Assert.Equal((uint)v1.Length, U32(v1, 0x10));        // fileSize
        Assert.Equal(1, U16(v1, 0x14));                      // numVfr
        Assert.Equal(Anm2Hash.H41("bone0"), U32(v1, 0x20));
        Assert.Equal([40, 1, 40, 1], new[] { U16(v1, 0x34), U16(v1, 0x36), U16(v1, 0x38), U16(v1, 0x3A) });   // segFrames, vfrDen, (len, rate)
        Assert.Equal(v3.AsSpan(h3.HeaderSize).ToArray(), v1.AsSpan(h.HeaderSize).ToArray());                    // the payload is V3's

        var back = Anm2Clip.Decode(v1);
        Assert.Equal(1, back.Version);
        Assert.Equal(Anm2Clip.Decode(v3).Values, back.Values);
        Assert.Equal(40, back.TimeBound);
        Assert.Equal(v1, Anm2Encoder.Encode(back));

        // the reserved bytes and the header pad are kept verbatim
        var odd = (byte[])v1.Clone();
        odd[0x17] = 0x5A;
        odd[h.LayoutEnd] = 0xBC;
        Assert.Equal(odd, Anm2Payload.Decode(odd).Encode());
        Assert.Equal(odd, Anm2Encoder.Encode(Anm2Clip.Decode(odd)));

        void Refuses<T>(byte[] b, string text) where T : Exception => Assert.Contains(text, Assert.Throws<T>(() => Anm2Payload.Decode(b)).Message);
        Refuses<Anm2FormatException>(With(v1, 0x08, 0), "numFrames is 0");
        Refuses<Anm2FormatException>(With(v1, 0x0A, 0), "numTracks is 0");
        Refuses<Anm2FormatException>(With(v1, 0x0C, 2), "numBlocks");
        Refuses<Anm2FormatException>(With(With(v1, 0x0E, 0x50), 0x10, (ushort)(v1.Length + 0x10)), "headerSize");
        Refuses<Anm2FormatException>(With(v1, 0x14, 0), "numVfr");
        Refuses<Anm2FormatException>(With(v1, 0x34, 39), "segFrames");
        Refuses<Anm2FormatException>(With(v1, 0x3A, 2), "VFR");               // Σ len·rate / den != numFrames − 1
        Refuses<Anm2FormatException>(v1[..^16], "fileSize");
        Assert.Throws<Anm2UnsupportedException>(() => Anm2Encoder.Encode(       // V1 has no pose list
            Anm2Encoder.FromStreams([1, 2], [5, 6, 7, 8, 9, 10, 11, 12, 13, 14], 3, new double[4 * 18]), new Anm2EncodeOptions { Version = 1 }));
    }

    /// <summary>
    /// A V0 clip written by hand from a one-segment V3 clip whose frame count is not a multiple of 15 (where the two
    /// block layouts agree): the static block moves into the header with flag bit 7 cleared; the payload keeps the key
    /// blocks behind an offset table without the static entry.
    /// </summary>
    private static byte[] V0FromV3(byte[] v3, ushort rate, byte[] trailer)
    {
        var h = Anm2Header.Parse(v3);
        Assert.Equal(1, h.NumBlocks);
        Assert.NotEqual(0, h.FrameBound % 15);
        var seg = v3.AsSpan(h.HeaderSize).ToArray();
        int T = h.NumTracks, nb = (h.FrameBound + 14) / 15;
        int so = U16(seg, 0) * 16, k0 = U16(seg, 2) * 16, end = U16(seg, 2 * (nb + 1)) * 16;
        var stat = seg[so..k0];
        int fo = 16 + U16(stat, 6) + 4 * U16(stat, 0);
        for (int t = 0; t < T; t++) stat[fo + t] &= 0x7F;
        int tbl = (2 * (nb + 1) + 15) & ~15;
        var payload = new byte[tbl + end - k0];
        for (int b = 0; b <= nb; b++)
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2 * b), (ushort)((tbl + U16(seg, 2 * (1 + b)) * 16 - k0) / 16));
        seg.AsSpan(k0, end - k0).CopyTo(payload.AsSpan(tbl));
        int dataEnd = 0x20 + stat.Length + 4 * T + 2 + trailer.Length, hs = (dataEnd + 0x7FF) & ~0x7FF;
        var o = new byte[hs + payload.Length];
        var s = o.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, Anm2Header.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], 42);
        BinaryPrimitives.WriteUInt16LittleEndian(s[8..], rate);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x0A..], (ushort)(h.FrameBound + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x0C..], (ushort)(h.FrameBound + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x0E..], (ushort)T);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x10..], (ushort)stat.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x12..], (ushort)trailer.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x14..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x16..], (ushort)dataEnd);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x18..], (uint)o.Length);
        stat.CopyTo(s[0x20..]);
        int at = 0x20 + stat.Length;
        foreach (uint th in h.TrackHashes) { BinaryPrimitives.WriteUInt32LittleEndian(s[at..], th); at += 4; }
        BinaryPrimitives.WriteUInt16LittleEndian(s[at..], h.FrameBound);
        trailer.CopyTo(s[(at + 2)..]);
        payload.CopyTo(s[hs..]);
        return o;
    }

    [Fact]
    public void V0KeepsTheStaticBlockInTheHeader()
    {
        var src = Synthetic(tracks: 6, keys: 41, seed: 12);
        var v3 = Anm2Encoder.Encode(src);
        byte[] trailer = [(byte)'d', (byte)'e', (byte)'m', (byte)'o', 0];
        var v0 = V0FromV3(v3, 30, trailer);
        var h = Anm2Header.Parse(v0);
        Assert.Equal(0, h.Version);
        Assert.Equal(30, h.Rate);
        Assert.Equal(trailer, h.Trailer);
        Assert.Equal(2048, h.HeaderSize);
        Assert.Equal(40, h.FrameBound);
        Assert.Empty(h.Vfr);

        var back = Anm2Clip.Decode(v0);
        Assert.Equal(0, back.Version);
        Assert.Equal(Anm2Clip.Decode(v3).Values, back.Values);   // flag bit 7 is not read
        var seg = back.Source!.Segments[0];
        Assert.True(seg.TrailingBlock);
        Assert.All(seg.Flags, f => Assert.Equal(0, f & 0x80));
        Assert.Equal(v0, Anm2Payload.Decode(v0).Encode());
        Assert.Equal(v0, Anm2Encoder.Encode(back));
        var (hd, pd) = Anm2Resource.SplitStreamPair(v0);
        Assert.Equal(2048, hd.Length);
        Assert.Equal(h.PayloadSize, Anm2Header.Parse(hd, pd.Length).PayloadSize);

        // 30 frames, a multiple of 15: V0 stores a third, trailing key block (the last key again and a pad row)
        var src30 = Synthetic(tracks: 6, keys: 31, seed: 13);
        var clip30 = new Anm2Clip { Version = 0, TrackHashes = src30.TrackHashes, FrameBound = 30, Values = src30.Values, Source = back.Source };
        var b30 = Anm2Encoder.Encode(clip30);
        var back30 = Anm2Clip.Decode(b30);
        Assert.Equal(0, back30.Version);
        var s30 = back30.Source!.Segments[0];
        Assert.Equal(3, s30.Blocks);
        Assert.Equal(16 + 16 + 2, s30.Rows);
        Assert.Equal(b30.Length - 2048, U16(b30, 2048 + 2 * 3) * 16);   // off16[3] is the payload end
        Assert.Empty(back30.PadRows);
        Assert.Empty(back30.EndCopies);                                   // row 15 of block 1 repeats key 30 exactly
        var check = Anm2Encoder.SelfCheck(src30, b30);
        Assert.True(check.HeaderOk && check.MaxRotationDegrees < 0.01 && check.MaxTranslation < 1e-4, $"{check}");
        Assert.Equal(b30, Anm2Encoder.Encode(back30));
        Assert.Equal(b30, Anm2Payload.Decode(b30).Encode());

        void Refuses<T>(byte[] b, string text) where T : Exception => Assert.Contains(text, Assert.Throws<T>(() => Anm2Payload.Decode(b)).Message);
        Refuses<Anm2UnsupportedException>(With(v0, 0x14, 2), "payload blocks");
        Refuses<Anm2UnsupportedException>(With(v0, 0x0C, 40), "second frame count");
        Refuses<Anm2FormatException>(With(v0, 0x16, (ushort)(U16(v0, 0x16) + 1)), "header data end");
        Refuses<Anm2FormatException>(With(v0, 0x20, (ushort)(U16(v0, 0x20) + 1)), "nConst");
        Refuses<Anm2FormatException>(With(v0, 2048, 0), "overlaps the offset table");
        Refuses<Anm2FormatException>(v0[..^16], "fileSize");
        // writing V0 takes the rate word and trailer from a V0 source
        Assert.Throws<Anm2UnsupportedException>(() => Anm2Encoder.Encode(src, new Anm2EncodeOptions { Version = 0 }));
    }

    // ---- shipped --------------------------------------------------------------------------------------------

    private static readonly string[] DltbV0 =
    [
        "fighter_attack_right_hand_down_demo", "fighter_attack_right_hand_up_demo", "infected_sprint_01_demo", "mz_upset_sprint_demo",
        "mzi_upset_climb_02_demo", "mzi_upset_jumps07_demo", "mzi_upset_sprint_start90l_rleg_demo", "mzi_upset_sprintbarrierjumpup_demo",
        "mzi_upset_stand_idle01_demo",
    ];

    private static readonly (string Pack, string Name)[] Dl2V1 =
    [
        ("common_anims_pc.rpack", "infected_walk_loop_01"), ("common_anims_pc.rpack", "m_coop_calm_run_stop"),
        ("common_anims_pc.rpack", "m_coop_unarmed_runforward"), ("common_anims_pc.rpack", "militarycargo_trigger"),
        ("common_anims_pc.rpack", "mzi_sprint_sudden_turn_180r_mirror"), ("common_anims_pc.rpack", "wardrobe_encounter"),
        ("common_anims_pc.rpack", "wardrobe_opening"), ("player_anims_static_pc.rpack", "m_fpp_unarmed_wagondoor_minigame"),
        ("player_anims_static_pc.rpack", "m_fpp_unarmed_wagondoor_minigame_02"), ("player_anims_static_pc.rpack", "m_fpp_unarmed_wagondoor_minigame_begin_02"),
        ("player_anims_static_pc.rpack", "m_fpp_wagondoor_minigame_02"), ("player_anims_static_pc.rpack", "m_fpp_wagondoor_minigame_begin_02"),
        ("player_anims_static_pc.rpack", "valveminigame"),
    ];

    /// <summary>Bound tracks of <paramref name="clip"/> whose frame-0 translation is the skeleton's bind local within 1e-5 m.</summary>
    private static (int Bound, int AtBind) AtBind(Anm2Clip clip, MeshModel skel)
    {
        var bind = Anm2Binding.Bind(clip.TrackHashes, skel);
        int close = 0;
        for (int t = 0; t < clip.TrackCount; t++)
        {
            int b = bind.TrackToBone[t];
            if (b < 0) continue;
            var L = skel.Entities[b].Local;
            var tr = clip.Translation(0, t);
            if (Math.Abs(tr.X - L[3]) <= 1e-5 && Math.Abs(tr.Y - L[7]) <= 1e-5 && Math.Abs(tr.Z - L[11]) <= 1e-5) close++;
        }
        return (bind.Bound, close);
    }

    private static MeshModel Skeleton(GameInstall gi, string name)
    {
        using var meshes = Need(gi, "common_meshes_pc.rpack");
        int i = meshes.IndicesOf(System.Text.Encoding.Latin1.GetBytes(name)).FirstOrDefault(x => meshes.Logicals[x].Type == 0x10, -1);
        if (i < 0) Assert.Skip($"no {name} mesh in common_meshes_pc");
        return MeshDecoder.Decode(meshes, i);
    }

    [Fact]
    public void ShippedV0Clips()
    {
        var gi = Installs.Require("dltb");
        using var common = Need(gi, "common_anims_pc.rpack");
        using var stream = Need(gi, "common_anims_stream_pc.rpack");
        foreach (var name in DltbV0)
        {
            var bytes = Anm2Resource.Read(common, Find(common, name));
            var h = Anm2Header.Parse(bytes);
            Assert.Equal(0, h.Version);
            Assert.Equal(1, h.NumBlocks);
            Assert.Equal(0, h.HeaderSize % 2048);
            Assert.Equal(41, h.Trailer.Length);
            Assert.Equal(0, h.Trailer[^1]);
            Assert.Equal(name == "infected_sprint_01_demo" ? 0xBC3C : 30, h.Rate);
            AssertRoundTrip(bytes, name);
            Assert.Equal(bytes, Anm2Resource.Read(stream, Find(stream, name), out bool pair));   // 0x44 = the 2 KiB header
            Assert.True(pair);
        }
        // 195 frames, a multiple of 15: 13 full key blocks and the trailing one
        var climb = Anm2Clip.Decode(Anm2Resource.Read(common, Find(common, "mzi_upset_climb_02_demo")));
        Assert.Equal(195, climb.FrameBound);
        Assert.Equal(14, climb.Source!.Segments[0].Blocks);
        // frame 0 of an infected clip sits on the generic skeleton's bind translations
        var (bound, atBind) = AtBind(Anm2Clip.Decode(Anm2Resource.Read(common, Find(common, "mzi_upset_sprint_start90l_rleg_demo"))), Skeleton(gi, "skeleton"));
        Assert.True(bound >= 60 && atBind >= 50, $"{atBind} of {bound} bound tracks at the bind translation");
    }

    [Fact]
    public void ShippedV1Clips()
    {
        var gi = Installs.Require("dl2");
        var twins = new Dictionary<string, string> { ["common_anims_pc.rpack"] = "common_anims_stream_pc.rpack", ["player_anims_static_pc.rpack"] = "player_anims_stream_pc.rpack" };
        var clips = new Dictionary<string, Anm2Clip>();
        foreach (var g in Dl2V1.GroupBy(x => x.Pack))
        {
            using var pack = Need(gi, g.Key);
            using var stream = Need(gi, twins[g.Key]);
            foreach (var (_, name) in g)
            {
                var bytes = Anm2Resource.Read(pack, Find(pack, name));
                Assert.Equal(1, Anm2Header.Parse(bytes).Version);
                AssertRoundTrip(bytes, name);
                Assert.Equal(bytes, Anm2Resource.Read(stream, Find(stream, name)));
                clips[name] = Anm2Clip.Decode(bytes);
            }
        }
        Assert.Equal(13, clips.Count);
        var (bound, atBind) = AtBind(clips["infected_walk_loop_01"], Skeleton(gi, "skeleton"));
        Assert.True(bound >= 60 && atBind >= 40, $"{atBind} of {bound} bound tracks at the bind translation");

        // one clip in both old versions: the V1 clip and its V0 "_demo" twin carry the same motion
        using var common = Need(gi, "common_anims_pc.rpack");
        var demo = Anm2Clip.Decode(Anm2Resource.Read(common, Find(common, "mzi_sprint_sudden_turn_180r_mirror_demo")));
        Assert.Equal(0, demo.Version);
        var (dts, deg) = MaxDiff(clips["mzi_sprint_sudden_turn_180r_mirror"], demo);
        Assert.InRange(dts, 1e-7, 1e-3);   // two quantisations of one motion
        Assert.True(deg < 0.1, $"{deg}°");
    }

    [Fact]
    public void OldVersionsMatchTheirDltbReencodes()
    {
        var dl2 = Installs.Require("dl2");
        var dltb = Installs.Require("dltb");
        Anm2Clip Load(GameInstall gi, string file, string name)
        {
            using var pack = Need(gi, file);
            return Anm2Clip.Decode(Anm2Resource.Read(pack, Find(pack, name)));
        }
        // DLTB ships V3 re-encodes of DL2's V1 wardrobe clips with the same values
        foreach (var name in new[] { "wardrobe_opening", "wardrobe_encounter" })
        {
            var v1 = Load(dl2, "common_anims_pc.rpack", name);
            var v3 = Load(dltb, "common_anims_pc.rpack", name);
            Assert.Equal((1, 3), (v1.Version, v3.Version));
            Assert.Equal(v1.TrackHashes, v3.TrackHashes);
            Assert.Equal(v1.Values, v3.Values);
        }
        // and V3 re-encodes of DL2's V0 demo clips, within a quantisation step
        var v0 = Load(dl2, "common_anims_pc.rpack", "mzi_upset_walk_loop_v04_demo");
        var r3 = Load(dltb, "common_anims_pc.rpack", "mzi_upset_walk_loop_v04_demo");
        Assert.Equal((0, 3), (v0.Version, r3.Version));
        Assert.Equal(v0.FrameBound, r3.FrameBound);
        var (dts, deg) = MaxDiff(v0, r3);
        Assert.True(dts < 1e-3 && deg < 0.1, $"{dts} m, {deg}°");
    }
}
