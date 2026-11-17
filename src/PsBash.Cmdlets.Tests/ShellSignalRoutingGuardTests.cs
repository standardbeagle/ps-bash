using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A signal for a ps-bash shell is a message to the command its host runs, sent through ONE primitive,
/// <c>PsBash.Core.ShellSignals.SendToShell</c> (docs/specs/background-jobs.md §13). An OS kill of a host
/// (<c>sh -c "kill -USR1 $$"</c> did that once) ends every script of the session and the host with them. This scan pins:
/// (1) bash <c>kill</c> is the kill cmdlet and nothing else; (2) in it, every pid it does not own is offered to
/// <c>SendToShell</c> before any <c>Process.Kill</c>, and <c>Process.Kill</c> stays behind the <c>IsShellHost</c> refusal;
/// (3) no other source file both looks a process up by pid and kills it, except the listed lifecycle paths, which never
/// act on a script's request. Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent.
/// </summary>
public class ShellSignalRoutingGuardTests
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

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), "src", .. parts]));

    [Fact]
    public void KillCmdlet_OffersEveryForeignPidToSendToShell_BeforeAnyProcessKill()
    {
        var text = Source("PsBash.Cmdlets", "InvokeBashKillCommand.cs");
        int send = text.IndexOf("ShellSignals.SendToShell(", StringComparison.Ordinal);
        int refuse = text.IndexOf("IsShellHost(proc)", StringComparison.Ordinal);
        int kill = text.IndexOf("proc.Kill(", StringComparison.Ordinal);
        Assert.True(send > 0, "the kill cmdlet must route other shells' pids through ShellSignals.SendToShell");
        Assert.True(refuse > send, "the IsShellHost refusal must follow the SendToShell route");
        Assert.True(kill > refuse, "Process.Kill must stay behind the IsShellHost refusal");
        Assert.Single(Regex.Matches(text, @"(?m)^(?!\s*//).*?\.Kill\("));   // code lines only, not doc comments

        // Every signal goes to a trapping shell, ignored-by-default ones included (that shell may trap URG/WINCH):
        // only an existence probe (`kill -0`) skips the route.
        var sendLine = text[(text.LastIndexOf('\n', send) + 1)..text.IndexOf('\n', send)];
        Assert.Contains("!existenceOnly", sendLine);
        Assert.DoesNotContain("probeOnly", sendLine);
    }

    [Fact]
    public void BashKill_IsTheKillCmdletOnly()
    {
        var psm1 = Source("PsBash.Module", "PsBash.psm1");
        Assert.Matches(new Regex(@"Set-Alias\s+-Name\s+'kill'\s+-Value\s+'Invoke-BashKill'"), psm1);
        Assert.DoesNotMatch(new Regex(@"(?im)^\s*function\s+(kill|Invoke-BashKill)\b"), psm1);
    }

    /// <summary>file → why it may look a process up by pid and kill it: none acts on a script's `kill`.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["InvokeBashKillCommand.cs"] = "bash `kill` itself: guarded above (SendToShell, then IsShellHost)",
        ["InvokeBashBashCommand.cs"] = "kills only its own nested-shell child on Stop; the pid lookup reads a parent",
        ["WindowsFileLockers.cs"] = "ends only powershell.exe 5.1 compat sessions this process spawned",
        ["JobProcess.Posix.cs"] = "a background job's own process group (the job table's child)",
        ["IpcWorker.cs"] = "retires a wedged/obsolete host after ownership checks (host-lifecycle-contract.md)",
        ["PendingHostSpawn.cs"] = "kills a host spawn that never came up (host-lifecycle-contract.md)",
        ["WindowsHostSpawn.cs"] = "its own spawn's thread/handles during host start",
        ["HostCommands.cs"] = "the launcher's explicit `ps-bash host kill` admin command",
        ["TerminalBroker.cs"] = "ends a terminal session's own shell process",
    };

    [Fact]
    public void NoOtherSource_LooksUpAPidAndKillsIt()
    {
        var offenders = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in new[] { "PsBash.Cmdlets", "PsBash.Transpiler", "PsBash.Core", "PsBash.Host", "PsBash.Shell" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", project), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                var text = File.ReadAllText(file);
                if (!text.Contains("GetProcessById(", StringComparison.Ordinal) || !Regex.IsMatch(text, @"\.Kill\(")) continue;
                var name = Path.GetFileName(file);
                if (Allowed.ContainsKey(name)) seen.Add(name);
                else offenders.Add($"{project}/{name}");
            }
        }
        Assert.True(offenders.Count == 0,
            "A pid a script names is signalled only by the kill cmdlet, through ShellSignals.SendToShell for a ps-bash shell. "
            + "Route it there, or add the file with the reason it never acts on a script's `kill`:\n" + string.Join("\n", offenders));
        var stale = Allowed.Keys.Where(k => !seen.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Allow-list entries that no longer look up and kill a pid (remove them):\n" + string.Join("\n", stale));
    }
}
