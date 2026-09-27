using System.Buffers.Binary;
using System.Text;

namespace Nightrunner.Core.Anim;

/// <summary>Thrown when an animation bank (0x42/0x43, 0x47/0x48, 0x49/0x4A) is not in a shape this reader knows.</summary>
public sealed class AnimBankFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// AnimationScr (resource type 0x42, parts 0x42 + 0x43): a compiled sequence bank — the <c>SeqTrack</c> records a
/// graph clip node or a <c>.gds</c> row names as <c>&lt;bank&gt;.scr@&lt;Seq&gt;</c>. Read and byte-exact re-serialise.
/// </summary>
/// <remarks>
/// <code>
/// 0x42 (the loader patches it in place; 0x43 is NOT a ClassReader fixups stream)
///   SeqRecord[R], 0x38 each, sorted by _stricmp(name)
///     +0x00 u32 nameOff    into the name block (sequential in record order in every shipped bank)
///     +0x04 u32 junk       compiler heap garbage, kept verbatim
///     +0x08 u64 anm2       0 in file (runtime: CAnimFileHeader*)
///     +0x10 u32 defaultMode     +0x14 f32 defaultBlend
///     +0x18 f32 fps  +0x1C f32 startFrame  +0x20 f32 endFrame   (end &lt; start → reverse)
///     +0x24 u32 pad  +0x28 u64 eventsPtr (0)  +0x30 u32 eventCount  +0x34 u32 pad
///   AnimEventDef[Σ eventCount], 12 each, record order, contiguous:
///     u16 time5 (frame·5, clip-local; read as u16 by the runtime), u16 id, i32 actionList (−1 none), i16 param,
///     u16 junk
///   char name[R][] NUL-terminated; the part ends there
/// 0x43
///   u32 R; u32 G; G × { u32 n; ActionParams[n] }; char anm2Name[R][] (lowercase, no ".anm2"; "" = placeholder)
///   ActionParams: char name[]; u32 argc; argc × { char tag 'f'|'i'|'s'|'v'; f32 | i32 | char[] | 3 × f32 }
/// </code>
/// Strings are kept as Latin-1 (one char per byte), so every name survives a round trip whatever its bytes. Name
/// offsets are recomputed on write; a bank whose offsets are not the sequential layout is refused, since writing it
/// back would not reproduce it. Bytes past the parsed structure are kept in <see cref="TrailingRecords"/> /
/// <see cref="TrailingScript"/> (none in the shipped corpus).
/// </remarks>
public sealed class AnimBank
{
    public const byte PartRecords = 0x42, PartScript = 0x43;
    public const int RecordSize = 0x38, EventSize = 12;
    public const int MaxRecords = 1_000_000, MaxLists = 10_000_000, MaxArgs = 1_000, MaxString = 65536;
    public const uint NullName = 0xFFFFFFFF;

    internal static readonly Encoding Latin1 = Encoding.Latin1;

    public List<SeqRecord> Records { get; } = [];

    /// <summary>The 0x43 action lists; <see cref="AnimEventDef.ActionList"/> indexes this.</summary>
    public List<List<ActionParams>> ActionLists { get; } = [];

    public byte[] TrailingRecords { get; set; } = [];
    public byte[] TrailingScript { get; set; } = [];

    public int EventCount => Records.Sum(r => r.Events.Count);

    /// <summary>True when the records are in the engine's <c>_stricmp</c> order, the precondition of <see cref="Find"/>.</summary>
    public bool IsSorted
    {
        get
        {
            for (int i = 1; i < Records.Count; i++)
                if (StrICmp(Records[i - 1].NameBytes, Records[i].NameBytes) > 0) return false;
            return true;
        }
    }

    public static AnimBank Parse(ReadOnlySpan<byte> records, ReadOnlySpan<byte> script)
    {
        var bank = new AnimBank();
        var anm2 = ParseScript(script, bank);
        int r = anm2.Count;

        long eventsAt = (long)r * RecordSize;
        if (eventsAt > records.Length)
            throw new AnimBankFormatException($"0x42: {r} records need {eventsAt} bytes, part has {records.Length}");
        long events = 0;
        for (int i = 0; i < r; i++) events += U32(records, i * RecordSize + 0x30);
        long namesAt = eventsAt + events * EventSize;
        if (namesAt > records.Length)
            throw new AnimBankFormatException($"0x42: {events} events do not fit in {records.Length} bytes");

        int ev = (int)eventsAt, name = (int)namesAt;
        for (int i = 0; i < r; i++)
        {
            int o = i * RecordSize;
            uint nameOff = U32(records, o);
            if (nameOff != (uint)(name - namesAt))
                throw new AnimBankFormatException(
                    $"0x42 record {i}: name offset 0x{nameOff:X} is not the sequential layout (expected 0x{name - namesAt:X})");
            var nb = CString(records, name, "0x42 name");
            name += nb.Length + 1;
            var rec = new SeqRecord
            {
                Name = Latin1.GetString(nb),
                Anm2Name = anm2[i],
                Junk = U32(records, o + 0x04),
                Anm2Raw = U64(records, o + 0x08),
                DefaultMode = U32(records, o + 0x10),
                DefaultBlendBits = U32(records, o + 0x14),
                FpsBits = U32(records, o + 0x18),
                StartFrameBits = U32(records, o + 0x1C),
                EndFrameBits = U32(records, o + 0x20),
                Pad24 = U32(records, o + 0x24),
                EventsRaw = U64(records, o + 0x28),
                Pad34 = U32(records, o + 0x34),
            };
            int n = (int)U32(records, o + 0x30);
            for (int k = 0; k < n; k++, ev += EventSize)
                rec.Events.Add(new AnimEventDef(U16(records, ev), U16(records, ev + 2),
                    BinaryPrimitives.ReadInt32LittleEndian(records[(ev + 4)..]),
                    BinaryPrimitives.ReadInt16LittleEndian(records[(ev + 8)..]), U16(records, ev + 10)));
            bank.Records.Add(rec);
        }
        bank.TrailingRecords = records[name..].ToArray();
        return bank;
    }

    /// <summary>Part 0x43; fills <see cref="ActionLists"/> and <see cref="TrailingScript"/>, returns the anm2 names.</summary>
    private static List<string> ParseScript(ReadOnlySpan<byte> b, AnimBank bank)
    {
        if (b.Length < 8) throw new AnimBankFormatException($"0x43 shorter than its 8-byte header ({b.Length})");
        uint r = U32(b, 0), g = U32(b, 4);
        if (r > MaxRecords) throw new AnimBankFormatException($"0x43: record count {r} exceeds the bound {MaxRecords}");
        if (g > MaxLists) throw new AnimBankFormatException($"0x43: action list count {g} exceeds the bound {MaxLists}");
        int p = 8;
        for (int i = 0; i < g; i++)
        {
            uint n = Need32(b, ref p, "action list size");
            var list = new List<ActionParams>((int)Math.Min(n, 1024));
            for (int k = 0; k < n; k++)
            {
                var nm = CString(b, p, "action name");
                p += nm.Length + 1;
                uint argc = Need32(b, ref p, "argc");
                if (argc > MaxArgs) throw new AnimBankFormatException($"0x43: argc {argc} at 0x{p - 4:X} exceeds {MaxArgs}");
                var args = new List<ActionArg>((int)argc);
                for (int a = 0; a < argc; a++)
                {
                    if (p >= b.Length) throw new AnimBankFormatException("0x43: truncated argument");
                    char tag = (char)b[p++];
                    switch (tag)
                    {
                        case 'f' or 'i':
                            args.Add(new ActionArg(tag, Need32(b, ref p, "argument")));
                            break;
                        case 'v':
                            uint x = Need32(b, ref p, "vector"), y = Need32(b, ref p, "vector"), z = Need32(b, ref p, "vector");
                            args.Add(new ActionArg(tag, x, y, z));
                            break;
                        case 's':
                            var s = CString(b, p, "string argument");
                            p += s.Length + 1;
                            args.Add(new ActionArg(tag, Text: Latin1.GetString(s)));
                            break;
                        default:
                            throw new AnimBankFormatException($"0x43: unknown argument tag 0x{(byte)tag:X2} at 0x{p - 1:X}");
                    }
                }
                list.Add(new ActionParams(Latin1.GetString(nm), args));
            }
            bank.ActionLists.Add(list);
        }
        var names = new List<string>((int)r);
        for (int i = 0; i < r; i++)
        {
            var s = CString(b, p, "anm2 name");
            p += s.Length + 1;
            names.Add(Latin1.GetString(s));
        }
        bank.TrailingScript = b[p..].ToArray();
        return names;
    }

    /// <summary>Part 0x42 as the compiler lays it out.</summary>
    public byte[] RecordsBytes()
    {
        var names = Records.Select(r => r.NameBytes).ToList();
        int events = EventCount;
        int size = Records.Count * RecordSize + events * EventSize + names.Sum(n => n.Length + 1) + TrailingRecords.Length;
        var out_ = new byte[size];
        var s = out_.AsSpan();
        int ev = Records.Count * RecordSize, nameAt = ev + events * EventSize, nameOff = 0;
        for (int i = 0; i < Records.Count; i++)
        {
            var r = Records[i];
            int o = i * RecordSize;
            W32(s, o, (uint)nameOff);
            W32(s, o + 0x04, r.Junk);
            W64(s, o + 0x08, r.Anm2Raw);
            W32(s, o + 0x10, r.DefaultMode);
            W32(s, o + 0x14, r.DefaultBlendBits);
            W32(s, o + 0x18, r.FpsBits);
            W32(s, o + 0x1C, r.StartFrameBits);
            W32(s, o + 0x20, r.EndFrameBits);
            W32(s, o + 0x24, r.Pad24);
            W64(s, o + 0x28, r.EventsRaw);
            W32(s, o + 0x30, (uint)r.Events.Count);
            W32(s, o + 0x34, r.Pad34);
            foreach (var e in r.Events)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(s[ev..], e.Time5);
                BinaryPrimitives.WriteUInt16LittleEndian(s[(ev + 2)..], e.Id);
                BinaryPrimitives.WriteInt32LittleEndian(s[(ev + 4)..], e.ActionList);
                BinaryPrimitives.WriteInt16LittleEndian(s[(ev + 8)..], e.Param);
                BinaryPrimitives.WriteUInt16LittleEndian(s[(ev + 10)..], e.Junk);
                ev += EventSize;
            }
            names[i].CopyTo(s[(nameAt + nameOff)..]);
            nameOff += names[i].Length + 1;
        }
        TrailingRecords.CopyTo(s[(nameAt + nameOff)..]);
        return out_;
    }

    /// <summary>Part 0x43.</summary>
    public byte[] ScriptBytes()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((uint)Records.Count);
        w.Write((uint)ActionLists.Count);
        foreach (var list in ActionLists)
        {
            w.Write((uint)list.Count);
            foreach (var a in list)
            {
                WriteCString(w, a.Name);
                w.Write((uint)a.Args.Count);
                foreach (var g in a.Args)
                {
                    w.Write((byte)g.Tag);
                    switch (g.Tag)
                    {
                        case 'f' or 'i': w.Write(g.A); break;
                        case 'v': w.Write(g.A); w.Write(g.B); w.Write(g.C); break;
                        case 's': WriteCString(w, g.Text ?? ""); break;
                        default: throw new AnimBankFormatException($"action '{a.Name}': argument tag '{g.Tag}' cannot be written");
                    }
                }
            }
        }
        foreach (var r in Records) WriteCString(w, r.Anm2Name);
        w.Write(TrailingScript);
        return ms.ToArray();
    }

    /// <summary>
    /// Index of the record named <paramref name="name"/>, found the way the engine finds it (binary search in
    /// <c>_stricmp</c> order, 0x180322f60), or −1. An unsorted bank gives what the engine would: possibly −1.
    /// </summary>
    public int Find(string name)
    {
        var key = Latin1.GetBytes(name);
        int lo = 0, hi = Records.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            int c = StrICmp(Records[mid].NameBytes, key);
            if (c == 0) return mid;
            if (c < 0) lo = mid + 1; else hi = mid - 1;
        }
        return -1;
    }

    public SeqRecord? this[string name] => Find(name) is var i and >= 0 ? Records[i] : null;

    /// <summary>Records whose first event reads as a "negative" time (u16 ≥ 0x8000): their events never fire.</summary>
    public IEnumerable<(int Index, SeqRecord Record)> NegativeFirstEvent() =>
        Records.Select((r, i) => (i, r)).Where(t => t.r.FirstEventNegative);

    /// <summary>MSVC <c>_stricmp</c> in the C locale: ASCII A–Z folded to lower case, bytes compared unsigned.</summary>
    public static int StrICmp(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int x = Fold(a[i]), y = Fold(b[i]);
            if (x != y) return x - y;
        }
        return a.Length - b.Length;
    }

    private static int Fold(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? c + 32 : c;

    private static void WriteCString(BinaryWriter w, string s)
    {
        var b = Latin1.GetBytes(s);
        if (b.Contains((byte)0)) throw new AnimBankFormatException($"string '{s}' contains NUL and cannot be written");
        w.Write(b);
        w.Write((byte)0);
    }

    internal static byte[] CString(ReadOnlySpan<byte> b, int p, string what)
    {
        if (p > b.Length) throw new AnimBankFormatException($"{what} at 0x{p:X} is past the end ({b.Length})");
        int n = b[p..Math.Min(b.Length, p + MaxString)].IndexOf((byte)0);
        if (n < 0) throw new AnimBankFormatException($"unterminated {what} at 0x{p:X}");
        return b.Slice(p, n).ToArray();
    }

    private static uint Need32(ReadOnlySpan<byte> b, ref int p, string what)
    {
        if (p + 4 > b.Length) throw new AnimBankFormatException($"0x43: truncated {what} at 0x{p:X}");
        uint v = U32(b, p);
        p += 4;
        return v;
    }

    internal static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    internal static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    internal static ulong U64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);
    private static void W32(Span<byte> b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b[o..], v);
    private static void W64(Span<byte> b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b[o..], v);
}

/// <summary>One 0x38-byte <c>SeqTrack</c> record plus its events and its 0x43 anm2 name. Floats are kept as bits.</summary>
public sealed class SeqRecord
{
    public string Name { get; set; } = "";

    /// <summary>The clip (<c>&lt;name&gt;.anm2</c>, no extension); "" is a placeholder (a SeqTrackAlias with no target).</summary>
    public string Anm2Name { get; set; } = "";

    /// <summary>+0x04: compiler heap garbage (a few distinct values), kept verbatim.</summary>
    public uint Junk { get; set; }
    public ulong Anm2Raw { get; set; }

    /// <summary>+0x10: SeqTrack argument 6, used when the caller passes −1 (0, 1 or 3 in the corpus).</summary>
    public uint DefaultMode { get; set; }
    public uint DefaultBlendBits { get; set; }
    public uint FpsBits { get; set; }
    public uint StartFrameBits { get; set; }
    public uint EndFrameBits { get; set; }
    public uint Pad24 { get; set; }
    public ulong EventsRaw { get; set; }
    public uint Pad34 { get; set; }
    public List<AnimEventDef> Events { get; } = [];

    public float DefaultBlend => BitConverter.UInt32BitsToSingle(DefaultBlendBits);
    public float Fps => BitConverter.UInt32BitsToSingle(FpsBits);
    public float StartFrame => BitConverter.UInt32BitsToSingle(StartFrameBits);
    public float EndFrame => BitConverter.UInt32BitsToSingle(EndFrameBits);
    public bool Reverse => EndFrame < StartFrame;

    /// <summary>
    /// No clip (a SeqTrackAlias whose target is missing). Every such record in the corpus also has fps −1, mode 0,
    /// blend/start/end 0 and no events.
    /// </summary>
    public bool IsPlaceholder => Anm2Name.Length == 0;

    internal byte[] NameBytes => AnimBank.Latin1.GetBytes(Name);

    /// <summary>
    /// The compiler wrote a negative <c>Event(-n)</c> as i16 and sorted it first; the runtime reads u16, so the cursor
    /// stalls at ≥ 13,090 frames and none of this record's events fire.
    /// </summary>
    public bool FirstEventNegative => Events.Count > 0 && Events[0].Negative;

    public override string ToString() => $"{Name} → {Anm2Name} {Fps} fps [{StartFrame}, {EndFrame}] {Events.Count} ev";
}

/// <summary>A 12-byte timeline event. <see cref="Time5"/> is frame·5, clip-local.</summary>
public readonly record struct AnimEventDef(ushort Time5, ushort Id, int ActionList, short Param, ushort Junk)
{
    public const int NoActions = -1;

    /// <summary>The compiler's i16 view of the time (a negative value is an authoring error, see <see cref="Negative"/>).</summary>
    public short SignedTime5 => (short)Time5;
    public bool Negative => Time5 >= 0x8000;

    /// <summary>The frame the runtime fires at (u16 read, ×0.2).</summary>
    public float Frame => Time5 * 0.2f;
}

/// <summary>One <c>IAnimAction</c> call of an action list (0x1803256b0): lowercase keyword and typed arguments.</summary>
public sealed record ActionParams(string Name, List<ActionArg> Args)
{
    /// <summary>The argument tags in order, e.g. <c>ssii</c> for PlayStepSFx.</summary>
    public string Signature => new(Args.Select(a => a.Tag).ToArray());
}

/// <summary>An action argument: 'f' / 'i' keep the 32-bit word in <see cref="A"/>, 'v' three words, 's' the text.</summary>
public readonly record struct ActionArg(char Tag, uint A = 0, uint B = 0, uint C = 0, string? Text = null)
{
    public float Float => BitConverter.UInt32BitsToSingle(A);
    public int Int => (int)A;
    public (float X, float Y, float Z) Vector =>
        (BitConverter.UInt32BitsToSingle(A), BitConverter.UInt32BitsToSingle(B), BitConverter.UInt32BitsToSingle(C));

    public override string ToString() => Tag switch
    {
        'f' => Float.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        'i' => Int.ToString(System.Globalization.CultureInfo.InvariantCulture),
        'v' => $"[{Vector.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, " +
               $"{Vector.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, " +
               $"{Vector.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}]",
        's' => $"\"{Text}\"",
        _ => $"?{Tag}",
    };
}
