using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A command that runs a child in another directory (<c>env -C</c>, <c>find -execdir</c>) moves it through
/// <see cref="ChildDirectory.Enter"/>, which moves the PowerShell location, the process cwd AND the shell's logical cwd
/// (<c>pwd</c>) and restores all three. REGRESSION: env and find each moved the location themselves; env forgot the
/// process cwd and both forgot the logical cwd, so <c>env -C DIR pwd</c> printed the caller's directory. This scan
/// fails on a cmdlet that calls the location API itself. The allowed files restore SAVED shell state (subshell /
/// job state, <c>dirs -c</c>), they do not run a child elsewhere. Oracle note (qa-rubric Directive 1): a
/// repo-structure invariant; behaviour is pinned by ChildDirectoryDifferentialTests.
/// </summary>
public class ChildDirectoryGuardTests
{
    private static readonly Regex LocationApi = new(@"\.(SetLocation|PushCurrentLocation)\(", RegexOptions.Compiled);

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "ChildDirectory.cs", "BashShellState.cs", "InvokeBashDirsCommand.cs",
    };

    [Fact]
    public void Cmdlets_MoveTheLocationForAChildOnlyThroughChildDirectory()
    {
        var dir = Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var name = Path.GetFileName(file);
            if (Allowed.Contains(name)) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var code = lines[i].TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal)) continue;
                if (LocationApi.IsMatch(code)) offenders.Add($"{name}:{i + 1}: {code}");
            }
        }
        Assert.True(offenders.Count == 0,
            "Run a child in another directory through ChildDirectory.Enter (location + process cwd + pwd):\n"
            + string.Join("\n", offenders));
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
