using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.CompilerServices;

namespace PsBash.Cmdlets;

/// <summary>
/// bash's reader loop, as far as a script can see it: the number of top-level commands it has read (prompt <c>\#</c>,
/// bash's <c>current_command_number</c>) and, under <c>set -o history</c>, the history entry each one adds (prompt
/// <c>\!</c>, <c>history</c>). The whole script is transpiled before it runs, so the emitted script reports each
/// top-level command as it starts (<c>PsBuild.ReaderCommand</c>): one call per input line the reader takes in, a
/// multi-line compound command being one, <c>a; b</c> on one line being one. Statements inside a function, a sourced
/// file or an <c>eval</c> are not read by the reader and do not count (docs/specs/environment-model.md, prompt counters).
/// </summary>
public static class ShellReader
{
    private sealed class State { public int Commands; }

    // A runspace is one shell (the host runs each request in a fresh one); a background job's runspace has its own.
    private static readonly ConditionalWeakTable<Runspace, State> PerRunspace = new();
    private static readonly State NoRunspace = new();

    private static State Current => Runspace.DefaultRunspace is { } rs ? PerRunspace.GetValue(rs, _ => new State()) : NoRunspace;

    /// <summary><c>bash -c</c> (the launcher's mark): bash runs the string without its reader loop, so <c>\#</c> stays 0.</summary>
    private static bool DashC => Environment.GetEnvironmentVariable("PSBASH_DASH_C") == "1";

    /// <summary>
    /// An interactive shell's lines come from its front end (the line editor, or a piped <c>bash -i</c> replay), which
    /// records history itself (<c>history -s</c>); the text it runs is not the user's input, so it reports nothing.
    /// </summary>
    private static bool Interactive => InvokeBashShoptCommand.InteractiveShell;

    /// <summary>The reader took in one more top-level command (emitted before it; the script has no <c>history</c> use).</summary>
    public static void Next()
    {
        if (!DashC && !Interactive) Current.Commands++;
    }

    /// <summary>
    /// <see cref="Next()"/>, and under <c>set -o history</c> the command's text becomes a history entry before it runs,
    /// as bash adds a line when it reads it (<c>history</c> as the last command lists itself).
    /// </summary>
    public static void Next(SessionState session, string? line)
    {
        if (Interactive) return;
        Next();
        if (line is not null && InvokeBashShoptCommand.SetOptionOn("history")) InvokeBashHistoryCommand.Entries(session).Add(line);
    }

    /// <summary>Prompt <c>\#</c>: the number of the top-level command running now.</summary>
    public static int CommandNumber => CommandNumberFor(Current.Commands, DashC);

    /// <summary>
    /// The pure rule of <see cref="CommandNumber"/>: the count of commands read (1 before the first report: text with no
    /// reports, such as an interactive line); 0 under <c>bash -c</c>.
    /// </summary>
    internal static int CommandNumberFor(int commandsRead, bool dashC) => dashC ? 0 : Math.Max(commandsRead, 1);

    /// <summary>Prompt <c>\!</c> as <c>${v@P}</c> expands it: see <see cref="HistoryNumberFor"/>.</summary>
    public static int HistoryNumber(SessionState? session)
        => HistoryNumberFor(InvokeBashShoptCommand.SetOptionOn("history"), session is null ? 0 : InvokeBashHistoryCommand.EntryCount(session), Interactive);

    /// <summary>
    /// bash's <c>prompt_history_number</c> for <c>${v@P}</c>: with history off it is 1; on, it is the number of the
    /// command being read. In a script that is the entry count (the current command's own entry, added when read), 1
    /// when the list is empty (<c>history -c</c> as the current command); at an interactive prompt the next command is
    /// not in the list yet, so it is one past the count (bash 5.2: <c>bash -i</c> prompts <c>[1]</c>, <c>[2]</c>, ...).
    /// </summary>
    internal static int HistoryNumberFor(bool historyOn, int entries, bool interactive = false) =>
        !historyOn ? 1 : interactive ? entries + 1 : Math.Max(entries, 1);
}
