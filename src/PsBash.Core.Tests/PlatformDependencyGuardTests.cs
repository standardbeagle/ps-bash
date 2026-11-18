using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PsBash.Core.Platform;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards the platform-dependency rule (<c>docs/specs/README.md</c> §Platform dependencies): an OS/arch-dependent
/// struct layout or system tool path lives in <c>src/PsBash.Transpiler/Platform/</c> and nowhere else.
/// The bug class: two private copies of a Linux <c>struct stat</c> were x86_64-only (wrong on arm64), and five sites
/// spawned <c>/usr/bin/stat</c>, which Alpine keeps in <c>/bin</c>. A copy outside Platform/ is how both happened.
/// <para>
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent, so a source check is the
/// justified oracle. The layouts themselves are pinned by <see cref="PosixStatTests"/>.
/// </para>
/// </summary>
public class PlatformDependencyGuardTests
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

    private static readonly string[] ProductProjects =
        ["PsBash.Cmdlets", "PsBash.Core", "PsBash.Host", "PsBash.Shell", "PsBash.Transpiler"];

    /// <summary>The one home of platform facts, as a path relative to <c>src/</c> with <c>/</c> separators.</summary>
    private const string PlatformDir = "PsBash.Transpiler/Platform/";

    /// <summary>Product sources (path relative to <c>src/</c>, <c>/</c>-separated) matching <paramref name="pattern"/>,
    /// outside bin/obj and outside Platform/.</summary>
    private static IEnumerable<(string Rel, string Text)> Sources(IEnumerable<string> projects, string pattern)
    {
        var src = Path.Combine(RepoRoot(), "src");
        foreach (var project in projects)
        {
            var root = Path.Combine(src, project);
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(src, file).Replace(Path.DirectorySeparatorChar, '/');
                if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal)) continue;
                if (rel.StartsWith(PlatformDir, StringComparison.Ordinal)) continue;
                yield return (rel, File.ReadAllText(file));
            }
        }
    }

    /// <summary>Code lines only: a comment that names a field or a path is prose, not a dependency.</summary>
    private static IEnumerable<(int Line, string Text)> CodeLines(string text, string commentPrefix)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (!lines[i].TrimStart().StartsWith(commentPrefix, StringComparison.Ordinal))
                yield return (i + 1, lines[i]);
    }

    // ---- (a) struct stat layouts and stat P/Invokes ----

    /// <summary>A <c>struct stat</c> field, a stat-family entry point, or an extern method named like one.</summary>
    private static readonly Regex StatDependency = new(
        @"\bst_(mode|nlink|ino|uid|gid|dev|rdev|size|blksize|blocks)\b"
        + @"|EntryPoint\s*=\s*""_*[fl]?x?stat(at|x|64|vfs|fs)?(\$INODE64)?"""
        + @"|\bextern\b[^;(]*\b_*[fl]?x?stat\w*\s*\(",
        RegexOptions.Compiled);

    internal static List<string> StatOffenders(IEnumerable<(string Rel, string Text)> sources)
    {
        var offenders = new List<string>();
        foreach (var (rel, text) in sources)
            foreach (var (line, code) in CodeLines(text, "//"))
                if (StatDependency.IsMatch(code)) offenders.Add($"{rel}:{line}: {code.Trim()}");
        return offenders;
    }

    [Fact]
    public void StructStatLayoutsAndStatPInvokes_LiveOnlyInPlatform()
    {
        var offenders = StatOffenders(Sources(ProductProjects, "*.cs"));
        Assert.True(offenders.Count == 0,
            "a struct stat layout or a stat/lstat/fstat P/Invoke outside src/PsBash.Transpiler/Platform/ — use "
            + "PsBash.Core.Platform.PosixStat (it knows Linux x86_64, Linux arm64 and Darwin):\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>The guard's own pattern catches the copies it was written against.</summary>
    [Fact]
    public void StatOffenders_FlagTheRemovedInlineCopies()
    {
        const string copy = """
            [StructLayout(LayoutKind.Sequential)]
            private struct LinuxStat
            {
                public ulong st_nlink;
            }
            [DllImport("libc", SetLastError = true, EntryPoint = "fstat$INODE64")]
            private static extern int fstat_darwin_x64(int fd, out DarwinStat buf);
            [DllImport("libc", SetLastError = true)]
            private static extern int lstat(string path, byte* buf);
            // a comment about st_mode is fine
            """;
        var offenders = StatOffenders([("X.cs", copy)]);
        Assert.Equal(4, offenders.Count);
        Assert.Empty(StatOffenders([("Y.cs", "if (PosixStat.TryFStat(fd, out var st) && st.IsFifo) Close(fd);")]));
    }

    // ---- (b) system tool paths ----

    /// <summary>A C# string literal that starts with a system tool directory.</summary>
    private static readonly Regex CsToolPath = new(@"""(/usr(/local)?)?/s?bin/", RegexOptions.Compiled);

    /// <summary>A PowerShell word that is a system tool path (not part of a longer path such as <c>./bin/x</c>).</summary>
    private static readonly Regex PsToolPath = new(@"(?<![\w./~-])/(usr/(local/)?)?s?bin/\w", RegexOptions.Compiled);

    /// <summary>Files that may name a tool path outside Platform/, each with its reason; <c>Only</c> narrows the
    /// allowance to those literals (null = any).</summary>
    private static readonly Dictionary<string, (string[]? Only, string Reason)> ToolPathAllowlist = new(StringComparer.Ordinal)
    {
        ["PsBash.Cmdlets/FileMagic.cs"] = (null,
            "`file` names the interpreter of a #! line by matching its path as TEXT; it never starts it"),
    };

    internal static List<string> ToolPathOffenders(IEnumerable<(string Rel, string Text)> cs, IEnumerable<(string Rel, string Text)> ps)
    {
        var offenders = new List<string>();
        foreach (var (rel, text) in cs)
        {
            ToolPathAllowlist.TryGetValue(rel, out var allow);
            if (allow.Reason is not null && allow.Only is null) continue;
            foreach (var (line, code) in CodeLines(text, "//"))
            {
                var probe = allow.Only is null ? code : allow.Only.Aggregate(code, (c, ok) => c.Replace(ok, ""));
                if (CsToolPath.IsMatch(probe)) offenders.Add($"{rel}:{line}: {code.Trim()}");
            }
        }
        foreach (var (rel, text) in ps)
            foreach (var (line, code) in CodeLines(text, "#"))
                if (PsToolPath.IsMatch(code)) offenders.Add($"{rel}:{line}: {code.Trim()}");
        return offenders;
    }

    [Fact]
    public void SystemToolPaths_LiveOnlyInPlatform()
    {
        var cs = Sources(ProductProjects, "*.cs");
        var ps = Sources(["PsBash.Module"], "*.psm1")
            .Concat(Sources(["PsBash.Module", "PsBash.Host", "PsBash.Core"], "*.ps1"));
        var offenders = ToolPathOffenders(cs, ps);
        Assert.True(offenders.Count == 0,
            "a literal /usr/bin/, /bin/, /usr/sbin/ or /usr/local/bin/ tool path outside "
            + "src/PsBash.Transpiler/Platform/ (Alpine keeps stat and ps in /bin) — use "
            + "PsBash.Core.Platform.SystemTools.Resolve(\"name\") / .PosixShell (psm1: "
            + "[PsBash.Cmdlets.BashRuntime]::SystemToolPath('name')):\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void ToolPathOffenders_FlagTheRemovedLiterals_AndKeepTheAllowances()
    {
        var cs = new[]
        {
            ("PsBash.Cmdlets/A.cs", "var psi = new ProcessStartInfo(\"/usr/bin/stat\");\n// \"/usr/bin/stat\" in prose"),
            ("PsBash.Cmdlets/B.cs", "TryRun(\"/bin/ps\", \"-axo\");"),
            ("PsBash.Cmdlets/C.cs", "TryRun(\"/usr/sbin/sysctl\", \"-n\");"),
            ("PsBash.Cmdlets/FileMagic.cs", "case \"/usr/bin/perl\": return perl;"),
            ("PsBash.Transpiler/Processes/JobProcess.Posix.cs", "string program = chdirBySh ? \"/bin/sh\" : spec.FileName;"),
            ("PsBash.Transpiler/Processes/JobProcess.Posix.cs", "var x = \"/usr/bin/env\";"),
        };
        var ps = new[]
        {
            ("PsBash.Module/PsBash.psm1", "        $statOut = & /usr/bin/stat @statArgs 2>$null\n# & /usr/bin/stat in a comment\n$p = './bin/x'"),
        };
        var offenders = ToolPathOffenders(cs, ps);
        Assert.Equal(6, offenders.Count);
        Assert.DoesNotContain(offenders, o => o.Contains("FileMagic", StringComparison.Ordinal));
        Assert.Contains(offenders, o => o.Contains("chdirBySh", StringComparison.Ordinal));  // no allowance: SystemTools.PosixShell
    }

    // ---- SystemTools itself ----

    [Fact]
    public void SystemDirectories_SearchUsrBinThenBin()
    {
        var dirs = SystemTools.SystemDirectories.ToList();
        Assert.True(dirs.IndexOf("/usr/bin") == 0, "glibc distros and macOS keep their tools in /usr/bin");
        Assert.True(dirs.IndexOf("/bin") == 1, "Alpine (busybox) keeps stat and ps in /bin");
        Assert.Contains("/usr/sbin", dirs);
        Assert.Equal(SystemTools.PosixShell, SystemTools.HostShellCandidates[^1]);
    }

    [Fact]
    public void Find_TakesTheFirstDirectoryThatHasTheTool_NullWhenNone()
    {
        var root = Path.Combine(Path.GetTempPath(), "ps-bash", "systools-" + Guid.NewGuid().ToString("N")[..8]);
        var empty = Directory.CreateDirectory(Path.Combine(root, "usr-bin")).FullName;
        var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        try
        {
            var tool = Path.Combine(bin, "stat");
            File.WriteAllText(tool, "#!/bin/sh\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            Assert.Equal(tool, SystemTools.Find("stat", [empty, bin]));
            Assert.Null(SystemTools.Find("stat", [empty]));
            Assert.Null(SystemTools.Find("no-such-tool-" + Guid.NewGuid().ToString("N")[..6], [empty, bin]));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [SkippableFact]
    [Trait("Platform", "Unix")]
    public void Resolve_OnUnix_IsTheAbsolutePathOfTheSystemTool()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix system directories");
        var sh = SystemTools.Resolve("sh");
        Assert.True(Path.IsPathRooted(sh), sh);
        Assert.Contains(Path.GetDirectoryName(sh)!, SystemTools.SystemDirectories);
        var missing = "no-such-tool-" + Guid.NewGuid().ToString("N")[..6];
        Assert.Equal(missing, SystemTools.Resolve(missing));
    }

    [SkippableFact]
    [Trait("Platform", "Windows")]
    public void Resolve_OnWindows_IsTheBareName()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows has no Unix system directories");
        Assert.Null(SystemTools.Find("stat"));
        Assert.Equal("stat", SystemTools.Resolve("stat"));
    }
}
