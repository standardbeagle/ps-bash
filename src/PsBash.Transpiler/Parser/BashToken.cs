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
    PipeAmp,
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
    AmpGreat,  // &>   (redirect stdout+stderr)
    AmpDGreat, // &>>  (append stdout+stderr)
    DLessDash,
    TLess, // <<<  (here-string)
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
/// <param name="BodyStart">
/// For a heredoc delimiter token, the raw-source offset where its body begins
/// (the first body line, just past the command-line newline), or -1 when unset.
/// The lexer already walks the bodies to skip them; it records the span here so
/// the parser reads the exact region instead of re-deriving it from the token
/// cursor — which is what broke when <c>|</c>/<c>&amp;&amp;</c>/<c>;</c> followed the
/// heredoc on the same line.
/// </param>
/// <param name="BodyEnd">
/// The raw-source offset one past the last body character (the start of the
/// delimiter line, so its leading tabs are excluded), or -1 when unset.
/// </param>
public sealed record BashToken(
    BashTokenKind Kind,
    string Value,
    int Position,
    int BodyStart = -1,
    int BodyEnd = -1)
{
    /// <summary>True when this token carries a lexed heredoc body span.</summary>
    public bool HasHereDocBodySpan => BodyStart >= 0;
}
