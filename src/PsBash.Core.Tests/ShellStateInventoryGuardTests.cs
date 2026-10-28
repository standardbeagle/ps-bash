using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Inventory guard for docs/specs/background-jobs.md §5. A subshell (<c>BashShellState</c>) and a background
/// job (<c>BashStateSnapshot</c>) both copy the parent's shell state, and both go by one list:
/// <c>BashShellState.GlobalNames</c>. A new <c>$global:</c> the module or a cmdlet reads or writes must either be
/// in that list (so a subshell restores it and a job inherits it) or be named below as NOT shell state, with the
/// reason. Without this a new global silently leaks out of subshells and is missing from jobs.
/// Oracle note: repo-structure invariant with no bash equivalent, so a source scan is the justified oracle.
/// </summary>
public class ShellStateInventoryGuardTests
{
    /// <summary>Globals that are not per-shell state, each with why.</summary>
    private static readonly Dictionary<string, string> NotShellState = new(StringComparer.Ordinal)
    {
        ["LASTEXITCODE"] = "PowerShell's own; the status is carried by the command result, not copied",
        ["PsBashVersion"] = "constant, set at module load",
        ["BashVersionInfo"] = "constant, set at module load",
        ["BASH_VERSINFO"] = "constant alias of BashVersionInfo, set at module load (so declare -p sees it)",
        ["BashVersion"] = "constant, set at module load",
        ["BashStartTime"] = "constant, set at module load (SECONDS base)",
        ["__PsBashCwd"] = "host plumbing: the worker's working-directory request",
        ["__parentPid"] = "host plumbing: the owning process id",
        ["__BashStdIn"] = "per-command stdin plumbing, rebuilt for every command",
        ["__BashUnbound"] = "one-statement hand-off from Invoke-BashArith to the emitted nounset guard, cleared at once",
        ["BashHookErrors"] = "diagnostic buffer, per runspace",
        ["__BashEvalDepth"] = "eval recursion counter; a job process starts at 0",
        ["__BashBaselineFunctions"] = "names of the functions every host defines at startup; read by BashStateSnapshot",
    };

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

    private static HashSet<string> DeclaredGlobals()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets", "BashShellState.cs"));
        var block = Regex.Match(src, @"GlobalNames\s*=\s*\[(?<body>.*?)\];", RegexOptions.Singleline).Groups["body"].Value;
        return Regex.Matches(block, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<(string File, string Name)> UsedGlobals()
    {
        var root = RepoRoot();
        var files = new List<string> { Path.Combine(root, "src", "PsBash.Module", "PsBash.psm1") };
        files.AddRange(Directory.EnumerateFiles(Path.Combine(root, "src", "PsBash.Cmdlets"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")));
        foreach (var f in files)
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"[$""]global:([A-Za-z_][A-Za-z0-9_]*)"))
                yield return (Path.GetFileName(f), m.Groups[1].Value);
    }

    [Fact]
    public void EveryGlobalIsShellStateOrDeclaredNotShellState()
    {
        var declared = DeclaredGlobals();
        Assert.NotEmpty(declared);
        var unknown = UsedGlobals()
            .Where(u => !declared.Contains(u.Name) && !NotShellState.ContainsKey(u.Name))
            .Select(u => $"{u.Name} ({u.File})").Distinct().OrderBy(x => x).ToList();
        Assert.True(unknown.Count == 0,
            "New $global: state without a decision. Add it to BashShellState.GlobalNames (subshells restore it, jobs " +
            "inherit it; list it in BashStateSnapshot.NotInherited if a child must not) or to NotShellState here:\n  "
            + string.Join("\n  ", unknown));
    }

    [Fact]
    public void NotInheritedNamesAreAllDeclaredGlobals()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets", "BashStateSnapshot.cs"));
        var block = Regex.Match(src, @"NotInherited\s*=\s*\[(?<body>.*?)\];", RegexOptions.Singleline).Groups["body"].Value;
        var names = Regex.Matches(block, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(names);
        var declared = DeclaredGlobals();
        Assert.All(names, n => Assert.Contains(n, declared));
    }
}
