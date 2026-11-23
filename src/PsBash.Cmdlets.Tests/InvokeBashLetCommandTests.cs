using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 follow-on migration of
/// <c>Invoke-BashLet</c> from PsBash.psm1 to a binary cmdlet.
/// Oracle: the original psm1 function — BUT the oracle's
/// <c>Invoke-Expression</c>-based path is replaced with a purpose-built
/// integer-arithmetic parser (Directive 12 hardening), so the cmdlet's
/// security surface is strictly tighter than the oracle's. Exit code
/// 0 / 1 contract is preserved byte-for-byte.
/// </summary>
public class InvokeBashLetCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashLetCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    private long? RunAndReadIntVar(string script, string varName) => RunAndReadIntVar(_fixture, script, varName);

    internal static long? RunAndReadIntVar(SharedPwshFixture fixture, string script, string varName)
    {
        var pwsh = fixture.AcquireFresh();
        pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(
            $"$v = [PsBash.Cmdlets.BashVariableStore]::Get('{varName}'); [PsBash.Cmdlets.BashVariableStore]::Set('{varName}', $null); " +
            "if ($null -eq $v) { return } else { [int64]$v }").Invoke();
        var first = result.FirstOrDefault();
        if (first == null) return null;
        return Convert.ToInt64(first.BaseObject);
    }

    private int RunAndReadLastExitCode(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$global:LASTEXITCODE = 0").Invoke();
        pwsh.Commands.Clear();
        pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript("$global:LASTEXITCODE").Invoke();
        var first = result.FirstOrDefault();
        if (first == null) return 0;
        return Convert.ToInt32(first.BaseObject);
    }

    private string[] RunLines(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void Let_SimpleAssign_AssignsToCallerScope()
    {
        var v = RunAndReadIntVar("Invoke-BashLet 'x=5'", "x");
        Assert.Equal(5L, v);
    }

    [Fact]
    public void Let_Arithmetic_ComputesCorrectValue()
    {
        var v = RunAndReadIntVar("Invoke-BashLet 'y=2+3*4'", "y");
        Assert.Equal(14L, v);
    }

    [Fact]
    public void Let_Parens_RespectPrecedence()
    {
        var v = RunAndReadIntVar("Invoke-BashLet 'z=(2+3)*4'", "z");
        Assert.Equal(20L, v);
    }

    [Fact]
    public void Let_Subtraction_AndUnaryMinus()
    {
        var v = RunAndReadIntVar("Invoke-BashLet 'w=-(10-3)'", "w");
        Assert.Equal(-7L, v);
    }

    [Fact]
    public void Let_PowerOperator_RightAssociative()
    {
        // 2 ** 3 ** 2 == 2 ** 9 == 512 (right-assoc); != (2 ** 3) ** 2 == 64.
        var v = RunAndReadIntVar("Invoke-BashLet 'p=2**3**2'", "p");
        Assert.Equal(512L, v);
    }

    [Fact]
    public void Let_DivisionAndModulo_Work()
    {
        var v = RunAndReadIntVar("Invoke-BashLet 'q=17/5'", "q");
        var r = RunAndReadIntVar("Invoke-BashLet 'r=17%5'", "r");
        Assert.Equal(3L, v);
        Assert.Equal(2L, r);
    }

    [Fact]
    public void Let_ZeroResult_SetsLastExitCodeOne()
    {
        var code = RunAndReadLastExitCode("Invoke-BashLet 'foo=0'");
        Assert.Equal(1, code);
    }

    [Fact]
    public void Let_NonZeroResult_SetsLastExitCodeZero()
    {
        var code = RunAndReadLastExitCode("Invoke-BashLet 'bar=42'");
        Assert.Equal(0, code);
    }

    [Fact]
    public void Let_SyntaxError_SetsLastExitCodeOneAndDoesNotAssign()
    {
        var code = RunAndReadLastExitCode(
            "Invoke-BashLet 'junk=1+@@@' -ErrorAction SilentlyContinue");
        Assert.Equal(1, code);
        var v = RunAndReadIntVar(
            "Invoke-BashLet 'junk=1+@@@' -ErrorAction SilentlyContinue",
            "junk");
        Assert.Null(v);
    }

    // Oracle: o=4; v=o; let "$v |= 3"; echo $o $?  ->  7 0. The full arithmetic grammar (assignment operators included)
    // applies to a run-time operand; the old parser of its own had no |= ("expression error").
    [Fact]
    public void Let_CompoundBitwiseAssign_WritesTheShellVariable()
    {
        var v = RunAndReadIntVar("[PsBash.Cmdlets.BashVariableStore]::Set('letOr', '4'); Invoke-BashLet 'letOr |= 3'", "letOr");
        Assert.Equal(7L, v);
    }

    // Oracle: let a=0 b=1; echo $?  ->  0 and let a=1 b=0; echo $?  ->  1: only the LAST operand decides.
    [Theory]
    [InlineData("Invoke-BashLet 'letA=0' 'letB=1'", 0)]
    [InlineData("Invoke-BashLet 'letC=1' 'letD=0'", 1)]
    public void Let_Status_FollowsTheLastOperand(string script, int expected)
        => Assert.Equal(expected, RunAndReadLastExitCode(script));

    // Oracle: let; echo $?  ->  "let: expression expected", 1.
    [Fact]
    public void Let_NoOperand_IsAnError()
        => Assert.Equal(1, RunAndReadLastExitCode("Invoke-BashLet -ErrorAction SilentlyContinue"));

    // Oracle (bash 5.2, s.sh line 7): `s.sh: line 7: let: expression expected`, 1 -- a shell diagnostic, prefixed.
    [Fact]
    public void Let_NoOperand_ReportsTheShellDiagnostic()
        => CmdResult.Run(_fixture.AcquireFresh(),
                "$global:BASH_SOURCE = @('s.sh'); [PsBash.Cmdlets.VarFrameStack]::SetLine(7); Invoke-BashLet")
            .AssertFailed(1, "s.sh: line 7: let: expression expected");

    // Oracle: let '1+'  ->  `s.sh: line 7: let: 1+: syntax error: operand expected (error token is "+")`, 1. The
    // message had no prefix, no `let:` label and the parser's own words.
    [Theory]
    [InlineData("1+", "let: 1+: syntax error: operand expected (error token is \"+\")")]
    [InlineData("a b c", "let: a b c: syntax error in expression (error token is \"b c\")")]
    [InlineData("(1+2", "let: (1+2: missing `)' (error token is \"2\")")]
    public void Let_SyntaxError_ReportsBashsMessageWithThePrefix(string operand, string message)
        => CmdResult.Run(_fixture.AcquireFresh(),
                "$global:BASH_SOURCE = @('s.sh'); [PsBash.Cmdlets.VarFrameStack]::SetLine(7); Invoke-BashLet '" + operand + "'")
            .AssertFailed(1, "s.sh: line 7: " + message);

    [Fact]
    public void Let_AliasLet_ResolvesToCmdlet()
    {
        var v = RunAndReadIntVar("let 'aliased=7+8'", "aliased");
        Assert.Equal(15L, v);
    }

    [Fact]
    public void Let_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashLet --help");
        Assert.NotEmpty(lines);
    }

    [Fact]
    public void Let_NoStdout()
    {
        var lines = RunLines("Invoke-BashLet 'silent=1+1'");
        Assert.Empty(lines);
    }

    // Directive 12 — the critical security probe. The oracle's Invoke-Expression
    // path would have evaluated $(throw 'PWNED') as PowerShell. The cmdlet's
    // recursive-descent parser sees only literal characters and treats them as
    // a syntax error (no exception bubbles up, no $(throw) fires).
    [Fact]
    public void Let_InjectionInExpression_DoesNotExecutePayload()
    {
        // If the cmdlet were re-parsing user input as PowerShell, $(throw 'PWNED')
        // would surface as an uncaught throw and `after` would never run. Wrap
        // in an error-action so the bash-style error doesn't escape the test.
        var lines = RunLines(
            "Invoke-BashLet 'injected=$(throw \"PWNED\")' -ErrorAction SilentlyContinue; 'after'");
        Assert.Contains("after", lines);
    }

    [Fact]
    public void Let_InjectionInExpressionWithSemicolon_DoesNotExecutePayload()
    {
        var lines = RunLines(
            "Invoke-BashLet 'p=1;Write-Host PWNED' -ErrorAction SilentlyContinue; 'after'");
        // The semicolon falls through to the parser's "trailing tokens" error.
        Assert.DoesNotContain(lines, l => l.Contains("PWNED"));
        Assert.Contains("after", lines);
    }
}

/// <summary>let cases that set the process environment (<c>$env:a</c>, <c>$env:b</c>), so they run alone and restore it.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public class InvokeBashLetEnvironmentTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashLetEnvironmentTests(SharedPwshFixture fixture) => _fixture = fixture;

    [Fact]
    public void Let_VariableLookup_ReadsTheShellVariables()
    {
        using var env = PsBash.Testing.ProcessEnv.Preserve("a", "b", "c");
        var v = InvokeBashLetCommandTests.RunAndReadIntVar(_fixture, "$env:a = '10'; $env:b = '3'; Invoke-BashLet 'c=a*b+1'", "c");
        Assert.Equal(31L, v);
    }
}
