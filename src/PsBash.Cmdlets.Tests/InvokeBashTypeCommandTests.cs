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
public class InvokeBashTypeCommandTests
{
    private static System.Collections.ObjectModel.Collection<System.Management.Automation.PSObject> RunRaw(string script)
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("Set-BashErrorMode -Mode PowerShell").Invoke();
        pwsh.Commands.Clear();
        pwsh.AddScript("$error.Clear()").Invoke();
        pwsh.Commands.Clear();

        return pwsh.AddScript(script).Invoke();
    }

    private static string[] RunLines(string script)
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
    public void Type_BashAlias_ResolvesViaPsm1Alias()
    {
        // `ls` is a psm1 alias for Invoke-BashLs — the alias-probe branch
        // gates on `^Invoke-Bash|^Get-Bash|^Set-Bash|^ConvertFrom-`, so this
        // hits and emits "ls is aliased to `Invoke-BashLs'".
        var lines = RunLines("Invoke-BashType ls");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("ls is aliased to") &&
                                    l.Contains("Invoke-BashLs"));
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
    public void Type_DashP_KnownVariable_EmitsDeclareLine()
    {
        // -p mode formats a global PowerShell variable as bash declare syntax.
        var lines = RunLines("$global:myvar = 'hello'; Invoke-BashType -p myvar");
        Assert.Single(lines);
        Assert.Equal("declare -- myvar=\"hello\"", lines[0]);
    }

    [Fact]
    public void Type_DashP_MissingName_NoSuccessOutput()
    {
        // -p on a non-existent variable hits the not-found error branch.
        var lines = RunLines("Invoke-BashType -p totally_nonexistent_var_zz");
        Assert.Empty(lines);
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
