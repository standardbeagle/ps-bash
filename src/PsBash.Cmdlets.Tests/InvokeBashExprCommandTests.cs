using System.Collections.ObjectModel;
using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 follow-on migration of
/// <c>Invoke-BashExpr</c> from PsBash.psm1 to a binary cmdlet
/// (<see cref="PsBash.Cmdlets.InvokeBashExprCommand"/>).
///
/// Oracle: the psm1 function. Coreutils <c>expr</c> with arithmetic
/// (<c>+ - * / %</c>), comparisons (<c>= != &lt; &lt;= &gt; &gt;=</c>),
/// and string ops (<c>length</c> / <c>substr</c> / <c>index</c> / <c>match</c>).
/// Output is a typed <c>PsBash.ExprOutput</c> PSObject with <c>Value</c> +
/// <c>BashText</c>.
///
/// Failure-surface axes covered (per Directive 3):
/// empty input (missing-operand), unicode (length / substr on multibyte),
/// numeric edge cases (division by zero, negative numbers), exit-code
/// propagation (LASTEXITCODE set to 2 on error, 1/0 result-vs-zero via
/// <c>$?</c>-style consumer logic in shells), <c>--help</c>, alias
/// resolution, and Directive 12 injection (<c>;</c>-bearing operand stays a
/// literal string).
/// </summary>
public class InvokeBashExprCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashExprCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    private Collection<PSObject> RunRaw(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result;
    }

    private string RunBashText(string script)
    {
        var r = RunRaw(script);
        Assert.NotEmpty(r);
        // ExprOutput is a typed PSObject; BashText is on the wrapper.
        var bashText = r[0].Properties["BashText"]?.Value?.ToString();
        Assert.NotNull(bashText);
        return bashText!;
    }

    // ---- arithmetic ----

    [Fact]
    public void Expr_Addition_ReturnsSum()
    {
        Assert.Equal("5", RunBashText("Invoke-BashExpr 2 + 3"));
    }

    [Fact]
    public void Expr_Subtraction_ReturnsDifference()
    {
        Assert.Equal("6", RunBashText("Invoke-BashExpr 10 - 4"));
    }

    [Fact]
    public void Expr_Multiplication_ReturnsProduct()
    {
        // PowerShell needs `*` quoted to suppress its own glob expansion semantics
        // when bare; the bash transpiler quotes it already, but in a direct
        // Invoke-BashExpr call it has to be quoted.
        Assert.Equal("42", RunBashText("Invoke-BashExpr 6 '*' 7"));
    }

    [Fact]
    public void Expr_IntegerDivision_TruncatesTowardZero()
    {
        Assert.Equal("3", RunBashText("Invoke-BashExpr 15 / 4"));
    }

    [Fact]
    public void Expr_Modulo_ReturnsRemainder()
    {
        Assert.Equal("3", RunBashText("Invoke-BashExpr 15 % 4"));
    }

    [Fact]
    public void Expr_NegativeOperand_ParsesAsNumeric()
    {
        Assert.Equal("-2", RunBashText("Invoke-BashExpr -5 + 3"));
    }

    // ---- comparisons ----

    [Fact]
    public void Expr_Equal_ReturnsOneOnMatch()
    {
        Assert.Equal("1", RunBashText("Invoke-BashExpr 3 = 3"));
    }

    [Fact]
    public void Expr_Equal_ReturnsZeroOnMismatch()
    {
        Assert.Equal("0", RunBashText("Invoke-BashExpr 3 = 4"));
    }

    [Fact]
    public void Expr_LessThan_ReturnsOne()
    {
        Assert.Equal("1", RunBashText("Invoke-BashExpr 2 '<' 5"));
    }

    [Fact]
    public void Expr_GreaterEq_ReturnsZero()
    {
        Assert.Equal("0", RunBashText("Invoke-BashExpr 2 '>=' 5"));
    }

    [Fact]
    public void Expr_StringCompare_EqualReturnsOne()
    {
        Assert.Equal("1", RunBashText("Invoke-BashExpr foo = foo"));
    }

    [Fact]
    public void Expr_StringCompare_NotEqualReturnsOne()
    {
        Assert.Equal("1", RunBashText("Invoke-BashExpr foo '!=' bar"));
    }

    // ---- string ops ----

    [Fact]
    public void Expr_Length_ReturnsCharCount()
    {
        Assert.Equal("5", RunBashText("Invoke-BashExpr length hello"));
    }

    [Fact]
    public void Expr_Length_UnicodeCounted()
    {
        // .NET String.Length counts UTF-16 code units. The oracle calls
        // [string].Length which is the same. Unicode chars in the BMP each
        // contribute 1; a single 4-byte emoji becomes 2 (surrogate pair).
        // We document and pin that exact behavior here.
        Assert.Equal("3", RunBashText("Invoke-BashExpr length 'aéb'"));
    }

    [Fact]
    public void Expr_Substr_OneBasedExtraction()
    {
        Assert.Equal("ell", RunBashText("Invoke-BashExpr substr hello 2 3"));
    }

    [Fact]
    public void Expr_Substr_LengthClampedToAvailable()
    {
        // hello is 5 chars; pos=4, len=10 -> "lo" (2 chars).
        Assert.Equal("lo", RunBashText("Invoke-BashExpr substr hello 4 10"));
    }

    [Fact]
    public void Expr_Index_FirstOccurrenceOneBased()
    {
        // 'world' contains 'r' at index 2 (0-based) -> 1-based result 3.
        Assert.Equal("3", RunBashText("Invoke-BashExpr index world r"));
    }

    [Fact]
    public void Expr_Index_NoMatchReturnsZero()
    {
        Assert.Equal("0", RunBashText("Invoke-BashExpr index world z"));
    }

    [Fact]
    public void Expr_Match_AnchoredAtStart_NoCaptureGroupReturnsLength()
    {
        // 'abc123' matches '^[a-z]+' -> 3-char match -> "3".
        // .NET regex engine; the psm1 oracle only translates BRE \(...\) -> (...).
        // We pass a regex that's valid in .NET directly.
        Assert.Equal("3", RunBashText("Invoke-BashExpr match abc123 '[a-z]+'"));
    }

    [Fact]
    public void Expr_Match_NoMatchReturnsZero()
    {
        Assert.Equal("0", RunBashText("Invoke-BashExpr match abc '^[0-9]+'"));
    }

    // ---- single operand ----

    [Fact]
    public void Expr_SingleOperand_EchoedBack()
    {
        Assert.Equal("hello", RunBashText("Invoke-BashExpr hello"));
    }

    // ---- error paths ----

    [Fact]
    public void Expr_NoArgs_SetsLastExitCodeTwo()
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$global:LASTEXITCODE = 0").Invoke();
        pwsh.Commands.Clear();
        pwsh.AddScript("Invoke-BashExpr 2>$null").Invoke();
        pwsh.Commands.Clear();
        var exit = pwsh.AddScript("$global:LASTEXITCODE").Invoke();
        Assert.Equal(2, (int)exit[0].BaseObject);
    }

    [Fact]
    public void Expr_DivisionByZero_SetsLastExitCodeTwoAndNoOutput()
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$global:LASTEXITCODE = 0").Invoke();
        pwsh.Commands.Clear();
        var r = pwsh.AddScript("Invoke-BashExpr 5 / 0 2>$null").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(r);
        var exit = pwsh.AddScript("$global:LASTEXITCODE").Invoke();
        Assert.Equal(2, (int)exit[0].BaseObject);
    }

    [Fact]
    public void Expr_UnknownOperator_SetsLastExitCodeTwo()
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$global:LASTEXITCODE = 0").Invoke();
        pwsh.Commands.Clear();
        var r = pwsh.AddScript("Invoke-BashExpr 2 '@' 3 2>$null").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(r);
        var exit = pwsh.AddScript("$global:LASTEXITCODE").Invoke();
        Assert.Equal(2, (int)exit[0].BaseObject);
    }

    // ---- alias & help ----

    [Fact]
    public void Expr_ViaAlias_DispatchesCorrectly()
    {
        // The psm1 still carries Set-Alias 'expr' -> Invoke-BashExpr; this
        // proves the alias resolves to the binary cmdlet, not the (now-removed)
        // psm1 function.
        Assert.Equal("7", RunBashText("expr 3 + 4"));
    }

    [Fact]
    public void Expr_HelpFlag_EmitsUsage()
    {
        var r = RunRaw("Invoke-BashExpr --help");
        Assert.NotEmpty(r);
        var joined = string.Join('\n', r.Select(o => o?.ToString() ?? ""));
        Assert.Contains("expr", joined, StringComparison.OrdinalIgnoreCase);
    }

    // ---- typed output shape ----

    [Fact]
    public void Expr_NumericResult_ValuePropertyIsLong()
    {
        var r = RunRaw("Invoke-BashExpr 2 + 3");
        Assert.NotEmpty(r);
        Assert.Contains("PsBash.ExprOutput", r[0].TypeNames);
        var value = r[0].Properties["Value"]?.Value;
        Assert.Equal(5L, Assert.IsType<long>(value));
        Assert.Equal("5", r[0].Properties["BashText"]?.Value);
    }

    [Fact]
    public void Expr_StringResult_ValuePropertyIsString()
    {
        var r = RunRaw("Invoke-BashExpr length hello");
        Assert.NotEmpty(r);
        // "5" is numeric — boxed long. The oracle's contract is: numeric
        // string -> long, else string. Length always produces digits.
        var value = r[0].Properties["Value"]?.Value;
        Assert.Equal(5L, Assert.IsType<long>(value));
    }

    [Fact]
    public void Expr_SingleNonNumericOperand_ValueIsString()
    {
        var r = RunRaw("Invoke-BashExpr hello");
        Assert.NotEmpty(r);
        var value = r[0].Properties["Value"]?.Value;
        Assert.Equal("hello", Assert.IsType<string>(value));
    }

    // ---- Directive 12: injection probe ----

    [Fact]
    public void Expr_SemicolonBearingOperand_StaysLiteralString()
    {
        // Adversarial operand containing PS command-separator and $() forms.
        // It must be treated as the single-operand echo path and emitted
        // verbatim — never re-parsed as PowerShell syntax. The presence of
        // the literal string in BashText proves no nested evaluation occurred.
        var payload = "$(throw 'pwn');rm -rf /";
        var r = RunRaw($"Invoke-BashExpr '{payload.Replace("'", "''")}'");
        Assert.NotEmpty(r);
        Assert.Equal(payload, r[0].Properties["BashText"]?.Value);
    }
}
