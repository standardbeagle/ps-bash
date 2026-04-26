using PsBash.Core.Runtime;
using Xunit;

namespace PsBash.Escalation.Tests;

/// <summary>
/// Scale tests per QA rubric Directive 7 (axis 2 — large input).
///
/// All tests run via subprocess (ps-bash -c "...") with hard timeouts.
/// Mark [Trait("Category","Scale")] so CI can filter them if needed.
///
/// Each test documents the expected completion time and failure mode.
/// </summary>
[Trait("Category", "Escalation")]
[Trait("Category", "Scale")]
public class ScaleTests
{
    private static readonly string? PwshPath = FindPwsh();

    private static string? FindPwsh()
    {
        try { return PwshLocator.Locate(); }
        catch (PwshNotFoundException) { return null; }
    }

    // ── 1. Brace expansion — 1000 elements ────────────────────────────────────

    /// <summary>
    /// Directive 7 (large input) / Failure-surface axis 2.
    /// echo {1..1000} must produce ~1000 space-separated tokens containing
    /// "1" and "1000". Timeout: 10s.
    ///
    /// ps-bash-specific assertion: brace expansion is a transpiler feature;
    /// there is no oracle (bash produces the same output, but we assert
    /// ps-bash-specific token count from the emitted array literal).
    /// </summary>
    [SkippableFact]
    public async Task Scale_BraceExpansion_1000Elements()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, stderr) = await ProcessRunHelper.RunAsync(
            new[] { "-c", "echo {1..1000}" },
            timeout: TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);

        var normalized = stdout.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        // Output must contain both "1" and "1000".
        Assert.Contains("1", normalized, StringComparison.Ordinal);
        Assert.Contains("1000", normalized, StringComparison.Ordinal);

        // Split on whitespace (spaces or newlines between tokens).
        var tokens = normalized.Split(new[] { ' ', '\n', '\t' },
            StringSplitOptions.RemoveEmptyEntries);
        // Must have approximately 1000 tokens (allow ±50 for any joining behavior).
        Assert.True(tokens.Length >= 950 && tokens.Length <= 1050,
            $"Expected ~1000 tokens, got {tokens.Length}. stderr={stderr}");
    }

    // ── 2. Large pipe — ~50 KB via seq | wc -c ───────────────────────────────

    /// <summary>
    /// Directive 7 (large input) / Failure-surface axis 2.
    /// Stream ~50 KB of data through a pipeline: seq 1 10000 | wc -c.
    /// The numbers 1–10000 produce 48894 bytes (each decimal + newline).
    /// Output must be within ±20% of expected. Timeout: 30s.
    ///
    /// Note: heredoc+pipe (`cat <<'EOF' | wc -c`) is not supported in ps-bash
    /// because the transpiler cannot represent a heredoc as the stdin of a piped
    /// command. seq is used to generate equivalent large data instead.
    ///
    /// 10000 lines produce ~48 KB — a meaningful large-input probe that completes
    /// in ~3s raw, well within the 30s timeout including dotnet-run startup.
    ///
    /// ps-bash-specific assertion: exact byte count depends on ps-bash's Invoke-BashSeq
    /// newline convention. We allow ±20% tolerance to cover \r\n vs \n differences.
    /// </summary>
    [SkippableFact]
    public async Task Scale_LargeSeqPipe_WcBytes()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, stderr) = await ProcessRunHelper.RunAsync(
            new[] { "-c", "seq 1 10000 | wc -c" },
            timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(0, exitCode);

        // Extract the first number from stdout (wc -c output).
        var normalized = stdout.Replace("\r\n", "\n").Trim();
        var parts = normalized.Split(new[] { ' ', '\t', '\n' },
            StringSplitOptions.RemoveEmptyEntries);
        Assert.True(parts.Length >= 1,
            $"Expected numeric output from wc -c, got: '{normalized}'. stderr={stderr}");

        Assert.True(long.TryParse(parts[0], out var byteCount),
            $"Expected numeric first token from wc -c, got: '{parts[0]}'. stdout={normalized}");

        // seq 1 10000 produces 48894 bytes on LF systems.
        // On CRLF (Windows): each line gains 1 extra byte — up to 58894.
        // Allow a range that covers both: 38000–75000 bytes.
        Assert.True(byteCount is >= 38_000 and <= 75_000,
            $"Expected 38000–75000 bytes from seq 1 10000 | wc -c, got {byteCount}. stderr={stderr}");
    }

    // ── 3. 10k lines through sed ──────────────────────────────────────────────

    /// <summary>
    /// Directive 7 (large input) / Failure-surface axis 2.
    /// seq 1 10000 | sed 's/^/line: /' — output must have exactly 10000 lines.
    /// Timeout: 30s.
    ///
    /// ps-bash-specific assertion: line count is the oracle (bash and ps-bash
    /// must produce the same number of lines; content prefix is ps-bash-specific).
    /// </summary>
    [SkippableFact]
    public async Task Scale_10kLines_Sed()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, stderr) = await ProcessRunHelper.RunAsync(
            new[] { "-c", "seq 1 10000 | sed 's/^/line: /'" },
            timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(0, exitCode);

        var normalized = stdout.Replace("\r\n", "\n").TrimEnd('\n');
        var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.True(lines.Length == 10000,
            $"Expected 10000 output lines, got {lines.Length}. stderr={stderr}");

        // Spot-check first and last lines have the prefix.
        Assert.StartsWith("line: ", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("line: ", lines[^1], StringComparison.Ordinal);
    }

    // ── 4. Large multi-stage pipeline — seq | sed | wc -l ────────────────────

    /// <summary>
    /// Directive 7 (large input) / Failure-surface axis 2.
    /// Multi-stage pipeline with 10000 lines: seq 1 10000 | sed 's/$/!/' | wc -l.
    /// Output must be 10000. Timeout: 30s.
    ///
    /// Note: yes | head is not used here because ps-bash buffers all pipeline
    /// data in memory — yes produces infinite data faster than head can terminate,
    /// causing OOM / timeout. seq produces finite bounded data.
    ///
    /// This test deliberately overlaps in spirit with Scale_10kLines_Sed to probe
    /// the three-stage pipeline code path specifically (source → transform → count).
    ///
    /// ps-bash-specific assertion: wc -l numeric output is the oracle.
    /// </summary>
    [SkippableFact]
    public async Task Scale_LargePipe_WcCount()
    {
        Skip.If(PwshPath is null, "pwsh not available");

        var (exitCode, stdout, stderr) = await ProcessRunHelper.RunAsync(
            new[] { "-c", "seq 1 10000 | sed 's/$/ /' | wc -l" },
            timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(0, exitCode);

        var normalized = stdout.Replace("\r\n", "\n").Trim();
        // wc -l may produce "  10000" with leading spaces or "10000 -".
        var parts = normalized.Split(new[] { ' ', '\t', '\n' },
            StringSplitOptions.RemoveEmptyEntries);
        Assert.True(parts.Length >= 1,
            $"Expected numeric output from wc -l, got: '{normalized}'. stderr={stderr}");

        Assert.True(long.TryParse(parts[0], out var lineCount),
            $"Expected numeric first token from wc -l, got: '{parts[0]}'. stdout={normalized}");

        // All 10000 lines must pass through the three-stage pipeline.
        Assert.True(lineCount == 10_000,
            $"Expected 10000 lines, got {lineCount}. stderr={stderr}");
    }
}
