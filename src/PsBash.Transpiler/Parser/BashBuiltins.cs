namespace PsBash.Core.Parser;

/// <summary>
/// bash 5.2's builtin and reserved-word names: one list for the emitter (<c>builtin NAME</c> refuses a name that
/// is not a builtin) and the runtime (<c>type</c>, <c>command -v</c>, <c>compgen -b</c>/<c>-k</c>).
/// </summary>
public static class BashBuiltins
{
    /// <summary>bash 5.2's <c>enable -a</c> list: the names <c>type</c> calls "a shell builtin".</summary>
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        ".", ":", "[", "alias", "bg", "bind", "break", "builtin", "caller", "cd", "command", "compgen", "complete",
        "compopt", "continue", "declare", "dirs", "disown", "echo", "enable", "eval", "exec", "exit", "export",
        "false", "fc", "fg", "getopts", "hash", "help", "history", "jobs", "kill", "let", "local", "logout",
        "mapfile", "popd", "printf", "pushd", "pwd", "read", "readarray", "readonly", "return", "set", "shift",
        "shopt", "source", "suspend", "test", "times", "trap", "true", "type", "typeset", "ulimit", "umask",
        "unalias", "unset", "wait",
    };

    /// <summary>bash's reserved words: <c>type</c> calls them "a shell keyword".</summary>
    public static readonly IReadOnlySet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "!", "[[", "]]", "{", "}", "case", "coproc", "do", "done", "elif", "else", "esac", "fi", "for", "function",
        "if", "in", "select", "then", "time", "until", "while",
    };
}
