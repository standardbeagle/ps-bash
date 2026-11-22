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

    // --- Condition and loop status (PsBuild.ConditionStatus, EmitWhileLoop, EmitLoopControl) -------------------

    /// <summary>A condition is a command: a test (<c>[[ ]]</c>, <c>[ ]</c>, <c>(( ))</c>, <c>true</c>) sets <c>$?</c> for the
    /// branch, the <c>else</c>, an <c>elif</c> and the loop body, and an <c>if</c> whose branch is not taken is 0 (a <c>case</c>
    /// that matches nothing, or whose arm runs nothing, is 0 too). Folding a test
    /// to a bare boolean left the status of the command before it.</summary>
    [SkippableFact]
    public Task Condition_TestSetsTheStatusTheBranchSees() => Same(
        "false; if true; then echo if=$?; fi\ntrue; if false; then :; else echo else=$?; fi\n"
        + "false; if [[ a == a ]]; then echo dbl=$?; fi\ntrue; if [[ a == b ]]; then :; else echo dblf=$?; fi\n"
        + "false; if [ a = a ]; then echo sgl=$?; fi\nfalse; if [ a -eq 1 ] 2>/dev/null; then :; else echo bad=$?; fi\n"
        + "true; if (( 0 )); then :; else echo arithf=$?; fi\ntrue; if false; then :; elif true; then echo elif=$?; fi\n"
        + "true; if true && [[ a == b ]]; then :; else echo andf=$?; fi\ntrue; if false; then :; fi; echo nobranch=$?\n"
        + "false; while [[ x ]]; do echo wdbl=$?; break; done\ni=0; until (( i >= 2 )); do echo u-in=$?; i=$((i+1)); done\n"
        + "false; case x in y) ;; esac; echo case-nomatch=$?\nfalse; case x in x) ;; esac; echo case-empty=$?\ncase x in x) false;; esac; echo case-body=$?");

    /// <summary>A loop's status is its last body command's (0 when the body never ran), never the failed condition's or the
    /// step's; <c>break</c> and <c>continue</c> are 0.</summary>
    [SkippableFact]
    public Task Loop_StatusIsTheBodysNotTheConditions() => Same(
        "while true; do false; break; done; echo brk=$?\n"
        + "i=0; while [ $i -lt 2 ]; do i=$((i+1)); false; continue; done; echo cont=$?\n"
        + "i=0; while (( i < 2 )); do i=$((i+1)); false; done; echo body-last=$?\n"
        + "i=0; while (( i < 2 )); do i=$((i+1)); done; echo body-ok=$?\n"
        + "for ((i=0; i<2; i++)); do false; done; echo cfor=$?\nfalse; for ((i=0; i<0; i++)); do :; done; echo cfor-empty=$?\n"
        + "false; while false; do :; done; echo wempty=$?\nfalse; until true; do :; done; echo uempty=$?\n"
        + "for x in a b; do if [ $x = b ]; then break; fi; false; done; echo forin-brk=$?");

    // --- Positional parameters (PsBuild.PositionalIsShells) ------------------------------------------------------

    /// <summary>An empty positional list is still the shell's: <c>shift</c> to empty ends a function's
    /// <c>while [ "$1" ]</c> loop, and <c>set --</c> empties <c>$1</c>/<c>$#</c>/<c>$@</c> at the top too.</summary>
    [SkippableFact]
    public Task ShiftToEmpty_InAFunction_EndsTheLoop() => Same(
        "f() { while [ \"$1\" ]; do echo \"arg=$1\"; shift; done; echo \"left=$# [$1] [$*]\"; }\nf a b\n"
        + "set -- x y; set --; echo \"top=$# [$1] [$@]\"\ng() { shift 2; echo \"g=$# [$1]\"; }\ng p q");

    /// <summary>A background job is a forked subshell: it sees the shell's arrays, associative arrays, a function's local
    /// arrays, <c>BASH_REMATCH</c> and the positional parameters, as copies its changes do not leave. An in-process job
    /// got only the shell's options, so every one of these read empty in it.</summary>
    [SkippableFact]
    public Task BackgroundJob_InheritsArraysPositionalsAndRematch() => Same(
        "set -- a1 a2; { echo \"pos=$1 n=$#\"; } & wait\n"
        + "[[ abc =~ b(c) ]]; { echo \"rem=${BASH_REMATCH[1]}\"; } & wait\n"
        + "g() { { echo \"fpos=$1\"; } & wait; }; g x\n"
        + "a=(1 2 3); { echo \"len=${#a[@]} all=${a[*]}\"; } & wait\n"
        + "declare -A m=([k]=v [j]=w); { echo \"assoc=${m[k]}${m[j]} n=${#m[@]}\"; } & wait\n"
        + "{ a[0]=X; shift; echo \"inner=${a[0]} $1\"; } & wait; echo \"outer=${a[0]} $1\"\n"
        + "h() { local -a la=(l1 l2); local -A lm=([x]=y); { echo \"local=${la[1]} ${lm[x]}\"; } & wait; }; h\n"
        + "{ echo \"again=${#a[@]}\"; } & wait");

    /// <summary>An arithmetic expansion error is bash's DISCARD: the rest of the top-level command is dropped (the commands
    /// after it on its line, a function and the line that called it, a whole loop), status 1, and the next line runs; a
    /// subshell or command substitution stops it there. The arithmetic COMMAND <c>(( 1/0 ))</c> only fails. ps-bash ran
    /// the rest of the line. stderr is closed: after a multi-line loop bash's own line numbers run low. (A script file
    /// drops the line for an array subscript error too; under <c>bash -c</c>, as here, that one ends the shell: below.)</summary>
    [SkippableFact]
    public Task ArithmeticExpansionError_DiscardsTheRestOfTheLine() => Same(
        "exec 2>/dev/null\n"
        + "echo a; echo $((1/0)); echo same1\necho \"L2 st=$?\"\n"
        + "f() { echo $((1/0)); echo infn; }; f; echo same3\nf\necho L5\n"
        + "( echo $((1/0)); echo sub ); echo \"same7 st=$?\"\n"
        + "x=$((1/0)); echo same8\n(( 1/0 )); echo \"same9 st=$?\"\n"
        + "if [ $((1/0)) ]; then echo then; fi; echo same10\n"
        + "y=$(echo $((1/0)); echo insub); echo \"same11 y=$y\"\n"

        + "for i in 1 2; do\n  echo \"it$i $((1/0))\"\n  echo in\ndone\necho \"end st=$?\"");

    /// <summary>
    /// Under <c>bash -c</c> an arithmetic error in an array subscript ends the shell, status 1, wherever it happens (a
    /// function, eval, a sourced file, inside <c>$(( ))</c>, the arithmetic command and <c>let</c>); a <c>( )</c> or
    /// <c>$( )</c> ends only itself. A plain
    /// <c>$((1/0))</c> drops only its line. (A script file or stdin drops the line for both.) ps-bash ran on.
    /// </summary>
    [SkippableTheory]
    [InlineData("echo ${a[1/0]}; echo same")]
    [InlineData("a[i/0]=x; echo same")]
    [InlineData("echo \"${#a[1/0]}\"; echo same")]
    [InlineData("x=${a[1/0]}; echo same")]
    [InlineData("f() { echo ${a[1/0]}; echo infn; }; f; echo same")]
    [InlineData("eval 'echo ${a[1/0]}'; echo same")]
    [InlineData("echo $(( a[1/0] + 1 )); echo same")]
    [InlineData("( echo ${a[1/0]} ); y=$(echo ${a[1/0]}); echo \"same st=$? y=$y\"")]
    [InlineData("(( a[1/0] )); echo same")]
    [InlineData("let 'a[1/0]'; echo same")]
    [InlineData("( (( a[1/0] )); echo in ); y=$( (( a[1/0] )); echo in2 ); echo \"same st=$? y=$y\"")]
    public Task SubscriptArithmeticError_EndsADashCShell(string line) => Same(
        "exec 2>/dev/null\na=(1 2)\n" + line + "\necho \"next a=${a[*]}\"");

    /// <summary>
    /// A builtin handed an element operand evaluates its subscript at run time (<c>unset 'a[1/0]'</c>, <c>read</c>,
    /// <c>printf -v</c>, <c>test -v</c> / <c>[ -v ]</c>), also through a variable (<c>i=1/0; unset 'a[i]'</c>) and for a
    /// scalar or an unset name (<c>test -v 'x[1/0]'</c>; <c>unset</c> skips an unset name). One that is not an expression
    /// is bash's expansion error: under <c>bash -c</c> it ends the shell, a script drops the rest of the line
    /// (<see cref="BuiltinElementOperand_BadSubscript_DropsTheLineOfAScript"/>). REGRESSION: each builtin caught it as a
    /// plain failure, status 1, and the line went on (<c>BashArrayElement.FailSubscript</c> is now the one path).
    /// </summary>
    [SkippableTheory]
    [InlineData("unset 'a[1/0]'; echo same")]
    [InlineData("read 'a[1/0]' <<< hi; echo same")]
    [InlineData("printf -v 'a[1/0]' x; echo same")]
    [InlineData("test -v 'a[1/0]'; echo same")]
    [InlineData("[ -v 'a[1/0]' ]; echo same")]
    [InlineData("i=1/0; unset 'a[i]'; echo same")]
    [InlineData("b=x; unset 'b[1/0]'; echo same")]
    [InlineData("unset 'nosuch[1/0]'; echo \"unset name $?\"; test -v 'nosuch[1/0]'; echo same")]
    [InlineData("( unset 'a[1/0]'; echo in ); y=$(printf -v 'a[1/0]' x; echo in2); echo \"same st=$? y=$y\"")]
    public Task BuiltinElementOperand_BadSubscript_EndsADashCShell(string line) => Same(
        "a=(1 2)\n" + line + "\necho \"next a=${a[*]}\"");

    /// <summary>The script-file half of <see cref="BuiltinElementOperand_BadSubscript_EndsADashCShell"/>: the rest of the
    /// line is dropped, the next line runs, and the script ends 0. The emitter arms the line scope for such a builtin
    /// (<c>PsBuild.CanDiscardLine</c>) and records its line for the diagnostic.</summary>
    [SkippableTheory]
    [InlineData("unset 'a[1/0]'; echo same")]
    [InlineData("read 'a[1/0]' <<< hi; echo same")]
    [InlineData("printf -v 'a[1/0]' x; echo same")]
    [InlineData("test -v 'a[1/0]'; echo same")]
    [InlineData("[ -v 'a[1/0]' ]; echo same")]
    [InlineData("f() { unset 'a[1/0]'; echo infn; }\nf; echo same")]
    public Task BuiltinElementOperand_BadSubscript_DropsTheLineOfAScript(string line) => Same(
        "cd \"$(mktemp -d)\" || exit\ncat > t.sh <<'EOF'\na=(1 2)\n" + line + "\necho \"next a=${a[*]}\"\nEOF\nbash t.sh 2>&1; echo \"rc=$?\"");

    /// <summary>
    /// <c>read</c> takes an array element as a target, like <c>printf -v</c> (both through <c>BashArrayElement.Assign</c>):
    /// an arithmetic or associative subscript, a variable holding an expression, a negative one past the start (bad
    /// subscript, status 1); <c>test -v</c> sees a sparse array's holes. REGRESSION: <c>read 'a[1]'</c> failed with
    /// "not a valid identifier" and set nothing; <c>test -v</c> looked a sparse array's position up as a word.
    /// </summary>
    [SkippableFact]
    public Task Read_IntoArrayElements() => Same(
        "a=(1 2); read 'a[1]' <<< hi; echo \"r=$? ${a[*]}\"\n" +
        "i=0; read -r 'a[i+2]' <<< yo; echo \"${a[*]}\"\n" +
        "declare -A m; k=key; read \"m[$k]\" <<< v; echo \"${m[key]}\"\n" +
        "read 'a[0]' b <<< 'p q'; echo \"${a[*]} b=$b\"\n" +
        "read 'a[-9]' <<< x; echo \"neg $?\"\n" +
        "unset 'a[1]'; j='0+2'; test -v 'a[j]'; echo \"v=$?\"; test -v 'a[1]'; echo \"h=$?\"; test -v 'a[-1]'; echo \"n=$?\"");

    /// <summary>
    /// A failed clause of <c>for ((init; cond; step))</c> ends the loop with status 1: a bad init or condition before the
    /// body ever runs, a bad step after that pass. ps-bash ran on (an empty condition is true: <c>for ((i=1/0;;))</c> hit
    /// the loop limit) or kept the body's status.
    /// </summary>
    [SkippableFact]
    public Task ForArithClauseError_EndsTheLoopWithStatus1() => Same(
        "exec 2>/dev/null\n"
        + "for ((i=1/0;;)); do echo body0; break; done; echo \"s0 st=$?\"\n"
        + "for ((;1/0;)); do echo body1; break; done; echo \"s1 st=$?\"\n"
        + "n=0; for ((;n<3;n=n/0)); do echo body2 $n; n=1; done; echo \"s2 st=$?\"\n"
        + "for ((i=0;i<3;i++)); do echo ok$i; done; echo \"s3 st=$?\"\n"
        + "for ((i=0;i<2;i++)); do false; done; echo \"s4 st=$?\"\n");

    /// <summary>
    /// With a DEBUG trap anywhere in the text a <c>for ((;;))</c> is emitted another way (EmitDebugForArith): it lost the
    /// body's status (the step's was left), an empty condition did not parse (<c>if (-not ())</c>), and a failed clause
    /// did not end it.
    /// </summary>
    [SkippableFact]
    public Task ForArithUnderADebugTrap_KeepsBashStatus() => Same(
        "exec 2>/dev/null\n"
        + "for ((;1/0;)); do echo body1; break; done; echo \"s1 st=$?\"\n"
        + "n=0; for ((;n<3;n=n/0)); do echo body2 $n; n=1; done; echo \"s2 st=$?\"\n"
        + "for ((i=0;i<2;i++)); do false; done; echo \"s4 st=$?\"\n"
        + "trap 'echo dbg' DEBUG\n"
        + "for ((i=1/0;;)); do echo body5; break; done; echo \"s5 st=$?\"\n"
        + "trap - DEBUG\n"
        + "trap 'echo d2' DEBUG\n"
        + "for ((i=0;i<2;i++)); do false; done; echo \"s6 st=$?\"\n"
        + "for ((i=0;i<2;i++)); do continue; done; echo \"s7 st=$?\"\n"
        + "trap - DEBUG\n");

    // --- Subshell, stdin-script and builtin-usage boundaries (checked against bash 5.2) ------------------

    /// <summary>Runs <paramref name="body"/> as the script file <c>t.sh</c>: diagnostics name it and the line, as a
    /// script does (under <c>bash -c</c> they would say <c>bash: line N</c>), and the script's status is printed.</summary>
    private static Task SameAsScript(string body) => Same(
        "cd \"$(mktemp -d)\" || exit\ncat > t.sh <<'EOF'\n" + body + "\nEOF\n{ bash t.sh; echo \"rc=$?\"; } 2>&1");

    /// <summary>
    /// <c>source</c> / <c>.</c> read their file through the device choke point: <c>/dev/stdin</c>, <c>/dev/fd/N</c>, a process
    /// substitution, <c>/dev/null</c>, and a regular file read with the caller's stdin (here-string, here-document, <c>&lt;</c>
    /// file, pipe), whose rest the sourced script's own reads go on with. REGRESSION: <c>/dev/stdin</c> was opened as a file
    /// and a sourced script's <c>read</c> did not see the redirect.
    /// </summary>
    [SkippableFact]
    public Task Source_FromDevicesAndStdin() => SameAsScript(
        """
        {
        printf 'read l; echo "got $l"; cat\n' > f
        source ./f <<< $'hello\nrest'
        . ./f <<EOF2
        a1
        a2
        EOF2
        . ./f < f
        x=0; . /dev/stdin <<< 'x=5'; echo "x=$x"
        source /dev/stdin <<< "echo hi1; echo \"src=\${BASH_SOURCE[0]}\""; . /dev/stdin <<EOF2
        echo hi2
        cd /nonexist_zz
        EOF2
        echo "echo hi3" | source /dev/stdin; source <(echo echo hi4); . /dev/fd/0 <<< "echo hi5"; echo "rc=$?"
        . /dev/null; echo "null=$?"
        exec 3<<< 'echo hi6'; . /dev/fd/3; echo "fd3=$?"
        printf 'x=1\ny=2' | { . /dev/stdin; echo "x=$x y=$y"; }
        source /dev/stdin <<< 'g() { cd /nonexist_g; }'; g
        . ./nofile < f; echo "missing=$?"
        . ./f < nofile; echo "noin=$?"
        printf 'set -- a b; echo "n=$#"' | . /dev/stdin; echo "outer n=$#"
        } 2>&1
        """);

    /// <summary>
    /// A here-string's word is expanded like any word and quote-removed (escaped quotes, mixed quoting, <c>$( )</c>,
    /// backquotes, <c>${x/v/\"}</c>, <c>$'..'</c>, <c>"$@"</c> joined), never globbed or brace-expanded, with one newline
    /// added. REGRESSION: the word's raw text was re-scanned and kept its backslashes (<c>HereDoc.Word</c>, <c>PsBuild.HereStringValue</c>).
    /// </summary>
    [SkippableFact]
    public Task HereString_IsOneQuoteRemovedWord() => SameAsScript(
        """
        x=v
        cat <<< a\"b
        cat <<< "a"'b'$x
        cat <<< "$(echo \"in\")"
        cat <<< "a\\\"b"
        cat <<< "${x/v/\"}"
        cat <<< "`echo \"bq\"`"
        cat <<< "t\ab\n"
        cat <<< *.sh
        cat <<< a[1]{b,c}
        cat <<< ~/x | sed "s#$HOME#H#"
        set -- p1 "p 2"; cat <<< "$@"; cat <<< $*
        a=(x y); cat <<< "${a[@]}"
        cat <<< $'tab\there'
        cat <<< "$((2+3))"
        read -r l <<< "  lead \"q\"  "; echo "[$l]"
        while read -r w; do echo "w=$w"; done <<< "a \"b\""
        mapfile -t m <<< $'1\n2'; echo "${#m[@]}"
        cat <<< ''
        cat <<< "multi
        line"
        exec 4<<< "fd \"4\""; cat <&4
        y=$(cat <<< "in \"sub\""); echo "$y"
        cat <<< "$(printf 'a\n\n')"
        """);

    /// <summary>
    /// Every stage of a pipeline is a subshell: <c>.</c>, <c>source</c>, <c>eval</c>, a group, a function and <c>shift</c> (also
    /// behind <c>builtin</c>) change nothing in the caller, first stage or last. REGRESSION: a sourced file or eval in a stage
    /// set the caller's positional parameters and variables (<c>SubshellStageBuiltins</c>).
    /// </summary>
    [SkippableFact]
    public Task PipeStage_SourceEvalAndFunctionsRunInASubshell() => SameAsScript(
        """
        echo 'set -- a b; v=1' > f
        set -- x; v=; echo q | . ./f; echo "src n=$# v=$v"
        set -- x; v=; echo q | source ./f; echo "source n=$# v=$v"
        set -- x; v=; echo q | eval 'set -- a b; v=2'; echo "eval n=$# v=$v"
        set -- x; v=; c='set -- a b; v=2'; echo q | eval "$c"; echo "evaldyn n=$# v=$v"
        set -- x; v=; echo q | { set -- a b; v=3; }; echo "grp n=$# v=$v"
        h() { set -- a b; v=4; }
        set -- x; v=; echo q | h; echo "fn n=$# v=$v"
        set -- x; echo q | shift; echo "shift n=$#"
        set -- x; echo q | builtin shift; echo "bshift n=$#"
        set -- x; v=; . ./f | cat; echo "first src n=$# v=$v"
        """);

    /// <summary>
    /// State builtins in a pipe stage (<c>cd</c>, <c>set</c>, <c>export</c>, <c>declare</c>, <c>unset</c>, <c>let</c>,
    /// <c>read</c>, <c>shopt</c>, <c>alias</c>, <c>umask</c>, <c>trap</c>, <c>getopts</c>, <c>readonly</c>, <c>mapfile</c>,
    /// <c>return</c>, <c>exec</c>) act in that subshell only; <c>echo q | exec cat</c> reads the pipe. REGRESSION: several
    /// leaked into the caller, and <c>exec cat</c> printed nothing.
    /// </summary>
    [SkippableFact]
    public Task PipeStage_StateBuiltinsChangeNothingOutside() => SameAsScript(
        """
        {
        mkdir sub; echo 'set -- a b; v=1' > f
        set -- x; v=; top=$PWD
        cd sub | cat; [ "$PWD" = "$top" ] && echo cd-first-kept
        set -- a b | cat; echo "set first n=$#"
        echo q | cd sub; [ "$PWD" = "$top" ] && echo cd-last-kept
        echo q | export Z=1; echo "export Z=${Z-unset}"
        echo q | declare w=1; echo "declare w=${w-unset}"
        echo q | unset v; echo "unset v=${v-unset}"
        v=; echo q | let v=3; echo "let v=$v"
        echo q | read r | cat; echo "read mid r=${r-unset}"
        echo q | set -- a b | cat; echo "set mid n=$#"
        f2() { echo q | return 3; echo "after return $?"; }; f2
        echo q | exec cat; echo "exec rc=$?"
        echo q | shopt -s nullglob; shopt -q nullglob; echo "shopt=$?"
        echo q | alias zz=echo; alias zz 2>/dev/null; echo "alias=$?"
        u=$(umask); echo q | umask 077; [ "$(umask)" = "$u" ] && echo umask-kept
        echo q | trap 'echo trapped' USR1; trap -p USR1; echo "trap=$?"
        OPTIND=1; echo q | getopts a o -a; echo "getopts OPTIND=$OPTIND"
        echo q | readonly ro=1; ro=2; echo "ro=$ro"
        for i in 1 2; do echo q | break; echo "loop $i"; done
        echo q | mapfile -t m; echo "mapfile ${#m[@]}"
        set -- x y; echo q | shift; echo "shift n=$#"
        echo q | eval 'v=e'; echo "eval v=$v"
        echo q | . ./f; echo "src n=$# v=$v"
        x=$(echo q | set -- a b c; echo "in=$#"); echo "$x out=$#"
        } 2>&1
        """);

    /// <summary>
    /// <c>break</c> / <c>continue</c> in a pipe stage, a group stage or a subshell ends that subshell only: the loop goes on
    /// (a stage outside any loop warns, status 0). REGRESSION: the stage's break left the caller's loop.
    /// </summary>
    [SkippableFact]
    public Task PipeStage_LoopControlLeavesOnlyItsSubshell() => SameAsScript(
        """
        {
        break | cat; echo "a $?"
        echo q | break; echo "b $?"
        for i in 1 2; do break | cat; echo "c$i"; done
        for i in 1 2; do echo q | continue; echo "d$i"; done
        for i in 1 2; do echo q | eval break; echo "e$i"; done
        for i in 1 2; do eval break | cat; echo "f$i"; done
        for i in 1 2; do echo q | break 2; echo "g$i"; done
        for i in 1 2; do echo q | { break; }; echo "grp loop $i"; done
        for i in 1 2; do ( break ); echo "sub loop $i"; done
        } 2>&1
        """);

    /// <summary>
    /// A <c>( )</c> subshell's <c>umask</c>, <c>readonly</c>, <c>mapfile</c> and array assignments (indexed and associative)
    /// stay in it. REGRESSION: they reached the caller.
    /// </summary>
    [SkippableFact]
    public Task Subshell_IsolatesUmaskReadonlyAndArrays() => SameAsScript(
        """
        {
        u=$(umask); ( umask 077 ); [ "$(umask)" = "$u" ] && echo umask-kept
        ( readonly ro=1 ); ro=2; echo "ro=$ro"
        ( mapfile -t m <<< x ); echo "mapfile ${#m[@]}"
        ( arr=(1 2) ); echo "arr ${#arr[@]}"
        ( declare -A as=([k]=v) ); echo "assoc ${#as[@]}"
        } 2>&1
        """);

    /// <summary>
    /// <c>exec CMD</c> reads its own here-string / <c>&lt;</c> file when it has one, else the stdin it inherits (a pipe, a
    /// group's redirect, a subshell's), and ends only its subshell. REGRESSION: <c>echo q | exec cat</c> and
    /// <c>{ exec cat; } &lt; f</c> printed nothing; the fix's first cut displaced <c>( exec cat &lt;&lt;&lt; q )</c>'s own input.
    /// </summary>
    [SkippableFact]
    public Task Exec_ReadsItsOwnRedirectOrTheInheritedStdin() => SameAsScript(
        """
        {
        printf 'l1\nl2\n' > f
        ( exec cat <<< a1 )
        echo m1
        ( exec echo a2 )
        x=$( exec cat <<< a4 ); echo "x=$x"
        ( exec cat <<< a5 ) | cat
        ( exec cat < <(echo q3) )
        ( exec cat ) < f
        echo q | exec sort -r; echo "b rc=$?"
        x=$(echo zz | exec cat); echo "x=$x"
        while read -r l; do echo "r=$l"; done < f | exec cat
        echo q | exec cat; echo "a rc=$?"
        { exec cat; } < f; echo "unreached"
        } 2>&1
        """);

    /// <summary>
    /// <c>break</c> / <c>continue</c> / <c>return</c> / <c>exit</c> / <c>shift</c> / <c>history</c> with a bad operand report
    /// with the shell prefix and the line they are on: "numeric argument required" (break/continue end the shell with 128;
    /// return and exit 2; shift 1, the line goes on), a loop jump with no loop warns. REGRESSION: no prefix, an earlier
    /// statement's line, and <c>( exit q )</c> ran a command <c>q</c>.
    /// </summary>
    [SkippableFact]
    public Task NumericArgBuiltins_ReportWithTheLineAndStatus() => SameAsScript(
        """
        {
        ( for i in 1; do break x; done ); echo "b rc=$?"
        ( for i in 1; do v=y; continue $v; done ); echo "c rc=$?"
        f() { return x; }; f; echo "r rc=$?"
        g() { v=z; return $v; }; g; echo "r2 rc=$?"
        ( exit q ); echo "e rc=$?"
        ( v=w; exit $v ); echo "e2 rc=$?"
        shift x; echo "s rc=$?"
        ( exit "" ); echo "e0 rc=$?"
        ( exit "3x" ); echo "e5 rc=$?"
        ( exit " 4 " ); echo "e6 rc=$?"
        history x; echo "h rc=$?"
        cd /nonexistent_zz; break; continue 2; f() { break; }; f; eval break
        } 2>&1
        """);

    /// <summary>
    /// "too many arguments" (bash <c>no_args</c>) sets status 1 and drops the rest of the line, a loop or function on it
    /// included, and the script goes on with the next line; <c>exit 1 2</c> does not exit (<c>PsBuild.UsageDiscard</c>,
    /// <c>BashLineDiscard.UsageError</c>). REGRESSION: no message, and the rest of the line ran.
    /// </summary>
    [SkippableFact]
    public Task NumericArgBuiltins_TooManyArgumentsDropsTheLine() => SameAsScript(
        """
        {
        g() { return 1 2; echo in-g; }; g; echo "after-g rc=$?"
        echo next2
        for i in 1 2; do shift 1 2; echo "loop $i"; done; echo "after-loop rc=$?"
        echo next3
        for i in 1 2; do break 1 2; echo "bl $i"; done; echo "after-bl rc=$?"
        echo next4
        ( exit 1 2; echo in-sub ); echo "after-sub rc=$?"
        echo next5
        x=$(exit 1 2; echo in-cs); echo "cs=$x rc=$?"
        echo next6
        exit 1 2; echo "after rc=$?"
        echo next7
        { exit 1 2; echo "after2 rc=$?"; }
        echo pre; exit 1 2
        echo "after3 rc=$?"
        } 2>&1
        """);

    /// <summary>
    /// Under <c>bash -c</c> the whole string is the input bash discards: "too many arguments" ends the shell, status 1, and
    /// a later line never runs; inside a subshell, command substitution or pipe stage only that child ends.
    /// </summary>
    [SkippableFact]
    public Task NumericArgBuiltins_TooManyArgumentsEndsADashCShell() => Same(
        """
        cd "$(mktemp -d)" || exit
        { bash -c 'exit 1 2; echo after1
        echo next1'; echo "st=$?"; } 2>&1
        { bash -c 'for i in 1 2; do break 1 2; echo in; done; echo after2
        echo next2'; echo "st=$?"; } 2>&1
        { bash -c 'set -- a; shift 1 2; echo after3
        echo next3'; echo "st=$?"; } 2>&1
        { bash -c 'f() { return 1 2; }; f; echo after4
        echo next4'; echo "st=$?"; } 2>&1
        { bash -c '( exit 1 2; echo in5 ); echo "after5 $?"
        echo next5'; echo "st=$?"; } 2>&1
        { bash -c 'x=$(shift 1 2; echo in6); echo "after6 [$x] $?"
        echo next6'; echo "st=$?"; } 2>&1
        { bash -c 'echo a | { shift 1 2; echo in7; }; echo "after7 $?"
        echo next7'; echo "st=$?"; } 2>&1
        """);

    /// <summary>
    /// A function knows where it was defined: <c>BASH_SOURCE</c>, <c>FUNCNAME</c>, <c>BASH_LINENO</c>, <c>caller</c> and the
    /// diagnostic prefix name the defining file (a sourced library), <c>main</c> for a stdin script, the <c>-c</c> name for
    /// <c>bash -c</c> (<c>FunctionSources</c>, <c>ShellCaller</c>). REGRESSION: a library function's errors named the caller's file.
    /// </summary>
    [SkippableFact]
    public Task FunctionSource_AndCaller() => Same(
        """
        cd "$(mktemp -d)" || exit
        cat > lib.sh <<'EOF'
        lf() {
          echo "lf src=${BASH_SOURCE[0]} caller-src=${BASH_SOURCE[1]} fn=${FUNCNAME[*]} ln=${BASH_LINENO[*]}"
          cd /nonexist_lf
          caller 0
          inner
        }
        inner() { caller; caller 0; caller 1; caller 2; echo "c2=$?"; caller x; echo "cx=$?"; }
        EOF
        cat > main.sh <<'EOF'
        . ./lib.sh
        mf() { lf; }

        mf
        echo "top caller:"; caller; echo "tc=$?"
        EOF
        { bash main.sh; echo "rc=$?"; } 2>&1
        { bash -c 'g() { cd /nonexist_g; echo "src=${BASH_SOURCE[0]}-${#BASH_SOURCE[@]}"; caller 0; }; g'; } 2>&1
        { bash -c 'g() { cd /nonexist_g; }
        g' me; } 2>&1
        { printf 'h() { cd /nonexist_h; echo "s=$- src=${BASH_SOURCE[0]}"; }\nh\n' | bash; } 2>&1
        """);

    /// <summary>
    /// A nested <c>bash</c> with no program operand reads its script from stdin (<c>bash &lt; f</c>, <c>cat f | bash</c>,
    /// <c>bash -s x y</c>, a here-string): <c>$-</c> has <c>s</c>, not <c>c</c>, <c>$0</c> is <c>bash</c>, an error names
    /// <c>main</c>, and <c>-s</c> operands are <c>$1</c>.. REGRESSION: the text was rerun as <c>bash -c</c>, and
    /// <c>bash -s x y</c> took <c>x</c> for a script file.
    /// </summary>
    [SkippableFact]
    public Task StdinScript_IsNotDashC() => Same(
        """
        cd "$(mktemp -d)" || exit
        printf 'echo "a $- ${BASH_SOURCE[0]-unset} $0 $# ${1-}"; cd /nonexist_a\n' > s.txt
        { bash < s.txt; } 2>&1
        { cat s.txt | bash; } 2>&1
        { bash -s < s.txt; } 2>&1
        { bash -s x y < s.txt; } 2>&1
        { bash -s -- -x y < s.txt; } 2>&1
        { bash <<< 'echo "b $- $0 $#"'; } 2>&1
        { printf 'echo c' | bash; printf 'echo d\n' | bash -s; } 2>&1
        """);

    /// <summary>
    /// Two shell reports that named no script: a backtick body's syntax error (<c>t.sh: command substitution: line N:</c>,
    /// N the line the word ends on plus the error's line in the body; bash under <c>-c</c> says <c>bash</c>) and a
    /// background job a signal ended, reported by <c>wait</c> (<c>t.sh: line 4: PID Hangup</c>, the description padded,
    /// then the command). The parser's own wording after the prefix is a documented difference, masked here.
    /// REGRESSION: both said <c>bash:</c>, the backtick line was the body's (1), and the job report had no command.
    /// </summary>
    [SkippableFact]
    public Task ShellReports_NameTheScriptAndTheLine() => Same(
        """
        cd "$(mktemp -d)" || exit
        mask() { grep -v ": command substitution: line [0-9]*: \`" | sed 's/substitution: line \([0-9]*\): .*/substitution: line \1: MSG/; s/[0-9]\{3,\}/PID/'; }
        printf ': pad\nx=`echo (`\necho "after $?"\n' > t1.sh
        { bash t1.sh; echo "rc=$?"; } 2>&1 | mask
        printf ': pad\nf() {\n  y=`echo (`\n}\n: x\nf; echo "r=$?"\necho a; z=`\nfi`; echo after\nq=`echo a\nfi`; echo "q=$?"\n' > t2.sh
        { bash t2.sh; echo "rc=$?"; } 2>&1 | mask
        { bash -c 'x=`echo (`; echo r=$?'; } 2>&1 | mask
        printf ': pad\nsleep 5 &\nkill -HUP $!\nwait\necho "w=$?"\n' > t3.sh
        { bash t3.sh; echo "rc=$?"; } 2>&1 | mask
        """);

    // --- Function-local declarations, attributes and namerefs (PsBuild.DeclareLocalArray, VarFrameStack) ------

    /// <summary>
    /// Each refusal names its file and line (the line is recorded before the statement runs, <c>PsBuild.MayReportDiagnostic</c>)
    /// and keeps bash's status and line rule: a kind conversion, a circular or invalid nameref, a bad subscript, a readonly
    /// write, an ambiguous redirect, an unset <c>${v?}</c>, a NUL in a substitution, failglob, a bad indirection, a missing
    /// interpreter. REGRESSION: several said <c>line 1</c> or nothing, or ran the rest of the line.
    /// </summary>
    [SkippableFact]
    public Task Diagnostics_NameTheLineAndKeepTheLineRule() => Same(
        """
        cd "$(mktemp -d)" || exit
        n=0
        while IFS= read -r line; do
          n=$((n+1)); printf ': pad\n%s\necho end\n' "$line" > "t$n.sh"
          { bash "t$n.sh"; echo "rc=$?"; } 2>&1
        done <<'EOF'
        a=(1); declare -A a; echo "L1 rc=$?"
        declare -n r=r; echo "L2 rc=$?"
        b=(1); b[-5]=x; echo "L3 no"
        f() { local -A q; local -a q; echo "L4 rc=$?"; }; f
        readonly ro=1; ro=2; echo "L5 rc=$?"
        declare -n n1=ro; n1=3; echo "L6 rc=$?"
        declare -n n2='1bad'; echo "L7 rc=$?"
        x='a b'; echo hi > $x; echo "L8 rc=$?"
        ( echo ${zz?unset here}; echo no ); echo "L9 rc=$?"
        v=$(printf 'a\0b'); echo "L10 $v"
        shopt -s failglob; echo zz*q; echo "L11 no"; shopt -u failglob
        ref='a b'; echo ${!ref}; echo "L12 rc=$?"
        echo ${x!}; echo "L13 no"
        eval 'ro2=1'; readonly ro2; f2() { ro2=5; echo "L14 rc=$?"; }; f2
        declare -n c1=c2; declare -n c2=c1; c1=1; echo "L15 rc=$?"
        printf '#!/nonexistent/interp\n' > p; chmod +x p; ./p; echo "L16 rc=$?"
        ( set -o posix; export() { :; }; echo no ); echo "L17 rc=$?"
        declare -n n4=b; n4[-9]=z; echo "L18 no"
        EOF
        """);

    /// <summary>
    /// <c>local -A m</c> / <c>local -a q</c> (and <c>declare</c> with those flags in a function) make a local array of that
    /// kind, empty or with a value, gone at the return; converting one kind to the other is refused (status 1).
    /// REGRESSION: a scalar local, so <c>m[x]=1</c> evaluated x as arithmetic and wrote m[0].
    /// </summary>
    [SkippableFact]
    public Task FunctionLocalArrays_KeepTheirKind() => SameAsScript(
        """
        f() { local -A m; m[x]=1; m[y]=2; m[10]=3; echo "n=${#m[@]} x=${m[x]} y=${m[y]}"; declare -p m; local -a q; q[i+1]=5; declare -p q; }
        f; echo "after=${m-unset}"
        g() { local -A z=([a]=1); echo "${z[a]}"; local -a z; echo "rc=$?"; }; g
        f2() { local -A m=(); m[x]=1; m[y]=2; echo "n=${#m[@]} x=${m[x]}"; declare -p m; }; f2
        h() { local -A m; declare -p m; [[ -v m ]]; echo "v=$?"; echo "n=${#m[@]}"; local -a e; declare -p e; }; h
        k() { local -a m=(1 2); local -A m; echo "rc=$?"; declare -p m; }; k
        f3() { declare -a e; e[0]=1; declare -A g; g[k]=v; declare -p e g; }; f3; declare -p e g 2>&1
        o=(); h2() { local -a o; o[3]=x; }; h2; declare -p o
        p=(1); i() { local -a p; declare -p p; p+=(z); declare -p p; local -a p; declare -p p; }; i; declare -p p
        j() { local -A a; a[k]=1; local -A a; declare -p a; local -i n; declare -p n; }; j
        """);

    /// <summary>
    /// A local's attributes are its frame's: <c>-i</c>, <c>-l</c>, <c>-u</c> apply in the function and not to the caller's
    /// variable of that name, which gets its own back at the return; <c>-l</c> and <c>-u</c> together cancel; <c>-g</c> keeps
    /// the attribute global. REGRESSION: an attribute leaked across frames, and <c>declare -lu</c> converted the value.
    /// </summary>
    [SkippableFact]
    public Task FunctionLocalAttributes_BelongToTheFrame() => SameAsScript(
        """
        f() { local -i k=1+1; echo "k=$k"; k+=3; echo "k=$k"; local -l lo=ABC; echo "lo=$lo"; local -u up=abc; echo "up=$up"; up=def; echo "up=$up"; declare -p k lo up; local -i n; declare -p n; }; f
        declare -i n=1; g() { local n; n=2+2; echo "n=$n"; }; g; n=3+3; echo "outer n=$n"
        h() { declare -i d=2*3; echo "d=$d"; typeset -u t=xy; echo "t=$t"; }; h; echo "d=${d-unset}"
        k() { declare -gi gi=2+2; declare -gu gu=ab; declare -g -l gl=CD; }; k; echo "gi=$gi gu=$gu gl=$gl"; gi=3+3; gu=xy; echo "gi=$gi gu=$gu"; declare -p gi gu gl
        declare -ix top=1+1; echo "top=$top"; declare -lu lu=Mx; echo "lu=$lu"; declare -ul ul=Mx; echo "ul=$ul"
        declare -l -u a=Mx; declare -l c; declare -u c; c=Mx; echo "$a $c"; declare -p a c
        f2() { local -lu q=Mx; local -l r; local -u r; r=Mx; echo "$q $r"; declare -p q r; }; f2
        """);

    /// <summary>
    /// A callee's array write reaches the nearest caller's local of that name (bash's dynamic scope), else the global:
    /// element writes, <c>+=</c>, <c>mapfile</c>, <c>read -a</c>, <c>unset 'a[i]'</c>, <c>printf -v 'a[i]'</c>
    /// (<c>VarFrameStack.ArraySlot</c>). REGRESSION: every callee write went to the global. (A callee's write to a caller's
    /// <c>declare -i</c> local is not evaluated as arithmetic: environment-model.md, known gaps.)
    /// </summary>
    [SkippableFact]
    public Task FunctionLocalArrays_CalleeWritesReachTheCaller() => SameAsScript(
        """
        g() { echo "g sees ${m[k]} ${#a[@]} n=$n"; m[x]=2; a+=(z); }
        f() { declare -A m=([k]=v); declare -a a=(1 2); declare -i n=5; g; echo "f: ${m[x]} ${a[*]} n=$n"; declare -p m a n; }
        f; echo "after: ${m[k]-unset} ${a[*]-unset} ${n-unset}"
        h() { local -A cfg; cfg[a]=1; load; echo "h: ${!cfg[*]}"; }; load() { cfg[b]=2; }; h
        k() { declare -g -A G; G[q]=1; declare -gi gi=2+2; }; k; echo "G=${G[q]} gi=$gi"; gi=3+3; echo "gi=$gi"
        fill() { mapfile -t lines < <(printf 'a\nb\n'); read -ra parts <<< "x y z"; unset 'arr[1]'; printf -v 'arr[5]' '%s' P; }
        f2() { local -a lines parts; local arr=(0 1 2); fill; echo "${#lines[@]} ${parts[*]} ${!arr[*]} ${arr[5]}"; }; f2
        echo "outer: ${lines[*]-unset} ${parts[*]-unset} ${arr[*]-unset}"
        fill; echo "global: ${lines[*]} ${parts[*]} ${#arr[@]}"
        counter() { local -A seen; for w in a b a c b a; do seen[$w]=$(( ${seen[$w]:-0} + 1 )); done; for k in a b c; do printf '%s=%s ' "$k" "${seen[$k]}"; done; echo; }; counter
        """);

    /// <summary>
    /// <c>declare -r</c> / <c>-x</c> / <c>-n</c> / <c>-ri</c> in a function are local: read-only and exported only there
    /// (<c>declare -p</c> shows <c>-ir</c>), a nameref to a literal target writes it. REGRESSION: a local nameref never
    /// resolved, a local read-only scalar had no <c>r</c>, and <c>declare -x</c> leaked to the caller.
    /// </summary>
    [SkippableFact]
    public Task FunctionDeclare_ReadonlyExportAndNamerefAreLocal() => SameAsScript(
        """
        f() { declare -r ro=1; ro=2; echo "in ro=$ro"; }; f; echo "st=$?"; ro=3; echo "out ro=$ro"
        g() { declare -x ex=1; bash -c 'echo "child ex=$ex"'; }; g; echo "after ex=${ex-unset}"; bash -c 'echo "child2 ex=${ex-unset}"'
        t=5; h() { declare -n r=t; r=6; declare -p r; }; h; echo "t=$t"
        k() { declare -ri cnt=3; echo "$cnt"; declare -p cnt; }; k; declare -p cnt 2>&1
        m() { typeset -a ta=(1 2); typeset -A tb=([k]=v); echo "${ta[1]} ${tb[k]}"; }; m; echo "${ta[*]-unset} ${tb[k]-unset}"
        """);

    /// <summary>
    /// <c>declare NAME</c> with no value declares it unset: a later <c>NAME=v</c> is a scalar <c>declare -p</c> shows,
    /// <c>declare -a</c> makes <c>NAME=v</c> element 0, and <c>unset</c> takes the attributes with the variable.
    /// REGRESSION: a plain <c>declare c</c> made an empty array that hid the scalar a later <c>c=x</c> wrote.
    /// </summary>
    [SkippableFact]
    public Task Declare_NoValueIsDeclaredButUnset() => SameAsScript(
        """
        declare c; c=x; declare -p c; declare -a e; e=y; declare -p e; echo "${e[0]}"; declare g; g=(1); declare -p g
        declare h; echo "${h-unset} ${#h}"; declare -p h; [[ -v h ]]; echo "v=$?"; unset h; declare -p h; echo "rc=$?"
        declare -i n=1; unset n; declare -p n; echo "rc=$?"
        c() { echo fn; }; declare c2; unset c2; declare w; unset w; type -t c
        declare -l c3; c3=AB; declare -p c3
        """);

    /// <summary>
    /// A refused assignment (a readonly variable, <c>a[]=</c>) drops the rest of the top-level line: a loop on it, the
    /// functions it called and their caller's line too, status 1; a subshell or substitution stops it there, and
    /// <c>bash -c</c> goes on with its next line (<c>PsBuild.AssignmentError</c>). A readonly prefix binding
    /// (<c>r=3 cmd</c>) is reported and the command runs without it. REGRESSION: in a function the refusal only set the
    /// status, a substitution's refusal dropped the line it was on, and a prefix binding was silently ignored.
    /// </summary>
    [SkippableFact]
    public Task RefusedAssignment_DropsTheLineAndUnwindsFunctions() => SameAsScript(
        """
        readonly r=1; r=2; echo "same line $?"
        echo "next $?"
        f(){ local -r x=1; x=2; echo in; }; f; echo "st=$?"
        echo "n2 $?"
        g(){ readonly y=1; y=2; echo in-g; }
        g; echo "st=$?"
        r=3 true; echo "prefix $?"
        h(){ local -r z=1
        z=2
        echo in-h; }
        h; echo "h=$?"
        r+=x; echo "app $?"
        for i in 1 2; do r=2; echo "in $i"; done; echo "after $?"
        echo "loop-next $?"
        a[]=4; echo same; echo "nx $?"
        k(){ a[]=5; echo in-k; }
        k; echo "k=$?"
        w(){ while :; do r=9; echo loop; break; done; echo w-after; }
        w
        echo "w=$?"
        s=$(r=2; echo in-sub); echo "s=[$s] $?"
        r=3 echo hi; echo "st=$?"
        fp(){ echo "f r=$r"; }; r=4 fp; echo "st=$?"
        u=$(r=2); echo "u=[$u] $?"
        v=$(echo a; r=2; echo b); echo "v=[$v] $?"
        ( echo c; r=7 ); echo "sub $?"
        { r=8; echo no; }; echo "no2"
        bash -c 'readonly q=1; q=2; echo a
        echo b' 2>&1; echo "c=$?"
        echo end
        """);

    /// <summary>
    /// A function's nameref: a literal target (<c>local -n r=t</c>), redeclared to a new one, to an array or an element,
    /// <c>local +n</c>; a plain local shadows an outer nameref; <c>local -n out=$1</c> (the caller names the variable)
    /// reads and writes it, through arithmetic too (<c>(( c++ ))</c>, <c>c=$((c+1))</c>,
    /// <c>BashShellState.ResolveNameref</c>). REGRESSION: a literal target was never recorded, a plain local wrote through
    /// the outer nameref, and arithmetic read and wrote a variable named after the nameref.
    /// </summary>
    [SkippableFact]
    public Task FunctionNameref_TargetsShadowingAndArithmetic() => SameAsScript(
        """
        x=1; f(){ local -n lr=x; local +n lr; echo "[$lr]"; lr=5; echo "$x $lr"; }; f
        y=2; g(){ local -n q=y; q=3; local -n q=x; q=4; }; g; echo "$x $y"
        h(){ local -n m=arr; m=(a b); m[2]=c; echo "${#arr[@]} ${m[1]}"; }; h; echo "${arr[*]}"
        k(){ local v=7; local -n p=v; p=8; echo "$v"; }; k; echo "v=${v-unset}"
        declare -n gref=y; f2(){ local gref=9; echo "$gref $y"; }; f2; echo "$y"
        setv() { local -n out=$1; out="val-$2"; }
        setv A 1; setv B 2; echo "$A $B"
        push() { local -n arr_ref=$1; shift; arr_ref+=("$@"); }
        list=(); push list x y; push list z; echo "${#list[@]} ${list[*]}"
        outer() { local res; setv res 9; echo "outer res=$res"; }; outer; echo "res=${res-unset}"
        cnt() { local -n c=$1; (( c++ )); }; n=4; cnt n; cnt n; echo "n=$n"
        inc() { local -n c=$1; c=$((c+1)); }; m=4; inc m; echo "m=$m"
        lit() { local -n c=k; (( c += 2 )); echo $((c * 10)); }; k=1; lit; echo "k=$k"
        g2() { local -n gc=$1; echo "$(( gc + 1 ))"; }; z=7; g2 z
        """);

    /// <summary>
    /// A refused assignment under <c>set -e</c>, or in a posix-mode script, ends the shell with 1, even where errexit is
    /// otherwise ignored (<c>q=2 || echo</c>, an <c>if</c> body); a subshell or <c>$( )</c> ends with 1 and that ends its
    /// caller. An arithmetic expansion error only drops its line. REGRESSION: the refusal dropped the line and the script
    /// went on.
    /// </summary>
    [SkippableFact]
    public Task AssignmentError_UnderErrexitOrPosix_EndsTheShell() => SameAsScript(
        """
        t() { printf '%s\n' "$1" > s.sh; bash s.sh; echo "rc=$?"; }
        t 'set -e; readonly r=1; f() { r=2; echo in; }; f; echo same
        echo next'
        t 'set -e
        readonly q=1; q=2 || echo or
        echo next'
        t 'set -e
        if true; then readonly q=1; q=2; echo same; fi
        echo next'
        t 'set -e
        readonly q=1
        ( q=2; echo sub ); echo same
        echo next'
        t 'set -e
        readonly q=1
        y=$(q=2; echo sub); echo "same $?"
        echo next'
        t 'set -e
        a[]=1; echo same
        echo next'
        t 'set -o posix
        readonly x=1
        x=2
        echo hi'
        t 'set -e
        echo $((1/0)); echo same
        echo next'
        t 'readonly q=1
        q=2; echo same
        echo next'
        """);

    /// <summary>
    /// A function's <c>local x</c> with no value exists while unset: <c>declare -p x</c> shows <c>declare -- x</c>, in the
    /// function and in one it calls. <c>local +n r</c> leaves r the function's own. Under nounset an array with no element
    /// 0 is unbound when read as <c>$x</c>. REGRESSION: declare -p said "not found", a <c>local +n</c> name outlived its
    /// function, and <c>local -a x; echo $x</c> printed an empty line.
    /// </summary>
    [SkippableFact]
    public Task UnsetLocals_ExistAndUnsetArraysAreUnbound() => SameAsScript(
        """
        t() { printf '%s\n' "$1" > s.sh; bash s.sh; echo "rc=$?"; }
        x=1
        f() { local x; declare -p x; g; }; g() { declare -p x; }; f; declare -p x
        x=foo
        h() { local -n lr=x; local +n lr; echo "lr=$lr"; }; h; echo "x=$x lr=[${lr-unset}]"
        lr=outer; h; echo "lr=$lr"
        t 'set -u
        f() { local -a x; echo $x; }; f; echo "after $?"'
        t 'set -u
        f() { local -a x; echo "[$x]"; }; f; echo "after $?"'
        t 'set -u
        declare -a y; echo "[$y]"; echo no'
        t 'set -u
        a=(); a[1]=z; echo "[$a]"; echo no'
        t 'set -u
        b=(v); echo "[$b]"'
        """);
}
