using System.Collections;
using System.Globalization;
using System.Management.Automation;
using System.Text;

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
    public static string[] SplitNumber(object? value)
    {
        string s = Scalar(value);
        string? ifs = BashVariableStore.Get("IFS");
        if (ifs is null || ifs.AsSpan().IndexOfAny("-0123456789") < 0)
            return s.Length == 0 ? Array.Empty<string>() : new[] { s };
        var fields = Expand(new object?[] { Split, s }, ifs, extglob: false);
        var words = new string[fields.Count];
        for (int i = 0; i < words.Length; i++) words[i] = fields[i].Text;
        return words;
    }

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
                case Pattern:
                    Append(text, tags, Scalar(value), TPattern);
                    break;
                case Split:
                    Append(text, tags, Scalar(value), TSplit);
                    break;
                case SplitList:
                {
                    bool noIfs = ifs is { Length: 0 };
                    string sep = ifs is null ? " " : noIfs ? "" : ifs[..1];
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
        bool IsIfs(int k) => tags[k] == TSplit && ifs.IndexOf(text[k]) >= 0;
        bool IsWs(int k) => IsIfs(k) && IsIfsWhitespace(text[k]);

        var field = new StringBuilder();
        int i = 0;
        while (i < n && IsWs(i)) i++;
        while (i < n)
        {
            field.Clear();
            int start = i;
            bool anchored = false, active = false, escapes = false;
            while (i < n && tags[i] != TBreak && !IsIfs(i))
            {
                byte tag = tags[i];
                char c = text[i];
                if (tag == TAnchor) anchored = true;
                else if (tag == TPattern && c == '\\' && i + 1 < n && tags[i + 1] == TPattern && text[i + 1] is '*' or '?' or '[' or ']' or '\\' or '|' or '(' or ')')
                {
                    // An escape in pattern text (a class's quoted member, `[a"]"b]` is `[a\]b]`): the pattern keeps it,
                    // the text (what an unmatched word prints) has it removed.
                    escapes = true;
                    field.Append(text[++i]);
                }
                else
                {
                    field.Append(c);
                    if (tag == TQuoted) { if (PsBash.Core.Parser.BashPattern.QuotedNeedsEscape(c)) escapes = true; }
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
                    fields.Add(MakeField(field, text, tags, start, end, active, escapes));
                if (i < n) i++;
                continue;
            }

            // An IFS delimiter ends the field, even an empty one (`IFS=:; v=a::b` is a, "", b).
            fields.Add(MakeField(field, text, tags, start, end, active, escapes));
            if (IsWs(i))
            {
                while (i < n && IsWs(i)) i++;
                if (i < n && IsIfs(i) && !IsWs(i))
                {
                    i++;
                    while (i < n && IsWs(i)) i++;
                }
            }
            else
            {
                i++;
                while (i < n && IsWs(i)) i++;
            }
        }
        return fields;
    }

    private static Field MakeField(StringBuilder field, string text, List<byte> tags, int start, int end, bool active, bool escapes)
    {
        string value = field.ToString();
        if (!active) return new Field(value, null);
        if (!escapes) return new Field(value, value);
        var pattern = new StringBuilder(value.Length + 4);
        for (int k = start; k < end; k++)
        {
            byte tag = tags[k];
            if (tag is TAnchor or TBreak) continue;
            char c = text[k];
            // A quoted character is literal in the pattern, its bracket syntax too (`["$x"a]` with x=! is {!, a}).
            if (tag == TQuoted && PsBash.Core.Parser.BashPattern.QuotedNeedsEscape(c)) pattern.Append('\\');
            pattern.Append(c);
        }
        return new Field(value, pattern.ToString());
    }
}
