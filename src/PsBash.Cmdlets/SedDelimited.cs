using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// One delimited field of a sed script (an address regex, an <c>s</c> regex or replacement, a <c>y</c> string).
/// <see cref="Text"/> is the field with GNU's delimiter escapes resolved. When <see cref="Closed"/>, <see cref="End"/>
/// is the index just past the closing delimiter; otherwise it is where the scan stopped (a newline, or the end of
/// the script), which is the <c>char N</c> GNU names in "unterminated ...".
/// </summary>
internal readonly record struct SedField(string Text, int End, bool Closed);

/// <summary>
/// A regex address (<c>/RE/</c> or <c>\cREc</c>) with its <c>I</c> / <c>M</c> flags. <see cref="End"/> is the first
/// non-blank index after the flags (where a <c>,</c>, <c>!</c> or the command stands, and the <c>char N</c> of an
/// error in this regex); when not <see cref="Closed"/> it is where the scan stopped.
/// </summary>
internal readonly record struct SedRegexAddress(string Pattern, bool IgnoreCase, bool Multiline, int End, bool Closed);

/// <summary>
/// The ONE scanner for sed's delimited text, GNU sed 4.9's <c>match_slash</c>. The script splitter
/// (<see cref="InvokeBashSedCommand.SplitSedCommands(string)"/>) and the command parser both read every address regex,
/// <c>s</c> command and <c>y</c> command through it, so where a field ends and what it holds can never disagree
/// between them (guard: <c>SedDelimitedGuardTests</c>). Rules (oracle: GNU sed 4.9):
/// <list type="bullet">
/// <item><c>\DELIM</c> is the delimiter character itself, with its ordinary meaning in the field (<c>\|</c> under
/// <c>s|..|..|</c> is a literal <c>|</c> in a BRE, alternation in an ERE; <c>\.</c> under <c>s.a\.b.X.</c> is any
/// character). In a replacement, <c>\&amp;</c> stays an escaped <c>&amp;</c> even when <c>&amp;</c> is the delimiter.</item>
/// <item>In a regex, a bracket expression <c>[...]</c> is copied as it stands: a delimiter in it does not end the
/// field and a backslash in it is not an escape (<c>/[/]/</c>, <c>\|[\|]|</c>); a leading <c>]</c> and
/// <c>[:class:]</c> / <c>[.c.]</c> / <c>[=c=]</c> are members. Replacements and <c>y</c> strings have no brackets.</item>
/// <item>A backslash-newline is a newline; an unescaped newline ends the scan unterminated (inside brackets too).</item>
/// <item>Every other <c>\X</c> is kept as typed, so <c>\n</c> (the newline, even when <c>n</c> is the delimiter) and
/// <c>\\</c> reach the regex translator / replacement builder unchanged.</item>
/// </list>
/// </summary>
internal static class SedDelimited
{
    /// <summary>
    /// Scan one field that starts at <paramref name="pos"/> (just past its opening delimiter) and ends at the next
    /// unescaped <paramref name="delim"/>. <paramref name="regex"/> is true for a regex field (address, <c>s</c>
    /// pattern), which honours bracket expressions.
    /// </summary>
    public static SedField Scan(string s, int pos, char delim, bool regex)
    {
        StringBuilder? sb = null;   // allocated only once the text stops being a plain slice of s
        int sliceStart = pos;
        int i = pos;

        void Flush(int upTo)
        {
            sb ??= new StringBuilder(s.Length - pos);
            sb.Append(s, sliceStart, upTo - sliceStart);
        }

        string TextUpTo(int end)
        {
            if (sb == null) return s.Substring(pos, end - pos);
            sb.Append(s, sliceStart, end - sliceStart);
            return sb.ToString();
        }

        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\n') return new SedField(TextUpTo(i), i, false);
            if (c == delim) return new SedField(TextUpTo(i), i + 1, true);
            if (c == '\\')
            {
                if (i + 1 >= s.Length) return new SedField(TextUpTo(i), s.Length, false);
                char n = s[i + 1];
                bool keepBackslash = n != '\n' && (n != delim || (regex && n == 'n') || (!regex && n == '&'));
                if (!keepBackslash)
                {
                    // `\DELIM` -> DELIM, `\<newline>` -> newline: drop the backslash.
                    Flush(i);
                    sliceStart = i + 1;
                }
                i += 2;
                continue;
            }
            if (c == '[' && regex)
            {
                int close = BracketEnd(s, i);
                if (close < 0)
                {
                    int nl = s.IndexOf('\n', i);
                    int stop = nl < 0 ? s.Length : nl;
                    return new SedField(TextUpTo(stop), stop, false);
                }
                i = close;
                continue;
            }
            i++;
        }
        return new SedField(TextUpTo(s.Length), s.Length, false);
    }

    /// <summary>
    /// Index just past the bracket expression that opens at <paramref name="open"/> (a <c>[</c>), or -1 when a
    /// newline or the end of the script comes first. Nothing inside is an escape.
    /// </summary>
    public static int BracketEnd(string s, int open)
    {
        int j = open + 1;
        if (j < s.Length && s[j] == '^') j++;
        if (j < s.Length && s[j] == ']') j++;   // a leading `]` is a member
        while (j < s.Length)
        {
            char d = s[j];
            if (d == '\n') return -1;
            if (d == ']') return j + 1;
            if (d == '[' && j + 1 < s.Length && s[j + 1] is ':' or '.' or '=')
            {
                char kind = s[j + 1];
                int k = j + 2;
                while (k + 1 < s.Length && s[k] != '\n' && !(s[k] == kind && s[k + 1] == ']')) k++;
                if (k + 1 < s.Length && s[k] == kind && s[k + 1] == ']') { j = k + 2; continue; }
            }
            j++;
        }
        return -1;
    }

    /// <summary>
    /// True when a regex address starts at <paramref name="pos"/>: <c>/</c>, or <c>\</c> naming a custom delimiter.
    /// </summary>
    public static bool StartsRegexAddress(string s, int pos)
        => pos < s.Length && (s[pos] == '/' || s[pos] == '\\');

    /// <summary>
    /// Read a regex address at <paramref name="pos"/> (see <see cref="StartsRegexAddress"/>): the delimited regex, then
    /// any run of <c>I</c> (ignore case) / <c>M</c> (multi-line) flags, blanks allowed around them.
    /// </summary>
    public static SedRegexAddress ScanRegexAddress(string s, int pos)
    {
        int open = pos;
        if (s[pos] == '\\')
        {
            open = pos + 1;
            if (open >= s.Length || s[open] == '\n')
                return new SedRegexAddress(string.Empty, false, false, open, false);
        }
        char delim = s[open];
        var field = Scan(s, open + 1, delim, regex: true);
        if (!field.Closed) return new SedRegexAddress(field.Text, false, false, field.End, false);

        bool icase = false, multi = false;
        int i = field.End;
        while (true)
        {
            while (i < s.Length && s[i] is ' ' or '\t') i++;
            if (i < s.Length && s[i] == 'I') { icase = true; i++; continue; }
            if (i < s.Length && s[i] == 'M') { multi = true; i++; continue; }
            break;
        }
        return new SedRegexAddress(field.Text, icase, multi, i, true);
    }

    /// <summary>
    /// The two delimited fields of an <c>s</c> (regex, replacement) or <c>y</c> (source, dest) command whose command
    /// letter is at <paramref name="cmdPos"/>. <paramref name="second"/> is default when the first field is
    /// unterminated; check <see cref="SedField.Closed"/> on each.
    /// </summary>
    public static bool ScanCommandFields(string s, int cmdPos, out SedField first, out SedField second)
    {
        first = second = default;
        if (cmdPos + 1 >= s.Length) return false;
        char delim = s[cmdPos + 1];
        bool isSubst = s[cmdPos] == 's';
        first = Scan(s, cmdPos + 2, delim, regex: isSubst);
        if (!first.Closed) return false;
        second = Scan(s, first.End, delim, regex: false);
        return second.Closed;
    }
}
