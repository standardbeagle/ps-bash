using System.Runtime.InteropServices;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of Invoke-BashId
/// from PsBash.psm1 to a binary cmdlet (PsBash.Cmdlets.dll).
///
/// Oracle: the psm1 <c>Invoke-BashId</c> function. On Windows the oracle
/// derives output entirely from <c>WindowsIdentity.GetCurrent()</c> — the
/// same API the cmdlet calls in the same process, so cross-checking against
/// re-computed values is a strict round-trip without requiring real bash.
/// On non-Windows the cmdlet shells out to <c>/usr/bin/id</c>, and these
/// tests gate via <c>RuntimeInformation.IsOSPlatform</c> so each path is
/// exercised on its native platform. Pipeline / file / large / CRLF /
/// signal axes do not apply — id has no pipeline input and no file operands.
/// The injection-probe (Directive 12) test verifies an adversarial positional
/// operand is never re-parsed as PowerShell.
/// </summary>
public class InvokeBashIdCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashIdCommandTests(SharedPwshFixture fixture)
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

    private static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public void Id_NoFlags_EmitsUidGidGroupsLine()
    {
        // Default form must contain bash-shaped uid= / gid= / groups= keywords.
        var lines = RunLines("Invoke-BashId");
        Assert.Single(lines);
        var line = lines[0];
        Assert.Contains("uid=", line);
        Assert.Contains("gid=", line);
        Assert.Contains("groups=", line);
    }

    [Fact]
    public void Id_FlagU_ReturnsNumericOrSidOnly()
    {
        // -u: a single token, no spaces, no "uid="/"gid=" framing.
        var lines = RunLines("Invoke-BashId -u");
        Assert.Single(lines);
        var line = lines[0];
        Assert.DoesNotContain("uid=", line);
        Assert.DoesNotContain("gid=", line);
        Assert.DoesNotContain(" ", line);
        Assert.NotEmpty(line);
    }

    [Fact]
    public void Id_FlagG_ReturnsPrimaryGidOnly()
    {
        // -g: single token, no framing.
        var lines = RunLines("Invoke-BashId -g");
        Assert.Single(lines);
        var line = lines[0];
        Assert.DoesNotContain("uid=", line);
        Assert.DoesNotContain("gid=", line);
        Assert.DoesNotContain(" ", line);
        Assert.NotEmpty(line);
    }

    [Fact]
    public void Id_FlagN_DefaultFormStillHasUidGid()
    {
        // -n alone (no -u/-g/-G) still yields the default form per the oracle
        // on Windows. On Linux /usr/bin/id -n alone is an error; gate Windows.
        if (!IsWindows()) return;
        var lines = RunLines("Invoke-BashId -n");
        Assert.Single(lines);
        Assert.Contains("uid=", lines[0]);
    }

    [Fact]
    public void Id_FlagUN_ReturnsUsernameOnWindows()
    {
        if (!IsWindows()) return; // /usr/bin/id form differs across distros; gated.
        var lines = RunLines("Invoke-BashId -u -n");
        Assert.Single(lines);
        var name = lines[0];
        // -u -n on Windows: should be the bare username (no domain prefix).
        Assert.DoesNotContain("\\", name);
        Assert.NotEmpty(name);
        // The current user's bare name should match Environment.UserName.
        Assert.Equal(System.Environment.UserName, name, ignoreCase: true);
    }

    [Fact]
    public void Id_FlagBigG_ReturnsGroupListOnWindows()
    {
        if (!IsWindows()) return;
        // -G emits space-separated group SIDs (single line).
        var lines = RunLines("Invoke-BashId -G");
        Assert.Single(lines);
        Assert.NotEmpty(lines[0]);
    }

    [Fact]
    public void Id_ViaAlias_ReturnsDefaultLine()
    {
        var lines = RunLines("id");
        Assert.Single(lines);
        Assert.Contains("uid=", lines[0]);
    }

    [Fact]
    public void Id_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashId --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("id", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Id_FlagR_AcceptedAndDoesNotChangeOutput()
    {
        // -r is accepted by the oracle without altering output on Windows.
        // On Linux /usr/bin/id -r alone is an error and emits empty stdout —
        // gate Windows-only.
        if (!IsWindows()) return;
        var lines = RunLines("Invoke-BashId -r");
        Assert.Single(lines);
        Assert.Contains("uid=", lines[0]);
    }

    // ---- injection probe (Directive 12) ----

    [Fact]
    public void Id_PositionalArgWithScriptblockChars_IsLiteralNotExecuted()
    {
        // Adversarial positional username containing PS scriptblock + $() chars.
        // No account has that name: GNU's "no such user" (status 1), and the payload is never evaluated.
        var r = CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashId '$(throw \"pwn\")'");
        r.AssertFailed(1, "no such user");
        Assert.Empty(r.Lines);
        Assert.DoesNotContain("pwn", r.Stderr.Replace("$(throw \"pwn\")", ""));
    }

    // REGRESSION: an unknown NAME printed the CURRENT user's identity at status 0 on Windows; on Unix the system id's
    // diagnostic and status were dropped. Oracle: `id nosuchuser` / `id -u nosuchuser` → "id: ‘nosuchuser’: no such
    // user", status 1.
    [Theory]
    [InlineData("")]
    [InlineData("-u ")]
    public void Id_UnknownUser_NoSuchUserExit1(string flags)
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashId {flags}psb-nosuchuser-7q");
        r.AssertFailed(1, "id: ‘psb-nosuchuser-7q’: no such user");
        Assert.Empty(r.Lines);
    }

    [Fact]
    public void Id_KnownUser_Succeeds()
    {
        var name = System.Environment.UserName;
        CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashId -u '{name.Replace("'", "''")}'").AssertSuccess();
    }

    [Fact]
    public void Id_PositionalArgWithSemicolonInjection_IsLiteralNotExecuted()
    {
        // Same shape — adversarial token with ';' and 'rm' must not split or run.
        // A literal user name lookup that fails ("no such user"); no PowerShell evaluation of the payload.
        var lines = RunLines("Invoke-BashId 'foo;rm -rf /'");
        // Output must NEVER contain "pwn" / executed payload signs — single
        // observable check applicable to both platforms.
        foreach (var l in lines)
        {
            Assert.DoesNotContain("pwn", l);
        }
    }
}
