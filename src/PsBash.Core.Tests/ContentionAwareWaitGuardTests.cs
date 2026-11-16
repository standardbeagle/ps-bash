using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// A contention-aware wait has exactly ONE clock. The interactive test harness kept two: its pending read was
/// extended while the machine was starved (ContentionProbe), but a per-char <c>UtcNow &gt; deadline</c> check
/// against the ORIGINAL deadline failed the wait on the first char of a prompt that arrived inside the extension
/// (PromptRenderingIntegrationTests: "WaitForPromptAsync timed out after 15.0s / stdout received (1 chars) --- P",
/// only in the full suite). The extension and the verdict now live together in
/// <c>PsBash.Testing.CheckpointDeadline</c> / <c>ContentionProbe.WaitForExitAsync</c>; any other file asking the
/// probe directly is building its own second clock again.
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent.
/// </summary>
public class ContentionAwareWaitGuardTests
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

    private static readonly Regex DirectProbeUse = new(
        @"\b(MeasureAsync\s*\(|StarvedRatio\b|IsStarvedAsync\b)", RegexOptions.Compiled);

    [Fact]
    public void ContentionProbe_IsOnlyConsultedThroughCheckpointDeadline()
    {
        var offenders = new List<string>();
        var src = Path.Combine(RepoRoot(), "src");
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}")) continue;
            var name = Path.GetFileName(file);
            if (name is "ContentionProbe.cs" or "ContentionAwareWaitGuardTests.cs") continue;

            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("//", StringComparison.Ordinal)) continue;
                if (DirectProbeUse.IsMatch(lines[i]))
                    offenders.Add($"{Path.GetRelativePath(src, file)}:{i + 1}: {t}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Wait on a CheckpointDeadline (or ContentionProbe.WaitForExitAsync) instead of asking the probe directly: " +
            "a deadline kept beside the probe ignores its extensions and fails a correct-but-slow run. Offenders:\n  " +
            string.Join("\n  ", offenders));
    }

    // The product form of the same rule: a launcher waiting for a host to come up keeps its clock in a
    // HostStartupDeadline, which extends while the host is still starting. A bare `UtcNow + _startupTimeout` is a
    // fixed verdict on a correct-but-slow cold start ("did not accept connections within 15s", exit 125).
    private static readonly Regex FixedStartupDeadline = new(@"UtcNow\s*\+\s*_startupTimeout", RegexOptions.Compiled);

    [Fact]
    public void HostStartupWaits_KeepTheirClockInHostStartupDeadline()
    {
        var offenders = new List<string>();
        var core = Path.Combine(RepoRoot(), "src", "PsBash.Core");
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}")) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (!lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) && FixedStartupDeadline.IsMatch(lines[i]))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
        }

        Assert.True(offenders.Count == 0,
            "Wait for a starting host with HostStartupDeadline, not a fixed UtcNow + _startupTimeout. Offenders:\n  " +
            string.Join("\n  ", offenders));
    }
}
