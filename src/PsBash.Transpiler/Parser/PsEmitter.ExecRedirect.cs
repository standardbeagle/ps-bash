using System.Collections.Immutable;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

public static partial class PsEmitter
{
    /// <summary>
    /// <c>exec &gt;&amp;2; rest</c> / <c>exec &gt;log; rest</c> / <c>exec &lt;in; rest</c> / <c>exec &lt;&lt;&lt; w; rest</c>: a command-less <c>exec</c> that
    /// redirects the shell's own stdin, stdout or stderr applies to every later command. The host has no process
    /// descriptors to move, so the rest of the statement list runs as a brace group under those redirections:
    /// <c>{ rest; } &gt;&amp;2</c>. Null when <paramref name="cmd"/> is not such an <c>exec</c> (a numbered descriptor,
    /// a close, <c>{fd}</c>, a here-text on a descriptor other than stdin): <see cref="TryEmitExec"/> handles it alone. In a function body the redirection
    /// ends with the body; bash keeps it for the caller too.
    /// </summary>
    private static ImmutableArray<Redirect>? StdExecRedirects(Command cmd)
    {
        if (cmd is not Command.Simple { Words.Length: 1 } s || GetLiteralValue(s.Words[0]) != "exec"
            || s.Redirects.IsDefaultOrEmpty || !s.HereDocs.IsDefaultOrEmpty)
            return null;
        foreach (var r in s.Redirects)
        {
            // A here-text on stdin (`exec <<< w`, `exec <<EOF`) is the rest's stdin, as `exec < f` is; on another
            // descriptor it is opened by TryEmitExec.
            if (r.Fd is < 0 or > 2 || r.FdVar is not null || (r.Here is not null && r.Fd != 0)
                || (r.Here is null && GetLiteralValue(r.Target) == "-"))
                return null;
        }
        return s.Redirects;
    }

    /// <summary>
    /// The redirections of a simple command on a numbered descriptor (3+) that <see cref="WithScopedHighFds"/> opens around
    /// it, as it does for a compound command: <c>9&gt;f</c>, <c>9&gt;&gt;f</c>, <c>7&lt;f</c>, <c>7&lt;&gt;f</c>, <c>9&gt;&amp;1</c>,
    /// <c>9&gt;&amp;-</c>, <c>4&gt;&amp;3-</c>, <c>3&lt;&lt;&lt; w</c>, <c>3&lt;&lt;EOF</c> (<see cref="FdOpenStep"/>). Not <c>exec</c> (its
    /// descriptors stay open), <c>{fd}</c>, or a dup of stdin (<c>3&lt;&amp;0</c>: the table has no stdin to name). Empty when
    /// there are none. The command's own stdin here-document (<see cref="Command.Simple.HereDocs"/>) is the inner emission's.
    /// </summary>
    private static List<Redirect> ScopedHighFdRedirects(Command.Simple cmd)
    {
        var high = new List<Redirect>();
        if (cmd.Redirects.IsDefaultOrEmpty
            || (cmd.Words.Length > 0 && GetLiteralValue(cmd.Words[0]) == "exec"))
            return high;
        foreach (var r in cmd.Redirects)
        {
            if (r.Fd < 3 || r.FdVar is not null) continue;
            if (r.Here is not null || r.Op is ">" or ">>" or "<" || MovedFd(r) is not null
                || (r.Op is ">&" or "<&" && GetLiteralValue(r.Target) is { } t && (t == "-" || (IsAllDigits(t) && t != "0"))))
                high.Add(r);
        }
        return high;
    }

    /// <summary>The statements after a standard-descriptor <c>exec</c>, as one brace group under its redirections.</summary>
    private static Command RestUnderExecRedirects(ImmutableArray<Command> rest, ImmutableArray<Redirect> redirects)
        => new Command.BraceGroup(rest.Length == 1 ? rest[0] : new Command.CommandList(rest), redirects);
}
