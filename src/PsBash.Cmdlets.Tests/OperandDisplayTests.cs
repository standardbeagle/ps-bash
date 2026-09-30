using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// GNU quotes the operand AS TYPED in every rm/cp/mv diagnostic (<c>rm: cannot remove 'nosuch'</c>,
/// <c>cp: 's' and './s' are the same file</c>), never the resolved full path. Each case runs from
/// inside a scratch directory with RELATIVE operands, so a resolved path in a message would carry
/// the (long, unique) scratch prefix and fail the exact-text assertion. Expected texts were checked
/// against GNU coreutils 9.4 (<c>wsl bash</c>); the FsState differential cases pin the same
/// scenarios against the live oracle.
/// </summary>
public class OperandDisplayTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    public OperandDisplayTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), $"psb-disp-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Runs <paramref name="body"/> with the scratch directory as the PowerShell location.</summary>
    private CmdResult InTmp(string body) => CmdResult.Run(_fixture.AcquireFresh(),
        $"Push-Location '{_tmp}'; try {{ {body} }} finally {{ Pop-Location }}");

    private void Write(string rel, string content = "x\n")
    {
        var p = Path.Combine(_tmp, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private static void AssertStderr(CmdResult r, string expected)
    {
        r.AssertFailed(1, expected);
        Assert.DoesNotContain(Path.GetTempPath(), r.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rm_MissingOperand_QuotesOperandAsTyped() =>
        AssertStderr(InTmp("Invoke-BashRm nosuch"), "rm: cannot remove 'nosuch': No such file or directory");

    [Fact]
    public void Rm_MissingOperandWithDotSlash_KeepsTheDotSlash() =>
        AssertStderr(InTmp("Invoke-BashRm ./nosuch"), "rm: cannot remove './nosuch': No such file or directory");

    [Fact]
    public void Rm_MissingNestedOperand_QuotesWholeTypedPath() =>
        AssertStderr(InTmp("Invoke-BashRm -r nosuch/x"), "rm: cannot remove 'nosuch/x': No such file or directory");

    [Fact]
    public void Rm_DirectoryWithoutR_QuotesOperandAsTyped()
    {
        Write("d1/f");
        AssertStderr(InTmp("Invoke-BashRm d1"), "rm: cannot remove 'd1': Is a directory");
    }

    [Fact]
    public void Rm_WildcardMatch_DisplaysTheExpandedRelativeName()
    {
        Write("subdir1/f");
        AssertStderr(InTmp("Invoke-BashRm sub*"), "rm: cannot remove 'subdir1': Is a directory");
    }

    [Fact]
    public void Rm_UnmatchedWildcard_DisplaysThePatternLiterally() =>
        AssertStderr(InTmp("Invoke-BashRm '*.zz'"), "rm: cannot remove '*.zz': No such file or directory");

    [Fact]
    public void Rm_RecursiveVerbose_NamesChildrenUnderTheTypedOperand()
    {
        Write("d/f");
        var r = InTmp("Invoke-BashRm -rv d").AssertSuccess();
        Assert.Contains("removed 'd/f'", r.Stdout);
        Assert.DoesNotContain(Path.GetTempPath(), r.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cp_MissingSource_QuotesOperandAsTyped() =>
        AssertStderr(InTmp("Invoke-BashCp ./nosuch dst"), "cp: cannot stat './nosuch': No such file or directory");

    [Fact]
    public void Cp_DirectoryWithoutR_QuotesOperandAsTyped()
    {
        Write("d1/f");
        AssertStderr(InTmp("Invoke-BashCp d1 dst"), "cp: -r not specified; omitting directory 'd1'");
    }

    [Fact]
    public void Cp_SameFileViaDotSlash_QuotesBothAsTyped()
    {
        Write("s");
        AssertStderr(InTmp("Invoke-BashCp s ./s"), "cp: 's' and './s' are the same file");
    }

    [Fact]
    public void Cp_SameFileViaDotDestination_ReportsDestinationJoinedWithBasename()
    {
        Write("s");
        AssertStderr(InTmp("Invoke-BashCp s ."), "cp: 's' and './s' are the same file");
    }

    [Fact]
    public void Cp_MissingDestinationParent_ErrorsWithTheTypedDestinationAndCreatesNothing()
    {
        Write("s");
        var r = InTmp("Invoke-BashCp s nodir/x");
        AssertStderr(r, "cp: cannot create regular file 'nodir/x': No such file or directory");
        Assert.False(Directory.Exists(Path.Combine(_tmp, "nodir")));
    }

    [Fact]
    public void Cp_SeveralSourcesIntoMissingDirectory_ReportsTypedTarget()
    {
        Write("a");
        Write("b");
        AssertStderr(InTmp("Invoke-BashCp a b result"), "cp: target 'result': No such file or directory");
    }

    [Fact]
    public void Cp_Verbose_ReportsTypedNames()
    {
        Write("s");
        Directory.CreateDirectory(Path.Combine(_tmp, "d"));
        var r = InTmp("Invoke-BashCp -v s d/").AssertSuccess();
        Assert.Equal("'s' -> 'd/s'", r.Stdout.Trim());
    }

    [Fact]
    public void Mv_MissingSource_QuotesOperandAsTyped() =>
        AssertStderr(InTmp("Invoke-BashMv nosuch dst"), "mv: cannot stat 'nosuch': No such file or directory");

    [Fact]
    public void Mv_SameFileViaDotSlash_QuotesBothAsTyped()
    {
        Write("s");
        AssertStderr(InTmp("Invoke-BashMv s ./s"), "mv: 's' and './s' are the same file");
    }

    [Fact]
    public void Mv_DirectoryIntoItself_QuotesBothAsTyped()
    {
        Write("d/f");
        AssertStderr(InTmp("Invoke-BashMv d d/x"), "mv: cannot move 'd' to a subdirectory of itself, 'd/x'");
    }

    [Fact]
    public void Mv_MissingDestinationParent_ErrorsWithTheTypedNames()
    {
        Write("s");
        AssertStderr(InTmp("Invoke-BashMv s nodir/x"), "mv: cannot move 's' to 'nodir/x': No such file or directory");
        Assert.True(File.Exists(Path.Combine(_tmp, "s")));
    }

    [Fact]
    public void Mv_Verbose_ReportsTypedNames()
    {
        Write("s");
        Directory.CreateDirectory(Path.Combine(_tmp, "d"));
        var r = InTmp("Invoke-BashMv -v s d").AssertSuccess();
        Assert.Equal("'s' -> 'd/s'", r.Stdout.Trim());
    }
}
