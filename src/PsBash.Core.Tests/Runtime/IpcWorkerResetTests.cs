using System;
using System.IO;
using System.Net.Sockets;
using PsBash.Core.Runtime;
using Xunit;

namespace PsBash.Core.Tests.Runtime;

/// <summary>
/// Unit tests for <see cref="IpcWorker.IsTransportReset"/>, the classifier that
/// gates the self-healing pre-output retry in <see cref="IpcWorker"/>. A mid-
/// command transport RESET (host killed, endpoint stale, connection refused) is
/// recoverable; a timeout or a normal exit is not a reset and must not trip the
/// retry path. Regression guard for the wedged-daemon bug where a reset bubbled
/// straight to exit 125 with no respawn/retry.
/// </summary>
public class IpcWorkerResetTests
{
    [Fact]
    public void IsTransportReset_IOException_IsReset()
        => Assert.True(IpcWorker.IsTransportReset(new IOException("forcibly closed")));

    [Fact]
    public void IsTransportReset_SocketException_IsReset()
        => Assert.True(IpcWorker.IsTransportReset(new SocketException((int)SocketError.ConnectionRefused)));

    [Fact]
    public void IsTransportReset_IOExceptionWrappingSocket_IsReset()
        => Assert.True(IpcWorker.IsTransportReset(
            new IOException("reset", new SocketException((int)SocketError.ConnectionReset))));

    [Fact]
    public void IsTransportReset_OperationCanceled_IsNotReset()
        => Assert.False(IpcWorker.IsTransportReset(new OperationCanceledException()));

    [Fact]
    public void IsTransportReset_Timeout_IsNotReset()
        => Assert.False(IpcWorker.IsTransportReset(new TimeoutException("idle")));

    [Fact]
    public void IsTransportReset_GenericException_IsNotReset()
        => Assert.False(IpcWorker.IsTransportReset(new InvalidOperationException()));
}
