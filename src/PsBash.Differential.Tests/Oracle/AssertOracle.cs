using PsBash.Core.Transpiler;
using Xunit;
using Xunit.Sdk;

namespace PsBash.Differential.Tests.Oracle;

/// <summary>
/// Single-method assertion API for differential oracle tests.
///
/// Usage:
///   await AssertOracle.EqualAsync("echo hello");
///
/// On mismatch, throws <see cref="XunitException"/> whose message contains a
/// structured diff bundle with: input script, both stdouts (canonicalized),
/// both stderrs, both exit codes, both wall times, transpiled PowerShell text
/// (from PSBASH_DEBUG=1 stderr capture), and a filtered env snapshot.
///
/// On bash or ps-bash unavailability, the test is skipped via Skip.If.
/// </summary>
public static class AssertOracle
{
    private static readonly BashOracleFixture Fixture = new();

    // Environment variable names that are safe to include in the bundle
    private static readonly HashSet<string> AllowedEnvKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "HOME", "USERPROFILE", "TEMP", "TMP", "TMPDIR",
        "OS", "PROCESSOR_ARCHITECTURE", "COMPUTERNAME", "USERNAME",
        "TERM", "LANG", "LC_ALL", "SHELL",
        "PSBASH_DEBUG", "PSBASH_UNIX_PATHS", "DOTNET_ROOT",
    };

    /// <summary>
    /// Runs <paramref name="script"/> through bash and ps-bash, asserts identical
    /// canonicalized stdout, stderr, and exit code.
    /// Skips when bash or ps-bash binary is not available.
    /// </summary>
    /// <param name="script">The bash script to compare.</param>
    /// <param name="timeout">Per-process timeout; defaults to 5 s.</param>
    /// <exception cref="XunitException">When outputs differ.</exception>
    public static async Task EqualAsync(
        string script,
        TimeSpan? timeout = null)
    {
        Skip.If(Fixture.BashPath is null, "bash not available on this platform");
        Skip.If(Fixture.PsBashPath is null, "ps-bash binary not found -- build PsBash.Shell first");

        var (bashResult, psBashResult) = await Fixture.RunBothAsync(script, timeout);

        var bashStdout = Canonicalizer.Canonicalize(bashResult.Stdout);
        var psBashStdout = Canonicalizer.Canonicalize(psBashResult.Stdout);
        var bashStderr = Canonicalizer.Canonicalize(bashResult.Stderr);

        // Extract transpiled PS text from ps-bash debug stderr lines
        var transpiledPs = ExtractTranspiled(psBashResult.Stderr);
        var psBashStderr = Canonicalizer.Canonicalize(
            StripDebugLines(psBashResult.Stderr));

        bool stdoutMatch = bashStdout == psBashStdout;
        bool stderrMatch = bashStderr == psBashStderr;
        bool exitMatch = bashResult.ExitCode == psBashResult.ExitCode;

        if (stdoutMatch && stderrMatch && exitMatch)
            return;

        var bundle = BuildDiffBundle(
            script,
            bashResult, psBashResult,
            bashStdout, psBashStdout,
            bashStderr, psBashStderr,
            transpiledPs);

        throw new XunitException(bundle);
    }

    /// <summary>
    /// Extracts the "[ps-bash] transpiled: ..." line from PSBASH_DEBUG=1 stderr output.
    /// Returns empty string when the debug line is absent.
    /// </summary>
    private static string ExtractTranspiled(string debugStderr)
    {
        const string marker = "[ps-bash] transpiled: ";
        foreach (var line in debugStderr.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith(marker, StringComparison.Ordinal))
                return trimmed.Substring(marker.Length);
        }

        return string.Empty;
    }

    /// <summary>
    /// Removes [ps-bash] debug lines from stderr so they do not pollute the stderr diff.
    /// </summary>
    private static string StripDebugLines(string stderr)
    {
        var lines = stderr.Split('\n');
        return string.Join('\n', lines.Where(l =>
            !l.TrimEnd('\r').StartsWith("[ps-bash] ", StringComparison.Ordinal)));
    }

    private static string BuildDiffBundle(
        string script,
        OracleResult bashResult,
        OracleResult psBashResult,
        string bashStdout,
        string psBashStdout,
        string bashStderr,
        string psBashStderr,
        string transpiledPs)
    {
        // Compute transpiled PS if not captured from debug output
        string ps = transpiledPs;
        if (string.IsNullOrEmpty(ps))
        {
            try { ps = BashTranspiler.Transpile(script); }
            catch (Exception ex) { ps = $"<transpile error: {ex.Message}>"; }
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== Oracle Diff Bundle ===");
        sb.AppendLine();
        sb.AppendLine("--- Input Script ---");
        sb.AppendLine(script);
        sb.AppendLine();
        sb.AppendLine("--- Transpiled PowerShell ---");
        sb.AppendLine(ps);
        sb.AppendLine();
        sb.AppendLine($"--- Exit Codes --- bash={bashResult.ExitCode} ps-bash={psBashResult.ExitCode}");
        sb.AppendLine($"--- Wall Times  --- bash={bashResult.WallMs}ms ps-bash={psBashResult.WallMs}ms");
        sb.AppendLine();
        sb.AppendLine("--- bash stdout (canonicalized) ---");
        sb.AppendLine(bashStdout);
        sb.AppendLine("--- ps-bash stdout (canonicalized) ---");
        sb.AppendLine(psBashStdout);
        sb.AppendLine();
        sb.AppendLine("--- bash stderr (canonicalized) ---");
        sb.AppendLine(bashStderr);
        sb.AppendLine("--- ps-bash stderr (canonicalized) ---");
        sb.AppendLine(psBashStderr);
        sb.AppendLine();
        sb.AppendLine("--- Diff (stdout) ---");
        sb.AppendLine(ComputeLineDiff(bashStdout, psBashStdout));
        sb.AppendLine();
        sb.AppendLine("--- Diff (stderr) ---");
        sb.AppendLine(ComputeLineDiff(bashStderr, psBashStderr));
        sb.AppendLine();
        sb.AppendLine("--- Environment Snapshot ---");
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString() ?? "";
            if (AllowedEnvKeys.Contains(key))
                sb.AppendLine($"  {key}={entry.Value}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Produces a simple line diff between two canonicalized strings.
    /// Lines only in <paramref name="expected"/> are prefixed with '-',
    /// lines only in <paramref name="actual"/> with '+', common lines with ' '.
    /// </summary>
    private static string ComputeLineDiff(string expected, string actual)
    {
        if (expected == actual)
            return "(identical)";

        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        var sb = new System.Text.StringBuilder();

        int i = 0, j = 0;
        while (i < expectedLines.Length || j < actualLines.Length)
        {
            if (i < expectedLines.Length && j < actualLines.Length &&
                expectedLines[i] == actualLines[j])
            {
                sb.AppendLine($"  {expectedLines[i]}");
                i++; j++;
            }
            else
            {
                if (i < expectedLines.Length)
                    sb.AppendLine($"- {expectedLines[i++]}");
                if (j < actualLines.Length)
                    sb.AppendLine($"+ {actualLines[j++]}");
            }
        }

        return sb.ToString();
    }
}
