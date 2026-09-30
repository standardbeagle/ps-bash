using System.Management.Automation;
using Xunit.Sdk;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Structured outcome of running a script in a test runspace: what a bash caller would see
/// (stdout, stderr, exit status) instead of only the output strings. A helper that returns
/// just strings lets a FAILING command pass any test that asserts on filesystem state or
/// "no exception" — the failure (non-zero exit, error record) is invisible. Use
/// <see cref="AssertSuccess"/> for tests that expect success and
/// <see cref="AssertFailed"/> for tests that expect a specific failure.
///
/// <c>Stdout</c> is the pipeline output, one entry per emitted object (its ToString), joined
/// with newlines; <c>Errors</c> are the non-terminating error records the command wrote;
/// <c>Stderr</c> is their messages, one per line; <c>ExitCode</c> is the runspace's
/// <c>$global:LASTEXITCODE</c> after the script (null/unset counts as 0, like a command that
/// never failed). A terminating exception is NOT swallowed — it propagates out of
/// <see cref="Run"/> and fails the test loudly.
/// </summary>
public sealed record CmdResult(
    string Stdout,
    string Stderr,
    int ExitCode,
    IReadOnlyList<ErrorRecord> Errors,
    IReadOnlyList<string> Lines)
{
    /// <summary>
    /// Runs <paramref name="script"/> in <paramref name="pwsh"/> (already acquired/reset by the
    /// caller) and captures output, error records and exit status. <c>$LASTEXITCODE</c> is
    /// cleared first so a stale code from an earlier command cannot masquerade as this one's.
    /// </summary>
    public static CmdResult Run(PowerShell pwsh, string script)
    {
        pwsh.Commands.Clear();
        pwsh.Streams.ClearStreams();
        pwsh.AddScript("$global:LASTEXITCODE = $null").Invoke();
        pwsh.Commands.Clear();

        var output = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        var errors = pwsh.Streams.Error.ToArray();
        pwsh.Streams.ClearStreams();

        var exit = pwsh.AddScript("$global:LASTEXITCODE").Invoke();
        pwsh.Commands.Clear();
        var code = exit.Count > 0 && exit[0]?.BaseObject is { } o ? Convert.ToInt32(o) : 0;

        var lines = output.Select(x => x?.ToString() ?? "").ToArray();
        var stderr = string.Join("\n", errors.Select(e => e.ToString()));
        return new CmdResult(string.Join("\n", lines), stderr, code, errors, lines);
    }

    /// <summary>Exit 0, no error records. Returns this so callers can chain onto the output.</summary>
    public CmdResult AssertSuccess()
    {
        if (ExitCode != 0 || Errors.Count != 0)
            throw new XunitException(
                $"expected success (exit 0, no error records) but got exit {ExitCode}, " +
                $"{Errors.Count} error record(s).\n{Describe()}");
        return this;
    }

    /// <summary>
    /// Exact non-zero exit status and (when given) every <paramref name="stderrContains"/>
    /// fragment present in stderr. With no fragments only the exit status is checked — a
    /// legitimate silent failure (<c>grep</c> no-match, exit 1) writes no error record.
    /// </summary>
    public CmdResult AssertFailed(int exitCode, params string[] stderrContains)
    {
        if (exitCode == 0) throw new ArgumentException("use AssertSuccess for exit 0", nameof(exitCode));
        if (ExitCode != exitCode)
            throw new XunitException($"expected exit {exitCode} but got {ExitCode}.\n{Describe()}");
        foreach (var frag in stderrContains)
            if (!Stderr.Contains(frag, StringComparison.Ordinal))
                throw new XunitException($"stderr does not contain '{frag}'.\n{Describe()}");
        return this;
    }

    private string Describe() =>
        $"--- stdout ---\n{Stdout}\n--- stderr ---\n{Stderr}\n--- exit: {ExitCode} ---";
}
