namespace PsBash.Core.Stdio;

/// <summary>
/// The seam between the shell's descriptor table (PsBash.Cmdlets.BashFdTable) and the host that runs the foreground
/// command (SdkWorker). Spec: docs/specs/shell-fds-and-bash-source.md section The descriptor table.
/// <para>
/// <see cref="Terminal"/>: while a command runs, the host's raw stdout (fd 1) and stderr (fd 2) sinks, the shell's
/// ORIGINAL descriptors. A table entry that copies one of them (<c>exec 3&gt;&amp;1</c> at the top level) writes here, past
/// every redirection and capture the writing command is under (<c>x=$(echo hi &gt;&amp;3)</c> prints hi).
/// </para>
/// <para>
/// <see cref="Divert"/>: set by the table once the shell's own fd 1 or 2 is redirected by <c>exec</c>
/// (<c>exec &gt;log</c>). The host offers it every chunk that reaches the shell's original stdout / stderr; true means
/// the table wrote the chunk where that descriptor now points (a file, the other stream, nowhere).
/// </para>
/// Lives in the Transpiler assembly because the host (which sets <see cref="Terminal"/>) and the cmdlets (which own the
/// table) both load it.
/// </summary>
public static class ShellStdio
{
    /// <summary>The running command's original stdout (1) / stderr (2) sinks; null outside a host invocation.</summary>
    public static volatile Action<int, string>? Terminal;

    /// <summary>The descriptor table's diversion of the shell's own fd 1 / 2; null while neither was redirected by exec.</summary>
    public static volatile Func<int, string, bool>? Divert;

    /// <summary>
    /// Offer <paramref name="text"/>, bound for the shell's original descriptor <paramref name="fd"/>, to the table.
    /// True when the table took it. The host's platform newline (<c>\r\n</c> outside framed IPC on Windows) is written
    /// as bash's <c>\n</c>.
    /// </summary>
    public static bool TryDivert(int fd, string text)
    {
        var divert = Divert;
        if (divert is null) return false;
        if (Environment.NewLine == "\r\n" && text.EndsWith("\r\n", StringComparison.Ordinal))
            text = string.Concat(text.AsSpan(0, text.Length - 2), "\n");
        return divert(fd, text);
    }

    /// <summary>Write to the original stdout / stderr. False (nothing written) outside a host invocation.</summary>
    public static bool WriteTerminal(int fd, string text)
    {
        var terminal = Terminal;
        if (terminal is null) return false;
        terminal(fd, text);
        return true;
    }
}
