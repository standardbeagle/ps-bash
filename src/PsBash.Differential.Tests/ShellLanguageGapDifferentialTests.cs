using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Shell-language gaps the bash coverage probe (tests/bash-coverage) found, each checked against bash 5.2 and
/// closed. One class per bug class; the primitive each routes through is named on the test. Values that differ per
/// run (pids, times, group ids) are tested by shape, one name at a time, never by dumping the environment.
/// </summary>
public class ShellLanguageGapDifferentialTests
{
    private static Task Same(string script) => AssertOracle.EqualAsync(script, TimeSpan.FromSeconds(30));

    // --- Shell-owned variables (ShellVariables.Seeded / Computed) -----------------------------------

    /// <summary>IFS is $' \t\n' before any assignment; PS4, OPTIND and OPTERR have bash's start values.</summary>
    [SkippableFact]
    public Task ShellVariables_StartValues() => Same(
        "printf '%q\\n' \"$IFS\"; echo \"PS4=[$PS4] OPTIND=$OPTIND OPTERR=$OPTERR\"");

    [SkippableTheory]
    [InlineData("PPID")]
    [InlineData("SHLVL")]
    [InlineData("EUID")]
    [InlineData("UID")]
    [InlineData("EPOCHSECONDS")]
    [InlineData("SRANDOM")]
    [InlineData("BASHPID")]
    public Task ShellVariables_NumericValue(string name) => Same(
        $"[[ ${name} =~ ^[0-9]+$ ]] && echo {name}-num || echo {name}-bad");

    [SkippableTheory]
    [InlineData("BASH")]
    [InlineData("BASH_VERSION")]
    [InlineData("HOSTNAME")]
    [InlineData("HOSTTYPE")]
    [InlineData("MACHTYPE")]
    [InlineData("OSTYPE")]
    [InlineData("SHELL")]
    [InlineData("SHELLOPTS")]
    [InlineData("BASHOPTS")]
    [InlineData("COMP_WORDBREAKS")]
    [InlineData("PATH")]
    [InlineData("PWD")]
    [InlineData("RANDOM")]
    [InlineData("SECONDS")]
    [InlineData("LINENO")]
    public Task ShellVariables_SetAndNonEmpty(string name) => Same(
        $"echo \"{name}=${{{name}+set}} nonempty=${{{name}:+y}}\"");

    /// <summary>The array variables: GROUPS holds numbers, DIRSTACK the cwd, BASH_ALIASES the alias table.</summary>
    [SkippableFact]
    public Task ShellVariables_Arrays() => Same(
        "[[ ${GROUPS[0]} =~ ^[0-9]+$ ]] && echo groups-num\n"
        + "[[ ${DIRSTACK[0]} == \"$PWD\" ]] && echo dirstack-cwd n=${#DIRSTACK[@]}\n"
        + "alias ll='ls -l'; echo \"alias=${BASH_ALIASES[ll]}\"\n"
        + "echo \"vi=${BASH_VERSINFO[0]} fn=${FUNCNAME-unset}\"\n"
        + "v=FUNCNAME; echo \"ind=[${!v+set}][${!v}]\"; f() { echo \"inf=[${!v+set}][${!v}]\"; }; f");

    /// <summary>BASH_SUBSHELL counts subshell depth: a subshell, a nested one, and a command substitution.</summary>
    [SkippableFact]
    public Task ShellVariables_BashSubshellDepth() => Same(
        "echo $BASH_SUBSHELL; ( echo $BASH_SUBSHELL; ( echo $BASH_SUBSHELL ) ); echo \"c=$(echo $BASH_SUBSHELL)\"");

    /// <summary>SHLVL is exported (a child sees it), PPID is not (bash: it is shell-owned, never exported).</summary>
    [SkippableFact]
    public Task ShellVariables_ExportAttribute() => Same(
        "env | grep -c '^SHLVL='; env | grep -c '^PPID='; env | grep -c '^OPTIND='");

    // --- ERR trap inheritance (ErrTrapInheritance) ---------------------------------------------------

    /// <summary>
    /// A trap set inside a subshell or function fires there; <c>false || true</c> and an <c>if</c> condition never fire
    /// it; without <c>set -E</c> a function called from a subshell does not see the subshell's trap, with it, it does.
    /// </summary>
    [SkippableFact]
    public Task ErrTrap_SetInSubshellOrFunction_Fires() => Same(
        "( trap 'echo err' ERR; false )\n"
        + "g() { trap 'echo errg' ERR; false; echo after; }; g\ntrap - ERR\n"
        + "( trap 'echo errx' ERR; false || true; echo nofire )\n"
        + "( set -E; trap 'echo errE' ERR; h() { false; }; h )\n"
        + "( trap 'echo errN' ERR; h() { false; }; h )\n"
        + "( trap 'echo errI' ERR; if false; then :; fi; ! true; echo ok )\n"
        + "trap 'echo top' ERR; false; trap - ERR");

    /// <summary>
    /// A function's own trap replaces the caller's after it returns (bash maybe_set_error_trap); <c>trap - ERR</c> in a
    /// function leaves the caller's in force; a substitution sets its own.
    /// </summary>
    [SkippableFact]
    public Task ErrTrap_FunctionAndSubstitutionScope() => Same(
        "g() { trap 'echo E2' ERR; }; trap 'echo E1' ERR; g; false\n"
        + "u() { trap - ERR; }; trap 'echo E3' ERR; u; false; trap - ERR\n"
        + "x=$(trap 'echo E5' ERR; false); echo \"[$x]\"\n"
        + "trap 'echo E6' ERR; y=$(false); echo \"[$y]\"; trap - ERR");

    // --- Pipe-stage status when a downstream stage stops it (InvokeBashPipeStageCommand.RecordStopped) ---

    [SkippableFact]
    public Task PipeStatus_StageStoppedByHead_KeepsItsStatus() => Same(
        "nosuchcmd 2>&1 | head -1 >/dev/null; echo ${PIPESTATUS[0]}\n"
        + "nosuchcmd 2>/dev/null | cat; echo \"${PIPESTATUS[@]}\"\n"
        + "f() { return 3; }; f | cat; echo \"${PIPESTATUS[@]}\"\n"
        + "echo x | (exit 4); echo \"${PIPESTATUS[@]}\"\n"
        + "set -o pipefail; nosuchcmd 2>/dev/null | cat; echo \"pf=$?\"; f | cat; echo \"pf2=$?\"");
}
