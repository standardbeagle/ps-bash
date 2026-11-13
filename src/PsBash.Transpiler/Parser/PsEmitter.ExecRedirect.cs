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

    /// <summary>
    /// The redirections of a simple command on a numbered descriptor (3+) that <see cref="WithScopedHighFds"/> opens around
    /// it, as it does for a compound command: <c>9&gt;f</c>, <c>9&gt;&gt;f</c>, <c>7&lt;f</c>, <c>9&gt;&amp;1</c>, <c>9&gt;&amp;-</c>. Not
    /// <c>exec</c> (its descriptors stay open), <c>{fd}</c>, here-docs, or a dup of stdin (<c>3&lt;&amp;0</c>: the table has no
    /// stdin to name). Empty when there are none.
    /// </summary>
    private static List<Redirect> ScopedHighFdRedirects(Command.Simple cmd)
    {
        var high = new List<Redirect>();
        if (cmd.Redirects.IsDefaultOrEmpty || !cmd.HereDocs.IsDefaultOrEmpty
            || (cmd.Words.Length > 0 && GetLiteralValue(cmd.Words[0]) == "exec"))
            return high;
        foreach (var r in cmd.Redirects)
        {
            if (r.Fd < 3 || r.FdVar is not null || r.Here is not null) continue;
            if (r.Op is ">" or ">>" or "<"
                || (r.Op is ">&" or "<&" && GetLiteralValue(r.Target) is { } t && (t == "-" || (IsAllDigits(t) && t != "0"))))
                high.Add(r);
        }
        return high;
    }

    /// <summary>The statements after a standard-descriptor <c>exec</c>, as one brace group under its redirections.</summary>
    private static Command RestUnderExecRedirects(ImmutableArray<Command> rest, ImmutableArray<Redirect> redirects)
        => new Command.BraceGroup(rest.Length == 1 ? rest[0] : new Command.CommandList(rest), redirects);
}
