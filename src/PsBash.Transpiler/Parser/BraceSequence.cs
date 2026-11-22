namespace PsBash.Core.Parser;

/// <summary>
/// THE brace sequence expression <c>{x..y[..incr]}</c> (bash braces.c expand_seqterm / mkseq): whether a brace body is
/// one, and the words it makes. Every reader of a sequence asks this: the word parser's brace detection
/// (<c>IsBraceExpansionStart</c>), a top-level brace (<c>ParseBraceExpansion</c>), a nested one
/// (<c>ExpandBraceContent</c>) and the emitter's <c>BracedRange</c> words, so <c>{x,{1..10..3}}</c> steps and
/// <c>{a,b{0001..3}}</c> pads exactly as the top-level form does (guard <c>BraceSequenceGuardTests</c>; spec
/// <c>parser-grammar.md</c> §Brace sequence expressions).
/// <para>
/// bash: both ends are integers (an optional sign, then digits) or both single ASCII letters; the optional increment is
/// an integer whose sign is ignored (the direction comes from the ends) and 0 means 1. Anything else is NOT a sequence
/// and the braces stay literal while the rest of the word expands: <c>{1..$n}</c> is the text <c>{1..3}</c> for n=3,
/// <c>{1..3..$n}</c> is <c>{1..3..3}</c>. An integer end written with a leading zero (<c>01</c>, <c>-05</c>) pads every
/// word to the widest written end, sign included (<c>{-01..2}</c> is <c>-01 000 001 002</c>). A letter sequence runs
/// through the ASCII characters between its ends (<c>{Z..b..3}</c> is <c>Z ] `</c>; <c>{Z..b}</c> holds an empty word
/// where quote removal took the generated <c>\</c>).
/// </para>
/// </summary>
internal readonly record struct BraceSequence(bool IsChar, long Start, long End, long Increment, int Width)
{
    /// <summary>Parse a brace body (the text between the braces). False when it is not a sequence expression.</summary>
    public static bool TryParse(string body, out BraceSequence seq)
    {
        seq = default;
        int dots = body.IndexOf("..", StringComparison.Ordinal);
        if (dots < 0) return false;
        string lhs = body[..dots];
        string rest = body[(dots + 2)..];
        int incrDots = rest.IndexOf("..", StringComparison.Ordinal);
        string rhs = incrDots < 0 ? rest : rest[..incrDots];
        long incr = 0;   // none written: a step of 1 (as a written 0 is)
        if (incrDots >= 0 && !TryInteger(rest[(incrDots + 2)..], out incr))
            return false;

        if (TryInteger(lhs, out long l) && TryInteger(rhs, out long r))
        {
            seq = new BraceSequence(false, l, r, incr, Math.Max(PadWidth(lhs), PadWidth(rhs)));
            return true;
        }
        if (lhs.Length == 1 && rhs.Length == 1 && char.IsAsciiLetter(lhs[0]) && char.IsAsciiLetter(rhs[0]))
        {
            seq = new BraceSequence(true, lhs[0], rhs[0], incr, 0);
            return true;
        }
        return false;
    }

    /// <summary>The width an end written with a leading zero (<c>01</c>, <c>-05</c>) pads to; 0 for any other end.</summary>
    private static int PadWidth(string end) =>
        (end.Length > 1 && end[0] == '0') || (end.Length > 2 && end[0] == '-' && end[1] == '0') ? end.Length : 0;

    /// <summary>An optional sign then decimal digits, within a long (bash strtoimax; ERANGE is not a sequence).</summary>
    private static bool TryInteger(string s, out long value)
    {
        value = 0;
        int i = s.Length > 0 && s[0] is '+' or '-' ? 1 : 0;
        if (i >= s.Length) return false;
        for (int k = i; k < s.Length; k++)
            if (!char.IsAsciiDigit(s[k])) return false;
        return long.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The step actually taken: |increment| (0 is 1) toward <see cref="End"/>.</summary>
    private long Step
    {
        get
        {
            long mag = Increment == 0 ? 1 : Increment == long.MinValue ? long.MaxValue : Math.Abs(Increment);
            return Start <= End ? mag : -mag;
        }
    }

    /// <summary>How many words the sequence makes (checked against the brace-word cap before anything is built).</summary>
    public long Count
    {
        get
        {
            ulong span = Start <= End ? (ulong)(End - Start) : (ulong)(Start - End);
            ulong step = (ulong)Math.Abs(Step);
            return (long)Math.Min(span / step, (ulong)long.MaxValue - 1) + 1;
        }
    }

    /// <summary>
    /// The words, in order, as brace ITEM text (bash text that quote removal finishes, like a comma item's): a generated
    /// <c>\</c> is <c>''</c> (bash's quote removal leaves an empty word, which stays a word) and a generated backtick is
    /// <see cref="GeneratedBacktick"/>. Throws the brace-size parse error past
    /// <see cref="PsEmitter.MaxBraceWords"/>.
    /// </summary>
    public List<string> Words()
    {
        PsEmitter.CheckBraceWords(Count);
        long n = Count, step = Step;
        var words = new List<string>((int)n);
        for (long i = 0, v = Start; i < n; i++, v += step)
            words.Add(Format(v));
        return words;
    }

    /// <summary>The item a generated backtick makes: a literal backtick (the word parser reports the bad substitution
    /// when text follows it in its word).</summary>
    public const string GeneratedBacktick = "\\`";

    private string Format(long v)
    {
        if (IsChar) return v switch { '\\' => "''", '`' => GeneratedBacktick, _ => ((char)v).ToString() };
        string digits = v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (Width == 0 || digits.Length >= Width) return digits;
        // %0*d: zeros go between the sign and the digits.
        return v < 0 ? "-" + digits[1..].PadLeft(Width - 1, '0') : digits.PadLeft(Width, '0');
    }
}
