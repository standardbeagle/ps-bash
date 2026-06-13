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
        // tests are deterministic regardless of the test runner's real parent pid;
        // per-session tests set this seam (or PSBASH_SESSION) explicitly.
        IpcTransportFactory.SessionTokenOverride = () => null;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar, _priorEnv);
        Environment.SetEnvironmentVariable(IpcTransportFactory.SessionEnvVar, _priorSession);
        IpcTransportFactory.SessionTokenOverride = null;
    }

    private static void SetEnv(string? value)
        => Environment.SetEnvironmentVariable(IpcTransportFactory.EndpointEnvVar, value);

    private static void SetSession(string? value)
        => Environment.SetEnvironmentVariable(IpcTransportFactory.SessionEnvVar, value);

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
        // Even when the automatic (parent-pid) token is available, an explicit
        // PSBASH_SESSION takes precedence.
        IpcTransportFactory.SessionTokenOverride = () => "9999";
        SetSession("explicit");
        var (_, endpoint) = IpcTransportFactory.ResolveEndpoint();
        Assert.Contains("-sexplicit", endpoint);
        Assert.DoesNotContain("9999", endpoint);
    }

    [Fact]
    public void ResolveEndpoint_AutomaticToken_UsedWhenNoExplicitSession()
    {
        IpcTransportFactory.SessionTokenOverride = () => "12345";
        var (_, endpoint) = IpcTransportFactory.ResolveEndpoint();
        Assert.Contains("-s12345", endpoint);
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
}
