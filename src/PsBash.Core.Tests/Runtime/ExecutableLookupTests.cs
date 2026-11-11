using Xunit;

namespace PsBash.Core.Tests.Runtime;

public class ExecutableLookupTests : IDisposable
{
    private readonly List<string> _dirs = new();

    private string Dir()
    {
        var d = Directory.CreateTempSubdirectory("psb-exelookup").FullName;
        _dirs.Add(d);
        return d;
    }

    public void Dispose()
    {
        foreach (var d in _dirs) try { Directory.Delete(d, true); } catch { }
    }

    private static void Same(string expected, string? actual) =>
        Assert.Equal(expected, actual, ignoreCase: OperatingSystem.IsWindows());

    private static string Name(string bare) => OperatingSystem.IsWindows() ? bare + ".exe" : bare;

    private static string Make(string dir, string bare, bool executable)
    {
        var p = Path.Combine(dir, Name(bare));
        File.WriteAllText(p, "");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                    (executable ? UnixFileMode.UserExecute : 0));
        return p;
    }

    [Fact]
    public void Find_ReturnsFirstRunnableInPathOrder()
    {
        var a = Dir(); var b = Dir();
        var first = Make(a, "tool", true); Make(b, "tool", true);
        Same(first, ExecutableLookup.Find("tool", [a, b]));
    }

    [Fact]
    public void Find_SkipsNonExecutableShadowOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        var shadow = Dir(); var real = Dir();
        Make(shadow, "env", false);
        var good = Make(real, "env", true);
        Same(good, ExecutableLookup.Find("env", [shadow, real]));
        Assert.Null(ExecutableLookup.Find("env", [shadow]));
    }

    [Fact]
    public void Find_SkipsDirectoriesNamedLikeTheCommand()
    {
        var a = Dir(); var b = Dir();
        Directory.CreateDirectory(Path.Combine(a, Name("tool")));
        var good = Make(b, "tool", true);
        Same(good, ExecutableLookup.Find("tool", [a, b]));
    }

    [Fact]
    public void Find_ToleratesBlankAndMalformedEntries()
    {
        var a = Dir();
        var good = Make(a, "tool", true);
        Same(good, ExecutableLookup.Find("tool", ["", "  ", "\0bad", a]));
    }

    [Fact]
    public void FindAll_ListsEveryRunnableMatchInOrder()
    {
        var a = Dir(); var b = Dir(); var c = Dir();
        var pa = Make(a, "tool", true);
        if (!OperatingSystem.IsWindows()) Make(b, "tool", false);
        var pc = Make(c, "tool", true);
        var all = ExecutableLookup.FindAll("tool", [a, b, c]).ToArray();
        Assert.Equal(2, all.Length);
        Same(pa, all[0]); Same(pc, all[1]);
    }

    [Fact]
    public void Find_EmptyCommandOrAbsent_IsNull()
    {
        Assert.Null(ExecutableLookup.Find("", [Path.GetTempPath()]));
        Assert.Null(ExecutableLookup.Find("definitely-not-here-xyz", [Path.GetTempPath()]));
    }

    [Fact]
    public void PathDirectories_PutsFirstDirAheadOfPath_AndDropsEmptyEntries()
    {
        var a = Dir(); var b = Dir(); var first = Dir();
        var old = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, a, "", " ", b));
            Assert.Equal(new[] { first, a, b }, ExecutableLookup.PathDirectories(first).Where(d => !string.IsNullOrWhiteSpace(d)));
            Assert.Equal(new[] { a, b }, ExecutableLookup.PathDirectories());
            Make(b, "tool", true);
            Same(Path.Combine(b, Name("tool")), ExecutableLookup.Find("tool", ExecutableLookup.PathDirectories(first)));
        }
        finally { Environment.SetEnvironmentVariable("PATH", old); }
    }

    // A script writes PATH with `:` (`PATH=_tmp:$PATH`); on Windows each separating colon becomes `;`, a drive colon stays.
    [Theory]
    [InlineData(@"_tmp:C:\Program Files\x;C:\y", @"_tmp;C:\Program Files\x;C:\y")]
    [InlineData("a:b:c", "a;b;c")]
    [InlineData(@"C:\Users\me/bin:C:/tools", @"C:\Users\me/bin;C:/tools")]
    [InlineData("/usr/local/bin:/usr/bin", "/usr/local/bin;/usr/bin")]
    [InlineData("x:C:", "x;C:")]
    [InlineData(@"C:\a;D:\b", @"C:\a;D:\b")]
    [InlineData("ab:", "ab;")]
    public void ToWindowsPathList_SplitsBashSeparatorsAndKeepsDriveColons(string value, string expected)
        => Assert.Equal(expected, ExecutableLookup.ToWindowsPathList(value));
}
