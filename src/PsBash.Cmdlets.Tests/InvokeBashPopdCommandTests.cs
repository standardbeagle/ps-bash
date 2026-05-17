using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 dir-stack-batch migration of
/// Invoke-BashPopd from PsBash.psm1 to a binary cmdlet.
/// </summary>
public class InvokeBashPopdCommandTests
{
    private static System.Collections.ObjectModel.Collection<System.Management.Automation.PSObject> RunRaw(
        System.Management.Automation.PowerShell pwsh, string script)
    {
        pwsh.Commands.Clear();
        return pwsh.AddScript(script).Invoke();
    }

    private static string[] RunLines(System.Management.Automation.PowerShell pwsh, string script)
    {
        return RunRaw(pwsh, script).Select(o => o?.ToString() ?? "").ToArray();
    }

    private static System.Management.Automation.PowerShell NewPwsh()
    {
        var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("Set-BashErrorMode -Mode PowerShell").Invoke();
        pwsh.Commands.Clear();
        return pwsh;
    }

    [Fact]
    public void Popd_AfterPushd_RestoresPriorLocation()
    {
        // Note: each `Invoke-Bash*` call runs through the cmdlet's
        // `InvokeCommand.InvokeScript` body, which carries a child
        // SessionState. PowerShell's location stack is shared across the
        // runspace for chdir purposes (Pop-Location's chdir IS visible
        // outside) but the stack-count snapshot read from outside the
        // InvokeScript reflects pushes that the child scope didn't
        // unwind, so we assert only the chdir side of the contract here.
        // The stack count is independently checked in the dirs tests via
        // `-c` clear and inside-cmdlet reads.
        using var pwsh = NewPwsh();
        var tmp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var subdir = Path.Combine(tmp, "psbash-popd-1-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(subdir);
        try
        {
            RunLines(pwsh, $"Set-Location '{tmp}'");
            RunLines(pwsh, $"Invoke-BashPushd '{subdir}'");
            RunLines(pwsh, "Invoke-BashPopd");
            var cwd = RunLines(pwsh, "(Get-Location).Path");
            Assert.Equal(tmp, cwd[0]);
        }
        finally
        {
            Directory.Delete(subdir);
        }
    }

    [Fact]
    public void Popd_OnEmptyStack_DoesNotThrow()
    {
        // Oracle: Pop-Location on empty stack writes a non-terminating
        // PowerShell error; the cmdlet catches RuntimeException and routes
        // via Write-BashError. The pwsh.Invoke() must not throw.
        using var pwsh = NewPwsh();
        var ex = Record.Exception(() => RunLines(pwsh, "Invoke-BashPopd"));
        Assert.Null(ex);
    }

    [Fact]
    public void Popd_PlusN_AcceptsRotationFlagWithoutThrowing()
    {
        // Oracle: popd +N pops (N+1) entries off the stack via -Stack. Same
        // SessionState scope caveat as Pushd_PlusN — assert the contract
        // that survives the in-process SDK isolation: a `+N` token matches
        // the regex, parses cleanly, and the InvokeScript body executes
        // without throwing.
        using var pwsh = NewPwsh();
        var tmp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var d1 = Path.Combine(tmp, "psbash-popd-2a-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var d2 = Path.Combine(tmp, "psbash-popd-2b-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(d1);
        Directory.CreateDirectory(d2);
        try
        {
            RunLines(pwsh, $"Set-Location '{tmp}'");
            RunLines(pwsh, $"Invoke-BashPushd '{d1}'");
            RunLines(pwsh, $"Invoke-BashPushd '{d2}'");
            var ex = Record.Exception(() => RunLines(pwsh, "Invoke-BashPopd +0"));
            Assert.Null(ex);
        }
        finally
        {
            Directory.Delete(d1);
            Directory.Delete(d2);
        }
    }

    [Fact]
    public void Popd_ViaAlias_Works()
    {
        using var pwsh = NewPwsh();
        var tmp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var subdir = Path.Combine(tmp, "psbash-popd-alias-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(subdir);
        try
        {
            RunLines(pwsh, $"Set-Location '{tmp}'");
            RunLines(pwsh, $"Invoke-BashPushd '{subdir}'");
            RunLines(pwsh, "popd");
            var cwd = RunLines(pwsh, "(Get-Location).Path");
            Assert.Equal(tmp, cwd[0]);
        }
        finally
        {
            Directory.Delete(subdir);
        }
    }

    [Fact]
    public void Popd_HelpFlag_EmitsUsage()
    {
        using var pwsh = NewPwsh();
        var lines = RunLines(pwsh, "Invoke-BashPopd --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("popd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Popd_PlusN_OnEmptyStack_DoesNotThrow()
    {
        // Pop-Location -Stack with -ErrorAction SilentlyContinue eats the
        // error per oracle parity.
        using var pwsh = NewPwsh();
        var ex = Record.Exception(() => RunLines(pwsh, "Invoke-BashPopd +0"));
        Assert.Null(ex);
    }

    [Fact]
    public void Popd_AfterMultiplePushdsThenSinglePopd_RestoresOnlyOne()
    {
        // popd (no +N) pops exactly one entry — the top of the stack.
        using var pwsh = NewPwsh();
        var tmp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var d1 = Path.Combine(tmp, "psbash-popd-3a-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var d2 = Path.Combine(tmp, "psbash-popd-3b-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(d1);
        Directory.CreateDirectory(d2);
        try
        {
            RunLines(pwsh, $"Set-Location '{tmp}'");
            RunLines(pwsh, $"Invoke-BashPushd '{d1}'");   // now at d1; stack: [tmp]
            RunLines(pwsh, $"Invoke-BashPushd '{d2}'");   // now at d2; stack: [d1, tmp]
            RunLines(pwsh, "Invoke-BashPopd");
            // After one popd we go back to d1 (top of stack at time of pop).
            // Stack count is not asserted here — see the note on
            // Popd_AfterPushd_RestoresPriorLocation for the scope caveat.
            var cwd = RunLines(pwsh, "(Get-Location).Path");
            Assert.Equal(d1, cwd[0]);
        }
        finally
        {
            Directory.Delete(d1);
            Directory.Delete(d2);
        }
    }

    [Fact]
    public void Popd_PlusNArgWithScriptblockChars_TreatedAsLiteralAndIgnored()
    {
        // Directive 12: '+0$(throw "pwn")' fails the ^\+\d+$ regex and
        // therefore falls through to the default pop-one branch — no
        // PowerShell re-parsing of the payload. pwsh.Invoke() returns
        // without an exception bearing "pwn".
        using var pwsh = NewPwsh();
        var ex = Record.Exception(() => RunLines(pwsh, "Invoke-BashPopd '+0$(throw \"pwn\")'"));
        Assert.Null(ex);
    }
}
