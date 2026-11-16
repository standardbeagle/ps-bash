using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// <see cref="DevicePath"/> is the ONE list of <c>/dev/*</c> device names (docs/specs/paths.md "Device names").
/// Oracle note (qa-rubric Directive 1): bash has no classifier to compare with; the names and what each device does
/// are checked against bash by <c>DeviceNameDifferentialTests</c>. These pin the spellings, including the Windows
/// forms the inbound path mappers produce before a site sees the name.
/// </summary>
public class DevicePathTests
{
    [Theory]
    [InlineData("/dev/null", DeviceKind.Null, -1)]
    [InlineData("/dev/stdin", DeviceKind.Stdin, 0)]
    [InlineData("/dev/stdout", DeviceKind.Stdout, 1)]
    [InlineData("/dev/stderr", DeviceKind.Stderr, 2)]
    [InlineData("/dev/fd/0", DeviceKind.Stdin, 0)]
    [InlineData("/dev/fd/1", DeviceKind.Stdout, 1)]
    [InlineData("/dev/fd/2", DeviceKind.Stderr, 2)]
    [InlineData("/dev/fd/3", DeviceKind.Fd, 3)]
    [InlineData("/dev/fd/63", DeviceKind.Fd, 63)]
    [InlineData("/dev/full", DeviceKind.Full, -1)]
    [InlineData("/dev/zero", DeviceKind.Zero, -1)]
    [InlineData("/dev/random", DeviceKind.Random, -1)]
    [InlineData("/dev/urandom", DeviceKind.Random, -1)]
    [InlineData("/dev/tty", DeviceKind.Tty, -1)]
    public void Classify_BashSpelling_OnEveryPlatform(string path, DeviceKind kind, int fd)
    {
        foreach (bool windows in new[] { true, false })
        {
            Assert.Equal(kind, DevicePath.Classify(path, windows, out int got));
            Assert.Equal(fd, got);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("dev/stdout")]            // relative: a real file
    [InlineData("./dev/null")]
    [InlineData("/dev/stdout2")]
    [InlineData("/dev/nullx")]
    [InlineData("/dev/")]
    [InlineData("/dev")]
    [InlineData("/dev/fd/")]
    [InlineData("/dev/fd/x")]
    [InlineData("/dev/fd/-1")]
    [InlineData("/dev/fd/3/x")]
    [InlineData("/dev/fd/99999999999")]
    [InlineData("/dev/sda")]
    [InlineData("/tmp/stdout")]
    [InlineData("/x/dev/null")]
    [InlineData(@"C:\work\dev\zero")]     // a project directory called dev, not the drive's /dev
    [InlineData("C:/work/dev/stdout")]
    [InlineData("stdout")]
    public void Classify_NotADevice(string? path)
    {
        Assert.Equal(DeviceKind.None, DevicePath.Classify(path, windows: true, out _));
        Assert.Equal(DeviceKind.None, DevicePath.Classify(path, windows: false, out _));
    }

    // What GetUnresolvedProviderPathFromPSPath / Path.GetFullPath / RuntimePath.Map turn "/dev/x" into on Windows: the
    // spelling that reached BashFdTable.Open and was opened as a file (the bug this type exists for).
    [Theory]
    [InlineData(@"C:\dev\stdout", DeviceKind.Stdout)]
    [InlineData("C:/dev/stdout", DeviceKind.Stdout)]
    [InlineData(@"p:\DEV\StdErr", DeviceKind.Stderr)]
    [InlineData(@"C:\dev\null", DeviceKind.Null)]
    [InlineData(@"C:\dev\fd\7", DeviceKind.Fd)]
    [InlineData("C:/dev/fd/0", DeviceKind.Stdin)]
    [InlineData(@"\\server\share\dev\zero", DeviceKind.Zero)]
    [InlineData("//server/share/dev/urandom", DeviceKind.Random)]
    [InlineData(@"\dev\tty", DeviceKind.Tty)]
    [InlineData("/DEV/FULL", DeviceKind.Full)]
    [InlineData("NUL", DeviceKind.Null)]
    [InlineData("nul", DeviceKind.Null)]
    [InlineData(@"C:\some\dir\NUL", DeviceKind.Null)]
    public void Classify_WindowsMappedSpellings_AreTheDeviceOnWindowsOnly(string path, DeviceKind kind)
    {
        Assert.Equal(kind, DevicePath.Classify(path, windows: true, out _));
        Assert.Equal(DeviceKind.None, DevicePath.Classify(path, windows: false, out _));
    }

    [Fact]
    public void Classify_WindowsDriveForm_NeedsTheDriveRoot()
    {
        Assert.Equal(DeviceKind.None, DevicePath.Classify(@"C:dev\null", windows: true, out _));
        Assert.Equal(DeviceKind.None, DevicePath.Classify(@"\\server\dev\null", windows: true, out _)); // "dev" is the share
        Assert.Equal(DeviceKind.None, DevicePath.Classify("CNUL", windows: true, out _));
    }

    [Fact]
    public void StreamKinds_AreNeverAnOsFile_OnAnyPlatform()
    {
        foreach (var kind in new[] { DeviceKind.Stdin, DeviceKind.Stdout, DeviceKind.Stderr, DeviceKind.Fd })
        {
            Assert.True(DevicePath.IsStream(kind));
            Assert.Null(DevicePath.NativeDevice(kind, windows: true));
            Assert.Null(DevicePath.NativeDevice(kind, windows: false));
        }
        Assert.False(DevicePath.IsStream(DeviceKind.Null));
        Assert.False(DevicePath.IsStream(DeviceKind.None));
    }

    [Fact]
    public void NativeDevice_IsTheRealDeviceOffWindows_AndOnlyNulOnWindows()
    {
        Assert.Equal("NUL", DevicePath.NativeDevice(DeviceKind.Null, windows: true));
        foreach (var kind in new[] { DeviceKind.Zero, DeviceKind.Full, DeviceKind.Random, DeviceKind.Tty })
        {
            Assert.Null(DevicePath.NativeDevice(kind, windows: true));
            Assert.Equal(kind, DevicePath.Classify(DevicePath.NativeDevice(kind, windows: false), windows: false, out _));
        }
        Assert.Equal(DevicePath.Null, DevicePath.NativeDevice(DeviceKind.Null, windows: false));
    }
}

/// <summary>
/// Keeps the device names in ONE place (docs/specs/paths.md "Device names"): product code asks
/// <see cref="DevicePath.Classify(string?)"/> and never compares against its own <c>"/dev/stdout"</c> literal. Each
/// private list (redirect, awk, find, sed, tee, test, ...) knew a different subset, and a site with no list - the fd
/// table behind <c>{var}&gt;/dev/stdout</c> - opened <c>C:\dev\stdout</c>. The allow-list names each remaining literal
/// and why it is not a device test.
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent.
/// </summary>
public class DevicePathGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }

    // A C# string or char-span literal that holds a device name: "/dev/stdout", "2>/dev/null", "/dev/fd/", "/dev/".
    private static readonly Regex Forbidden = new(
        @"""[^""\r\n]*/dev/(?:null|stdin|stdout|stderr|fd|zero|full|u?random|tty|"")",
        RegexOptions.Compiled);

    /// <summary>file name -> why its literal is not a device-name test.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["DevicePath.cs"] = "the primitive itself",
        ["DetachedProcess.cs"] = "text of a /bin/sh script run by the real sh on Linux/macOS (nohup)",
        ["PipedInteractive.cs"] = "bash source text fed to the shell (`history -r 2>/dev/null`)",
    };

    private static readonly string[] ProductDirs =
        ["PsBash.Cmdlets", "PsBash.Transpiler", "PsBash.Host", "PsBash.Core", "PsBash.Shell"];

    [Fact]
    public void ProductCode_NamesDevicesOnlyThroughDevicePath()
    {
        var offenders = new List<string>();
        var matchedAllowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in ProductDirs)
        {
            var dir = Path.Combine(RepoRoot(), "src", project);
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                var name = Path.GetFileName(file);
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    var t = lines[i].TrimStart();
                    if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal)) continue;
                    if (!Forbidden.IsMatch(StripTrailingComment(lines[i]))) continue;
                    if (Allowed.ContainsKey(name)) { matchedAllowed.Add(name); continue; }
                    offenders.Add($"{project}/{name}:{i + 1}: {t}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Recognise a /dev/* device name with PsBash.Core.DevicePath.Classify (constants: DevicePath.Null / Stdin / Tty), " +
            "not a literal: a private list misses names the other sites handle, and a missed name is opened as C:\\dev\\<name>. " +
            "Offenders:\n  " + string.Join("\n  ", offenders));

        var stale = Allowed.Keys.Where(k => !matchedAllowed.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Allow-list entries that no longer hold a device literal (remove them):\n" + string.Join("\n", stale));
    }

    /// <summary>The code part of a line: a <c>//</c> comment that starts outside a string literal is dropped.</summary>
    private static string StripTrailingComment(string line)
    {
        bool inString = false;
        for (int i = 0; i < line.Length - 1; i++)
        {
            char c = line[i];
            if (c == '\\' && inString) { i++; continue; }
            if (c == '"') inString = !inString;
            else if (!inString && c == '/' && line[i + 1] == '/') return line.Substring(0, i);
        }
        return line;
    }

    [Fact]
    public void TheGuard_CatchesEachPrivateListShape()
    {
        // The shapes the per-site lists had.
        Assert.Matches(Forbidden, "if (path == \"/dev/stdout\") sink = new StdoutSink(this);");
        Assert.Matches(Forbidden, "else if (name is \"/dev/stderr\" or \"/dev/fd/2\") sink = x;");
        Assert.Matches(Forbidden, "\"/dev/stdout\" or \"/dev/fd/1\" => RedirectDevice.Stdout,");
        Assert.Matches(Forbidden, "if (slashed.EndsWith(\"/dev/null\", StringComparison.OrdinalIgnoreCase))");
        Assert.Matches(Forbidden, "if (slashed == \"/dev/zero\") return true;");
        Assert.Matches(Forbidden, "literalTarget.StartsWith(\"/dev/\", StringComparison.Ordinal)");
        Assert.Matches(Forbidden, "p.StartsWith(\"/dev/fd/\")");
        Assert.Matches(Forbidden, "open(\"/dev/tty\", 2)");
        Assert.Matches(Forbidden, "x == \"/dev/urandom\" || x == \"/dev/random\" || x == \"/dev/full\" || x == \"/dev/stdin\"");
        Assert.DoesNotMatch(Forbidden, "if (DevicePath.Classify(path) == DeviceKind.Stdout) sink = new StdoutSink(this);");
        Assert.DoesNotMatch(Forbidden, "var p = \"/device/null\";");
        Assert.DoesNotMatch(Forbidden, StripTrailingComment("x = 1; // `f 2>/dev/null` stays quiet"));
        Assert.Matches(Forbidden, StripTrailingComment("x = \"http://a\" + \"/dev/null\"; // note"));
    }
}
