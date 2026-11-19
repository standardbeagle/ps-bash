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
}
