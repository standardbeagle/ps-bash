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

    // ───────────── tee ─────────────

    [SkippableFact] public Task Tee_PrintfWithoutNewline_FileBytesAreExactlyTheInput() =>
        EqualAsync(
            "",
            "printf x | tee f");

    [SkippableFact] public Task Tee_DoubleDash_DashNamedFileIsAnOperand() =>
        EqualAsync(
            "",
            "printf 'hi\\n' | tee -- -zz");

    // ───────────── known product bug (found by this helper) ─────────────

    // Confirmed against GNU bash: `printf x > f` leaves f = "x" (1 byte). ps-bash writes "x\n":
    // Invoke-BashRedirect joins records and always appends "\n", ignoring the NoTrailingNewline
    // marker that printf / echo -n set (tee already honours it). Same for `echo -n x > f`.
    [SkippableFact(Skip = "PRODUCT BUG: `printf x > f` / `echo -n x > f` write a trailing newline (Invoke-BashRedirect ignores NoTrailingNewline). Remove Skip when fixed and record the cassette.")]
    public Task Redirect_PrintfWithoutNewline_FileBytesAreExactlyTheInput() =>
        EqualAsync("", "printf x > f");

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
