using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 follow-on migration of
/// <c>Invoke-BashUnset</c> from PsBash.psm1 to a binary cmdlet.
/// Oracle: the original psm1 function. Variable mode (default / <c>-v</c>)
/// removes the variable from the caller's scope AND the matching env var;
/// function mode (<c>-f</c>) removes the function from <c>Function:\NAME</c>.
/// Missing names silently ignored. <c>-v</c> prefix-collides with
/// <c>-Verbose</c> — covered by the explicit <c>SwitchParameter V</c> binding.
/// </summary>
public class InvokeBashUnsetCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashUnsetCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    private string? RunAndReadVar(string script, string varName)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(
            $"$v = Get-Variable -Name '{varName}' -ValueOnly -ErrorAction SilentlyContinue; " +
            "if ($null -eq $v) { 'NULL' } else { [string]$v }").Invoke();
        pwsh.Commands.Clear();
        return result.FirstOrDefault()?.ToString();
    }

    private string? RunAndReadEnv(string script, string envName)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(
            $"if (Test-Path 'Env:\\{envName}') {{ (Get-Item 'Env:\\{envName}').Value }} else {{ 'NULL' }}").Invoke();
        pwsh.Commands.Clear();
        return result.FirstOrDefault()?.ToString();
    }

    private bool RunAndCheckFunctionExists(string script, string funcName)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(
            $"[bool](Test-Path 'Function:\\{funcName}')").Invoke();
        pwsh.Commands.Clear();
        var first = result.FirstOrDefault();
        return first != null && (bool)first.BaseObject;
    }

    private string[] RunLines(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void Unset_Variable_RemovesEnvEntry()
    {
        // The env-var removal slice is the more reliably-observable half of
        // the unset contract from the test-fixture runspace.
        var after = RunAndReadEnv(
            "$env:PSBASH_TEST_UNSET_VAR = 'present'; " +
            "Invoke-BashUnset PSBASH_TEST_UNSET_VAR",
            "PSBASH_TEST_UNSET_VAR");
        Assert.Equal("NULL", after);
    }

    [Fact]
    public void Unset_FunctionFlag_RemovesFunction()
    {
        var stillExists = RunAndCheckFunctionExists(
            "function Test-PsBashUnsetTarget { 'hi' }; " +
            "Invoke-BashUnset -f Test-PsBashUnsetTarget",
            "Test-PsBashUnsetTarget");
        Assert.False(stillExists);
    }

    [Fact]
    public void Unset_DefaultMode_IsVariableNotFunction()
    {
        // Without -f, a function with the same name should NOT be removed.
        var stillExists = RunAndCheckFunctionExists(
            "function Test-PsBashKeepMe { 'hi' }; " +
            "Invoke-BashUnset Test-PsBashKeepMe",
            "Test-PsBashKeepMe");
        Assert.True(stillExists);
    }

    [Fact]
    public void Unset_MissingName_SilentlyIgnored()
    {
        // No error should fire for a name that does not exist.
        var lines = RunLines(
            "Invoke-BashUnset PSBASH_DOES_NOT_EXIST_XYZ; 'after'");
        Assert.Contains("after", lines);
    }

    [Fact]
    public void Unset_MultipleNames_RemovesAll()
    {
        var v1 = RunAndReadEnv(
            "$env:PSBASH_A1='1'; $env:PSBASH_A2='2'; " +
            "Invoke-BashUnset PSBASH_A1 PSBASH_A2",
            "PSBASH_A1");
        var v2 = RunAndReadEnv(
            "$env:PSBASH_A1='1'; $env:PSBASH_A2='2'; " +
            "Invoke-BashUnset PSBASH_A1 PSBASH_A2",
            "PSBASH_A2");
        Assert.Equal("NULL", v1);
        Assert.Equal("NULL", v2);
    }

    [Fact]
    public void Unset_VFlagThenName_BindsViaExplicitSwitch()
    {
        // -v prefix-collides with -Verbose. The explicit SwitchParameter V on
        // the cmdlet should bind it without triggering "ambiguous parameter".
        var after = RunAndReadEnv(
            "$env:PSBASH_VFLAG_TEST='here'; " +
            "Invoke-BashUnset -v PSBASH_VFLAG_TEST",
            "PSBASH_VFLAG_TEST");
        Assert.Equal("NULL", after);
    }

    [Fact]
    public void Unset_NoStdout()
    {
        var lines = RunLines(
            "$env:PSBASH_NOSTDOUT='x'; Invoke-BashUnset PSBASH_NOSTDOUT");
        Assert.Empty(lines);
    }

    [Fact]
    public void Unset_AliasUnset_ResolvesToCmdlet()
    {
        var after = RunAndReadEnv(
            "$env:PSBASH_ALIAS_TEST='hi'; unset PSBASH_ALIAS_TEST",
            "PSBASH_ALIAS_TEST");
        Assert.Equal("NULL", after);
    }

    [Fact]
    public void Unset_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashUnset --help");
        Assert.NotEmpty(lines);
    }

    // Directive 12 — name token containing scriptblock chars should not be
    // re-parsed as PowerShell. The parameter-bound InvokeScript body uses
    // string concatenation on a positional $args entry, never an expansion of
    // the raw token into the script body.
    [Fact]
    public void Unset_InjectionInName_DoesNotExecutePayload()
    {
        // The name is literal — it can't match any real env var, and Remove-Item
        // -ErrorAction SilentlyContinue swallows the lookup miss without throwing.
        var lines = RunLines(
            "$env:PSBASH_INJ_PROBE='safe'; " +
            "Invoke-BashUnset '$(throw \"PWNED\")' -ErrorAction SilentlyContinue; " +
            "$env:PSBASH_INJ_PROBE");
        // The literal env var should still exist and be readable.
        Assert.Contains("safe", lines);
    }
}
