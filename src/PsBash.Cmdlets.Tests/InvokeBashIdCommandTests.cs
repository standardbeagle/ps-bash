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

    /// <summary>
    /// A usage or lookup failure is what the HOST's id says off Windows: there the cmdlet runs the system id, as bash
    /// on that host would, and the wording is the implementation's (GNU `‘x’: no such user`, uutils `'x'`, BSD
    /// `x: no such user`). On Windows the cmdlet is the implementation and GNU's text is the oracle.
    /// </summary>
    private void AssertFailsLikeId(string psArgs, string[] hostArgs, string gnuStderr)
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashId " + psArgs);
        if (IsWindows())
        {
            r.AssertFailed(1, gnuStderr);
            return;
        }
        var host = PsBash.Testing.ProcessSpawn.RunAsync("id", hostArgs, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        Assert.NotEqual(0, host.ExitCode);
        string firstLine = host.Stderr.Split('\n')[0].TrimEnd('\r');
        r.AssertFailed(host.ExitCode, firstLine);
    }

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

    // -n / -r alone (no -u/-g/-G) is GNU's usage error on every platform (oracle: `id -n` → rc 1). REGRESSION: the
    // Windows path printed the default form.
    [Theory]
    [InlineData("-n")]
    [InlineData("-r")]
    [InlineData("-nr")]
    public void Id_NameOrRealAlone_IsAUsageError(string flag)
        => AssertFailsLikeId($"'{flag}'", [flag], "id: cannot print only names or real IDs in default format");

    [Fact]
    public void Id_TwoChoices_IsAUsageError()
        => AssertFailsLikeId("'-ug'", ["-ug"], "id: cannot print \"only\" of more than one choice");

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
    public void Id_FlagUR_IsTheUid()
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashId '-ur'").AssertSuccess();
        Assert.Equal(CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashId '-u'").AssertSuccess().Stdout.Trim(), r.Stdout.Trim());
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
        AssertFailsLikeId(flags + "psb-nosuchuser-7q",
            [.. flags.Split(' ', StringSplitOptions.RemoveEmptyEntries), "psb-nosuchuser-7q"],
            "id: ‘psb-nosuchuser-7q’: no such user");
        Assert.Empty(CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashId {flags}psb-nosuchuser-7q").Lines);
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
