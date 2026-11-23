using System.Collections.Immutable;
using System.Text;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

/// <summary>
/// The <c>[KEY]=VALUE</c> split of an array-literal element (<c>a=([2]=x)</c>, <c>declare -A m=(['a]=b']=c)</c>), read
/// from the element's parts. The parser leaves the subscript as raw text in a glob part (<c>['a]=b']</c>, quotes and
/// all) or as a run of parts (<c>[</c>, <c>"$k"</c>, <c>]=</c>); the close is the <c>]</c>
/// <see cref="BashLexer.SubscriptClose"/> finds in that text, with every other part (a quoted string, an expansion)
/// taken whole. A search of the joined text for the first <c>]=</c> split <c>['a]=b']=c</c> as key <c>'a</c>, value
/// <c>b']=c</c> (bash: key <c>a]=b</c>, value <c>c</c>). Spec: parser-grammar.md §Array subscripts.
/// </summary>
internal static class ArraySubscript
{
    // Stands for a part that is not unquoted text: it is no bracket, quote, backslash or `=`.
    private const char Opaque = '\u0001';

    /// <summary>
    /// <paramref name="elem"/> as <c>[KEY]=VALUE</c>: the subscript's parts (the leading <c>[</c> and the closing
    /// <c>]</c> cut off; a cut text part keeps its kind), the text after the <c>=</c> in the part that holds it, and the
    /// value's remaining parts. False when the element is no subscripted assignment.
    /// </summary>
    internal static bool TrySplit(CompoundWord elem, out ImmutableArray<WordPart> key, out string head, out ImmutableArray<WordPart> rest)
        => TrySplit(elem, out key, out head, out rest, out _);

    /// <summary>As <see cref="TrySplit(CompoundWord, out ImmutableArray{WordPart}, out string, out ImmutableArray{WordPart})"/>;
    /// <paramref name="closePart"/> is the index of the part that holds the closing <c>]</c>.</summary>
    internal static bool TrySplit(CompoundWord elem, out ImmutableArray<WordPart> key, out string head, out ImmutableArray<WordPart> rest, out int closePart)
    {
        closePart = -1;
        key = ImmutableArray<WordPart>.Empty;
        head = "";
        rest = ImmutableArray<WordPart>.Empty;
        var parts = elem.Parts;
        if (parts.IsDefaultOrEmpty || ScanText(parts[0]) is not ['[', ..]) return false;
        var text = new StringBuilder();
        var starts = new int[parts.Length + 1];
        for (int p = 0; p < parts.Length; p++)
        {
            starts[p] = text.Length;
            if (ScanText(parts[p]) is { } t) text.Append(t);
            else text.Append(Opaque);
        }
        starts[parts.Length] = text.Length;
        string s = text.ToString();
        int close = BashLexer.SubscriptClose(s, 0);
        // `[]=v` names no element (bash: a bad subscript); it stays a plain word here.
        if (close < 2 || close + 1 >= s.Length || s[close + 1] != '=') return false;
        closePart = PartAt(starts, close);
        int eqPart = PartAt(starts, close + 1);
        var keyParts = ImmutableArray.CreateBuilder<WordPart>();
        for (int p = 0; p <= closePart; p++)
        {
            int from = Math.Max(starts[p], 1), to = Math.Min(starts[p + 1], close);
            if (from >= to) continue;
            keyParts.Add(from == starts[p] && to == starts[p + 1] ? parts[p] : Slice(parts[p], from - starts[p], to - starts[p]));
        }
        key = keyParts.ToImmutable();
        head = TextOf(parts[eqPart])![(close + 2 - starts[eqPart])..];
        rest = parts.RemoveRange(0, eqPart + 1);
        return true;
    }

    /// <summary>The subscript's text when every part of it is unquoted text (<c>[12]</c>, <c>[k1]</c>), else null.</summary>
    internal static string? PlainText(ImmutableArray<WordPart> key)
    {
        var sb = new StringBuilder();
        foreach (var part in key)
        {
            if (TextOf(part) is not { } t) return null;
            sb.Append(t);
        }
        return sb.ToString();
    }

    /// <summary>The source text of an unquoted text part (a literal, a glob part), else null.</summary>
    internal static string? TextOf(WordPart part) => part switch
    {
        WordPart.Literal l => l.Value,
        WordPart.GlobPart g => g.Pattern,
        _ => null,
    };

    // The text the scan reads for a part: a glob part is source text (its quotes are quotes); a literal is text after
    // quote removal, so its quote, backslash and `$` characters are plain characters there.
    private static string? ScanText(WordPart part) => part switch
    {
        WordPart.GlobPart g => g.Pattern,
        WordPart.Literal l when l.Value.AsSpan().IndexOfAny("'\"\\$`") < 0 => l.Value,
        WordPart.Literal l => MaskQuoting(l.Value),
        _ => null,
    };

    private static string MaskQuoting(string value)
    {
        var chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] is '\'' or '"' or '\\' or '$' or '`') chars[i] = Opaque;
        return new string(chars);
    }

    private static WordPart Slice(WordPart part, int from, int to) => part switch
    {
        WordPart.Literal l => new WordPart.Literal(l.Value[from..to]),
        WordPart.GlobPart g => new WordPart.GlobPart(g.Pattern[from..to]),
        _ => part,
    };

    private static int PartAt(int[] starts, int index)
    {
        int p = 0;
        while (starts[p + 1] <= index) p++;
        return p;
    }
}
