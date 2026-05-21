using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 follow-on migration of
/// <c>Invoke-BashTput</c> from a psm1 script function to a binary cmdlet
/// (<see cref="InvokeBashTputCommand"/>).
///
/// Oracle: the original psm1 function. The psm1 oracle first tried the on-disk
/// <c>tput</c> binary and emitted its stdout when available; otherwise it fell
/// back to a switch over a small set of capability tokens
/// (<c>cols</c> / <c>lines</c> / <c>clear</c> / <c>bold</c> / <c>sgr0</c> /
/// <c>setaf N</c>). The tests below exercise the fallback path because the
/// native binary's behavior is platform-dependent and not what this migration
/// is replacing — the fallback is the one the cmdlet owns end-to-end.
///
/// Directive-3 axes exercised: empty input (no operands), missing-target
/// (unknown capability), unicode (alias resolution byte stream), platform
/// (test class declares no Trait so it runs everywhere — the fallback path
/// touches only ASCII and System.Console / RawUI fallback chain).
/// Directive 12: injection probe asserts a <c>$()</c>-laden operand cannot
/// trigger PS evaluation.
/// </summary>
public class InvokeBashTputCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashTputCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    private string[] RunLines(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    /// <summary>
    /// True when the cmdlet's native-passthrough path is active: a real
    /// <c>tput</c> binary on PATH on a non-Windows host (mirrors
    /// <see cref="InvokeBashTputCommand"/>'s native gate). In that case the
    /// terminfo database — not the in-process emulator — produces the bytes,
    /// so the fallback-byte assertions below do not apply (e.g. ncurses emits
    /// <c>\e(B\e[m</c> for <c>sgr0</c> and the 16-color <c>\e[34m</c> for
    /// <c>setaf 4</c> under the default TERM). These tests verify the emulator,
    /// which is only reached when no native tput exists.
    /// </summary>
    private static bool NativeTputActive()
    {
        if (OperatingSystem.IsWindows()) return false;
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            try { if (File.Exists(Path.Combine(dir, "tput"))) return true; }
            catch { /* malformed PATH entry — ignore */ }
        }
        return false;
    }

    [Fact]
    public void Tput_Cols_ReturnsPositiveIntegerString()
    {
        // The fallback path returns the host's window width as a base-10 int
        // string. We can't guarantee a specific value, but it must be a
        // positive integer and exactly one emit.
        var lines = RunLines("Invoke-BashTput cols");
        Assert.Single(lines);
        Assert.True(int.TryParse(lines[0], out var w) && w > 0,
            $"expected positive int width, got [{lines[0]}]");
    }

    [Fact]
    public void Tput_Lines_ReturnsPositiveIntegerString()
    {
        var lines = RunLines("Invoke-BashTput lines");
        Assert.Single(lines);
        Assert.True(int.TryParse(lines[0], out var h) && h > 0,
            $"expected positive int height, got [{lines[0]}]");
    }

    [Fact]
    public void Tput_Bold_EmitsAnsiSgrBoldEscape()
    {
        var lines = RunLines("Invoke-BashTput bold");
        Assert.Single(lines);
        Assert.Equal("\x1B[1m", lines[0]);
    }

    [SkippableFact]
    public void Tput_Sgr0_EmitsAnsiSgrResetEscape()
    {
        Skip.If(NativeTputActive(),
            "native tput on PATH overrides the in-process emulator (ncurses emits \\e(B\\e[m for sgr0)");
        var lines = RunLines("Invoke-BashTput sgr0");
        Assert.Single(lines);
        Assert.Equal("\x1B[0m", lines[0]);
    }

    [SkippableFact]
    public void Tput_Setaf_EmitsAnsi256ColorEscape()
    {
        Skip.If(NativeTputActive(),
            "native tput on PATH overrides the in-process emulator (ncurses emits the 16-color \\e[34m for setaf 4)");
        var lines = RunLines("Invoke-BashTput setaf 4");
        Assert.Single(lines);
        Assert.Equal("\x1B[38;5;4m", lines[0]);
    }

    [Fact]
    public void Tput_NoArgs_EmitsNothing()
    {
        // Failure axis: empty input. Oracle's switch on $Arguments[0] when
        // $Arguments is empty matches the default arm (empty string) and
        // emits nothing.
        var lines = RunLines("Invoke-BashTput");
        Assert.Empty(lines);
    }

    [Fact]
    public void Tput_UnknownCapability_EmitsNothing()
    {
        // Failure axis: missing target / unknown capability. Oracle's default
        // arm produces '' which is filtered out before Emit-BashLine.
        var lines = RunLines("Invoke-BashTput nonexistent-cap-xyz");
        // On a host with native `tput` on PATH, the binary itself may emit
        // something on stderr but stdout will be empty and exit non-zero, so
        // the cmdlet falls through to the in-process fallback which also
        // emits nothing. Either way, stdout must have no records.
        Assert.Empty(lines);
    }

    [Fact]
    public void Tput_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashTput --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("tput", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Tput_ViaAlias_ResolvesToCmdlet()
    {
        // The psm1 Set-Alias `tput -> Invoke-BashTput` must still resolve to
        // the cmdlet now that the psm1 function is gone.
        var lines = RunLines("tput bold");
        Assert.Single(lines);
        Assert.Equal("\x1B[1m", lines[0]);
    }

    [Fact]
    public void Tput_InjectionProbe_DoesNotExecutePayload(/* Directive 12 */)
    {
        // Operand contains PS scriptblock + $() chars. The cmdlet binds the
        // operand positionally and either passes it to a child Process via
        // ArgumentList (no shell) or matches it against a fixed string set
        // in the fallback switch. Neither path may re-parse it as code.
        var lines = RunLines("Invoke-BashTput '$(throw \"pwn\")'");
        Assert.DoesNotContain(lines, l => l.Contains("pwn"));
    }
}
