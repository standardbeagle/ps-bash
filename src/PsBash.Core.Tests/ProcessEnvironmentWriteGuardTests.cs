using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// The launcher library (PsBash.Core), the transpiler / shared runtime (PsBash.Transpiler) and the host (PsBash.Host)
/// write the process environment only where the inventory below says, each with its reason. The process environment is shared by every thread: IpcWorker once lowered a nested
/// daemon's idle window by setting <c>PSBASH_HOST_IDLE_SECS</c> around the spawn and restoring it, so a host or child
/// another thread started meanwhile inherited 60 s, and the restore raced. A setting for one child goes on that child:
/// its command line (<see cref="HostIdleWindow.Arg"/>) or its <c>ProcessStartInfo.Environment</c>. A shell variable is
/// written through its store, <see cref="ProcessScalars"/>, the one Transpiler writer: a direct write elsewhere bypasses
/// the exact value it keeps where the environment cannot hold one (Linux: an invalid byte became U+FFFD).
/// (The cmdlets have their own rule: BashVariableStoreGuardTests.NoCmdlet_SetsTheProcessEnvironment_ExceptTheStore.)
///
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent, so a source check is the
/// justified oracle.
/// </summary>
public class ProcessEnvironmentWriteGuardTests
{
    /// <summary>Every allowed write, per file (path under src/), with how many there are and why.</summary>
    private static readonly (string File, int Count, string Reason)[] Inventory =
    [
        ("PsBash.Host/Program.cs", 4,
            "startup, before the host serves anything: $PWD in shell form, PSBASH_INTERACTIVE for the interactive host's "
            + "life, and consuming the stateful-session switch so the commands it runs do not inherit it"),
        ("PsBash.Host/Runtime/SdkRunspace.cs", 1, "startup: the execution policy every runspace of the process uses"),
        ("PsBash.Host/Runtime/SdkWorker.cs", 1,
            "the sweep that unsets what the launcher's block does not have, while the command holds the host's execution "
            + "lease (docs/specs/environment-model.md); the block's own values go in through ProcessScalars"),
        ("PsBash.Transpiler/ProcessScalars.cs", 3,
            "the shell's scalar store (environment-model.md section 8): an assignment, and the one-time probe of whether "
            + "the environment gives back every string, which sets and removes its own variable"),
    ];

    private static readonly Regex EnvWrite =
        new(@"Environment\.SetEnvironmentVariable\b|\[(System\.)?Environment\]::SetEnvironmentVariable", RegexOptions.Compiled);

    [Fact]
    public void CoreAndHost_WriteTheProcessEnvironment_OnlyWhereTheInventorySays()
    {
        var root = Path.Combine(RepoRoot(), "src");
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var project in new[] { "PsBash.Core", "PsBash.Transpiler", "PsBash.Host" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal)) continue;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    var trimmed = lines[i].TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal)) continue;
                    if (!EnvWrite.IsMatch(lines[i])) continue;
                    if (!found.TryGetValue(rel, out var list)) found[rel] = list = [];
                    list.Add($"{rel}:{i + 1}: {trimmed}");
                }
            }
        }

        var problems = new List<string>();
        foreach (var (rel, writes) in found)
        {
            var allowed = Inventory.FirstOrDefault(e => e.File == rel);
            if (allowed.File is null)
                problems.Add("not in the inventory:\n    " + string.Join("\n    ", writes));
            else if (writes.Count != allowed.Count)
                problems.Add($"{rel} has {writes.Count} writes, the inventory {allowed.Count} ({allowed.Reason}):\n    "
                    + string.Join("\n    ", writes));
        }
        foreach (var entry in Inventory.Where(e => !found.ContainsKey(e.File)))
            problems.Add($"{entry.File}: in the inventory but writes nothing now; remove the entry");

        Assert.True(problems.Count == 0,
            "PsBash.Core, PsBash.Transpiler and PsBash.Host write the process environment only at the inventoried places. It is every "
            + "thread's: never set a variable around a spawn and restore it; pass the child its setting on its command "
            + "line or ProcessStartInfo.Environment. Problems:\n  " + string.Join("\n  ", problems));
    }

    /// <summary>The nested daemon's shorter idle window travels on its command line (the fix the guard above protects).</summary>
    [Fact]
    public void IpcWorker_HandsANestedDaemonItsIdleWindowAsAnArgument()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PsBash.Core", "Runtime", "IpcWorker.cs"));
        Assert.Contains("HostIdleWindow.NestedHostArgument(", text);
        Assert.DoesNotContain("SetEnvironmentVariable", text);
    }

    [Theory]
    [InlineData(null, null, HostIdleWindow.DefaultSecs)]
    [InlineData(null, "120", 120)]
    [InlineData("60", "900", 60)]
    [InlineData("60", null, 60)]
    [InlineData("0", "120", 120)]
    [InlineData("x", null, HostIdleWindow.DefaultSecs)]
    [InlineData(null, "-5", HostIdleWindow.DefaultSecs)]
    public void HostIdleWindow_Resolve_PrefersTheArgumentThenTheEnvironment(string? arg, string? env, int expectedSecs)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSecs), HostIdleWindow.Resolve(arg, env));

    [Theory]
    [InlineData(null, "--idle-secs=60")]
    [InlineData("900", "--idle-secs=60")]
    [InlineData("30", "--idle-secs=30")]
    [InlineData("60", "--idle-secs=60")]
    [InlineData("0", "--idle-secs=60")]
    [InlineData("abc", "--idle-secs=60")]
    public void HostIdleWindow_NestedHostArgument_IsSixtyOrTheUsersShorterWindow(string? env, string expected)
        => Assert.Equal(expected, HostIdleWindow.NestedHostArgument(env));

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
}
