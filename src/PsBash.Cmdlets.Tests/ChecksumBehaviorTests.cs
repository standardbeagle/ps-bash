using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// End-to-end behaviour of md5sum/sha1sum/sha256sum against GNU coreutils 9.4 output (`wsl bash`, the
/// expected strings below are its literal output): names as typed, exact stdin bytes, --tag, -z, and the
/// whole check mode (OK/FAILED/FAILED open or read, WARNING summaries, --status/--quiet/--strict/
/// --warn/--ignore-missing, tag-format lists, exit statuses).
/// </summary>
public class ChecksumBehaviorTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private const string HelloMd5 = "b1946ac92492d2347c6235b4d2611184";   // printf 'hello\n' | md5sum
    private const string XMd5 = "9dd4e461268c8034f5c8564e155c67a6";       // printf 'x' | md5sum
    private const string XNlMd5 = "401b30e3b8b5d629635a5c613cdb7919";     // printf 'x\n' | md5sum

    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public ChecksumBehaviorTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), $"psb-cks-{Guid.NewGuid():N}".Substring(0, 20));
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "a"), "hello\n"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_dir, "b"), "x"u8.ToArray());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static string[] Texts(CmdResult r) => r.Lines.ToArray();

    /// <summary>Runs <paramref name="script"/> from the fixture directory; each emitted record is read as its BashText.</summary>
    private CmdResult RunText(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        return CmdResult.Run(pwsh,
            $"$ErrorActionPreference='Continue'; Push-Location '{_dir.Replace("'", "''")}'; " +
            $"try {{ & {{ {script} }} | ForEach-Object {{ Get-BashText $_ }} }} finally {{ Pop-Location }}");
    }

    [Fact]
    public void Hash_PrintsTheNameAsTyped_NotTheResolvedAbsolutePath()
    {
        var r = RunText("Invoke-BashMd5sum 'a'");
        Assert.Equal(new[] { HelloMd5 + "  a" }, Texts(r));
        Assert.Equal(0, r.ExitCode);
    }

    [Fact]
    public void Hash_StdinBytesAreExact_PrintfHasNoTrailingNewline()
    {
        Assert.Equal(new[] { XMd5 + "  -" }, Texts(RunText("Invoke-BashPrintf 'x' | Invoke-BashMd5sum")));
        Assert.Equal(new[] { XNlMd5 + "  -" }, Texts(RunText("Invoke-BashEcho 'x' | Invoke-BashMd5sum")));
    }

    [Fact]
    public void Hash_TagFormat_And_BinaryMarker()
    {
        Assert.Equal(new[] { "MD5 (a) = " + HelloMd5 }, Texts(RunText("Invoke-BashMd5sum '--tag' 'a'")));
        Assert.Equal(new[] { HelloMd5 + " *a" }, Texts(RunText("Invoke-BashMd5sum '-b' 'a'")));
        Assert.Equal(new[] { "MD5 (a) = " + HelloMd5 }, Texts(RunText("Invoke-BashMd5sum '--tag' '-b' 'a'")));
    }

    [Fact]
    public void Hash_Sha1AndSha256Tags_UseTheirOwnLabel()
    {
        Assert.StartsWith("SHA1 (a) = ", Texts(RunText("Invoke-BashSha1sum '--tag' 'a'"))[0]);
        Assert.StartsWith("SHA256 (a) = ", Texts(RunText("Invoke-BashSha256sum '--tag' 'a'"))[0]);
    }

    [Fact]
    public void Hash_ZeroTerminatesWithNulAndNoNewline()
    {
        var pwsh = _fixture.AcquireFresh();
        var objs = pwsh.AddScript($"Push-Location '{_dir.Replace("'", "''")}'; try {{ Invoke-BashMd5sum '-z' 'a' }} finally {{ Pop-Location }}").Invoke();
        pwsh.Commands.Clear();
        var o = Assert.Single(objs);
        Assert.Equal(HelloMd5 + "  a\0", (string)o.Properties["BashText"].Value);
        Assert.True((bool)o.Properties["NoTrailingNewline"].Value);
    }

    [Fact]
    public void Hash_MissingFile_ErrorsAndContinues_ExitOne()
    {
        var r = RunText("Invoke-BashMd5sum 'a' 'nosuch' 'b'");
        Assert.Equal(new[] { HelloMd5 + "  a", XMd5 + "  b" }, Texts(r));
        Assert.Contains(r.Errors, e => e.ToString() == "md5sum: nosuch: No such file or directory");
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void Hash_Directory_IsADirectory()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "dd"));
        var r = RunText("Invoke-BashMd5sum 'dd'");
        Assert.Contains(r.Errors, e => e.ToString() == "md5sum: dd: Is a directory");
        Assert.Equal(1, r.ExitCode);
    }

    // ---------------------------------------------------------------- check mode

    private void WriteList(string name, params string[] lines)
        => File.WriteAllText(Path.Combine(_dir, name), string.Join("\n", lines) + "\n");

    [Fact]
    public void Check_AllOk_ExitZero_BlankAndCommentLinesSkipped()
    {
        WriteList("s", HelloMd5 + "  a", "", "# comment", XMd5 + " *b");
        var r = RunText("Invoke-BashMd5sum '-c' 's'");
        Assert.Equal(new[] { "a: OK", "b: OK" }, Texts(r));
        Assert.Equal(0, r.ExitCode);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void Check_Mismatch_FailedLine_Warning_ExitOne()
    {
        WriteList("s", HelloMd5 + "  a", HelloMd5 + "  b");
        var r = RunText("Invoke-BashMd5sum '-c' 's'");
        Assert.Equal(new[] { "a: OK", "b: FAILED" }, Texts(r));
        Assert.Contains(r.Errors, e => e.ToString() == "md5sum: WARNING: 1 computed checksum did NOT match");
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void Check_TwoMismatches_PluralWarning()
    {
        WriteList("s", XMd5 + "  a", HelloMd5 + "  b");
        var r = RunText("Invoke-BashMd5sum '-c' 's'");
        Assert.Equal(new[] { "a: FAILED", "b: FAILED" }, Texts(r));
        Assert.Contains(r.Errors, e => e.ToString() == "md5sum: WARNING: 2 computed checksums did NOT match");
    }

    [Fact]
    public void Check_Status_PrintsNothing_OnlyExit()
    {
        WriteList("s", HelloMd5 + "  a", HelloMd5 + "  b");
        var r = RunText("Invoke-BashMd5sum '-c' '--status' 's'");
        Assert.Empty(Texts(r));
        Assert.Empty(r.Errors);
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void Check_Quiet_HidesOkKeepsFailed()
    {
        WriteList("s", HelloMd5 + "  a", HelloMd5 + "  b");
        var r = RunText("Invoke-BashMd5sum '-c' '--quiet' 's'");
        Assert.Equal(new[] { "b: FAILED" }, Texts(r));
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void Check_MissingListedFile_FailedOpenOrRead_AndWarning()
    {
        WriteList("s", HelloMd5 + "  a", XMd5 + "  nosuch");
        var r = RunText("Invoke-BashMd5sum '-c' 's'");
        Assert.Equal(new[] { "a: OK", "nosuch: FAILED open or read" }, Texts(r));
        Assert.Contains(r.Errors, e => e.ToString() == "md5sum: nosuch: No such file or directory");
        Assert.Contains(r.Errors, e => e.ToString() == "md5sum: WARNING: 1 listed file could not be read");
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void Check_IgnoreMissing_SkipsSilently_ButNothingVerifiedFails()
    {
        WriteList("s", HelloMd5 + "  a", XMd5 + "  nosuch");
        var ok = RunText("Invoke-BashMd5sum '-c' '--ignore-missing' 's'");
        Assert.Equal(new[] { "a: OK" }, Texts(ok));
        Assert.Equal(0, ok.ExitCode);

        WriteList("only", XMd5 + "  nosuch");
        var none = RunText("Invoke-BashMd5sum '-c' '--ignore-missing' 'only'");
        Assert.Contains(none.Errors, e => e.ToString() == "md5sum: only: no file was verified");
        Assert.Equal(1, none.ExitCode);
    }

    [Fact]
    public void Check_ImproperLines_WarnedAtTheEnd_FailOnlyUnderStrict()
    {
        WriteList("s", HelloMd5 + "  a", "xx", "yy");
        var lenient = RunText("Invoke-BashMd5sum '-c' 's'");
        Assert.Equal(new[] { "a: OK" }, Texts(lenient));
        Assert.Contains(lenient.Errors, e => e.ToString() == "md5sum: WARNING: 2 lines are improperly formatted");
        Assert.Equal(0, lenient.ExitCode);

        var strict = RunText("Invoke-BashMd5sum '-c' '--strict' 's'");
        Assert.Equal(1, strict.ExitCode);
    }

    [Fact]
    public void Check_Warn_ReportsEachImproperLineWithItsNumber()
    {
        WriteList("s", HelloMd5 + "  a", "xx", "yy");
        var r = RunText("Invoke-BashMd5sum '-c' '--warn' 's'");
        Assert.Contains(r.Errors, e => e.ToString() == "md5sum: s: 2: improperly formatted MD5 checksum line");
        Assert.Contains(r.Errors, e => e.ToString() == "md5sum: s: 3: improperly formatted MD5 checksum line");
    }

    [Fact]
    public void Check_NoProperLines_IsAnError()
    {
        WriteList("bad", "bad line");
        var r = RunText("Invoke-BashMd5sum '-c' 'bad'");
        Assert.Contains(r.Errors, e => e.ToString() == "md5sum: bad: no properly formatted checksum lines found");
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void Check_TagFormatList_IsAccepted_AndAnotherAlgorithmsTagIsImproper()
    {
        WriteList("t", "MD5 (a) = " + HelloMd5);
        var ok = RunText("Invoke-BashMd5sum '-c' 't'");
        Assert.Equal(new[] { "a: OK" }, Texts(ok));

        WriteList("t2", "SHA1 (a) = " + HelloMd5);
        var bad = RunText("Invoke-BashMd5sum '-c' 't2'");
        Assert.Contains(bad.Errors, e => e.ToString() == "md5sum: t2: no properly formatted checksum lines found");
    }

    [Fact]
    public void Check_ListFromStdin_AndRoundTripOfOwnOutput()
    {
        // `md5sum a | md5sum -c`: the names are as typed, so the list verifies from the same directory.
        var r = RunText("Invoke-BashMd5sum 'a' 'b' | Invoke-BashMd5sum '-c'");
        Assert.Equal(new[] { "a: OK", "b: OK" }, Texts(r));
        Assert.Equal(0, r.ExitCode);
    }

    [Fact]
    public void Check_MissingListFile_IsAnError()
    {
        var r = RunText("Invoke-BashMd5sum '-c' 'nosuch'");
        Assert.Contains(r.Errors, e => e.ToString() == "md5sum: nosuch: No such file or directory");
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void DirectCall_BareDashCAndDashW_BindDecoys()
    {
        // Pester/PowerShell callers type the bare flags: -c (Confirm) and -w (Warning*) collide with common parameters.
        WriteList("s", HelloMd5 + "  a", "xx");
        var r = RunText("Invoke-BashMd5sum -c -w s");
        Assert.Equal(new[] { "a: OK" }, Texts(r));
        Assert.Contains(r.Errors, e => e.ToString() == "md5sum: s: 2: improperly formatted MD5 checksum line");
    }

    [Theory]
    [InlineData("Invoke-BashMd5sum '--zzz'", 1, "unrecognized option '--zzz'")]
    [InlineData("Invoke-BashMd5sum '-x' 'a'", 1, "invalid option -- 'x'")]
    [InlineData("Invoke-BashMd5sum '--warn' 'a'", 1, "the --warn option is meaningful only when verifying checksums")]
    [InlineData("Invoke-BashMd5sum '-c' '--tag' 's'", 1, "the --tag option is meaningless when verifying checksums")]
    [InlineData("Invoke-BashMd5sum '--t' 'a'", 1, "option '--t' is ambiguous; possibilities: '--tag' '--text'")]
    public void UsageErrors_ExitOne_GnuWording(string script, int exit, string fragment)
    {
        var r = RunText(script);
        Assert.Equal(exit, r.ExitCode);
        Assert.Contains(r.Errors, e => e.ToString().Contains(fragment, StringComparison.Ordinal));
    }
}
