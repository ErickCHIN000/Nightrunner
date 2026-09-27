using Nightrunner.Core.Mesh;
using Nightrunner.Core.Mesh.ClassReader;

namespace Nightrunner.Core.Anim;

/// <summary>
/// AnimGraphBank (resource type 0x47: part 0x47 ClassReader image + part 0x48 ClassReader fixups, version 140).
/// Read-only: the fixups re-serialise byte for byte (<see cref="FixupsBytes"/>), the image is kept as given, and the
/// graphs are extracted from it — never rewritten.
/// </summary>
/// <remarks>
/// A bank is a header object (class 0x3C) plus 0–6 root graphs (class 0x0E, <c>CRootNodeTemplate</c>; the loader
/// hands every one to <c>RegisterTemplateBank</c>). Root layout, measured on all 57 shipped roots:
/// <code>
/// +0x38 str  bank name           +0x40 str  graph name          +0x48 u32 entry node (C: the top node's index)
/// +0x50 ptr  ValueTable (0x0F)   +0x58 u32 variable count       +0x60 ptr  ValueTable (C: variable defaults)
/// +0x68 ptr  variable hashes     +0xC0 u32 event count          +0xC8 ptr  event hashes
/// +0xD0 u32  flag count          +0xE0 ptr  flag hashes         +0xE8 u32  enum count     +0xF0 ptr enum hashes
/// +0x110 u32 namespace count     +0x118 ptr namespace hashes
/// +0x120 u32 node count          +0x128 ptr node* array (class 0x10)
/// +0x1B0/+0x1C0/+0x1D0/+0x1E0/+0x1F0  debug names of vars/events/flags/enums/namespaces {ptr, u32 n, u32 cap}
/// +0x200 ptr node debug names (class 0x3D: {names, n, cap, paths, n, cap})
/// node: +0x08 u32 index (== position in the array), +0x0C u32 ENodeType (1:1 with the class id)
/// </code>
/// "str" is an 8-byte packed string: a tagged relocation slot pointing at the characters (u32 length pair at −8), or
/// inline characters whose last byte is 7 − length.
/// </remarks>
public sealed class AnimGraphBank
{
    public const byte PartImage = 0x47, PartFixups = 0x48;

    public const uint ClassRaw = 0x00, ClassClip = 0x05, ClassRoot = 0x0E, ClassValueTable = 0x0F, ClassPointers = 0x10,
        ClassNameHashList = 0x11, ClassStateMachine = 0x1D, ClassState = 0x1E, ClassTransition = 0x1F,
        ClassBlendTransitionEffect = 0x21, ClassHeader = 0x3C, ClassNodeNames = 0x3D;

    /// <summary>Class ids by name (doc §2.5 table plus the classes read here); the rest are listed as hex.</summary>
    public static readonly IReadOnlyDictionary<uint, string> ClassNames = new Dictionary<uint, string>
    {
        [0x00] = "Raw", [0x05] = "Clip", [0x09] = "ClipEventBridge", [0x0A] = "ClipEventBridgeOp",
        [0x0C] = "StringArray", [0x0E] = "Root", [0x0F] = "ValueTable", [0x10] = "PointerArray",
        [0x11] = "NameHashList", [0x18] = "Blender", [0x19] = "Graph", [0x1A] = "Selector", [0x1D] = "StateMachine",
        [0x1E] = "State", [0x1F] = "Transition", [0x21] = "BlendTransitionEffect", [0x3C] = "BankHeader",
        [0x3D] = "NodeNames", [0x3F] = "GraphCondition", [0x43] = "ExternalSource", [0x44] = "GeneratorBinding",
        [0x54] = "BlendSpace", [0x56] = "MotionWarping",
    };

    public static string ClassName(uint id) => ClassNames.TryGetValue(id, out var n) ? n : $"0x{id:X2}";

    public Image Image { get; }
    public Fixups Fixups => Image.Fixups;
    public List<AnimGraph> Graphs { get; } = [];

    /// <summary>A 4-byte image with no root: 20 of the 70 shipped banks.</summary>
    public bool IsStub => Graphs.Count == 0;

    private AnimGraphBank(Image image) => Image = image;

    public static AnimGraphBank Parse(byte[] image, ReadOnlySpan<byte> fixups)
    {
        try
        {
            var bank = new AnimGraphBank(Image.FromParts(image, fixups));
            foreach (var (index, rec) in bank.Fixups.RecordsOfClass(ClassRoot))
                bank.Graphs.Add(new AnimGraph(bank.Image, index, (int)rec.Offset));
            return bank;
        }
        catch (Exception e) when (e is MeshFormatException or MeshUnsupportedException)
        {
            throw new AnimBankFormatException($"graph bank: {e.Message}", e);
        }
    }

    /// <summary>Part 0x48 re-serialised from the parsed fixups.</summary>
    public byte[] FixupsBytes() => Fixups.ToBytes();

    /// <summary>Resolves every relocation slot (all must land inside the image); returns how many there are.</summary>
    public int CheckRelocations()
    {
        try
        {
            foreach (var s in Fixups.Slots) Image.Pointer((int)s.Offset);
            return Fixups.Slots.Count;
        }
        catch (Exception e) when (e is MeshFormatException or MeshUnsupportedException)
        {
            throw new AnimBankFormatException($"graph bank: {e.Message}", e);
        }
    }

    /// <summary>Every clip node of every graph.</summary>
    public IEnumerable<(AnimGraph Graph, GraphNode Node)> Clips() =>
        Graphs.SelectMany(g => g.Nodes.Where(n => n.Clip is not null).Select(n => (g, n)));

    /// <summary>An 8-byte packed string (see the class remarks); null for a null pointer or an all-zero word.</summary>
    internal static string? PackedString(Image img, int off)
    {
        if (img.IsSlot(off)) return img.Target(off) is { } t ? AnimBank.Latin1.GetString(img.CString(t)) : null;
        ulong raw = img.U64(off);
        if (raw == 0) return null;
        int last = (int)(raw >> 56);
        if (last > 7) throw new AnimBankFormatException($"0x{off:X}: word 0x{raw:X16} is neither a slot nor an inline string");
        var chars = img.Span(off, 7 - last);
        if (chars.Contains((byte)0)) throw new AnimBankFormatException($"0x{off:X}: inline string 0x{raw:X16} holds a NUL");
        return AnimBank.Latin1.GetString(chars);
    }
}

/// <summary>One root graph (class 0x0E) of a bank.</summary>
public sealed class AnimGraph
{
    public const int StateSize = 0x48, TransitionSize = 0x40;

    public int RecordIndex { get; }
    public int Offset { get; }
    public string? BankName { get; }
    public string? GraphName { get; }
    public uint EntryNode { get; }
    public ValueTable Values { get; }
    public int? VariableDefaultsOffset { get; }
    public InterfaceList Variables { get; }
    public InterfaceList Events { get; }
    public InterfaceList Flags { get; }
    public InterfaceList Enums { get; }
    public InterfaceList Namespaces { get; }
    public List<GraphNode> Nodes { get; } = [];
    public string[]? NodeNames { get; }
    public string[]? NodePaths { get; }

    public IEnumerable<InterfaceList> Interface => [Variables, Events, Flags, Enums, Namespaces];

    internal AnimGraph(Image img, int recordIndex, int r)
    {
        RecordIndex = recordIndex;
        Offset = r;
        BankName = AnimGraphBank.PackedString(img, r + 0x38);
        GraphName = AnimGraphBank.PackedString(img, r + 0x40);
        EntryNode = img.U32(r + 0x48);
        Values = new ValueTable(img, img.Target(r + 0x50) ?? throw new AnimBankFormatException($"root 0x{r:X}: no value table"));
        VariableDefaultsOffset = img.Target(r + 0x60);
        Variables = List(img, r, "vars", 0x58, 0x68, 0x1B0);
        Events = List(img, r, "events", 0xC0, 0xC8, 0x1C0);
        Flags = List(img, r, "flags", 0xD0, 0xE0, 0x1D0);
        Enums = List(img, r, "enums", 0xE8, 0xF0, 0x1E0);
        Namespaces = List(img, r, "namespaces", 0x110, 0x118, 0x1F0);

        uint count = img.U32(r + 0x120);
        if (img.Target(r + 0x200) is { } nn)
        {
            NodeNames = Strings(img, nn, "node names");
            NodePaths = Strings(img, nn + 0x10, "node paths");
        }
        if (count > 0)
        {
            int arr = img.Target(r + 0x128) ?? throw new AnimBankFormatException($"root 0x{r:X}: {count} nodes, null array");
            for (int i = 0; i < count; i++)
            {
                int n = img.Target(arr + i * 8) ?? throw new AnimBankFormatException($"root 0x{r:X}: node {i} is null");
                var rec = img.RecordAt(n) is { } k ? img.Records[k] : throw new AnimBankFormatException(
                    $"root 0x{r:X}: node {i} at 0x{n:X} is not a record start");
                if (img.U32(n + 0x08) != i)
                    throw new AnimBankFormatException($"root 0x{r:X}: node {i} at 0x{n:X} says it is node {img.U32(n + 0x08)}");
                Nodes.Add(new GraphNode(i, n, rec.ClassId, img.U32(n + 0x0C),
                    NodeNames is { } names && i < names.Length ? names[i] : null,
                    NodePaths is { } paths && i < paths.Length ? paths[i] : null));
            }
        }
        foreach (var node in Nodes)
        {
            switch (node.ClassId)
            {
                case AnimGraphBank.ClassClip:
                    node.Clip = new ClipInfo(
                        Resolve(img, node.Offset + 0x88),
                        AnimGraphBank.PackedString(img, node.Offset + 0x80),
                        Resolve(img, node.Offset + 0x30),
                        Resolve(img, node.Offset + 0x50));
                    break;
                case AnimGraphBank.ClassBlendTransitionEffect:
                    node.BlendDuration = Resolve(img, node.Offset + 0x38);
                    break;
                case AnimGraphBank.ClassStateMachine:
                    node.StateMachine = Machine(img, node);
                    break;
            }
        }
    }

    public GraphValue Resolve(ValueRef r) => Values.Resolve(r, Variables);

    private GraphValue Resolve(Image img, int off) => Resolve(new ValueRef(img.U64(off)));

    public GraphNode? Node(int index) => index >= 0 && index < Nodes.Count ? Nodes[index] : null;

    private StateMachineInfo Machine(Image img, GraphNode node)
    {
        int o = node.Offset;
        uint ns = img.U32(o + 0x38), nt = img.U32(o + 0x3C);
        var sm = new StateMachineInfo();
        int? st = img.Target(o + 0x28), tr = img.Target(o + 0x30);
        CheckArray(img, st, ns, AnimGraphBank.ClassState, o, "states");
        CheckArray(img, tr, nt, AnimGraphBank.ClassTransition, o, "transitions");
        for (int i = 0; i < ns; i++)
        {
            int s = st!.Value + i * StateSize;
            sm.States.Add(new SmState(i, s, (int)img.U32(s), (int)img.U32(s + 4)));
        }
        for (int i = 0; i < nt; i++)
        {
            int t = tr!.Value + i * TransitionSize;
            int effect = (int)img.U32(t + 0x18);
            var fx = Node(effect);
            sm.Transitions.Add(new SmTransition(i, t, (int)img.U32(t), (int)img.U32(t + 4), effect,
                img.Target(t + 0x10), img.Raw(t, TransitionSize),
                fx?.ClassId == AnimGraphBank.ClassBlendTransitionEffect ? Resolve(img, fx.Offset + 0x38) : null));
        }
        return sm;
    }

    private static void CheckArray(Image img, int? at, uint count, uint cls, int owner, string what)
    {
        if (count == 0) return;
        if (at is not { } a) throw new AnimBankFormatException($"state machine 0x{owner:X}: {count} {what}, null pointer");
        if (img.RecordAt(a) is not { } k || img.Records[k].ClassId != cls || img.Records[k].Count != count)
            throw new AnimBankFormatException(
                $"state machine 0x{owner:X}: {what} at 0x{a:X} is not a class 0x{cls:X2} record of {count}");
    }

    private static InterfaceList List(Image img, int r, string kind, int countAt, int hashAt, int namesAt)
    {
        uint count = img.U32(r + countAt);
        var hashes = Array.Empty<uint>();
        if (img.Target(r + hashAt) is { } hl)
        {
            ulong n = img.U64(hl + 8);
            hashes = n == 0 ? [] : img.U32s(img.Target(hl) ?? throw new AnimBankFormatException(
                $"root 0x{r:X}: {kind} hash list 0x{hl:X} has {n} entries and no data"), (int)n);
        }
        if (hashes.Length != count)
            throw new AnimBankFormatException($"root 0x{r:X}: {kind} count {count} but {hashes.Length} hashes");
        string[]? names = null;
        if (img.U32(r + namesAt + 8) > 0)
        {
            names = Strings(img, r + namesAt, kind);
            if (names.Length != count)
                throw new AnimBankFormatException($"root 0x{r:X}: {count} {kind} but {names.Length} debug names");
        }
        return new InterfaceList(kind, hashes, names);
    }

    /// <summary><c>{packed string* data, u32 count, u32 capacity}</c> at <paramref name="at"/>.</summary>
    private static string[] Strings(Image img, int at, string what)
    {
        uint n = img.U32(at + 8);
        if (n == 0) return [];
        int p = img.Target(at) ?? throw new AnimBankFormatException($"0x{at:X}: {n} {what}, null pointer");
        var s = new string[n];
        for (int i = 0; i < n; i++) s[i] = AnimGraphBank.PackedString(img, p + i * 8) ?? "";
        return s;
    }

    public override string ToString() => $"{BankName}/{GraphName}: {Nodes.Count} nodes";
}

/// <summary>A node of a root's flat node array.</summary>
public sealed class GraphNode(int index, int offset, uint classId, uint nodeType, string? name, string? path)
{
    public int Index { get; } = index;
    public int Offset { get; } = offset;
    public uint ClassId { get; } = classId;

    /// <summary>ENodeType (+0x0C); one value per class in the corpus.</summary>
    public uint NodeType { get; } = nodeType;
    public string? Name { get; } = name;
    public string? Path { get; } = path;
    public string ClassName => AnimGraphBank.ClassName(ClassId);

    public ClipInfo? Clip { get; internal set; }
    public StateMachineInfo? StateMachine { get; internal set; }

    /// <summary>BlendTransitionEffect +0x38: blend duration in seconds.</summary>
    public GraphValue? BlendDuration { get; internal set; }

    public override string ToString() => $"#{Index} {ClassName}{(Name is null ? "" : $" '{Name}'")}";
}

/// <summary>Clip node (class 0x05, 0xC0 bytes + its string).</summary>
/// <param name="Sequence">+0x88: the sequence, <c>"&lt;bank&gt;.scr@&lt;Seq&gt;"</c> or a variable <c>"… :: Seq"</c>.</param>
/// <param name="SequenceText">+0x80: copy of the constant string (packed string).</param>
/// <param name="Speed">+0x30.</param>
/// <param name="Namespace">+0x50: the bind namespace (<c>Human</c>, <c>Prop01</c>, <c>Weapon</c>…).</param>
public sealed record ClipInfo(GraphValue Sequence, string? SequenceText, GraphValue Speed, GraphValue Namespace)
{
    /// <summary>The constant sequence as a reference, when it is one.</summary>
    public SeqRef? SeqRef => Sequence.Text is { } s ? SeqRef.TryParseGraph(s) : null;
}

public sealed class StateMachineInfo
{
    public List<SmState> States { get; } = [];
    public List<SmTransition> Transitions { get; } = [];
}

/// <summary>A state (0x48 bytes): +0x00 its index, +0x04 the node it plays.</summary>
public readonly record struct SmState(int Index, int Offset, int StateIndex, int NodeIndex);

/// <summary>
/// A transition (0x40 bytes): +0x00 from (−1 = any), +0x04 to, +0x10 GraphCondition* (<c>parser::program</c> bytecode,
/// not decoded), +0x18 effect node index. The other words are kept in <see cref="Raw"/>, meaning (C).
/// </summary>
public sealed record SmTransition(int Index, int Offset, int From, int To, int EffectNode, int? ConditionOffset,
                                  byte[] Raw, GraphValue? Duration);
