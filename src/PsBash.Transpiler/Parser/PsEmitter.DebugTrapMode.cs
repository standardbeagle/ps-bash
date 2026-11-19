using System.Text.RegularExpressions;

namespace PsBash.Core.Parser;

public static partial class PsEmitter
{
    private static readonly Regex DebugTrapSet = new(@"\btrap\s+(?:'[^'\n]*'|\x22[^\x22\n]*\x22)\s+DEBUG\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// True when <paramref name="bash"/> sets a <c>trap ... DEBUG</c> AND runs some other command, so every command
    /// after it runs the action first (bash semantics). Text that is nothing but the trap (an interactive prompt hook
    /// typed on its own line) keeps the directory-change mapping (<c>Register-BashChpwdHook</c>).
    /// <para>
    /// It used to require three non-comment LINES, so <c>trap 'echo D' DEBUG; echo a</c> (one line) and a two-line
    /// script never ran the action. What counts is whether anything besides the trap statement is left, on any line.
    /// Spec: error-handling.md section 4.4. Test: <c>DebugTrapDifferentialTests</c> (one-line form).
    /// </para>
    /// </summary>
    internal static bool SetsDebugTrapForOtherCommands(string bash)
    {
        if (!DebugTrapSet.IsMatch(bash)) return false;
        foreach (var line in DebugTrapSet.Replace(bash, "").Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0 || text[0] == '#') continue;
            if (text.Trim(';', '&', ' ', '\t', '\r').Length > 0) return true;
        }
        return false;
    }
}
