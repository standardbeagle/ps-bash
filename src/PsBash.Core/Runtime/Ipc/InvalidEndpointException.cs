namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// A malformed endpoint setting (<c>PSBASH_IPC_ENDPOINT</c> or <c>--ipc-endpoint</c>): a configuration error the
/// launcher reports as one line with exit 2, not an unhandled exception.
/// </summary>
public sealed class InvalidEndpointException(string message) : ArgumentException(message);
