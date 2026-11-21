using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// What a background job inherits besides variables: the parent's <c>set</c> options, each written on or off (an
/// in-process job's pooled runspace keeps the previous job's), and the ERR trap only under errtrace (a job is a
/// subshell). ps-bash-specific plumbing; the bash behaviour is pinned in <c>ErrtraceDifferentialTests</c>.
/// </summary>
public class BashStateSnapshotJobOptionsTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public BashStateSnapshotJobOptionsTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private const string Reset =
        "$global:__BashErrexit = $false; $global:__BashErrtrace = $false; $global:__BashNounset = $false; " +
        "$global:__BashPipefail = $false; $global:__BashNoclobber = $false; $global:__BashNoglob = $false; $global:__BashTrapERR = $null; ";

    // The job's globals as `name = value` lines (a script block as its text in {}).
    private IReadOnlyList<string> Options(string setup) =>
        CmdResult.Run(_fixture.AcquireFresh(),
            Reset + setup + "; $__g = [PsBash.Cmdlets.BashStateSnapshot]::JobGlobals($ExecutionContext.SessionState); " +
            "foreach ($__k in $__g.Keys) { $__v = $__g[$__k]; if ($null -eq $__v) { \"$__k = null\" } elseif ($__v -is [scriptblock]) { \"$__k = {$__v}\" } else { \"$__k = $__v\" } }")
            .AssertSuccess().Lines.ToList();

    [Fact]
    public void JobOptions_WriteEveryOptionOnOrOff()
    {
        var lines = Options("$global:__BashErrexit = $true; $global:__BashNounset = $true");

        Assert.Contains("__BashErrexit = True", lines);
        Assert.Contains("__BashNounset = True", lines);
        Assert.Contains("__BashPipefail = False", lines);
        Assert.Contains("__BashErrtrace = False", lines);
        Assert.Contains("__BashErrexitSuppress = 0", lines);
    }

    [Fact]
    public void JobOptions_ErrTrapOnlyUnderErrtrace()
    {
        Assert.Contains("__BashTrapERR = null",
            Options("$global:__BashTrapERR = [scriptblock]::Create('echo T')"));

        var withErrtrace = Options("$global:__BashErrtrace = $true; $global:__BashTrapERR = [scriptblock]::Create(\"echo 'it''s'\")");
        Assert.Contains("__BashTrapERR = {echo 'it''s'}", withErrtrace);
    }

    /// <summary>The trap is rebuilt from its text, unbound: it runs in the job runspace it is handed to, not the parent's.</summary>
    [Fact]
    public void JobOptions_RebuildTheTrapUnbound()
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(),
            Reset + "$global:__BashErrtrace = $true; $global:__BashTrapERR = [scriptblock]::Create('\"x; $(y)\"'); " +
            "$__t = [PsBash.Cmdlets.BashStateSnapshot]::JobGlobals($ExecutionContext.SessionState)['__BashTrapERR']; " +
            "$__t.ToString(); [object]::ReferenceEquals($__t, $global:__BashTrapERR)").AssertSuccess();
        Assert.Equal(new[] { "\"x; $(y)\"", "False" }, r.Lines);
    }
}
