using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// cp behaviours the differential oracle cannot cover here: symbolic links (Windows needs a privilege
/// the CI runners may lack, so those cases skip with a reason), answers piped to <c>-i</c>, and the
/// engine seams. Expectations were read from GNU coreutils 9.4 (<c>wsl bash</c>).
/// </summary>
public class CpOptionsTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    public CpOptionsTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), $"psb-cpo-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    private CmdResult InTmp(string body) => CmdResult.Run(_fixture.AcquireFresh(),
        $"Push-Location '{_tmp}'; try {{ {body} }} finally {{ Pop-Location }}");

    private void Write(string rel, string content = "x")
    {
        var p = Path.Combine(_tmp, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private string Read(string rel) => File.ReadAllText(Path.Combine(_tmp, rel));
    private bool Exists(string rel) => File.Exists(Path.Combine(_tmp, rel)) || Directory.Exists(Path.Combine(_tmp, rel));

    private bool Symlink(string linkRel, string target)
    {
        try
        {
            File.CreateSymbolicLink(Path.Combine(_tmp, linkRel), target);
            return true;
        }
        catch { return false; }
    }

    private string? LinkTarget(string rel) => new FileInfo(Path.Combine(_tmp, rel)).LinkTarget;

    // ───────────── -i answers come from the pipeline ─────────────

    [Fact]
    public void Interactive_PipedYes_Overwrites_AndPromptsOnStderr()
    {
        Write("a", "new"); Write("b", "old");
        // The prompt rides the error stream (the only one the host delivers to stderr), so not AssertSuccess.
        var r = InTmp("'y' | Invoke-BashCp '-i' a b");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("new", Read("b"));
        Assert.Contains("cp: overwrite 'b'? ", r.Stderr);
    }

    [Fact]
    public void Interactive_PromptTextIsGnus()
    {
        Write("a", "new"); Write("b", "old");
        var r = InTmp("'n' | Invoke-BashCp '-i' a b");
        r.AssertFailed(1, "cp: overwrite 'b'? ");
        Assert.Equal("old", Read("b"));
    }

    [Fact]
    public void Interactive_AnswerStartingWithCapitalYIsYes()
    {
        Write("a", "new"); Write("b", "old");
        Assert.Equal(0, InTmp("'Yes please' | Invoke-BashCp '-i' a b").ExitCode);
        Assert.Equal("new", Read("b"));
    }

    [Fact]
    public void Interactive_AnswersAreConsumedOnePerPrompt()
    {
        Write("s/a", "NA"); Write("s/b", "NB"); Write("t/s/a", "OA"); Write("t/s/b", "OB");
        // One yes, one no (in directory order): exactly one file is replaced and the exit status is 1.
        var r = InTmp("'y','n' | Invoke-BashCp '-ri' s t");
        Assert.Equal(1, r.ExitCode);
        var replaced = new[] { Read("t/s/a") == "NA", Read("t/s/b") == "NB" };
        Assert.Single(replaced, true);
    }

    [Fact]
    public void Interactive_NoPromptForANewDestination()
    {
        Write("a", "x");
        InTmp("Invoke-BashCp '-i' a n").AssertSuccess();
        Assert.True(Exists("n"));
    }

    // ───────────── --debug / -v text ─────────────

    [Fact]
    public void Debug_PrintsGnusLinePerFile()
    {
        Write("a", "x");
        var r = InTmp("Invoke-BashCp '--debug' a n").AssertSuccess();
        Assert.Equal(new[] { "'a' -> 'n'", "copy offload: yes, reflink: unsupported, sparse detection: no" },
            r.Lines.Select(l => l.TrimEnd('\n', '\r')).ToArray());
    }

    [Fact]
    public void Debug_ReflinkNeverChangesTheWording()
    {
        Write("a", "x");
        var r = InTmp("Invoke-BashCp '--debug' '--reflink=never' a n").AssertSuccess();
        Assert.Contains("copy offload: avoided, reflink: no, sparse detection: no", r.Stdout);
    }

    [Fact]
    public void Debug_NoClobberReportsTheSkip()
    {
        Write("a"); Write("b", "keep");
        var r = InTmp("Invoke-BashCp '--debug' '--no-clobber' a b");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("skipped 'b'", r.Stdout);
        Assert.Equal("keep", Read("b"));
    }

    [Fact]
    public void Verbose_NamesEveryEntryOfATree_DirectoriesOnlyWhenCreated()
    {
        Write("s/x/f");
        var r = InTmp("Invoke-BashCp '-rv' s t").AssertSuccess();
        Assert.Equal(new[] { "'s' -> 't'", "'s/x' -> 't/x'", "'s/x/f' -> 't/x/f'" },
            r.Lines.Select(l => l.TrimEnd('\n', '\r')).ToArray());

        var again = InTmp("Invoke-BashCp '-rv' s t").AssertSuccess();      // t exists: copied INTO it
        Assert.Equal("'s' -> 't/s'", again.Lines[0].TrimEnd('\n', '\r'));

        var merged = InTmp("Invoke-BashCp '-rvT' s t").AssertSuccess();     // -T merges: no line for t itself
        Assert.DoesNotContain("'s' -> 't'", merged.Lines.Select(l => l.TrimEnd('\n', '\r')));
    }

    // ───────────── hard links: the line is printed before the attempt ─────────────

    [Fact]
    public void HardLink_Verbose_ThenFileExists_Exit1()
    {
        Write("a", "A"); Write("b", "B");
        var r = InTmp("Invoke-BashCp '-lv' a b");
        r.AssertFailed(1, "cp: cannot create hard link 'b' to 'a': File exists");
        Assert.Contains("'a' -> 'b'", r.Stdout);
        Assert.Equal("B", Read("b"));
    }

    [Fact]
    public void HardLink_SharesContent()
    {
        Write("a", "A");
        InTmp("Invoke-BashCp '-l' a n").AssertSuccess();
        File.AppendAllText(Path.Combine(_tmp, "n"), "more");
        Assert.Equal("Amore", Read("a"));
    }

    [Fact]
    public void Reflink_Always_IsGnusError_AndCreatesNothing()
    {
        Write("a");
        InTmp("Invoke-BashCp '--reflink=always' a n").AssertFailed(1, "cp: failed to clone 'n' from 'a': Operation not supported");
        Assert.False(Exists("n"));
    }

    // ───────────── option shapes ─────────────

    [Fact]
    public void MissingDestinationOperand_NamesTheSource()
    {
        Write("a");
        InTmp("Invoke-BashCp a").AssertFailed(1, "cp: missing destination file operand after 'a'", "Try 'cp --help'");
    }

    [Fact]
    public void TargetDirectory_TakesEveryOperandAsASource()
    {
        Write("a", "A"); Write("b", "B"); Directory.CreateDirectory(Path.Combine(_tmp, "d"));
        InTmp("Invoke-BashCp '-t' d a b").AssertSuccess();
        Assert.Equal("A", Read("d/a"));
        Assert.Equal("B", Read("d/b"));
    }

    [Fact]
    public void NoTargetDirectory_NeverDescendsIntoADirectory()
    {
        Write("a"); Directory.CreateDirectory(Path.Combine(_tmp, "d"));
        InTmp("Invoke-BashCp '-T' a d").AssertFailed(1, "cp: cannot overwrite directory 'd' with non-directory");
    }

    [Fact]
    public void Parents_RebuildsTheSourcePath_AndAnnouncesTheDirectories()
    {
        Write("p/q/r", "R"); Directory.CreateDirectory(Path.Combine(_tmp, "out"));
        var r = InTmp("Invoke-BashCp '--parents' '-v' p/q/r out").AssertSuccess();
        Assert.Equal("R", Read("out/p/q/r"));
        Assert.Equal(new[] { "p -> out/p", "p/q -> out/p/q", "'p/q/r' -> 'out/p/q/r'" },
            r.Lines.Select(l => l.TrimEnd('\n', '\r')).ToArray());
    }

    [Fact]
    public void Parents_DestinationMustBeADirectory()
    {
        Write("p/q/r"); Write("b");
        InTmp("Invoke-BashCp '--parents' p/q/r b").AssertFailed(1, "cp: with --parents, the destination must be a directory");
    }

    [Fact]
    public void StripTrailingSlashes_OnlyInDirectoryForm()
    {
        Write("a", "A"); Directory.CreateDirectory(Path.Combine(_tmp, "d"));
        InTmp("Invoke-BashCp '--strip-trailing-slashes' a/ d").AssertSuccess();
        Assert.Equal("A", Read("d/a"));
        InTmp("Invoke-BashCp '--strip-trailing-slashes' a/ n").AssertFailed(1, "cannot stat 'a/'");
    }

    [Fact]
    public void AttributesOnly_CreatesAnEmptyFile_AndLeavesContentAlone()
    {
        Write("a", "DATA"); Write("b", "KEEP");
        InTmp("Invoke-BashCp '--attributes-only' a n").AssertSuccess();
        Assert.Equal("", Read("n"));
        InTmp("Invoke-BashCp '--attributes-only' a b").AssertSuccess();
        Assert.Equal("KEEP", Read("b"));
    }

    [Fact]
    public void RemoveDestination_UnlinksBeforeCopying()
    {
        Write("a", "NEW"); Write("b", "OLD");
        InTmp("Invoke-BashCp '--remove-destination' a b").AssertSuccess();
        Assert.Equal("NEW", Read("b"));
    }

    [Fact]
    public void Backup_Simple_AndVerboseNote()
    {
        Write("a", "NEW"); Write("b", "OLD");
        var r = InTmp("Invoke-BashCp '-bv' a b").AssertSuccess();
        Assert.Equal("NEW", Read("b"));
        Assert.Equal("OLD", Read("b~"));
        Assert.Equal("'a' -> 'b' (backup: 'b~')", r.Lines[0].TrimEnd('\n', '\r'));
    }

    [Fact]
    public void Backup_BadControlWord_ExitsOne_AndCopiesNothing()
    {
        Write("a", "NEW"); Write("b", "OLD");
        InTmp("Invoke-BashCp '--backup=bogus' a b").AssertFailed(1, "invalid argument 'bogus' for 'backup type'");
        Assert.Equal("OLD", Read("b"));
    }

    [Fact]
    public void Direct_BareBackupFlags_AreNotSwallowedByTheBinder()
    {
        // Pester-style direct calls: -b/-S/-t/-T/-l/-s/-L/-x are plain Arguments (no common-parameter prefix).
        Write("a", "NEW"); Write("b", "OLD"); Directory.CreateDirectory(Path.Combine(_tmp, "d"));
        InTmp("Invoke-BashCp -b a b").AssertSuccess();
        Assert.Equal("OLD", Read("b~"));
        InTmp("Invoke-BashCp -t d a").AssertSuccess();
        Assert.Equal("NEW", Read("d/a"));
        InTmp("Invoke-BashCp -T a n").AssertSuccess();
        InTmp("Invoke-BashCp -l a hl").AssertSuccess();
        InTmp("Invoke-BashCp -x -L a n2").AssertSuccess();
    }

    // ───────────── symbolic links (privilege-gated on Windows) ─────────────

    [SkippableFact]
    public void SymbolicLink_FileOperand_FollowedByDefault_CopiedAsLinkUnderP()
    {
        Write("a", "A");
        Skip.IfNot(Symlink("la", "a"), "symbolic links cannot be created here (Windows needs Developer Mode / privilege)");

        InTmp("Invoke-BashCp la c1").AssertSuccess();
        Assert.Null(LinkTarget("c1"));
        Assert.Equal("A", Read("c1"));

        InTmp("Invoke-BashCp '-P' la c2").AssertSuccess();
        Assert.Equal("a", LinkTarget("c2"));

        InTmp("Invoke-BashCp '-d' la c3").AssertSuccess();
        Assert.Equal("a", LinkTarget("c3"));

        InTmp("Invoke-BashCp '-L' la c4").AssertSuccess();
        Assert.Null(LinkTarget("c4"));

        InTmp("Invoke-BashCp '-PL' la c5").AssertSuccess();      // last wins: -L
        Assert.Null(LinkTarget("c5"));
        InTmp("Invoke-BashCp '-LP' la c6").AssertSuccess();
        Assert.Equal("a", LinkTarget("c6"));
    }

    [SkippableFact]
    public void SymbolicLink_RecursiveDefaultCopiesLinksAsLinks_LFollowsThem()
    {
        Write("real/f", "F");
        Skip.IfNot(Symlink("real/lf", "f"), "symbolic links cannot be created here (Windows needs Developer Mode / privilege)");

        InTmp("Invoke-BashCp '-r' real r1").AssertSuccess();
        Assert.Equal("f", LinkTarget("r1/lf"));

        InTmp("Invoke-BashCp '-rL' real r2").AssertSuccess();
        Assert.Null(LinkTarget("r2/lf"));
        Assert.Equal("F", Read("r2/lf"));

        InTmp("Invoke-BashCp '-a' real r3").AssertSuccess();
        Assert.Equal("f", LinkTarget("r3/lf"));
    }

    [SkippableFact]
    public void SymbolicLink_S_CreatesALinkHoldingTheSourceAsTyped()
    {
        Write("a", "A");
        Skip.IfNot(Symlink("probe", "a"), "symbolic links cannot be created here (Windows needs Developer Mode / privilege)");
        File.Delete(Path.Combine(_tmp, "probe"));

        var r = InTmp("Invoke-BashCp '-sv' a ln1").AssertSuccess();
        Assert.Equal("a", LinkTarget("ln1"));
        Assert.Equal("'a' -> 'ln1'", r.Lines[0].TrimEnd('\n', '\r'));

        InTmp("Invoke-BashCp '-s' a ln1").AssertFailed(1, "cp: cannot create symbolic link 'ln1' to 'a': File exists");
        InTmp("Invoke-BashCp '-sf' a ln1").AssertSuccess();
    }

    [SkippableFact]
    public void SymbolicLink_S_RelativeSourceOnlyWhenTheLinkLandsInTheCurrentDirectory()
    {
        Write("a", "A"); Directory.CreateDirectory(Path.Combine(_tmp, "d"));
        Skip.IfNot(Symlink("probe", "a"), "symbolic links cannot be created here (Windows needs Developer Mode / privilege)");
        File.Delete(Path.Combine(_tmp, "probe"));

        InTmp("Invoke-BashCp '-s' a d").AssertFailed(1, "can make relative symbolic links only in current directory");
        InTmp("Invoke-BashCp '-s' (Join-Path $PWD 'a') d").AssertSuccess();
        Assert.Equal(Path.Combine(_tmp, "a"), LinkTarget("d/a"));
    }

    [SkippableFact]
    public void SymbolicLink_BackupOfALinkDestination()
    {
        Write("a", "A"); Write("x", "X");
        Skip.IfNot(Symlink("lk", "x"), "symbolic links cannot be created here (Windows needs Developer Mode / privilege)");
        InTmp("Invoke-BashCp '-b' a lk").AssertSuccess();
        Assert.Equal("x", LinkTarget("lk~"));
        Assert.Null(LinkTarget("lk"));
        Assert.Equal("A", Read("lk"));
        Assert.Equal("X", Read("x"));        // the link target was never written through
    }
}
