using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// One owner for a program's stdin (docs/specs/stdio-messaging.md §4.7):
/// <list type="bullet">
/// <item>A program ps-bash starts itself takes its stdin from <c>NativeStdinBridge.ProgramStdin</c> (the shell's stdin
/// through the cursor's one pipe; in a job, end of input). REGRESSION: <c>RunMerged</c> and a job's
/// <c>Invoke-BashNative</c> said <c>InheritStdin</c> themselves, so <c>echo in | { env X=1 cat; }</c> read the process stdin
/// (nothing), and a job's program fell back to the foreground's.</item>
/// <item>Only the cursor's pipe (<c>CursorPipe</c>) takes a cursor's records past its tap
/// (<c>TryTakeUpstream</c>/<c>PushFrontUpstream</c>). REGRESSION: a pump per program took the records the shell's
/// <c>read</c> came for (<c>while read x; do /usr/bin/true &amp; /usr/bin/echo $x; done</c> lost lines), and a second pipe
/// opened during a put-back reordered them.</item>
/// </list>
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant; behavior vs bash is Differential
/// <c>JobStdinDifferentialTests</c>.
/// </summary>
public class StdinOwnershipGuardTests
{
    private static readonly (Regex Pattern, string[] Owners, string Rule)[] Rules =
    [
        (new Regex(@"\bInheritStdin\s*[:=]", RegexOptions.Compiled), ["NativeStdinBridge.cs", "ChildStdio.cs", "JobProcess.cs", "BashRuntime.cs"],
            "a program's stdin comes from NativeStdinBridge.ProgramStdin (ChildStdio/JobProcess declare it, BashRuntime's buffered run sets it false)"),
        (new Regex(@"\.(TryTakeUpstream|PushFrontUpstream)\s*\(", RegexOptions.Compiled), ["CursorPipe.cs", "StdinCursor.cs"],
            "only the cursor's pipe reads a cursor past its tap"),
        (new Regex(@"\bSetStdHandle\s*\(|\bdup2\s*\([^,]+,\s*0\s*\)", RegexOptions.Compiled), ["NativeStdinBridge.cs", "InheritedFdDetach.cs"],
            "only NativeStdinBridge.Enter swaps the process stdin (InheritedFdDetach: the host lets go of its own at start)"),
    ];

    internal static List<string> Offenders(IEnumerable<(string Name, string Text)> files)
    {
        var offenders = new List<string>();
        foreach (var (name, text) in files)
        {
            var file = Path.GetFileName(name);
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal)) continue;
                foreach (var (pattern, owners, rule) in Rules)
                    if (pattern.IsMatch(lines[i]) && !owners.Contains(file))
                        offenders.Add($"{name}:{i + 1}: {t.TrimEnd()} ({rule})");
            }
        }
        return offenders;
    }

    [Fact]
    public void ProgramStdin_HasOneOwner()
    {
        var root = RepoRoot();
        var files = new[] { "PsBash.Cmdlets", "PsBash.Host", "PsBash.Transpiler", "PsBash.Core", "PsBash.Shell" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, "src", p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Select(f => (Path.GetRelativePath(root, f), File.ReadAllText(f)))
            .ToList();
        var offenders = Offenders(files);
        Assert.True(offenders.Count == 0, "Offenders:\n  " + string.Join("\n  ", offenders));
        foreach (var owner in Rules.SelectMany(r => r.Owners).Distinct())
            Assert.True(files.Any(f => Path.GetFileName(f.Item1) == owner), owner + " moved: update this guard");
    }

    [Fact]
    public void TheGuard_CatchesTheBypassesThatCausedTheBugs()
    {
        Assert.Single(Offenders([("InvokeBashNativeCommand.cs",
            "new ChildStdioMode(MergeStderr: StderrMerge.JoinsStdout(cmdlet), InheritStdin: input is null && inheritStdin));")]));
        Assert.Single(Offenders([("X.cs", "var m = mode with { InheritStdin = true };")]));
        Assert.Single(Offenders([("NativeStdinBridge.cs", "while (_cursor.TryTakeUpstream(ct, out var record))")]));
        Assert.Single(Offenders([("X.cs", "cursor.PushFrontUpstream(records);")]));
        Assert.Single(Offenders([("X.cs", "if (dup2(fd, 0) < 0) throw;")]));

        Assert.Empty(Offenders([("NativeStdinBridge.cs", "return new ChildStdioMode(MergeStderr: m, InheritStdin: inherit, Stdin: s);")]));
        Assert.Empty(Offenders([("CursorPipe.cs", "while (_cursor.TryTakeUpstream(ct, out var record))")]));
        Assert.Empty(Offenders([("X.cs", "/// <see cref=\"ChildStdioMode.InheritStdin\"/>: prose")]));
        Assert.Empty(Offenders([("X.cs", "dup2(pipe, 1);")]));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }
}
