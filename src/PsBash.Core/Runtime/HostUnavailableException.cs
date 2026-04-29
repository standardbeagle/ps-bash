namespace PsBash.Core.Runtime;

/// <summary>
/// Raised by <see cref="IpcWorker"/> when the launcher cannot reach a running
/// <c>ps-bash-host</c> and cannot spawn a new one. Distinct from generic
/// <see cref="InvalidOperationException"/> so the soft-fallback decision logic
/// in T07 (launcher) can pattern-match this exception type and decide whether
/// to fall back to the in-process <see cref="PwshWorker"/>.
/// </summary>
/// <remarks>
/// Thrown for these specific situations (T06 scope):
/// <list type="bullet">
///   <item><description>The host binary (<c>ps-bash-host</c>) is not present at the resolved location.</description></item>
///   <item><description>A stale lock file was found (no host listening) and the binary needed to spawn a new one is missing.</description></item>
///   <item><description>The spawned host process exited before the lock file appeared.</description></item>
///   <item><description>The 5-second startup poll elapsed without the host writing a lock file.</description></item>
/// </list>
/// Connection failures against a live lock-file endpoint surface as
/// <see cref="System.Net.Sockets.SocketException"/> from the underlying
/// transport — those are not wrapped, on the principle that the launcher's
/// fallback policy treats "host crashed mid-call" differently from "host never
/// existed".
/// </remarks>
public sealed class HostUnavailableException : Exception
{
    public HostUnavailableException(string message) : base(message) { }
    public HostUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}
