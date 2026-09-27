namespace Nightrunner.Core.Anim;

/// <summary>Which side of the <c>@</c> the bank is on. <c>DecodeSeqName</c> (0x1801f39f0) does not decide; the caller does.</summary>
public enum SeqRefForm
{
    /// <summary><c>&lt;bank&gt;.scr@&lt;Seq&gt;</c> — graph constants (0x47).</summary>
    Graph,

    /// <summary><c>&lt;Seq&gt;@&lt;bank&gt;.scr[:blend]</c> — <c>.gds</c> Data rows and generator outputs.</summary>
    Gds,
}

/// <summary>
/// A sequence reference string (§3.3): a bank (an AnimationScr resource name) and a <c>SeqTrack</c> name inside it.
/// The split is at the first <c>@</c>; the bank goes through <see cref="NormalizeBank"/> (<c>PrepareSequenceBankName</c>:
/// lowercase, <c>.scr</c> dropped). The sequence half is kept as written — the bank lookup is case-insensitive.
/// </summary>
/// <param name="Bank">Normalised bank name (lowercase, no <c>.scr</c>).</param>
/// <param name="Seq">SeqTrack name as written.</param>
/// <param name="Blend">The <c>.gds</c> <c>:blend</c> suffix as written, or null (graph form never has one).</param>
public sealed record SeqRef(string Bank, string Seq, SeqRefForm Form, string? Blend = null)
{
    public const string BankExtension = ".scr";

    /// <summary>The blend suffix as a number, or null when absent or not a number.</summary>
    public double? BlendValue =>
        Blend is not null && double.TryParse(Blend, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;

    /// <summary><c>&lt;bank&gt;.scr@&lt;Seq&gt;</c>.</summary>
    public string FormatGraph() => $"{Bank}{BankExtension}@{Seq}";

    /// <summary><c>&lt;Seq&gt;@&lt;bank&gt;.scr[:blend]</c>.</summary>
    public string FormatGds() => $"{Seq}@{Bank}{BankExtension}" + (Blend is null ? "" : ":" + Blend);

    public override string ToString() => Form == SeqRefForm.Graph ? FormatGraph() : FormatGds();

    /// <summary>Parses the graph form. Null when the text has no <c>@</c> (e.g. a graph variable <c>"… :: Seq"</c>).</summary>
    public static SeqRef? TryParseGraph(string text)
    {
        int at = text.IndexOf('@');
        if (at < 0) return null;
        string bank = text[..at], seq = text[(at + 1)..];
        if (bank.Length == 0 || seq.Length == 0) return null;
        return new SeqRef(NormalizeBank(bank), seq, SeqRefForm.Graph);
    }

    /// <summary>Parses the <c>.gds</c> form; the text after the bank's first <c>:</c> is the blend.</summary>
    public static SeqRef? TryParseGds(string text)
    {
        int at = text.IndexOf('@');
        if (at < 0) return null;
        string seq = text[..at], rest = text[(at + 1)..];
        string? blend = null;
        int colon = rest.IndexOf(':');
        if (colon >= 0)
        {
            blend = rest[(colon + 1)..];
            rest = rest[..colon];
        }
        if (seq.Length == 0 || rest.Length == 0) return null;
        return new SeqRef(NormalizeBank(rest), seq, SeqRefForm.Gds, blend);
    }

    public static SeqRef Parse(string text, SeqRefForm form) =>
        (form == SeqRefForm.Graph ? TryParseGraph(text) : TryParseGds(text))
        ?? throw new FormatException($"'{text}' is not a {form} sequence reference");

    /// <summary><c>PrepareSequenceBankName</c>: ASCII lowercase, a trailing <c>.scr</c> dropped.</summary>
    public static string NormalizeBank(string bank)
    {
        var chars = bank.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] is >= 'A' and <= 'Z') chars[i] = (char)(chars[i] + 32);
        var s = new string(chars);
        return s.EndsWith(BankExtension, StringComparison.Ordinal) ? s[..^BankExtension.Length] : s;
    }
}
