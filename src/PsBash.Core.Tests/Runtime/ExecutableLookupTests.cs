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
}
