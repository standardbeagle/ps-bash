using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A <c>return</c> (or a subshell's <c>exit</c>) inside a command whose redirect puts it in a PowerShell script block
/// leaves the function, eval text or subshell, as in bash 5.2. REGRESSION: <c>f() { { return 6; } 2&gt;/dev/null; echo no;
/// }</c> printed no and returned 0. Primitive: <c>PsEmitter.RedirectGroup</c> (guard <c>Core.Tests/RedirectGroupGuardTests</c>).
/// break/continue, a function call with a redirect, and <c>source f 2&gt;x</c> (whose return ends only the file) are the
/// controls.
/// </summary>
public class RedirectGroupReturnDifferentialTests
{
    private static Task Same(string script) => AssertOracle.EqualAsync(script, TimeSpan.FromSeconds(30));

    private const string Probe = "t(){ \"$@\"; echo \"$1=$?\"; }\n";

    [SkippableFact]
    public Task Return_InARedirectedCompound_LeavesTheFunction() => Same(
        "printf 'return 9\\necho fileafter\\n' > rc\n" + Probe
        + "b1(){ { return 6; } 2>/dev/null; echo no; }; t b1\n"
        + "b2(){ while true; do return 6; done 2>x; echo no; }; t b2\n"
        + "b3(){ if true; then return 6; fi >x; echo no; }; t b3\n"
        + "b4(){ case a in a) return 6;; esac 2>x; echo no; }; t b4\n"
        + "b5(){ for i in 1; do return 6; done >x; echo no; }; t b5\n"
        + "b6(){ until false; do return 6; done 2>x; echo no; }; t b6\n"
        + "b7(){ for ((i=0;i<1;i++)); do return 6; done 2>x; echo no; }; t b7\n"
        + "b8(){ { return 6; } <rc; echo no; }; t b8\n"
        + "b9(){ { return 6; } <<< x; echo no; }; t b9\n"
        + "b10(){ { return 6; } 2>&1; echo no; }; t b10\n"
        + "b11(){ { { return 6; } >x; echo no-inner; } 2>/dev/null; echo no; }; t b11\n"
        + "b12(){ if true; then { return 6; } 2>x; fi; echo no; }; t b12\n"
        + "b13(){ { return 6; } >x 2>&1; echo no; }; t b13\n"
        + "b14(){ [[ 1 ]] && { return 6; } 2>x; echo no; }; t b14\n"
        + "b15(){ while read -r l; do return 6; done < rc; echo no; }; t b15\n"
        + "b16(){ { return 6; } > x 2>&1 < x; echo no; }; t b16\n"
        + "b17(){ case a in a) { return 6; } 2>x;; esac; echo no; }; t b17\n");

    [SkippableFact]
    public Task Return_InARedirectedCommand_OtherShapes() => Same(
        Probe
        + "h1(){ { return 6; } 3>x; echo no; }; t h1\n"
        + "h2(){ { return 6; } {fd}>x; echo no; }; t h2\n"
        + "h3(){ eval 'return 6' <<< x; echo no; }; t h3\n"
        + "h4(){ builtin eval 'return 6' 2>/dev/null; echo no; }; t h4\n"
        + "h5(){ eval 'return 6' 9>x; echo no; }; t h5\n"
        + "h7(){ while true; do { return 6; } 2>x; done; echo no; }; t h7\n"
        + "h8(){ g(){ { return 2; } 2>x; echo gno; }; { g; echo \"g=$?\"; } 2>x; echo after; }; t h8\n"
        + "h9(){ { if true; then return 6; fi; } 2>x | cat; echo \"pipe=$?\"; }; t h9\n"
        + "h10(){ if { return 6; } 2>x; then echo then; fi; echo no; }; t h10\n"
        + "h11(){ { return 6; } 2>x && echo and; echo no; }; t h11\n"
        + "h12(){ ! { return 6; } 2>x; echo no; }; t h12\n"
        + "h13(){ x=$( { return 6; } 2>/dev/null; echo sub ); echo \"x=$x\"; }; t h13\n"
        + "h14(){ ( { return 7; } 2>/dev/null; echo sub ); echo \"st=$?\"; }; t h14\n"
        + "c='{ return 5; } 2>/dev/null'; h15(){ eval \"$c\"; echo no; }; t h15\n"
        + "h18(){ { true; } 2>x; echo yes; }; t h18\n");

    [SkippableFact]
    public Task Exit_InARedirectedCompound_LeavesTheSubshell() => Same(
        "( { exit 3; } 2>/dev/null; echo no ); echo \"sx=$?\"\n"
        + "( if true; then exit 4; fi >x; echo no ); echo \"sx2=$?\"\n"
        + "( set -e; { false; } 2>x; echo no ); echo \"se=$?\"\n"
        + "( while true; do exit 5; done 2>x; echo no ); echo \"sx3=$?\"\n");

    // A block that is no redirect's: errexit's final and-or operand (an ERR trap turns it on), `< /dev/null`, `< file`.
    // REGRESSION: r6/r7 printed no; `return 6 <f` and `eval 'return 7' <f` failed the parse of the whole script.
    [SkippableFact]
    public Task Return_InAnErrexitOperandOrAnInputRedirect_LeavesTheFunction() => Same(
        "trap ':' ERR\nprintf 'l1\\n' > rc\n"
        + "r1(){ { return 6; } >/nonexistent/x; echo \"no $?\"; }; r1 2>/dev/null; echo \"r1=$?\"\n"
        + "r2(){ return 6 </dev/null; echo no; }; r2; echo \"r2=$?\"\n"
        + "r3(){ eval 'return 7' </dev/null; echo no; }; r3; echo \"r3=$?\"\n"
        + "r4(){ eval 'return 7' <rc; echo no; }; r4; echo \"r4=$?\"\n"
        + "r5(){ return 6 <missing; echo \"no $?\"; }; r5 2>/dev/null; echo \"r5=$?\"\n"
        + "r6(){ [[ 1 ]] && { return 6; }; echo no; }; r6; echo \"r6=$?\"\n"
        + "r7(){ [[ 1 ]] && { return 6; } 2>x; echo no; }; r7; echo \"r7=$?\"\n"
        + "( true && { exit 3; }; echo no ); echo \"r8=$?\"\n"
        + "r9(){ while :; do return 6; done </dev/null; echo no; }; r9; echo \"r9=$?\"\n"
        + "r10(){ false || return 5; echo no; }; r10; echo \"r10=$?\"\n"
        + "r11(){ { return 6; } 2>/nonexistent/x || echo \"or $?\"; echo after; }; r11 2>/dev/null; echo \"r11=$?\"\n"
        + "r12(){ eval 'return 7' <missing; echo \"no $?\"; }; r12 2>/dev/null; echo \"r12=$?\"\n"
        + "c='return 8'; r13(){ eval \"$c\" <rc; echo no; }; r13; echo \"r13=$?\"\n"
        // The flag across blocks: an inner block that ends, a break past the check, a function's block, a failed redirect.
        + "n1(){ { { :; } 2>x; return 6; } >y; echo no; }; n1; echo \"n1=$?\"\n"
        + "n2(){ for i in 1 2; do { [[ $i = 3 ]] && return 6; break; } 2>x; done; { return 7; } 2>x; echo no; }; n2; echo \"n2=$?\"\n"
        + "n3(){ g(){ { return 2; } 2>x; }; { g; echo \"g=$?\"; return 8; } 2>x; echo no; }; n3; echo \"n3=$?\"\n"
        + "n4(){ { :; } >/nonexistent/x; { return 6; } 2>x; echo no; }; n4 2>/dev/null; echo \"n4=$?\"\n");

    // A return in a condition, a `!` pipeline or an and-or left operand leaves the function under set -e (each case in a
    // subshell that turns it on: the status 6 then ends the subshell) and under an ERR trap, also from run-time text.
    // Guard: RedirectGroupGuardTests.Transpile_ReturnInAnErrexitCondition_StaysAStatement.
    [SkippableFact]
    public Task Return_InAConditionUnderErrexitOrAnErrTrap_LeavesTheFunction() => Same(
        "c1(){ if return 6; then echo t; fi; echo no; }\n"
        + "c2(){ while return 6; do :; done; echo no; }\n"
        + "c3(){ until return 6; do :; done; echo no; }\n"
        + "c4(){ ! return 6; echo no; }\n"
        + "c5(){ return 6 && echo and; echo no; }\n"
        + "c6(){ return 6 || echo or; echo no; }\n"
        + "c7(){ if { return 6; }; then :; fi; echo no; }\n"
        + "c8(){ if ! return 6; then :; fi; echo no; }\n"
        + "c9(){ while { return 6; } 2>x; do :; done; echo no; }\n"
        + "c10(){ if [[ 1 ]] && return 6; then :; fi; echo no; }\n"
        + "c11(){ if false; then :; elif return 6; then :; fi; echo no; }\n"
        + "c12(){ if eval 'return 6'; then :; fi; echo no; }\n"
        + "c13(){ ! { return 6; } 2>x; echo no; }\n"
        + "c14(){ [[ 1 ]] && ! return 6; echo no; }\n"
        + "c15(){ while :; do if return 6; then :; fi; done; echo no; }\n"
        + "cc='if return 6; then :; fi; until ! return 7; do :; done'; c16(){ eval \"$cc\"; echo no; }\n"
        + "se(){ ( set -e; \"$@\"; echo after ); echo \"se-$1=$?\"; }\n"
        + "for k in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16; do se c$k; done\n"
        + "trap ':' ERR\n"
        + "for k in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16; do c$k; echo \"err-c$k=$?\"; done\n");

    // Controls: what bash does NOT pass on.
    [SkippableFact]
    public Task Controls_BreakContinueCallsAndSource() => Same(
        "printf 'return 9\\necho fileafter\\n' > rc\nprintf 'break\\necho fileafter\\n' > rcb\n" + Probe
        + "g(){ return 3; }; c1(){ g 2>/dev/null; echo \"c1in=$?\"; }; t c1\n"
        + "s1(){ source ./rc 2>/dev/null; echo \"s1in=$?\"; }; t s1\n"
        + "s2(){ . ./rc >x; echo \"s2in=$?\"; }; t s2\n"
        + "for i in 1 2 3; do { break; } 2>/dev/null; echo no; done; echo \"L1=$i\"\n"
        + "for i in 1 2; do { continue; } 2>x; echo no; done; echo \"L2=$i\"\n"
        + "for i in 1 2 3; do if true; then break; fi >x; echo no; done; echo \"L3=$i\"\n"
        + "for i in 1 2 3; do while true; do break 2; done 2>x; echo no; done; echo \"L5=$i\"\n"
        + "for i in 1 2 3; do source ./rcb 2>/dev/null; echo \"after$i\"; done; echo \"L6=$i\"\n"
        + "{ return 6; } 2>/dev/null; echo \"top=$?\"\n");
}
