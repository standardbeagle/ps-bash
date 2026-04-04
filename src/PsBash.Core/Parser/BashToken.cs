namespace PsBash.Core.Parser;

/// <summary>
/// The kind of a lexical token produced by <see cref="BashLexer"/>.
/// </summary>
public enum BashTokenKind
{
    Word,
    AssignmentWord,
    Newline,
    Semi,
    Amp,
    Pipe,
    AndIf,
    OrIf,
    LParen,
    RParen,
    LBrace,
    RBrace,
    Less,
    Great,
    DLess,
    DGreat,
    LessAnd,
    GreatAnd,
    DLessDash,
    Bang,
    IoNumber,
    Eof,
}

/// <summary>
/// A single lexical token from bash input.
/// </summary>
/// <param name="Kind">The token classification.</param>
/// <param name="Value">The raw text of the token.</param>
/// <param name="Position">The zero-based character offset in the input.</param>
public sealed record BashToken(BashTokenKind Kind, string Value, int Position);
