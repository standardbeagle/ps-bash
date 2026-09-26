using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Guards the solution-wide runtime budget: no xunit project may cap its own
/// test-thread count below the fleet norm. One project pinned
/// <c>maxParallelThreads</c> to 2 while every other suite used 4; because the
/// solution serializes projects (<c>dotnet test -m:1</c>), that single cap made
/// the whole suite wait on the slowest project. Lowering a suite's own
/// parallelism never buys correctness — the differential suite already throttles
/// its WSL bash spawns with its own <c>SemaphoreSlim</c> — but it does serialize
/// every other project behind it. This lives in the differential project because
/// that is where the misconfiguration was, and it fails the full suite if it
/// reappears.
/// </summary>
public class TestSuiteParallelismGuardTests
{
    private const int FleetNorm = 4;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Repo root (the dir with CLAUDE.md) not found walking up from {AppContext.BaseDirectory}.");
    }

    [Fact]
    public void EveryTestProject_DoesNotCapParallelismBelowFleetNorm()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var runnerJson in Directory.GetFiles(
            Path.Combine(root, "src"), "xunit.runner.json", SearchOption.AllDirectories))
        {
            // Only the checked-in source config counts; bin/obj copies are outputs.
            if (runnerJson.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || runnerJson.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var m = Regex.Match(
                File.ReadAllText(runnerJson), @"""maxParallelThreads""\s*:\s*(\d+)");
            if (m.Success && int.Parse(m.Groups[1].Value) < FleetNorm)
            {
                offenders.Add($"{runnerJson} sets maxParallelThreads={m.Groups[1].Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            $"xunit project(s) cap parallelism below the fleet norm of {FleetNorm}, making the whole "
            + "solution wait on them (dotnet test -m:1 serializes projects). Raise maxParallelThreads "
            + "(WSL/PowerShell contention is throttled by the suite's own semaphores, not by the "
            + "test-thread count). Offenders: " + string.Join("; ", offenders));
    }
}
