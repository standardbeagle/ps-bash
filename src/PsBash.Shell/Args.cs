namespace PsBash.Shell;

public record ShellArgs(
    string? Command,
    bool Interactive,
    bool Login,
    bool ReadFromStdin,
    bool NoProfile,
    bool? UnixPaths = null)
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
        "--login", "--noprofile", "--norc"
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
        bool endOfOptions = false;

        for (int i = 0; i < expanded.Count; i++)
        {
            if (endOfOptions)
                break;

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
                        ApplyValuelessFlag(expanded[j], ref interactive, ref login, ref stdin, ref noprofile);
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
                case "--":
                    endOfOptions = true;
                    break;
            }
        }

        return new ShellArgs(command, interactive, login, stdin, noprofile, unixPaths);
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
        string flag, ref bool interactive, ref bool login, ref bool stdin, ref bool noprofile)
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
        }
    }
}
