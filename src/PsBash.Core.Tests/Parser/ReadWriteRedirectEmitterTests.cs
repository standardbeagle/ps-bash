using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// <c>N&lt;&gt; f</c> opens f for reading and writing: created when missing, never truncated (bash 5.2). Emitter shape only;
/// the behavior is Differential <c>RedirectOperatorDifferentialTests.ReadWriteOpen_OnStderrAndOnAGroup</c>.
/// </summary>
public class ReadWriteRedirectEmitterTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    // REGRESSION: `cmd 2<> f` was emitted as a comment and dropped (stderr stayed on the terminal, f was never created).
    [Theory]
    [InlineData("ls x 2<> e")]
    [InlineData("echo XY 2<> e >&2")]   // the stdout-to-stderr path (TryEmitStderrRedirect) takes it as an output target
    public void Transpile_StderrReadWrite_OpensTheErrorTargetWithoutTruncating(string bash)
    {
        var ps = T(bash);

        Assert.Contains("ReadWrite", ps);
        Assert.DoesNotContain("input redirect ignored", ps);
    }

    [Fact]
    public void Transpile_StderrReadWrite_IsTheErrorPathsOwnSwitch()
        => Assert.Contains("-ErrorPath e -ErrorReadWrite", T("ls x 2<> e"));

    // REGRESSION: `cat 0<> /nodir/f` reported the open twice (the read-write open, then the read).
    [Fact]
    public void Transpile_StdinReadWrite_ReadsOnlyWhenTheOpenWorked()
    {
        var ps = T("cat 0<> f");

        Assert.Contains(PsBuild.ReadWriteInputOpen("f", "").Split(" { ")[0], ps);
        Assert.Contains("if ($global:LASTEXITCODE -eq 0) { ", ps);
    }

    // REGRESSION: `{ ...; } 0<> new` did not create the file (it was read as `< new`, which fails).
    [Theory]
    [InlineData("{ cat; } 0<> newc")]
    [InlineData("while read -r l; do echo \"$l\"; done 0<> newc")]
    public void Transpile_CompoundStdinReadWrite_CreatesTheFileFirst(string bash)
    {
        var ps = T(bash);

        Assert.Contains("@() | Invoke-BashRedirect -Path newc -Append", ps);
        Assert.Contains("if ($global:LASTEXITCODE -eq 0) { ", ps);
    }
}
