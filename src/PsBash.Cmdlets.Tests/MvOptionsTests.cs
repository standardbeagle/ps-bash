using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// mv <c>-i</c> / <c>-t</c> / <c>-T</c> / <c>-b</c> / <c>-S</c> / <c>-u</c> / <c>--update=WHEN</c> behaviours.
/// Expectations were read from GNU coreutils 9.4 (<c>wsl bash</c>): verbose lines are
/// <c>renamed 'a' -> 'b'</c>, a declined <c>-i</c> prompt exits 1, <c>--update=none</c> skips silently.
/// </summary>
public class MvOptionsTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    public MvOptionsTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), $"psb-mvo-{Guid.NewGuid():N}".Substring(0, 22));
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

    [Fact]
    public void Verbose_IsGnusRenamedLine()
    {
        Write("a");
        var r = InTmp("Invoke-BashMv '-v' a b").AssertSuccess();
        Assert.Equal("renamed 'a' -> 'b'", r.Stdout.Trim());
    }

    // ───────────── -i ─────────────

    [Fact]
    public void Interactive_PipedYes_Overwrites_AndPromptsOnStderr()
    {
        Write("a", "new"); Write("b", "old");
        var r = InTmp("'y' | Invoke-BashMv '-i' a b");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("new", Read("b"));
        Assert.False(Exists("a"));
        Assert.Contains("mv: overwrite 'b'? ", r.Stderr);
    }

    [Fact]
    public void Interactive_No_KeepsBoth_AndExitsOne()
    {
        Write("a", "new"); Write("b", "old");
        var r = InTmp("'n' | Invoke-BashMv '-i' a b");
        r.AssertFailed(1, "mv: overwrite 'b'? ");
        Assert.Equal("old", Read("b"));
        Assert.Equal("new", Read("a"));
    }

    [Fact]
    public void Interactive_NoAnswerAtAll_IsNo()
    {
        Write("a", "new"); Write("b", "old");
        var r = InTmp("Invoke-BashMv '-i' a b");
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("old", Read("b"));
    }

    [Fact]
    public void Interactive_NoPromptForANewDestination()
    {
        Write("a");
        InTmp("Invoke-BashMv '-i' a n").AssertSuccess();
        Assert.True(Exists("n"));
    }

    [Fact]
    public void Interactive_LastOfIAndNWins()
    {
        Write("a", "new"); Write("b", "old");
        // -n after -i: no prompt, "not replacing".
        var r = InTmp("Invoke-BashMv '-i' '-n' a b");
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("old", Read("b"));
        // -f after -i: replaces without asking.
        InTmp("Invoke-BashMv '-i' '-f' a b").AssertSuccess();
        Assert.Equal("new", Read("b"));
    }

    // ───────────── -t / -T ─────────────

    [Fact]
    public void TargetDirectory_MovesEverySourceIntoIt()
    {
        Write("a", "A"); Write("b", "B"); Directory.CreateDirectory(Path.Combine(_tmp, "d"));
        InTmp("Invoke-BashMv '-t' d a b").AssertSuccess();
        Assert.Equal("A", Read("d/a"));
        Assert.Equal("B", Read("d/b"));
    }

    [Fact]
    public void TargetDirectory_Missing_IsGnusError()
    {
        Write("a");
        InTmp("Invoke-BashMv '-t' nodir a").AssertFailed(1, "mv: target directory 'nodir': No such file or directory");
        Assert.True(Exists("a"));
    }

    [Fact]
    public void TargetDirectory_NotADirectory_IsGnusError()
    {
        Write("a"); Write("f");
        InTmp("Invoke-BashMv '-t' f a").AssertFailed(1, "mv: target directory 'f': Not a directory");
    }

    [Fact]
    public void NoTargetDirectory_TreatsDirectoryDestinationAsAName()
    {
        Write("a");
        Directory.CreateDirectory(Path.Combine(_tmp, "d"));
        // d is an existing (empty) directory and a is a file: GNU refuses to overwrite a directory with a non-directory.
        var r = InTmp("Invoke-BashMv '-T' a d");
        r.AssertFailed(1, "mv: cannot overwrite directory 'd' with non-directory");
        Assert.True(Exists("a"));
    }

    [Fact]
    public void NoTargetDirectory_PlainRename()
    {
        Write("a", "A");
        InTmp("Invoke-BashMv '-T' a z").AssertSuccess();
        Assert.Equal("A", Read("z"));
    }

    [Fact]
    public void NoTargetDirectory_ThreeOperands_IsExtraOperand()
    {
        Write("a"); Write("b"); Write("c");
        InTmp("Invoke-BashMv '-T' a b c").AssertFailed(1, "mv: extra operand 'c'");
    }

    [Fact]
    public void TargetDirectoryAndNoTargetDirectory_Conflict()
    {
        Write("a"); Directory.CreateDirectory(Path.Combine(_tmp, "d"));
        InTmp("Invoke-BashMv '-t' d '-T' a").AssertFailed(1, "cannot combine --target-directory (-t) and --no-target-directory (-T)");
    }

    // ───────────── -b / -S ─────────────

    [Fact]
    public void Backup_Simple_RenamesOldToTilde_AndVerboseNotesIt()
    {
        Write("a", "new"); Write("b", "old");
        var r = InTmp("Invoke-BashMv '-vb' a b").AssertSuccess();
        Assert.Equal("renamed 'a' -> 'b' (backup: 'b~')", r.Stdout.Trim());
        Assert.Equal("new", Read("b"));
        Assert.Equal("old", Read("b~"));
    }

    [Fact]
    public void Backup_Numbered_CountsUp()
    {
        Write("b", "v0");
        Write("a", "v1"); InTmp("Invoke-BashMv '--backup=numbered' a b").AssertSuccess();
        Write("a", "v2"); InTmp("Invoke-BashMv '--backup=numbered' a b").AssertSuccess();
        Assert.Equal("v0", Read("b.~1~"));
        Assert.Equal("v1", Read("b.~2~"));
        Assert.Equal("v2", Read("b"));
    }

    [Fact]
    public void Backup_ExistingSwitchesToNumberedOnceANumberedBackupExists()
    {
        Write("b", "v0"); Write("b.~1~", "n1");
        Write("a", "v1");
        InTmp("Invoke-BashMv '--backup=existing' a b").AssertSuccess();
        Assert.Equal("v0", Read("b.~2~"));
        Assert.Equal("v1", Read("b"));
    }

    [Fact]
    public void Backup_CustomSuffix_AloneTurnsBackupsOn()
    {
        Write("a", "new"); Write("b", "old");
        InTmp("Invoke-BashMv '-S' '.bak' a b").AssertSuccess();
        Assert.Equal("old", Read("b.bak"));
        Assert.Equal("new", Read("b"));
    }

    [Fact]
    public void Backup_None_MakesNoBackup()
    {
        Write("a", "new"); Write("b", "old");
        InTmp("Invoke-BashMv '--backup=none' a b").AssertSuccess();
        Assert.False(Exists("b~"));
    }

    [Fact]
    public void Backup_BadControlWord_IsGnusError()
    {
        Write("a"); Write("b");
        InTmp("Invoke-BashMv '--backup=bogus' a b").AssertFailed(1, "invalid argument 'bogus' for 'backup type'");
    }

    [Fact]
    public void Backup_NoExistingDestination_MakesNoBackup()
    {
        Write("a");
        var r = InTmp("Invoke-BashMv '-vb' a b").AssertSuccess();
        Assert.Equal("renamed 'a' -> 'b'", r.Stdout.Trim());
        Assert.False(Exists("b~"));
    }

    // ───────────── -u / --update ─────────────

    [Fact]
    public void Update_KeepsADestinationNotOlderThanTheSource()
    {
        Write("a", "src"); Write("b", "dst");
        File.SetLastWriteTimeUtc(Path.Combine(_tmp, "a"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_tmp, "b"), new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var r = InTmp("Invoke-BashMv '-u' a b");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("dst", Read("b"));
        Assert.True(Exists("a"));
    }

    [Fact]
    public void Update_ReplacesAnOlderDestination()
    {
        Write("a", "src"); Write("b", "dst");
        File.SetLastWriteTimeUtc(Path.Combine(_tmp, "a"), new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_tmp, "b"), new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        InTmp("Invoke-BashMv '--update=older' a b").AssertSuccess();
        Assert.Equal("src", Read("b"));
        Assert.False(Exists("a"));
    }

    [Fact]
    public void Update_None_SkipsSilently_ExitZero()
    {
        Write("a", "src"); Write("b", "dst");
        var r = InTmp("Invoke-BashMv '--update=none' a b").AssertSuccess();
        Assert.Equal("", r.Stdout.Trim());
        Assert.Equal("dst", Read("b"));
        Assert.True(Exists("a"));
    }

    [Fact]
    public void Update_All_ReplacesAlways()
    {
        Write("a", "src"); Write("b", "dst");
        File.SetLastWriteTimeUtc(Path.Combine(_tmp, "a"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_tmp, "b"), new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        InTmp("Invoke-BashMv '--update=all' a b").AssertSuccess();
        Assert.Equal("src", Read("b"));
    }

    [Fact]
    public void Update_BadWord_IsGnusError()
    {
        Write("a"); Write("b");
        InTmp("Invoke-BashMv '--update=sometimes' a b").AssertFailed(1, "invalid argument 'sometimes' for '--update'");
    }

    [Fact]
    public void Update_NewDestination_JustMoves()
    {
        Write("a", "src");
        InTmp("Invoke-BashMv '-u' a b").AssertSuccess();
        Assert.Equal("src", Read("b"));
    }

    // ───────────── direct-call decoy (-i / -v) ─────────────

    [Fact]
    public void DirectCall_BareDashI_BindsDecoy_AndPrompts()
    {
        Write("a", "new"); Write("b", "old");
        var r = InTmp("'y' | Invoke-BashMv -i a b");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("new", Read("b"));
    }
}
