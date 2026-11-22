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

    /// <summary>
    /// How a builtin NAMED AT RUN TIME (<c>"$@"</c>, <c>$cmd</c>, <c>command NAME</c>, <c>builtin NAME</c>) runs. Every
    /// name in <see cref="Names"/> is in exactly one of three groups (guard: <c>Core.Tests/BuiltinDispatchGuardTests</c>):
    /// <list type="bullet">
    /// <item><see cref="RunAsSource"/>: the emitter implements it (no run-time command does the same). A run-time call is
    /// the emitter's own static form: <c>NAME 'arg'...</c> transpiled and run in the caller
    /// (<see cref="PsBuild.BuiltinAsSourceScript"/>), so <c>c=eval; $c 'x=1'</c>, <c>"$@"</c> running <c>set -- a b</c> or
    /// <c>. ./f</c> act exactly as the literal command does. A run-time lookup that only knew the run-time commands said
    /// <c>eval: command not found</c> (127) for these.</item>
    /// <item><see cref="CallStackBuiltins"/>: Resolve-BashCommand's own blocks, which act on the caller's PowerShell call
    /// stack (<c>break</c> leaves the loop in reach at the call site, Resolve-BashCommand -LoopMode) or are trivially cheap.</item>
    /// <item>every other name: the static emitter passes it through to <c>Invoke-Bash&lt;Name&gt;</c>, the command the run-time
    /// lookup finds by the same name.</item>
    /// </list>
    /// </summary>
    public static readonly IReadOnlySet<string> RunAsSource = new HashSet<string>(StringComparer.Ordinal)
    {
        ".", "alias", "cd", "declare", "eval", "exec", "exit", "export", "fc", "let", "local", "readarray", "readonly", "set",
        "source", "trap", "typeset", "unalias",
    };

    /// <summary>Builtins Resolve-BashCommand runs itself (see <see cref="RunAsSource"/>).</summary>
    public static readonly IReadOnlySet<string> CallStackBuiltins = new HashSet<string>(StringComparer.Ordinal)
    {
        ":", "[", "true", "false", "break", "continue", "return", "builtin",
    };

    /// <summary>
    /// The bash source of a run-time call of the <see cref="RunAsSource"/> builtin <paramref name="name"/>: its operands
    /// are already expanded words, so each is single-quoted (<c>eval 'x=1'</c>, <c>set '--' 'a b'</c>).
    /// </summary>
    public static string SourceFor(string name, string[]? operands)
    {
        if (!RunAsSource.Contains(name))
            throw new ArgumentException("not a builtin the emitter implements: " + name, nameof(name));
        var sb = new System.Text.StringBuilder(name);
        if (operands is not null)
        {
            foreach (var op in operands)
                sb.Append(" '").Append((op ?? "").Replace("'", "'\\''", StringComparison.Ordinal)).Append('\'');
        }
        return sb.ToString();
    }

    /// <summary>bash's reserved words: <c>type</c> calls them "a shell keyword".</summary>
    public static readonly IReadOnlySet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "!", "[[", "]]", "{", "}", "case", "coproc", "do", "done", "elif", "else", "esac", "fi", "for", "function",
        "if", "in", "select", "then", "time", "until", "while",
    };
}
