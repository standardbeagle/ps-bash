namespace PsBash.Core;

/// <summary>
/// How a bash command words a usage error, shared by the cmdlets (<c>FileSystemHelpers</c>) and the host
/// (<c>SdkWorker</c>, for a PowerShell parameter-binding error). SMA-free on purpose: it lives in the assembly both
/// reference, as <see cref="StderrRecord"/> does.
/// </summary>
public static class UsageErrors
{
    /// <summary>
    /// What a tool prints after a usage error's first line (GNU coreutils: <c>Try 'CMD --help' for more
    /// information.</c>). The other GNU tools word it their own way (oracle: Ubuntu 24.04). null = this ps-bash prints
    /// no hint: sed / strings print their whole --help text, tree / rg word even the first line differently.
    /// </summary>
    public static string? Hint(string cmd) => cmd switch
    {
        "diff" => "diff: Try 'diff --help' for more information.",
        "gzip" or "gunzip" or "zcat" => $"Try `{cmd} --help' for more information.",
        "tar" => "Try 'tar --help' or 'tar --usage' for more information.",
        "grep" or "egrep" or "fgrep" => $"Usage: {cmd} [OPTION]... PATTERNS [FILE]...\nTry '{cmd} --help' for more information.",
        "file" => "Usage: file [-bcCdEhikLlNnprsSvzZ0] [--apple] [--extension] [--mime-encoding]\n"
            + "            [--mime-type] [-e <testname>] [-F <separator>]  [-f <namefile>]\n"
            + "            [-m <magicfiles>] [-P <parameter=value>] [--exclude-quiet]\n"
            + "            <file> ...\n"
            + "       file -C [-m <magicfiles>]\n"
            + "       file [--help]",
        "sed" or "strings" or "tree" or "rg" or "hostname" => null,   // hostname: its usage text follows, on stdout
        _ => $"Try '{cmd} --help' for more information.",
    };

    /// <summary>
    /// The status a usage error leaves: 2 for a bash builtin and for the GNU tools whose "trouble" status is 2 (grep,
    /// diff, ls, sort, tar); 1 for the rest of coreutils.
    /// </summary>
    public static int Status(string cmd)
        => Parser.BashBuiltins.Names.Contains(cmd) || cmd is "grep" or "egrep" or "fgrep" or "diff" or "ls" or "sort" or "tar"
            ? 2 : 1;

    /// <summary><c>Invoke-BashTr</c> -&gt; <c>tr</c>; null for a command that is not a bash command.</summary>
    public static string? BashName(string? cmdlet)
        => cmdlet is { Length: > 11 } && cmdlet.StartsWith("Invoke-Bash", StringComparison.OrdinalIgnoreCase)
            ? cmdlet.Substring("Invoke-Bash".Length).ToLowerInvariant()
            : null;

    /// <summary>
    /// A PowerShell parameter-binding error met by a command: what the shell prints and the status it leaves. The
    /// binder fails the statement before the cmdlet runs, so no cmdlet sets a status (it stayed 0) and the text was
    /// PowerShell's (<c>tr -O a b</c>: <c>Parameter cannot be processed because the parameter name 'O' is
    /// ambiguous…</c>). A single letter the binder rejected (<paramref name="errorId"/>
    /// <c>NamedParameterNotFound</c> / <c>AmbiguousParameter</c>) is reported as getopt does: <c>tr: invalid option --
    /// 'O'</c> and the usage hint. Any other binding error keeps <paramref name="text"/>. A bash command leaves its
    /// usage status (<see cref="Status"/>); any other command 1.
    /// </summary>
    public static (string Message, int Status) BindingFailure(string? cmdletName, string? errorId, string? parameterName, string text)
    {
        if (BashName(cmdletName) is not { } command) return (text, 1);
        if (errorId is "NamedParameterNotFound" or "AmbiguousParameter" && parameterName is { Length: 1 })
        {
            string message = $"{command}: invalid option -- '{parameterName}'";
            if (Hint(command) is { } hint) message += "\n" + hint;
            return (message, Status(command));
        }
        return (text, Status(command));
    }
}
