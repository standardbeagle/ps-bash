using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>A failed arithmetic WORD abandons its command (status 1); a failed <c>(( ))</c> command only fails.</summary>
public class ArithExpansionFailureTests
{
    /// <summary>A variable whose value is not a valid expression is an error, not 0 (x='1 3'); a leading 0 is octal.</summary>
    [SkippableFact]
    public Task InvalidValueOfAVariable_IsAnError_AndLeadingZeroIsOctal() => AssertOracle.EqualAsync(
        "x='1 3'\n{ echo $((x)); } 2>/dev/null\necho st=$?\n{ (( x )); } 2>/dev/null; echo st=$?\n"
        + "s='12 34'\n{ echo $(( s[0] )); } 2>/dev/null\necho st=$?\na=('1 3' 5); b=('1 3' 5)\n{ (( a == b )); } 2>/dev/null; echo st=$?\n"
        + "o=010; echo $((o + 1)); e=08\n{ echo $((e)); } 2>/dev/null\necho st=$?; n=' -7 '; echo $((n * 2)); z=''; w=' '; echo $((z)) $((w + 1))");

    [SkippableFact]
    public Task FailedArithWord_AbandonsEcho() => AssertOracle.EqualAsync(
        "{ echo $((1 + 2.3)); } 2>/dev/null\necho status=$?\necho $((1+2)) ok");

    [SkippableFact]
    public Task FailedArithWord_InsideQuotes_AbandonsEcho() => AssertOracle.EqualAsync(
        "{ echo \"q$((1/0))\"; } 2>/dev/null\necho status=$?\necho next");

    [SkippableFact]
    public Task FailedArithAssignment_KeepsStatus_AndLaterEchoRuns() => AssertOracle.EqualAsync(
        "{ x=$((1/0)); } 2>/dev/null\necho status=$?\necho hi");

    [SkippableFact]
    public Task FailedArithCommand_DoesNotAbandonTheNextCommand() => AssertOracle.EqualAsync(
        "{ (( 1[2] = 3 )); } 2>/dev/null\necho status=$?\na=9\n{ (( (a + 2) = 3 )); } 2>/dev/null\necho $a\n"
        + "{ if (( 1 / 0 )); then echo yes; else echo no; fi; } 2>/dev/null\necho end");
    // The mark a failed word leaves must die with its own command: a later echo still runs.
    [SkippableFact]
    public Task FailedArithWord_OfAnotherCommand_DoesNotAbandonALaterEcho() => AssertOracle.EqualAsync(
        "{ true $((1/0)); } 2>/dev/null\necho after-true\n{ : $((1/0)); } 2>/dev/null\necho after-colon");

    [SkippableFact]
    public Task FailedArithWord_InARedirectTarget_AbandonsTheCommand() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\"\n{ echo hi > zz$(( 42 / 0 )) in; } 2>/dev/null\necho inside=$?\nls | wc -l");

    /// <summary>
    /// The command has a file redirect, which runs as a second pipeline element that begins first. Before, the redirect
    /// cleared the failure mark the command's word had set, so echo printed <c>q[]</c> with status 0.
    /// </summary>
    [SkippableFact]
    public Task FailedArithWord_OfACommandWithAFileRedirect_AbandonsIt() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\"\n{ echo \"q[$((1/0))]\" 2>>err.txt; } 2>/dev/null\necho st=$?\necho ok 2>>err.txt\necho st=$?");

    /// <summary>
    /// bash 5.2: an arithmetic error in ANY word -- a slice bound (string, array, positional), a subscript, a
    /// <c>$(( ))</c>, one nested in a slice bound -- ends its command and drops the rest of the input line (a function
    /// and the line that called it, a loop), in an assignment as in a command, status 1; the next line runs. Under
    /// <c>bash -c</c> the same, except that a SUBSCRIPT error ends the shell. Each row runs as line 2 of a script and of a
    /// <c>-c</c> string. REGRESSION: a slice error was reported and the command ran with the whole value
    /// (<c>x=${s:08}; echo "[$x]"</c> printed <c>[abcdef]</c>); <c>a[08]=x</c> reported a syntax error and went on.
    /// </summary>
    [SkippableFact]
    public Task FailedArithWord_InASliceSubscriptOrNestedWord_DropsTheLine() => AssertOracle.EqualAsync(
        "W=\"$(mktemp -d)\"; cd \"$W\"\npre='s=abcdef; a=(p q r); set -- u v w'\n"
        + "while IFS= read -r body; do\n"
        + "  printf '%s\\n%s\\necho \"next rc=$?\"\\n' \"$pre\" \"$body\" > c.sh\n"
        + "  echo \"== $body\"\n"
        + "  bash c.sh >o 2>&1; rc=$?; sed 's/^c\\.sh: //' o; echo \"[script $rc]\"\n"
        + "  bash -c \"$(cat c.sh)\" >o 2>&1; rc=$?; sed 's/^[^:]*bash: //' o; echo \"[-c $rc]\"\n"
        + "done <<'CASES'\n"
        + "echo \"${s:08}\"; echo same\n"
        + "x=${s:08}; echo \"same [$x]\"\n"
        + "echo \"${s:1:08}\"; echo same\n"
        + "echo \"${a[@]:08}\"; echo same\n"
        + "echo \"${@:08}\"; echo same\n"
        + "echo \"${a[08]}\"; echo same\n"
        + "x=${a[08]}; echo same\n"
        + "echo $((08)); echo same\n"
        + "x=$((08)); echo same\n"
        + "a[08]=x; echo same\n"
        + ": ${s:08}; echo same\n"
        + "x=${s:08} true; echo same\n"
        + "declare x=${s:08}; echo same\n"
        + "export y=${s:08}; echo same\n"
        + "f(){ echo ${s:08}; echo in-f; }; f; echo after\n"
        + "f(){ x=${s:08}; echo in-f; }; f; echo after\n"
        + "for i in 1; do echo ${s:08}; echo in-loop; done; echo after\n"
        + "if [[ ${s:08} == a ]]; then echo y; fi; echo same\n"
        + "case ${s:08} in *) echo c;; esac; echo same\n"
        + "y=$(echo ${s:08}; echo inner); echo \"[$y]\"\n"
        + "( echo ${s:08}; echo inner ); echo outer\n"
        + "{ echo ${s:08}; echo inner; }; echo outer\n"
        + "echo ${s:08} || echo alt; echo same\n"
        + "true && echo ${s:08}; echo same\n"
        + "echo \"${s:$((08))}\"; echo same\n"
        + "echo \"${#a[08]}\"; echo same\n"
        + "x=${a[@]:1:08}; echo same\n"
        + "x=${s:x/0}; echo same\n"
        + "CASES\ncd /; rm -rf \"$W\"",
        timeout: TimeSpan.FromSeconds(240));

    /// <summary>
    /// A <c>$(( ))</c> inside another expression is a word of its own: expanded to TEXT before the outer one is evaluated
    /// (<c>x$((1))</c> is the name x1, <c>2-$((-1))</c> is 3), and its failure is its own (<c>08: value too great for
    /// base</c>, not <c>$((08</c>), dropping the line even inside <c>(( ))</c>, <c>let</c> and <c>[[ ]]</c>. The
    /// words a <c>for</c> or <c>case</c> expands name its line. REGRESSION: the inner expansion was paren grouping of the
    /// outer expression; a <c>case</c> subject's error named the line of the statement before.
    /// </summary>
    [SkippableFact]
    public Task NestedArithWord_IsExpandedFirst_AndItsFailureDropsTheLine() => AssertOracle.EqualAsync(
        "W=\"$(mktemp -d)\"; cd \"$W\"\ncat > s.sh <<'EOF'\n"
        + "s=abcdef\n"
        + "echo \"1 $(( 2-$((-1)) )) $(( $((1+2))*3 )) $(( 2*$((1+2)) )) $(( -$((-5)) )) $(( 2-$((1-2)) ))\"\n"
        + "x=3; echo \"2 $(( x$((1)) )) $(( 2**$((1+1)) )) ${s:$((1+1))} ${s: -$((1+1))}\"\n"
        + "echo \"3 $(( 1 + $((08)) ))\"; echo same3\n"
        + "x=$(( $((08)) + 1 )); echo same4\n"
        + "echo \"5 $(( 1 + $((1/0)) ))\"; echo same5\n"
        + "echo \"6 ${s:$((08))}\"; echo same6\n"
        + "(( $((08)) )); echo same7\n"
        + "let \"y=$((08))\"; echo same8\n"
        + "(( 1 + $((1/0)) )) || echo alt9; echo same9\n"
        + "[[ $((08)) -eq 1 ]]; echo same10\n"
        + "[[ 1 -eq $((08)) ]] || echo alt11; echo same11\n"
        + "f() { echo \"12 ${s:$((08))}\"; echo in-f12; }; f; echo after12\n"
        + "echo \"13 ${s:$((1)):$((08))}\"; echo same13\n"
        + "for i in ${s:08}; do echo \"$i\"; done; echo same14\n"
        + "case x in ${s:08}) echo c ;; esac; echo same15\n"
        + "case ${s:08} in *) echo c ;; esac; echo same16\n"
        + "echo \"end $?\"\n"
        + "EOF\nbash s.sh 2>&1; echo \"rc=$?\"\ncd /; rm -rf \"$W\"");
}
