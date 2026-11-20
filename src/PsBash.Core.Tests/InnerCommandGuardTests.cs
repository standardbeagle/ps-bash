using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards the one runner for a command a cmdlet starts by name (<c>Cmdlets/InnerCommand.cs</c>; spec
/// <c>runtime-functions.md</c> §Wrapper commands). Each wrapper that re-derived <c>&amp; $c @a</c> lost a rule: env dropped
/// its pipe, flock rejected it, the command's errors went past the wrapper's <c>2&gt;/dev/null</c>, and a bare capturing
/// <c>2&gt;&amp;1</c> made a spawned child merge its stderr into stdout.
/// <para>Oracle note (qa-rubric Directive 1): a repo-structure invariant; the behavior is pinned against bash by
/// <c>WrapperCommandDifferentialTests</c> and <c>NativeStdioBytesTests</c>.</para>
/// </summary>
public class InnerCommandGuardTests
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

    private static IEnumerable<(string Name, string Text, string[] Lines)> CmdletSources() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets"), "*.cs", SearchOption.AllDirectories)
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f), File.ReadAllLines(f)));

    private static bool IsComment(string line) => line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    /// <summary>A script that runs a resolved command (<c>&amp; $c @rest</c>) outside the runner, each with why.</summary>
    private static readonly Dictionary<string, string> RunByNameAllowed = new()
    {
        ["InnerCommand.cs"] = "the runner itself",
        ["InvokeBashTimeCommand.cs"] = "the `time` keyword: runs functions too (Resolve-BashCommand) and buffers the run to time it",
        ["InvokeBashXargsCommand.cs"] = "xargs: the command's stdin is not xargs' pipe (that is its argument list); maps 255/126/127 itself",
    };

    private static readonly Regex RunByName = new(@"&\s*\$c\s+@", RegexOptions.Compiled);

    [Fact]
    public void RunByNameScript_OnlyInTheInnerCommandRunner()
    {
        var offenders = new List<string>();
        foreach (var (name, _, lines) in CmdletSources())
        {
            if (RunByNameAllowed.ContainsKey(name)) continue;
            for (int i = 0; i < lines.Length; i++)
                if (!IsComment(lines[i]) && RunByName.IsMatch(lines[i]))
                    offenders.Add($"{name}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0,
            "Run a command by name through InnerCommand.Run (its stdin = your pipeline records, its errors re-written on "
            + "your stream, ProgramFile, merged 2>&1). Offenders:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void RunByNameAllowList_HasNoStaleEntries()
    {
        var sources = CmdletSources().GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.SelectMany(s => s.Lines).ToArray());
        var stale = RunByNameAllowed.Keys
            .Where(k => !sources.TryGetValue(k, out var lines) || !lines.Any(l => !IsComment(l) && RunByName.IsMatch(l)))
            .ToList();
        Assert.True(stale.Count == 0, "Inventoried but no run-by-name script any more - remove: " + string.Join(", ", stale));
    }

    /// <summary>
    /// A cmdlet that runs a command by name - through InnerCommand or its own inventoried script - must be able to receive
    /// the pipe the command reads: without a pipeline parameter the binder rejects <c>echo x | flock f cat</c> ("The input
    /// object cannot be bound"), and <c>echo x | time cat</c> printed nothing (the records went nowhere).
    /// </summary>
    [Fact]
    public void EveryCommandRunner_TakesPipelineInput()
    {
        var offenders = CmdletSources()
            .Where(s => s.Name != "InnerCommand.cs"
                        && (s.Text.Contains("InnerCommand.Run(", StringComparison.Ordinal) || RunByNameAllowed.ContainsKey(s.Name))
                        && !s.Text.Contains("ValueFromPipeline = true", StringComparison.Ordinal))
            .Select(s => s.Name).ToList();
        Assert.True(offenders.Count == 0, "Declare a ValueFromPipeline parameter and collect its records: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// A fresh ps-bash a cmdlet starts is THIS build's launcher (<c>InvokeBashBashCommand.ResolvePsBashExecutable</c>),
    /// never one found on PATH: an installed ps-bash of another build judged the running host stale and retired it in
    /// the middle of the command (a foreground <c>nohup sh -c 'exit 3' &gt; f</c> under the test harness).
    /// </summary>
    [Fact]
    public void NestedPsBash_IsThisBuildsLauncher_NeverAPathLookup()
    {
        var pathLookup = new Regex(@"ExecutableLookup\.Find\([^;]*""ps-bash", RegexOptions.Compiled);
        var offenders = new List<string>();
        foreach (var (name, _, lines) in CmdletSources())
            for (int i = 0; i < lines.Length; i++)
                if (!IsComment(lines[i]) && pathLookup.IsMatch(lines[i]))
                    offenders.Add($"{name}:{i + 1}: {lines[i].Trim()}");
        Assert.True(offenders.Count == 0,
            "Start a nested ps-bash with InvokeBashBashCommand.ResolvePsBashExecutable(cmdlet). Offenders:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// A nested script's error stream is captured with <c>InnerCommand.ErrorCapture</c>, never a bare <c>" 2&gt;&amp;1"</c>
    /// after a call: the bare one tells a spawning command (<c>sh -c</c>) its stderr is merged into stdout.
    /// </summary>
    [Fact]
    public void NestedCallCapture_UsesErrorCapture()
    {
        var call = new Regex(@"(&\s*\$c\b|&\s*'\['|\[scriptblock\]::Create\(\$args\[0\]\)|\.\s*\$__psbash_cr).*2>&1", RegexOptions.Compiled);
        var offenders = new List<string>();
        foreach (var (name, _, lines) in CmdletSources())
        {
            if (RunByNameAllowed.ContainsKey(name) && name != "InnerCommand.cs") continue;
            for (int i = 0; i < lines.Length; i++)
                if (!IsComment(lines[i]) && call.IsMatch(lines[i]))
                    offenders.Add($"{name}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0,
            "Append InnerCommand.ErrorCapture(cmdlet) to the call instead of a bare 2>&1. Offenders:\n  " + string.Join("\n  ", offenders));
    }
}
