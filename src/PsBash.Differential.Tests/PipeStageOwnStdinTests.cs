using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A pipeline stage with its own stdin redirect (<c>&lt; file</c>, <c>&lt;&lt;&lt; text</c>, <c>&lt;&lt;EOF</c>) reads THAT, not the pipe.
/// The redirect used to be dropped (here-string / here-document) or handed over as an operand (<c>0&lt;file</c>).
/// </summary>
public class PipeStageOwnStdinTests
{
    [Theory]
    [InlineData("echo hi | cat <<< here")]
    [InlineData("echo hi | FOO=1 cat <<< envhere")]
    [InlineData("seq 3 | cat <<< a | tr a b")]
    [InlineData("echo hi | cat <<< x > /dev/null; echo st=$?")]
    [InlineData("printf 'one\\ntwo\\n' > f.txt; echo hi | cat < f.txt | head -1")]
    [InlineData("printf 'one\\ntwo\\n' > f.txt; echo hi | grep -c . < f.txt")]
    [InlineData("true | sed -e 's/o/0/' <<E\n/foo\nE")]
    [InlineData("x=$(true | sed -e 's/o/0/' <<E\n/foo\nE\n)\necho \"[$x]\"")]
    [InlineData("set -o pipefail; false | cat <<< pf; echo st=$?")]
    [InlineData("echo hi | while read l; do echo \"L=$l\"; done <<< loop")]
    public Task StageRedirect_ReplacesThePipe(string script) => AssertOracle.EqualAsync(script);

    // A here-document on a command-less assignment has no reader: the assignment still happens.
    [Theory]
    [InlineData("foo=`echo v`<<EOM\nhello world\nEOM\necho \"[$foo]\"")]
    [InlineData("foo=bar <<EOM\nbody\nEOM\necho \"[$foo]\"")]
    public Task HereDocOnBareAssignment_IsDropped(string script) => AssertOracle.EqualAsync(script);

    // A PowerShell keyword, or a bash reserved word after an environment prefix, is an ordinary command name.
    [Theory]
    [InlineData("{ var x = 1; } 2>/dev/null; echo st=$?")]
    [InlineData("{ try x; } 2>/dev/null; echo st=$?")]
    [InlineData("{ class x; } 2>/dev/null; echo st=$?")]
    [InlineData("{ switch x; } 2>/dev/null; echo st=$?")]
    [InlineData("{ using x; } 2>/dev/null; echo st=$?")]
    [InlineData("filter() { echo \"fn $1\"; }; filter q")]
    [InlineData("data() { echo \"fn $1\"; }; data q")]
    [InlineData("{ FOO=bar [[ foo == foo ]]; } 2>/dev/null; echo st=$?")]
    [InlineData("{ FOO=bar for; } 2>/dev/null; echo st=$?")]
    public Task KeywordCommandName_IsLookedUp(string script) => AssertOracle.EqualAsync(script);

    // An arithmetic subscript may hold shell metacharacters.
    [Theory]
    [InlineData("a[3|4]=8; a[(1+2)*3]=9; a[2#11 + 2]=5; a[1 + 2]=7; typeset -p a")]
    [InlineData("a[1]=x; eval 'a[5&3]=hello'; echo \"status=$?, a[1]=${a[1]}\"")]
    [InlineData("a[6>>1]=s; a[1<2]=t; typeset -p a")]
    [InlineData("echo x | cat; echo a[1] b")]
    public Task SubscriptWithMetacharacters_IsOneAssignment(string script) => AssertOracle.EqualAsync(script);

    // A run-time command name (`$GREP`, `$SED`, `$SH`) takes its flags as arguments whatever it resolves to: a bare `-e`
    // reached a cmdlet as an ambiguous PowerShell parameter prefix.
    [Theory]
    [InlineData("SH=bash; $SH -e -c 'echo ok0'")]
    [InlineData("GREP=grep; printf 'a\\nb\\n' > t.txt; $GREP -e a t.txt; $GREP -c -v a t.txt; \"$GREP\" -n -i B t.txt")]
    [InlineData("SED=sed; printf 'a\\nb\\n' > t.txt; $SED -n -e 2p t.txt; echo x | $SED -e s/x/y/")]
    [InlineData("f() { echo \"args: $*\"; }; F=f; $F -e -n -- -x")]
    [InlineData("E=echo; $E -n hi; $E; $E -e 'a\\tb'")]
    [InlineData("printf 'a\\nb\\n' > t.txt; x=t.txt; W=wc; S=sed; $W -l $x | $S -e 's/ .*//'")]
    public Task RunTimeCommandName_FlagsAreArguments(string script) => AssertOracle.EqualAsync(script);
}
