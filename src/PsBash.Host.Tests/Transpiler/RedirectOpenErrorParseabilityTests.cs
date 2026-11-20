using Xunit;

namespace PsBash.Host.Tests.Transpiler;

/// <summary>
/// The shapes the redirect-open-error diagnostics emit must parse (ParseabilityContract branch A; the
/// oracle is PowerShell's parser). <c>x=$(&lt; f 2&gt;/dev/null)</c> emitted a redirect tail as a command after a pipe
/// ("Expressions are only allowed as the first element of a pipeline"): no line of the
/// script ran.
/// </summary>
public class RedirectOpenErrorParseabilityTests
{
    [Theory]
    [InlineData("x=$(< nofile 2>/dev/null); echo \"rc=$?\"")]
    [InlineData("x=$(< nofile); echo \"rc=$?\"")]
    [InlineData("< nofile; echo after")]
    [InlineData("cat 2>/dev/null < nofile; cat < nofile 2>/dev/null")]
    [InlineData("cat >out 2>err < nofile")]
    [InlineData("while read l; do echo $l; done 2>/dev/null < nofile")]
    [InlineData("{ cat; } 2>e < nofile")]
    [InlineData("( cat ) 2>/dev/null < nofile")]
    [InlineData("echo hi 2>/dev/null > nodir/y; echo hi 2>e > nodir/y; ls 2>&1 > nodir/q | sed 's/^/O:/'")]
    [InlineData("set -e; echo hi 2>/dev/null > nodir/y; echo after")]
    [InlineData("f() { echo in; }\nf 2>e > nodir/w\n{ echo a\n  echo b\n} > nodir/z")]
    public void Transpile_DiagnosticShapes_EmitParseablePowerShell(string bash)
        => Assert.Equal(ParseabilityContract.Outcome.ValidPowerShell, ParseabilityContract.Assert(bash, "redirect open errors"));
}
