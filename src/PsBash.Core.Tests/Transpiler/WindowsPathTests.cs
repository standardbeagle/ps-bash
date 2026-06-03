using PsBash.Core;
using Xunit;

namespace PsBash.Core.Tests.Transpiler;

/// <summary>
/// Unit tests for the shared <see cref="WindowsPath"/> mapper. Pure string logic,
/// platform-independent (the rules are about path SHAPE, not the running OS), so
/// these run everywhere. Covers every variant the emitter and runtime feed it.
/// </summary>
public class WindowsPathTests
{
    // --- TryMapUnixDrivePath: the narrow /c/ /mnt/c/ transform ---

    [Theory]
    [InlineData("/c/Users/andyb", "C:\\Users\\andyb")]
    [InlineData("/c/Users/andyb/foo.log", "C:\\Users\\andyb\\foo.log")]
    [InlineData("/d/data/file.txt", "D:\\data\\file.txt")]
    [InlineData("/C/Users", "C:\\Users")]          // uppercase drive letter
    [InlineData("/c", "C:\\")]                       // bare MSYS drive root
    [InlineData("/c/", "C:\\")]                      // drive root with trailing slash
    [InlineData("/mnt/c/Users/andyb", "C:\\Users\\andyb")]
    [InlineData("/mnt/d/data", "D:\\data")]
    [InlineData("/mnt/c", "C:\\")]                   // bare WSL drive root
    [InlineData("/mnt/c/", "C:\\")]
    public void TryMapUnixDrivePath_MapsUnixDriveForms(string input, string expected)
    {
        Assert.True(WindowsPath.TryMapUnixDrivePath(input, out var result));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("/usr/bin")]          // bare POSIX path — NOT a drive path
    [InlineData("/etc/hosts")]
    [InlineData("/cc/x")]             // two-letter first segment, not a drive
    [InlineData("/mnt/cc/x")]         // two-letter WSL segment, not a drive
    [InlineData("/mnt/foo")]          // /mnt/<word> where word isn't a single drive letter
    [InlineData("foo/bar")]           // relative
    [InlineData("./foo")]
    [InlineData("C:\\Users")]         // already native
    [InlineData("c:/users")]          // native, forward slashes
    [InlineData("//server/share")]    // UNC
    [InlineData("\\\\server\\share")] // UNC backslash
    [InlineData("/")]                 // root only
    [InlineData("")]
    [InlineData(null)]
    public void TryMapUnixDrivePath_LeavesNonUnixDrivePathsAlone(string? input)
    {
        Assert.False(WindowsPath.TryMapUnixDrivePath(input, out var result));
        Assert.Equal(input ?? string.Empty, result);
    }

    // --- Normalize: full canonicalization for known file paths ---

    [Theory]
    [InlineData("/c/Users/x", "C:\\Users\\x")]        // unix drive
    [InlineData("/mnt/c/Users/x", "C:\\Users\\x")]    // wsl
    [InlineData("c:/users/x", "C:\\users\\x")]        // native lowercase + fwd slashes
    [InlineData("C:/Users/x", "C:\\Users\\x")]        // native fwd slashes
    [InlineData("C:\\Users\\x", "C:\\Users\\x")]      // already canonical
    [InlineData("d:\\data", "D:\\data")]              // lowercase drive
    [InlineData("c:foo", "C:foo")]                     // drive-relative preserved (no sep added)
    [InlineData("c:/", "C:\\")]
    public void Normalize_CanonicalizesAllDriveForms(string input, string expected)
        => Assert.Equal(expected, WindowsPath.Normalize(input));

    [Theory]
    [InlineData("/usr/bin")]          // POSIX path untouched
    [InlineData("/etc/hosts")]
    [InlineData("foo/bar")]           // relative untouched (separators preserved)
    [InlineData("foo\\bar")]
    [InlineData("//server/share")]    // UNC untouched
    [InlineData("")]
    public void Normalize_LeavesNonDrivePathsAlone(string input)
        => Assert.Equal(input, WindowsPath.Normalize(input));

    [Fact]
    public void Normalize_NullReturnsEmpty()
        => Assert.Equal(string.Empty, WindowsPath.Normalize(null));
}
