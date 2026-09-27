using System.Buffers.Binary;
using Nightrunner.Core.Anim;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Tests;

// Sequence banks (AnimationScr 0x42/0x43), graph banks (AnimGraphBank 0x47/0x48) and the AnimCustomResource header
// pair (0x49/0x4A). The prototype's types/animscr, animgraph and animcustom modules are structural dumps with no tests
// of their own; nothing is ported from nightrunner-main/tests. The whole-install census is tools/AnimBankCheck.
public class AnimBankTests
{
    // ---- synthetic ------------------------------------------------------------------------------------------

    private static AnimBank SampleBank()
    {
        var bank = new AnimBank();
        bank.ActionLists.Add([new ActionParams("playstepsfx", [new ActionArg('s', Text: "step_left"), new ActionArg('s', Text: "l_foot")])]);
        bank.ActionLists.Add([
            new ActionParams("_attributes", [new ActionArg('i', 0), new ActionArg('i', 0), new ActionArg('i', 1), new ActionArg('s', Text: "Human")]),
            new ActionParams("playfx", [new ActionArg('f', BitConverter.SingleToUInt32Bits(0.5f)),
                new ActionArg('v', BitConverter.SingleToUInt32Bits(1f), BitConverter.SingleToUInt32Bits(-2f), BitConverter.SingleToUInt32Bits(3.25f))]),
        ]);
        foreach (var (name, anm2, ev) in new[] { ("a_idle", "m_a_idle", 2), ("a_walk", "", 0), ("b_run", "m_b_run", 1) })
        {
            var r = new SeqRecord
            {
                Name = name, Anm2Name = anm2, Junk = 626, DefaultMode = 1,
                DefaultBlendBits = BitConverter.SingleToUInt32Bits(0.2f), FpsBits = BitConverter.SingleToUInt32Bits(30f),
                StartFrameBits = BitConverter.SingleToUInt32Bits(0f), EndFrameBits = BitConverter.SingleToUInt32Bits(58f),
            };
            for (int k = 0; k < ev; k++) r.Events.Add(new AnimEventDef((ushort)(k * 70), 13000, k == 0 ? 0 : -1, 0, 0xBEEF));
            bank.Records.Add(r);
        }
        return bank;
    }

    [Fact]
    public void SyntheticBankRoundTrip()
    {
        var bank = SampleBank();
        byte[] p42 = bank.RecordsBytes(), p43 = bank.ScriptBytes();
        Assert.Equal(3 * AnimBank.RecordSize + 3 * AnimBank.EventSize + "a_idle\0a_walk\0b_run\0".Length, p42.Length);
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(p42.AsSpan(AnimBank.RecordSize)));   // second name offset
        var back = AnimBank.Parse(p42, p43);
        Assert.Equal(p42, back.RecordsBytes());
        Assert.Equal(p43, back.ScriptBytes());
        Assert.Equal(["m_a_idle", "", "m_b_run"], back.Records.Select(r => r.Anm2Name));
        Assert.Equal(0xBEEF, back.Records[0].Events[1].Junk);
        Assert.Equal("iiis", back.ActionLists[1][0].Signature);
        Assert.Equal((1f, -2f, 3.25f), back.ActionLists[1][1].Args[1].Vector);
        Assert.Equal(0.5f, back.ActionLists[1][1].Args[0].Float);
        Assert.True(back.IsSorted);
    }

    [Fact]
    public void FindUsesStricmpOrder()
    {
        // '_' (0x5F) sorts before letters once folded; unfolded, 'B' (0x42) would sort before '_'
        Assert.True(AnimBank.StrICmp("a_b"u8, "ab"u8) < 0);
        Assert.True(AnimBank.StrICmp("A_B"u8, "ab"u8) < 0);
        Assert.Equal(0, AnimBank.StrICmp("FPP_Unarmed"u8, "fpp_unarmed"u8));
        Assert.True(AnimBank.StrICmp("abc"u8, "ab"u8) > 0);
        var bank = SampleBank();
        Assert.Equal(2, bank.Find("B_RUN"));
        Assert.Equal(0, bank.Find("a_idle"));
        Assert.Equal(-1, bank.Find("a_idl"));
        Assert.Null(bank["zzz"]);
        bank.Records.Reverse();
        Assert.False(bank.IsSorted);
    }

    [Fact]
    public void NegativeEventTime()
    {
        var e = new AnimEventDef(65451, 1001, -1, -1, 0);   // an Event(-17) written as i16 by the compiler
        Assert.True(e.Negative);
        Assert.Equal(-85, e.SignedTime5);
        Assert.Equal(13090.2f, e.Frame, 3);
        var r = new SeqRecord();
        r.Events.Add(e);
        Assert.True(r.FirstEventNegative);
    }

    [Fact]
    public void RefusesWhatItCannotReproduce()
    {
        var bank = SampleBank();
        byte[] p42 = bank.RecordsBytes(), p43 = bank.ScriptBytes();
        var bad = (byte[])p42.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(AnimBank.RecordSize), 3);   // not the sequential layout
        Assert.Throws<AnimBankFormatException>(() => AnimBank.Parse(bad, p43));
        var tag = (byte[])p43.Clone();
        int at = Array.IndexOf(tag, (byte)'s', 8 + 4 + "playstepsfx\0".Length + 4);
        tag[at] = (byte)'x';
        Assert.Throws<AnimBankFormatException>(() => AnimBank.Parse(p42, tag));
        Assert.Throws<AnimBankFormatException>(() => AnimBank.Parse(p42, p43.AsSpan(0, p43.Length - 1)));
    }

    [Fact]
    public void ValueRefDecode()
    {
        var clip = new ValueRef(0xFFFFFFF8_00000012);   // a clip node's +0x88: string constant #0
        Assert.Equal((ValueRef.TypeString, ValueRef.ModeConstant, 0), (clip.Type, clip.Mode, clip.Index));
        Assert.Equal(0x1FFFFFFFUL, clip.Rest);
        var dur = new ValueRef(0xFFFFFFF8_000001D0);   // a BlendTransitionEffect's +0x38: f32 constant #7
        Assert.Equal((ValueRef.TypeFloat, ValueRef.ModeConstant, 7), (dur.Type, dur.Mode, dur.Index));
        var v = new ValueRef(0xFFFFFFF8_00000562);     // string variable #21
        Assert.True(v.IsVariable);
        Assert.Equal(21, v.Index);
        Assert.Equal(v, ValueRef.Make(ValueRef.TypeString, ValueRef.ModeVariable, 21));
        Assert.Equal("f32 const #7", dur.ToString());
    }

    [Fact]
    public void InterfaceHashRule()
    {
        Assert.Equal(0xF2597A00u, InterfaceList.H41("Behavior :: AnimEnd"));
        Assert.Equal(0x12022429u, InterfaceList.H41("Human"));
        Assert.Equal(0x7D0172A8u, InterfaceList.ExpectedHash("* BehaviorPlace :: State :: Type"));
        Assert.Equal(0u, InterfaceList.ExpectedHash("* Banshee / Man_Dialog_Graph / Weapon :: ForceStow"));
    }

    [Fact]
    public void SeqRefParseAndFormat()
    {
        var g = SeqRef.Parse("M_FPP_Unarmed_AnimGraph.SCR@FPP_Unarmed_Stand", SeqRefForm.Graph);
        Assert.Equal("m_fpp_unarmed_animgraph", g.Bank);
        Assert.Equal("FPP_Unarmed_Stand", g.Seq);
        Assert.Equal("m_fpp_unarmed_animgraph.scr@FPP_Unarmed_Stand", g.FormatGraph());
        Assert.Equal("FPP_Unarmed_Stand@m_fpp_unarmed_animgraph.scr", g.FormatGds());

        var d = SeqRef.Parse("walker_grab_front@Walker_Grabs.scr:0.25", SeqRefForm.Gds);
        Assert.Equal(("walker_grabs", "walker_grab_front", "0.25"), (d.Bank, d.Seq, d.Blend));
        Assert.Equal(0.25, d.BlendValue);
        Assert.Equal("walker_grab_front@walker_grabs.scr:0.25", d.ToString());
        Assert.Null(SeqRef.Parse("a@b.scr", SeqRefForm.Gds).Blend);

        Assert.Equal("biter", SeqRef.NormalizeBank("Biter.scr"));
        Assert.Equal("biter.scr2", SeqRef.NormalizeBank("biter.scr2"));
        Assert.Null(SeqRef.TryParseGraph("DialogFullbody :: Single_Seq"));   // a graph variable, not a reference
        Assert.Null(SeqRef.TryParseGraph("@seq"));
        Assert.Throws<FormatException>(() => SeqRef.Parse("no_at_sign", SeqRefForm.Gds));
    }

    // ---- install-backed -------------------------------------------------------------------------------------

    private static RpackFile OpenPack(string file)
    {
        var install = Installs.Require("dltb");
        var path = install.Rpacks().FirstOrDefault(p => Path.GetFileName(p).Equals(file, StringComparison.OrdinalIgnoreCase));
        if (path is null) Assert.Skip($"{file} not in the dltb install");
        return RpackFile.Open(path);
    }

    private static int Index(RpackFile pack, byte type, string name)
    {
        int i = AnimBankPacks.Find(pack, type, name);
        if (i < 0) Assert.Skip($"{name} (0x{type:X2}) not in {Path.GetFileName(pack.Path)}");
        return i;
    }

    [Theory]
    [InlineData("common_anims_pc.rpack", "flare")]
    [InlineData("common_anims_pc.rpack", "anims_player")]
    [InlineData("player_anims_pc.rpack", "m_fpp")]
    [InlineData("player_anims_static_pc.rpack", "anims_man_all")]
    [InlineData("lang_speech_en_pc.rpack", "solid_head_lipsync")]
    public void SequenceBankRoundTrip(string file, string name)
    {
        using var pack = OpenPack(file);
        var parts = AnimBankPacks.ReadParts(pack, Index(pack, AnimBankPacks.TypeScr, name), AnimBankPacks.ScrShape);
        var bank = AnimBank.Parse(parts[0], parts[1]);
        Assert.Equal(parts[0], bank.RecordsBytes());
        Assert.Equal(parts[1], bank.ScriptBytes());
        Assert.True(bank.IsSorted);
        Assert.Empty(bank.TrailingRecords);
        Assert.Empty(bank.TrailingScript);
        foreach (var r in bank.Records.Where((_, k) => k % 97 == 0))
            Assert.Same(r, bank[r.Name.ToUpperInvariant()]);
        Assert.All(bank.Records.SelectMany(r => r.Events),
                   e => Assert.True(e.ActionList == -1 || e.ActionList < bank.ActionLists.Count));
    }

    [Fact]
    public void KnownSeqTrack()
    {
        using var pack = OpenPack("player_anims_pc.rpack");
        var bank = AnimBankPacks.ReadBank(pack, Index(pack, AnimBankPacks.TypeScr, "m_fpp"));
        int i = bank.Find("FPP_Unarmed_Crouch_Idle");
        Assert.True(i >= 0);
        var r = bank.Records[i];
        Assert.Equal("fpp_unarmed_crouch_idle", r.Name);
        Assert.Equal("m_fpp_unarmed_crouch_idle", r.Anm2Name);
        Assert.Equal((30f, 0f, 159f), (r.Fps, r.StartFrame, r.EndFrame));
        Assert.Equal(1u, r.DefaultMode);
        Assert.Equal(0.4f, r.DefaultBlend);
        Assert.Empty(r.Events);
    }

    [Fact]
    public void KnownEventsAndNegativeRecords()
    {
        using var pack = OpenPack("common_anims_pc.rpack");
        var bank = AnimBankPacks.ReadBank(pack, Index(pack, AnimBankPacks.TypeScr, "anims_player"));
        var r = bank["tpp_balls_walkbackward_begin"];
        Assert.NotNull(r);
        Assert.Equal(8105, bank.Find(r.Name));   // the record the notes name
        Assert.Equal("m_tpp_ball_walk_b_start", r.Anm2Name);
        Assert.True(r.FirstEventNegative);
        Assert.Equal(7, r.Events.Count);
        var step = r.Events[2];
        Assert.Equal((70, 13000), (step.Time5, step.Id));
        var call = Assert.Single(bank.ActionLists[step.ActionList]);
        Assert.Equal("playstepsfx", call.Name);
        Assert.Equal(["step_left", "l_foot"], call.Args.Select(a => a.Text));
        Assert.Equal(16, bank.NegativeFirstEvent().Count());
    }

    [Theory]
    [InlineData("ai_debug")]
    [InlineData("hubert_tutorial_01_simple_gesture")]
    [InlineData("player")]
    [InlineData("hmf_main")]
    [InlineData("treninggraph_piotrek")]   // a 4-byte stub
    public void GraphBankRoundTrip(string name)
    {
        using var pack = OpenPack("common_anims_pc.rpack");
        var parts = AnimBankPacks.ReadParts(pack, Index(pack, AnimBankPacks.TypeGraph, name), AnimBankPacks.GraphShape);
        var bank = AnimGraphBank.Parse(parts[0], parts[1]);
        Assert.Equal(parts[1], bank.FixupsBytes());
        Assert.Equal(bank.Fixups.Slots.Count, bank.CheckRelocations());
        Assert.Equal((uint)parts[0].Length, bank.Fixups.PrimarySize);
        Assert.Equal(parts[0].Length == 4, bank.IsStub);
        foreach (var g in bank.Graphs)
            for (int k = 0; k < g.Nodes.Count; k++) Assert.Equal(k, g.Nodes[k].Index);
    }

    [Fact]
    public void GraphClipNodesAndTransitions()
    {
        using var pack = OpenPack("common_anims_pc.rpack");
        var bank = AnimBankPacks.ReadGraphBank(pack, Index(pack, AnimBankPacks.TypeGraph, "hubert_tutorial_01_simple_gesture"));
        var g = Assert.Single(bank.Graphs);
        Assert.Equal(("hubert_tutorial_01_simple_gesture", "Generic_Man_Dialog"), (g.BankName, g.GraphName));
        Assert.Equal(6, g.Nodes.Count);
        var idle = g.Nodes[0];
        Assert.Equal(("Idle Normal", AnimGraphBank.ClassClip, 3u), (idle.Name, idle.ClassId, idle.NodeType));
        Assert.Equal("npc_dialogs.scr@survivor_dialog_normal_idle_01", idle.Clip!.Sequence.Text);
        Assert.Equal(idle.Clip.Sequence.Text, idle.Clip.SequenceText);
        Assert.Equal(new SeqRef("npc_dialogs", "survivor_dialog_normal_idle_01", SeqRefForm.Graph), idle.Clip.SeqRef);
        Assert.Equal(1f, idle.Clip.Speed.Float);

        var sm = g.Nodes.Single(n => n.StateMachine is not null).StateMachine!;
        Assert.Equal([0, 1], sm.States.Select(s => s.NodeIndex));
        var toGesture = sm.Transitions.Single(t => t.From == 0 && t.To == 1);
        Assert.Equal("Idle  to gesture", g.Nodes[toGesture.EffectNode].Name);
        Assert.Equal(0.3f, toGesture.Duration!.Float);
        Assert.Equal(["Behavior :: AnimEnd", "Behavior :: gesture_01"], g.Events.Names!);
        Assert.Equal(g.Events.Names!.Select(InterfaceList.ExpectedHash), g.Events.Hashes);
    }

    [Fact]
    public void PlayerGraphClipStringsAndVariables()
    {
        using var pack = OpenPack("common_anims_pc.rpack");
        var bank = AnimBankPacks.ReadGraphBank(pack, Index(pack, AnimBankPacks.TypeGraph, "player"));
        var main = bank.Graphs.Single(x => x.GraphName == "player_main");
        Assert.Equal("player", main.BankName);
        var idle = main.Nodes[8];                                     // the notes' first in-game test target
        Assert.Equal("Unarmed Idle", idle.Name);
        Assert.Equal("m_fpp_unarmed_animgraph.scr@FPP_Unarmed_Stand", idle.Clip!.Sequence.Text);
        var single = main.Nodes[13].Clip!.Sequence;
        Assert.Equal(GraphValueKind.Variable, single.Kind);
        Assert.Equal("DialogFullbody :: Single_Seq", single.VariableName);
        Assert.Equal(InterfaceList.H41("DialogFullbody :: Single_Seq"), single.VariableHash);
        Assert.Equal(main.Variables.Count, main.Variables.Names!.Length);
    }

    [Fact]
    public void CustomResourceHeader()
    {
        using var pack = OpenPack("common_anims_pc.rpack");
        var parts = AnimBankPacks.ReadParts(pack, Index(pack, AnimBankPacks.TypeCustom, "anim_commands_example"), AnimBankPacks.CustomShape);
        var res = AnimCustomResource.Parse(parts[0], parts[1], parts[2], parts[3]);
        Assert.Equal(("anim_commands_example", "engine", "CAnimCommandsResourceBaker", 16UL),
                     (res.BankName, res.BakerDll, res.BakerFunc, res.BakerVersion));
        Assert.Equal(parts[1], res.HeaderFixupsBytes());
        Assert.Same(parts[2], res.BodyImage);

        var seq = AnimBankPacks.ReadCustom(pack, Index(pack, AnimBankPacks.TypeCustom, "gameplay_anims"));
        Assert.Equal(("CSequenceBankBaker", 27UL), (seq.BakerFunc, seq.BakerVersion));
    }
}
