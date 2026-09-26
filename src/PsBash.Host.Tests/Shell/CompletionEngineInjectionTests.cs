using System.Management.Automation;
using PsBash.Core.Runtime;
using PsBash.Host.Runtime;
using PsBash.Host.Shell;
using Xunit;

namespace PsBash.Host.Tests.Shell;

/// <summary>
/// Regression tests for the completion-engine script-injection hole: user text was
/// interpolated into a single-quoted PowerShell string with only ASCII <c>'</c> escaped, but
/// PowerShell also terminates such a string at the Unicode curly single quotes
/// U+2018..U+201B. A pasted token containing one of those closed the string and the remainder
/// ran through <c>AddScript</c> on every keystroke.
///
/// Each case builds a payload that would set <c>$global:PSBASH_PWNED</c> through the live
/// query, captures the exact expression the engine handed the worker, executes it in a real
/// runspace, and asserts the marker was never set. Oracle note (qa-rubric Directive 1): this
/// is a ps-bash-specific interactive surface with no bash oracle; the real-runspace execution
/// is the proof of no side effect.
/// </summary>
public class CompletionEngineInjectionTests
{
    private sealed class CapturingWorker : IWorker, ICompletionWorker
    {
        public string? Expression;
        public int QueryCount;
        public Action<string>? OutputCallback { get; set; }
        public bool HasExited { get; set; }

        public Task<int> ExecuteAsync(string command, CancellationToken ct = default,
            IReadOnlyList<KeyValuePair<string, string>>? environment = null) => Task.FromResult(0);

        public Task<string> QueryAsync(string expression, CancellationToken ct = default)
        {
            QueryCount++;
            Expression = expression;
            return Task.FromResult(string.Empty);
        }

        Task<IReadOnlyList<string>> ICompletionWorker.CompleteInputAsync(string input, int cursorIndex, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static CompletionEngine Engine(IWorker worker) => new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        cwd: () => Environment.CurrentDirectory,
        lastCommand: () => null,
        history: null,
        worker: worker);

    /// <summary>
    /// Execute the captured expression in a fresh runspace, then read the marker global it
    /// would have set had the injected text escaped the string literal.
    /// </summary>
    private static bool ExecutedMarker(string expression)
    {
        using var ps = PowerShell.Create();
        ps.AddScript(expression);
        try
        {
            ps.Invoke();
        }
        catch (RuntimeException)
        {
            // The expression's own failure is irrelevant; only whether the injected
            // command ran (setting the marker) is under test.
        }

        ps.Commands.Clear();
        ps.AddScript("$global:PSBASH_PWNED");
        return ps.Invoke().Any(o => string.Equals(o?.ToString(), "12345", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData('\u2018')]
    [InlineData('\u2019')]
    [InlineData('\u201a')]
    [InlineData('\u201b')]
    public async Task CompleteAsync_CommandTokenWithCurlyQuote_DoesNotExecuteInjectedCommand(char quote)
    {
        // No whitespace: SplitAtWordBoundaryQuoteAware splits the token on spaces, so the
        // payload stays one token; ';' both terminates the interpolated Get-Command argument
        // and runs the marker assignment; '#' comments the query's own tail.
        var payload = $"x{quote};$global:PSBASH_PWNED=12345;#";
        var worker = new CapturingWorker();

        await Engine(worker).CompleteAsync(payload, payload.Length, default);

        Assert.Equal(1, worker.QueryCount);
        Assert.NotNull(worker.Expression);
        Assert.False(ExecutedMarker(worker.Expression!), "the injected command ran through AddScript");
    }

    [Theory]
    [InlineData('\u2018')]
    [InlineData('\u2019')]
    [InlineData('\u201a')]
    [InlineData('\u201b')]
    public async Task GetFlagHintsAsync_FlagTokenWithCurlyQuote_DoesNotExecuteInjectedCommand(char quote)
    {
        // The flag-like token reaches the '-like "<prefix>*"' filter. The payload closes the
        // open braces (Where-Object + if) so the whole expression still parses once injected,
        // then runs the marker assignment on the way through.
        var payload = "x" + quote + ";$global:PSBASH_PWNED=12345;}}#";
        var line = "Get-ChildItem -" + payload;
        var worker = new CapturingWorker();

        await Engine(worker).GetFlagHintsAsync(line, line.Length, default);

        Assert.Equal(1, worker.QueryCount);
        Assert.NotNull(worker.Expression);
        Assert.False(ExecutedMarker(worker.Expression!), "the injected command ran through AddScript");
    }
}
