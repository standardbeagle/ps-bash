using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards the stop invariant (<c>docs/specs/stdio-messaging.md</c> §4.6, <c>background-jobs.md</c> §6): stopping a
/// child ends its whole process tree BY CONSTRUCTION - the child was started inside a Job Object / process group
/// (<c>JobProcess</c>, reached through <c>ChildStdio.Start</c>) and one call ends every member. <c>Process.Kill</c>,
/// with or without <c>entireProcessTree</c>, walks parent-pid links at kill time: a grandchild whose parent already
/// exited is not found, keeps running, and keeps the pipes open.
/// <para>
/// So every <c>.Kill(</c> call in product code is inventoried here with its reason and its count. A new one fails this
/// test: stop a child with <c>SpawnedChild.KillTree()</c> / <c>StreamingChildProcess.Kill()</c> / <c>JobProcess.KillTree()</c>
/// instead, or - if it really is a different kind of kill - add it below with the reason.
/// </para>
/// <para>
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent, so a source check is the
/// justified oracle. The behavior is pinned by <c>ProcessTreeStopTests</c> (pid liveness after a stop).
/// </para>
/// </summary>
public class ProcessTreeKillGuardTests
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

    /// <summary>Every file that calls a <c>.Kill(</c>, how many times, and why it is not a missed process tree.</summary>
    private static readonly Dictionary<string, (int Count, string Reason)> KillSites = new(StringComparer.Ordinal)
    {
        // ---- through the tree primitive (the call is a wrapper over JobProcess.KillTree) ----
        ["PsBash.Cmdlets/AwkInput.cs"] = (1, "AwkShell.KillAll -> AwkCommandStream.Kill -> SpawnedChild.KillTree"),
        ["PsBash.Cmdlets/InvokeBashBashCommand.cs"] = (1, "StopProcessing -> StreamingChildProcess.Kill -> SpawnedChild.KillTree"),
        ["PsBash.Cmdlets/InvokeBashNativeCommand.cs"] = (1, "StopProcessing -> StreamingChildProcess.Kill -> SpawnedChild.KillTree"),
        ["PsBash.Transpiler/Processes/JobProcess.Posix.cs"] = (1, "the primitive: a child that could not lead its own group (host is the terminal's foreground job)"),

        // ---- not a child this code started as a command ----
        ["PsBash.Cmdlets/InvokeBashKillCommand.cs"] = (1, "the `kill` builtin: the user names one pid, and one pid is what bash signals"),
        ["PsBash.Cmdlets/WindowsFileLockers.cs"] = (1, "ends this host's own idle WinPS compat session that pins a directory; it has no children"),

        // ---- ps-bash-host processes (host lifecycle; Windows private hosts are held in a WindowsProcessTree job) ----
        ["PsBash.Core/Runtime/IpcWorker.cs"] = (6, "host lifecycle: a private/failed ps-bash-host; 2 of the 6 are WindowsProcessTree.Kill (the job)"),
        ["PsBash.Core/Runtime/Ipc/PendingHostSpawn.cs"] = (1, "host lifecycle: a host that never came up, identity-checked"),
        ["PsBash.Core/Runtime/Ipc/WindowsHostSpawn.cs"] = (1, "host lifecycle: a host whose spawn failed half-way"),
        ["PsBash.Shell/HostCommands.cs"] = (1, "`ps-bash host kill`: the user asks for the hosts to die"),
        ["PsBash.Shell/Sessions/TerminalBroker.cs"] = (1, "session broker: ends a terminal session's shell on request"),
        ["PsBash.Host/Server/ShutdownBackstop.cs"] = (1, "host lifecycle: the host ends ITSELF when its shutdown hangs; its command children sit in kill-on-close jobs"),
        ["PsBash.Host/Shell/CommandAssistProvider.cs"] = (1, "AI assist helper CLI on its own time budget; not a user command, output never reaches a script"),

        // ---- test infrastructure shipped under src/ ----
        ["PsBash.Spec/WslBashExecutor.cs"] = (1, "spec runner: the wsl.exe oracle process"),
        ["PsBash.Testing/ProcessSpawn.cs"] = (2, "test harness: the launcher under test"),
    };

    private static readonly Regex KillCall = new(@"\.Kill\s*\(", RegexOptions.Compiled);

    private static Dictionary<string, List<string>> FindKillCalls()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var project in Directory.EnumerateDirectories(src))
        {
            if (Path.GetFileName(project).EndsWith(".Tests", StringComparison.Ordinal)) continue;
            foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(src, file).Replace(Path.DirectorySeparatorChar, '/');
                if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal)) continue;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    var trimmed = lines[i].TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue; // comments and XML docs
                    if (!KillCall.IsMatch(lines[i])) continue;
                    if (!found.TryGetValue(rel, out var list)) found[rel] = list = new List<string>();
                    list.Add($"{rel}:{i + 1}: {trimmed}");
                }
            }
        }
        return found;
    }

    [Fact]
    public void EveryKillCall_IsTheTreePrimitive_OrAnInventoriedException()
    {
        var found = FindKillCalls();
        var problems = new List<string>();
        foreach (var (file, calls) in found)
        {
            if (!KillSites.TryGetValue(file, out var allowed))
                problems.AddRange(calls.Select(c => "NEW  " + c));
            else if (calls.Count != allowed.Count)
                problems.Add($"COUNT {file}: {calls.Count} .Kill( calls, inventory says {allowed.Count} ({allowed.Reason})\n      "
                    + string.Join("\n      ", calls));
        }
        foreach (var file in KillSites.Keys.Where(f => !found.ContainsKey(f)))
            problems.Add($"STALE {file}: inventoried but has no .Kill( call any more - remove the entry");

        Assert.True(problems.Count == 0,
            "A child is stopped through the process-tree primitive: SpawnedChild.KillTree() (ChildStdio.Start), "
            + "StreamingChildProcess.Kill(), or JobProcess.KillTree(). Process.Kill - even Kill(entireProcessTree: true) - "
            + "finds descendants by parent pid at kill time and misses a grandchild whose parent already exited; that "
            + "orphan keeps the pipes open and the stop hangs (stdio-messaging.md §4.6). Fix the call, or inventory it in "
            + "ProcessTreeKillGuardTests.KillSites with its reason:\n  " + string.Join("\n  ", problems));
    }

    /// <summary>
    /// <c>Kill(entireProcessTree: true)</c> looks like the right tool and is the one that silently misses orphans, so it
    /// is forbidden outright in the cmdlet assembly: every child there comes from <c>ChildStdio.Start</c>.
    /// </summary>
    [Fact]
    public void Cmdlets_NeverUse_KillEntireProcessTree()
    {
        var offenders = FindKillCalls()
            .Where(kv => kv.Key.StartsWith("PsBash.Cmdlets/", StringComparison.Ordinal))
            .SelectMany(kv => kv.Value)
            .Where(c => c.Contains("entireProcessTree", StringComparison.Ordinal))
            .ToList();
        Assert.True(offenders.Count == 0,
            "Kill(entireProcessTree: true) in PsBash.Cmdlets: use SpawnedChild.KillTree() (stdio-messaging.md §4.6):\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Every parameterless <c>WaitForExit()</c> in the cmdlet assembly, with why a stop still ends it. A wait for a child
    /// on the pipeline thread waits on its exit OR a stop - <c>SpawnedChild.WaitForExit(ms, stopToken)</c> - because the
    /// stop arrives from another thread (<c>StopProcessing</c>) and must be able to end it: awk's <c>close()</c> of an
    /// output pipe waited with a bare <c>WaitForExit()</c> that no Ctrl+C could reach.
    /// </summary>
    private static readonly Dictionary<string, (int Count, string Reason)> UnboundedWaitSites = new(StringComparer.Ordinal)
    {
        ["PsBash.Cmdlets/StreamingChildProcess.cs"] = (1, "exit-watcher background thread (not the pipeline thread); Kill -> KillTree ends the child and so the wait"),
        ["PsBash.Cmdlets/InvokeBashLessCommand.cs"] = (1, "interactive pager owns the console; the user quits it"),
    };

    private static readonly Regex UnboundedWait = new(@"\.WaitForExit\s*\(\s*\)", RegexOptions.Compiled);

    [Fact]
    public void Cmdlets_EveryUnboundedWaitForExit_HasAStopPath()
    {
        var root = Path.Combine(RepoRoot(), "src");
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "PsBash.Cmdlets"), "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal)) continue;
            var calls = File.ReadAllLines(file)
                .Select((l, i) => (Line: l.TrimStart(), No: i + 1))
                .Where(x => !x.Line.StartsWith("//", StringComparison.Ordinal) && UnboundedWait.IsMatch(x.Line))
                .Select(x => $"{rel}:{x.No}: {x.Line}")
                .ToList();
            if (calls.Count == 0) continue;
            seen.Add(rel);
            if (!UnboundedWaitSites.TryGetValue(rel, out var allowed))
                problems.AddRange(calls.Select(c => "NEW  " + c));
            else if (calls.Count != allowed.Count)
                problems.Add($"COUNT {rel}: {calls.Count}, inventory says {allowed.Count} ({allowed.Reason})\n      " + string.Join("\n      ", calls));
        }
        foreach (var file in UnboundedWaitSites.Keys.Where(f => !seen.Contains(f)))
            problems.Add($"STALE {file}: inventoried but has no WaitForExit() any more - remove the entry");

        Assert.True(problems.Count == 0,
            "Wait for a child on its exit OR the stop: SpawnedChild.WaitForExit(Timeout.Infinite, stopToken) (bash puts no "
            + "time limit on it; Ctrl+C must still end it, stdio-messaging.md §4.6), or inventory the call in "
            + "ProcessTreeKillGuardTests.UnboundedWaitSites with why a stop ends it:\n  " + string.Join("\n  ", problems));
    }

    /// <summary>The wait after a stop must not hang on a pipe: the buffered helper waits on the process and bounds its drains.</summary>
    [Fact]
    public void BufferedWait_KillsThroughThePrimitive_AndNeverJoinsADrainUnbounded()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets", "BashRuntime.cs"));
        int start = text.IndexOf("private static ChildProcessResult WaitBuffered(", StringComparison.Ordinal);
        Assert.True(start >= 0, "BashRuntime.WaitBuffered not found");
        int end = text.IndexOf("private static System.TimeSpan GetChildProcessTimeout()", start, StringComparison.Ordinal);
        var body = text[start..end];
        Assert.Contains("child.KillTree();", body);
        Assert.Contains("StreamDrain.JoinAll(DrainAfterKill", body);
        Assert.DoesNotContain(".Join()", text[text.IndexOf("private sealed class StreamDrain", StringComparison.Ordinal)..]);
        Assert.DoesNotContain("WaitForExit()", body); // the parameterless Process.WaitForExit waits for pipe EOF
    }
}
