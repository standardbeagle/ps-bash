using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Platform dependencies live in ONE place, <c>src/PsBash.Transpiler/Platform/</c> (namespace
/// <c>PsBash.Core.Platform</c>). A private <c>/proc</c> parser is a Linux-only answer: on macOS it silently said
/// "no parent" / "not a zombie" / "no group members" (nested-host detection, <c>host gc</c>, <c>kill -0</c> and the
/// process-tree stop all failed there). A private <c>ioctl</c> declaration is a variadic call declared fixed: correct on
/// Linux, a garbage pointer on Apple arm64. So:
/// <list type="bullet">
/// <item>product code (<c>src/PsBash.{Cmdlets,Core,Host,Shell,Transpiler}</c>) names a <c>/proc</c> path only inside
/// <c>Platform/</c> or in an inventoried file below, with the reason;</item>
/// <item><c>ioctl</c> is declared only in <c>Platform/TerminalIoctl.cs</c>.</item>
/// </list>
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent, so a source check is the
/// justified oracle. Behaviour is pinned by <c>Platform/ProcessInfoTests</c> and <c>Platform/TerminalPlatformTests</c>.
/// </summary>
public class PlatformProbeGuardTests
{
    private static readonly string[] ProductProjects =
        ["PsBash.Cmdlets", "PsBash.Core", "PsBash.Host", "PsBash.Shell", "PsBash.Transpiler"];

    private const string PlatformDir = "PsBash.Transpiler/Platform/";

    /// <summary>
    /// Files allowed to read <c>/proc</c> outside <c>Platform/</c>: each is the Linux branch of a reader that already has
    /// its own non-<c>/proc</c> macOS branch, so nothing is silently missing off Linux. Moving them into
    /// <c>ProcessInfo</c> is welcome; adding a file here needs the same justification.
    /// </summary>
    private static readonly Dictionary<string, string> ProcAllowed = new(StringComparer.Ordinal)
    {
        ["PsBash.Cmdlets/InvokeBashPsCommand.cs"] =
            "`ps` Linux listing (per-pid stat/status/cmdline, /proc/uptime, /proc/meminfo); macOS lists via Process + /bin/ps",
        ["PsBash.Host/InheritedFdDetach.cs"] =
            "/proc/self/fd enumeration + readlink on Linux; macOS walks fds 3..255 with fstat (IsFdPipeViaFstat)",
    };

    // A string literal that starts a /proc path: "/proc", "/proc/…", $"/proc/{pid}", @"/proc".
    private static readonly Regex ProcLiteral = new(@"(\$@?|@\$?)?""/proc(/|""|\b)", RegexOptions.Compiled);

    // An ioctl declaration: a P/Invoke whose entry point or method name is ioctl.
    private static readonly Regex IoctlDeclaration = new(
        @"EntryPoint\s*=\s*""ioctl""|\b(extern|partial)\s+[\w.]+\s+ioctl\w*\s*\(", RegexOptions.Compiled);

    private const string IoctlHome = "PsBash.Transpiler/Platform/TerminalIoctl.cs";

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

    /// <summary>(relative path, line number, code) for every non-comment line of product code matching <paramref name="pattern"/>.</summary>
    private static List<(string File, int Line, string Code)> Scan(Regex pattern)
    {
        var src = Path.Combine(RepoRoot(), "src");
        var hits = new List<(string, int, string)>();
        foreach (var project in ProductProjects)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(src, project), "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(src, file).Replace(Path.DirectorySeparatorChar, '/');
                if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal)) continue;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    var trimmed = lines[i].TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal)) continue;
                    if (pattern.IsMatch(lines[i])) hits.Add((rel, i + 1, trimmed));
                }
            }
        }
        return hits;
    }

    [Fact]
    public void ProcPaths_AreReadOnlyByThePlatformLayer_OrAnInventoriedLinuxBranch()
    {
        var hits = Scan(ProcLiteral);
        var offenders = hits
            .Where(h => !h.File.StartsWith(PlatformDir, StringComparison.Ordinal) && !ProcAllowed.ContainsKey(h.File))
            .Select(h => $"{h.File}:{h.Line}: {h.Code}")
            .ToList();
        var stale = ProcAllowed.Keys.Where(f => !hits.Any(h => h.File == f))
            .Select(f => $"STALE {f}: inventoried but reads no /proc path any more - remove the entry")
            .ToList();

        Assert.True(offenders.Count == 0 && stale.Count == 0,
            "Process information comes from PsBash.Core.Platform.ProcessInfo (Linux /proc, macOS libproc, Windows NtQuery). "
            + "A private /proc reader answers nothing on macOS. Use ProcessInfo (or extend it), or inventory the file in "
            + "PlatformProbeGuardTests.ProcAllowed with its macOS branch:\n  " + string.Join("\n  ", offenders.Concat(stale)));
    }

    [Fact]
    public void Ioctl_IsDeclaredOnlyInTheTerminalIoctlHelper()
    {
        var hits = Scan(IoctlDeclaration);
        var offenders = hits.Where(h => h.File != IoctlHome).Select(h => $"{h.File}:{h.Line}: {h.Code}").ToList();

        Assert.True(offenders.Count == 0,
            "ioctl is variadic; a fixed-argument P/Invoke passes its pointer in a register, but Apple arm64 passes variadic "
            + "arguments on the stack. Declare it only in PsBash.Core.Platform.TerminalIoctl (which pads for osx-arm64) and "
            + "add the request there:\n  " + string.Join("\n  ", offenders));
        Assert.Contains(hits, h => h.File == IoctlHome); // the scan still recognises the helper's own declarations
    }

    [Fact]
    public void ProcLiteralPattern_MatchesEveryShape_AndNotLookalikes()
    {
        Assert.Matches(ProcLiteral, "File.ReadAllBytes(\"/proc/\" + pid + \"/stat\")");
        Assert.Matches(ProcLiteral, "new FileStream($\"/proc/{pid}/stat\", FileMode.Open)");
        Assert.Matches(ProcLiteral, "Directory.EnumerateDirectories(\"/proc\")");
        Assert.Matches(ProcLiteral, "Path.Combine(@\"/proc\", x)");
        Assert.DoesNotMatch(ProcLiteral, "var x = \"/process\";");
        Assert.DoesNotMatch(ProcLiteral, "var x = \"/procedures/a\";");
    }

    [Fact]
    public void IoctlPattern_MatchesBothDeclarationStyles()
    {
        Assert.Matches(IoctlDeclaration, "public static partial int ioctl(int fd, ulong request, ref Winsize ws);");
        Assert.Matches(IoctlDeclaration, "private static extern int ioctl(int fd, ulong request, IntPtr arg);");
        Assert.Matches(IoctlDeclaration, "[LibraryImport(\"libc\", EntryPoint = \"ioctl\", SetLastError = true)]");
        Assert.DoesNotMatch(IoctlDeclaration, "if (!TerminalIoctl.TrySetWindowSize(fd, cols, rows, out int err))");
    }
}
