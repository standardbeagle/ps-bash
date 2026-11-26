using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;
using PsBash.Testing;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

/// <summary>
/// Coverage for endpoint override resolution: CLI flag, PSBASH_IPC_ENDPOINT
/// env var, malformed/empty input, unknown scheme, and precedence between the
/// three layers. Mutates the real env var, so it runs alone
/// (<see cref="ProcessEnvironmentCollection"/>) and restores the prior values.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public class IpcTransportFactoryTests : IDisposable
{
    private readonly ProcessEnv _env;

    public IpcTransportFactoryTests()
    {
        _env = ProcessEnv.Set((IpcTransportFactory.EndpointEnvVar, null), (IpcTransportFactory.SessionEnvVar, null));
        // Pin the automatic session token off by default so the canonical-fallback
        // tests are deterministic regardless of the test runner's real parent
        // ancestry; per-session tests set this seam (or PSBASH_SESSION) explicitly.
        IpcTransportFactory.SessionAnchorOverride = () => null;
        IpcTransportFactory.EnclosingHostOverride = () => null;
    }

    public void Dispose()
    {
        _env.Dispose();
        IpcTransportFactory.SessionAnchorOverride = null;
        IpcTransportFactory.EnclosingHostOverride = null;
    }

    [Fact]
    public void ResolveEndpoint_EnclosedByAnUnregisteredHost_FallsBackToTheSessionAnchor()
    {
        // A host with no sidecar here (an interactive terminal's host) keeps the anchored session.
        IpcTransportFactory.EnclosingHostOverride = () => Anchor(int.MaxValue - 7, 1);
        IpcTransportFactory.SessionAnchorOverride = () => Anchor(12345, 987654321);
        IpcTransportFactory.ResolveEndpoint(null, out var anchor, out var enclosing);
        Assert.Equal(Anchor(12345, 987654321), anchor);
        Assert.Null(enclosing);  // not inside a registered host: busy slots keep the contended back-off
    }

    [Fact]
    public void ResolveEndpoint_ExplicitEndpoint_ReportsNoEnclosingHost()
    {
        IpcTransportFactory.EnclosingHostOverride = () => Anchor(12345, 1);
        IpcTransportFactory.ResolveEndpoint("unix:/custom.sock", out _, out var enclosing);
        Assert.Null(enclosing);
    }

    private static ProcessIdentity Anchor(int pid, long startTicks)
        => new(pid, startTicks);

    private void SetEnv(string? value) => _env.Change(IpcTransportFactory.EndpointEnvVar, value);

    private void SetSession(string? value) => _env.Change(IpcTransportFactory.SessionEnvVar, value);

    [Fact]
    public void ResolveSlotEndpoint_Slot0IsTheBase_HigherSlotsAreDistinctSiblings()
    {
        var (s0, e0) = IpcTransportFactory.ResolveSlotEndpoint("unix", "/tmp/psb-slot-x.sock", 0);
        var (s1, e1) = IpcTransportFactory.ResolveSlotEndpoint("unix", "/tmp/psb-slot-x.sock", 1);
        var (_, e2) = IpcTransportFactory.ResolveSlotEndpoint("unix", "/tmp/psb-slot-x.sock", 2);
        Assert.Equal("unix", s0);
        Assert.Equal("unix", s1);
        Assert.Equal("/tmp/psb-slot-x.sock", e0);
        Assert.Equal("/tmp/psb-slot-x.sock-x1", e1);
        Assert.Equal("/tmp/psb-slot-x.sock-x2", e2);
    }

    [Theory]
    [InlineData("/tmp/psb-slot-x.sock", false)]
    [InlineData("/tmp/psb-slot-x.sock-x1", true)]
    [InlineData("/tmp/psb-slot-x.sock-x6", true)]
    [InlineData(@"C:\t\ps-bash\host-andyb-s21576-639301987569778510.sock", false)]
    [InlineData(@"C:\t\ps-bash\psbash-slot-0123456789ABCDEF-x3", true)]
    [InlineData("/tmp/a.sock-x", false)]
    [InlineData("/tmp/a.sock-x123", false)]
    [InlineData("/tmp/a.sock-y1", false)]
    public void IsSlotEndpoint_MatchesOnlyResolveSlotEndpointNames(string endpoint, bool expected)
        => Assert.Equal(expected, IpcTransportFactory.IsSlotEndpoint(endpoint));

    [Fact]
    public void IsSlotEndpoint_EveryResolvedSlotParsesBack()
    {
        var longBase = "/tmp/" + new string('a', 400) + ".sock";
        for (int slot = 1; slot <= IpcTransportFactory.MaxExecSlots; slot++)
        {
            Assert.True(IpcTransportFactory.IsSlotEndpoint(IpcTransportFactory.ResolveSlotEndpoint("unix", "/tmp/b.sock", slot).Endpoint));
            Assert.True(IpcTransportFactory.IsSlotEndpoint(IpcTransportFactory.ResolveSlotEndpoint("unix", longBase, slot).Endpoint));
        }
    }

    [Fact]
    public void ResolveSlotEndpoint_OverlongBase_FallsBackToShortHashedName()
    {
        var longBase = "/tmp/" + new string('a', 400) + ".sock";
        var (_, e1) = IpcTransportFactory.ResolveSlotEndpoint("unix", longBase, 1);
        var (_, e2) = IpcTransportFactory.ResolveSlotEndpoint("unix", longBase, 2);
        Assert.True(e1.Length <= IpcTransportFactory.UnixSocketPathBudget());
        Assert.NotEqual(e1, e2);
        Assert.EndsWith("-x1", e1);
    }
    [Fact]
    public void ResolveEndpoint_CliOverrideUnix_ReturnsParsedPair()
    {
        var (scheme, endpoint) = IpcTransportFactory.ResolveEndpoint("unix:/tmp/test.sock");
        Assert.Equal("unix", scheme);
        Assert.Equal("/tmp/test.sock", endpoint);
    }

    [Fact]
    public void ResolveEndpoint_CliOverridePipe_IsRejectedWithRemovalHint()
    {
        var ex = Assert.Throws<InvalidEndpointException>(() => IpcTransportFactory.ResolveEndpoint("pipe:psbash-test-xyz"));
        Assert.Contains("named-pipe transport was removed", ex.Message);
        Assert.Contains("unix:", ex.Message);
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
        var (scheme, endpoint) = IpcTransportFactory.ResolveEndpoint("unix:/cli/path.sock");
        Assert.Equal("unix", scheme);
        Assert.Equal("/cli/path.sock", endpoint);
    }

    [Fact]
    public void ResolveEndpoint_NoOverride_FallsBackToCanonical()
    {
        var (scheme, endpoint) = IpcTransportFactory.ResolveEndpoint();
        Assert.Equal("unix", scheme);
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

    // GH #7: the anchor that keys the endpoint is reported so the launcher can hand it to the
    // daemon it spawns; the daemon exits once that process is gone. Only the automatic
    // token has an owning process — every other endpoint source reports none.
    [Fact]
    public void ResolveEndpoint_AutomaticToken_ReportsTheAnchorItUsed()
    {
        IpcTransportFactory.SessionAnchorOverride = () => Anchor(12345, 987654321);
        IpcTransportFactory.ResolveEndpoint(null, out var anchor);
        Assert.Equal(Anchor(12345, 987654321), anchor);
    }

    [Fact]
    public void ResolveEndpoint_ExplicitSession_ReportsNoAnchor()
    {
        IpcTransportFactory.SessionAnchorOverride = () => Anchor(9999, 111);
        SetSession("explicit");
        IpcTransportFactory.ResolveEndpoint(null, out var anchor);
        Assert.Null(anchor);
    }

    [Fact]
    public void ResolveEndpoint_EndpointOverride_ReportsNoAnchor()
    {
        IpcTransportFactory.SessionAnchorOverride = () => Anchor(9999, 111);
        IpcTransportFactory.ResolveEndpoint("unix:/custom.sock", out var fromCli);
        Assert.Null(fromCli);
        SetEnv("unix:/custom.sock");
        IpcTransportFactory.ResolveEndpoint(null, out var fromEnv);
        Assert.Null(fromEnv);
    }

    [Fact]
    public void ResolveEndpoint_NoAnchorAvailable_ReportsNoAnchor()
    {
        IpcTransportFactory.ResolveEndpoint(null, out var anchor);
        Assert.Null(anchor);
    }

    [Fact]
    public void ResolveEndpoint_EndpointOverrideBeatsSession()
    {
        SetSession("ignored");
        var (scheme, endpoint) = IpcTransportFactory.ResolveEndpoint("unix:/custom.sock");
        Assert.Equal("unix", scheme);
        Assert.Equal("/custom.sock", endpoint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveEndpoint_EmptyOrWhitespaceCli_TreatedAsAbsent(string spec)
    {
        var (scheme, _) = IpcTransportFactory.ResolveEndpoint(spec);
        Assert.Equal("unix", scheme);
    }

    [Theory]
    [InlineData("nocolon")]
    [InlineData(":nopath")]
    [InlineData("unix:")]
    public void ResolveEndpoint_MalformedCli_Throws(string spec)
    {
        var ex = Assert.Throws<InvalidEndpointException>(() => IpcTransportFactory.ResolveEndpoint(spec));
        Assert.Contains("--ipc-endpoint", ex.Message);
        Assert.Contains(spec, ex.Message);
    }

    [Fact]
    public void ResolveEndpoint_UnknownScheme_Throws()
    {
        var ex = Assert.Throws<InvalidEndpointException>(
            () => IpcTransportFactory.ResolveEndpoint("tcp:127.0.0.1:9000"));
        Assert.Contains("tcp", ex.Message);
        Assert.Contains("Expected unix", ex.Message);
    }

    [Fact]
    public void ResolveEndpoint_EnvSocketPathPastSunPath_ThrowsNamingTheInput()
    {
        // REGRESSION: a too-long override reached UnixDomainSocketEndPoint and crashed the launcher.
        var fits = "/" + new string('a', IpcTransportFactory.UnixSocketPathBudget() - 1);
        Assert.Equal(fits, IpcTransportFactory.ResolveEndpoint("unix:" + fits).Endpoint);

        SetEnv("unix:" + fits + "b");
        var ex = Assert.Throws<InvalidEndpointException>(() => IpcTransportFactory.ResolveEndpoint());
        Assert.Contains(IpcTransportFactory.EndpointEnvVar, ex.Message);
        Assert.Contains($"AF_UNIX allows {IpcTransportFactory.UnixSocketPathBudget()}", ex.Message);
    }

    [Fact]
    public void ResolveEndpoint_MalformedEnvVar_ThrowsWithEnvVarName()
    {
        SetEnv("garbage-no-colon");
        var ex = Assert.Throws<InvalidEndpointException>(() => IpcTransportFactory.ResolveEndpoint());
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

    // Longest path allowed by AF_UNIX sun_path on this platform (107 on Linux/Windows, 103 on macOS).
    private static int SunPathBudget => IpcTransportFactory.UnixSocketPathBudget();

    /// <summary>
    /// A temp root sized so the per-invocation socket path leaves exactly 20 hex chars of room under the
    /// budget: enough (>= 12) to stay valid, too little for the 32-char guid, so the shortening is
    /// exercised. Independent of the machine's real temp dir, which is long on macOS and Windows runners.
    /// </summary>
    private static string LongTempRoot()
    {
        var seed = System.IO.Path.DirectorySeparatorChar == '\\' ? @"C:\t" : "/t";
        var saved = PsBashRuntimeDirectory.TempPathOverride;
        PsBashRuntimeDirectory.TempPathOverride = () => seed;
        int overhead;
        try { overhead = PsBashRuntimeDirectory.GetPath().Length - seed.Length; }
        finally { PsBashRuntimeDirectory.TempPathOverride = saved; }

        // prefix = <dir>\host-pi-<pid>-   then   <20 hex> + ".sock"
        const int fixedTail = 8 + 6 + 1 /* "host-pi-", pid, '-' */ + 1 /* separator */ + 20 + 5 /* ".sock" */;
        int rootLength = SunPathBudget - overhead - fixedTail;

        // The root is a real directory the factory will create, so it must be writable: the machine's temp
        // dir when that is short enough (Linux, Windows), else /tmp (macOS's per-user temp is already long).
        var sep = System.IO.Path.DirectorySeparatorChar;
        var baseDir = System.IO.Path.GetTempPath().TrimEnd(sep);
        if (baseDir.Length + 2 > rootLength && sep == '/') baseDir = "/tmp";
        return baseDir + sep + new string('x', Math.Max(1, rootLength - baseDir.Length - 1));
    }

    [Fact]
    public void ResolvePerInvocationEndpoint_SixDigitPidAndLongTempRoot_FitsSunPathBudget()
    {
        // Reproduces the failed Differential gate: a 6-digit launcher pid under
        // the canonical long temp root produced a 108-char socket path and the
        // host died in UnixDomainSocketEndPoint construction.
        IpcTransportFactory.ProcessIdOverride = () => 291088;
        PsBashRuntimeDirectory.TempPathOverride = LongTempRoot;
        try
        {
            var (scheme, endpoint) = IpcTransportFactory.ResolvePerInvocationEndpoint();

            // The random suffix is shortened to fit; the launcher pid is kept.
            Assert.Equal("unix", scheme);
            Assert.True(
                endpoint.Length <= SunPathBudget,
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
            Assert.True(e1.Length <= SunPathBudget, $"e1 is {e1.Length} chars: {e1}");
            Assert.True(e2.Length <= SunPathBudget, $"e2 is {e2.Length} chars: {e2}");
        }
        finally
        {
            ResetSeams();
        }
    }

    [Fact]
    public void ResolveEndpoint_OverlongRuntimeDirectory_FailsClearlyInsteadOfFallingBack()
    {
        // There is no second transport: a runtime directory that leaves no room under sun_path is a
        // configuration fault, reported with the way out.
        PsBashRuntimeDirectory.TempPathOverride =
            () => System.IO.Path.Combine(System.IO.Path.GetTempPath(), new string('x', 120));
        IpcTransportFactory.SessionAnchorOverride = () => Anchor(12345, 1);
        try
        {
            var ex = Assert.Throws<IOException>(() => IpcTransportFactory.ResolveEndpoint());
            Assert.Contains("AF_UNIX", ex.Message);
            Assert.Contains("TMPDIR", ex.Message);
        }
        finally
        {
            ResetSeams();
        }
    }

    private static void ResetSeams()
    {
        IpcTransportFactory.ProcessIdOverride = null;
        PsBashRuntimeDirectory.TempPathOverride = null;
        IpcTransportFactory.SessionAnchorOverride = () => null;
    }
}
