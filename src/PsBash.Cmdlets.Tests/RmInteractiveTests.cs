using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// rm <c>-d</c> / <c>--dir</c>, <c>-i</c>, <c>-I</c> and <c>--interactive[=WHEN]</c>. Every expected
/// prompt, exit status and resulting tree was checked against GNU coreutils 9.4 (<c>wsl bash</c>).
/// Answers come from the pipeline (the stdin of the transpiled command); with none, the answer is
/// "no", exactly like GNU reading EOF. The prompt is written to the error stream (the only stream the
/// host delivers to stderr) WITHOUT touching the exit status, so a declined prompt still exits 0.
/// </summary>
public class RmInteractiveTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    public RmInteractiveTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), $"psb-rmi-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    private CmdResult InTmp(string body) => CmdResult.Run(_fixture.AcquireFresh(),
        $"Push-Location '{_tmp}'; try {{ {body} }} finally {{ Pop-Location }}");

    private void File1(string rel, string content = "x\n")
    {
        var p = Path.Combine(_tmp, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private void Dir(string rel) => Directory.CreateDirectory(Path.Combine(_tmp, rel));
    private bool Exists(string rel) => File.Exists(Path.Combine(_tmp, rel)) || Directory.Exists(Path.Combine(_tmp, rel));

    /// <summary>Exit 0 and exactly these prompts (in order) on stderr.</summary>
    private static void AssertPrompts(CmdResult r, params string[] prompts)
    {
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(prompts, r.Errors.Select(e => e.ToString()).ToArray());
    }

    // ───────────── -d / --dir ─────────────

    [Fact]
    public void D_RemovesAnEmptyDirectory()
    {
        Dir("e");
        InTmp("Invoke-BashRm -d e").AssertSuccess();
        Assert.False(Exists("e"));
    }

    [Fact]
    public void LongDir_RemovesAnEmptyDirectory()
    {
        Dir("e");
        InTmp("Invoke-BashRm --dir e").AssertSuccess();
        Assert.False(Exists("e"));
    }

    [Fact]
    public void D_NonEmptyDirectory_FailsWithDirectoryNotEmpty()
    {
        File1("ne/g");
        InTmp("Invoke-BashRm -d ne").AssertFailed(1, "rm: cannot remove 'ne': Directory not empty");
        Assert.True(Exists("ne/g"));
    }

    [Fact]
    public void D_RegularFile_IsRemovedAsUsual()
    {
        File1("f");
        InTmp("Invoke-BashRm -d f").AssertSuccess();
        Assert.False(Exists("f"));
    }

    [Fact]
    public void D_MissingOperand_StillReportsNoSuchFile()
    {
        InTmp("Invoke-BashRm -d nosuch").AssertFailed(1, "rm: cannot remove 'nosuch': No such file or directory");
    }

    [Fact]
    public void D_WithR_RemovesANonEmptyTree()
    {
        File1("ne/g");
        InTmp("Invoke-BashRm -rd ne").AssertSuccess();
        Assert.False(Exists("ne"));
    }

    [Fact]
    public void D_Verbose_SaysRemovedDirectory()
    {
        Dir("e1");
        var r = InTmp("Invoke-BashRm -dv e1").AssertSuccess();
        Assert.Equal("removed directory 'e1'", r.Stdout.Trim());
    }

    [Fact]
    public void RecursiveVerbose_UsesGnuWordingAndChildrenBeforeParent()
    {
        File1("d/f");
        File1("d/e/inner");
        var r = InTmp("Invoke-BashRm -rv d").AssertSuccess();
        Assert.Contains("removed 'd/f'", r.Lines);
        Assert.Contains("removed 'd/e/inner'", r.Lines);
        Assert.Contains("removed directory 'd/e'", r.Lines);
        Assert.Equal("removed directory 'd'", r.Lines[^1]);
        var lines = r.Lines.ToList();
        Assert.True(lines.IndexOf("removed 'd/e/inner'") < lines.IndexOf("removed directory 'd/e'"));
    }

    // ───────────── -i ─────────────

    [Fact]
    public void I_Yes_RemovesAfterPrompting()
    {
        File1("a");
        var r = InTmp("'yes' | Invoke-BashRm -i a");
        AssertPrompts(r, "rm: remove regular file 'a'? ");
        Assert.False(Exists("a"));
    }

    [Fact]
    public void I_No_KeepsTheFileAndExitsZero()
    {
        File1("b");
        var r = InTmp("'no' | Invoke-BashRm -i b");
        AssertPrompts(r, "rm: remove regular file 'b'? ");
        Assert.True(Exists("b"));
    }

    [Fact]
    public void I_NoAnswerAtAll_IsNo()
    {
        File1("c");
        var r = InTmp("Invoke-BashRm -i c");
        AssertPrompts(r, "rm: remove regular file 'c'? ");
        Assert.True(Exists("c"));
    }

    [Theory]
    [InlineData("y", true)]
    [InlineData("Y", true)]
    [InlineData("YES", true)]
    [InlineData("yeah", true)]
    [InlineData("n", false)]
    [InlineData("ok", false)]
    [InlineData("", false)]
    public void I_OnlyAFirstLetterYCountsAsYes(string answer, bool removed)
    {
        File1("f");
        InTmp($"'{answer}' | Invoke-BashRm -i f");
        Assert.Equal(!removed, Exists("f"));
    }

    [Fact]
    public void I_OneAnswerPerOperand_InOrder()
    {
        File1("c");
        File1("d");
        var r = InTmp("'y','n' | Invoke-BashRm -i c d");
        AssertPrompts(r, "rm: remove regular file 'c'? ", "rm: remove regular file 'd'? ");
        Assert.False(Exists("c"));
        Assert.True(Exists("d"));
    }

    [Fact]
    public void I_EmptyFile_SaysRegularEmptyFile()
    {
        File1("emp", "");
        var r = InTmp("'y' | Invoke-BashRm -i emp");
        AssertPrompts(r, "rm: remove regular empty file 'emp'? ");
        Assert.False(Exists("emp"));
    }

    [Fact]
    public void I_MissingOperand_DoesNotPrompt()
    {
        var r = InTmp("'y' | Invoke-BashRm -i nosuch");
        r.AssertFailed(1, "rm: cannot remove 'nosuch': No such file or directory");
        Assert.DoesNotContain("remove regular", r.Stderr);
    }

    [Fact]
    public void I_Verbose_PrintsRemovedAfterYes()
    {
        File1("f2");
        var r = InTmp("'y' | Invoke-BashRm '-iv' f2");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("removed 'f2'", r.Stdout.Trim());
    }

    [Fact]
    public void I_LastOfFAndIWins_IThenF_DoesNotPrompt()
    {
        File1("z2");
        InTmp("'n' | Invoke-BashRm -if z2").AssertSuccess();
        Assert.False(Exists("z2"));
    }

    [Fact]
    public void I_LastOfFAndIWins_FThenI_Prompts()
    {
        File1("z1");
        var r = InTmp("'n' | Invoke-BashRm -fi z1");
        AssertPrompts(r, "rm: remove regular file 'z1'? ");
        Assert.True(Exists("z1"));
    }

    [Fact]
    public void I_WithF_MissingOperandIsSilent()
    {
        var r = InTmp("Invoke-BashRm -if nosuch").AssertSuccess();
        Assert.Empty(r.Errors);
    }

    // ───────────── -i -r (directories) ─────────────

    [Fact]
    public void Ir_AsksDescendThenRemoveForEveryEntry()
    {
        File1("q/r/s");
        var r = InTmp("'y','y','y','y','y' | Invoke-BashRm -ir q");
        AssertPrompts(r,
            "rm: descend into directory 'q'? ",
            "rm: descend into directory 'q/r'? ",
            "rm: remove regular file 'q/r/s'? ",
            "rm: remove directory 'q/r'? ",
            "rm: remove directory 'q'? ");
        Assert.False(Exists("q"));
    }

    [Fact]
    public void Ir_DecliningToDescend_KeepsTheWholeSubtree_AndSkipsTheRemovePrompt()
    {
        File1("p/q/r/f");
        var r = InTmp("'y','n' | Invoke-BashRm -ir p");
        AssertPrompts(r, "rm: descend into directory 'p'? ", "rm: descend into directory 'p/q'? ");
        Assert.True(Exists("p/q/r/f"));
    }

    [Fact]
    public void Id_EmptyDirectory_AsksRemoveDirectory()
    {
        Dir("em");
        var r = InTmp("'y' | Invoke-BashRm -id em");
        AssertPrompts(r, "rm: remove directory 'em'? ");
        Assert.False(Exists("em"));
    }

    [Fact]
    public void Id_NonEmptyDirectory_FailsWithoutPrompting()
    {
        File1("ne/x");
        var r = InTmp("'y' | Invoke-BashRm -id ne");
        r.AssertFailed(1, "rm: cannot remove 'ne': Directory not empty");
        Assert.DoesNotContain("remove directory", r.Stderr);
    }

    [Fact]
    public void I_DirectoryWithoutR_IsStillAnError_NoPrompt()
    {
        Dir("dd");
        var r = InTmp("'y' | Invoke-BashRm -i dd");
        r.AssertFailed(1, "rm: cannot remove 'dd': Is a directory");
        Assert.DoesNotContain("remove directory", r.Stderr);
    }

    // ───────────── -I (once) ─────────────

    [Fact]
    public void BigI_FourFiles_PromptsOnceForAllArguments()
    {
        foreach (var n in new[] { "a1", "a2", "a3", "a4" }) File1(n);
        var r = InTmp("'y' | Invoke-BashRm -I a1 a2 a3 a4");
        AssertPrompts(r, "rm: remove 4 arguments? ");
        foreach (var n in new[] { "a1", "a2", "a3", "a4" }) Assert.False(Exists(n));
    }

    [Fact]
    public void BigI_FourFiles_DecliningRemovesNothingAndExitsZero()
    {
        foreach (var n in new[] { "a1", "a2", "a3", "a4" }) File1(n);
        var r = InTmp("'n' | Invoke-BashRm -I a1 a2 a3 a4");
        AssertPrompts(r, "rm: remove 4 arguments? ");
        foreach (var n in new[] { "a1", "a2", "a3", "a4" }) Assert.True(Exists(n));
    }

    [Fact]
    public void BigI_ThreeFiles_DoesNotPrompt()
    {
        foreach (var n in new[] { "g1", "g2", "g3" }) File1(n);
        var r = InTmp("'n' | Invoke-BashRm '-I' g1 g2 g3").AssertSuccess();
        Assert.Empty(r.Errors);
        foreach (var n in new[] { "g1", "g2", "g3" }) Assert.False(Exists(n));
    }

    [Fact]
    public void BigI_Recursive_PromptsRecursivelyEvenForOneArgument()
    {
        File1("k/l/f");
        var r = InTmp("'y' | Invoke-BashRm '-I' -r k");
        AssertPrompts(r, "rm: remove 1 argument recursively? ");
        Assert.False(Exists("k"));
    }

    [Fact]
    public void BigI_Recursive_ManyArguments_PluralWording()
    {
        Dir("m1");
        Dir("m2");
        var r = InTmp("'n' | Invoke-BashRm '-I' -r m1 m2");
        AssertPrompts(r, "rm: remove 2 arguments recursively? ");
        Assert.True(Exists("m1"));
    }

    [Fact]
    public void BigI_SingleFile_DoesNotPrompt()
    {
        File1("b1");
        var r = InTmp("'n' | Invoke-BashRm '-I' b1").AssertSuccess();
        Assert.Empty(r.Errors);
        Assert.False(Exists("b1"));
    }

    // ───────────── --interactive[=WHEN] ─────────────

    [Theory]
    [InlineData("--interactive=never")]
    [InlineData("--interactive=no")]
    [InlineData("--interactive=none")]
    [InlineData("--interactive=n")]
    public void InteractiveNever_DoesNotPrompt(string flag)
    {
        File1("w1");
        var r = InTmp($"'n' | Invoke-BashRm {flag} w1").AssertSuccess();
        Assert.Empty(r.Errors);
        Assert.False(Exists("w1"));
    }

    [Theory]
    [InlineData("--interactive")]
    [InlineData("--interactive=always")]
    [InlineData("--interactive=yes")]
    [InlineData("--interactive=a")]
    public void InteractiveAlwaysAndBare_PromptPerFile(string flag)
    {
        File1("w2");
        var r = InTmp($"'n' | Invoke-BashRm {flag} w2");
        AssertPrompts(r, "rm: remove regular file 'w2'? ");
        Assert.True(Exists("w2"));
    }

    [Theory]
    [InlineData("--interactive=once")]
    [InlineData("--interactive=o")]
    public void InteractiveOnce_PromptsOncePerBatch(string flag)
    {
        foreach (var n in new[] { "w5", "w6", "w7", "w8" }) File1(n);
        var r = InTmp($"'n' | Invoke-BashRm {flag} w5 w6 w7 w8");
        AssertPrompts(r, "rm: remove 4 arguments? ");
        Assert.True(Exists("w5"));
    }

    [Fact]
    public void InteractiveBogus_IsAGnuUsageError()
    {
        File1("w4");
        var r = InTmp("Invoke-BashRm --interactive=bogus w4");
        r.AssertFailed(1,
            "rm: invalid argument 'bogus' for '--interactive'",
            "Valid arguments are:",
            "'never', 'no', 'none'",
            "'once'",
            "'always', 'yes'");
        Assert.True(Exists("w4"));
    }

    [Fact]
    public void InteractiveEmptyValue_IsAmbiguous()
    {
        File1("o4");
        InTmp("Invoke-BashRm --interactive= o4").AssertFailed(1, "rm: ambiguous argument '' for '--interactive'");
        Assert.True(Exists("o4"));
    }

    [Fact]
    public void Interactive_NoOperands_IsAMissingOperandError()
    {
        InTmp("Invoke-BashRm -i").AssertFailed(1, "rm: missing operand");
    }

    // ───────────── direct-call decoys ─────────────

    [Fact]
    public void DirectCall_BareDashD_BindsTheDecoyAndStillRemovesTheDirectory()
    {
        Dir("dd2");
        // No quoting: PowerShell binds the bare -d to the decoy switch, which is re-injected.
        InTmp("Invoke-BashRm -d dd2").AssertSuccess();
        Assert.False(Exists("dd2"));
    }

    [Fact]
    public void DirectCall_BareDashI_PromptsInsteadOfCrashingTheBinder()
    {
        File1("bi");
        var r = InTmp("'y' | Invoke-BashRm -i bi");
        AssertPrompts(r, "rm: remove regular file 'bi'? ");
        Assert.False(Exists("bi"));
    }
}
