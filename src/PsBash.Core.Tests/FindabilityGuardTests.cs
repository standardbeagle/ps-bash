using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

    [Fact]
    public void Psm1_HasNoTopLevelLoops_ThatLeakVariablesIntoTheRunspace()
    {
        // PsBash.psm1 is imported into the host runspace at module (script) scope.
        // A `foreach`/`for` written at column 0 therefore leaks its loop variable
        // into the scope where transpiled bash and the binary cmdlets resolve
        // names — a leaked `$a` ('type', the last conflicting alias) once made
        // `a=5; echo $((a))` return 0. Such loops MUST be wrapped in `& { … }`
        // (block scope) or live inside a function. This guard blocks regressions.
        var root = RepoRoot();
        var psm1 = File.ReadAllText(Path.Combine(root, "src", "PsBash.Module", "PsBash.psm1"));
        var offenders = Regex.Matches(psm1, @"(?m)^(foreach|for)\b[^\n]*")
            .Select(m => m.Value.Trim())
            .ToList();
        Assert.True(offenders.Count == 0,
            "Top-level (column-0) loop in PsBash.psm1 leaks its loop variable into the runspace and can "
            + "shadow a transpiled bash variable. Wrap it in `& { … }` or move it into a function. Found: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void ReleaseNotes_UnderPsGalleryLimit()
    {
        // PSGallery rejects a manifest whose ReleaseNotes exceeds 10600 chars with HTTP 400 — and
        // the publish step is continue-on-error, so it fails the upload while the run still goes
        // green. This guard catches the accumulating-notes overflow BEFORE a release. Keep notes
        // to recent versions + a link to GitHub releases for history.
        var root = RepoRoot();
        var psd1 = File.ReadAllText(Path.Combine(root, "src", "PsBash.Module", "PsBash.psd1"));
        var m = Regex.Match(psd1, "ReleaseNotes = '((?:[^']|'')*)'");
        Assert.True(m.Success, "ReleaseNotes not found in PsBash.psd1.");
        var value = m.Groups[1].Value.Replace("''", "'"); // un-escape PS single-quote doubling
        Assert.True(value.Length <= 10600,
            $"PsBash.psd1 ReleaseNotes is {value.Length} chars; PSGallery caps it at 10600 (publish 400s). "
            + "Trim to recent versions + a link to GitHub releases.");
    }
}
