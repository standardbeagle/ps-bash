namespace PsBash.Shell;

public record ShellArgs(
    string? Command,
    bool Interactive,
    bool Login,
    bool ReadFromStdin,
    bool NoProfile,
    bool? UnixPaths = null,
    string? ScriptPath = null,
    string[] ScriptArgs = null!,
    bool RawPowerShell = false,
    bool ShowVersion = false,
    bool ShowHelp = false,
    string? Timeout = null,
    bool? CompactOutput = null)
{
    // Bash-compatible short flags ps-bash recognizes. Used to expand bundled
    // forms like `-lc` and to let `-c` skip past intervening flags when callers
    // (e.g. Claude Code) pass `-c -l "cmd"` instead of the canonical
    // `-l -c "cmd"`. Unknown short letters in a bundle are dropped silently.
    private static readonly HashSet<char> KnownShortFlags = new() { 'l', 'i', 's', 'c' };

    // Long flags that take no value and must not be mistaken for the `-c`
    // command argument when callers pass flags after `-c`.
    private static readonly HashSet<string> KnownValuelessLongFlags = new()
    {
        "--login", "--noprofile", "--norc", "--compact-output", "--no-compact-output", "--caveman", "--wenyan"
    };

    public static ShellArgs Parse(string[] args)
    {
        var expanded = ExpandBundledShortFlags(args);

        string? command = null;
        bool interactive = false;
        bool login = false;
        bool stdin = false;
        bool noprofile = false;
        bool? unixPaths = null;
        bool rawPs = false;
        bool showVersion = false;
        bool showHelp = false;
        bool? compactOutput = null;
        bool endOfOptions = false;
        string? scriptPath = null;
        string[] scriptArgs = [];
        string? timeout = null;

        for (int i = 0; i < expanded.Count; i++)
        {
            if (endOfOptions)
            {
                // After --, all remaining args are positional
                if (scriptPath is null)
                    scriptPath = expanded[i];
                else
                    scriptArgs = [..scriptArgs, expanded[i]];
                continue;
            }

            // --timeout <value> / --timeout=<value>: per-invocation idle timeout
            // the caller can set without juggling the PSBASH_TIMEOUT env var.
            // Value is seconds, or none/0/off/infinite to disable. Program.cs
            // forwards it to PSBASH_TIMEOUT so IpcWorker's single parser owns it.
            if (expanded[i] == "--timeout")
            {
                if (i + 1 < expanded.Count) timeout = expanded[++i];
                continue;
            }
            if (expanded[i].StartsWith("--timeout=", StringComparison.Ordinal))
            {
                timeout = expanded[i]["--timeout=".Length..];
                continue;
            }

            switch (expanded[i])
            {
                case "-c":
                    // Bash spec says "next arg becomes command". In practice,
                    // wrappers (Claude Code on Windows) pass `-c -l "real cmd"`
                    // expecting `-l` to still be parsed as a flag. Honor both:
                    // skip recognized valueless flags, then take the first
                    // non-flag arg as the command.
                    int j = i + 1;
                    while (j < expanded.Count && IsKnownValuelessFlag(expanded[j]))
                    {
                        ApplyValuelessFlag(expanded[j], ref interactive, ref login, ref stdin, ref noprofile, ref compactOutput);
                        j++;
                    }
                    if (j < expanded.Count)
                    {
                        command = expanded[j];
                        i = j;
                    }
                    break;
                case "-i":
                    interactive = true;
                    break;
                case "--login":
                case "-l":
                    login = true;
                    break;
                case "-s":
                    stdin = true;
                    break;
                case "--noprofile":
                case "--norc":
                    noprofile = true;
                    break;
                case "--unix-paths":
                    unixPaths = true;
                    break;
                case "--windows-paths":
                    unixPaths = false;
                    break;
                case "--compact-output":
                case "--caveman":
                case "--wenyan":
                    compactOutput = true;
                    break;
                case "--no-compact-output":
                    compactOutput = false;
                    break;
                // PTY-9 follow-on: raw PowerShell passthrough. When set, the
                // -c argument / stdin / script body is forwarded to the host
                // runspace WITHOUT bash transpilation. Enables driving raw
                // PowerShell probe scripts ([Console]::ReadKey, etc.) through
                // the same launcher → PTY → host pipeline as bash code, so
                // tests can exercise PTY behaviors that the bash front-end
                // does not expose. Long form only; no short alias.
                case "--ps":
                case "--raw-ps":
                    rawPs = true;
                    break;
                // Informational flags. Real bash exits 0 after printing for
                // `--version` / `--help`. ps-bash had no case for either, so
                // they fell through to the default (a `-`-prefixed token is not
                // a ScriptPath), leaving command/script null — which dropped the
                // launcher into interactive/stdin mode and hung when no tty was
                // attached (e.g. a tooling probe of `ps-bash --version`).
                case "--version":
                case "-V":
                    showVersion = true;
                    break;
                case "--help":
                    showHelp = true;
                    break;
                case "--":
                    endOfOptions = true;
                    break;
                default:
                    // Non-flag positional argument: first becomes ScriptPath,
                    // subsequent become ScriptArgs (only when no -c command given)
                    if (command is null && !expanded[i].StartsWith('-'))
                    {
                        if (scriptPath is null)
                            scriptPath = expanded[i];
                        else
                            scriptArgs = [..scriptArgs, expanded[i]];
                    }
                    break;
            }
        }

        return new ShellArgs(command, interactive, login, stdin, noprofile, unixPaths, scriptPath, scriptArgs, rawPs, showVersion, showHelp, timeout, compactOutput);
    }

    // Expands `-lc` -> `-l`, `-c`. Single-char flags (`-c`, `-l`) and long
    // flags (`--login`, anything starting with `--`) pass through unchanged.
    // Stops at `--` so positional args after end-of-options are untouched.
    private static List<string> ExpandBundledShortFlags(string[] args)
    {
        var result = new List<string>(args.Length);
        bool past = false;
        foreach (var a in args)
        {
            if (past) { result.Add(a); continue; }
            if (a == "--") { past = true; result.Add(a); continue; }

            if (a.Length > 2 && a[0] == '-' && a[1] != '-' && a.Skip(1).All(c => KnownShortFlags.Contains(c)))
            {
                foreach (var c in a.AsSpan(1))
                    result.Add("-" + c);
            }
            else
            {
                result.Add(a);
            }
        }
        return result;
    }

    private static bool IsKnownValuelessFlag(string arg)
        => arg is "-l" or "-i" or "-s" || KnownValuelessLongFlags.Contains(arg);

    private static void ApplyValuelessFlag(
        string flag,
        ref bool interactive,
        ref bool login,
        ref bool stdin,
        ref bool noprofile,
        ref bool? compactOutput)
    {
        switch (flag)
        {
            case "-l":
            case "--login":
                login = true; break;
            case "-i":
                interactive = true; break;
            case "-s":
                stdin = true; break;
            case "--noprofile":
            case "--norc":
                noprofile = true; break;
            case "--compact-output":
            case "--caveman":
            case "--wenyan":
                compactOutput = true; break;
            case "--no-compact-output":
                compactOutput = false; break;
        }
    }
}
