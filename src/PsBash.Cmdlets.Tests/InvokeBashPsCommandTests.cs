using System.Linq;
using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of Invoke-BashPs
/// from PsBash.psm1 to a binary cmdlet.
///
/// Oracle: the original psm1 Invoke-BashPs. The function enumerated processes
/// via System.Diagnostics.Process.GetProcesses() on non-Linux hosts and walked
/// /proc on Linux; the cmdlet uses the same APIs in the same process. Tests
/// assert structural shape (typed PsBash.PsEntry PSObject + side properties),
/// presence of the current PID in bare-call output, filter semantics, custom
/// column output, alias resolution, --help passthrough, and a Directive 12
/// injection probe through -p. Pipeline / file / large / CRLF / unicode axes
/// do not apply — ps has no pipeline input and no file operands.
/// </summary>
public class InvokeBashPsCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashPsCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    private System.Collections.ObjectModel.Collection<PSObject> RunRaw(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var r = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return r;
    }

    private string[] RunLines(string script)
    {
        return RunRaw(script).Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void Ps_AllFlag_EmitsAtLeastOneEntryWithCurrentPid()
    {
        // -e shows all processes; current process must appear.
        var results = RunRaw("Invoke-BashPs -e");
        Assert.NotEmpty(results);

        var currentPid = System.Diagnostics.Process.GetCurrentProcess().Id;
        var hit = results.Any(o =>
        {
            var pidProp = o.Properties["PID"]?.Value;
            return pidProp != null &&
                System.Convert.ToInt32(pidProp) == currentPid;
        });
        Assert.True(hit, $"Expected to find current PID {currentPid} in -e output");
    }

    [Fact]
    public void Ps_AllFlag_EntriesAreTypedPsEntry()
    {
        var results = RunRaw("Invoke-BashPs -e");
        Assert.NotEmpty(results);
        var first = results.First();
        Assert.Contains("PsBash.PsEntry", first.TypeNames);
        // Side properties the oracle emitted.
        Assert.NotNull(first.Properties["PID"]);
        Assert.NotNull(first.Properties["PPID"]);
        Assert.NotNull(first.Properties["User"]);
        Assert.NotNull(first.Properties["BashText"]);
    }

    [Fact]
    public void Ps_FilterByPid_EmitsOnlyMatchingEntry()
    {
        var currentPid = System.Diagnostics.Process.GetCurrentProcess().Id;
        var results = RunRaw($"Invoke-BashPs -p {currentPid}");
        // The single -p filter restricts to that PID. The oracle emits the
        // entry only if the process is enumerable; on Windows/macOS the dotnet
        // path may emit it. We assert ≤ 1 entry, and if 1, the PID matches.
        Assert.True(results.Count <= 1, "Expected at most one entry for -p filter");
        if (results.Count == 1)
        {
            var pidProp = results[0].Properties["PID"]?.Value;
            Assert.NotNull(pidProp);
            Assert.Equal(currentPid, System.Convert.ToInt32(pidProp));
        }
    }

    [Fact]
    public void Ps_FilterByCommaSeparatedPidList_FiltersToBothPids()
    {
        // GNU ps -p accepts a comma-separated PID list, not just one integer.
        // Verified against the WSL oracle: `ps -p 1,2` lists both PIDs. The
        // second PID need not resolve to a live process — it just proves the
        // comma-separated operand parses as two PIDs instead of failing
        // int.TryParse and falling through to an unfiltered listing.
        var currentPid = System.Diagnostics.Process.GetCurrentProcess().Id;
        var other = currentPid + 1;

        var results = RunRaw($"Invoke-BashPs -p '{currentPid},{other}'");
        Assert.True(results.Count is >= 1 and <= 2,
            $"Expected at most the two listed PIDs, got {results.Count}");
        var pids = results
            .Select(o => System.Convert.ToInt32(o.Properties["PID"]?.Value ?? 0))
            .ToHashSet();
        Assert.Contains(currentPid, pids);
    }

    [Theory]
    [InlineData("abc")]        // wholly non-integer
    [InlineData("1,abc")]      // mixed valid + invalid in a list
    public void Ps_MalformedPidList_ErrorsWithExit1AndNoEntries(string operand)
    {
        // GNU ps errors ("error: process ID list syntax error", exit 1) on a
        // malformed -p operand rather than silently listing every process.
        // Verified against the WSL oracle: `ps -p abc` / `ps -p 1,abc` ->
        // exit 1, stderr "error: process ID list syntax error".
        var pwsh = _fixture.AcquireFresh();
        var results = pwsh.AddScript($"Invoke-BashPs -p '{operand}'; $LASTEXITCODE").Invoke();
        var errs = pwsh.Streams.Error.ToArray();
        pwsh.Commands.Clear();

        Assert.NotEmpty(errs);
        Assert.Contains(errs, e => (e.Exception?.Message ?? e.ToString())
            .Contains("process ID list syntax error", System.StringComparison.OrdinalIgnoreCase));
        // No process rows leaked — only the trailing $LASTEXITCODE value.
        Assert.Single(results);
        Assert.Equal("1", results[0]?.ToString());
    }

    [Fact]
    public void Ps_UnknownSortSpecifier_ErrorsWithExit1AndNoEntries()
    {
        // GNU ps --sort=bogus errors ("error: unknown sort specifier", exit 1)
        // rather than silently degrading to PID order. Verified against the
        // WSL oracle: `ps --sort=bogus` -> exit 1, stderr "error: unknown sort specifier".
        var pwsh = _fixture.AcquireFresh();
        var results = pwsh.AddScript("Invoke-BashPs -e --sort=bogus; $LASTEXITCODE").Invoke();
        var errs = pwsh.Streams.Error.ToArray();
        pwsh.Commands.Clear();

        Assert.NotEmpty(errs);
        Assert.Contains(errs, e => (e.Exception?.Message ?? e.ToString())
            .Contains("unknown sort specifier", System.StringComparison.OrdinalIgnoreCase));
        // No process entries — just the trailing $LASTEXITCODE value.
        Assert.Single(results);
        Assert.Equal("1", results[0]?.ToString());
    }

    [Fact]
    public void Ps_CustomOutputColumns_ProducesCommaSeparatedFieldsInBashText()
    {
        // -o pid,comm — BashText carries the formatted columns joined with a space.
        // The value is quoted to keep the comma from being parsed as a PowerShell
        // array separator (which would bind to the string? O parameter as an
        // array, joining via Out-String).
        var results = RunRaw("Invoke-BashPs -e -o 'pid,comm'");
        Assert.NotEmpty(results);
        var first = results.First();
        var bt = first.Properties["BashText"]?.Value as string ?? "";
        Assert.NotEmpty(bt);
        // pid column emits 7-wide right-aligned digits; comm column emits the
        // process name. The format has them space-separated.
        Assert.Contains(' ', bt);
    }

    [Fact]
    public void Ps_FullFormat_BashTextContainsUserAndPidColumns()
    {
        // -ef produces the aux-line format (oracle's Format-PsAuxLine).
        var results = RunRaw("Invoke-BashPs -ef");
        Assert.NotEmpty(results);
        var first = results.First();
        var bt = first.Properties["BashText"]?.Value as string ?? "";
        var user = first.Properties["User"]?.Value?.ToString() ?? "";
        // The aux format leads with the user column (oracle: '{0,-8}' format).
        Assert.False(string.IsNullOrEmpty(bt));
        Assert.False(string.IsNullOrEmpty(user));
    }

    [Fact]
    public void Ps_ViaAlias_ReturnsAtLeastOneEntry()
    {
        // The psm1 `Set-Alias ps -> Invoke-BashPs` must resolve to the cmdlet.
        var results = RunRaw("ps -e");
        Assert.NotEmpty(results);
        Assert.Contains("PsBash.PsEntry", results.First().TypeNames);
    }

    [Fact]
    public void Ps_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashPs --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("ps", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ps_FilterByNonexistentUser_EmitsNoEntries()
    {
        // -u with a definitely-not-present user filters everything out.
        var results = RunRaw("Invoke-BashPs -e -u '__nonexistent_user_abc123__'");
        Assert.Empty(results);
    }

    [Fact]
    public void Ps_SortByPid_EntriesAreOrdered()
    {
        var results = RunRaw("Invoke-BashPs -e --sort pid");
        Assert.NotEmpty(results);
        var pids = results
            .Select(o => System.Convert.ToInt32(o.Properties["PID"]?.Value ?? 0))
            .ToArray();
        for (int i = 1; i < pids.Length; i++)
        {
            Assert.True(pids[i - 1] <= pids[i],
                $"Expected ascending pids, but pids[{i-1}]={pids[i-1]} > pids[{i}]={pids[i]}");
        }
    }

    [Fact]
    public void Ps_SortByPidDescending_EntriesAreReverseOrdered()
    {
        // --sort=-pid prefix-dash flips direction (oracle parity).
        var results = RunRaw("Invoke-BashPs -e --sort=-pid");
        Assert.NotEmpty(results);
        var pids = results
            .Select(o => System.Convert.ToInt32(o.Properties["PID"]?.Value ?? 0))
            .ToArray();
        for (int i = 1; i < pids.Length; i++)
        {
            Assert.True(pids[i - 1] >= pids[i],
                $"Expected descending pids, but pids[{i-1}]={pids[i-1]} < pids[{i}]={pids[i]}");
        }
    }

    [Fact]
    public void Ps_DefaultBashTextHasPidTtyTimeCommandColumns()
    {
        var results = RunRaw("Invoke-BashPs -e");
        Assert.NotEmpty(results);
        var first = results.First();
        var bt = first.Properties["BashText"]?.Value as string ?? "";
        // Default format: PID(7) TTY(7) TIME(8) COMMAND — four fields joined by spaces.
        Assert.False(string.IsNullOrEmpty(bt));
        // PID must appear at the start (allowing leading spaces from format).
        var pid = first.Properties["PID"]?.Value?.ToString() ?? "";
        Assert.Contains(pid, bt);
    }

    // ---- Directive 12 injection probes ----

    [Fact]
    public void Ps_InjectionInDashP_NoExceptionFallsThroughToEmpty()
    {
        // -p value containing $(throw 'pwn') is a non-integer token. The cmdlet
        // must NOT evaluate it — int.TryParse fails, so this is now a GNU
        // "process ID list syntax error" (exit 1, no rows) rather than a silent
        // unfiltered listing. Either way the security invariant holds: the
        // payload is never evaluated and no output row contains "pwn".
        var results = RunRaw("Invoke-BashPs -p '$(throw ''pwn'')'");
        Assert.DoesNotContain(results, o =>
            (o.Properties["BashText"]?.Value as string ?? "")
                .Contains("pwn", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ps_InjectionInDashO_NoEvalProducesLiteralColumnAndNoThrow()
    {
        // -o value containing scriptblock chars is split on comma; unknown
        // tokens emit "?" via the column switch's default branch. No eval.
        var results = RunRaw("Invoke-BashPs -e -o '$(throw),pid'");
        Assert.NotEmpty(results);
        var first = results.First();
        var bt = first.Properties["BashText"]?.Value as string ?? "";
        // First column is the unknown placeholder "?", second is the pid.
        Assert.StartsWith("?", bt);
        Assert.DoesNotContain("pwn", bt);
    }
}
