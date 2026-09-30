using PsBash.Differential.Tests.Oracle;
using Xunit;
using static PsBash.Differential.Tests.Oracle.FsStateOracle;

namespace PsBash.Differential.Tests;

/// <summary>
/// GNU-vs-ps-bash comparisons that check the resulting FILESYSTEM STATE (paths, file/dir kind,
/// exact file bytes) as well as stdout and exit status — see <see cref="FsStateOracle"/>.
/// Cases: the cp/mv data-loss regressions and the tee byte-fidelity/`--` cases. Unlike
/// <c>CpMvDifferentialTests</c> (which prints <c>find</c> + <c>cat</c>), the snapshot here
/// compares every file's bytes, so a stray or missing trailing newline is caught.
/// </summary>
public class FsStateDifferentialTests
{
    // ───────────── cp ─────────────

    [SkippableFact] public Task Cp_RecursiveForce_MergesAndKeepsDestinationOnlyFiles() =>
        EqualAsync(
            Tree(("src/proj/new", "n"), ("dst/proj/new", "old"), ("dst/proj/only", "keep"), ("dst/proj/nested/deep", "keep-deep")),
            "cp -rf src/proj dst");

    [SkippableFact] public Task Cp_SeveralSourcesToMissingDestination_ErrorsAndWritesNothing() =>
        EqualAsync(
            Tree(("a", "A"), ("b", "B")),
            "cp a b result");

    [SkippableFact] public Task Cp_RecursiveNoClobber_TraversesAndSkipsOnlyConflicts() =>
        EqualAsync(
            Tree(("s/x/f1", "new1"), ("s/x/f2", "new2"), ("t/s/x/f1", "old1")),
            "cp -rn s t");

    // ───────────── mv ─────────────

    [SkippableFact] public Task Mv_SourceIntoItsOwnParent_KeepsSource() =>
        EqualAsync(
            Tree(("p/src/f", "payload")),
            "mv p/src p");

    [SkippableFact] public Task Mv_DirOntoNonEmptyDirectory_Refused() =>
        EqualAsync(
            Tree(("s/moved", "moved"), ("t/s/keep", "old")),
            "mv s t");

    // ───────────── ln operand forms (hard links: no symlink privilege needed) ─────────────

    private static string LnTree() =>
        Tree(("a", "A"), ("b", "B"), ("d/c", "C"), ("e", null));

    [SkippableFact] public Task Ln_SingleOperand_LinksBasenameIntoCwd() =>
        EqualAsync(LnTree(), "ln -v d/c");

    [SkippableFact] public Task Ln_SingleOperand_ExistingName_Fails() =>
        EqualAsync(LnTree(), "ln a 2>&1");

    [SkippableFact] public Task Ln_SeveralTargets_IntoDirectory() =>
        EqualAsync(LnTree(), "ln -v a b e");

    [SkippableFact] public Task Ln_SeveralTargets_TrailingSlashDirectory() =>
        EqualAsync(LnTree(), "ln -v a b d/");

    [SkippableFact] public Task Ln_SeveralTargets_LastOperandMissing() =>
        EqualAsync(LnTree(), "ln a b nodir 2>&1");

    [SkippableFact] public Task Ln_SeveralTargets_LastOperandIsFile() =>
        EqualAsync(LnTree(), "ln a b b 2>&1");

    [SkippableFact] public Task Ln_SeveralTargets_OneExists_OthersStillLinked() =>
        EqualAsync(Tree(("a", "A"), ("b", "B"), ("e/a", "OLD")), "ln a b e 2>&1");

    [SkippableFact] public Task Ln_TwoOperands_LinkNameIsDirectory_LinksInside() =>
        EqualAsync(LnTree(), "ln -v a e");

    [SkippableFact] public Task Ln_TargetDirectoryOption() =>
        EqualAsync(LnTree(), "ln -v -t e a b");

    [SkippableFact] public Task Ln_TargetDirectoryOption_MissingDirectory() =>
        EqualAsync(LnTree(), "ln -t nodir a 2>&1");

    [SkippableFact] public Task Ln_NoTargetDirectory_DoesNotDescendIntoDirectory() =>
        EqualAsync(LnTree(), "ln -T a d 2>&1");

    [SkippableFact] public Task Ln_NoTargetDirectory_PlainName() =>
        EqualAsync(LnTree(), "ln -Tv a z");

    [SkippableFact] public Task Ln_NoTargetDirectory_ExtraOperand() =>
        EqualAsync(LnTree(), "ln -T a z y 2>&1");

    [SkippableFact] public Task Ln_TargetAndNoTargetDirectory_Conflict() =>
        EqualAsync(LnTree(), "ln -t e -T a 2>&1");

    [SkippableFact] public Task Ln_HardLink_MissingTarget() =>
        EqualAsync(LnTree(), "ln missing z 2>&1");

    [SkippableFact] public Task Ln_HardLink_DirectoryTarget() =>
        EqualAsync(LnTree(), "ln d z 2>&1");

    // ───────────── tee ─────────────

    [SkippableFact] public Task Tee_PrintfWithoutNewline_FileBytesAreExactlyTheInput() =>
        EqualAsync(
            "",
            "printf x | tee f");

    [SkippableFact] public Task Tee_DoubleDash_DashNamedFileIsAnOperand() =>
        EqualAsync(
            "",
            "printf 'hi\\n' | tee -- -zz");

    // ───────────── diagnostics quote operands as typed ─────────────
    //
    // `2>&1` folds the message into the compared stdout: GNU quotes each operand exactly as typed
    // ('nosuch', './nosuch', 'dir1/../s'), never the resolved full path.

    [SkippableFact] public Task Rm_DiagnosticsQuoteOperandsAsTyped() =>
        EqualAsync(
            Tree(("d1/f", "x"), ("keep", "k")),
            "rm nosuch ./nosuch2 d1 nosuch/x 2>&1");

    [SkippableFact] public Task Cp_DiagnosticsQuoteOperandsAsTyped() =>
        EqualAsync(
            Tree(("s", "S"), ("d1/f", "x")),
            "cp nosuch dst 2>&1; cp d1 dst 2>&1; cp s ./s 2>&1; cp s d1/../s 2>&1");

    [SkippableFact] public Task Cp_MissingDestinationParent_ErrorsAndCreatesNothing() =>
        EqualAsync(
            Tree(("s", "S")),
            "cp s nodir/x 2>&1");

    [SkippableFact] public Task Cp_DestinationDirectoryTrailingSlash_SameFileNamesJoinedTarget() =>
        EqualAsync(
            Tree(("d/s", "S")),
            "cd d; cp s ./ 2>&1; cp s . 2>&1");

    [SkippableFact] public Task Mv_DiagnosticsQuoteOperandsAsTyped() =>
        EqualAsync(
            Tree(("s", "S"), ("d/f", "x")),
            "mv nosuch dst 2>&1; mv s ./s 2>&1; mv d d/x 2>&1; mv s nodir/x 2>&1");

    // ───────────── rm -d / -i / -I / --interactive ─────────────
    //
    // Prompts go to stderr without a newline in GNU (the host ends every stderr line), so these
    // compare stdout + exit + the resulting tree, not the prompt text (RmInteractiveTests pins it).

    [SkippableFact] public Task Rm_DashD_RemovesEmptyDirsAndFiles_RefusesNonEmpty() =>
        EqualAsync(
            Tree(("e", null), ("ne/g", "x"), ("f", "x")),
            "rm -d e ne f 2>&1");

    [SkippableFact] public Task Rm_DashRD_RemovesANonEmptyTree() =>
        EqualAsync(
            Tree(("ne/x/g", "x")),
            "rm -rd ne");

    [SkippableFact] public Task Rm_Interactive_OneAnswerPerOperand() =>
        EqualAsync(
            Tree(("a", "A"), ("b", "B"), ("c", "C")),
            "printf 'y\\nn\\nyes\\n' | rm -i a b c");

    [SkippableFact] public Task Rm_Interactive_NoAnswerIsNo() =>
        EqualAsync(
            Tree(("a", "A")),
            "printf '' | rm -i a");

    [SkippableFact] public Task Rm_InteractiveOnce_FourFilesDeclined_KeepsEverything() =>
        EqualAsync(
            Tree(("a", "A"), ("b", "B"), ("c", "C"), ("d", "D")),
            "echo n | rm -I a b c d");

    [SkippableFact] public Task Rm_InteractiveOnce_ThreeFiles_DoesNotAsk() =>
        EqualAsync(
            Tree(("a", "A"), ("b", "B"), ("c", "C")),
            "echo n | rm -I a b c");

    [SkippableFact] public Task Rm_InteractiveOnce_Recursive_AsksEvenForOneArgument() =>
        EqualAsync(
            Tree(("k/l/f", "x")),
            "echo n | rm -I -r k");

    [SkippableFact] public Task Rm_InteractiveRecursive_DecliningADescentKeepsTheSubtree() =>
        EqualAsync(
            Tree(("p/q/r/f", "x"), ("p/top", "t")),
            "printf 'y\\nn\\ny\\n' | rm -ir p");

    [SkippableFact] public Task Rm_LastOfFAndIWins() =>
        EqualAsync(
            Tree(("z1", "1"), ("z2", "2")),
            "echo n | rm -if z2; echo n | rm -fi z1");

    [SkippableFact] public Task Rm_InteractiveWhen_NeverAlwaysAndBare() =>
        EqualAsync(
            Tree(("a", "A"), ("b", "B"), ("c", "C")),
            "echo n | rm --interactive=never a; echo n | rm --interactive=always b; echo n | rm --interactive c");

    [SkippableFact] public Task Rm_RecursiveVerbose_DirectoriesAreReportedAsRemovedDirectory() =>
        EqualAsync(
            Tree(("d/f", "x")),
            "rm -rv d");

    // ───────────── mkdir -m ─────────────
    //
    // The snapshot has no permission column (Windows cannot reproduce one), so these pin what is
    // observable everywhere: which directories exist, the exit status, and that an invalid mode
    // creates NOTHING (GNU rejects it before touching any operand).

    [SkippableFact] public Task Mkdir_Mode_CreatesTheDirectories() =>
        EqualAsync(
            "",
            "mkdir -m 755 a; mkdir --mode=u=rwx,go=rx b; mkdir -pm 700 p/q/r; mkdir -m 0777 c");

    [SkippableFact] public Task Mkdir_InvalidMode_ExitsOneAndCreatesNothing() =>
        EqualAsync(
            Tree(("keep", "k")),
            "mkdir -m 999 d; echo \"one=$?\"; mkdir -m rwx a b; echo \"two=$?\"; mkdir -m '' e; echo \"three=$?\"");

    [SkippableFact] public Task Mkdir_ModeOnExistingDirectory_IsFileExists() =>
        EqualAsync(
            Tree(("d/f", "x")),
            "mkdir -m 700 d; echo \"rc=$?\"");

    // ───────────── touch -t / -h / --time ─────────────
    //
    // The snapshot has no timestamps (and the two shells may disagree on the local zone), so the
    // stamp is observed through an ORDERING (`ls -t`), which is zone-independent, plus exit status
    // and which files exist.

    [SkippableFact] public Task Touch_Stamp_OrdersFilesByModificationTime() =>
        EqualAsync(
            "",
            "touch -t 202401021530 a; touch -t 202401021531 b; touch -t 202401021529.30 c; touch -t 2401021532 d; ls -t");

    [SkippableFact] public Task Touch_InvalidStampOrConflictingSources_ExitOneAndCreateNothing() =>
        EqualAsync(
            Tree(("keep", "k")),
            "touch -t bad f; echo r1=$?; touch -t 202401021530 -d 2020-01-01 g; echo r2=$?; " +
            "touch --time=bogus h; echo r3=$?; touch -t 202401021530.61 i; echo r4=$?; touch -t 202302291200 j; echo r5=$?");

    [SkippableFact] public Task Touch_TimeWord_OnlyTheNamedTimeMoves() =>
        EqualAsync(
            Tree(("old", "o"), ("mid", "m")),
            "touch -t 200101010000 old mid; touch --time=mtime -t 202401021530 mid; ls -t; echo r=$?");

    [SkippableFact] public Task Touch_NoDereference_DoesNotCreateAMissingName() =>
        EqualAsync(
            "",
            "touch -h nosuch; echo r1=$?; touch -hc nosuch2; echo r2=$?; touch --no-dereference plain; echo r3=$?");

    // ───────────── cp --preserve / --no-preserve ─────────────
    //
    // A copy made "now" is newer than the 2001 source; a copy that preserved timestamps is not.
    // `find -newer` observes exactly that, independent of the zone and of the snapshot's lack of times.

    [SkippableFact] public Task Cp_Preserve_OnlyTimestampPreservingCopiesStayOld() =>
        EqualAsync(
            Tree(("s", "S")),
            "touch -t 200101010000 s; cp s d1; cp --preserve=timestamps s d2; cp --no-preserve=all s d3; " +
            "cp -p --no-preserve=timestamps s d4; cp --no-preserve=timestamps -p s d5; cp --preserve s d6; " +
            "cp --preserve=mode s d7; cp --preserve=t s d8; cp --preserve=all --no-preserve=mode s d9; " +
            "find . -newer s | sort");

    [SkippableFact] public Task Cp_Preserve_ListAndOrder() =>
        EqualAsync(
            Tree(("s", "S")),
            "touch -t 200101010000 s; cp --preserve=mode,timestamps s a; cp --preserve=ownership,links,xattr s b; " +
            "cp --preserve=timestamps --no-preserve=timestamps s c; cp --no-preserve=timestamps --preserve=timestamps s d; " +
            "find . -newer s | sort");

    [SkippableFact] public Task Cp_Preserve_ErrorsExitOneAndCopyNothing() =>
        EqualAsync(
            Tree(("s", "S")),
            "cp --preserve=context s x; echo r1=$?; cp --preserve=bogus s y; echo r2=$?; cp --preserve= s z; echo r3=$?; " +
            "cp --no-preserve s w; echo r4=$?; cp --preserve=mode,bogus s v; echo r5=$?; cp --preserve=mode,context s u; echo r6=$?");

    [SkippableFact] public Task Cp_Preserve_Recursive_TimestampsOfTheTreeStayOld() =>
        EqualAsync(
            Tree(("s/f", "F"), ("ref", "R")),
            "touch -t 200101010000 s/f ref; touch -t 200101010000 s; cp -r --preserve=timestamps s t; cp -r s u; " +
            "find t u -newer ref | sort");

    // ───────────── same file under another name (hard links) ─────────────

    [SkippableFact] public Task Cp_HardLinkedOperands_AreTheSameFile() =>
        EqualAsync(
            Tree(("a", "A")) + "\nln a h",
            "cp a h 2>&1; echo r1=$?; cp h a 2>&1; echo r2=$?; cp -f a ./h 2>&1; echo r3=$?; cp -u a h 2>&1; echo r4=$?");

    [SkippableFact] public Task Cp_NoClobber_OnHardLinkedOperands_IsASilentSkip() =>
        EqualAsync(
            Tree(("a", "A")) + "\nln a h",
            "cp -n a h 2>/dev/null; echo r=$?");

    [SkippableFact] public Task Mv_HardLinkedOperands_AreTheSameFile_BothNamesRemain() =>
        EqualAsync(
            Tree(("a", "A")) + "\nln a h",
            "mv a h 2>&1; echo r1=$?; mv -f h a 2>&1; echo r2=$?");

    [SkippableFact] public Task CpMv_HardLinksIntoADirectory_AreFine() =>
        EqualAsync(
            Tree(("a", "A"), ("d", null), ("e", null)) + "\nln a h",
            "cp a h d; echo r1=$?; mv a h e; echo r2=$?");

    // ───────────── redirect byte fidelity ─────────────

    // `printf x > f` leaves f = "x" (1 byte): Invoke-BashRedirect honours the NoTrailingNewline
    // marker printf / echo -n set, like tee.
    [SkippableFact]
    public Task Redirect_PrintfWithoutNewline_FileBytesAreExactlyTheInput() =>
        EqualAsync("", "printf x > f");

    // NUL / octal escapes reach the file as real bytes: `printf 'x\0' > f` is 2 bytes.
    [SkippableFact]
    public Task Redirect_PrintfNulEscape_FileHoldsARealNulByte() =>
        EqualAsync("", "printf 'x\\0' > f");

    [SkippableFact]
    public Task Redirect_PrintfOctalEscapes_FileBytesMatch() =>
        EqualAsync("", "printf 'a\\101\\0101b\\n' > f; printf '%b' 'p\\0101q' > g");

    [SkippableFact]
    public Task Redirect_EchoEOctalAndNul_FileBytesMatch() =>
        EqualAsync("", "echo -e 'a\\0101b\\0c\\101' > f");

    [SkippableFact] public Task Redirect_EchoDashN_FileBytesAreExactlyTheInput() =>
        EqualAsync("", "echo -n x > f");

    [SkippableFact] public Task Redirect_Append_PrintfThenPrintf_Concatenates() =>
        EqualAsync("", "printf a > f; printf b >> f");

    [SkippableFact] public Task Redirect_PrintfMultiLine_NoFinalNewline() =>
        EqualAsync("", "printf 'a\\nb' > f");

    [SkippableFact] public Task Redirect_BraceGroupMixedRecords_OnlyLastLosesNewline() =>
        EqualAsync("", "{ echo x; printf y; } > f");

    [SkippableFact] public Task Redirect_EmptyPrintf_TruncatesExistingFile() =>
        EqualAsync(Tree(("f", "OLD")), "printf '' > f");

    [SkippableFact] public Task Redirect_PlainEcho_KeepsNewline() =>
        EqualAsync("", "echo x > f");

    // ───────────── helper self-checks (no spawn) ─────────────

    [Fact]
    public void Compose_RunsSetupThenCommandThenSnapshot_InThatOrder()
    {
        var script = Compose(Tree(("a/b", "x")), "rm a/b");
        var setup = script.IndexOf("printf '%s\\n' 'x' > 'a/b'", StringComparison.Ordinal);
        var cmd = script.IndexOf("rm a/b", StringComparison.Ordinal);
        var snap = script.LastIndexOf("__fs_snap", StringComparison.Ordinal);
        Assert.True(setup >= 0 && setup < cmd && cmd < snap, script);
        Assert.Contains("echo \"== exit $?\"", script);
    }

    [Fact]
    public void Tree_QuotesPathsAndContentsLiterally()
    {
        var setup = Tree(("d/it's", "a'b"));
        Assert.Contains("'d/it'\\''s'", setup);
        Assert.Contains("'%s\\n' 'a'\\''b'", setup);
    }

    [Fact]
    public void Compose_StderrIsDiscardedUnlessRequested()
    {
        Assert.Contains("}" + " 2>/dev/null", Compose("", "x"));
        Assert.DoesNotContain("}" + " 2>/dev/null", Compose("", "x", compareStderr: true));
    }
}
