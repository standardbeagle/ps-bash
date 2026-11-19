using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Who sees the <c>trap ... ERR</c> action. bash does not hand it to a shell function, a subshell or a command
/// substitution unless <c>set -E</c> (errtrace) is on: on the way in it puts the trap aside, so the body starts with
/// none, and a trap the body sets is its own (it fires there). A subshell's trap ends with it
/// (<see cref="BashShellState.Restore"/> puts the caller's back); a function's trap is the shell's, so leaving one puts
/// the caller's back only when the function left none set (bash <c>maybe_set_error_trap</c>):
/// <c>g() { trap 'echo E2' ERR; }; trap 'echo E1' ERR; g; false</c> prints E2, and <c>trap - ERR</c> in a function
/// leaves the caller's trap in force. The trap action lives in <c>$global:__BashTrapERR</c> (<c>Invoke-BashTrap</c>).
/// </summary>
public static class ErrTrapInheritance
{
    private const string TrapVariable = "global:__BashTrapERR";
    private const string ErrtraceVariable = "global:__BashErrtrace";

    /// <summary>
    /// A function, subshell or substitution body starts: without errtrace the ERR trap is put aside. Returns the action
    /// put aside (null when none was set, or errtrace keeps it in force).
    /// </summary>
    public static object? Hide(SessionState session)
    {
        if (LanguagePrimitives.IsTrue(session.PSVariable.GetValue(ErrtraceVariable))) return null;
        object? action = session.PSVariable.GetValue(TrapVariable);
        if (!LanguagePrimitives.IsTrue(action)) return null;
        session.PSVariable.Set(TrapVariable, null);
        return action;
    }

    /// <summary>A function returns: the trap <see cref="Hide"/> put aside comes back unless the function set one.</summary>
    public static void RestoreAfterFunction(SessionState session, object? hidden)
    {
        if (hidden is null) return;
        if (!LanguagePrimitives.IsTrue(session.PSVariable.GetValue(TrapVariable)))
            session.PSVariable.Set(TrapVariable, hidden);
    }
}
