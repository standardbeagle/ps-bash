using Xunit;

namespace PsBash.Host.Tests.Transpiler;

/// <summary>
/// The shapes the <c>let</c> and <c>.</c>/<c>source</c> diagnostics emit must parse (ParseabilityContract branch A).
/// <c>let '1+'</c> crashed the transpile (BashArithmeticParseException): no line of the script ran.
/// </summary>
public class LetSourceDiagnosticParseabilityTests
{
    [Theory]
    [InlineData("let '1+'; let 'x=='; let; echo after")]
    [InlineData("let x=1/0 y=2")]
    [InlineData(".; source; echo after")]
    public void Transpile_DiagnosticShapes_EmitParseablePowerShell(string bash)
        => Assert.Equal(ParseabilityContract.Outcome.ValidPowerShell, ParseabilityContract.Assert(bash, "let/source diagnostics"));
}
