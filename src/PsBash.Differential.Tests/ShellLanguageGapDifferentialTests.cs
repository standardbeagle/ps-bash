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

    // --- Positional slices as unquoted words (PsEmitter.IsUnquotedPositionalSliceWord) ----------------

    [SkippableFact]
    public Task PositionalSlice_AllForms() => Same(
        "set -- a b c d e\necho ${@:2:2} ${*: -1}\necho ${@:1}\necho ${@: -2}\n"
        + "for x in \"${@:2}\"; do echo \"[$x]\"; done\n"
        + "set -- \"a b\" c \"d e\"\nfor x in \"${@:2}\"; do echo \"<$x>\"; done\nfor x in \"${*:2}\"; do echo \"{$x}\"; done\n"
        + "printf '(%s)' ${@:1:2}; echo\n"
        + "f() { echo \"${@:2:1}\" \"${#@}\"; for y in \"${@: -2}\"; do echo \"f[$y]\"; done; }; f p \"q r\" s");

    // --- Operators on special parameters (BashParser.IsSpecialParameterWithOperator) -------------------

    [SkippableFact]
    public Task SpecialParameter_Operators() => Same(
        "sleep 0 & wait\necho ${!:+pid}\necho ${!:-x} | tr -d 0-9\nn=${#!}; [ \"$n\" -gt 0 ] && echo lenok\n"
        + "echo ${$:+pp} ${#$} | tr -d 0-9\nfalse; echo ${?:-q} ${?:+r}\ntrue; echo ${?:-q} ${?+s} ${#?}\n"
        + "echo ${-:+dash}\nset -- 1 2 3; echo ${#:+cnt} ${#:-z}\nset --; echo ${#:-z} ${#:+n}\necho ${0:+zero}");

    [SkippableFact]
    public Task SpecialParameter_UnsetBangBeforeAnyJob() => Same(
        "echo \"[${!:-none}] [${!:+x}] [${!-dash}]\"");

    // --- Associative keys with blanks, quotes, ] and expansions (BashParser.SubscriptedElementText) ----

    [SkippableFact]
    public Task AssocKeys_CompoundAndSubscriptAssignment() => Same(
        "declare -A m=([x y]=1 [\"q\\\"z\"]=2)\necho \"${m[x y]}\" \"${m[q\\\"z]}\"\n"
        + "k=\"p q\"; m[$k]=v; echo \"${m[$k]}\" \"${m[p q]}\"\n"
        + "declare -A n; k2='a]b'; n[$k2]=w; echo \"${n[$k2]}\"\n"
        + "j=x; declare -A o=([$j-1]=e [${j}2]=f); echo \"${o[x-1]}\" \"${o[x2]}\"\n"
        + "declare -A p=([\"sp ace\"]=s ['si ngle']=t); echo \"${p[sp ace]}\" \"${p[si ngle]}\" ${#p[@]}\n"
        + "for kk in \"${!m[@]}\"; do echo \"key<$kk>\"; done | sort\n"
        + "i=1; a=([$i]=x [2]=y); echo \"${a[@]}\"");

    // --- Read-write descriptors (Redirect.ReadWrite -> BashFdTable "<>") -----------------------------

    [SkippableFact]
    public Task ExecReadWrite_ReadsAndWritesOneDescriptor() => Same(
        "cd \"$(mktemp -d)\" || exit\necho hello > f\nexec 5<> f\nread -u 5 line; echo \"got=$line\"\nexec 5>&-\n"
        + "exec 6<> new; echo made >&6; exec 6>&-; cat new\n"
        + "printf 'abc\\n' > g; exec 7<>g; echo XY >&7; exec 7>&-; cat g");

    // --- help builtin (InvokeBashHelpCommand) ----------------------------------------------------------

    /// <summary><c>help NAME</c>'s first two lines, <c>-s</c>, <c>-d</c> (bare, it bound <c>-Debug</c>), a pattern with bash's
    /// header, a prefix (<c>help -s e</c>), an unknown topic; and a pipe that closes early ends neither the sourced script
    /// nor its caller.</summary>
    [SkippableFact]
    public Task Help_Builtin_FirstLinesAndForms() => Same(
        "help cd | head -2\nhelp -s pwd cd\nhelp -d umask cd\nhelp 'ec*' | head -3\nhelp -d 'ec*'\nhelp -s e\nhelp nosuch 2>/dev/null; echo rc=$?");

    [SkippableFact]
    public Task Help_PipeClosedEarly_InSourcedScript_DoesNotEndTheCaller() => Same(
        "cd \"$(mktemp -d)\" || exit\necho 'help cd | head -1; echo still' > s3\n"
        + "for f in s3 s3; do ( . \"./$f\" ) 2>&1 </dev/null; echo \"after $f rc=$?\"; done\necho done");
}
