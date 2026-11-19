using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Regression tests for the psm1 <c>Invoke-BashKill</c>.
///
/// HEADLINE REGRESSION (Directive 13): the function looped with
/// <c>foreach ($pid in $pids)</c>, but <c>$PID</c> is a read-only automatic
/// variable — every invocation threw "Cannot overwrite variable PID because it
/// is read-only or constant" before reaching <c>Stop-Process</c>, so kill never
/// killed anything. The loop variable is now <c>$procId</c>.
/// <see cref="Kill_RealProcess_ActuallyTerminatesIt"/> is the end-to-end guard:
/// it spawns a real OS process and asserts the process is gone afterward — the
/// review found kill had effectively zero behavioral coverage, which is how a
/// 100%-reproducible crash survived in-tree.
/// </summary>
public class InvokeBashKillCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashKillCommandTests(SharedPwshFixture fixture) => _fixture = fixture;

    private void Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
    }

    private string[] RunBashText(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result
            .Select(o =>
            {
                var prop = o?.Properties["BashText"];
                return (prop != null ? prop.Value?.ToString() ?? "" : o?.ToString() ?? "")
                    .TrimEnd('\n', '\r');
            })
            .ToArray();
    }

    private string[] RunErrors(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        pwsh.AddScript(script).Invoke();
        var errs = pwsh.Streams.Error.Select(e => e.Exception?.Message ?? e.ToString()).ToArray();
        pwsh.Commands.Clear();
        return errs;
    }

    // Oracle: `kill -9999 $pid` prints "invalid signal specification", exits 1 and signals nothing.
    [Fact]
    public void Kill_SignalNumberOutOfRange_IsRejectedAndSignalsNothing()
    {
        var errs = RunErrors("$p = Start-Process -PassThru -WindowStyle Hidden pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 5'; try { Invoke-BashKill -9999 $p.Id; \"rc=$global:LASTEXITCODE alive=$(-not $p.HasExited)\" | Out-Null; $global:__k = \"rc=$global:LASTEXITCODE alive=$(-not $p.HasExited)\" } finally { Stop-Process -Id $p.Id -Force -ErrorAction Ignore }");
        Assert.Contains(errs, e => e.Contains("invalid signal specification"));
    }

    [Fact]
    public void Kill_SDashUnknownSignal_IsRejected_AndListNamesAndNumbersRoundTrip()
    {
        var errs = RunErrors("Invoke-BashKill '-s' 9999 ([System.Environment]::ProcessId); Invoke-BashKill '-s' FOO ([System.Environment]::ProcessId)");
        Assert.Equal(2, errs.Count(e => e.Contains("invalid signal specification")));

        // kill -l: 128+N prints the name N has, 0 is EXIT, a name prints its number, 128 itself is invalid.
        var lines = RunBashText("Invoke-BashKill '-l' 134 0 SIGTERM");
        Assert.Equal(new[] { "ABRT", "EXIT", "15" }, lines);
        Assert.Contains(RunErrors("Invoke-BashKill '-l' 128"), e => e.Contains("128: invalid signal specification"));
    }

    // Oracle (bash 5.2, Linux): `kill -l` rows; a real-time signal has a number (`kill -34` once fell back to 15, TERM).
    [Fact]
    public void Kill_SignalTable_IsBashsAndHasTheRealTimeSignals()
    {
        var list = InvokeBashKillCommand.SignalList().Split('\n');
        Assert.Equal(" 1) SIGHUP\t 2) SIGINT\t 3) SIGQUIT\t 4) SIGILL\t 5) SIGTRAP", list[0]);
        Assert.Equal("31) SIGSYS\t34) SIGRTMIN\t35) SIGRTMIN+1\t36) SIGRTMIN+2\t37) SIGRTMIN+3", list[6]);
        Assert.Equal("63) SIGRTMAX-1\t64) SIGRTMAX\t", list[12]);
        Assert.Equal(new[] { 34, 35, 63, 64, 15 },
            new[] { "SIGRTMIN", "SIGRTMIN+1", "SIGRTMAX-1", "SIGRTMAX", "SIGTERM" }.Select(InvokeBashKillCommand.SignalNumber));
    }
    // Oracle: `trap 'echo got' USR1; kill -USR1 $$; echo after` prints got then after - the trap runs and the script
    // carries on (it ended the script with status 138). `trap '' USR2` ignores the signal.
    [Fact]
    public void Kill_SelfWithTrap_RunsTheTrapAndContinues()
    {
        var lines = RunBashText(
            "$global:BashTrapHandlers = @{}; Invoke-BashTrap 'Write-Output ran-trap' USR1; Invoke-BashKill -USR1 ([System.Environment]::ProcessId); 'after'");

        Assert.Equal(new[] { "ran-trap", "after" }, lines);
    }

    [Fact]
    public void Kill_SelfWithIgnoredSignal_DoesNothing()
    {
        var lines = RunBashText(
            "$global:BashTrapHandlers = @{}; Invoke-BashTrap '' USR2; Invoke-BashKill -USR2 ([System.Environment]::ProcessId); 'after'");

        Assert.Equal(new[] { "after" }, lines);
    }

    [SkippableFact]
    public void Kill_RealProcess_ActuallyTerminatesIt()
    {
        using var proc = StartLongLivedProcess();
        Skip.If(proc is null, "no spawnable long-lived process host (pwsh/powershell) available");
        var pid = proc!.Id;
        Assert.False(proc.HasExited, "precondition: spawned process must be running");

        Run($"Invoke-BashKill {pid}");

        Assert.True(proc.WaitForExit(10_000),
            "Invoke-BashKill <pid> must terminate the process (regression: $PID loop-var crash)");
    }

    [SkippableFact]
    public void Kill_NamedSignalKILL_TerminatesProcess()
    {
        using var proc = StartLongLivedProcess();
        Skip.If(proc is null, "no spawnable process host available");
        var pid = proc!.Id;

        // -KILL used to fall through int::TryParse and be silently dropped; the
        // signal token now routes to Stop-Process -Force.
        Run($"Invoke-BashKill -KILL {pid}");

        Assert.True(proc.WaitForExit(10_000), "kill -KILL <pid> must terminate the process");
    }

    [SkippableFact]
    public void Kill_NumericSignal9_TerminatesProcess()
    {
        using var proc = StartLongLivedProcess();
        Skip.If(proc is null, "no spawnable process host available");
        var pid = proc!.Id;

        Run($"Invoke-BashKill -9 {pid}");

        Assert.True(proc.WaitForExit(10_000), "kill -9 <pid> must terminate the process");
    }

    [SkippableFact]
    public void Kill_Signal0_DoesNotTerminate_ExistenceProbeOnly()
    {
        using var proc = StartLongLivedProcess();
        Skip.If(proc is null, "no spawnable process host available");
        var pid = proc!.Id;

        // REGRESSION: `kill -0` mapped to SIG0 and fell through to Stop-Process,
        // KILLING the process. signal 0 is an existence probe that must NOT kill.
        Run($"Invoke-BashKill -0 {pid}");

        Assert.False(proc.HasExited, "kill -0 <pid> must NOT terminate the process");
        proc.Kill(); // cleanup
    }

    [SkippableTheory]
    [InlineData("-URG")]
    [InlineData("-WINCH")]
    [InlineData("-s CHLD")]
    [InlineData("-23")]
    public void Kill_DefaultIgnoredSignal_DoesNotTerminate(string signal)
    {
        using var proc = StartLongLivedProcess();
        Skip.If(proc is null, "no spawnable process host available");
        // REGRESSION: an unlisted name resolved to TERM, so `kill -URG $!` killed the job. bash: ignored by default.
        Run($"Invoke-BashKill {signal} {proc!.Id}");
        Assert.False(proc.HasExited, $"kill {signal} <pid> must NOT terminate the process");
        proc.Kill();
    }

    // Oracle (bash 5.2): every spec form is validated before anything is signalled — `kill -FOO $pid`,
    // `kill -SIGFOO`, `kill -s FOO`, `kill -n 99`, `kill -65` print `kill: SPEC: invalid signal specification`
    // and return 1. REGRESSION: `-NAME` and `--signal=NAME` took any name and fell back to TERM.
    // (bash has no `--signal`; ps-bash accepts it like procps kill and reports the value the same way.)
    [Theory]
    [InlineData("'-FOO'", "FOO")]
    [InlineData("'-SIGFOO'", "SIGFOO")]
    [InlineData("'--signal=FOO'", "FOO")]
    [InlineData("'--signal' FOO", "FOO")]
    [InlineData("'-s' FOO", "FOO")]
    [InlineData("'-n' 99", "99")]
    [InlineData("'-65'", "65")]
    [InlineData("'-s' RTMIN+31", "RTMIN+31")]
    public void Kill_InvalidSignalSpec_EveryForm_RejectedExit1(string spec, string shown)
    {
        var errs = RunErrors($"Invoke-BashKill {spec} 2147483646; $global:__kr = $global:LASTEXITCODE");
        Assert.Contains(errs, e => e.Contains($"kill: {shown}: invalid signal specification"));
        Assert.DoesNotContain(errs, e => e.Contains("No such process"));
        Assert.Equal(new[] { "1" }, RunBashText("\"$global:__kr\""));
    }

    // Oracle: names are case-insensitive with or without SIG (`-term`, `-SIGterm`, `-s rtmax-1`); a valid spec reaches
    // the target, so a missing pid reports `No such process`, never an invalid spec.
    [Theory]
    [InlineData("'-term'")]
    [InlineData("'-SIGterm'")]
    [InlineData("'-RTMIN+2'")]
    [InlineData("'-RTMAX-3'")]
    [InlineData("'-SIGRTMIN+1'")]
    [InlineData("'--signal=KILL'")]
    [InlineData("'-s' rtmax-1")]
    [InlineData("'-n' 64")]
    public void Kill_ValidSignalSpec_ReachesTarget(string spec)
    {
        var errs = RunErrors($"Invoke-BashKill {spec} 2147483646");
        Assert.DoesNotContain(errs, e => e.Contains("invalid signal specification"));
        // REGRESSION: `-RTMIN+2` was taken for a pid word ("arguments must be process or job IDs").
        Assert.DoesNotContain(errs, e => e.Contains("arguments must be process or job IDs"));
        Assert.Contains(errs, e => e.Contains("No such process", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("TERM", "SIGTERM")]
    [InlineData("sigterm", "SIGTERM")]
    [InlineData("0", "SIG0")]
    [InlineData("64", "SIGRTMAX")]
    [InlineData("RTMIN+15", "SIGRTMIN+15")]
    [InlineData("rtmax-14", "SIGRTMAX-14")]
    [InlineData("FOO", null)]
    [InlineData("65", null)]
    [InlineData("RTMIN+16", "SIGRTMAX-14")]
    [InlineData("RTMIN+31", null)]
    [InlineData("", null)]
    public void TryResolveSignalSpec_ValidatesEveryName(string spec, string? expected)
    {
        bool ok = InvokeBashKillCommand.TryResolveSignalSpec(spec, out var name);
        Assert.Equal(expected != null, ok);
        if (expected != null) Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("SIGURG", 23)]
    [InlineData("SIGILL", 4)]
    [InlineData("SIGKILL", 9)]
    [InlineData("SIGNOPE", 15)]
    public void SignalNumber_ResolvesEveryLinuxName(string name, int number)
        => Assert.Equal(number, InvokeBashKillCommand.SignalNumber(name));
    [Fact]
    public void Kill_Signal0_NonexistentPid_EmitsNoSuchProcess()
    {
        var errs = RunErrors("Invoke-BashKill -0 2147483646");
        Assert.Contains(errs, m => m.Contains("No such process", StringComparison.OrdinalIgnoreCase));
    }

    // REGRESSION: `kill -PIPE PID` printed "kill: usage": -PIPE prefix-matched the -PipelineVariable common parameter,
    // which took the pid as a variable name. Direct call shape (Pester); the transpiled shape is single-quoted
    // (PsEmitter.OrderedArgCommands) and covered by BackgroundJobSelfSignalDifferentialTests (PIPE).
    [Fact]
    public void Kill_PipeSignalWord_DirectCall_ReachesTheSignalNotPipelineVariable()
    {
        var errs = RunErrors("Invoke-BashKill -PIPE 2147483646");
        Assert.Contains(errs, m => m.Contains("No such process", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(errs, m => m.Contains("usage", StringComparison.OrdinalIgnoreCase));
    }

    // Guard for the class: every signal word kill accepts as `-NAME` that a common parameter's name starts with
    // (the binder's prefix match) has an exact-name decoy parameter, so a direct call cannot lose it to the binder.
    [Fact]
    public void Kill_EverySignalWordThatPrefixesACommonParameter_HasADecoy()
    {
        var names = (string?[])typeof(InvokeBashKillCommand)
            .GetField("LinuxSignalNames", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;
        var common = typeof(System.Management.Automation.Internal.CommonParameters).GetProperties().Select(p => p.Name).ToArray();
        Assert.Contains("PipelineVariable", common);
        var missing = new List<string>();
        foreach (string? word in names)
        {
            if (word is null) continue;
            if (!common.Any(c => c.StartsWith(word, StringComparison.OrdinalIgnoreCase))) continue;
            var prop = typeof(InvokeBashKillCommand).GetProperty(word);
            if (prop is null || !prop.IsDefined(typeof(System.Management.Automation.ParameterAttribute), false)) missing.Add(word);
        }
        Assert.True(missing.Count == 0, "signal words the binder would take as a common parameter, without a decoy: " + string.Join(", ", missing));
    }

    [Fact]
    public void Kill_NoArguments_EmitsUsageError()
    {
        var errs = RunErrors("Invoke-BashKill");
        Assert.Contains(errs, m => m.Contains("usage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Kill_NonexistentPid_EmitsNoSuchProcess()
    {
        // A PID that is overwhelmingly unlikely to exist.
        var errs = RunErrors("Invoke-BashKill 2147483646");
        Assert.Contains(errs, m => m.Contains("No such process", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Kill_ListSignals_IncludesCommonNames()
    {
        var lines = RunBashText("Invoke-BashKill -l");
        var all = string.Join("\n", lines);
        Assert.Contains("SIGKILL", all, StringComparison.Ordinal);
        Assert.Contains("SIGTERM", all, StringComparison.Ordinal);
    }

    /// <summary>
    /// Spawn a process that stays alive for ~60s and is cleanly killable.
    /// Uses pwsh/powershell (guaranteed present where these PowerShell-hosting
    /// tests run). Returns null if none can be started, so callers Skip.
    /// </summary>
    private static Process? StartLongLivedProcess()
    {
        foreach (var exe in new[] { "pwsh", "powershell" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8,
                    RedirectStandardError = true, StandardErrorEncoding = System.Text.Encoding.UTF8,
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add("Start-Sleep -Seconds 60");
                var p = Process.Start(psi);
                if (p is not null && !p.HasExited)
                    return p;
                p?.Dispose();
            }
            catch (Win32Exception) { /* exe not found — try next */ }
            catch (PlatformNotSupportedException) { }
        }
        return null;
    }
}
