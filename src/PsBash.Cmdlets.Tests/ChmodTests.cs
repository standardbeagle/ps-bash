using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>chmod</c>. The argv scan is a pure table (a dash-leading word of mode characters is the MODE, as in GNU:
/// <c>chmod -x f</c>, <c>chmod -rw f</c>). The run cases call the cmdlet DIRECTLY with the common-parameter
/// colliding flags (<c>-c</c>, <c>-v</c>, <c>-f</c>). Messages checked against GNU coreutils 9.4 (<c>wsl bash</c>);
/// ps-bash quotes with straight quotes like every other diagnostic. The <c>-v</c> text is asserted here, not in the
/// differential suite: the oracle's working directory reports 0777 for every file.
/// </summary>
public class ChmodTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    public ChmodTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), $"psb-chm-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.GetFileSystemEntries(_tmp, "*", SearchOption.AllDirectories))
                FileSystemHelpers.ClearReadOnly(f);
            Directory.Delete(_tmp, recursive: true);
        }
        catch { /* best-effort */ }
    }

    private CmdResult InTmp(string body) => CmdResult.Run(_fixture.AcquireFresh(),
        $"Push-Location '{_tmp}'; try {{ {body} }} finally {{ Pop-Location }}");

    private string P(string rel) => Path.Combine(_tmp, rel);

    private static string Scan(params string[] argv)
    {
        var p = InvokeBashChmodCommand.Plan(argv);
        if (p.Error is { } e) return "ERR " + e.Split('\n')[0];
        static int B(bool b) => b ? 1 : 0;
        return $"R={B(p.Recursive)} v={B(p.Verbose)} c={B(p.Changes)} f={B(p.Quiet)} mode={p.Mode} files=[{string.Join(",", p.Files)}]";
    }

    [Theory]
    [InlineData("R=0 v=0 c=0 f=0 mode=644 files=[a]", "644", "a")]
    [InlineData("R=0 v=0 c=0 f=0 mode=+x files=[a,b]", "+x", "a", "b")]
    [InlineData("R=0 v=0 c=0 f=0 mode=-x files=[a]", "-x", "a")]
    [InlineData("R=0 v=0 c=0 f=0 mode=-rw files=[a]", "-rw", "a")]
    [InlineData("R=0 v=0 c=0 f=0 mode=-w,+x files=[a]", "-w,+x", "a")]
    [InlineData("R=1 v=0 c=0 f=0 mode=go-w files=[d]", "-R", "go-w", "d")]
    [InlineData("R=1 v=1 c=0 f=0 mode=755 files=[d]", "-Rv", "755", "d")]
    [InlineData("R=0 v=0 c=1 f=1 mode=u+x files=[a]", "-c", "-f", "u+x", "a")]
    [InlineData("R=1 v=1 c=1 f=1 mode=a=r files=[a]", "--recursive", "--verbose", "--changes", "--quiet", "a=r", "a")]
    [InlineData("R=0 v=0 c=0 f=0 mode=644 files=[-v]", "--", "644", "-v")]
    [InlineData("R=0 v=1 c=0 f=0 mode=644 files=[a]", "644", "a", "-v")]
    [InlineData("ERR chmod: missing operand")]
    [InlineData("ERR chmod: missing operand after '644'", "644")]
    [InlineData("ERR chmod: missing operand after '+x'", "-v", "+x")]
    [InlineData("ERR chmod: invalid option -- 'q'", "-q", "644", "a")]
    [InlineData("ERR chmod: invalid option -- 'z'", "-Rz", "644", "a")]
    [InlineData("ERR chmod: unrecognized option '--bogus'", "--bogus", "644", "a")]
    [InlineData("ERR chmod: --reference is not supported by ps-bash", "--reference=x", "a")]
    public void Plan_ScansArgv(string expected, params string[] argv) => Assert.Equal(expected, Scan(argv));

    [Theory]
    [InlineData(0x1ED, "0755 (rwxr-xr-x)")]
    [InlineData(0x1A4, "0644 (rw-r--r--)")]
    [InlineData(0x124, "0444 (r--r--r--)")]
    [InlineData(0xFFF, "7777 (rwsrwsrwt)")]
    [InlineData(0xE00, "7000 (--S--S--T)")]
    [InlineData(0, "0000 (---------)")]
    public void Describe_IsGnuVerboseForm(int mode, string expected)
        => Assert.Equal(expected, InvokeBashChmodCommand.Describe(mode));

    [Fact]
    public void Chmod_PlusX_Succeeds()
    {
        File.WriteAllText(P("s.sh"), "echo hi\n");
        InTmp("Invoke-BashChmod +x s.sh").AssertSuccess();
    }

    [Fact]
    public void Chmod_DirectFlags_C_V_F_AreChmodFlags()
    {
        File.WriteAllText(P("a"), "x");
        var r = InTmp("Invoke-BashChmod -c 444 a; Invoke-BashChmod -v 444 a; Invoke-BashChmod -f 644 nosuch; $global:LASTEXITCODE");
        Assert.Contains("mode of 'a' changed from ", r.Stdout);
        Assert.Contains(" to 0444 (r--r--r--)", r.Stdout);
        Assert.Contains("mode of 'a' retained as 0444 (r--r--r--)", r.Stdout);
        Assert.DoesNotContain("nosuch", r.Stderr);
        Assert.EndsWith("1", r.Stdout.TrimEnd());
    }

    [Fact]
    public void Chmod_WithoutOwnerWrite_MakesFileReadOnly_AndPlusWRestores()
    {
        File.WriteAllText(P("a"), "x");
        InTmp("Invoke-BashChmod -w a").AssertSuccess(); // direct call: -w is the mode, not -WarningAction
        if (OperatingSystem.IsWindows()) Assert.True(new FileInfo(P("a")).IsReadOnly);
#pragma warning disable CA1416
        else Assert.Equal(0, (int)File.GetUnixFileMode(P("a")) & 0x92);
#pragma warning restore CA1416
        InTmp("Invoke-BashChmod u+w a").AssertSuccess();
        File.WriteAllText(P("a"), "y");
        Assert.Equal("y", File.ReadAllText(P("a")));
    }

    [Fact]
    public void Chmod_Recursive_ReachesNestedFiles()
    {
        Directory.CreateDirectory(P("d/e"));
        File.WriteAllText(P("d/e/f"), "x");
        InTmp("Invoke-BashChmod -R a-w d").AssertSuccess();
        if (OperatingSystem.IsWindows()) Assert.True(new FileInfo(P("d/e/f")).IsReadOnly);
        InTmp("Invoke-BashChmod -R u+w d").AssertSuccess();
        File.WriteAllText(P("d/e/f"), "y");
    }

    [Theory]
    [InlineData("Invoke-BashChmod", "chmod: missing operand")]
    [InlineData("Invoke-BashChmod 644", "chmod: missing operand after '644'")]
    [InlineData("Invoke-BashChmod zzz a", "chmod: invalid mode: 'zzz'")]
    [InlineData("Invoke-BashChmod 9999 a", "chmod: invalid mode: '9999'")]
    [InlineData("Invoke-BashChmod 644 nosuch", "chmod: cannot access 'nosuch': No such file or directory")]
    public void Chmod_UsageError_Fails1(string call, string stderr)
    {
        File.WriteAllText(P("a"), "x");
        InTmp(call).AssertFailed(1, stderr);
    }

    [Fact]
    public void Chmod_MissingFileAmongOthers_StillChangesTheRest()
    {
        File.WriteAllText(P("a"), "x");
        InTmp("Invoke-BashChmod a-w nosuch a").AssertFailed(1, "cannot access 'nosuch'");
        if (OperatingSystem.IsWindows()) Assert.True(new FileInfo(P("a")).IsReadOnly);
    }
}
