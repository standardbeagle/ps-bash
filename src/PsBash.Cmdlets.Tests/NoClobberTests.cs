using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>mv -n</c> / <c>cp -n</c> as GNU coreutils 9.4 behaves (WSL Ubuntu 24.04, checked case by case):
/// <list type="bullet">
/// <item>mv -n on an existing destination says <c>mv: not replacing 'DEST'</c> (DEST as typed, or
/// <c>d/name</c> inside a directory operand), leaves both files and exits 1 — even for the same file;
/// the other sources of a multi-source mv still move. Among <c>-f</c> / <c>-n</c> the LAST one wins.</item>
/// <item>cp -n silently skips an existing destination and exits 0, but prints
/// <c>cp: warning: behavior of -n is non-portable and may change in future; use --update=none
/// instead</c> once per invocation, before any other diagnostic. <c>-n</c> wins over <c>-f</c> in
/// either order.</item>
/// </list>
/// </summary>
public class NoClobberTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private const string CpWarning =
        "cp: warning: behavior of -n is non-portable and may change in future; use --update=none instead";

    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    public NoClobberTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), "psb-nc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private void Write(string rel, string content)
    {
        var p = Path.Combine(_tmp, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private string Read(string rel) => File.ReadAllText(Path.Combine(_tmp, rel));

    private CmdResult InTmp(string body) => CmdResult.Run(_fixture.AcquireFresh(),
        $"Push-Location '{_tmp}'; try {{ {body} }} finally {{ Pop-Location }}");

    [Fact]
    public void Mv_N_ExistingDestination_NotReplacing_Exit1()
    {
        Write("a", "A\n"); Write("b", "B\n");
        InTmp("Invoke-BashMv -n a b").AssertFailed(1, "mv: not replacing 'b'");
        Assert.Equal("A\n", Read("a"));
        Assert.Equal("B\n", Read("b"));
    }

    [Fact]
    public void Mv_N_SameFile_NotReplacing_NotSameFile()
    {
        Write("a", "A\n");
        var r = InTmp("Invoke-BashMv -n a a").AssertFailed(1, "mv: not replacing 'a'");
        Assert.DoesNotContain("same file", r.Stderr);
    }

    [Fact]
    public void Mv_N_DestinationInsideDirectory_NamesDirSlashName()
    {
        Write("a", "A\n"); Write("d/a", "D\n");
        InTmp("Invoke-BashMv -n a d").AssertFailed(1, "mv: not replacing 'd/a'");
        Assert.Equal("D\n", Read("d/a"));
        Assert.True(File.Exists(Path.Combine(_tmp, "a")));
    }

    [Fact]
    public void Mv_N_MultiSource_SkipsOnlyTheConflict()
    {
        Write("e", "E\n"); Write("f", "F\n"); Write("m2/e", "X\n");
        InTmp("Invoke-BashMv -n e f m2").AssertFailed(1, "mv: not replacing 'm2/e'");
        Assert.Equal("X\n", Read("m2/e"));
        Assert.Equal("F\n", Read("m2/f"));
        Assert.True(File.Exists(Path.Combine(_tmp, "e")));
        Assert.False(File.Exists(Path.Combine(_tmp, "f")));
    }

    [Fact]
    public void Mv_N_NewDestination_Moves()
    {
        Write("a", "A\n");
        InTmp("Invoke-BashMv -n a c").AssertSuccess();
        Assert.Equal("A\n", Read("c"));
    }

    [Fact]
    public void Mv_LastOfForceAndNoClobberWins()
    {
        Write("a", "A\n"); Write("b", "B\n");
        InTmp("Invoke-BashMv -f -n a b").AssertFailed(1, "mv: not replacing 'b'");
        Assert.Equal("B\n", Read("b"));

        InTmp("Invoke-BashMv -n -f a b").AssertSuccess();
        Assert.Equal("A\n", Read("b"));
    }

    [Fact]
    public void Cp_N_ExistingDestination_SkipsSilentlyExit0_WithWarning()
    {
        Write("a", "A\n"); Write("b", "B\n");
        var r = InTmp("Invoke-BashCp -n a b");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(CpWarning, r.Stderr);
        Assert.Equal("B\n", Read("b"));
    }

    [Fact]
    public void Cp_N_WarningComesBeforeOtherDiagnostics()
    {
        var r = InTmp("Invoke-BashCp -n nosuch b");
        r.AssertFailed(1, CpWarning, "cp: cannot stat 'nosuch': No such file or directory");
        Assert.True(r.Stderr.IndexOf(CpWarning, StringComparison.Ordinal)
                    < r.Stderr.IndexOf("cannot stat", StringComparison.Ordinal));
    }

    [Fact]
    public void Cp_N_Wins_OverForce_InEitherOrder()
    {
        Write("a", "A\n"); Write("b", "B\n");
        InTmp("Invoke-BashCp -n -f a b");
        Assert.Equal("B\n", Read("b"));
        InTmp("Invoke-BashCp -f -n a b");
        Assert.Equal("B\n", Read("b"));
    }

    [Fact]
    public void Cp_WithoutN_NoWarning()
    {
        Write("a", "A\n");
        InTmp("Invoke-BashCp a c").AssertSuccess();
    }
}
