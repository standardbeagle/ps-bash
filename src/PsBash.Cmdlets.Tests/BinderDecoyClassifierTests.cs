using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Regression tests for the binder-collision cluster (2026-07 review). Each of these
/// short flags previously either hard-crashed PowerShell's case-insensitive binder
/// ("ambiguous") or silently bound a common parameter (-Verbose/-Debug/-Confirm/
/// -Arguments) BEFORE the cmdlet's valid-but-unsupported classifier could run — so
/// the intended exit-2 "recognized but not supported" message was impossible.
///
/// Each is now a declared decoy [Parameter] that is re-injected into the arg stream,
/// so the classifier fires. A passing Invoke (no thrown binder exception) also proves
/// the crash is gone. Guarded mechanically by CommonParameterCollisionGuardTests.
///
/// Oracle note (qa-rubric Directive 1): ps-bash-specific (the exit-2 classifier
/// surface has no bash equivalent), hand-asserted per the exception list.
/// </summary>
public class BinderDecoyClassifierTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public BinderDecoyClassifierTests(SharedPwshFixture fixture) => _fixture = fixture;

    private (string[] Err, int Exit) Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        pwsh.AddScript(script).Invoke();
        var errs = pwsh.Streams.Error.Select(e => e.Exception?.Message ?? e.ToString()).ToArray();
        pwsh.Commands.Clear();
        var exitObj = pwsh.AddScript("$global:LASTEXITCODE").Invoke();
        pwsh.Commands.Clear();
        int exit = exitObj.Count > 0 && exitObj[0]?.BaseObject is int e ? e : 0;
        return (errs, exit);
    }

    [Theory]
    // cp / mv / rm interactive + copy-as-is (the "impossible under -f" cases)
    [InlineData("Invoke-BashCp -i a b")]
    [InlineData("Invoke-BashCp -d a b")]
    [InlineData("Invoke-BashMv -i a b")]
    [InlineData("Invoke-BashRm -i x")]
    [InlineData("Invoke-BashRm -f -i x")]   // classifier fires EVEN under -f (GNU parity)
    [InlineData("Invoke-BashRm -d x")]
    // cat show-all / show-nonprinting
    [InlineData("'x' | Invoke-BashCat -A")]
    [InlineData("'x' | Invoke-BashCat -v")]
    // tee diagnose-write-errors
    [InlineData("'x' | Invoke-BashTee -p out.txt")]
    // column output width / separator
    [InlineData("'a b' | Invoke-BashColumn -o")]
    [InlineData("'a b' | Invoke-BashColumn -c")]
    // split elide-empty / line-bytes
    [InlineData("'x' | Invoke-BashSplit -e")]
    [InlineData("'x' | Invoke-BashSplit -C")]
    // strings all / encoding
    [InlineData("'x' | Invoke-BashStrings -a")]
    [InlineData("'x' | Invoke-BashStrings -e")]
    // tree colorize / permissions
    [InlineData("Invoke-BashTree -p")]
    [InlineData("Invoke-BashTree -C")]
    // head / tail verbose
    [InlineData("'x' | Invoke-BashHead -v")]
    [InlineData("'x' | Invoke-BashTail -v")]
    // grep directories / devices
    [InlineData("'x' | Invoke-BashGrep -d skip")]
    // sort ignore-nonprinting
    [InlineData("'b','a' | Invoke-BashSort -i")]
    public void CollidingClassifierFlag_FiresExit2_WithoutBinderCrash(string script)
    {
        var (err, exit) = Run(script);
        Assert.Equal(2, exit);
        Assert.Contains(err, m => m.Contains("recognized but not supported", StringComparison.Ordinal));
    }

    [Fact]
    public void Du_DashP_NoBinderCrash_RunsNormally()
    {
        // du silently swallows unknown short flags (oracle behavior); the P decoy just
        // prevents the -ProgressAction binder crash. du -P . computes the size normally.
        var (_, exit) = Run("Invoke-BashDu -P .");
        Assert.Equal(0, exit);
    }
}
