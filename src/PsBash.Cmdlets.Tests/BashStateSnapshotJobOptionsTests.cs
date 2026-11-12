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

    private IReadOnlyList<string> Options(string setup) =>
        CmdResult.Run(_fixture.AcquireFresh(),
            Reset + setup + "; [PsBash.Cmdlets.BashStateSnapshot]::BuildJobOptions($ExecutionContext.SessionState)")
            .AssertSuccess().Lines.SelectMany(l => l.Split('\n', StringSplitOptions.RemoveEmptyEntries)).ToList();

    [Fact]
    public void JobOptions_WriteEveryOptionOnOrOff()
    {
        var lines = Options("$global:__BashErrexit = $true; $global:__BashNounset = $true");

        Assert.Contains("$global:__BashErrexit = $true", lines);
        Assert.Contains("$global:__BashNounset = $true", lines);
        Assert.Contains("$global:__BashPipefail = $false", lines);
        Assert.Contains("$global:__BashErrtrace = $false", lines);
        Assert.Contains("$global:__BashErrexitSuppress = 0", lines);
    }

    [Fact]
    public void JobOptions_ErrTrapOnlyUnderErrtrace()
    {
        Assert.Contains("$global:__BashTrapERR = $null",
            Options("$global:__BashTrapERR = [scriptblock]::Create('echo T')"));

        var withErrtrace = Options("$global:__BashErrtrace = $true; $global:__BashTrapERR = [scriptblock]::Create(\"echo 'it''s'\")");
        Assert.Contains("$global:__BashTrapERR = [scriptblock]::Create('echo ''it''''s''')", withErrtrace);
    }

    /// <summary>The text round-trips: running the options in another runspace rebuilds the same trap.</summary>
    [Fact]
    public void JobOptions_RebuildTheTrapInAnotherRunspace()
    {
        var text = string.Join("\n", Options("$global:__BashErrtrace = $true; $global:__BashTrapERR = [scriptblock]::Create('\"x; $(y)\"')"));

        using var other = PwshTestFixture.Create();
        var r = CmdResult.Run(other, text.Replace("\n", "; ") + "; $global:__BashTrapERR.ToString(); $global:__BashErrtrace").AssertSuccess();
        Assert.Equal(new[] { "\"x; $(y)\"", "True" }, r.Lines);
    }
}
