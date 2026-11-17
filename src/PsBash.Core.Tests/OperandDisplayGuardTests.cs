using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// A diagnostic names an operand AS TYPED (GNU: <c>cat: ./x: No such file or directory</c>). FileSystemHelpers.ProviderPath
/// records the text it is given for OperandDisplay, so a caller must hand it the operand itself: pre-normalizing it
/// (<c>ProviderPath(this, NormalizeOperandPath(x))</c>, a no-op for resolution since ProviderPath normalizes) records
/// the rewritten native path instead, and <c>cat "$PWD/x"</c> printed <c>C:\...\x</c>.
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant; the behavior itself is covered against bash by
/// Differential OperandDisplayDifferentialTests.
/// </summary>
public class OperandDisplayGuardTests
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

    private static readonly Regex PreNormalized = new(
        @"ProviderPath\(\s*\w+\s*,\s*(?:FileSystemHelpers\.)?NormalizeOperandPath\(", RegexOptions.Compiled);

    [Fact]
    public void ProviderPath_IsGivenTheOperandAsTyped_NeverAPreNormalizedPath()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets"), "*.cs",
                     SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (PreNormalized.IsMatch(lines[i]))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0,
            "pass the operand as typed (ProviderPath normalizes it and records the typed text for diagnostics):\n"
            + string.Join("\n", offenders));
    }
    // A cmdlet that resolves its own patterns returns bare paths, and a name recovered from a path is the LAST
    // spelling of that file: `wc ./f f` said `f` twice. Operands resolve through FileSystemHelpers
    // (ResolveOperands / ResolveReaderOperands, which carry the typed name), never a private glob slice.
    // Allowed: a resolver that already pairs each path with its typed name.
    private static readonly Dictionary<string, string> OwnResolverAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FileSystemHelpers.cs"] = "the shared resolver",
        ["InvokeBashLsCommand.cs"] = "returns (Target, Display): each literal keeps its typed name; ls names glob matches itself",
    };

    [Fact]
    public void CmdletOperands_ResolveThroughTheSharedResolver_WhichKeepsTheTypedName()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets"), "*.cs",
                     SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || OwnResolverAllowed.ContainsKey(Path.GetFileName(file)))
                continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].Contains("GetResolvedProviderPathFromPSPath(") && !lines[i].TrimStart().StartsWith("//"))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0,
            "resolve operands with FileSystemHelpers.ResolveOperands / ResolveReaderOperands (they keep each operand's typed "
            + "name; claim it at its turn when resolving ahead):\n" + string.Join("\n", offenders));
    }
}
