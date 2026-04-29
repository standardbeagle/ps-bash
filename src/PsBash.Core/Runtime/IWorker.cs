namespace PsBash.Core.Runtime;

/// <summary>
/// Narrow worker boundary that decouples the launcher from any specific worker
/// process implementation. Concrete implementations (e.g.
/// <see cref="PwshWorker"/>) own the underlying transport (stdin/stdout pipes,
/// in-process runspace, etc.) and translate the contract below into that
/// transport.
/// </summary>
/// <remarks>
/// This is a pure-refactor seam (architecture-migration task T01). Behavior
/// matches the existing <see cref="PwshWorker"/> surface verbatim:
/// <list type="bullet">
///   <item><description><see cref="ExecuteAsync"/> runs a command and returns its exit code, routing line output through <see cref="OutputCallback"/> when set.</description></item>
///   <item><description><see cref="QueryAsync"/> runs a command and returns captured stdout as a string, transparently saving and restoring <see cref="OutputCallback"/> for the caller.</description></item>
///   <item><description>After <see cref="IAsyncDisposable.DisposeAsync"/>, both methods throw <see cref="ObjectDisposedException"/>.</description></item>
/// </list>
/// </remarks>
public interface IWorker : IAsyncDisposable
{
    /// <summary>
    /// Optional callback that receives each output line. When set,
    /// <see cref="ExecuteAsync"/> routes output to this callback instead of
    /// the process console. <see cref="QueryAsync"/> swaps in its own
    /// callback for the duration of the call and restores this value on
    /// completion.
    /// </summary>
    Action<string>? OutputCallback { get; set; }

    /// <summary>
    /// True when the underlying worker process / runtime has exited and the
    /// instance can no longer execute commands. Implementations should
    /// return <c>true</c> after <see cref="IAsyncDisposable.DisposeAsync"/>.
    /// </summary>
    bool HasExited { get; }

    /// <summary>
    /// Execute a command and return its exit code. Output lines are routed
    /// through <see cref="OutputCallback"/> when set, otherwise to the
    /// implementation's default sink (console).
    /// </summary>
    /// <exception cref="ObjectDisposedException">If called after dispose.</exception>
    Task<int> ExecuteAsync(string command, CancellationToken ct = default);

    /// <summary>
    /// Execute a command and return captured stdout as a single newline-joined
    /// string. The current <see cref="OutputCallback"/> is saved and restored
    /// across the call so callers may run queries inside an
    /// ExecuteAsync-callback-pumped session without losing their callback.
    /// </summary>
    /// <exception cref="ObjectDisposedException">If called after dispose.</exception>
    Task<string> QueryAsync(string expression, CancellationToken ct = default);
}
