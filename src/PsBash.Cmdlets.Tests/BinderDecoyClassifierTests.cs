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
    // cp / mv interactive + copy-as-is (rm -i/-d are implemented now: see RmInteractiveTests)
    [InlineData("Invoke-BashMv -i a b")]
    // cat show-all / show-nonprinting
    // tee diagnose-write-errors
    [InlineData("'x' | Invoke-BashTee -p out.txt")]
    // column output width (-o is implemented now: a value-bearing decoy)
    // split elide-empty / line-bytes
    [InlineData("'x' | Invoke-BashSplit -e")]
    [InlineData("'x' | Invoke-BashSplit -C")]
    // strings encoding (-a/--all is accepted now: scanning the whole input is what strings does)
    [InlineData("'x' | Invoke-BashStrings -e")]
    // tree colorize / permissions
    [InlineData("Invoke-BashTree -p")]
    [InlineData("Invoke-BashTree -C")]
    // head / tail verbose
    // grep directories / devices
    // du no-dereference (bare -P prefix-collides with -ProgressAction)
    [InlineData("Invoke-BashDu -P .")]
    public void CollidingClassifierFlag_FiresExit2_WithoutBinderCrash(string script)
    {
        var (err, exit) = Run(script);
        Assert.Equal(2, exit);
        Assert.Contains(err, m => m.Contains("recognized but not supported", StringComparison.Ordinal));
    }
}
