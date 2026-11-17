using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards <see cref="PowerShellAliases"/>: every session state built for a runspace that runs bash drops PowerShell's
/// built-in aliases (<c>RemoveForeignAliases</c>). The bug class: each builder started from <c>CreateDefault</c>, which
/// carries about 135 aliases, and PowerShell resolves an alias before a function or a program, so <c>write -- $x</c> ran
/// <c>Write-Output</c> (oils hay.spec.sh, Windows fleet s22e) and <c>r</c> re-ran the last command. A new builder that
/// forgets the strip brings them all back. Behavior: Host.Tests/ForeignAliasTests,
/// Cmdlets.Tests/BashSessionStateTests, Differential.Tests/PowerShellAliasWordTests.
/// <para>Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent, so a source check.</para>
/// </summary>
public class PowerShellAliasGuardTests
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

    [Fact]
    public void EverySessionStateBuilderDropsPowerShellsAliases()
    {
        var offenders = new List<string>();
        int builders = 0;
        foreach (var project in Directory.EnumerateDirectories(Path.Combine(RepoRoot(), "src")))
        {
            string name = Path.GetFileName(project);
            if (name.EndsWith(".Tests", StringComparison.Ordinal) || name is "PsBash.Testing" or "PsBash.Spec") continue;
            foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(project, file);
                if (rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                string text = File.ReadAllText(file);
                if (!text.Contains("InitialSessionState.Create", StringComparison.Ordinal)) continue;
                builders++;
                if (!text.Contains("RemoveForeignAliases(", StringComparison.Ordinal))
                    offenders.Add($"{name}/{rel}");
            }
        }
        Assert.True(builders >= 3, $"found {builders} session-state builders; the scan no longer sees them");
        Assert.True(offenders.Count == 0,
            "A session state a runspace runs bash in must drop PowerShell's aliases (BashSessionState.RemoveForeignAliases "
            + "/ SdkRunspace.RemoveForeignAliases):\n" + string.Join('\n', offenders));
    }

    [Theory]
    [InlineData("Invoke-BashEcho", true)]
    [InlineData("invoke-bashls", true)]
    [InlineData("Write-Output", false)]
    [InlineData("Get-ChildItem", false)]
    [InlineData(null, false)]
    public void IsBashRuntimeAlias_KeepsOnlyTheRuntimesOwn(string? definition, bool kept)
        => Assert.Equal(kept, PowerShellAliases.IsBashRuntimeAlias(definition));
}
