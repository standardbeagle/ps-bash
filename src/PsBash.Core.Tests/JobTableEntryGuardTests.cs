using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// One way into the shell's job table (docs/specs/background-jobs.md section 8): every background child, whatever starts
/// it, enters through <c>BashBgJobTable.Enter</c> (its job number, then the list), and <c>$!</c> is set only by the launch
/// that registered it: <c>BashBgJobTable</c> (an in-process job, a <c>nohup CMD &amp;</c> child through
/// <c>AddProcessJob</c>) or the module's <c>Start-BashBgJob</c> (a job in a host of its own).
/// REGRESSION: <c>nohup CMD &amp;</c> set <c>$global:BashBgLastPid</c> itself and never entered the table, so <c>wait</c>
/// returned at once (<c>echo in | { nohup cat &gt; o &amp; wait; }; cat o</c> printed nothing), and <c>jobs</c>,
/// <c>kill %N</c>, <c>wait %N</c> and <c>wait -n</c> did not know it.
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant; behavior vs bash is Differential
/// <c>JobStdinDifferentialTests</c> / <c>WrapperCommandDifferentialTests</c> (the nohup job rows).
/// </summary>
public class JobTableEntryGuardTests
{
    private static readonly (Regex Pattern, string[] Owners, string Rule)[] CsRules =
    [
        (new Regex(@"PSVariable\.Set\(\s*""global:BashBgLastPid""|\$global:BashBgLastPid\s*=(?!=)", RegexOptions.Compiled), ["BashBgJobTable.cs"],
            "$! is set by the launch that entered the job in the table (BashBgJobTable; the module's Start-BashBgJob)"),
        (new Regex(@"\bJobs\.(Add|Insert)\s*\(", RegexOptions.Compiled), ["BashBgJobTable.cs"],
            "a job enters the table through BashBgJobTable.Enter"),
        (new Regex(@"\bnew\s+DetachedJob\s*\(", RegexOptions.Compiled), ["InvokeBashNohupCommand.cs"],
            "a nohup child becomes a job in nohup, which enters it in the table (AddProcessJob)"),
        (new Regex(@"\.AddProcessJob\s*\(", RegexOptions.Compiled), ["InvokeBashNohupCommand.cs"],
            "a running child enters the table as a job from where it was started (nohup)"),
    ];

    /// <summary>psm1 rules: (pattern, the functions allowed to match it, rule). An empty owner list: nowhere.</summary>
    private static readonly (Regex Pattern, string[] Owners, string Rule)[] Psm1Rules =
    [
        (new Regex(@"\$script:(BashBgJobs|BashBgPids)\.(Add|Insert)\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase), [],
            "a job enters the table through BashBgJobTable.Enter (Start-BashBgJob)"),
        (new Regex(@"\.Enter\s*\(\s*\$", RegexOptions.Compiled), ["Start-BashBgJob"],
            "the module enters a job in the table in Start-BashBgJob only"),
        (new Regex(@"\$global:BashBgLastPid\s*=(?!=)(?!\s*\$null\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase), ["Start-BashBgJob"],
            "$! is set where the job entered the table (Start-BashBgJob; clearing it to $null is teardown)"),
    ];

    internal static List<string> CsOffenders(IEnumerable<(string Name, string Text)> files)
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
                foreach (var (pattern, owners, rule) in CsRules)
                    if (pattern.IsMatch(lines[i]) && !owners.Contains(file))
                        offenders.Add($"{name}:{i + 1}: {t.TrimEnd()} ({rule})");
            }
        }
        return offenders;
    }

    private static readonly Regex FunctionStart = new(@"^function\s+([\w-]+)", RegexOptions.Compiled);

    internal static List<string> Psm1Offenders(string psm1)
    {
        var offenders = new List<string>();
        string? function = null;
        var lines = psm1.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (FunctionStart.Match(lines[i]) is { Success: true } m) function = m.Groups[1].Value;
            else if (lines[i].Length > 0 && lines[i][0] == '}') { function = null; continue; }
            var t = lines[i].TrimStart();
            if (t.StartsWith("#", StringComparison.Ordinal)) continue;
            foreach (var (pattern, owners, rule) in Psm1Rules)
                if (pattern.IsMatch(lines[i]) && (function is null || !owners.Contains(function, StringComparer.OrdinalIgnoreCase)))
                    offenders.Add($"PsBash.psm1:{i + 1} ({function ?? "top level"}): {t.TrimEnd()} ({rule})");
        }
        return offenders;
    }

    [Fact]
    public void JobTable_HasOneWayIn()
    {
        var root = RepoRoot();
        var files = new[] { "PsBash.Cmdlets", "PsBash.Host", "PsBash.Transpiler", "PsBash.Core", "PsBash.Shell" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, "src", p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Select(f => (Path.GetRelativePath(root, f), File.ReadAllText(f)))
            .ToList();
        var psm1 = File.ReadAllText(Path.Combine(root, "src", "PsBash.Module", "PsBash.psm1"));
        var offenders = CsOffenders(files).Concat(Psm1Offenders(psm1)).ToList();
        Assert.True(offenders.Count == 0, "Offenders:\n  " + string.Join("\n  ", offenders));
        foreach (var owner in CsRules.SelectMany(r => r.Owners).Distinct())
            Assert.True(files.Any(f => Path.GetFileName(f.Item1) == owner), owner + " moved: update this guard");

        // The ways in are still there: Start-BashBgJob enters through the table, nohup's job through AddProcessJob.
        Assert.Contains("$script:BashBgJobTable.Enter($BgJob)", psm1);
        Assert.Contains("$global:BashBgLastPid = $BgJob.Id", psm1);
        var nohup = File.ReadAllText(Path.Combine(root, "src", "PsBash.Cmdlets", "InvokeBashNohupCommand.cs"));
        Assert.Matches(new Regex(@"new DetachedJob\(child, stdin\)[\s\S]*\.AddProcessJob\(this, job, "), nohup);
    }

    [Fact]
    public void TheGuard_CatchesTheBypassesThatCausedTheBugs()
    {
        // The bug: nohup set $! itself and the child never became a job.
        Assert.Single(CsOffenders([("InvokeBashNohupCommand.cs", "InvokeCommand.InvokeScript(\"param($p) $global:BashBgLastPid = $p\", child.Pid);")]));
        Assert.Single(CsOffenders([("InvokeBashNohupCommand.cs", "SessionState.PSVariable.Set(\"global:BashBgLastPid\", child.Pid);")]));
        Assert.Single(CsOffenders([("InvokeBashSetsidCommand.cs", "table.Jobs.Add(record);")]));
        Assert.Single(CsOffenders([("InvokeBashSetsidCommand.cs", "var job = new DetachedJob(child, null);")]));
        Assert.Single(Psm1Offenders("function Invoke-BashNohup {\n    $script:BashBgJobs.Add($rec)\n}\n"));
        Assert.Single(Psm1Offenders("function Invoke-BashNohup {\n    $global:BashBgLastPid = $p\n}\n"));
        Assert.Single(Psm1Offenders("function Start-BashBgJob {\n    $script:BashBgJobs.Add($BgJob)\n}\n"));
        Assert.Single(Psm1Offenders("$global:BashBgLastPid = 5\n"));

        Assert.Empty(CsOffenders([("BashBgJobTable.cs", "cmdlet.SessionState.PSVariable.Set(\"global:BashBgLastPid\", id);")]));
        Assert.Empty(CsOffenders([("PsBuild.cs", "return braced ? \"${global:BashBgLastPid}\" : \"$global:BashBgLastPid\";")]));
        Assert.Empty(CsOffenders([("SdkWorker.cs", "if (_ps.Runspace.SessionStateProxy.GetVariable(\"BashBgLastPid\") is null) return;")]));
        Assert.Empty(Psm1Offenders("function Start-BashBgJob {\n    $script:BashBgJobTable.Enter($BgJob)\n    $global:BashBgLastPid = $BgJob.Id\n}\n"));
        Assert.Empty(Psm1Offenders("function Remove-BashBgState {\n    $global:BashBgLastPid = $null\n}\n$global:BashBgLastPid = $null\n"));
        Assert.Empty(Psm1Offenders("function X {\n    if ($global:BashBgLastPid -eq 3) { }\n}\n"));
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
