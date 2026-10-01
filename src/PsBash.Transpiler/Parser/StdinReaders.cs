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
internal static class StdinReaders
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
        ["comm"] = "", ["base64"] = "w", ["md5sum"] = "", ["sha1sum"] = "", ["sha256sum"] = "",
        ["strings"] = "n", ["column"] = "sol", ["split"] = "labCn", ["file"] = "FfmP", ["gzip"] = "S",
        ["less"] = "", ["more"] = "", ["shuf"] = "ino",
        ["grep"] = "efmABCdD", ["sed"] = "efl", ["awk"] = "Fvf", ["rg"] = "efgmABCtTjrdMsE",
        ["jq"] = "fL", ["yq"] = "",
    };

    // flag letters that SUPPLY the pattern/program (so no positional is consumed as one).
    private static readonly Dictionary<string, string> PatternLetters = new(StringComparer.Ordinal)
    {
        ["grep"] = "ef", ["sed"] = "ef", ["awk"] = "f", ["rg"] = "ef", ["jq"] = "f", ["yq"] = "",
    };

    private static readonly HashSet<string> AlwaysReaders = new(StringComparer.Ordinal)
    {
        "tr", "xargs", "tee", "mapfile", "readarray",
    };

    private static readonly HashSet<string> PatternCommands = new(StringComparer.Ordinal)
    {
        "grep", "sed", "awk", "rg", "jq", "yq",
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
        "--glob", "--type", "--threads",
    };

    // Everything that is a bash builtin or a ps-bash command that does not read stdin as its main input.
    private static readonly HashSet<string> NeverReaders = new(StringComparer.Ordinal)
    {
        "cd", "echo", "printf", "pwd", "ls", "find", "cp", "mv", "rm", "mkdir", "rmdir", "touch", "ln",
        "stat", "date", "seq", "expr", "du", "tree", "env", "printenv", "basename", "dirname",
        "hostname", "whoami", "sleep", "time", "which", "ping", "traceroute", "tracert", "uname",
        "readlink", "mktemp", "realpath", "install", "kill", "type", "command", "test", "[", "[[",
        "let", "eval", "trap", "unset", "shift", "shopt", "tput", "yes", "pushd", "popd", "dirs",
        "source", ".", "export", "local", "declare", "typeset", "readonly", "set", "alias", "unalias",
        "exit", "return", "break", "continue", "true", "false", ":", "read", "wait", "jobs", "fg", "bg",
        "bash", "sh", "id", "ps", "diff", "tar", "xan", "browse", "cmp", "hash", "umask", "ulimit",
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

    /// <summary>
    /// <paramref name="args"/> are the words after the command name; a null entry is a non-literal word
    /// (variable, command substitution, glob …), which counts as a positional operand.
    /// </summary>
    internal static bool ShouldFeed(string name, IReadOnlyList<string?> args)
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
                if (word == "-") explicitStdin = true;
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
