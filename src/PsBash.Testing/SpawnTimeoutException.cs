namespace PsBash.Testing;

/// <summary>
/// Thrown when a spawned process does not exit within the configured timeout.
///
/// The unified replacement for the per-suite timeout exceptions. It extends
/// <see cref="TimeoutException"/> so suites that historically asserted
/// <c>ThrowsAsync&lt;TimeoutException&gt;</c> (Escalation) stay green, and
/// suites with a bespoke timeout type (Differential's <c>OracleTimeoutException</c>)
/// can subclass this. Carries partial stdout/stderr captured before the kill
/// so test failures stay diagnosable (QA rubric Directive 9: observability on
/// failure).
///
/// The message contains the literal substring "did not exit within" so test
/// output and CI log scrapers can match it unambiguously.
/// </summary>
public class SpawnTimeoutException : TimeoutException
{
    /// <summary>The executable that was spawned.</summary>
    public string Executable { get; }

    /// <summary>The arguments passed to the executable, space-joined.</summary>
    public string Arguments { get; }

    /// <summary>The timeout that elapsed.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Stdout captured before the process tree was killed (may be partial).</summary>
    public string PartialStdout { get; }

    /// <summary>Stderr captured before the process tree was killed (may be partial).</summary>
    public string PartialStderr { get; }

    public SpawnTimeoutException(
        string executable,
        string arguments,
        TimeSpan timeout,
        string partialStdout,
        string partialStderr)
        : base(BuildMessage(executable, arguments, timeout, partialStdout, partialStderr))
    {
        Executable = executable;
        Arguments = arguments;
        Timeout = timeout;
        PartialStdout = partialStdout;
        PartialStderr = partialStderr;
    }

    private static string BuildMessage(
        string executable, string arguments, TimeSpan timeout,
        string partialStdout, string partialStderr)
        => $"{Path.GetFileName(executable)} did not exit within {timeout.TotalSeconds:F0}s; " +
           $"entire process tree was killed.\n" +
           $"--- args ---\n{arguments}\n" +
           $"--- partial stdout ---\n{partialStdout}\n" +
           $"--- partial stderr ---\n{partialStderr}";
}
