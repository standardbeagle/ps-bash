namespace PsBash.Core.Parser;

/// <summary>
/// Decides whether a simple command INSIDE a compound command's stdin scope should be fed that
/// stdin (<c>PsBuild.StdinFeed | cmd</c>). A mapped <c>Invoke-Bash*</c> cmdlet reads the pipeline only
/// when it has no file operand, but the emitter must not parse flags (passthrough principle), so
/// this is a conservative, purely syntactic estimate of "has a file operand":
/// positional words, minus the ones that are really a flag's value (<c>head -n 2</c>,
/// <c>sort -k 2</c>) and minus the leading pattern/script of <c>grep sed awk rg jq</c> unless it was
/// given with <c>-e</c>/<c>-f</c>. A lone <c>-</c> is an explicit stdin operand and always feeds.
/// A wrong guess in the "no feed" direction only means that command sees no stdin; in the "feed"
/// direction it consumes stdin that a command with a file operand did not want — both are rare
/// next to the plain no-operand forms this exists for.
/// </summary>
public static class StdinReaders
{
    internal enum Kind
    {
        /// <summary>Never reads stdin: builtins, producers, file mutators.</summary>
        Never,
        /// <summary>Reads stdin whatever its operands are (<c>tr</c>, <c>xargs</c>, <c>tee</c>, <c>mapfile</c>).</summary>
        Always,
        /// <summary>Positional operands are files; none means stdin (<c>cat</c>, <c>sort</c>, <c>wc</c>, …).</summary>
        FileOperands,
        /// <summary>First positional is the pattern/program unless <c>-e</c>/<c>-f</c> gave one; the rest are files.</summary>
        PatternThenFiles,
        /// <summary>Not a ps-bash command and not a builtin: possibly a native program that reads stdin.</summary>
        Native,
    }

    // flag letters that take a value (separate word or attached), per command.
    private static readonly Dictionary<string, string> ValueLetters = new(StringComparer.Ordinal)
    {
        ["cat"] = "", ["head"] = "nc", ["tail"] = "ncs", ["wc"] = "", ["sort"] = "ktoST",
        ["uniq"] = "fsw", ["cut"] = "bcfd", ["nl"] = "bnswvildfh", ["tac"] = "s", ["rev"] = "",
        ["fold"] = "w", ["expand"] = "t", ["unexpand"] = "t", ["paste"] = "d", ["join"] = "t12javeo",
        ["comm"] = "", ["base64"] = "w", ["md5sum"] = "", ["sha1sum"] = "", ["sha256sum"] = "", ["od"] = "AtjN",
        ["strings"] = "n", ["column"] = "sol", ["split"] = "labCn", ["file"] = "FfmP", ["gzip"] = "S",
        ["less"] = "", ["more"] = "", ["shuf"] = "ino",
        ["grep"] = "efmABCdD", ["egrep"] = "efmABCdD", ["fgrep"] = "efmABCdD", ["sed"] = "efl", ["awk"] = "Fvf", ["rg"] = "efgmABCtTjrdMsE",
        ["jq"] = "fL", ["yq"] = "",
        // bash / sh with no script operand read their program from stdin (`f() { bash --norc "$@"; }; echo cmd | f`,
        // `{ bash -i; } < cmds`); the -c string counts as an operand, so `bash -c cmd` is not fed.
        ["bash"] = "oO", ["sh"] = "oO",
    };

    // flag letters that SUPPLY the pattern/program (so no positional is consumed as one).
    private static readonly Dictionary<string, string> PatternLetters = new(StringComparer.Ordinal)
    {
        ["grep"] = "ef", ["egrep"] = "ef", ["fgrep"] = "ef", ["sed"] = "ef", ["awk"] = "f", ["rg"] = "ef", ["jq"] = "f", ["yq"] = "",
    };

    private static readonly HashSet<string> AlwaysReaders = new(StringComparer.Ordinal)
    {
        "tr", "xargs", "tee", "mapfile", "readarray",
    };

    private static readonly HashSet<string> PatternCommands = new(StringComparer.Ordinal)
    {
        "grep", "egrep", "fgrep", "sed", "awk", "rg", "jq", "yq",
    };

    // Long options that take a separate value word (`--lines 5`); `--opt=value` is one word anyway.
    private static readonly HashSet<string> LongValueOptions = new(StringComparer.Ordinal)
    {
        "--lines", "--bytes", "--field-separator", "--delimiter", "--delimiters", "--key", "--separator",
        "--output", "--width", "--tabs", "--max-count", "--regexp", "--file", "--expression",
        "--after-context", "--before-context", "--context", "--suffix-length", "--field", "--fields",
        "--characters", "--output-delimiter", "--number-format", "--number-separator", "--number-width",
        "--body-numbering", "--starting-line-number", "--line-increment", "--skip-fields",
        "--skip-chars", "--check-chars", "--sleep-interval", "--include", "--exclude", "--exclude-dir",
        "--glob", "--type", "--threads", "--rcfile", "--init-file",
    };

    // Everything that is a bash builtin or a ps-bash command that does not read stdin as its main input.
    private static readonly HashSet<string> NeverReaders = new(StringComparer.Ordinal)
    {
        "cd", "echo", "printf", "pwd", "ls", "find", "cp", "mv", "rm", "mkdir", "chmod", "rmdir", "touch", "ln",
        "stat", "date", "seq", "expr", "du", "tree", "env", "printenv", "basename", "dirname",
        "hostname", "whoami", "sleep", "time", "which", "ping", "traceroute", "tracert", "uname",
        "readlink", "mktemp", "realpath", "install", "kill", "type", "command", "test", "[", "[[",
        "let", "eval", "trap", "unset", "shift", "shopt", "tput", "yes", "pushd", "popd", "dirs",
        "source", ".", "export", "local", "declare", "typeset", "readonly", "set", "alias", "unalias",
        "exit", "return", "break", "continue", "true", "false", ":", "read", "wait", "jobs", "fg", "bg",
        "id", "ps", "diff", "tar", "xan", "browse", "cmp", "hash", "umask", "ulimit",
        "history", "exec", "getopts", "compgen", "complete", "builtin", "enable", "help", "logout",
        "psgit", "gtui", "psav", "psffmpeg", "avtui", "balias", "unset", "wait",
    };

    internal static Kind Classify(string name)
    {
        if (AlwaysReaders.Contains(name)) return Kind.Always;
        if (PatternCommands.Contains(name)) return Kind.PatternThenFiles;
        if (ValueLetters.ContainsKey(name)) return Kind.FileOperands;
        if (NeverReaders.Contains(name)) return Kind.Never;
        return Kind.Native;
    }

    // Commands that run OTHER code with the stdin they were given: what that code reads cannot be told from the name.
    private static readonly HashSet<string> CodeRunners = new(StringComparer.Ordinal)
    {
        "eval", "source", ".", "exec", "builtin", "env", "time", "timeout", "flock", "nohup", "bash", "sh", "fg",
    };

    /// <summary>
    /// The conservative twin of <see cref="ShouldFeed"/>, for the question "is this command PROVEN not to consume the stdin
    /// it inherits?" (PsEmitter.ProbeStdinScope): true unless the name and operands rule a read out. <see cref="ShouldFeed"/>
    /// answers "feed it?" and so says no to everything that is not fed through the pipeline -- <c>read</c>, <c>eval</c>,
    /// <c>source</c>, <c>bash -c</c>, <c>command prog</c> -- although each of them reads, or runs code that may read, the
    /// stdin of the compound command around it. A name that is no ps-bash command and no builtin (a function, a program)
    /// is a reader.
    /// </summary>
    public static bool MayReadStdin(string name, IReadOnlyList<string?> args)
    {
        if (name is "read" or "mapfile" or "readarray") return true;
        if (name == "command")
        {
            // `command -v x` / `command -V x` only look a name up; `command prog` runs prog.
            if (IsCommandLookup(args)) return false;
            return CommandTarget(args) is not { } target || MayReadStdin(target.Name, target.Args);
        }
        if (CodeRunners.Contains(name)) return args.Count > 0;
        switch (Classify(name))
        {
            case Kind.Native:
            case Kind.Always: return true;
            // `tar xf -`, `diff - f`: an explicit stdin operand (`cd -` and `echo -` are not one).
            case Kind.Never: return name is "tar" or "diff" or "cmp" or "xan" && args.Contains("-");
            default: return ShouldFeed(name, args);
        }
    }

    /// <summary>
    /// True for a command that reads no stdin whatever its operands turn out to be at run time (<c>echo $x</c>,
    /// <c>true</c>, <c>cd "$d"</c>): a never-reading builtin or ps-bash command that runs no other code and takes no
    /// <c>-</c> operand for stdin. <see cref="MayReadStdin"/> judges an operand it cannot see as a file, which may be
    /// <c>-</c> (<c>g=-; cat $g</c>).
    /// </summary>
    public static bool NeverReadsWhateverOperands(string name) =>
        Classify(name) == Kind.Never && !CodeRunners.Contains(name)
        && name is not ("read" or "command" or "tar" or "diff" or "cmp" or "xan");

    /// <summary>True for <c>command -v NAME</c> / <c>command -V NAME</c> (also bundled, <c>-pv</c>): a lookup that runs nothing.</summary>
    private static bool IsCommandLookup(IReadOnlyList<string?> args)
    {
        foreach (var a in args)
        {
            if (a is null || a.Length < 2 || a[0] != '-' || a == "--") return false;
            if (a.AsSpan(1).IndexOfAny('v', 'V') >= 0) return true;
        }
        return false;
    }

    /// <summary>
    /// The command <c>command [-p] [--] NAME ARG…</c> runs, from the words after <c>command</c>; null for the lookup forms
    /// (<c>-v</c> / <c>-V</c>), for no command at all, and for a NAME that is not a literal.
    /// </summary>
    internal static (string Name, IReadOnlyList<string?> Args)? CommandTarget(IReadOnlyList<string?> args)
    {
        if (IsCommandLookup(args)) return null;
        int i = 0;
        while (i < args.Count && args[i] is { Length: >= 2 } a && a[0] == '-')
        {
            i++;
            if (a == "--") break;
        }
        if (i >= args.Count || args[i] is not { } name) return null;
        var rest = new List<string?>(args.Count - i - 1);
        for (int k = i + 1; k < args.Count; k++) rest.Add(args[k]);
        return (name, rest);
    }

    /// <summary>
    /// THE decision how a command in a stdin scope gets the scope's stdin, for a literal name (PsEmitter.EmitSimpleFed)
    /// and a run-time one (the psm1 <c>BashDynamicCommandBody</c>) alike: not at all, piped as records (a ps-bash command
    /// that reads it, <see cref="ShouldFeed"/>), or as the real process stdin (<see cref="StdinPlan.ProcessStdin"/>:
    /// anything else - a program, a function, a name that is not found - through <c>Enter-BashNativeStdin</c>, which
    /// installs the pipe only for a program). <c>command NAME …</c> is planned as NAME.
    /// Never a pipe into a program or an unknown name: the feed that pipe needs waits for the first record, so under a
    /// launcher whose stdin is open and silent (an agent's shell tool) <c>$PY -c 'print(2)'</c> and
    /// <c>command nosuch 2&gt;/dev/null</c> hung until the stdin closed.
    /// </summary>
    public static StdinPlan Plan(string name, IReadOnlyList<string?> args)
    {
        if (name == "command")
            return CommandTarget(args) is { } target ? Plan(target.Name, target.Args) : StdinPlan.None;
        if (!ShouldFeed(name, args)) return StdinPlan.None;
        return Classify(name) == Kind.Native ? StdinPlan.ProcessStdin : StdinPlan.Pipe;
    }
    /// <summary>
    /// <paramref name="args"/> are the words after the command name; a null entry is a non-literal word
    /// (variable, command substitution, glob …), which counts as a positional operand. The runtime asks the same
    /// question of a run-time command name (<c>$c</c>, <c>"$tool"</c>) once its name and operands are known.
    /// </summary>
    public static bool ShouldFeed(string name, IReadOnlyList<string?> args)
    {
        switch (Classify(name))
        {
            case Kind.Never: return false;
            case Kind.Always:
            case Kind.Native: return true;
        }

        var valueLetters = ValueLetters.GetValueOrDefault(name) ?? "";
        var patternLetters = PatternLetters.GetValueOrDefault(name) ?? "";
        int positionals = 0;
        bool patternGiven = false;
        bool explicitStdin = false;
        bool endOfOptions = false;

        for (int i = 0; i < args.Count; i++)
        {
            var word = args[i];
            if (word is null || endOfOptions || word.Length < 2 || word[0] != '-')
            {
                // `-`, or the same stdin by its device name (`cat /dev/stdin`, `cat /dev/fd/0`).
                if (word == "-" || DevicePath.Classify(word, windows: false, out _) == DeviceKind.Stdin) explicitStdin = true;
                else if (word is null || endOfOptions || !IsObsoleteCount(word)) positionals++;
                continue;
            }

            if (word == "--") { endOfOptions = true; continue; }

            if (word[1] == '-')
            {
                if (word.Contains('=')) { patternGiven |= word.StartsWith("--regexp=", StringComparison.Ordinal) || word.StartsWith("--file=", StringComparison.Ordinal) || word.StartsWith("--expression=", StringComparison.Ordinal); continue; }
                if (LongValueOptions.Contains(word))
                {
                    if (word is "--regexp" or "--file" or "--expression") patternGiven = true;
                    i++;
                }
                continue;
            }

            // short-flag bundle: scan letters; a value letter takes the rest or the next word.
            for (int c = 1; c < word.Length; c++)
            {
                if (valueLetters.IndexOf(word[c]) < 0) continue;
                if (patternLetters.IndexOf(word[c]) >= 0) patternGiven = true;
                if (c == word.Length - 1) i++; // value is the next word
                break;                         // otherwise the rest of the word is the value
            }
        }

        if (explicitStdin) return true;
        int filePositionals = positionals;
        if (PatternCommands.Contains(name) && !patternGiven && filePositionals > 0) filePositionals--;
        return filePositionals == 0;
    }

    // `head 5` / `tail +5`: the legacy ps-bash count shorthand is not a file operand.
    private static bool IsObsoleteCount(string word) =>
        word.Length > 1 && word[0] == '+' && word.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;
}

/// <summary>How a command in a stdin scope is handed that stdin (<see cref="StdinReaders.Plan"/>).</summary>
public enum StdinPlan
{
    /// <summary>It does not read it: run as is.</summary>
    None,
    /// <summary>A ps-bash command that reads it: the scope's records piped in.</summary>
    Pipe,
    /// <summary>A program (or a function / unknown name, which the bridge leaves alone): the stdin as its real process stdin.</summary>
    ProcessStdin,
}
