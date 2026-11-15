using Xunit;

namespace PsBash.Core.Tests.Transpiler;

/// <summary>
/// <see cref="BashPath.ToShell(string?, bool)"/>: the one outbound path mapping (docs/specs/paths.md).
/// Pure, with the platform explicit, so every case runs on every OS.
/// Oracle note (qa-rubric Directive 1): a ps-bash platform mapping; bash on Linux never sees a backslash path.
/// </summary>
public class BashPathTests
{
    [Theory]
    [InlineData(@"C:\Users\x", "C:/Users/x")]
    [InlineData(@"C:\", "C:/")]
    [InlineData(@"c:\a b\c", "c:/a b/c")]
    [InlineData("C:/Users/x", "C:/Users/x")]
    [InlineData(@"C:/mixed\sep", "C:/mixed/sep")]
    [InlineData(@"\\server\share\dir", "//server/share/dir")]
    [InlineData(@"\\?\C:\long\path", "C:/long/path")]
    [InlineData(@"\\?\UNC\server\share\x", "//server/share/x")]
    [InlineData(@"sub\dir", "sub/dir")]
    [InlineData(@"..\x", "../x")]
    [InlineData("rel/already", "rel/already")]
    [InlineData("/usr/bin", "/usr/bin")]
    [InlineData("", "")]
    public void Windows_ForwardSlashesKeepTheDrive(string native, string expected)
        => Assert.Equal(expected, BashPath.ToShell(native, windows: true));

    [Theory]
    [InlineData(@"C:\Users\x")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\C:\x")]
    [InlineData("sub/dir")]
    public void Idempotent(string native)
    {
        var once = BashPath.ToShell(native, windows: true);
        Assert.Equal(once, BashPath.ToShell(once, windows: true));
    }

    [Theory]
    [InlineData(@"a\b")]
    [InlineData(@"/home/u/odd\name")]
    public void OffWindows_ABackslashIsAFileNameCharacter(string path)
        => Assert.Equal(path, BashPath.ToShell(path, windows: false));

    [Fact]
    public void Null_IsEmpty()
    {
        Assert.Equal("", BashPath.ToShell(null, windows: true));
        Assert.Equal("", BashPath.ToShell(null, windows: false));
    }

    [Theory]
    [InlineData(@"C:\Users\x")]
    [InlineData(@"C:\a b\c")]
    public void RoundTrip_TheInboundMapperReadsTheShellSpellingBack(string native)
        => Assert.Equal(native, WindowsPath.Normalize(BashPath.ToShell(native, windows: true)));
}
