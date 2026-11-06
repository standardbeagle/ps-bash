using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// ExecutableLookup is the one "is this a runnable command on PATH" definition. Hand-written (no bash oracle): the
/// exec-bit fixtures need real files with real modes. Regression: a non-executable file earlier on PATH used to shadow
/// the real binary later on PATH (exit 13 "permission denied" where the OS keeps searching).
/// </summary>
public class ExecutableLookupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "psb_exelookup_" + Guid.NewGuid().ToString("N")[..8]);

    public ExecutableLookupTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Dir(string name) { var d = Path.Combine(_root, name); Directory.CreateDirectory(d); return d; }

    private static string Make(string dir, string file, bool exec)
    {
        var p = Path.Combine(dir, file);
        File.WriteAllText(p, "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(p, exec
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return p;
    }

    private static void AssertSame(string expected, string? actual) => Assert.Equal(expected, actual, ignoreCase: OperatingSystem.IsWindows());   // PATHEXT case (.EXE)

    private static string Name(string stem) => OperatingSystem.IsWindows() ? stem + ".exe" : stem;

    [SkippableFact]
    public void Find_NonExecutableFileEarlierOnPath_IsSkippedForTheExecutableLater()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows has no execute bit");
        var first = Dir("first"); var second = Dir("second");
        Make(first, "tool", exec: false);
        var real = Make(second, "tool", exec: true);
        AssertSame(real, ExecutableLookup.Find("tool", new[] { first, second }));
        Assert.False(ExecutableLookup.IsRunnable(Path.Combine(first, "tool")));
    }

    [Fact]
    public void Find_DirectoryNamedLikeTheCommand_IsSkipped()
    {
        var first = Dir("first"); var second = Dir("second");
        Directory.CreateDirectory(Path.Combine(first, Name("tool")));
        var real = Make(second, Name("tool"), exec: true);
        AssertSame(real, ExecutableLookup.Find("tool", new[] { first, second }));
        Assert.False(ExecutableLookup.IsRunnable(Path.Combine(first, Name("tool"))));
    }

    [Fact]
    public void Find_EmptyAndMalformedPathEntries_AreIgnored()
    {
        var d = Dir("good");
        var real = Make(d, Name("tool"), exec: true);
        var dirs = new[] { "", "   ", "bad\0dir", Path.Combine(_root, "missing"), d };
        AssertSame(real, ExecutableLookup.Find("tool", dirs));
    }

    [Fact]
    public void Find_MissingCommand_ReturnsNullAndNeverThrows()
    {
        Assert.Null(ExecutableLookup.Find("no-such-cmd-xyz", new[] { Dir("a") }));
        Assert.Null(ExecutableLookup.Find("", new[] { Dir("b") }));
        Assert.False(ExecutableLookup.IsRunnable(""));
        Assert.False(ExecutableLookup.IsRunnable("bad\0path"));
    }

    [Fact]
    public void Find_NameWithDirectorySeparator_IsCheckedAsAPathNotSearched()
    {
        var d = Dir("d");
        var real = Make(d, Name("tool"), exec: true);
        AssertSame(real, ExecutableLookup.Find(real, new[] { Dir("other") }));
        Assert.Null(ExecutableLookup.Find(Path.Combine(d, "nope"), new[] { d }));
    }

    [Fact]
    public void Find_ExtraFirstDir_WinsOverPath()
    {
        var cwd = Dir("cwd"); var onPath = Dir("onpath");
        var inCwd = Make(cwd, Name("tool"), exec: true);
        Make(onPath, Name("tool"), exec: true);
        AssertSame(inCwd, ExecutableLookup.Find("tool", SearchWith(cwd, onPath)));
    }

    private static IEnumerable<string> SearchWith(string first, string pathDir) => new[] { first, pathDir };

    [Fact]
    public void FindAll_ListsEveryRunnableMatchInPathOrder()
    {
        var a = Dir("a"); var b = Dir("b"); var c = Dir("c");
        var inA = Make(a, Name("tool"), exec: true);
        Make(b, "readme", exec: true);
        var inC = Make(c, Name("tool"), exec: true);
        var old = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, a, b, "", c));
            var all = ExecutableLookup.FindAll("tool"); Assert.Equal(2, all.Count); AssertSame(inA, all[0]); AssertSame(inC, all[1]);
        }
        finally { Environment.SetEnvironmentVariable("PATH", old); }
    }

    [SkippableFact]
    public void Find_Windows_BareNameFindsPathextSuffix()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PATHEXT is Windows-only");
        var d = Dir("w");
        var cmd = Make(d, "tool.cmd", exec: true);
        Assert.Equal(cmd, ExecutableLookup.Find("tool", new[] { d }), ignoreCase: true);
    }
}

