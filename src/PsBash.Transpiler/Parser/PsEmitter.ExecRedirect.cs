using System.Collections.Immutable;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

public static partial class PsEmitter
{
    /// <summary>
    /// <c>exec &gt;&amp;2; rest</c> / <c>exec &gt;log; rest</c> / <c>exec &lt;in; rest</c>: a command-less <c>exec</c> that
    /// redirects the shell's own stdin, stdout or stderr applies to every later command. The host has no process
    /// descriptors to move, so the rest of the statement list runs as a brace group under those redirections:
    /// <c>{ rest; } &gt;&amp;2</c>. Null when <paramref name="cmd"/> is not such an <c>exec</c> (a numbered descriptor,
    /// a close, <c>{fd}</c>, a here-doc): <see cref="TryEmitExec"/> handles it alone. In a function body the redirection
    /// ends with the body; bash keeps it for the caller too.
    /// </summary>
    private static ImmutableArray<Redirect>? StdExecRedirects(Command cmd)
    {
        if (cmd is not Command.Simple { Words.Length: 1 } s || GetLiteralValue(s.Words[0]) != "exec"
            || s.Redirects.IsDefaultOrEmpty || !s.HereDocs.IsDefaultOrEmpty)
            return null;
        foreach (var r in s.Redirects)
        {
            if (r.Fd is < 0 or > 2 || r.FdVar is not null || r.Here is not null || GetLiteralValue(r.Target) == "-")
                return null;
        }
        return s.Redirects;
    }

    /// <summary>The statements after a standard-descriptor <c>exec</c>, as one brace group under its redirections.</summary>
    private static Command RestUnderExecRedirects(ImmutableArray<Command> rest, ImmutableArray<Redirect> redirects)
        => new Command.BraceGroup(rest.Length == 1 ? rest[0] : new Command.CommandList(rest), redirects);
}
