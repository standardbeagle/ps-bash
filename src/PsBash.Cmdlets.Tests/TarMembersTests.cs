using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// tar member selection, <c>-T</c>/<c>-X</c> lists, default stdin/stdout archive. Expectations (wording, exit codes) were
/// read from GNU tar 1.35 (<c>wsl bash</c>); stdout/tree parity against it is <c>TarMembersDifferentialTests</c>, this
/// class pins what the oracle harness does not compare (stderr text, the pipeline byte path, the pure matchers).
/// </summary>
public class TarMembersTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    public TarMembersTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), $"psb-tarm-{Guid.NewGuid():N}".Substring(0, 23));
        Directory.CreateDirectory(_tmp);
        Write("d/a.txt", "1"); Write("d/sub/b.txt", "2"); Write("d/other/c.log", "3"); Write("e/f.txt", "4"); Write("t.txt", "top");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    private CmdResult InTmp(string body) => CmdResult.Run(_fixture.AcquireFresh(),
        $"Push-Location '{_tmp}'; try {{ {body} }} finally {{ Pop-Location }}");

    private void Write(string rel, string content)
    {
        var p = Path.Combine(_tmp, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private string Read(string rel) => File.ReadAllText(Path.Combine(_tmp, rel));
    private bool Exists(string rel) => File.Exists(Path.Combine(_tmp, rel)) || Directory.Exists(Path.Combine(_tmp, rel));

    private static string[] Names(CmdResult r) => r.Lines.Select(l => l.TrimEnd('\r', '\n')).Where(l => l.Length > 0).OrderBy(l => l, StringComparer.Ordinal).ToArray();

    private void MakeArchive() => InTmp("Invoke-BashTar '-cf' a.tar d e t.txt").AssertSuccess();

    // ───────────── TarGlob (pure) ─────────────

    [Theory]
    [InlineData("*.txt", "a.txt", true)]
    [InlineData("*.txt", "d/a.txt", true)]            // * crosses /
    [InlineData("d/?.txt", "d/a.txt", true)]
    [InlineData("d/?.txt", "d/ab.txt", false)]
    [InlineData("d/[a-b].txt", "d/b.txt", true)]
    [InlineData("d/[a-b].txt", "d/c.txt", false)]
    [InlineData("d/[!a-b].txt", "d/c.txt", true)]
    [InlineData("d/[^a-b].txt", "d/a.txt", false)]
    [InlineData("a\\*b", "a*b", true)]
    [InlineData("a\\*b", "axb", false)]
    [InlineData("a[", "a[", true)]                    // unterminated [ is literal
    [InlineData("*", "", true)]
    [InlineData("", "", true)]
    [InlineData("", "x", false)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("[]]", "]", true)]
    [InlineData("[a-]", "-", true)]
    public void Glob_IsFnmatchWithStarCrossingSlash(string pattern, string text, bool expected)
    {
        Assert.Equal(expected, TarGlob.IsMatch(pattern, text));
    }

    // ───────────── TarMemberFilter (pure) ─────────────

    private static TarMemberFilter Filter(string[] names, bool wild = false, params string[] excludes) =>
        new(names.Select(n => (n, wild)), excludes);

    [Fact]
    public void Filter_NoNames_SelectsEverything()
    {
        var f = Filter(Array.Empty<string>());
        Assert.True(f.Selects("anything/at/all"));
        Assert.False(f.HasNames);
    }

    [Theory]
    [InlineData("d", "d/", true)]
    [InlineData("d", "d/a.txt", true)]
    [InlineData("d/", "d/a.txt", true)]               // trailing slash on the name is ignored
    [InlineData("d/su", "d/sub/b.txt", false)]        // a name is whole components, not a prefix of text
    [InlineData("d/sub", "d/sub/", true)]
    [InlineData("./t.txt", "t.txt", false)]           // no ./ normalisation
    [InlineData("t.txt", "t.txt", true)]
    public void Filter_PlainName(string name, string member, bool expected)
    {
        Assert.Equal(expected, Filter(new[] { name }).Selects(member));
    }

    [Theory]
    [InlineData("d/su?", "d/sub/", true)]             // matches a leading directory of the member
    [InlineData("d/su?", "d/sub/b.txt", true)]
    [InlineData("d/*", "d/", false)]                  // d itself is not under d/*
    [InlineData("d/*", "d/a.txt", true)]
    [InlineData("*/b.txt", "d/sub/b.txt", true)]
    [InlineData("z*", "d/a.txt", false)]
    public void Filter_WildcardName(string pattern, string member, bool expected)
    {
        Assert.Equal(expected, Filter(new[] { pattern }, wild: true).Selects(member));
    }

    [Fact]
    public void Filter_FirstMatchingNameWins_SoADuplicateIsUnmatched()
    {
        var f = Filter(new[] { "t.txt", "t.txt" });
        Assert.True(f.Selects("t.txt"));
        Assert.Equal(new[] { "t.txt" }, f.Unmatched().ToArray());
    }

    [Fact]
    public void Filter_Unmatched_ListsNamesThatSelectedNothing_InOrder()
    {
        var f = Filter(new[] { "b", "a", "c" });
        f.Selects("a");
        Assert.Equal(new[] { "b", "c" }, f.Unmatched().ToArray());
    }

    [Fact]
    public void Filter_WildcardNameFlagIsPerName()
    {
        var f = new TarMemberFilter(new[] { ("*.txt", false), ("d/*", true) }, Array.Empty<string>());
        Assert.False(f.Selects("a.txt"));              // first name is literal
        Assert.True(f.Selects("d/x"));
    }

    [Theory]
    [InlineData("sub", "d/sub", true)]
    [InlineData("sub", "d/sub/b.txt", true)]          // a component run anywhere excludes the subtree
    [InlineData("d/sub", "d/sub/b.txt", true)]
    [InlineData("b.txt", "d/sub/b.txt", true)]
    [InlineData("b.txt", "d/sub", false)]
    [InlineData("*.log", "d/other/c.log", true)]
    [InlineData("*b*", "d/sub/b.txt", true)]
    [InlineData("?", "d/a.txt", true)]                // the one-character component d
    [InlineData("?", "tt/a.txt", false)]
    [InlineData("ub", "d/sub", false)]                // whole components only
    [InlineData("d/", "d/a.txt", true)]               // trailing slash on the pattern is ignored
    public void Filter_Exclude(string pattern, string member, bool excluded)
    {
        Assert.Equal(excluded, !Filter(Array.Empty<string>(), false, pattern).Selects(member));
    }

    [Fact]
    public void Filter_ExcludeBeatsAName()
    {
        var f = Filter(new[] { "d" }, false, "sub");
        Assert.True(f.Selects("d/a.txt"));
        Assert.False(f.Selects("d/sub/b.txt"));
    }

    [Theory]
    [InlineData("a\nb\n", new[] { "a", "b" })]
    [InlineData("a\n\nb", new[] { "a", "", "b" })]
    [InlineData("a\r\n", new[] { "a\r" })]
    [InlineData("a  \n", new[] { "a  " })]
    [InlineData("", new string[0])]
    [InlineData("\n", new[] { "" })]
    public void ReadListLines_SplitsOnNewlineOnly(string text, string[] expected)
    {
        Assert.Equal(expected, TarMemberFilter.ReadListLines(text));
    }

    // ───────────── member selection through the cmdlet ─────────────

    [Fact]
    public void List_Members_SelectsDirectoryAndChildren()
    {
        MakeArchive();
        Assert.Equal(new[] { "d/sub/", "d/sub/b.txt" }, Names(InTmp("Invoke-BashTar '-tf' a.tar d/sub").AssertSuccess()));
    }

    [Fact]
    public void List_MissingMember_IsNotFoundInArchive_Exit2()
    {
        MakeArchive();
        var r = InTmp("Invoke-BashTar '-tf' a.tar nosuch");
        r.AssertFailed(2, "tar: nosuch: Not found in archive", "tar: Exiting with failure status due to previous errors");
    }

    [Fact]
    public void List_MissingAndPresent_ListsPresent_StillFails()
    {
        MakeArchive();
        var r = InTmp("Invoke-BashTar '-tf' a.tar t.txt nosuch");
        Assert.Equal(new[] { "t.txt" }, Names(r));
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("tar: nosuch: Not found in archive", r.Stderr);
    }

    [Fact]
    public void List_PatternCharactersWithoutWildcardsFlag_WarnsAndIsLiteral()
    {
        MakeArchive();
        var r = InTmp("Invoke-BashTar '-tf' a.tar 'd/*.txt'");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("tar: Pattern matching characters used in file names", r.Stderr);
        Assert.Contains("tar: Use --wildcards to enable pattern matching, or --no-wildcards to suppress this warning", r.Stderr);
        Assert.Contains("tar: d/*.txt: Not found in archive", r.Stderr);
    }

    [Fact]
    public void List_NoWildcards_SuppressesTheWarning()
    {
        MakeArchive();
        var r = InTmp("Invoke-BashTar '-tf' a.tar '--no-wildcards' 'd/*.txt'");
        Assert.DoesNotContain("Pattern matching characters", r.Stderr);
        Assert.Equal(2, r.ExitCode);
    }

    [Fact]
    public void List_Wildcards_Globs()
    {
        MakeArchive();
        Assert.Equal(new[] { "d/a.txt", "d/sub/b.txt" }, Names(InTmp("Invoke-BashTar '-tf' a.tar '--wildcards' 'd/*.txt'").AssertSuccess()));
    }

    [Fact]
    public void Extract_Members_ExtractsOnlyThose_AndCreatesParents()
    {
        MakeArchive();
        InTmp("New-Item -ItemType Directory x | Out-Null; Invoke-BashTar '-xf' a.tar '-C' x t.txt e/f.txt").AssertSuccess();
        Assert.Equal("top", Read("x/t.txt").Trim());
        Assert.Equal("4", Read("x/e/f.txt").Trim());
        Assert.False(Exists("x/d"));
    }

    [Fact]
    public void Extract_MissingMember_ExtractsTheRest_ThenFails()
    {
        MakeArchive();
        var r = InTmp("New-Item -ItemType Directory x | Out-Null; Invoke-BashTar '-xf' a.tar '-C' x nosuch t.txt");
        Assert.Equal(2, r.ExitCode);
        Assert.True(Exists("x/t.txt"));
        Assert.Contains("tar: nosuch: Not found in archive", r.Stderr);
    }

    [Fact]
    public void Extract_Exclude_SkipsMatchingMembers()
    {
        MakeArchive();
        InTmp("New-Item -ItemType Directory x | Out-Null; Invoke-BashTar '-xf' a.tar '-C' x '--exclude=*.txt'").AssertSuccess();
        Assert.True(Exists("x/d/other/c.log"));
        Assert.False(Exists("x/t.txt"));
        Assert.False(Exists("x/d/a.txt"));
    }

    [Fact]
    public void Extract_Verbose_NamesDirectoriesWithASlash()
    {
        MakeArchive();
        var r = InTmp("New-Item -ItemType Directory x | Out-Null; Invoke-BashTar '-xvf' a.tar '-C' x e").AssertSuccess();
        Assert.Equal(new[] { "e/", "e/f.txt" }, Names(r));
    }

    // ───────────── -T / -X ─────────────

    [Fact]
    public void FilesFrom_ListsNamesFromAFile_PlusOperands()
    {
        MakeArchive();
        File.WriteAllText(Path.Combine(_tmp, "list"), "t.txt\nd/sub\n");
        Assert.Equal(new[] { "d/sub/", "d/sub/b.txt", "e/f.txt", "t.txt" },
            Names(InTmp("Invoke-BashTar '-tf' a.tar '-T' list e/f.txt").AssertSuccess()));
    }

    [Fact]
    public void FilesFrom_LinesAreVerbatim_TrailingBlankIsPartOfTheName()
    {
        MakeArchive();
        File.WriteAllText(Path.Combine(_tmp, "list"), "t.txt\n\ne/f.txt  \n");
        var r = InTmp("Invoke-BashTar '-tf' a.tar '-T' list");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("tar: e/f.txt  : Not found in archive", r.Stderr);
    }

    [Fact]
    public void FilesFrom_MissingFile_IsCannotStat_NotRecoverable()
    {
        MakeArchive();
        InTmp("Invoke-BashTar '-tf' a.tar '-T' nolist")
            .AssertFailed(2, "tar: nolist: Cannot stat: No such file or directory", "tar: Error is not recoverable: exiting now");
    }

    [Fact]
    public void FilesFrom_Stdin_ReadsThePipeline()
    {
        MakeArchive();
        Assert.Equal(new[] { "t.txt" }, Names(InTmp("'t.txt' | Invoke-BashTar '-tf' a.tar '-T' '-'").AssertSuccess()));
    }

    [Fact]
    public void FilesFrom_OptionLine_IsRefused()
    {
        MakeArchive();
        File.WriteAllText(Path.Combine(_tmp, "list"), "-C\ne\n");
        InTmp("Invoke-BashTar '-cf' b.tar '-T' list").AssertFailed(2, "tar: list:1: option lines in a file list are not supported by ps-bash");
    }

    [Fact]
    public void FilesFrom_Create_KeepsTypedRelativePaths_InOperandOrder()
    {
        File.WriteAllText(Path.Combine(_tmp, "list"), "d/a.txt\n");
        InTmp("Invoke-BashTar '-cf' b.tar t.txt '-T' list e/f.txt").AssertSuccess();
        Assert.Equal(new[] { "d/a.txt", "e/f.txt", "t.txt" }, Names(InTmp("Invoke-BashTar '-tf' b.tar").AssertSuccess()));
    }

    [Fact]
    public void ExcludeFrom_ReadsPatternsFromAFile_SkipsEmptyLines()
    {
        MakeArchive();
        File.WriteAllText(Path.Combine(_tmp, "ex"), "\n*.log\nsub\n");
        Assert.Equal(new[] { "d/", "d/a.txt", "d/other/", "e/", "e/f.txt", "t.txt" },
            Names(InTmp("Invoke-BashTar '-tf' a.tar '-X' ex").AssertSuccess()));
    }

    [Fact]
    public void ExcludeFrom_MissingFile_IsNotRecoverable()
    {
        MakeArchive();
        InTmp("Invoke-BashTar '-tf' a.tar '-X' noex")
            .AssertFailed(2, "tar: noex: No such file or directory", "tar: Error is not recoverable: exiting now");
    }

    [Fact]
    public void ExcludeFrom_AppliesOnCreate_IncludingTheTopLevelDirectory()
    {
        File.WriteAllText(Path.Combine(_tmp, "ex"), "d\n");
        InTmp("Invoke-BashTar '-cf' b.tar '-X' ex d t.txt").AssertSuccess();
        Assert.Equal(new[] { "t.txt" }, Names(InTmp("Invoke-BashTar '-tf' b.tar").AssertSuccess()));
    }

    // ───────────── create: names as typed, errors ─────────────

    [Fact]
    public void Create_RelativeOperand_KeepsItsDirectoryPart()
    {
        InTmp("Invoke-BashTar '-cf' b.tar d/a.txt").AssertSuccess();
        Assert.Equal(new[] { "d/a.txt" }, Names(InTmp("Invoke-BashTar '-tf' b.tar").AssertSuccess()));
    }

    [Fact]
    public void Create_MissingSource_IsCannotStat_Exit2_ButKeepsTheRest()
    {
        var r = InTmp("Invoke-BashTar '-cf' b.tar nosuch t.txt");
        r.AssertFailed(2, "tar: nosuch: Cannot stat: No such file or directory", "tar: Exiting with failure status due to previous errors");
        Assert.Equal(new[] { "t.txt" }, Names(InTmp("Invoke-BashTar '-tf' b.tar").AssertSuccess()));
    }

    [Fact]
    public void Create_VerboseNamesDirectoriesWithASlash()
    {
        var r = InTmp("Invoke-BashTar '-cvf' b.tar e").AssertSuccess();
        Assert.Equal(new[] { "e/", "e/f.txt" }, Names(r));
    }

    // ───────────── stdin / stdout ─────────────

    [Fact]
    public void Stdout_NoF_EmitsArchiveBytes_ThatTheNextTarReads()
    {
        var r = InTmp("Invoke-BashTar 'c' d t.txt | Invoke-BashTar 'tf' '-'").AssertSuccess();
        Assert.Equal(new[] { "d/", "d/a.txt", "d/other/", "d/other/c.log", "d/sub/", "d/sub/b.txt", "t.txt" }, Names(r));
    }

    [Fact]
    public void Stdout_Verbose_NamesGoToStderr()
    {
        var r = InTmp("Invoke-BashTar '-cvf' '-' t.txt | Out-Null");
        Assert.Contains("t.txt", r.Stderr);
    }

    [Fact]
    public void Stdin_PipeToExtract_WithNoF()
    {
        InTmp("New-Item -ItemType Directory x | Out-Null; Invoke-BashTar '-cf' '-' d | Invoke-BashTar 'x' '-C' x").AssertSuccess();
        Assert.Equal("1", Read("x/d/a.txt").Trim());
        Assert.Equal("2", Read("x/d/sub/b.txt").Trim());
    }

    [Fact]
    public void Stdin_MemberToStdout()
    {
        var r = InTmp("Invoke-BashTar '-cf' '-' d t.txt | Invoke-BashTar '-xOf' '-' t.txt").AssertSuccess();
        Assert.Equal("top", r.Stdout.Trim());
    }

    [Fact]
    public void Gzip_Stdout_RoundTripsWithZ()
    {
        var r = InTmp("Invoke-BashTar '-czf' '-' t.txt | Invoke-BashTar '-tzf' '-'").AssertSuccess();
        Assert.Equal(new[] { "t.txt" }, Names(r));
    }

    [Fact]
    public void Gzip_StdinWithoutZ_IsGnusError()
    {
        InTmp("Invoke-BashTar '-czf' '-' t.txt | Invoke-BashTar '-tf' '-'")
            .AssertFailed(2, "tar: Archive is compressed. Use -z option", "tar: Error is not recoverable: exiting now");
    }

    [Fact]
    public void Gzip_FileIsDetectedWithoutZ_EvenWithAPlainName()
    {
        InTmp("Invoke-BashTar '-czf' plain.tar t.txt").AssertSuccess();
        Assert.Equal(new[] { "t.txt" }, Names(InTmp("Invoke-BashTar '-tf' plain.tar").AssertSuccess()));
    }

    [Fact]
    public void Gzip_ZOnANonGzipArchive_IsGzipsError()
    {
        MakeArchive();
        InTmp("Invoke-BashTar '-tzf' a.tar")
            .AssertFailed(2, "gzip: stdin: not in gzip format", "tar: Child returned status 1", "tar: Error is not recoverable: exiting now");
    }

    [Fact]
    public void EmptyStdin_IsNotATarArchive()
    {
        InTmp("Invoke-BashTar 't'").AssertFailed(2, "tar: This does not look like a tar archive", "tar: Exiting with failure status due to previous errors");
    }

    [Fact]
    public void GarbageStdin_IsNotATarArchive()
    {
        InTmp("'garbage' | Invoke-BashTar 't'").AssertFailed(2, "tar: This does not look like a tar archive");
    }

    [Fact]
    public void MissingArchiveFile_IsCannotOpen_NotRecoverable()
    {
        InTmp("Invoke-BashTar '-tf' nosuch.tar")
            .AssertFailed(2, "Cannot open: No such file or directory", "tar: Error is not recoverable: exiting now");
    }

    [Fact]
    public void TapeEnvironment_IsTheDefaultArchive()
    {
        InTmp("$env:TAPE = 'x.tape'; try { Invoke-BashTar 'c' t.txt } finally { $env:TAPE = $null }").AssertSuccess();
        Assert.True(Exists("x.tape"));
    }
}
