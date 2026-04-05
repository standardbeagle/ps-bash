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
}

/// <summary>
/// A compound word made up of one or more word parts.
/// Modeled after oils syntax.asdl CompoundWord.
/// </summary>
public sealed record CompoundWord(ImmutableArray<WordPart> Parts) : BashNode;
