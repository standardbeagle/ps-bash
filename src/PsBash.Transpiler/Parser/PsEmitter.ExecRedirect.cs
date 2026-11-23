using System.Collections.Immutable;
using System.Text;
using PsBash.Core.Parser.Ast;
using PsBash.Core.Transpiler;

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
                || (r.Op is ">&" or "<&" && GetLiteralValue(r.Target) is { } t && (t == "-" || IsAllDigits(t))))
                high.Add(r);
        }
        return high;
    }

    /// <summary>The statements after a standard-descriptor <c>exec</c>, as one brace group under its redirections.</summary>
    private static Command RestUnderExecRedirects(ImmutableArray<Command> rest, ImmutableArray<Redirect> redirects)
        => new Command.BraceGroup(rest.Length == 1 ? rest[0] : new Command.CommandList(rest), redirects);

    /// <summary>The body of the subshell being emitted: its top-level statements are the subshell's whole remaining life.</summary>
    [ThreadStatic] private static Command? _subshellBody;

    private static string EmitSubshellBody(Command body)
    {
        var prior = _subshellBody;
        _subshellBody = body;
        try { return EmitBody(body); }
        finally { _subshellBody = prior; }
    }

    /// <summary>
    /// Whether <c>exec &gt;f; rest</c> in <paramref name="list"/> may run as <c>{ rest; } &gt;f</c>: only where the rest
    /// of the list IS the rest of the shell's life - the script's top level, or a subshell's - or for stdin, which the
    /// table does not carry. Anywhere else (a function, loop, if, group, eval, sourced file) the exec outlives the list,
    /// so the descriptor table does it at run time (<c>Invoke-BashExecFd 1 '&gt;' f</c>, BashFdTable).
    /// </summary>
    private static bool ExecRewriteApplies(Command.CommandList list, ImmutableArray<Redirect> execRedirects, bool isRoot)
    {
        foreach (var r in execRedirects)
        {
            // `exec >&5`: fd 1 becomes a copy of what 5 names NOW; `{ rest; } >&5` would follow 5 instead, so an
            // `exec 5>&-` in the rest broke stdout ("5: Bad file descriptor").
            if (r.Fd != 0 && r.Op is ">&" or "<&" && GetLiteralValue(r.Target) is { } t && IsAllDigits(t) && t is not ("0" or "1" or "2"))
                return false;
        }
        foreach (var r in execRedirects)
            if (r.Fd == 0) return true;
        // The script's top level keeps the rewrite for every target: a background job started later (`exec >log; cmd &`)
        // must see the redirection, and a job's output does not pass through the table.
        if (isRoot && _context != TranspileContext.Eval) return true;
        if (!ReferenceEquals(list, _subshellBody)) return false;
        // In a subshell, only a redirection that cannot fail: a file that does not open leaves the descriptor as it was
        // and the rest runs (`( exec >/nonexist/f; echo x )` prints x, status 0), where `{ rest; } >f` skipped the rest.
        foreach (var r in execRedirects)
        {
            bool dup = r.Op is ">&" or "<&" && GetLiteralValue(r.Target) is { } t && IsAllDigits(t);
            if (!dup && DevicePath.Classify(GetLiteralValue(r.Target), windows: false, out _) != DeviceKind.Null) return false;
        }
        return true;
    }

    // ---- The redirect model's view of numbered descriptors (TryAppendModelledRedirectTail) --------------------------

    /// <summary>The whole redirect list of the command whose fd 3+ redirections a caller applies itself
    /// (WithScopedHighFds); the model reads it for what those descriptors name (<c>3&gt;&amp;1 1&gt;&amp;2 2&gt;&amp;3</c>).</summary>
    [ThreadStatic] private static IReadOnlyList<Redirect>? _fdPrelude;

    /// <summary>The simple command being emitted, for a closed stdout's <c>NAME: write error</c>; null for a compound.</summary>
    [ThreadStatic] private static string? _redirectCommandName;

    private static T WithFdPrelude<T>(IReadOnlyList<Redirect> all, string? commandName, Func<T> emit)
    {
        var prior = _fdPrelude;
        var priorName = _redirectCommandName;
        _fdPrelude = all;
        _redirectCommandName = commandName;
        try { return emit(); }
        finally { _fdPrelude = prior; _redirectCommandName = priorName; }
    }

    private static void WithFdPrelude(IReadOnlyList<Redirect> all, string? commandName, Action emit)
        => WithFdPrelude(all, commandName, () => { emit(); return 0; });

    /// <summary>A simple command, emitted with its name known to the redirect model (<see cref="_redirectCommandName"/>).</summary>
    private static string EmitSimple(Command.Simple cmd)
    {
        var priorName = _redirectCommandName;
        _redirectCommandName = cmd.Words.Length > 0 ? GetLiteralValue(cmd.Words[0]) : null;
        try { return EmitSimpleCore(cmd); }
        finally { _redirectCommandName = priorName; }
    }

    /// <summary>The list to apply: the prelude when <paramref name="tail"/> is part of it, else the tail itself.</summary>
    private static IReadOnlyList<Redirect> FdPreludeFor(IReadOnlyList<Redirect> tail)
    {
        if (_fdPrelude is not { Count: > 0 } prelude) return tail;
        foreach (var r in tail)
            if (!ContainsRef(prelude, r)) return tail;
        return prelude;
    }

    private static bool ContainsRef(IReadOnlyList<Redirect> list, Redirect r)
    {
        foreach (var x in list)
            if (ReferenceEquals(x, r)) return true;
        return false;
    }

    /// <summary>
    /// What descriptor <paramref name="m"/> names at this point of the list, as an absolute destination: the command's
    /// original stdout is <see cref="FdDestKind.StdoutDefault"/>, its original stderr <see cref="FdDestKind.StderrDefault"/>.
    /// </summary>
    private static FdDest AbsoluteDest(int m, FdDest fd1, FdDest fd2, Dictionary<int, FdDest>? high) => m switch
    {
        1 => fd1.Kind == FdDestKind.Default ? new FdDest(FdDestKind.StdoutDefault) : fd1,
        2 => fd2.Kind == FdDestKind.Default ? new FdDest(FdDestKind.StderrDefault) : fd2,
        _ => high is not null && high.TryGetValue(m, out var d) ? d : new FdDest(FdDestKind.Table, m),
    };

    /// <summary>An absolute destination as fd <paramref name="fd"/>'s own (its original is <see cref="FdDestKind.Default"/>).</summary>
    private static FdDest RelativeTo(int fd, FdDest abs) => (fd, abs.Kind) switch
    {
        (1, FdDestKind.StdoutDefault) => new FdDest(FdDestKind.Default),
        (2, FdDestKind.StderrDefault) => new FdDest(FdDestKind.Default),
        _ => abs,
    };

    /// <summary>A numbered descriptor's redirection that its caller applies (WithScopedHighFds): what it names after it.</summary>
    private static void TrackHighFd(Redirect r, FdDest fd1, FdDest fd2, ref Dictionary<int, FdDest>? high)
    {
        if (r.Fd < 3 || r.FdVar is not null || r.Here is not null) return;
        FdDest dest = new(FdDestKind.Table, r.Fd);
        if (r.Op is ">&" or "<&" && GetLiteralValue(r.Target) is { } t)
        {
            if (t == "-") dest = new FdDest(FdDestKind.Closed);
            else if (TryParseDescriptor(t, out int m) && m != 0) dest = AbsoluteDest(m, fd1, fd2, high);
        }
        (high ??= new Dictionary<int, FdDest>())[r.Fd] = dest;
    }

    /// <summary>
    /// A redirection target read as a descriptor number: ASCII digits only, decimal (<c>&gt;&amp;3</c>, <c>&lt;&amp;010</c> is
    /// descriptor 10, the M of <c>&gt;&amp;M-</c>). A descriptor is not an arithmetic operand: bash reads it with its own
    /// digit loop, never as octal. Every redirect-target fd parse goes through here (ArithmeticOperandGuardTests).
    /// </summary>
    private static bool TryParseDescriptor(string? text, out int fd)
    {
        fd = -1;
        if (string.IsNullOrEmpty(text)) return false;
        foreach (char c in text)
            if (c is < '0' or > '9') return false;
        return int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out fd);
    }

    /// <summary>True when the final fd 1 / fd 2 need the route stage: a table descriptor, a closed stdout, or a swap.</summary>
    private static bool NeedsFdRoute(FdDest fd1, FdDest fd2)
        => fd1.Kind is FdDestKind.Table or FdDestKind.Closed || fd2.Kind is FdDestKind.Table or FdDestKind.Closed
           || (fd1.Kind == FdDestKind.StderrDefault && fd2.Kind == FdDestKind.StdoutDefault);

    /// <summary>
    /// <c>cmd 2&gt;&amp;3</c>, <c>cmd 2&gt;&amp;1 1&gt;&amp;3</c>, <c>cmd 3&gt;&amp;1 1&gt;&amp;2 2&gt;&amp;3</c>, <c>echo hi &gt;&amp;-</c>: the command's
    /// stderr joins the stream (unless it stays where it is), <c>Invoke-BashFdRoute</c> sends each stream to its final
    /// destination, and a file destination is left to the <c>Invoke-BashRedirect</c> stage after it.
    /// </summary>
    private static void AppendRoutedRedirectTail(StringBuilder sb, List<(string Target, bool Append, bool Force, bool ReadWrite)> opened,
        FdDest fd1, FdDest fd2, List<string> truncate, List<string> touch, string? openPlan)
    {
        if (fd2.Kind == FdDestKind.Null) sb.Append(" 2>$null");
        else if (fd2.Kind != FdDestKind.Default) sb.Append(" 2>&1");
        sb.Append(PsBuild.FdRouteStage(RouteName(fd1, stdout: true), RouteName(fd2, stdout: false), _redirectCommandName));
        string? stdoutPath = fd1.Kind == FdDestKind.File ? opened[fd1.File].Target : null;
        string? errorPath = fd2.Kind == FdDestKind.File && !(fd1.Kind == FdDestKind.File && fd1.File == fd2.File)
            ? opened[fd2.File].Target : null;
        if (stdoutPath is null && errorPath is null && truncate.Count == 0 && touch.Count == 0) return;
        sb.Append(PsBuild.RedirectStage(
            stdoutPath, stdoutPath is not null && opened[fd1.File].Append,
            errorPath, errorPath is not null && opened[fd2.File].Append,
            passErrors: false, truncate, touch,
            force: stdoutPath is not null && opened[fd1.File].Force,
            errorForce: errorPath is not null && opened[fd2.File].Force,
            openPlan: openPlan,
            readWrite: stdoutPath is not null && opened[fd1.File].ReadWrite));
    }

    private static string RouteName(FdDest dest, bool stdout) => dest.Kind switch
    {
        FdDestKind.Default => stdout ? "out" : "err",
        FdDestKind.StdoutDefault => "out",
        FdDestKind.StderrDefault => "err",
        FdDestKind.Null => "null",
        FdDestKind.Closed => "close",
        FdDestKind.File => "pass",
        _ => dest.File.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
