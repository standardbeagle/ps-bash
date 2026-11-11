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
    bool? CompactOutput = null,
    bool Verbose = false,
    string? Error = null,
    string? Prelude = null,
    bool NoExec = false)
{
    // Short flags that may be bundled (`-lc`, `-eu`, `-oo errexit noglob`). A bundle holding any other
    // letter stays one word and is reported as an unsupported option before anything runs.
    private static readonly System.Buffers.SearchValues<char> BundleLetters = System.Buffers.SearchValues.Create("lisceuxnoO");

    // `set` options that may also come AFTER `-c` (`bash -c -e 'cmd'`): bash takes the first non-option word as the command.
    private static bool IsSetOptionFlag(string arg) => arg is "-e" or "-u" or "-x" or "-n";

    // Long flags that take no value and must not be mistaken for the `-c`
    // command argument when callers pass flags after `-c`.
    private static readonly HashSet<string> KnownValuelessLongFlags = new()
    {
        "--login", "--noprofile", "--norc", "--compact-output", "--no-compact-output", "--caveman", "--wenyan", "--verbose"
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
        bool verbose = false;
        bool endOfOptions = false;
        string? scriptPath = null;
        string[] scriptArgs = [];
        string? timeout = null;
        string? error = null;
        // `-e` / `-u` / `-x` / `-o NAME` / `-O NAME` / `+O NAME`: shell options given on the command line, run as `set`/`shopt` ahead of the -c string.
        var prelude = new List<string>();
        // `-n` (noexec): read and parse the commands, run none of them (`bash -n script.sh` is a syntax check).
        bool noExec = false;
        void ApplySetOption(string flag)
        {
            if (flag == "-n") noExec = true;
            else prelude.Add("set " + flag);
        }
        void AddNamedOption(string flag, string name)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_]+$"))
                error ??= $"{flag}: invalid option name '{name}'";
            else
                prelude.Add(flag switch
                {
                    "-o" => $"set -o {name}",
                    "+o" => $"set +o {name}",
                    "-O" => $"shopt -s {name}",
                    _ => $"shopt -u {name}",
                });
        }

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
                else error ??= "--timeout requires an argument";
                continue;
            }
            // --rcfile/--init-file <file>: bash startup-file override. The value is consumed so
            // it is not mistaken for a script path; ps-bash does not source it.
            if (expanded[i] is "--rcfile" or "--init-file")
            {
                if (i + 1 < expanded.Count) i++;
                else error ??= $"{expanded[i]}: option requires an argument";
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
                case "+c":
                    // bash: `-c` makes the first non-option word the command, so options may still follow it.
                    // Wrappers (Claude Code on Windows) pass `-c -l "real cmd"`; bash itself accepts
                    // `-c -x -e 'cmd'`, `-c -- 'cmd'` and `-c - 'cmd'`. `+c` is accepted like `-c`.
                    int j = i + 1;
                    while (j < expanded.Count)
                    {
                        var opt = expanded[j];
                        if (IsSetOptionFlag(opt)) ApplySetOption(opt);
                        else if (IsKnownValuelessFlag(opt))
                            ApplyValuelessFlag(opt, ref interactive, ref login, ref stdin, ref noprofile, ref compactOutput, ref verbose);
                        else if (opt is "--" or "-") { j++; break; }
                        else if (opt is "-o" or "+o" or "-O" or "+O" && j + 1 < expanded.Count)
                        {
                            AddNamedOption(opt, expanded[++j]);
                        }
                        else if (opt.Length > 1 && opt[0] == '-')
                        {
                            // An option bash does not know is a usage error (status 2), never the command string.
                            error ??= $"{opt}: invalid option";
                            j = expanded.Count;
                            break;
                        }
                        else break;
                        j++;
                    }
                    if (j < expanded.Count)
                    {
                        command = expanded[j];
                        // Bash: `-c string [name [args...]]` — everything AFTER the
                        // command string is POSITIONAL (name → $0, then $1…), never a
                        // flag. So `-c 'echo hi' --version` runs the command; it does
                        // NOT print the version banner, and the trailing args are not
                        // dropped. Capture them as ScriptArgs and stop flag parsing.
                        if (j + 1 < expanded.Count)
                            scriptArgs = [.. scriptArgs, .. expanded.Skip(j + 1)];
                        i = expanded.Count; // end option parsing — the rest are positionals
                    }
                    else
                    {
                        error ??= "-c requires an argument";
                        i = j;
                    }
                    break;
                case "-e":
                case "-u":
                case "-x":
                case "-n":
                    ApplySetOption(expanded[i]);
                    break;
                case "-o":
                case "+o":
                case "-O":
                case "+O":
                    if (i + 1 < expanded.Count)
                    {
                        AddNamedOption(expanded[i], expanded[i + 1]);
                        i++;
                    }
                    else error ??= $"{expanded[i]}: option requires an argument";
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
                case "--verbose":
                    verbose = true;
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
                    if (expanded[i].StartsWith('-'))
                    {
                        error ??= $"unsupported option '{expanded[i]}'";
                        break;
                    }
                    // Non-flag positional argument: first becomes ScriptPath,
                    // subsequent become ScriptArgs (only when no -c command given)
                    if (command is null && !expanded[i].StartsWith('-'))
                    {
                        if (scriptPath is null)
                        {
                            scriptPath = expanded[i];
                            // bash: everything after the script path is positional ($1..),
                            // dash-words included (`bash s.sh -v -c x`), so option parsing ends.
                            endOfOptions = true;
                        }
                        else
                            scriptArgs = [..scriptArgs, expanded[i]];
                    }
                    break;
            }
        }

        // Only a -c string can carry the options; a script or stdin run would silently drop them, so refuse that.
        if (prelude.Count > 0 && command is null)
            error ??= "-e, -u, -x, -o and -O are only supported together with -c";

        return new ShellArgs(command, interactive, login, stdin, noprofile, unixPaths, scriptPath, scriptArgs, rawPs, showVersion, showHelp, timeout, compactOutput, verbose, error,
            prelude.Count > 0 ? string.Join("; ", prelude) : null, noExec);
    }

    // Expands `-lc` -> `-l`, `-c`. Single-char flags (`-c`, `-l`) and long
    // flags (`--login`, anything starting with `--`) pass through unchanged.
    // Stops at `--` so positional args after end-of-options are untouched.
    private static List<string> ExpandBundledShortFlags(string[] args)
    {
        var result = new List<string>(args.Length);
        bool past = false;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (past) { result.Add(a); continue; }
            if (a == "--") { past = true; result.Add(a); continue; }
            if (a is "--timeout" or "--rcfile" or "--init-file" or "-o" or "+o" or "-O" or "+O")
            {
                result.Add(a);
                if (i + 1 < args.Length) result.Add(args[++i]);
                continue;
            }
            // The first non-dash word is the script path (or, after -c, the command string);
            // everything after it is positional and must not be bundle-expanded.
            if (a.Length == 0 || a[0] != '-') { past = true; result.Add(a); continue; }

            if (a.Length > 2 && a[1] != '-' && a.AsSpan(1).IndexOfAnyExcept(BundleLetters) < 0)
            {
                // bash: each `o`/`O` in a bundle takes the next word, in order (`-oo errexit noglob`, `-eo noglob`).
                foreach (var c in a.AsSpan(1))
                {
                    result.Add("-" + c);
                    if (c is 'o' or 'O' && i + 1 < args.Length) result.Add(args[++i]);
                }
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
        ref bool? compactOutput,
        ref bool verbose)
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
            case "--verbose":
                verbose = true; break;
        }
    }
}
