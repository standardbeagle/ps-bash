using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Enforces the findability invariants from .claude/rules/findability.md. Lives in the build-gate
/// test project so a violation fails CI (rules/skills only guide an agent; this blocks drift).
/// Oracle note (qa-rubric Directive 1): repo-structure invariants, ps-bash-specific, no bash oracle.
/// </summary>
public class FindabilityGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Repo root (the dir with CLAUDE.md) not found walking up from {AppContext.BaseDirectory}.");
    }

    [Fact]
    public void EverySpec_IsListedInSpecIndex()
    {
        var root = RepoRoot();
        var specsDir = Path.Combine(root, "docs", "specs");
        if (!Directory.Exists(specsDir))
        {
            return;
        }

        // The index is the findability surface for specs (keeps CLAUDE.md small per Finding A);
        // every spec must appear in it so none becomes an orphan.
        var indexPath = Path.Combine(specsDir, "README.md");
        Assert.True(File.Exists(indexPath), "docs/specs/README.md index is missing.");
        var index = File.ReadAllText(indexPath);

        var orphans = Directory.GetFiles(specsDir, "*.md")
            .Select(Path.GetFileName)
            .Where(name => name is not null
                && !string.Equals(name, "README.md", StringComparison.OrdinalIgnoreCase)
                && !index.Contains(name, StringComparison.Ordinal))
            .ToList();

        Assert.True(orphans.Count == 0,
            "specs not listed in docs/specs/README.md (orphans = unfindable): " + string.Join(", ", orphans));
    }

    [Fact]
    public void CodeMap_ExistsAndIsReferencedFromClaudeMd()
    {
        var root = RepoRoot();
        Assert.True(File.Exists(Path.Combine(root, "CODE_MAP.md")),
            "CODE_MAP.md is missing — it is the top-of-context navigation index.");
        var claude = File.ReadAllText(Path.Combine(root, "CLAUDE.md"));
        Assert.Contains("CODE_MAP.md", claude, StringComparison.Ordinal);
    }

    [Fact]
    public void FlagSpecs_HaveExactlyOneSource()
    {
        var root = RepoRoot();
        Assert.True(
            File.Exists(Path.Combine(root, "src", "PsBash.Module", "BashFlagSpecs.json")),
            "Canonical PsBash.Module/BashFlagSpecs.json is missing.");

        var dup = Path.Combine(root, "src", "PsBash.Host", "Resources", "FlagSpecs.json");
        Assert.False(File.Exists(dup),
            $"A second flag-spec source reappeared at {dup}. Keep ONE source "
            + "(PsBash.Module/BashFlagSpecs.json); the host embeds it under the FlagSpecs.json resource name.");
    }
}
