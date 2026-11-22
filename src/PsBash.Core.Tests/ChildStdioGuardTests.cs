using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards the child-process stdio merge invariant (<c>docs/specs/stdio-messaging.md</c> §4): when a script merges a
/// child's stderr into its stdout, the child gets ONE pipe or file for fd 1 and fd 2, and only <c>ChildStdio</c> makes
/// that pipe. A spawn that reads the two on separate pipes and merges them afterwards decides their order by a race.
/// <para>
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent, so a source check is the
/// justified oracle. The behavior itself is pinned by <c>StderrMergeOrderDifferentialTests</c> against bash.
/// </para>
/// </summary>
public class ChildStdioGuardTests
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

    private static IEnumerable<(string Name, string[] Lines)> CmdletSources() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets"), "*.cs", SearchOption.AllDirectories)
            .Select(f => (Path.GetFileName(f), File.ReadAllLines(f)));

    private static bool IsComment(string line) => line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    /// <summary>
    /// Files that may start a process themselves, each for a reason that is not "a child whose output the script
    /// sees on its stdout and stderr".
    /// </summary>
    private static readonly Dictionary<string, string> RawSpawnAllowed = new(StringComparer.Ordinal)
    {
        ["ChildStdio.cs"] = "the primitive itself",
        ["DetachedProcess.cs"] = "nohup: the child's fd 1 and fd 2 are one file handle already, and it outlives the host",
        ["InvokeBashLessCommand.cs"] = "interactive pager on the console",
    };

    /// <summary>
    /// A file handle a child inherits is opened for WRITING, never with FILE_APPEND_DATA alone: the MSYS2/Cygwin runtime
    /// (Git for Windows' ls.exe, sh.exe) derives the inherited fd's mode from the granted access, reads append-only as not
    /// writable, and writes nothing (<c>nohup ls /x &gt; f 2&gt;&amp;1</c> left f empty on the Windows runner, v0.15.1 gate).
    /// An append opens GENERIC_WRITE and moves to the end (<c>DetachedProcess.OpenRedirectTarget</c>).
    /// </summary>
    [Fact]
    public void ChildHandles_AreNeverOpenedAppendDataOnly()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');
            if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal)
                || rel.Split('/')[1].EndsWith(".Tests", StringComparison.Ordinal)) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i]) || lines[i].Contains("///", StringComparison.Ordinal)) continue;
                if (Regex.IsMatch(lines[i], @"\bFILE_APPEND_DATA\b|\bFileAppendData\b|FileSystemRights\.AppendData\b"))
                    offenders.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(offenders.Count == 0,
            "Open a child's redirect file GENERIC_WRITE (seek to the end to append), not FILE_APPEND_DATA: MSYS2 programs "
            + "cannot write an append-only handle. Offenders:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void ProcessStart_OnlyInTheStdioPrimitive_OrADocumentedExemption()
    {
        var offenders = new List<string>();
        foreach (var (name, lines) in CmdletSources())
        {
            if (RawSpawnAllowed.ContainsKey(name)) continue;
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i]) || lines[i].Contains("///", StringComparison.Ordinal)) continue;
                if (Regex.IsMatch(lines[i], @"\bProcess\.Start\(") || Regex.IsMatch(lines[i], @"new\s+Process\s*\("))
                    offenders.Add($"{name}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Start a child through ChildStdio.Start / BashRuntime.RunChildProcess / StartChildProcessStreaming, which "
            + "take a ChildStdioMode: a merged child must get ONE pipe for stdout and stderr (stdio-messaging.md §4). "
            + "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// A streaming child's output goes straight to the cmdlet's own output and error streams, so the spawn must say
    /// whether those are merged.
    /// </summary>
    [Fact]
    public void StreamingSpawn_AlwaysStatesTheMergeMode()
    {
        var offenders = new List<string>();
        foreach (var (name, lines) in CmdletSources())
        {
            if (name == "BashRuntime.cs") continue; // the definition
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i]) || lines[i].Contains("///", StringComparison.Ordinal)) continue;
                if (!lines[i].Contains("StartChildProcessStreaming(", StringComparison.Ordinal)) continue;
                // The call may wrap: read up to its end.
                var call = string.Join(" ", lines.Skip(i).Take(4));
                if (!call.Contains("ChildStdioMode", StringComparison.Ordinal))
                    offenders.Add($"{name}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "StartChildProcessStreaming must be given a ChildStdioMode computed from StderrMerge.JoinsStdout "
            + "(stdio-messaging.md §4). Offenders:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// A cmdlet that runs a command by name (<c>env</c>, <c>xargs</c>, <c>find -exec</c>, <c>command</c>, <c>time</c>,
    /// <c>flock</c>) must run a PROGRAM through the one-pipe path when its stderr is merged: PowerShell's own
    /// <c>&amp; $c</c> reads a program's stdout and stderr on two pipes.
    /// </summary>
    [Fact]
    public void RunByNameCmdlet_ConsultsTheMerge()
    {
        // `& $f` / `& $global:...` calls that run a FUNCTION or a trap handler (never a program) are not this.
        var exempt = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["InvokeBashCompgenCommand.cs"] = "runs a completion FUNCTION with its output discarded",
            ["InvokeBashEvalCommand.cs"] = "runs the ERR trap handler",
            ["InvokeBashFusedPipelineCommand.cs"] = "runs emitted pipeline text, whose programs are emitted through Invoke-BashNative",
        };
        var offenders = new List<string>();
        foreach (var (name, lines) in CmdletSources())
        {
            if (exempt.ContainsKey(name)) continue;
            var code = lines.Where(l => !IsComment(l) && !l.Contains("///", StringComparison.Ordinal)).ToArray();
            bool runsByName = code.Any(l => Regex.IsMatch(l, "\"[^\"]*&\\s*\\$(c|args\\[0\\]|__psbash_cmd)\\b"));
            if (!runsByName) continue;
            bool consults = code.Any(l => l.Contains("StderrMerge.JoinsStdout(", StringComparison.Ordinal)
                                          || l.Contains("MergedProgramPath(", StringComparison.Ordinal));
            if (!consults) offenders.Add(name);
        }

        Assert.True(offenders.Count == 0,
            "This cmdlet runs a command by name with PowerShell's `& $c`; when its stderr is merged a program must run "
            + "through InvokeBashNativeCommand.MergedProgramPath / RunMerged instead (stdio-messaging.md §4). "
            + "Offenders:\n  " + string.Join("\n  ", offenders));
    }
}
