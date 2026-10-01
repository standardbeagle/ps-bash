using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

/// <summary>
/// Coverage for endpoint override resolution: CLI flag, PSBASH_IPC_ENDPOINT
/// env var, malformed/empty input, unknown scheme, and precedence between the
/// three layers. Mutates the real env var; runs in a serial collection so
/// parallel tests do not race on process-wide state.
/// </summary>
[Collection("EnvVar")]
public class IpcTransportFactoryTests : IDisposable
{
    private readonly string? _priorEnv;
    private readonly string? _priorSession;

    public IpcTransportFactoryTests()
    {
        _priorEnv = Environment.GetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar);
        _priorSession = Environment.GetEnvironmentVariable(IpcTransportFactory.SessionEnvVar);
        Environment.SetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar, null);
        Environment.SetEnvironmentVariable(IpcTransportFactory.SessionEnvVar, null);
        // Pin the automatic session token off by default so the canonical-fallback
        // tests are deterministic regardless of the test runner's real parent
        // ancestry; per-session tests set this seam (or PSBASH_SESSION) explicitly.
        IpcTransportFactory.SessionAnchorOverride = () => null;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar, _priorEnv);
        Environment.SetEnvironmentVariable(IpcTransportFactory.SessionEnvVar, _priorSession);
        IpcTransportFactory.SessionAnchorOverride = null;
    }

    private static ProcessAncestry.ProcessIdentity Anchor(int pid, long startTicks)
        => new(pid, startTicks);

    private static void SetEnv(string? value)
        => Environment.SetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar, value);

    private static void SetSession(string? value)
        => Environment.SetEnvironmentVariable(IpcTransportFactory.SessionEnvVar, value);

    [Fact]
    public void ResolveNestedEndpoint_DerivesPerDepthEndpoint_AndParsesBack()
    {
        SetEnv("pipe:psb-nest-x");
        var (s1, e1) = IpcTransportFactory.ResolveNestedEndpoint(1);
        var (_, e2) = IpcTransportFactory.ResolveNestedEndpoint(2);
        Assert.Equal("pipe", s1);
        Assert.Equal("psb-nest-x-nested1", e1);
        Assert.NotEqual(e1, e2);
        Assert.Equal(1, IpcTransportFactory.ParseNestDepth(e1));
        Assert.Equal(2, IpcTransportFactory.ParseNestDepth(e2));
        Assert.Equal(0, IpcTransportFactory.ParseNestDepth("psb-nest-x"));
    }

    [Fact]
    public void CurrentNestDepth_ZeroOutsideHost_PublishedDepthInside()
    {
        var prior = Environment.GetEnvironmentVariable(IpcTransportFactory.InsideHostEnvVar);
        var priorDepth = Environment.GetEnvironmentVariable(IpcTransportFactory.NestDepthEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(IpcTransportFactory.InsideHostEnvVar, null);
            Environment.SetEnvironmentVariable(IpcTransportFactory.NestDepthEnvVar, "3");
            Assert.Equal(0, IpcTransportFactory.CurrentNestDepth());
            Environment.SetEnvironmentVariable(IpcTransportFactory.InsideHostEnvVar, "123");
            Assert.Equal(3, IpcTransportFactory.CurrentNestDepth());
            Environment.SetEnvironmentVariable(IpcTransportFactory.NestDepthEnvVar, null);
            Assert.Equal(1, IpcTransportFactory.CurrentNestDepth()); // older host: marker only
        }
        finally
        {
            Environment.SetEnvironmentVariable(IpcTransportFactory.InsideHostEnvVar, prior);
            Environment.SetEnvironmentVariable(IpcTransportFactory.NestDepthEnvVar, priorDepth);
        }
    }

    [Fact]
    public void ResolveEndpoint_CliOverrideUnix_ReturnsParsedPair()
    {
        var (scheme, endpoint) = IpcTransportFactory.ResolveEndpoint("unix:/tmp/test.sock");
        Assert.Equal("unix", scheme);
        Assert.Equal("/tmp/test.sock", endpoint);
    }

    [Fact]
    public void ResolveEndpoint_CliOverridePipe_ReturnsParsedPair()
    {
        var (scheme, endpoint) = IpcTransportFactory.ResolveEndpoint("pipe:psbash-test-xyz");
        Assert.Equal("pipe", scheme);
        Assert.Equal("psbash-test-xyz", endpoint);
    }

    [Fact]
    public void ResolveEndpoint_EnvVarUnix_UsedWhenCliAbsent()
    {
        SetEnv("unix:/var/run/psbash.sock");
        var (scheme, endpoint) = IpcTransportFactory.ResolveEndpoint();
        Assert.Equal("unix", scheme);
        Assert.Equal("/var/run/psbash.sock", endpoint);
    }

    [Fact]
    public void ResolveEndpoint_CliBeatsEnvVar()
    {
        SetEnv("unix:/env/path.sock");
        var (scheme, endpoint) = IpcTransportFactory.ResolveEndpoint("pipe:cli-pipe");
        Assert.Equal("pipe", scheme);
        Assert.Equal("cli-pipe", endpoint);
    }

    [Fact]
    public void ResolveEndpoint_NoOverride_FallsBackToCanonical()
    {
        var (scheme, endpoint) = IpcTransportFactory.ResolveEndpoint();
        Assert.True(scheme is "unix" or "pipe");
        Assert.False(string.IsNullOrEmpty(endpoint));
    }

    [Fact]
    public void ResolveEndpoint_NoSessionToken_OmitsSessionSuffix()
    {
        // Seam returns null and PSBASH_SESSION unset → historical per-user endpoint,
        // no "-s" session segment.
        var (_, endpoint) = IpcTransportFactory.ResolveEndpoint();
        Assert.DoesNotContain("-s", System.IO.Path.GetFileName(endpoint));
    }

    [Fact]
    public void ResolveEndpoint_ExplicitSession_FoldedIntoEndpoint()
    {
        SetSession("agent42");
        var (_, endpoint) = IpcTransportFactory.ResolveEndpoint();
        Assert.Contains("-sagent42", endpoint);
    }

    [Fact]
    public void ResolveEndpoint_DistinctSessions_ProduceDistinctEndpoints()
    {
        SetSession("alpha");
        var (_, a) = IpcTransportFactory.ResolveEndpoint();
        SetSession("beta");
        var (_, b) = IpcTransportFactory.ResolveEndpoint();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ResolveEndpoint_SameSession_ProducesSameEndpoint()
    {
        SetSession("shared");
        var (_, a) = IpcTransportFactory.ResolveEndpoint();
        var (_, b) = IpcTransportFactory.ResolveEndpoint();
        Assert.Equal(a, b);
    }

    [Fact]
    public void ResolveEndpoint_ExplicitSessionBeatsAutomaticToken()
    {
        // Even when the automatic (ancestor-identity) token is available, an
        // explicit PSBASH_SESSION takes precedence.
        IpcTransportFactory.SessionAnchorOverride = () => Anchor(9999, 111);
        SetSession("explicit");
        var (_, endpoint) = IpcTransportFactory.ResolveEndpoint();
        Assert.Contains("-sexplicit", endpoint);
        Assert.DoesNotContain("9999", endpoint);
    }

    [Fact]
    public void ResolveEndpoint_AutomaticToken_IncludesAnchorStartTime()
    {
        // R07: the automatic token is "<pid>-<startTicks>", so a recycled PID is a
        // distinct key and a stable ancestor reuses one warm daemon.
        IpcTransportFactory.SessionAnchorOverride = () => Anchor(12345, 987654321);
        var (_, endpoint) = IpcTransportFactory.ResolveEndpoint();
        Assert.Contains("-s12345-987654321", endpoint);
    }

    [Fact]
    public void ResolveEndpoint_SameAnchorIdentity_ProducesSameEndpoint()
    {
        IpcTransportFactory.SessionAnchorOverride = () => Anchor(12345, 987);
        var (_, a) = IpcTransportFactory.ResolveEndpoint();
        var (_, b) = IpcTransportFactory.ResolveEndpoint();
        Assert.Equal(a, b);
    }

    [Fact]
    public void ResolveEndpoint_RecycledPidWithNewStartTime_ProducesDistinctEndpoint()
    {
        // R07: the same PID with a different start time is a different session —
        // PID reuse can never attach an unrelated invocation to a stale daemon.
        IpcTransportFactory.SessionAnchorOverride = () => Anchor(12345, 1000);
        var (_, a) = IpcTransportFactory.ResolveEndpoint();
        IpcTransportFactory.SessionAnchorOverride = () => Anchor(12345, 2000);
        var (_, b) = IpcTransportFactory.ResolveEndpoint();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ResolveEndpoint_EndpointOverrideBeatsSession()
    {
        SetSession("ignored");
        var (scheme, endpoint) = IpcTransportFactory.ResolveEndpoint("pipe:custom");
        Assert.Equal("pipe", scheme);
        Assert.Equal("custom", endpoint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveEndpoint_EmptyOrWhitespaceCli_TreatedAsAbsent(string spec)
    {
        var (scheme, _) = IpcTransportFactory.ResolveEndpoint(spec);
        Assert.True(scheme is "unix" or "pipe");
    }

    [Theory]
    [InlineData("nocolon")]
    [InlineData(":nopath")]
    [InlineData("unix:")]
    [InlineData("pipe:")]
    public void ResolveEndpoint_MalformedCli_Throws(string spec)
    {
        var ex = Assert.Throws<ArgumentException>(() => IpcTransportFactory.ResolveEndpoint(spec));
        Assert.Contains("--ipc-endpoint", ex.Message);
        Assert.Contains(spec, ex.Message);
    }

    [Fact]
    public void ResolveEndpoint_UnknownScheme_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => IpcTransportFactory.ResolveEndpoint("tcp:127.0.0.1:9000"));
        Assert.Contains("tcp", ex.Message);
        Assert.Contains("Expected unix or pipe", ex.Message);
    }

    [Fact]
    public void ResolveEndpoint_MalformedEnvVar_ThrowsWithEnvVarName()
    {
        SetEnv("garbage-no-colon");
        var ex = Assert.Throws<ArgumentException>(() => IpcTransportFactory.ResolveEndpoint());
        Assert.Contains(IpcTransportFactory.EndpointEnvVar, ex.Message);
    }

    [Fact]
    public void ResolveEndpoint_EndpointWithEmbeddedColons_PreservedAfterFirstColon()
    {
        // Endpoint may contain colons (Windows absolute paths). Split is on
        // the FIRST colon only — everything after is the endpoint verbatim.
        var (scheme, endpoint) = IpcTransportFactory.ResolveEndpoint(@"unix:C:\Users\x\sock");
        Assert.Equal("unix", scheme);
        Assert.Equal(@"C:\Users\x\sock", endpoint);
    }

    // Longest path allowed by AF_UNIX sun_path. .NET rejects any Unix
    // domain-socket path longer than 107 chars (108 bytes incl. the NUL).
    private const int SunPathBudget = 107;

    private static string LongTempRoot()
        => System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "psb-g12345678-0123456789abcdef");

    [Fact]
    public void ResolvePerInvocationEndpoint_SixDigitPidAndLongTempRoot_FitsSunPathBudget()
    {
        // Reproduces the failed Differential gate: a 6-digit launcher pid under
        // the canonical long temp root produced a 108-char socket path and the
        // host died in UnixDomainSocketEndPoint construction.
        IpcTransportFactory.UnixSocketSupportedOverride = () => true;
        IpcTransportFactory.ProcessIdOverride = () => 291088;
        PsBashRuntimeDirectory.TempPathOverride = LongTempRoot;
        try
        {
            var (scheme, endpoint) = IpcTransportFactory.ResolvePerInvocationEndpoint();

            // Either the unix path was shortened to fit, or it fell back to the
            // named-pipe scheme; both keep the launcher pid and never overflow.
            Assert.True(scheme is "unix" or "pipe", $"unexpected scheme '{scheme}'");
            Assert.True(
                scheme != "unix" || endpoint.Length <= SunPathBudget,
                $"endpoint is {endpoint.Length} chars, exceeds the {SunPathBudget}-char sun_path budget: {endpoint}");
            Assert.Contains("291088", endpoint);
        }
        finally
        {
            ResetSeams();
        }
    }

    [Fact]
    public void ResolvePerInvocationEndpoint_SamePid_TwoCalls_AreDistinct()
    {
        // Shortening the random suffix must not lose the per-invocation
        // uniqueness that lets two concurrent launchers of the same pid
        // (e.g. a forked -c fan-out) each bind their own private socket.
        IpcTransportFactory.UnixSocketSupportedOverride = () => true;
        IpcTransportFactory.ProcessIdOverride = () => 291088;
        PsBashRuntimeDirectory.TempPathOverride = LongTempRoot;
        try
        {
            var (s1, e1) = IpcTransportFactory.ResolvePerInvocationEndpoint();
            var (s2, e2) = IpcTransportFactory.ResolvePerInvocationEndpoint();

            Assert.Equal(s1, s2);
            Assert.NotEqual(e1, e2);
            Assert.Contains("291088", e1);
            Assert.Contains("291088", e2);
            Assert.True(s1 != "unix" || e1.Length <= SunPathBudget, $"e1 is {e1.Length} chars: {e1}");
            Assert.True(s1 != "unix" || e2.Length <= SunPathBudget, $"e2 is {e2.Length} chars: {e2}");
        }
        finally
        {
            ResetSeams();
        }
    }

    [Fact]
    public void ResolveEndpoint_LongTempRoot_FallsBackToPipeWithinBudget()
    {
        // The canonical per-session socket carries the user and session token;
        // a long temp root (or user/session token) must not overflow sun_path.
        // It falls back to the named-pipe scheme, which has no such limit.
        IpcTransportFactory.UnixSocketSupportedOverride = () => true;
        PsBashRuntimeDirectory.TempPathOverride =
            () => System.IO.Path.Combine(System.IO.Path.GetTempPath(), new string('x', 120));
        IpcTransportFactory.SessionAnchorOverride = () => Anchor(12345, 1);
        try
        {
            var (scheme, endpoint) = IpcTransportFactory.ResolveEndpoint();

            Assert.Equal("pipe", scheme);
            Assert.False(string.IsNullOrEmpty(endpoint));
        }
        finally
        {
            ResetSeams();
        }
    }

    private static void ResetSeams()
    {
        IpcTransportFactory.UnixSocketSupportedOverride = null;
        IpcTransportFactory.ProcessIdOverride = null;
        PsBashRuntimeDirectory.TempPathOverride = null;
        IpcTransportFactory.SessionAnchorOverride = () => null;
    }
}
