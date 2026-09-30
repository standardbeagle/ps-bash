using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>cp --preserve[=ATTR_LIST]</c> and <c>--no-preserve=ATTR_LIST</c>. Attribute words are
/// mode, timestamps, ownership, links, context, xattr and all (unique prefixes accepted); a bare
/// <c>--preserve</c> and <c>-p</c> mean mode,ownership,timestamps; the options are applied in
/// command-line order, so the LAST one naming an attribute wins. Every expectation below was checked
/// against GNU coreutils 9.4 (<c>wsl bash</c>). What maps where: timestamps are real on every OS;
/// mode is the Unix permission bits (or, on Windows, the read-only attribute — the only part of a
/// mode Windows has); ownership, links and xattr are accepted and have nothing to do here (no error,
/// like GNU on a filesystem that cannot hold them); <c>context</c> requested by name is GNU's
/// "SELinux-enabled kernel" error, and <c>all</c> silently skips it.
/// </summary>
public class CpPreserveTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private static readonly DateTime Old = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Local);

    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    public CpPreserveTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), $"psb-cpp-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmp);
        File.WriteAllText(P("s"), "hi\n");
        File.SetLastWriteTime(P("s"), Old);
        File.SetLastAccessTime(P("s"), Old);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_tmp, "*", SearchOption.AllDirectories)) FileSystemHelpers.ClearReadOnly(f);
            Directory.Delete(_tmp, recursive: true);
        }
        catch { /* best-effort */ }
    }

    private CmdResult InTmp(string body) => CmdResult.Run(_fixture.AcquireFresh(),
        $"Push-Location '{_tmp}'; try {{ {body} }} finally {{ Pop-Location }}");

    private string P(string rel) => Path.Combine(_tmp, rel);

    private bool MtimeIsOld(string rel) => Math.Abs((File.GetLastWriteTime(P(rel)) - Old).TotalSeconds) < 2;

    // ───────────── which attributes each spelling preserves ─────────────

    [Fact]
    public void Default_DoesNotPreserveTheModificationTime()
    {
        InTmp("Invoke-BashCp s d").AssertSuccess();
        Assert.False(MtimeIsOld("d"));
    }

    [Theory]
    [InlineData("--preserve")]                        // bare: mode,ownership,timestamps
    [InlineData("--preserve=timestamps")]
    [InlineData("--preserve=t")]                      // unique prefix
    [InlineData("--preserve=ti")]
    [InlineData("'--preserve=mode,timestamps'")]
    [InlineData("--preserve=all")]
    [InlineData("--preserve=a")]
    [InlineData("'--preserve=timestamps,ownership'")]
    [InlineData("--pre=timestamps")]                  // abbreviated option name
    [InlineData("--preserve=ownership --preserve=timestamps")]
    public void PreservesTheModificationTime(string flags)
    {
        InTmp($"Invoke-BashCp {flags} s d").AssertSuccess();
        Assert.True(MtimeIsOld("d"), flags);
    }

    [Theory]
    [InlineData("--preserve=mode")]
    [InlineData("--preserve=ownership")]
    [InlineData("--preserve=links")]
    [InlineData("--preserve=xattr")]
    [InlineData("'--preserve=mode,ownership,links,xattr'")]
    [InlineData("--no-preserve=all")]
    public void DoesNotPreserveTheModificationTime(string flags)
    {
        InTmp($"Invoke-BashCp {flags} s d").AssertSuccess();
        Assert.False(MtimeIsOld("d"), flags);
    }

    [Fact]
    public void ShortDashP_StillPreservesTimestamps()
    {
        InTmp("Invoke-BashCp -p s d").AssertSuccess();
        Assert.True(MtimeIsOld("d"));
    }

    [Fact]
    public void Archive_PreservesEverything()
    {
        InTmp("Invoke-BashCp '-a' s d").AssertSuccess();
        Assert.True(MtimeIsOld("d"));
    }

    // ───────────── order: the last option naming an attribute wins ─────────────

    [Theory]
    [InlineData("--preserve=timestamps --no-preserve=timestamps", false)]
    [InlineData("--no-preserve=timestamps --preserve=timestamps", true)]
    [InlineData("'-p' --no-preserve=timestamps", false)]
    [InlineData("--no-preserve=timestamps '-p'", true)]    // -p AFTER the clear preserves again (quoted: a bare -p binds the decoy and loses its position)
    [InlineData("--no-preserve=all '-p'", true)]
    [InlineData("--preserve=all --no-preserve=mode", true)] // mode is cleared, timestamps remain
    [InlineData("--preserve=all --no-preserve=all", false)]
    [InlineData("--no-preserve=t", false)]
    public void LastOptionWins(string flags, bool timestampsPreserved)
    {
        InTmp($"Invoke-BashCp {flags} s d").AssertSuccess();
        Assert.Equal(timestampsPreserved, MtimeIsOld("d"));
    }

    // ───────────── errors: nothing is copied ─────────────

    [Fact]
    public void Context_ByName_IsGnusSelinuxError_AndNothingIsCopied()
    {
        InTmp("Invoke-BashCp --preserve=context s d")
            .AssertFailed(1, "cp: cannot preserve security context without an SELinux-enabled kernel");
        Assert.False(File.Exists(P("d")));
    }

    [Fact]
    public void Context_AmongOthers_IsTheSameError()
    {
        InTmp("Invoke-BashCp '--preserve=mode,context' s d")
            .AssertFailed(1, "cp: cannot preserve security context without an SELinux-enabled kernel");
        Assert.False(File.Exists(P("d")));
    }

    [Fact]
    public void NoPreserveContext_IsFine()
    {
        InTmp("Invoke-BashCp --no-preserve=context s d").AssertSuccess();
        Assert.True(File.Exists(P("d")));
    }

    [Fact]
    public void Bogus_IsAGnuUsageErrorWithTheValidList_AndNothingIsCopied()
    {
        InTmp("Invoke-BashCp --preserve=bogus s d").AssertFailed(1,
            "cp: invalid argument 'bogus' for '--preserve'",
            "Valid arguments are:",
            "- 'mode'", "- 'timestamps'", "- 'ownership'", "- 'links'", "- 'context'", "- 'xattr'", "- 'all'",
            "Try 'cp --help' for more information.");
        Assert.False(File.Exists(P("d")));
    }

    [Fact]
    public void Bogus_AfterAGoodWord_StillCopiesNothing()
    {
        InTmp("Invoke-BashCp '--preserve=mode,bogus' s d").AssertFailed(1, "invalid argument 'bogus'");
        Assert.False(File.Exists(P("d")));
    }

    [Fact]
    public void NoPreserve_Bogus_NamesTheNoPreserveOption()
    {
        InTmp("Invoke-BashCp --no-preserve=bogus s d").AssertFailed(1, "cp: invalid argument 'bogus' for '--no-preserve'");
    }

    [Fact]
    public void EmptyList_IsAmbiguous()
    {
        InTmp("Invoke-BashCp --preserve= s d").AssertFailed(1, "cp: ambiguous argument '' for '--preserve'");
        Assert.False(File.Exists(P("d")));
    }

    [Fact]
    public void NoPreserve_TakesTheNextWordAsItsArgument()
    {
        // `--no-preserve` has a REQUIRED argument: here it swallows the source operand, like GNU.
        InTmp("Invoke-BashCp --no-preserve s d").AssertFailed(1, "cp: invalid argument 's' for '--no-preserve'");
    }

    [Fact]
    public void NoLongerRefusedAsUnsupported()
    {
        var r = InTmp("Invoke-BashCp --preserve=timestamps s d");
        Assert.DoesNotContain("not supported", r.Stderr);
    }

    // ───────────── recursive copy ─────────────

    [Fact]
    public void Recursive_PreservesTimestampsOfFilesAndDirectories()
    {
        Directory.CreateDirectory(P("sd"));
        File.WriteAllText(P("sd/f"), "x\n");
        File.SetLastWriteTime(P("sd/f"), Old);
        Directory.SetLastWriteTime(P("sd"), Old);

        InTmp("Invoke-BashCp -r --preserve=timestamps sd dd").AssertSuccess();

        Assert.True(MtimeIsOld("dd/f"));
        Assert.True(Math.Abs((Directory.GetLastWriteTime(P("dd")) - Old).TotalSeconds) < 2);
    }

    // ───────────── mode ─────────────

    [SkippableFact]
    public void Windows_NoPreserveMode_ClearsTheReadOnlyAttribute_DefaultAndPreserveKeepIt()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "read-only attribute is the Windows mapping of the mode");
        File.SetAttributes(P("s"), FileAttributes.ReadOnly);
        InTmp("Invoke-BashCp s keep; Invoke-BashCp --preserve=mode s pres; Invoke-BashCp --no-preserve=mode s clear").AssertSuccess();
        Assert.True((File.GetAttributes(P("keep")) & FileAttributes.ReadOnly) != 0);
        Assert.True((File.GetAttributes(P("pres")) & FileAttributes.ReadOnly) != 0);
        Assert.False((File.GetAttributes(P("clear")) & FileAttributes.ReadOnly) != 0);
    }

#pragma warning disable CA1416 // guarded by Skip.If(OperatingSystem.IsWindows())
    [SkippableFact]
    public void Unix_ModeFollowsGnu_DefaultKeepsSourceBitsMaskedByUmask_PreserveIsExact_NoPreserveIs0666()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix mode bits only exist on Unix");
        File.SetUnixFileMode(P("s"), (UnixFileMode)0x1A0); // 0640
        int umask = FileModeSpec.CurrentUmask();

        InTmp("Invoke-BashCp s keep; Invoke-BashCp --preserve=mode s pres; Invoke-BashCp --no-preserve=mode s clear").AssertSuccess();

        Assert.Equal(0x1A0 & ~umask, (int)File.GetUnixFileMode(P("keep")));
        Assert.Equal(0x1A0, (int)File.GetUnixFileMode(P("pres")));
        Assert.Equal(0x1B6 & ~umask, (int)File.GetUnixFileMode(P("clear")));  // 0666 & ~umask
    }

    [SkippableFact]
    public void Unix_AnExistingDestinationKeepsItsModeUnlessModeIsPreserved()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix mode bits only exist on Unix");
        File.SetUnixFileMode(P("s"), (UnixFileMode)0x124); // 0444
        File.WriteAllText(P("e1"), "x\n");
        File.WriteAllText(P("e2"), "x\n");
        File.SetUnixFileMode(P("e1"), (UnixFileMode)0x1B6); // 0666
        File.SetUnixFileMode(P("e2"), (UnixFileMode)0x1B6);

        InTmp("Invoke-BashCp s e1; Invoke-BashCp --preserve=mode s e2").AssertSuccess();

        Assert.Equal(0x1B6, (int)File.GetUnixFileMode(P("e1")));  // untouched, as GNU
        Assert.Equal(0x124, (int)File.GetUnixFileMode(P("e2")));  // --preserve=mode applies the source's
    }
#pragma warning restore CA1416
}
