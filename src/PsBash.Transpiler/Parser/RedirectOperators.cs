namespace PsBash.Core.Parser;

/// <summary>
/// The ONE table of bash's redirection operators, read by the lexer (which digit word before an operator is its
/// descriptor, <see cref="BashTokenKind.IoNumber"/>) and by the parser (which tokens start a redirection, after a word,
/// after <c>{name}</c>, after a compound command's closing keyword, in a declaration's operands). Each used to keep its
/// own list, and they disagreed: <c>&lt;&lt;&lt;</c> took no <c>N</c> prefix (<c>read -u 3 b 3&lt;&lt;&lt; x</c> passed
/// <c>3</c> as a name) and no <c>{name}</c> prefix on a simple command (<c>exec {v}&lt;&lt;&lt; x</c> ran <c>{v}</c>),
/// and a compound command's <c>3&lt;&lt;EOF</c> was read as a file redirect named by the delimiter.
/// Every operator here takes an optional <c>N</c> or <c>{name}</c> prefix on every kind of command, except the
/// <c>&amp;&gt;</c> pair, which names both stdout and stderr (bash reads <c>2&amp;&gt;f</c> as the word <c>2</c> then <c>&amp;&gt;f</c>).
/// Spec: docs/specs/parser-grammar.md section Redirections.
/// </summary>
public static class RedirectOperators
{
    /// <summary>The redirection operators and their text, longest spelling first where one prefixes another. The lexer
    /// gives <c>&lt;&gt;</c> as a <see cref="BashTokenKind.Less"/> and <c>&gt;|</c> as a <see cref="BashTokenKind.Great"/>
    /// whose text is the spelling.</summary>
    public static readonly IReadOnlyList<(BashTokenKind Kind, string Text)> All =
    [
        (BashTokenKind.TLess, "<<<"),
        (BashTokenKind.DLessDash, "<<-"),
        (BashTokenKind.DLess, "<<"),
        (BashTokenKind.AmpDGreat, "&>>"),
        (BashTokenKind.AmpGreat, "&>"),
        (BashTokenKind.Less, "<>"),
        (BashTokenKind.Great, ">|"),
        (BashTokenKind.DGreat, ">>"),
        (BashTokenKind.LessAnd, "<&"),
        (BashTokenKind.GreatAnd, ">&"),
        (BashTokenKind.Less, "<"),
        (BashTokenKind.Great, ">"),
    ];

    /// <summary>True for every token kind that starts a redirection.</summary>
    public static bool IsRedirect(BashTokenKind kind) =>
        kind is BashTokenKind.Less or BashTokenKind.Great or BashTokenKind.DLess or BashTokenKind.DGreat
            or BashTokenKind.LessAnd or BashTokenKind.GreatAnd or BashTokenKind.AmpGreat or BashTokenKind.AmpDGreat
            or BashTokenKind.DLessDash or BashTokenKind.TLess;

    /// <summary>True when a digit word right before the operator (no blank between) is its descriptor.</summary>
    public static bool TakesFdPrefix(BashTokenKind kind) =>
        IsRedirect(kind) && kind is not (BashTokenKind.AmpGreat or BashTokenKind.AmpDGreat);

    /// <summary>A here-document (<c>&lt;&lt;</c>, <c>&lt;&lt;-</c>): the next word is its delimiter, the body follows the line.</summary>
    public static bool IsHereDocument(BashTokenKind kind) => kind is BashTokenKind.DLess or BashTokenKind.DLessDash;

    /// <summary>A here-document or here-string: the redirection supplies text, not a file.</summary>
    public static bool SuppliesText(BashTokenKind kind) => IsHereDocument(kind) || kind == BashTokenKind.TLess;
}
