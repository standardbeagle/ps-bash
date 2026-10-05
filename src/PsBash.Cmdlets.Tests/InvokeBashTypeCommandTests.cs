using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of Invoke-BashType
/// from PsBash.psm1 to a binary cmdlet (PsBash.Cmdlets.dll).
///
/// Oracle: the psm1 function classified a command name as
/// alias / function / builtin / file / not-found and (in -p mode) emitted a
/// bash-style declare line for a variable value.
///
/// Failure-surface axes that apply: missing target (unknown name, Directive 3
/// axis 14), quoting / injection (Directive 12), alias resolution. Streaming /
/// file-content / signal axes do not apply: type is in-process metadata.
/// </summary>
public class InvokeBashTypeCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashTypeCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    private System.Collections.ObjectModel.Collection<System.Management.Automation.PSObject> RunRaw(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result;
    }

    private string[] RunLines(string script)
    {
        return RunRaw(script)
            .Select(o =>
            {
                if (o == null) return "";
                var bt = o.Properties["BashText"]?.Value as string;
                return bt ?? o.ToString() ?? "";
            })
            .ToArray();
    }

    [Fact]
    public void Type_KnownCmdlet_ReportsFile()
    {
        // Get-Item is a real PowerShell cmdlet — Get-Command returns it with
        // CommandType=Cmdlet, oracle classifies as "file" (default branch of
        // the switch — only Alias and Function get special-cased).
        var lines = RunLines("Invoke-BashType Get-Item");
        Assert.Single(lines);
        Assert.Contains("Get-Item is ", lines[0]);
    }

    [Fact]
    public void Type_PsBashCommand_IsAFileNamedByItsBashName()
    {
        // `ls` is the runtime's own ls (alias to Invoke-BashLs): no file, so its "path" is its bash name. The
        // cmdlet name used to leak ("ls is aliased to `Invoke-BashLs'").
        var lines = RunLines("Invoke-BashType ls");
        Assert.Equal(new[] { "ls is ls" }, lines);
        Assert.Equal(new[] { "file" }, RunLines("Invoke-BashType '-t' ls"));
    }

    private (string[] lines, int exit) RunWithExit(string script)
    {
        // One runspace: the status is appended as the last line.
        var all = RunLines("$global:LASTEXITCODE = 0; " + script + "; \"rc=$global:LASTEXITCODE\"");
        return (all[..^1], int.Parse(all[^1]["rc=".Length..]));
    }

    [Fact]
    public void Type_DashP_PsBashCommand_PrintsItsName()
    {
        // `type -p jq >/dev/null` is a stock availability probe; it must succeed for the runtime's own commands.
        var (lines, exit) = RunWithExit("Invoke-BashType '-p' ls");
        Assert.Equal(new[] { "ls" }, lines);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void Type_DashP_Builtin_PrintsNothingStatusZero()
    {
        // bash 5.2: `type -p cd` → no output, status 0; `type -P cd` → no output, status 1.
        var (lines, exit) = RunWithExit("Invoke-BashType '-p' echo");
        Assert.Empty(lines);
        Assert.Equal(0, exit);

        // `cd`, not `echo`: CI runners have an echo PROGRAM on PATH (/usr/bin/echo, Git's echo.exe), which -P
        // rightly finds; no platform ships a cd binary on PATH.
        var (big, bigExit) = RunWithExit("Invoke-BashType '-P' cd");
        Assert.Empty(big);
        Assert.Equal(1, bigExit);
    }

    [Fact]
    public void Type_DashT_Missing_PrintsNothingStatusOne()
    {
        var (lines, exit) = RunWithExit("Invoke-BashType '-t' definitely_not_a_real_command_xyz 2>&1");
        Assert.Empty(lines);
        Assert.Equal(1, exit);
    }

    [Fact]
    public void Type_BashFunction_IsAFunction()
    {
        var def = "function psbtype_fn {" + PsBash.Core.Parser.PsBuild.FunctionPrologue + "1"
            + PsBash.Core.Parser.PsBuild.FunctionEpilogue + "}; ";
        Assert.Equal(new[] { "function" }, RunLines(def + "Invoke-BashType '-t' psbtype_fn"));
    }

    [Fact]
    public void Type_NonexistentName_NoSuccessOutput()
    {
        // The oracle writes "bash: type: NAME: not found" to error; we set
        // $LASTEXITCODE=1. The success-pipeline carries no objects.
        var lines = RunLines("Invoke-BashType definitely_not_a_real_command_xyz");
        Assert.Empty(lines);
    }

    [Fact]
    public void Type_DashT_EmitsKindOnly()
    {
        // -t mode returns just the kind word, not the descriptive sentence.
        var lines = RunLines("Invoke-BashType -t Get-Item");
        Assert.Single(lines);
        // Get-Item is a cmdlet → oracle's default switch arm → "file".
        Assert.Equal("file", lines[0]);
    }

    [Fact]
    public void Type_DashT_BuiltinName_ReportsBuiltin()
    {
        // `echo` is in the hard-coded builtins list; -t returns "builtin".
        var lines = RunLines("Invoke-BashType -t echo");
        Assert.Single(lines);
        Assert.Equal("builtin", lines[0]);
    }

    [Fact]
    public void Type_DashP_DirectCall_IsThePathForm()
    {
        // `-p` is bash's path form (it used to print a VARIABLE — that is `declare -p`, Invoke-BashDeclare).
        // A bare direct-call -p binds the P decoy; a missing name prints nothing.
        Assert.Equal(new[] { "ls" }, RunLines("Invoke-BashType -p ls"));
        Assert.Empty(RunLines("Invoke-BashType -p totally_nonexistent_cmd_zz"));
    }

    [Fact]
    public void Type_DashA_BuiltinAndAlias_EmitsAllMatches()
    {
        // `echo` is both a builtin (hard-coded) AND a PS alias to Write-Output.
        // The oracle's PS alias only emits if definition matches the
        // Invoke-Bash / Get-Bash / Set-Bash / ConvertFrom- regex, so the alias
        // path is suppressed for echo→Write-Output. -a still emits the builtin.
        var lines = RunLines("Invoke-BashType -a echo");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("shell builtin"));
    }

    [Fact]
    public void Type_MultipleNames_EmitsOnePerName()
    {
        // Two operands → two emissions in order.
        var lines = RunLines("Invoke-BashType echo Get-Item");
        Assert.Equal(2, lines.Length);
        Assert.Contains("shell builtin", lines[0]);
        Assert.Contains("Get-Item", lines[1]);
    }

    [Fact]
    public void Type_ViaAlias_Works()
    {
        // The `type` alias (declared in psm1) must resolve to the cmdlet.
        var lines = RunLines("type -t echo");
        Assert.Single(lines);
        Assert.Equal("builtin", lines[0]);
    }

    [Fact]
    public void Type_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashType --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Type_NoOperand_NoSuccessOutput()
    {
        // Missing operand routes through Write-BashError, no success object.
        var lines = RunLines("Invoke-BashType");
        Assert.Empty(lines);
    }

    // ---- injection probe (Directive 12) ----

    [Fact]
    public void Type_NameWithScriptblockChars_TreatedAsLiteralName()
    {
        // A command name containing $() / ; must not be re-parsed as
        // PowerShell. The cmdlet's lookups (Get-Command -Name, Get-Alias)
        // bind the name as a parameter — the binder treats it as a string
        // literal, no nested evaluation.
        //
        // Asserts: pwsh.Invoke() returns normally (no RuntimeException
        // carrying "pwn") AND the name lands in the not-found error branch
        // (zero success-pipeline objects). Negative-assertion is the security
        // probe per the playbook.
        var lines = RunLines("Invoke-BashType '$(throw \"pwn\")'");
        Assert.Empty(lines);
    }
}
