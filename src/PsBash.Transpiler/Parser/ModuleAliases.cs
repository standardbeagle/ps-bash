namespace PsBash.Core.Parser;

/// <summary>
/// The names PsBash.psm1 aliases for the whole session (<c>Set-Alias -Name 'X' -Value 'Invoke-BashX' -Scope Global</c>).
/// PowerShell resolves an alias BEFORE a function, so a script function of one of these names (<c>true() { …; }</c>)
/// never ran unless the call names the function explicitly (<c>PsEmitter.IsAliasedCommandName</c>).
/// Kept equal to the psm1's list by <c>Core.Tests/FunctionShadowsModuleAliasGuardTests</c>.
/// </summary>
public static class ModuleAliases
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "[", "alias", "avtui", "awk", "balias", "base64", "basename", "bash", "bg", "browse", "cat", "cd", "chmod",
        "column", "comm", "command", "compgen", "cp", "cut", "date", "diff", "dirname", "dirs", "du", "echo", "env",
        "expand", "expr", "false", "fc", "fg", "file", "find", "flock", "fold", "getopts", "grep", "gtui", "gunzip",
        "gzip", "hash", "head", "history", "hostname", "id", "install", "jobs", "join", "jq", "kill", "less", "let",
        "ln", "ls", "mapfile", "md5sum", "mkdir", "mktemp", "more", "mv", "nl", "nohup", "od", "paste", "ping",
        "popd", "printenv", "printf", "ps", "psav", "psffmpeg", "psgit", "pushd", "pwd", "readarray", "readlink",
        "realpath", "rev", "rg", "rm", "rmdir", "sed", "seq", "sh", "sha1sum", "sha256sum", "shift", "shopt", "shuf",
        "sleep", "sort", "source", "split", "stat", "strings", "tac", "tail", "tar", "tee", "test", "time", "timeout",
        "times", "touch", "tput", "tr", "traceroute", "tracert", "trap", "tree", "true", "type", "ulimit", "umask",
        "unalias", "uname", "unexpand", "uniq", "unset", "wait", "wc", "which", "whoami", "xan", "xargs", "yes", "yq",
        "zcat",
    };
}
