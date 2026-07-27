using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards the two invariants that make a per-invocation bash-variable store
/// implementable later as a ONE-METHOD change rather than a ~100-site audit.
/// See <c>docs/specs/host-lifecycle-contract.md</c> §"the process environment is
/// NOT per-invocation".
///
/// <para>Invariant 1 — every place that spawns a CHILD PROCESS applies the
/// materialization seam (<c>BashVariableStore.ApplyTo</c>), because a store that
/// is not the process environment would otherwise stop `export` reaching the
/// child.</para>
///
/// <para>Invariant 2 — no cmdlet reads or writes a BASH VARIABLE through
/// <c>Environment.*EnvironmentVariable</c> directly. ps-bash's own configuration
/// knobs (<c>PSBASH_*</c>, <c>NO_COLOR</c>, <c>USERNAME</c>, <c>PATH</c>, …) are
/// host process state, not script state, and are explicitly allowed.</para>
///
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash
/// equivalent, so a hand-driven source check is the justified oracle.
/// </summary>
public class BashVariableStoreGuardTests
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

    private static IEnumerable<string> CmdletSources() =>
        Directory.EnumerateFiles(
            Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets"), "*.cs", SearchOption.AllDirectories);

    /// <summary>
    /// Names that are ps-bash HOST configuration, not bash script variables.
    /// Reading these from the real process environment is correct and required:
    /// scoping them per invocation would break the launcher's ability to
    /// configure the host at all.
    /// </summary>
    private static bool IsHostConfigName(string line) =>
        line.Contains("\"PSBASH_", StringComparison.Ordinal)
        || line.Contains("\"NO_COLOR\"", StringComparison.Ordinal)
        || line.Contains("\"USERNAME\"", StringComparison.Ordinal)
        || line.Contains("\"PATH\"", StringComparison.Ordinal);

    /// <summary>
    /// A helper whose name declares it reads HOST CONFIG (e.g.
    /// <c>BashRuntime.IsHostConfigTruthy</c>) takes the variable name as a
    /// PARAMETER, so the literal-name check above cannot see it. The NAME is the
    /// contract — which is exactly why that helper is not called "IsEnvTruthy".
    /// </summary>
    private static bool IsInsideHostConfigHelper(string[] lines, int index)
    {
        // Walk back to the enclosing member declaration.
        for (int i = index; i >= 0 && index - i < 25; i--)
        {
            if (lines[i].Contains("HostConfig", StringComparison.Ordinal)
                && lines[i].Contains("static", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public void NoCmdlet_ReadsOrWritesABashVariable_ThroughEnvironmentDirectly()
    {
        var offenders = new List<string>();

        foreach (var file in CmdletSources())
        {
            // BashVariableStore itself IS the seam — it is the one place allowed
            // to touch Environment for bash variables.
            if (Path.GetFileName(file) == "BashVariableStore.cs") continue;

            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!Regex.IsMatch(line, @"Environment\.(Get|Set)EnvironmentVariable\b")) continue;
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (line.Contains("///", StringComparison.Ordinal)) continue;
                if (IsHostConfigName(line)) continue;
                if (IsInsideHostConfigHelper(lines, i)) continue;

                offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A bash VARIABLE must go through BashVariableStore, not Environment directly "
            + "(host config knobs like PSBASH_*/NO_COLOR/USERNAME/PATH are exempt). Offenders:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void EverySpawnSite_AppliesTheMaterializationSeam()
    {
        var offenders = new List<string>();

        foreach (var file in CmdletSources())
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("Process.Start(", StringComparison.Ordinal)) continue;

            // RunChildProcess is the shared seam and applies it centrally; every
            // OTHER Process.Start is a documented streaming/interactive exemption
            // and must apply it itself.
            int spawnCount = Regex.Matches(text, @"Process\.Start\(").Count;
            int applyCount = Regex.Matches(text, @"BashVariableStore\.ApplyTo\(").Count;

            if (applyCount < spawnCount)
            {
                offenders.Add(
                    $"{Path.GetFileName(file)}: {spawnCount} Process.Start call(s) but only "
                    + $"{applyCount} BashVariableStore.ApplyTo call(s)");
            }
        }

        Assert.True(offenders.Count == 0,
            "A child-process spawn must apply BashVariableStore.ApplyTo so `export` still reaches "
            + "the child once the store moves off the process environment. Offenders:\n  "
            + string.Join("\n  ", offenders));
    }
}
