using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>mkdir -m MODE</c> / <c>--mode=MODE</c>. Unix applies the mode with
/// <see cref="File.SetUnixFileMode(string, UnixFileMode)"/>; Windows has no mode bits, so the only
/// representable part is honoured: a mode without the owner-write bit sets the read-only attribute.
/// Expected modes were checked against GNU coreutils 9.4 (<c>wsl bash</c>); an invalid mode is
/// <c>mkdir: invalid mode 'X'</c>, exit 1, and nothing is created (GNU quotes with locale curly
/// quotes; ps-bash uses straight quotes like every other diagnostic).
/// </summary>
public class MkdirModeTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    public MkdirModeTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), $"psb-mkm-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try
        {
            foreach (var d in Directory.GetDirectories(_tmp, "*", SearchOption.AllDirectories))
                FileSystemHelpers.ClearReadOnly(d);
            Directory.Delete(_tmp, recursive: true);
        }
        catch { /* best-effort */ }
    }

    private CmdResult InTmp(string body) => CmdResult.Run(_fixture.AcquireFresh(),
        $"Push-Location '{_tmp}'; try {{ {body} }} finally {{ Pop-Location }}");

    private string P(string rel) => Path.Combine(_tmp, rel);

#pragma warning disable CA1416 // guarded by Skip.If(OperatingSystem.IsWindows())
    private int UnixMode(string rel) => (int)File.GetUnixFileMode(P(rel));
#pragma warning restore CA1416

    [SkippableTheory]
    [InlineData("-m 700", "d", 0x1C0)]                 // 0700
    [InlineData("-m 0755", "d", 0x1ED)]                // 0755
    [InlineData("--mode=750", "d", 0x1E8)]             // 0750
    [InlineData("-m u=rwx,go=rx", "d", 0x1ED)]         // 0755
    [InlineData("-m a=rx", "d", 0x16D)]                // 0555
    [InlineData("-m u+w,g-r", "d", 0x1DF)]             // 0737 from base 0777
    [InlineData("-m 0", "d", 0)]
    public void Unix_AppliesTheRequestedMode(string flags, string name, int expected)
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix mode bits only exist on Unix");
        InTmp($"Invoke-BashMkdir {flags} {name}").AssertSuccess();
        try { Assert.Equal(expected, UnixMode(name)); }
        finally { File.SetUnixFileMode(P(name), (UnixFileMode)0x1ED); } // let Dispose delete it
    }

    [SkippableFact]
    public void Unix_WithParents_OnlyTheFinalDirectoryGetsTheMode()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix mode bits only exist on Unix");
        InTmp("Invoke-BashMkdir -p -m 700 p/q/r").AssertSuccess();
        Assert.Equal(0x1C0, UnixMode("p/q/r"));
        Assert.NotEqual(0x1C0, UnixMode("p")); // intermediates keep the umask default (GNU)
    }

    [SkippableFact]
    public void Unix_AttachedBundleForm_PmWorks()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix mode bits only exist on Unix");
        InTmp("Invoke-BashMkdir '-pm700' a/b").AssertSuccess();
        Assert.Equal(0x1C0, UnixMode("a/b"));
    }

    [Fact]
    public void Mode_InvalidIsAGnuUsageError_AndNothingIsCreated()
    {
        InTmp("Invoke-BashMkdir -m 999 d").AssertFailed(1, "mkdir: invalid mode '999'");
        Assert.False(Directory.Exists(P("d")));
    }

    [Fact]
    public void Mode_InvalidSymbolicIsRejected_BeforeAnyOperandIsCreated()
    {
        InTmp("Invoke-BashMkdir -m rwx a b").AssertFailed(1, "mkdir: invalid mode 'rwx'");
        Assert.False(Directory.Exists(P("a")));
        Assert.False(Directory.Exists(P("b")));
    }

    [Fact]
    public void Mode_EmptyIsInvalid()
    {
        InTmp("Invoke-BashMkdir -m '' d").AssertFailed(1, "mkdir: invalid mode ''");
        Assert.False(Directory.Exists(P("d")));
    }

    [Fact]
    public void Mode_MissingArgumentIsAGetoptError()
    {
        InTmp("Invoke-BashMkdir d -m").AssertFailed(1, "mkdir: option requires an argument -- 'm'");
    }

    [Fact]
    public void Mode_OnAnExistingDirectory_StillReportsFileExists_AndLeavesItAlone()
    {
        Directory.CreateDirectory(P("a"));
        InTmp("Invoke-BashMkdir -m 700 a").AssertFailed(1, "mkdir: cannot create directory 'a': File exists");
    }

    [Fact]
    public void Mode_Verbose_StillAnnouncesTheDirectory()
    {
        var r = InTmp("Invoke-BashMkdir -v -m 755 vv").AssertSuccess();
        Assert.Equal("mkdir: created directory 'vv'", r.Stdout.Trim());
    }

    [Fact]
    public void Mode_NoLongerRefusedAsUnsupported()
    {
        var r = InTmp("Invoke-BashMkdir -m 755 ok");
        Assert.DoesNotContain("not supported", r.Stderr);
        Assert.True(Directory.Exists(P("ok")));
    }

    [SkippableTheory]
    [InlineData("-m 555", true)]    // no owner write bit -> read-only attribute
    [InlineData("-m 0", true)]
    [InlineData("-m 400", true)]
    [InlineData("-m 755", false)]
    [InlineData("-m 700", false)]
    [InlineData("-m 200", false)]
    public void Windows_ReadOnlyAttributeFollowsTheOwnerWriteBit(string flags, bool readOnly)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the read-only attribute mapping is the Windows behaviour");
        InTmp($"Invoke-BashMkdir {flags} d").AssertSuccess();
        var attrs = File.GetAttributes(P("d"));
        Assert.Equal(readOnly, (attrs & FileAttributes.ReadOnly) != 0);
    }
}
