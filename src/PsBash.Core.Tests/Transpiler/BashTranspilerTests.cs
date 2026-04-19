using Xunit;
using PsBash.Core.Transpiler;

namespace PsBash.Core.Tests.Transpiler;

public class BashTranspilerTests
{
    [Fact]
    public void SimpleEcho_PassesThrough()
    {
        Assert.Equal("Invoke-BashEcho hello", BashTranspiler.Transpile("echo hello"));
    }

    // Regression: Claude Code's Bash-tool prelude wraps every command in
    // `shopt ... 2>/dev/null || true && eval ... && pwd -P >| /tmp/x`.
    // Two bugs surfaced live:
    //   (a) `cmd || true` emitted `cmd || $global:LASTEXITCODE = 0; [void]$true`
    //       which PowerShell rejects: the `||` only consumes `$global:LASTEXITCODE`
    //       and `= 0` is leftover junk.
    //   (b) `>|` (force-clobber redirect) was lexed as `>` then `|`, producing
    //       `Invoke-BashRedirect -Path |` (empty pipe element).
    [Fact]
    public void OrTrue_InAndOrChain_ProducesParseableOperand()
    {
        var result = BashTranspiler.Transpile("cmd || true");
        // The `true` operand must be a single PowerShell expression; a script
        // block invocation `& { ... }` is one such expression.
        Assert.Contains("|| & { $global:LASTEXITCODE = 0; [void]$true }", result);
    }

    [Fact]
    public void ForceClobberRedirect_TreatedAsPlainStdoutRedirect()
    {
        var result = BashTranspiler.Transpile("echo hi >| out.txt");
        Assert.Equal("Invoke-BashEcho hi | Invoke-BashRedirect -Path out.txt", result);
    }

    [Fact]
    public void InputRedirectFromDevNull_DroppedNotEmittedAsGetContentNull()
    {
        // `Get-Content $null` throws "Cannot bind argument to parameter 'Path'
        // because it is null." — so drop the redirect entirely.
        var result = BashTranspiler.Transpile("eval 'echo hi' < /dev/null");
        Assert.DoesNotContain("Get-Content $null", result);
    }

    [Fact]
    public void MsysDrivePathInRedirectTarget_TranslatedToWindowsPath_WhenUnixPathsOn()
    {
        var prior = Environment.GetEnvironmentVariable("PSBASH_UNIX_PATHS");
        Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", "1");
        try
        {
            var result = BashTranspiler.Transpile("echo hi > /c/Users/andyb/foo.log");
            Assert.Contains("Invoke-BashRedirect -Path C:\\Users\\andyb\\foo.log", result);
        }
        finally { Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", prior); }
    }

    [Fact]
    public void MsysDrivePathInRedirectTarget_PreservedLiteral_WhenUnixPathsOff()
    {
        var prior = Environment.GetEnvironmentVariable("PSBASH_UNIX_PATHS");
        Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", "0");
        try
        {
            var result = BashTranspiler.Transpile("echo hi > /c/Users/andyb/foo.log");
            Assert.Contains("Invoke-BashRedirect -Path /c/Users/andyb/foo.log", result);
        }
        finally { Environment.SetEnvironmentVariable("PSBASH_UNIX_PATHS", prior); }
    }

    [Fact]
    public void ClaudeCodeBashWrapper_TranspilesWithoutEmptyPipeOrAssignmentJunk()
    {
        // Captured live from Claude Code Bash tool argv tracing.
        var input = "shopt -u extglob 2>/dev/null || true && eval 'echo hi' < /dev/null && pwd -P >| /tmp/cwd";
        var result = BashTranspiler.Transpile(input);
        // The two specific failures we observed must not appear:
        Assert.DoesNotContain("|| $global:LASTEXITCODE = 0;", result);
        Assert.DoesNotContain("Invoke-BashRedirect -Path |", result);
        // And the force-clobber must produce a real path arg.
        Assert.Contains("Invoke-BashRedirect -Path", result);
    }

    [Fact]
    public void DevNullWithEnvVar_TransformsBoth()
    {
        var result = BashTranspiler.Transpile("echo $FOO 2> /dev/null");
        Assert.Equal("Invoke-BashEcho $env:FOO 2>$null", result);
    }

    [Fact]
    public void ExportAndEchoVar_TransformsBoth()
    {
        var result = BashTranspiler.Transpile("export FOO=bar");
        Assert.Equal("$env:FOO = \"bar\"", result);
    }

    [Fact]
    public void TmpPathWithGrep_TransformsBoth()
    {
        var result = BashTranspiler.Transpile("cat /tmp/log.txt | grep error");
        Assert.Equal("Invoke-BashCat $env:TEMP\\log.txt | Invoke-BashGrep error", result);
    }

    [Fact]
    public void FileTestWithVar_TransformsBoth()
    {
        var result = BashTranspiler.Transpile("[ -f /etc/config ] && echo $MSG");
        Assert.Equal("[void](Test-Path \"/etc/config\" -PathType Leaf) && Invoke-BashEcho $env:MSG", result);
    }

    [Fact]
    public void HomePathWithPipe_TransformsBoth()
    {
        var result = BashTranspiler.Transpile("ls ~/.config | head -n 5");
        Assert.Equal("Invoke-BashLs $HOME\\.config | Invoke-BashHead -n 5", result);
    }

    [Fact]
    public void ComplexPipeline_TransformsAll()
    {
        var result = BashTranspiler.Transpile("cat /tmp/data.csv | grep -v header | sort | uniq | wc -l");
        Assert.Equal(
            "Invoke-BashCat $env:TEMP\\data.csv | Invoke-BashGrep -v header | Invoke-BashSort | Invoke-BashUniq | Invoke-BashWc -l",
            result);
    }

    [Fact]
    public void ExportQuotedValue_TransformsCorrectly()
    {
        var result = BashTranspiler.Transpile("export NODE_ENV=\"production\"");
        Assert.Equal("$env:NODE_ENV = \"production\"", result);
    }

    [Fact]
    public void DevNullRedirectWithStderrMerge_TransformsCorrectly()
    {
        var result = BashTranspiler.Transpile("cmd > /dev/null 2>&1");
        Assert.Equal("cmd >$null 2>&1", result);
    }

    [Fact]
    public void EnvVarDoesNotDoubleTransform()
    {
        var result = BashTranspiler.Transpile("export FOO=bar && echo $FOO");
        Assert.Contains("$env:FOO = \"bar\"", result);
        Assert.Contains("$env:FOO", result);
        Assert.DoesNotContain("$env:env:", result);
    }

    [Fact]
    public void PipeSedAndAwk_TransformsBoth()
    {
        var result = BashTranspiler.Transpile("cat file | sed 's/old/new/' | awk '{print $1}'");
        Assert.Equal("Invoke-BashCat file | Invoke-BashSed 's/old/new/' | Invoke-BashAwk '{print $1}'", result);
    }

    [Fact]
    public void AwkWithFlags_PreservesExpression()
    {
        var result = BashTranspiler.Transpile("echo \"a,b,c\" | awk -F, '{print $1, $3}'");
        Assert.Equal("Invoke-BashEcho \"a,b,c\" | Invoke-BashAwk \"-F,\" '{print $1, $3}'", result);
    }

    [Fact]
    public void FileTestEmptyVar_TransformsCorrectly()
    {
        var result = BashTranspiler.Transpile("[ -z \"$HOME\" ] && echo empty");
        Assert.Equal("[void]([string]::IsNullOrEmpty($HOME)) && Invoke-BashEcho empty", result);
    }

    [Fact]
    public void FileTestWithAnd_WrapsInVoid()
    {
        var result = BashTranspiler.Transpile("[ -f ./README.md ] && echo \"exists\"");
        Assert.Equal("[void](Test-Path \"./README.md\" -PathType Leaf) && Invoke-BashEcho \"exists\"", result);
    }

    [Fact]
    public void DirTestWithAnd_WrapsInVoid()
    {
        var result = BashTranspiler.Transpile("[ -d ./src ] && echo \"is dir\"");
        Assert.Equal("[void](Test-Path \"./src\" -PathType Container) && Invoke-BashEcho \"is dir\"", result);
    }

    [Fact]
    public void ExportWithAnd_WrapsInVoid()
    {
        var result = BashTranspiler.Transpile("export FOO=\"bar\" && echo $FOO");
        Assert.Equal("[void]($env:FOO = \"bar\") && Invoke-BashEcho $env:FOO", result);
    }

    [Fact]
    public void FileTestWithOr_WrapsInVoid()
    {
        var result = BashTranspiler.Transpile("[ -f missing ] || echo \"not found\"");
        Assert.Equal("[void](Test-Path \"missing\" -PathType Leaf) || Invoke-BashEcho \"not found\"", result);
    }

    [Fact]
    public void TmpPath_TransformsToEnvTemp()
    {
        var result = BashTranspiler.Transpile("echo /tmp/test");
        Assert.Contains("$env:TEMP", result);
    }
}
