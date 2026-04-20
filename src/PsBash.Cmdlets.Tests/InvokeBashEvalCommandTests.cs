using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Xunit;

namespace PsBash.Cmdlets.Tests;

public class InvokeBashEvalCommandTests
{
    private static PowerShell CreatePwsh()
    {
        var iss = InitialSessionState.CreateDefault2();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        var pwsh = PowerShell.Create();
        pwsh.Runspace = runspace;

        // Import PsBash script module from source directory.
        var psbashPsd1 = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "PsBash.Module", "PsBash.psd1"));
        if (File.Exists(psbashPsd1))
        {
            pwsh.AddCommand("Import-Module").AddParameter("Name", psbashPsd1).Invoke();
            pwsh.Commands.Clear();
        }

        // Import PsBash.Cmdlets binary module from test output directory.
        var cmdletDll = Path.Combine(AppContext.BaseDirectory, "PsBash.Cmdlets.dll");
        pwsh.AddCommand("Import-Module").AddParameter("Name", cmdletDll).Invoke();
        pwsh.Commands.Clear();

        return pwsh;
    }

    [Fact]
    public void EchoVar_WithPassThru_ReturnsOutput()
    {
        using var pwsh = CreatePwsh();
        var result = pwsh.AddScript("Invoke-BashEval 'x=5; echo $x' -PassThru").Invoke();
        Assert.NotEmpty(result);
        Assert.Equal("5", result[0].ToString());
    }

    [Fact]
    public void EchoVar_WithoutPassThru_DiscardsOutput()
    {
        using var pwsh = CreatePwsh();
        var result = pwsh.AddScript("Invoke-BashEval 'x=5; echo $x'").Invoke();
        Assert.Empty(result);
    }

    [Fact]
    public void ExportVar_SetsEnvVarInCallerScope()
    {
        using var pwsh = CreatePwsh();
        var prior = Environment.GetEnvironmentVariable("PSBASH_EVAL_TEST_FOO");
        try
        {
            Environment.SetEnvironmentVariable("PSBASH_EVAL_TEST_FOO", null);
            var result = pwsh.AddScript(
                "Invoke-BashEval 'export PSBASH_EVAL_TEST_FOO=bar'; $env:PSBASH_EVAL_TEST_FOO")
                .Invoke();
            Assert.Single(result);
            Assert.Equal("bar", result[0].ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSBASH_EVAL_TEST_FOO", prior);
        }
    }

    [Fact]
    public void Cd_ExecutesWithoutError()
    {
        using var pwsh = CreatePwsh();
        // cd is passed through as-is by the transpiler; verify it executes cleanly
        // (actual location change requires Set-Location cmdlet which is not loaded
        // in the minimal runspace used for these unit tests).
        var result = pwsh.AddScript("Invoke-BashEval 'cd $HOME'").Invoke();
        pwsh.Commands.Clear();
        var errors = pwsh.AddScript("$error | Select-Object -First 1").Invoke();
        pwsh.Commands.Clear();
        var errorText = errors.Count > 0 ? errors[0]?.ToString() : string.Empty;
        Assert.True(errors.Count == 0 || string.IsNullOrEmpty(errorText),
            $"cd command threw an error: {errorText}");
    }

    [Fact]
    public void FunctionDef_DefinesFunctionInCallerScope()
    {
        using var pwsh = CreatePwsh();
        var result = pwsh.AddScript(
            "Invoke-BashEval 'greet() { echo hi; }'; greet")
            .Invoke();
        Assert.NotEmpty(result);
        Assert.Equal("hi", result[0].ToString());
    }

    [Fact]
    public void NoLocalScope_IsolatesFunctionScope()
    {
        using var pwsh = CreatePwsh();
        var result = pwsh.AddScript(
            "Invoke-BashEval 'function psbash_isolated_func { 42 }' -NoLocalScope; " +
            "Get-Command psbash_isolated_func -ErrorAction SilentlyContinue")
            .Invoke();
        Assert.Empty(result);
    }

    [Fact]
    public void PipelineInput_EvaluatesEachRecord()
    {
        using var pwsh = CreatePwsh();
        var result = pwsh.AddScript(
            "@('echo a', 'echo b') | Invoke-BashEval -PassThru")
            .Invoke();
        Assert.Equal(2, result.Count);
        Assert.Equal("a", result[0].ToString());
        Assert.Equal("b", result[1].ToString());
    }
}
