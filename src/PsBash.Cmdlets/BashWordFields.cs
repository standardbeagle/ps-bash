using System.Collections;
using System.Globalization;
using System.Management.Automation;
using System.Text;
using PsBash.Core;
using PsBash.Core.Parser;

namespace PsBash.Cmdlets;

/// <summary>
/// bash's word expansion after the values are known (bash §3.5.7 word splitting, §3.5.8 pathname expansion, §3.5.9 quote
/// removal), for ONE word given as an ordered list of segments. The emitter builds the segments from the word's parts
/// (<c>PsEmitter.EmitUnquotedSplitWordArray</c>) and every split / globbed word goes through here, so no word shape has
/// rules of its own (emitter-strategy.md "Word expansion: one primitive").
///
/// <para>A segment is a kind and a value:</para>
/// <list type="bullet">
///   <item><see cref="Quoted"/>: quoted text (<c>'a b'</c>, <c>"$x"</c>, <c>\*</c>, a tilde's directory). Never split,
///   never a pattern character, and it makes its field exist even when empty (<c>""</c> is one empty field).</item>
///   <item><see cref="Pattern"/>: unquoted literal text and glob parts (<c>a</c>, <c>*</c>, <c>[ab]</c>, in the pattern
///   dialect with <c>\</c> escapes). Never split; its glob characters are active.</item>
///   <item><see cref="Split"/>: the value of an unquoted expansion (<c>$x</c>, <c>$(cmd)</c>, <c>$((n))</c>, the default
///   word of <c>${v:-a b}</c>): split on IFS, glob characters active.</item>
///   <item><see cref="SplitList"/>: an unquoted list expansion (<c>$@</c>, <c>${a[@]}</c>, <c>${@/x/y}</c>): the values
///   joined with the first IFS character and split like <see cref="Split"/>; with an empty IFS each value is a field of its
///   own and an empty one is dropped.</item>
///   <item><see cref="QuotedList"/>: a quoted list expansion (<c>"$@"</c>, <c>"${a[@]}"</c>, <c>"${@^^}"</c>): the first
///   value joins the text before it, every later value starts a new field, the last joins the text after it; each value
///   is quoted text. No values contribute nothing, not even an empty field.</item>
///   <item><see cref="QuotedValue"/>: an expansion inside the same quotes as a list (<c>$e</c> of <c>"$@$e"</c>): quoted
///   text, but it does not make its field exist, so with no values and an empty <c>$e</c> the word is no field.</item>
/// </list>
/// <para>In the parts array a kind is followed by its value. In place of a kind there may be a nested parts array (a group,
/// the chosen arm of <c>${v:-word}</c>) or null (an empty group).</para>
/// <para>The fields: a field ends at an IFS character of a split segment (IFS whitespace folds and is trimmed at the ends,
/// every other IFS character delimits on its own) and between two list values. A field made only of split text that came
/// out empty is removed. A field with an active glob character carries its <see cref="Field.Pattern"/>: quoted characters
/// backslash-escaped, split and pattern text as is.</para>
/// </summary>
public static class BashWordFields
{
    public const int Quoted = 1;
    public const int Pattern = 2;
    public const int Split = 3;
    public const int SplitList = 4;
    public const int QuotedList = 5;
    /// <summary>A field boundary between two words of one brace expansion (<c>{1,2}$x</c>); its value is ignored.</summary>
    public const int Break = 6;
    public const int QuotedValue = 7;

    // Per-character tags of the joined word text.
    private const byte TSplit = 0;   // from a split segment: may delimit, glob-active
    private const byte TQuoted = 1;  // quoted: literal, escaped in a pattern
    private const byte TAnchor = 2;  // zero-width: the field it sits in exists
    private const byte TPattern = 3; // unquoted literal / glob text: glob-active, never delimits
    private const byte TBreak = 4;   // zero-width: a field ends here (between two list values)

    /// <summary>One field: its text, and the glob pattern when it has an active glob character (null otherwise).</summary>
    public readonly record struct Field(string Text, string? Pattern);

    /// <summary>
    /// An unquoted arithmetic result alone (<c>$((n))</c>): its fields under the current IFS. A number holds no glob
    /// character, so no pathname expansion; with IFS unset or free of digits and <c>-</c> it is the one field.
    /// </summary>
    public static string[] SplitNumber(object? value) => SplitNumber(value, "IFS");

    /// <summary><see cref="SplitNumber(object?)"/> under the IFS held by <paramref name="ifsVariable"/>: a word of
    /// <c>IFS=: cmd $((n))</c> is expanded before the binding, so it reads the parked value (see <see cref="Words(IList, string)"/>).</summary>
    public static string[] SplitNumber(object? value, string ifsVariable)
    {
        string s = Scalar(value);
        string? ifs = BashVariableStore.Get(ifsVariable);
        if (ifs is null || ifs.AsSpan().IndexOfAny("-0123456789") < 0)
            return s.Length == 0 ? Array.Empty<string>() : new[] { s };
        var fields = Expand(new object?[] { Split, s }, ifs, extglob: false);
        var words = new string[fields.Count];
        for (int i = 0; i < words.Length; i++) words[i] = fields[i].Text;
        return words;
    }

    /// <summary>
    /// The words of a word given as segments, without a cmdlet call: the fast path of every split word
    /// (<c>PsBuild.WordFieldsArray</c>). Null when a field has an active glob character: pathname expansion
    /// (<c>nullglob</c>, <c>failglob</c>'s error, <c>set -f</c>) is the <c>ConvertTo-BashWords</c> cmdlet's, which the
    /// emitted code then calls with the same, already evaluated, segments. A cmdlet call costs ~20 times this.
    /// </summary>
    public static string[]? Words(IList parts) => Words(parts, "IFS");

    /// <summary><see cref="Words(IList)"/> under the IFS held by <paramref name="ifsVariable"/>: <c>IFS=: cmd $x</c>
    /// expands its words before the binding applies, so they read the IFS the emitter parked (<c>__psbash_ea_IFS</c>).</summary>
    public static string[]? Words(IList parts, string ifsVariable)
    {
        var fields = Expand(parts, BashVariableStore.Get(ifsVariable), InvokeBashShoptCommand.IsEnabled("extglob"));
        var words = fields.Count == 0 ? Array.Empty<string>() : new string[fields.Count];
        for (int i = 0; i < words.Length; i++)
        {
            if (fields[i].Pattern is not null) return null;
            words[i] = fields[i].Text;
        }
        return words;
    }

    /// <summary>The first character of <paramref name="ifs"/> as a string: a surrogate pair whole (an IFS of one
    /// character outside the BMP joins with that character, not half of it).</summary>
    public static string FirstIfsCharacter(string ifs)
        => ifs.Length > 1 && char.IsHighSurrogate(ifs[0]) && char.IsLowSurrogate(ifs[1]) ? ifs[..2] : ifs[..1];

    [ThreadStatic] private static StringBuilder? t_text;
    [ThreadStatic] private static List<byte>? t_tags;

    /// <summary>The fields of a word given as segments (see the type summary). <paramref name="ifs"/> null = unset.</summary>
    public static List<Field> Expand(IList parts, string? ifs, bool extglob)
    {
        var text = t_text ??= new StringBuilder(64);
        var tags = t_tags ??= new List<byte>(64);
        text.Clear();
        tags.Clear();
        try
        {
            AppendGroup(text, tags, parts, ifs);
            return SplitFields(text, tags, ifs ?? " \t\n", extglob);
        }
        finally
        {
            if (text.Capacity > 4096) t_text = null;
            if (tags.Capacity > 4096) t_tags = null;
        }
    }

    private static void AppendGroup(StringBuilder text, List<byte> tags, IList parts, string? ifs)
    {
        for (int i = 0; i < parts.Count; i++)
        {
            object? k = Unwrap(parts[i]);
            if (k is null) continue;                                       // an empty group
            if (k is IList group) { AppendGroup(text, tags, group, ifs); continue; }
            int kind = Convert.ToInt32(k, CultureInfo.InvariantCulture);
            object? value = i + 1 < parts.Count ? Unwrap(parts[++i]) : null;
            switch (kind)
            {
                case Quoted:
                    Append(text, tags, "\0", TAnchor);
                    Append(text, tags, Scalar(value), TQuoted);
                    break;
                case QuotedValue:
                    Append(text, tags, Scalar(value), TQuoted);
                    break;
                case Pattern:
                    Append(text, tags, Scalar(value), TPattern);
                    break;
                case Split:
                    Append(text, tags, Scalar(value), TSplit);
                    break;
                case SplitList:
                {
                    bool noIfs = ifs is { Length: 0 };
                    string sep = ifs is null ? " " : noIfs ? "" : FirstIfsCharacter(ifs);
                    int n = 0;
                    foreach (var v in Values(value))
                    {
                        if (n++ > 0)
                        {
                            if (noIfs) Append(text, tags, "\0", TBreak);
                            else Append(text, tags, sep, TSplit);
                        }
                        Append(text, tags, v, TSplit);
                    }
                    break;
                }
                case QuotedList:
                {
                    int n = 0;
                    foreach (var v in Values(value))
                    {
                        if (n++ > 0) Append(text, tags, "\0", TBreak);
                        Append(text, tags, "\0", TAnchor);
                        Append(text, tags, v, TQuoted);
                    }
                    break;
                }
                case Break:
                    Append(text, tags, "\0", TBreak);
                    break;
                default:
                    throw new ArgumentException($"unknown word segment kind {kind}", nameof(parts));
            }
        }
    }

    private static void Append(StringBuilder text, List<byte> tags, string s, byte tag)
    {
        text.Append(s);
        for (int j = 0; j < s.Length; j++) tags.Add(tag);
    }

    private static object? Unwrap(object? o) => o is PSObject p ? p.BaseObject : o;

    /// <summary>A scalar value as text: null is empty; a collection (an expansion that produced several objects) is its
    /// items joined by a space, as PowerShell's <c>[string]</c> would.</summary>
    private static string Scalar(object? value)
    {
        value = Unwrap(value);
        if (value is null) return "";
        if (value is string s) return s;
        if (value is IEnumerable e)
        {
            var sb = new StringBuilder();
            foreach (var item in e)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Scalar(item));
            }
            return sb.ToString();
        }
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    private static IEnumerable<string> Values(object? value)
    {
        value = Unwrap(value);
        if (value is null) yield break;
        if (value is string s) { yield return s; yield break; }
        if (value is IDictionary d)
        {
            foreach (var v in d.Values) yield return Scalar(v);
            yield break;
        }
        if (value is IEnumerable e)
        {
            foreach (var item in e) yield return Scalar(item);
            yield break;
        }
        yield return Scalar(value);
    }

    private static bool IsIfsWhitespace(char c) => c is ' ' or '\t' or '\n';

    private static List<Field> SplitFields(StringBuilder textSb, List<byte> tags, string ifs, bool extglob)
    {
        var fields = new List<Field>();
        int n = textSb.Length;
        if (n == 0) return fields;
        string text = textSb.ToString();
        // A delimiter is one IFS character of split text: one char, or a surrogate pair whole (a pair is never cut).
        int Delim(int k) => tags[k] == TSplit ? IfsCharLength(text, k, ifs, tags) : 0;
        bool IsWs(int k) => tags[k] == TSplit && IsIfsWhitespace(text[k]) && ifs.IndexOf(text[k]) >= 0;

        var field = new StringBuilder();
        int i = 0;
        while (i < n && IsWs(i)) i++;
        while (i < n)
        {
            field.Clear();
            int start = i;
            bool anchored = false, active = false, rebuild = false;
            while (i < n && tags[i] != TBreak && Delim(i) == 0)
            {
                byte tag = tags[i];
                char c = text[i];
                if (tag == TAnchor) anchored = true;
                else if (tag == TPattern && c == '\\' && i + 1 < n && tags[i + 1] == TPattern && text[i + 1] is '*' or '?' or '[' or ']' or '\\' or '|' or '(' or ')')
                {
                    // An escape in pattern text (a class's quoted member, `[a"]"b]` is `[a\]b]`): the pattern keeps it,
                    // the text (what an unmatched word prints) has it removed.
                    rebuild = true;
                    field.Append(text[++i]);
                }
                else
                {
                    field.Append(c);
                    if (tag == TQuoted) { if (BashPattern.QuotedNeedsEscape(c)) rebuild = true; }
                    else if (c is '*' or '?' or '[' || extglob && c is '(' && i > start && text[i - 1] is '@' or '+' or '!' && tags[i - 1] != TQuoted)
                        active = true;
                }
                i++;
            }
            int end = i;
            if (i >= n || tags[i] == TBreak)
            {
                // The end of the word or of a list value: an empty field of split text alone is no field.
                if (field.Length > 0 || anchored)
                    fields.Add(MakeField(field, text, tags, start, end, active, rebuild));
                if (i < n)
                {
                    // The word after a brace break starts like a word: its leading IFS whitespace makes no field
                    // (x=' a': `${x}{1,2}` is a1 a2).
                    i++;
                    while (i < n && IsWs(i)) i++;
                }
                continue;
            }

            // An IFS delimiter ends the field, even an empty one (`IFS=:; v=a::b` is a, "", b).
            fields.Add(MakeField(field, text, tags, start, end, active, rebuild));
            if (IsWs(i))
            {
                while (i < n && IsWs(i)) i++;
                if (i < n && !IsWs(i) && Delim(i) is > 0 and var len)
                {
                    i += len;
                    while (i < n && IsWs(i)) i++;
                }
            }
            else
            {
                i += Delim(i);
                while (i < n && IsWs(i)) i++;
            }
        }
        return fields;
    }

    /// <summary>The length of the IFS character at <paramref name="k"/>: 1, 2 for a surrogate pair, 0 when it is none
    /// (half of a pair never delimits).</summary>
    private static int IfsCharLength(string text, int k, string ifs, List<byte> tags)
    {
        char c = text[k];
        if (char.IsHighSurrogate(c))
        {
            if (k + 1 >= text.Length || !char.IsLowSurrogate(text[k + 1]) || tags[k + 1] != TSplit) return 0;
            return ifs.AsSpan().IndexOf(text.AsSpan(k, 2)) >= 0 ? 2 : 0;
        }
        if (char.IsLowSurrogate(c)) return 0;
        return ifs.IndexOf(c) >= 0 ? 1 : 0;
    }

    /// <summary>
    /// A field's text and, when it has an active glob character, its pattern: split and pattern text as is, quoted text
    /// made inert by the pattern compiler's own escaping (<see cref="BashPattern.EscapeLiteral"/>: <c>* ? [ ] \ ( ) | ! ^ -</c>,
    /// so a quoted <c>"+(a)"</c> is no extglob). On Windows a drive path's backslashes (<c>C:\tmp\[x]</c>, quoted or from a
    /// value) are separators: they become <c>/</c> before the escaping, never after it.
    /// </summary>
    private static Field MakeField(StringBuilder field, string text, List<byte> tags, int start, int end, bool active, bool rebuild)
    {
        string value = field.ToString();
        if (!active) return new Field(value, null);
        bool drive = OperatingSystem.IsWindows() && value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '\\';
        if (!rebuild && !drive) return new Field(value, value);
        var pattern = new StringBuilder(value.Length + 8);
        int k = start;
        while (k < end)
        {
            byte tag = tags[k];
            if (tag is TAnchor or TBreak) { k++; continue; }
            int runStart = k;
            while (k < end && tags[k] == tag) k++;
            string run = text.Substring(runStart, k - runStart);
            if (drive && tag != TPattern) run = BashPath.ToShell(run, windows: true);
            // A quoted character is literal in the pattern, its bracket syntax too (`["$x"a]` with x=! is {!, a}).
            pattern.Append(tag == TQuoted ? BashPattern.EscapeLiteral(run) : run);
        }
        return new Field(value, pattern.ToString());
    }
}
