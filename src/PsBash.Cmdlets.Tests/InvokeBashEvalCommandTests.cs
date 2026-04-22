using Xunit;

namespace PsBash.Cmdlets.Tests;

public class InvokeBashEvalCommandTests
{
    [Fact]
    public void EchoVar_WithPassThru_ReturnsOutput()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript("Invoke-BashEval 'x=5; echo $x' -PassThru").Invoke();
        Assert.NotEmpty(result);
        Assert.Equal("5", result[0].ToString());
    }

    [Fact]
    public void EchoVar_WithoutPassThru_DiscardsOutput()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript("Invoke-BashEval 'x=5; echo $x'").Invoke();
        Assert.Empty(result);
    }

    [Fact]
    public void ExportVar_SetsEnvVarInCallerScope()
    {
        using var pwsh = PwshTestFixture.Create();
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
        using var pwsh = PwshTestFixture.Create();
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
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript(
            "Invoke-BashEval 'greet() { echo hi; }'; greet")
            .Invoke();
        Assert.NotEmpty(result);
        Assert.Equal("hi", result[0].ToString());
    }

    [Fact]
    public void NoLocalScope_IsolatesFunctionScope()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript(
            "Invoke-BashEval 'function psbash_isolated_func { 42 }' -NoLocalScope; " +
            "Get-Command psbash_isolated_func -ErrorAction SilentlyContinue")
            .Invoke();
        Assert.Empty(result);
    }

    [Fact]
    public void PipelineInput_EvaluatesEachRecord()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript(
            "@('echo a', 'echo b') | Invoke-BashEval -PassThru")
            .Invoke();
        Assert.Equal(2, result.Count);
        Assert.Equal("a", result[0].ToString());
        Assert.Equal("b", result[1].ToString());
    }

    [Fact]
    public void ParseError_ThrowsTranspileFailedWithBashLine1()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript(@"
            try {
                Invoke-BashEval 'if [ ] then'
            } catch {
                $_.FullyQualifiedErrorId + '|' + $_.Exception.Message
            }
        ").Invoke();
        Assert.Single(result);
        var parts = result[0].ToString().Split('|');
        Assert.StartsWith("PsBash.TranspileFailed", parts[0]);
        Assert.Contains("bash:1:", parts[1]);
    }

    [Fact]
    public void RuntimeError_ThrowsRuntimeFailedWithBashLine2()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript(@"
            try {
                Invoke-BashEval 'echo ok
nonexistent'
            } catch {
                $_.FullyQualifiedErrorId + '|' + $_.ErrorDetails.Message
            }
        ").Invoke();
        Assert.Single(result);
        var parts = result[0].ToString().Split('|');
        Assert.StartsWith("PsBash.RuntimeFailed", parts[0]);
        Assert.Contains("bash:2:", parts[1]);
    }

    [Fact]
    public void TryCatch_ParseException_IsCATCHABLE()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript(@"
            try {
                Invoke-BashEval 'if [ ] then'
            } catch [System.Management.Automation.ParseException] {
                'caught-parse-exception'
            }
        ").Invoke();
        Assert.Single(result);
        Assert.Equal("caught-parse-exception", result[0].ToString());
    }

    [Fact]
    public void False_SetsLastExitCodeToOne()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript("Invoke-BashEval 'false'; $LASTEXITCODE").Invoke();
        Assert.Single(result);
        Assert.Equal("1", result[0].ToString());
    }

    [Fact]
    public void False_AndEcho_DoesNotOutput()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript("Invoke-BashEval 'false && echo yes' -PassThru").Invoke();
        Assert.Empty(result);
    }

    [Fact]
    public void SetE_False_ThrowsErrexitAndUnreachableNotPrinted()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript(@"
            $output = @()
            $errorId = ''
            try {
                Invoke-BashEval 'set -e; false; echo unreachable' -PassThru | ForEach-Object { $output += $_ }
            } catch {
                $errorId = $_.FullyQualifiedErrorId
            }
            [PSCustomObject]@{
                ErrorId = $errorId
                Output = ($output -join ',')
            }
        ").Invoke();
        Assert.Single(result);
        var errorId = result[0].Properties["ErrorId"]?.Value?.ToString() ?? "";
        var output = result[0].Properties["Output"]?.Value?.ToString() ?? "";
        Assert.Equal("PsBash.ErrexitFailure", errorId.Split(',')[0]);
        Assert.DoesNotContain("unreachable", output);
    }

    [Fact]
    public void TrapExit_RunsOnCompletion()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript(
            "Invoke-BashEval 'trap ''Invoke-BashEcho EXIT'' EXIT; echo body' -PassThru")
            .Invoke();
        Assert.Equal(2, result.Count);
        Assert.Equal("body", result[0].ToString());
        Assert.Equal("EXIT", result[1].ToString());
    }

    [Fact]
    public void TrapErr_RunsOnFailure()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript(
            "Invoke-BashEval 'trap ''Invoke-BashEcho ERR'' ERR; false' -PassThru")
            .Invoke();
        Assert.Single(result);
        Assert.Equal("ERR", result[0].ToString());
    }

    [Fact]
    public void LastExitCode_DoesNotClobberOnSuccessPathThatSetsNothing()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript(@"
            $LASTEXITCODE = 42
            Invoke-BashEval 'echo hello'
            $LASTEXITCODE
        ").Invoke();
        Assert.Single(result);
        Assert.Equal("42", result[0].ToString());
    }

    [Fact]
    public void SetE_EchoHi_CallerLastExitCodePreserved()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript(@"
            $LASTEXITCODE = 1
            $errorId = ''
            try {
                Invoke-BashEval 'set -e; echo hi'
            } catch {
                $errorId = $_.FullyQualifiedErrorId
            }
            [PSCustomObject]@{
                ErrorId = $errorId
                LastExitCode = $LASTEXITCODE
            }
        ").Invoke();
        Assert.Single(result);
        Assert.Equal("", result[0].Properties["ErrorId"]?.Value?.ToString() ?? "");
        Assert.Equal("1", result[0].Properties["LastExitCode"]?.Value?.ToString() ?? "");
    }

    [Fact]
    public void TrapErr_DoesNotFireOnStaleLastExitCode()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript(@"
            $LASTEXITCODE = 7
            Invoke-BashEval 'trap ''Invoke-BashEcho TRAPPED'' ERR; echo ok' -PassThru
        ").Invoke();
        Assert.Single(result);
        Assert.Equal("ok", result[0].ToString());
    }

    [Fact]
    public void TrapErrAndExit_SetE_False_BothFire()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript(@"
            $global:errFired = $false
            $global:exitFired = $false
            $errorId = ''
            try {
                Invoke-BashEval 'set -e; trap ''$global:errFired=$true'' ERR; trap ''$global:exitFired=$true'' EXIT; false; echo unreachable'
            } catch {
                $errorId = $_.FullyQualifiedErrorId
            }
            [PSCustomObject]@{
                ErrorId = $errorId
                ErrFired = $global:errFired
                ExitFired = $global:exitFired
            }
        ").Invoke();
        Assert.Single(result);
        var errorId = result[0].Properties["ErrorId"]?.Value?.ToString() ?? "";
        Assert.Equal("PsBash.ErrexitFailure", errorId.Split(',')[0]);
        Assert.Equal("True", result[0].Properties["ErrFired"]?.Value?.ToString() ?? "");
        Assert.Equal("True", result[0].Properties["ExitFired"]?.Value?.ToString() ?? "");
    }
}
