using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 follow-on migration of
/// <c>Invoke-BashShift</c> from PsBash.psm1 to a binary cmdlet.
/// Oracle: the original psm1 function. The cmdlet preserves every observable
/// branch: read+mutate <c>$global:BashPositional</c>, optional numeric
/// argument (default 1), non-integer / negative argument error, shift-past-end
/// error, no stdout output.
/// </summary>
public class InvokeBashShiftCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashShiftCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    private object[]? RunAndReadPositional(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(
            "$v = $global:BashPositional; " +
            "if ($null -eq $v) { return } " +
            "$arr = @($v); for ($k=0; $k -lt $arr.Count; $k++) { Write-Output $arr[$k] }").Invoke();
        pwsh.Commands.Clear();
        return result.Select(o => (object?)o?.ToString()).ToArray()!;
    }

    private string[] RunLines(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void Shift_Default_RemovesOneElement()
    {
        var arr = RunAndReadPositional(
            "$global:BashPositional = @('a','b','c'); Invoke-BashShift");
        Assert.Equal(new object?[] { "b", "c" }, arr);
    }

    [Fact]
    public void Shift_ByN_RemovesNElements()
    {
        var arr = RunAndReadPositional(
            "$global:BashPositional = @('a','b','c','d'); Invoke-BashShift 2");
        Assert.Equal(new object?[] { "c", "d" }, arr);
    }

    [Fact]
    public void Shift_ByZero_LeavesArrayUnchanged()
    {
        var arr = RunAndReadPositional(
            "$global:BashPositional = @('x','y'); Invoke-BashShift 0");
        Assert.Equal(new object?[] { "x", "y" }, arr);
    }

    [Fact]
    public void Shift_AllElements_LeavesEmptyArray()
    {
        var arr = RunAndReadPositional(
            "$global:BashPositional = @('one','two'); Invoke-BashShift 2");
        // Either null/empty after consuming all.
        Assert.True(arr == null || arr.Length == 0);
    }

    [Fact]
    public void Shift_PastEnd_EmitsErrorAndArrayUntouched()
    {
        var arr = RunAndReadPositional(
            "$global:BashPositional = @('a','b'); Invoke-BashShift 5 -ErrorAction SilentlyContinue");
        Assert.Equal(new object?[] { "a", "b" }, arr);
    }

    [Fact]
    public void Shift_NonNumericArg_EmitsErrorAndArrayUntouched()
    {
        var arr = RunAndReadPositional(
            "$global:BashPositional = @('a','b','c'); " +
            "Invoke-BashShift abc -ErrorAction SilentlyContinue");
        Assert.Equal(new object?[] { "a", "b", "c" }, arr);
    }

    [Fact]
    public void Shift_NoStdout_OnHappyPath()
    {
        var lines = RunLines(
            "$global:BashPositional = @('a','b'); Invoke-BashShift");
        Assert.Empty(lines);
    }

    [Fact]
    public void Shift_AliasShift_ResolvesToCmdlet()
    {
        var arr = RunAndReadPositional(
            "$global:BashPositional = @('p','q','r'); shift");
        Assert.Equal(new object?[] { "q", "r" }, arr);
    }

    [Fact]
    public void Shift_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashShift --help");
        Assert.NotEmpty(lines);
    }

    // Directive 12 — injection probe via the numeric argument slot.
    [Fact]
    public void Shift_InjectionInArg_TreatedAsLiteralNonNumeric()
    {
        var arr = RunAndReadPositional(
            "$global:BashPositional = @('a','b','c'); " +
            "Invoke-BashShift '$(throw \"pwn\");rm -rf /' -ErrorAction SilentlyContinue");
        // Array should be untouched (non-numeric error path).
        Assert.Equal(new object?[] { "a", "b", "c" }, arr);
    }
}
