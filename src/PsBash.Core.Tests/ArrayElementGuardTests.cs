using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards the run-time array element invariant: a cmdlet that holds a <c>NAME[subscript]</c> from an operand or a value
/// (<c>printf -v 'm[$k]'</c>, <c>unset 'm[$k]'</c>, <c>x='a[i]'; $(( x = 1 ))</c>) resolves and writes it through
/// <c>PsBash.Cmdlets.BashArrayElement</c>, never with its own copy of the rules.
/// <para>
/// The bug class: three cmdlets re-derived them, and <c>printf -v</c>'s copy never expanded <c>$k</c>, so
/// <c>printf -v 'm[$k]'</c> added a key named <c>$k</c> (oils <c>array-assoc</c> spec, Windows fleet s22a). The marker
/// of a copy is the ad-hoc <c>$k</c> / <c>${k}</c> replacement regex. Behavior against bash:
/// <c>ArraySubscriptWordTests.RunTimeElementOperand_ResolvesLikeBash</c>.
/// </para>
/// <para>Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent, so a source check.</para>
/// </summary>
public class ArrayElementGuardTests
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

    private const string KeyExpansionRegex = @"\$\{?(\w+)\}?";

    [Fact]
    public void OnlyBashArrayElementExpandsARunTimeSubscriptsVariables()
    {
        string root = Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file);
            if (rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.GetFileName(file) == "BashArrayElement.cs") continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].Contains(KeyExpansionRegex, StringComparison.Ordinal)
                    && !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                    offenders.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0,
            "Resolve a run-time NAME[subscript] through BashArrayElement (AssocKey / Index / Write), not a local copy:\n"
            + string.Join('\n', offenders));
        Assert.Contains(KeyExpansionRegex, File.ReadAllText(Path.Combine(root, "BashArrayElement.cs")), StringComparison.Ordinal);
    }
}
