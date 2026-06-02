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

    [Fact]
    public void Ping_NoHost_WritesUsageError()
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("Invoke-BashPing -c 1").Invoke();

        Assert.True(pwsh.HadErrors);
        Assert.Contains(pwsh.Streams.Error, e => e.ToString().Contains("Destination address required"));
    }

    [Fact]
    public void Ping_PipedToShowStyled_AutoPicksNetSheet()
    {
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
