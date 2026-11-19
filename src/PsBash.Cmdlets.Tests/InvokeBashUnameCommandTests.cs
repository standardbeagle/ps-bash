using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of Invoke-BashUname
/// from PsBash.psm1 to a binary cmdlet (PsBash.Cmdlets.dll).
///
/// Oracle: the psm1 <c>Invoke-BashUname</c> function. Its output is computed
/// entirely from .NET APIs (<c>System.Environment.OSVersion</c>,
/// <c>System.Environment.MachineName</c>, <c>System.Environment.Is64BitProcess</c>)
/// — the same APIs the cmdlet calls in the same process. So comparing cmdlet
/// output to in-process recomputation of those identical .NET calls is a strict
/// round-trip and does not require real bash. The failure-surface axes that
/// apply: empty input (no flags = default <c>-s</c>), missing target (unknown
/// flag silently ignored), and quoting/injection (Directive 12). Pipeline /
/// file / signal / large / CRLF / unicode axes do not apply — uname has no
/// pipeline input and no file operands.
///
/// The PwshTestFixture loads psm1 (which no longer defines this function) then
/// imports PsBash.Cmdlets.dll, mirroring the host load order — so these tests
/// also prove the function-shadowing removal worked and the psm1
/// <c>Set-Alias uname</c> line still resolves to the cmdlet.
/// </summary>
public class InvokeBashUnameCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashUnameCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    // The MINGW identity is the Windows answer; Linux / macOS report the host kernel (FormatHost tests below).
    private string[] RunLines(string script)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "MINGW identity is the Windows answer; Unix reports the host kernel");
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        var err = pwsh.AddScript("$error | Select-Object -First 1").Invoke();
        pwsh.Commands.Clear();
        Assert.True(err.Count == 0 || err[0] == null,
            $"Unexpected error running [{script}]: {(err.Count > 0 ? err[0]?.ToString() : "none")}");

        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    // Reproduce the oracle's derivation in test space.
    private static string ExpectedSysName()
    {
        var v = System.Environment.OSVersion.Version;
        return $"MINGW64_NT-{v.Major}.{v.Minor}.{v.Build}";
    }
    private static string ExpectedHostName() => System.Environment.MachineName.ToLowerInvariant();
    private static string ExpectedRelease()
    {
        var v = System.Environment.OSVersion.Version;
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }
    private static string ExpectedArch() =>
        System.Environment.Is64BitProcess ? "x86_64" : "i686";

    [SkippableFact]
    public void Uname_NoFlags_ReturnsKernelName()
    {
        // Default == -s.
        var lines = RunLines("Invoke-BashUname");
        Assert.Equal(new[] { ExpectedSysName() }, lines);
    }

    [SkippableFact]
    public void Uname_FlagS_ReturnsKernelName()
    {
        var lines = RunLines("Invoke-BashUname -s");
        Assert.Equal(new[] { ExpectedSysName() }, lines);
    }

    [SkippableFact]
    public void Uname_FlagN_ReturnsLowerHostName()
    {
        var lines = RunLines("Invoke-BashUname -n");
        Assert.Equal(new[] { ExpectedHostName() }, lines);
    }

    [SkippableFact]
    public void Uname_FlagR_ReturnsRelease()
    {
        var lines = RunLines("Invoke-BashUname -r");
        Assert.Equal(new[] { ExpectedRelease() }, lines);
    }

    [SkippableFact]
    public void Uname_FlagM_ReturnsMachineArch()
    {
        var lines = RunLines("Invoke-BashUname -m");
        Assert.Equal(new[] { ExpectedArch() }, lines);
    }

    [SkippableFact]
    public void Uname_FlagA_ReturnsAllJoined()
    {
        var expected = $"{ExpectedSysName()} {ExpectedHostName()} {ExpectedRelease()} {ExpectedArch()} MINGW64";
        var lines = RunLines("Invoke-BashUname -a");
        Assert.Equal(new[] { expected }, lines);
    }

    [SkippableFact]
    public void Uname_BundledFlags_SnrEmitsThreeFieldsInOrder()
    {
        // Oracle order is s, n, r, m regardless of bundle order.
        var expected = $"{ExpectedSysName()} {ExpectedHostName()} {ExpectedRelease()}";
        var lines = RunLines("Invoke-BashUname -snr");
        Assert.Equal(new[] { expected }, lines);
    }

    [SkippableFact]
    public void Uname_BundledFlags_AllShort_SameAsFlagA_ButWithoutMingw64Suffix()
    {
        // -snrm = s + n + r + m (no trailing "MINGW64" — that's -a only).
        var expected = $"{ExpectedSysName()} {ExpectedHostName()} {ExpectedRelease()} {ExpectedArch()}";
        var lines = RunLines("Invoke-BashUname -snrm");
        Assert.Equal(new[] { expected }, lines);
    }

    [SkippableFact]
    public void Uname_ViaAlias_ReturnsKernelName()
    {
        var lines = RunLines("uname");
        Assert.Equal(new[] { ExpectedSysName() }, lines);
    }

    [SkippableFact]
    public void Uname_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashUname --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("uname", System.StringComparison.OrdinalIgnoreCase));
    }

    // GNU: an unknown option or an operand is a usage error (status 1, hint line), never silently dropped.
    // REGRESSION: `uname -x` printed the kernel name with status 0.
    [Theory]
    [InlineData("Invoke-BashUname -x", "uname: invalid option -- 'x'")]
    [InlineData("Invoke-BashUname foo", "uname: extra operand")]
    [InlineData("Invoke-BashUname '-s;rm'", "uname: invalid option -- ';'")]
    public void Uname_UsageError_IsReportedWithStatus1(string script, string error)
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(), script).AssertFailed(1, error, "Try 'uname --help'");
        Assert.Equal("", r.Stdout.Trim());
    }

    // ---- injection probe (Directive 12) ----

    [Fact]
    public void Uname_ArgWithScriptblockChars_IsAnOperandNotExecuted()
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashUname '$(throw \"pwn\")'").AssertFailed(1, "uname: extra operand");
        Assert.DoesNotContain("pwn\"", r.Stdout);
    }

    // DIRECT calls (Pester / module users): -o -p -i collide ambiguously with common parameters and -v is -Verbose;
    // the decoys put them back. REGRESSION: "Parameter cannot be processed because the parameter name 'o' is
    // ambiguous", and -v was ignored.
    [Theory]
    [InlineData("-o", "Msys", "GNU/Linux")]
    [InlineData("-p", "unknown", null)]
    [InlineData("-i", "unknown", null)]
    [InlineData("-s -o", null, null)]
    [InlineData("-v", null, null)]
    public void Uname_CollidingFlag_DirectInvocation_Works(string flags, string? windows, string? linux)
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashUname {flags}").AssertSuccess();
        var line = r.Stdout.Trim();
        Assert.NotEqual("", line);
        if (OperatingSystem.IsWindows() && windows is not null) Assert.Equal(windows, line);
        if (OperatingSystem.IsLinux() && linux is not null) Assert.Equal(linux, line);
        if (flags == "-s -o") Assert.True(line.Split(' ').Length >= 2, line); // both fields, -s first
    }
    // ---- host kernel (Linux / macOS) ----

    private static readonly PsBash.Core.Platform.KernelIdentity.Uts Box =
        new("Linux", "box", "6.8.0-45-generic", "#45-Ubuntu SMP", "x86_64");

    // coreutils order s n r v m p i o; -a = all on Linux (p / i = machine: Debian/Ubuntu/Fedora builds), BSD's five
    // on macOS; no field = -s; a usage error is null (the cmdlet reports it).
    [Theory]
    [InlineData(false, "", "Linux")]
    [InlineData(false, "-s", "Linux")]
    [InlineData(false, "-rn", "box 6.8.0-45-generic")]
    [InlineData(false, "-m -v", "#45-Ubuntu SMP x86_64")]
    [InlineData(false, "-o", "GNU/Linux")]
    [InlineData(false, "-a", "Linux box 6.8.0-45-generic #45-Ubuntu SMP x86_64 x86_64 x86_64 GNU/Linux")]
    [InlineData(false, "--kernel-release --machine", "6.8.0-45-generic x86_64")]
    [InlineData(true, "-a", "Linux box 6.8.0-45-generic #45-Ubuntu SMP x86_64")]
    [InlineData(false, "-x", null)]
    [InlineData(false, "extra", null)]
    [InlineData(false, "-v -s", "Linux #45-Ubuntu SMP")]
    [InlineData(false, "--operating", "GNU/Linux")]
    public void FormatHost_IsCoreutilsUname(bool darwin, string args, string? expected)
        => Assert.Equal(expected, InvokeBashUnameCommand.FormatHost(Box, darwin,
            args.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    // REGRESSION: on Linux/macOS uname printed MINGW64_NT-<.NET version>.
    [SkippableFact]
    [Trait("Platform", "Unix")]
    public void Uname_OnUnix_IsTheHostKernel()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows keeps the MINGW identity");
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript("Invoke-BashUname").Invoke();
        Assert.Equal(new[] { OperatingSystem.IsMacOS() ? "Darwin" : "Linux" }, result.Select(o => o?.ToString()).ToArray());
    }
}