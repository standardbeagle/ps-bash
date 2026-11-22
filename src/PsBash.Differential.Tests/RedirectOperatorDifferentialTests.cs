using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Redirections against bash 5.2: every operator of the one table with an <c>N</c> / <c>{name}</c> prefix on a simple
/// command, a group, a loop and <c>exec</c> (RedirectOperators; parse + PowerShell validity is
/// <c>Host.Tests/Transpiler/RedirectOperatorTableGuardTests</c>), unquoted here-document line continuations, <c>&lt;&gt;</c> and
/// <c>N&gt;&amp;M-</c>, and a redirection that cannot be opened under <c>set -e</c> (a status the one emitted errexit check acts on).
/// </summary>
public class RedirectOperatorDifferentialTests
{
    private static Task Eq(string script) => AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    // Class: the lexer and the parser each kept a list of redirection operators; `<<<` took no `N` prefix
    // (`read -u 3 b 3<<< x` passed `3` as a name), a compound's `3<<< x` / `3<<EOF` and `exec {v}<<< x` did not parse.
    // Primitive: RedirectOperators, one ParseRedirect for every command kind, one FdOpenStep for every descriptor.
    [SkippableFact]
    public Task HereText_OnANumberedOrNamedDescriptor_EveryCommandKind()
        => Eq("read -u 3 b 3<<< x; echo \"b=$b\"; cat 0<<< zero\n"
            + "{ read -u 3 l; echo \"g=$l\"; } 3<<< gx\n"
            + "while read -u 3 l; do echo \"w=$l\"; done 3<<< wx\n"
            + "if true; then read -u 3 l; echo \"if=$l\"; fi 3<<< ifx\n"
            + "exec {hv}<<< hx; read -u $hv l; echo \"hv=$l\"; exec {hv}<&-\n"
            + "exec 4<<< four; read -u 4 l; echo \"4=$l\"; exec 4<&-\n"
            + "read -u 6 a 6<<< \"q $((1+2))\"; echo \"a=$a\"\n"
            + "exec <<< sh-in; read l; echo \"stdin=$l\"\n");

    [SkippableFact]
    public Task HereDocument_OnANumberedOrNamedDescriptor_EveryCommandKind()
        => Eq("while read -u 3 l; do echo \"d=$l\"; done 3<<EOF\nd1\nd2\nEOF\n"
            + "exec {hd}<<EOF\nhd1\nEOF\nread -u $hd l; echo \"hd=$l\"; exec {hd}<&-\n"
            + "read -u 5 l 5<<EOF\nfive\nEOF\necho \"5=$l\"\n"
            + "{ cat; } <<< grp\n");

    // Class: an unquoted here-document kept backslash-newline; bash removes it as it reads each line, before the `<<-`
    // tab strip, the delimiter match and expansion. Primitive: BashLexer.JoinHereDocContinuations (lexer + parser).
    [SkippableFact]
    public Task HereDocument_BackslashNewline_IsALineContinuation()
        => Eq("x=V; cat <<EOF\na $x\\\nb\nc\\$x\\\nd\ne\\\\\nf\nEOF\n"
            + "cat <<-EOF\n\tt1\\\n\tt2\n\tEOF\n"
            + "cat <<EOF\na\\\nEOF\nEOF\n"
            + "cat <<'EOF'\nq\\\nr\nEOF\n"
            + "cat <<EOF\n\\a \\n \\\" \\' \\$x \\`x\\` \\\\ \\{\nEOF\n"
            + "cat <<EOF\nend\\\\\nEOF\n");

    // `<>` opens read-write: created if missing, never truncated, written from the start; a later `>&0` writes into the
    // same file. `N>&M-` moves M to N.
    [SkippableFact]
    public Task ReadWriteOpen_AndDescriptorMove()
        => Eq("cd \"$(mktemp -d)\" || exit\n"
            + "printf 'abcdef' > rw; echo XY 1<> rw; od -c rw\n"
            + "cat 0<> newrw; echo \"s=$?\"; ls newrw\n"
            + "echo XY 0<> rw4 >&0; od -c rw4\n"
            + "printf 'abc\\n' > rd; read -u 7 l 7<> rd; echo \"7=$l\"; od -c rd\n"
            + "exec 3>fm; exec 4>&3-; echo m >&4; exec 4>&-; cat fm\n");

    // Class: Invoke-BashRedirect threw a terminating error under set -e: it ignored `||` / `if` (__BashErrexitSuppress),
    // ended the whole script from inside a subshell, and its text escaped the subshell's 2>/dev/null. Primitive: the cmdlet
    // only sets the status (FailOpen); the one emitted errexit check (IsErrexitCheckedSimple, RedirectFailureChecked) ends
    // the shell.
    [SkippableFact]
    public Task RedirectThatCannotOpen_UnderErrexit_IsAStatus()
        => Eq("( set -e; { echo x; } > /nonexist/f; echo no ) 2>/dev/null; echo \"s=$?\"\n"
            + "( set -e; echo hi > /nonexist/f; echo after ) 2>/dev/null; echo \"s=$?\"\n"
            + "( set -e; : > /nonexist/f; echo after-special-e ) 2>/dev/null; echo \"s=$?\"\n"
            + "( set -e; > /nodir/z; echo after-bare ) 2>/dev/null; echo \"s=$?\"\n"
            + "( echo hi > /nodir/w; echo goes-on ) 2>/dev/null; echo \"s=$?\"\n");

    [SkippableFact]
    public Task RedirectThatCannotOpen_InAConditionOrOrList_GoesOn()
        => Eq("set -e\n"
            + "echo hi > /nonexist_zz/x 2>/dev/null || echo \"x01 caught\"\n"
            + "if echo hi > /nodir/x 2>/dev/null; then echo t; else echo e; fi\n"
            + "f() { echo hi > /nodir/y; }; f 2>/dev/null || echo \"f caught\"\n"
            + "echo \"x04 end\"\n");
}
