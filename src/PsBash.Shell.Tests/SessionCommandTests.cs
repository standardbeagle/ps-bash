using BrokerState = PsBash.Shell.Sessions.TerminalClient.BrokerState;
using TerminalClient = PsBash.Shell.Sessions.TerminalClient;
using PsBash.Testing;
using Xunit;

namespace PsBash.Shell.Tests;

public sealed class SessionCommandTests
{
    [Theory]
    [InlineData("build")]
    [InlineData("agent_1-a")]
    [InlineData("x")]
    public void IsValidName_AcceptsPathSafeNames(string name)
        => Assert.True(SessionCommands.IsValidName(name));

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("a;rm")]
    [InlineData("name-that-is-way-too-long-to-be-a-session")]
    public void IsValidName_RejectsAnythingThatCouldEscapeAnEndpointName(string name)
        => Assert.False(SessionCommands.IsValidName(name));

    [Theory]
    [InlineData("host-andy-sbuild.sock.host.json", "andy", "build")]
    [InlineData("host-andy-s123-456.sock.host.json", "andy", "123-456")]
    public void TokenFromSidecarName_ExtractsTheSessionToken(string file, string user, string expected)
        => Assert.Equal(expected, SessionCommands.TokenFromSidecarName(file, user));

    [Theory]
    [InlineData("host-other-sbuild.sock.host.json")]      // another user's session
    [InlineData("host-andy.sock.host.json")]               // per-user endpoint, no session
    [InlineData("host-andy-s.sock.host.json")]             // empty token
    [InlineData("host-.sock.host.json")]
    [InlineData("module-0.12.0.0.json")]
    [InlineData("spawn-unix-abc.lock")]
    public void TokenFromSidecarName_IgnoresEverythingElse(string file)
        => Assert.Null(SessionCommands.TokenFromSidecarName(file, "andy"));

    [Theory]
    [InlineData("4242-639279814259006900", true)]   // launcher-anchored: pid-startTicks
    [InlineData("build", false)]
    [InlineData("2024-release", false)]
    public void IsAnchorToken_SeparatesEphemeralFromNamedSessions(string token, bool anchored)
        => Assert.Equal(anchored, SessionCommands.IsAnchorToken(token));

    [Theory]
    [InlineData("host-andy-sbuild-t.sock", "andy", "build")]
    [InlineData("host-andy-sa_b-t.sock", "andy", "a_b")]
    public void TokenFromTerminalSocketName_ExtractsTheSessionToken(string file, string user, string expected)
        => Assert.Equal(expected, SessionCommands.TokenFromTerminalSocketName(file, user));

    [Theory]
    [InlineData("host-other-sbuild-t.sock")]         // another user's broker
    [InlineData("host-andy-sbuild.sock")]            // a command-host socket
    [InlineData("host-andy-s-t.sock")]               // empty token
    [InlineData("host-andy-t.sock")]                 // per-user endpoint, no session
    [InlineData("host-andy-sbuild-t.sock.host.json")]
    public void TokenFromTerminalSocketName_IgnoresEverythingElse(string file)
        => Assert.Null(SessionCommands.TokenFromTerminalSocketName(file, "andy"));

    [Fact]
    public void FormatListLine_TerminalOnlySession_IsListedWithNoHost()
        => Assert.Equal("build\tno-host\tterminal=running",
            SessionCommands.FormatListLine("build", null, false, sidecarPresent: false,
                BrokerState.Running));

    [Fact]
    public void FormatListLine_HostOnlySession_ReportsNoTerminal()
        => Assert.Equal("build\tunknown\tterminal=none",
            SessionCommands.FormatListLine("build", null, false, sidecarPresent: true,
                BrokerState.None));

    [Fact]
    public void FormatListLine_LeftoverBrokerSocket_IsStale()
        => Assert.EndsWith("terminal=stale",
            SessionCommands.FormatListLine("build", null, false, sidecarPresent: false,
                BrokerState.Stale));

    [Fact]
    public async Task Probe_MissingSocket_IsNone()
        => Assert.Equal(BrokerState.None,
            await TerminalClient.ProbeAsync(
                Path.Combine(Path.GetTempPath(), "psb-no-such-" + Guid.NewGuid().ToString("N")[..8] + "-t.sock"),
                CancellationToken.None));
}

/// <summary>
/// <see cref="SessionCommandTests"/>' environment-gated case: <c>PSBASH_SESSION</c> is process environment, so it runs
/// alone (<see cref="ProcessEnvironmentCollection"/>) and restores it.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SessionCommandEnvironmentTests
{
    [Fact]
    public void IsStatefulSessionActive_WithoutANamedSession_IsFalse()
    {
        using var env = ProcessEnv.Set("PSBASH_SESSION", null);
        Assert.False(SessionCommands.IsStatefulSessionActive());
    }
}
