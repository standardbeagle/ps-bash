using System.Collections.Immutable;

namespace PsBash.Core.Parser.Ast;

/// <summary>
/// Base type for all word parts. A word part is a component of a compound word.
/// Modeled after oils syntax.asdl word_part variants.
/// </summary>
public abstract record WordPart : BashNode
{
    /// <summary>An unquoted literal string, e.g. <c>hello</c>.</summary>
    public sealed record Literal(string Value) : WordPart;

    /// <summary>A backslash-escaped character, e.g. <c>\ </c> (escaped space).</summary>
    public sealed record EscapedLiteral(string Value) : WordPart;

    /// <summary>A single-quoted string, e.g. <c>'hello world'</c>.</summary>
    public sealed record SingleQuoted(string Value) : WordPart;

    /// <summary>A double-quoted string containing word parts, e.g. <c>"hello $name"</c>.</summary>
    public sealed record DoubleQuoted(ImmutableArray<WordPart> Parts) : WordPart;

    /// <summary>A simple variable substitution, e.g. <c>$foo</c> or <c>$?</c>.</summary>
    public sealed record SimpleVarSub(string Name) : WordPart;

    /// <summary>A braced variable substitution, e.g. <c>${foo:-default}</c>.</summary>
    public sealed record BracedVarSub(string Name, string? Suffix) : WordPart;

    /// <summary>A command substitution, e.g. <c>$(cmd)</c> or <c>`cmd`</c>.</summary>
    public sealed record CommandSub(BashNode Body) : WordPart;

    /// <summary>An arithmetic substitution, e.g. <c>$(( x + 1 ))</c>.</summary>
    public sealed record ArithSub(string Expr) : WordPart;

    /// <summary>A tilde substitution, e.g. <c>~</c> or <c>~user</c>.</summary>
    public sealed record TildeSub(string? User) : WordPart;

    /// <summary>
    /// A glob pattern fragment: <c>*</c>, <c>?</c>, <c>[abc]</c>, or an extglob
    /// like <c>+(*.py|*.js)</c>. The <paramref name="Pattern"/> holds the raw text.
    /// </summary>
    public sealed record GlobPart(string Pattern) : WordPart;

    /// <summary>
    /// A brace expansion with comma-separated items, e.g. <c>{a,b,c}</c>.
    /// Each item is a string literal.
    /// </summary>
    public sealed record BracedTuple(ImmutableArray<string> Items) : WordPart;

    /// <summary>
    /// A brace expansion with a numeric range, e.g. <c>{1..10}</c> or <c>{01..05}</c>.
    /// When <paramref name="ZeroPad"/> is greater than zero, values are left-padded with zeros.
    /// </summary>
    public sealed record BracedRange(int Start, int End, int ZeroPad) : WordPart;

    /// <summary>
    /// A process substitution, e.g. <c>&lt;(cmd)</c> (input) or <c>&gt;(cmd)</c> (output).
    /// The <paramref name="Body"/> is the parsed command inside the substitution.
    /// <paramref name="IsInput"/> is true for <c>&lt;(...)</c>, false for <c>&gt;(...)</c>.
    /// </summary>
    public sealed record ProcessSub(BashNode Body, bool IsInput) : WordPart;
}

/// <summary>
/// A compound word made up of one or more word parts.
/// Modeled after oils syntax.asdl CompoundWord.
/// </summary>
public sealed record CompoundWord(ImmutableArray<WordPart> Parts) : BashNode;

/// <summary>
/// An array literal value, e.g. <c>(a b c)</c> in <c>arr=(a b c)</c>.
/// Stored as a list of compound words representing each element.
/// </summary>
public sealed record ArrayWord(ImmutableArray<CompoundWord> Elements) : BashNode;
