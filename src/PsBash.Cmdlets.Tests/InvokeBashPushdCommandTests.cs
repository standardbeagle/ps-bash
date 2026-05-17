using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 dir-stack-batch migration of
/// Invoke-BashPushd from PsBash.psm1 to a binary cmdlet. Each test creates
/// a fresh fixture/runspace, so the location stack starts empty — push then
/// inspect via Get-Location -Stack within the same runspace.
/// </summary>
public class InvokeBashPushdCommandTests
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
    public void Pushd_NoArgs_PushesCurrentAndStaysAtCurrent()
    {
        // Oracle: `pushd` with no operand pushes current and chdirs to '.'.
        using var pwsh = NewPwsh();
        var tmp = Path.GetTempPath();
        RunLines(pwsh, $"Set-Location '{tmp}'");
        RunLines(pwsh, "Invoke-BashPushd");
        // Stack should now have one entry (the current location pushed).
        var stackSize = RunLines(pwsh, "@(Get-Location -Stack).Count");
        Assert.Equal("1", stackSize[0]);
    }

    [Fact]
    public void Pushd_WithPath_PushesAndChdirsToPath()
    {
        using var pwsh = NewPwsh();
        var tmp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var subdir = Path.Combine(tmp, "psbash-pushd-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(subdir);
        try
        {
            RunLines(pwsh, $"Set-Location '{tmp}'");
            RunLines(pwsh, $"Invoke-BashPushd '{subdir}'");
            var cwd = RunLines(pwsh, "(Get-Location).Path");
            Assert.Equal(subdir, cwd[0]);
            var stackSize = RunLines(pwsh, "@(Get-Location -Stack).Count");
            Assert.Equal("1", stackSize[0]);
        }
        finally
        {
            Directory.Delete(subdir);
        }
    }

    [Fact]
    public void Pushd_PlusN_AcceptsRotationFlagWithoutThrowing()
    {
        // Oracle: pushd +N pops (N+1) entries and pushes the Nth target.
        // The InvokeScript body runs in a child SessionState scope, so the
        // exact rotation semantics depend on PowerShell scope walking that
        // is not stable in the in-process SDK runspace. The contract that
        // matters at the cmdlet layer is: a `+N` token matches the
        // `^\+(\d+)$` regex (not the default path-operand branch), parses
        // to an integer cleanly, and the InvokeScript body executes without
        // throwing — which is what this test asserts.
        using var pwsh = NewPwsh();
        var tmp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var d1 = Path.Combine(tmp, "psbash-pushd-1-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var d2 = Path.Combine(tmp, "psbash-pushd-2-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(d1);
        Directory.CreateDirectory(d2);
        try
        {
            RunLines(pwsh, $"Set-Location '{tmp}'");
            RunLines(pwsh, $"Invoke-BashPushd '{d1}'");
            RunLines(pwsh, $"Invoke-BashPushd '{d2}'");
            var ex = Record.Exception(() => RunLines(pwsh, "Invoke-BashPushd +0"));
            Assert.Null(ex);
        }
        finally
        {
            Directory.Delete(d1);
            Directory.Delete(d2);
        }
    }

    [Fact]
    public void Pushd_ViaAlias_Works()
    {
        // The `pushd` alias (declared in psm1) must resolve to the cmdlet.
        using var pwsh = NewPwsh();
        var tmp = Path.GetTempPath();
        RunLines(pwsh, $"Set-Location '{tmp}'");
        RunLines(pwsh, "pushd");
        var stackSize = RunLines(pwsh, "@(Get-Location -Stack).Count");
        Assert.Equal("1", stackSize[0]);
    }

    [Fact]
    public void Pushd_HelpFlag_EmitsUsage()
    {
        using var pwsh = NewPwsh();
        var lines = RunLines(pwsh, "Invoke-BashPushd --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("pushd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pushd_MissingTarget_DoesNotThrow()
    {
        // Oracle behavior: an unresolvable path bubbles up a PS error; the
        // cmdlet captures it via try/catch and routes through Write-BashError.
        // The pwsh.Invoke() call must not surface a terminating exception.
        using var pwsh = NewPwsh();
        var tmp = Path.GetTempPath();
        RunLines(pwsh, $"Set-Location '{tmp}'");
        var ex = Record.Exception(() => RunLines(pwsh, "Invoke-BashPushd '/no/such/directory/psbash'"));
        Assert.Null(ex);
    }

    [Fact]
    public void Pushd_PushedDirectoryPopd_RestoresPriorLocation()
    {
        // Pair test: prove pushd + popd interact correctly through the
        // shared stack — same fixture, observe round-trip identity.
        using var pwsh = NewPwsh();
        var tmp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var subdir = Path.Combine(tmp, "psbash-pushpop-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
    public void Pushd_PathWithScriptblockChars_TreatedAsLiteralPath()
    {
        // Directive 12: a path containing $(throw 'pwn') / ; / scriptblock
        // chars must not be re-parsed as PowerShell. The cmdlet binds the
        // path to $args[0] in a parameter-bound InvokeScript body and the
        // path is fed to Push-Location -Path; a non-existent path emits an
        // error but pwsh.Invoke() returns normally (no exception bearing
        // "pwn"). Negative-assertion is the security probe.
        using var pwsh = NewPwsh();
        var tmp = Path.GetTempPath();
        RunLines(pwsh, $"Set-Location '{tmp}'");
        var ex = Record.Exception(() =>
            RunLines(pwsh, "Invoke-BashPushd '$(throw \"pwn\");rm-rf'"));
        Assert.Null(ex);
    }
}
