using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Tests for <c>Invoke-BashPing</c> — the network-probe cmdlet that emits styled <c>PsBash.PingReply</c>
/// objects (a native-style <c>BashText</c> line plus typed properties and a latency <c>class</c>).
/// Oracle note (Directive 1): ps-bash-specific cmdlet surface, not a bash-parity feature. Probes the
/// loopback address only, so the tests are offline and deterministic.
/// </summary>
public class InvokeBashPingCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashPingCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Ping_LoopbackCount2_EmitsTwoStyledReplyObjects()
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript("Invoke-BashPing -c 2 127.0.0.1").Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Equal(2, result.Count);

        for (var i = 0; i < result.Count; i++)
        {
            var o = result[i];
            // Typed object, not a bare string: carries the Strata kind + the styling hooks.
            Assert.Contains("PsBash.PingReply", o.TypeNames);
            Assert.Equal(i + 1, (int)o.Properties["Seq"].Value);
            Assert.Equal("ok", (string)o.Properties["Status"].Value); // loopback always succeeds
            Assert.Equal("ok", (string)o.Properties["class"].Value);  // and is sub-80ms
            Assert.Contains("127.0.0.1", o.Properties["BashText"].Value?.ToString());
        }
    }

    // iputils statuses (oracle: `ping -c1 127.0.0.1` 0, `ping -c1 -W1 192.0.2.1` 1, `ping -c1 nosuch.invalid` 2).
    [Theory]
    [InlineData(2, false, 0)]
    [InlineData(0, false, 1)]
    [InlineData(0, true, 2)]
    public void ExitStatus_IsIputils(int replies, bool unresolved, int expected)
        => Assert.Equal(expected, InvokeBashPingCommand.ExitStatus(replies, unresolved));

    // DIRECT calls (Pester, an interactive PowerShell user): the transpiler quotes every flag, a direct call does not.
    // REGRESSION: the case-insensitive decoy W took a direct `-w 3` (deadline, unsupported) and ran it as `-W 3`
    // (reply timeout); `-i 1` / `-p ff` / `-O` were "parameter name is ambiguous" binder errors. The typed flag is read
    // back from the command's syntax tree (BashRuntime.TypedArguments).
    [Theory]
    [InlineData("Invoke-BashPing -w 3 127.0.0.1", "-w")]
    [InlineData("Invoke-BashPing -c 1 -i 1 127.0.0.1", "-i")]
    [InlineData("Invoke-BashPing -c 1 -I lo 127.0.0.1", "-I")]
    [InlineData("Invoke-BashPing -p ff -c 1 127.0.0.1", "-p")]
    [InlineData("Invoke-BashPing -c 1 -O 127.0.0.1", "-O")]
    [InlineData("Invoke-BashPing -c 1 -D 127.0.0.1", "-D")]
    public void Direct_CaseCollidingFlag_IsTheFlagTyped(string script, string flag)
        => CmdResult.Run(_fixture.AcquireFresh(), script)
            .AssertFailed(2, $"ping: option '{flag}' is recognized but not supported by ps-bash");

    [Fact]
    public void Direct_CapitalW_IsTheReplyTimeout()
        => CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashPing -W 1 -c 1 127.0.0.1").AssertSuccess();

    [Fact]
    public void Ping_Loopback_Exit0()
        => CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashPing -c 1 127.0.0.1").AssertSuccess();

    // REGRESSION: a name that does not resolve printed a timeout line per probe at status 0.
    [Fact]
    public void Ping_UnresolvableName_Exit2NoProbes()
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashPing -c 2 nosuch.invalid");
        r.AssertFailed(2, "ping: nosuch.invalid: Name or service not known");
        Assert.Empty(r.Lines);
    }

    [Fact]
    public void Ping_NoHost_WritesUsageError()
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("Invoke-BashPing -c 1").Invoke();

        Assert.True(pwsh.HadErrors);
        Assert.Contains(pwsh.Streams.Error, e => e.ToString().Contains("Destination address required"));
    }

    [SkippableFact]
    public void Ping_PipedToShowStyled_AutoPicksNetSheet()
    {
        Skip.IfNot(StrataBuild.Enabled, StrataBuild.SkipReason);
        // PingReply objects must auto-resolve to the built-in `net` stylesheet so
        // `ping | Show-Styled` (and the interactive default) colour replies by latency with no
        // explicit -Style. The test host has redirected I/O, so Show-Styled emits its headless
        // summary — which names the resolved style.
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript("Invoke-BashPing -c 2 127.0.0.1 | Show-Styled").Invoke();

        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        Assert.Single(result);
        Assert.Contains("style 'net'", result[0].ToString());
    }
}
