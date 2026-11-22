namespace PsBash.Core.Parser.Ast;

/// <summary>
/// A redirect operation, e.g. <c>&gt;file</c>, <c>2&gt;&amp;1</c>.
/// Modeled after oils syntax.asdl Redir.
/// </summary>
/// <param name="Op">The redirect operator, e.g. <c>&gt;</c>, <c>&gt;&gt;</c>, <c>&lt;</c>, <c>&lt;&lt;&lt;</c>.</param>
/// <param name="Fd">The file descriptor being redirected (default 0 for <c>&lt;</c>, 1 for <c>&gt;</c>).</param>
/// <param name="Target">The redirect target word (a filename, or the raw word for a here-string).</param>
/// <param name="FdVar">Set for <c>{var}&gt;file</c>, where the allocated fd is stored in <c>var</c>.</param>
/// <param name="Here">
/// Set only for a here-string (<c>&lt;&lt;&lt;</c>) attached to a COMPOUND command
/// (<c>done &lt;&lt;&lt; "$x"</c>). A simple command carries its here-strings in
/// <c>Command.Simple.HereDocs</c> instead; compound commands have no such list, so the
/// already-parsed body rides along on the redirect. <c>Target</c> is the raw word.
/// </param>
/// <param name="Force">True for <c>&gt;|</c>: a truncating write that <c>set -C</c> (noclobber) does not refuse.</param>
/// <param name="ReadWrite">
/// True for <c>&lt;&gt;</c> (<c>exec 5&lt;&gt; f</c>): the file is opened for reading AND writing, created when missing.
/// <c>Op</c> is then <c>&lt;</c>, so every path that only reads through the descriptor treats it as an input redirect;
/// <c>exec</c> opens the descriptor read-write (<see cref="OpenOperator"/>).
/// </param>
// NOTE: every parameter must keep a <param> tag. Documenting only SOME of them triggers
// CS1573, which is a harmless warning locally but a hard ERROR in publish.yml's
// build-binaries leg (/warnaserror) — it failed all three RIDs of the v0.10.23 attempt and
// the same class broke v0.10.15. Pre-check with:
//   dotnet build ps-bash.sln -c Release /warnaserror
public sealed record Redirect(
    string Op,
    int Fd,
    CompoundWord Target,
    string? FdVar = null,
    HereDoc? Here = null,
    bool Force = false,
    bool ReadWrite = false) : BashNode
{
    /// <summary>The operator a descriptor is opened with: <c>&lt;&gt;</c> for a read-write redirect, else <see cref="Op"/>.</summary>
    public string OpenOperator => ReadWrite ? "<>" : Op;
}

/// <summary>
/// A here-document redirect, e.g. <c>&lt;&lt;EOF\ntext\nEOF</c>.
/// <paramref name="Body"/> is the collected text between delimiters.
/// <paramref name="Expand"/> is true when variable expansion should occur (unquoted delimiter).
/// <paramref name="StripTabs"/> is true for <c>&lt;&lt;-</c> (leading tabs stripped from body).
/// <paramref name="Word"/> is set for a here-string (<c>&lt;&lt;&lt; word</c>): the parsed word, which the emitter expands
/// as a word (tilde, parameter, command and arithmetic expansion, quote removal; no splitting or globbing) plus a
/// newline. <paramref name="Body"/> then holds an approximation of its text for static scans.
/// </summary>
public sealed record HereDoc(string Body, bool Expand, bool StripTabs, string? FdVar = null, int Fd = 0, CompoundWord? Word = null) : BashNode;

/// <summary>
/// Assignment operator: <c>=</c> or <c>+=</c>.
/// </summary>
public enum AssignOp
{
    Equal,
    PlusEqual,
}

/// <summary>
/// A variable assignment, e.g. <c>foo=bar</c> or <c>foo+=baz</c>.
/// For array assignments like <c>arr=(a b c)</c>, <see cref="ArrayValue"/> is set
/// and <see cref="Value"/> is null.
/// Modeled after oils syntax.asdl AssignPair.
/// </summary>
public sealed record Assignment(
    string Name,
    AssignOp Op,
    CompoundWord? Value,
    ArrayWord? ArrayValue = null) : BashNode
{
    /// <summary>The STORE name (<see cref="PsBash.Core.BashNameCase.ToStore(string)"/>), subscript kept.</summary>
    public string Name { get; init; } = PsBash.Core.BashNameCase.ToStore(Name);
}

/// <summary>
/// An environment pair for command prefix, e.g. <c>FOO=bar cmd</c>.
/// Modeled after oils syntax.asdl EnvPair.
/// </summary>
public sealed record EnvPair(string Name, CompoundWord? Value) : BashNode
{
    /// <summary>The STORE name (<see cref="PsBash.Core.BashNameCase.ToStore(string)"/>).</summary>
    public string Name { get; init; } = PsBash.Core.BashNameCase.ToStore(Name);
}
