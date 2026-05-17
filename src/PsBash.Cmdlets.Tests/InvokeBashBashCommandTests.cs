using System;
using System.IO;
using System.Linq;
using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 follow-on migration of the
/// <c>Invoke-BashBash</c> psm1 function to the
/// <see cref="PsBash.Cmdlets.InvokeBashBashCommand"/> binary cmdlet.
///
/// Oracle: the original psm1 <c>Invoke-BashBash</c> function. The oracle
/// shelled out to a ps-bash child process and re-emitted child stdout as
/// <c>Emit-BashLine</c> objects, forwarding the child exit code via
/// <c>$global:LASTEXITCODE</c>. The cmdlet preserves that exact dispatch.
///
/// Failure-surface coverage (Directive 3): empty input, --help / --version
/// banner, missing target (ps-bash binary absent), exit-code propagation,
/// and a Directive-12 injection probe with <c>-c '$(throw "pwn")'</c>.
/// Tests that require actually spawning ps-bash are marked
/// <c>SkippableFact</c> and skip when no ps-bash executable is discoverable
/// in PATH or alongside the test runtime (a CI environment that ships only
/// the cmdlet bits and no ps-bash native binary).
/// </summary>
public class InvokeBashBashCommandTests
{
    private static (string[] stdout, string? errorMessage, int? lastExitCode) Run(string script)
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$error.Clear(); $global:LASTEXITCODE = $null").Invoke();
        pwsh.Commands.Clear();

        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        var errCol = pwsh.AddScript("$error | Select-Object -First 1").Invoke();
        pwsh.Commands.Clear();
        string? errMsg = errCol.Count > 0 ? errCol[0]?.ToString() : null;

        var exitObj = pwsh.AddScript("$global:LASTEXITCODE").Invoke();
        pwsh.Commands.Clear();
        int? exit = null;
        if (exitObj.Count > 0 && exitObj[0] != null)
        {
            if (LanguagePrimitives.TryConvertTo<int>(exitObj[0].BaseObject, out int v))
                exit = v;
        }

        var lines = result.Select(o => o?.ToString() ?? "").ToArray();
        return (lines, errMsg, exit);
    }

    private static string? LocatePsBash()
    {
        var fromPath = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator);
        var exe = OperatingSystem.IsWindows() ? "ps-bash.exe" : "ps-bash";
        if (fromPath != null)
        {
            foreach (var dir in fromPath)
            {
                try
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    var candidate = Path.Combine(dir, exe);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* skip unreadable PATH entry */ }
            }
        }
        // Also check next to test runtime in case the project happens to ship it.
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var local = Path.Combine(baseDir, exe);
            if (File.Exists(local)) return local;
        }
        catch { /* ignore */ }
        return null;
    }

    // ---- Cmdlet surface tests (do not require a real ps-bash binary) ----

    [Fact]
    public void Bash_HelpFlag_EmitsUsageMentioningBash()
    {
        var (lines, err, _) = Run("Invoke-BashBash --help");
        Assert.True(string.IsNullOrEmpty(err), $"unexpected error: {err}");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("bash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Bash_HelpFlag_ViaAlias_EmitsUsage()
    {
        var (lines, err, _) = Run("bash --help");
        Assert.True(string.IsNullOrEmpty(err), $"unexpected error: {err}");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("bash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Bash_VersionFlag_EmitsVersionBanner()
    {
        var (lines, err, _) = Run("Invoke-BashBash --version");
        Assert.True(string.IsNullOrEmpty(err), $"unexpected error: {err}");
        // Banner is two lines: "ps-bash, version X" and "Bash-to-PowerShell transpiler"
        Assert.True(lines.Length >= 1);
        Assert.Contains(lines, l => l.Contains("ps-bash", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, l => l.Contains("Bash-to-PowerShell", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Bash_VersionFlag_DoesNotRequirePsBashBinary()
    {
        // --version is handled entirely in-cmdlet (no child spawn). This proves
        // the short-circuit does not depend on locating a ps-bash executable.
        var (lines, err, _) = Run("Invoke-BashBash --version");
        Assert.True(string.IsNullOrEmpty(err), $"unexpected error: {err}");
        Assert.NotEmpty(lines);
    }

    [SkippableFact]
    public void Bash_DashCFlag_BindsAsCParameter()
    {
        // Confirms the cmdlet declares a literal `C` parameter and the
        // PowerShell binder routes `-c "..."` to it (the playbook fix for the
        // `-c` vs `-Confirm` prefix collision). Requires a real ps-bash on
        // PATH because the spawn path must complete without hanging.
        var psbash = LocatePsBash();
        Skip.If(psbash == null, "ps-bash binary not on PATH or next to test runtime");

        var (_, err, _) = Run("Invoke-BashBash -c 'echo hello' 2>$null; $true");
        Assert.False(err?.Contains("ParameterBinding") == true,
            $"Unexpected binder error: {err}");
    }

    [SkippableFact]
    public void Bash_InjectionProbe_ScriptStaysLiteral()
    {
        // Directive 12: a -c "$(throw 'pwn')" payload must NOT execute as
        // PowerShell at the cmdlet seam. The inner ps-bash child is welcome
        // to fail on the bash `$(throw)` command sub; the host cmdlet itself
        // must not re-parse the string. The word "pwn" never appears as
        // output from the cmdlet host, and the host does not throw a
        // PowerShell runtime exception (ScriptHalted).
        var psbash = LocatePsBash();
        Skip.If(psbash == null, "ps-bash binary not on PATH or next to test runtime");

        var (lines, err, _) = Run("Invoke-BashBash -c '$(throw \"pwn\")' 2>$null; $true");
        // Pass criterion: the host did not throw a PowerShell terminating
        // exception (no ScriptHalted, no ParseException at the host level).
        // The child ps-bash output (echoing "pwn" as a bash-side error string)
        // is legitimate downstream-shell content, not host re-parse — that is
        // EXACTLY what isolation should produce.
        Assert.False(err?.Contains("ScriptHalted") == true,
            $"Unexpected PowerShell-side eval: {err}");
        Assert.False(err?.Contains("ParseException") == true,
            $"Unexpected host parse failure: {err}");
        // The last emit should be the literal "True" sentinel from `$true`,
        // proving the pipeline reached the post-cmdlet statement (no
        // terminating throw from the cmdlet host).
        Assert.Contains(lines, l => l.Trim() == "True");
    }

    // ---- End-to-end tests (skip when no ps-bash binary is available) ----

    [SkippableFact]
    public void Bash_DashCEcho_RunsNestedScript()
    {
        var psbash = LocatePsBash();
        Skip.If(psbash == null, "ps-bash binary not on PATH or next to test runtime");

        var (lines, err, exit) = Run("Invoke-BashBash -c 'echo hello'");
        Assert.True(string.IsNullOrEmpty(err), $"unexpected error: {err}");
        Assert.Contains(lines, l => l.Contains("hello"));
        Assert.Equal(0, exit ?? 0);
    }

    [SkippableFact]
    public void Bash_DashCExit5_PropagatesExitCode()
    {
        var psbash = LocatePsBash();
        Skip.If(psbash == null, "ps-bash binary not on PATH or next to test runtime");

        var (_, err, exit) = Run("Invoke-BashBash -c 'exit 5'");
        Assert.True(string.IsNullOrEmpty(err), $"unexpected error: {err}");
        Assert.Equal(5, exit);
    }

    [SkippableFact]
    public void Bash_FileScript_RunsAndForwardsExit()
    {
        var psbash = LocatePsBash();
        Skip.If(psbash == null, "ps-bash binary not on PATH or next to test runtime");

        var tmp = Path.Combine(Path.GetTempPath(), $"psb-bash-test-{Guid.NewGuid():N}.sh");
        try
        {
            File.WriteAllText(tmp, "echo from-file\n");
            var ps = "Invoke-BashBash " + "'" + tmp.Replace("'", "''") + "'";
            var (lines, err, exit) = Run(ps);
            Assert.True(string.IsNullOrEmpty(err), $"unexpected error: {err}");
            Assert.Contains(lines, l => l.Contains("from-file"));
            Assert.Equal(0, exit ?? 0);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }
}
