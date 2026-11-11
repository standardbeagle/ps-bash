using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A command name known only at run time (<c>$x</c>, <c>"$x"</c>). An unquoted one is word-split first: empty runs the
/// next operand as the command (none: nothing runs, status 0), several words make the rest leading operands. Before,
/// an empty <c>$x</c> leaked PowerShell's "expression after '&amp;'" error and <c>x="echo a"; $x b</c> looked up
/// <c>echo a</c>. Inside a compound command's stdin scope the queue was piped into every run-time name, so
/// <c>printf</c> / <c>test</c> failed to bind a queued line, or on an empty queue never ran at all.
/// </summary>
public class RunTimeCommandNameTests
{
    [Theory]
    [InlineData("x=\n$x; echo st1=$?\n$x echo hi; echo st2=$?\nfalse; $x; echo st3=$?")]
    [InlineData("x=\n$x <<EOT\nbody\nEOT\necho st=$?\n$x < /dev/null; echo st=$?\n$x > /dev/null; echo st=$?")]
    [InlineData("x=\n\"$x\" echo hi 2>/dev/null; echo st=$?")]
    [InlineData("y=\"echo a\"; $y b\ne=\"printf %s-\"; $e 1 2; echo\nt=\"test -n\"; $t \"\"; echo st=$?")]
    [InlineData("g=\"grep -c\"; printf 'q\\nr\\nq\\n' | $g q")]
    [InlineData("f() { echo \"f:$*\"; }; z=\"f one\"; $z two")]
    [InlineData("c=cat; echo A | $c; echo B | $c -n; c2=\"cat -n\"; echo C | $c2")]
    // Injection probes: the value's words are operands, never code.
    [InlineData("x='echo $(echo no);x'; $x\nx='echo `echo no`'; $x\nx='echo \"q\" {a,b}'; $x")]
    public Task UnquotedName_IsSplitAtRunTime(string script) => AssertOracle.EqualAsync(script);

    [Theory]
    [InlineData("printf 'a\\nb\\n' | { p=printf; $p 'X\\n'; t=test; $t -n \"\"; echo st=$?; c=cat; $c; }")]
    [InlineData("echo q | { p=printf; $p 'Y\\n'; \"$p\" 'Z\\n'; g=\"grep -c\"; $g q; }")]
    [InlineData("echo r | { e=; $e cat; }\necho s | { e=; $e printf 'P\\n'; cat; }")]
    [InlineData("f() { while read -r l; do echo \"f:$l\"; done; }; echo u | { h=f; $h; }")]
    [InlineData("r=read; echo w | { $r v; echo \"v=$v\"; }")]
    public Task NameInAStdinScope_GetsTheStdinOnlyIfItReadsIt(string script) => AssertOracle.EqualAsync(script);
}
