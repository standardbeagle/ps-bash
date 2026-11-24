using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// Retires a host only while it runs no one's command (docs/specs/host-lifecycle-contract.md §Retiring a host).
/// A host runs one command at a time under its execution lease; <see cref="TryRetireIdleAsync"/> takes that lease
/// with a command of its own (<see cref="HoldBody"/>, sent with an environment block so a busy host refuses it with
/// <see cref="HostProtocol.ExecBusyExitCode"/> instead of queueing it), and only once the host has STARTED it asks the
/// host to shut down. The only command the shutdown can interrupt is the hold itself. Every host build that speaks
/// executor slots understands this, so it also retires hosts of the build being replaced.
/// REGRESSION: install-local killed every daemon host, and a new-build launcher retired an older-build host with
/// Shutdown + kill, busy or not — both ended agents' in-flight commands with exit 125.
/// </summary>
public static class HostRetire
{
    public enum Outcome
    {
        /// <summary>The host was idle; it was claimed and told to shut down.</summary>
        Retired,
        /// <summary>The host is running a command; nothing was sent to it but the refused claim.</summary>
        Busy,
        /// <summary>Nothing answered at the endpoint (gone already, or not a host that speaks the protocol).</summary>
        Unreachable,
    }

    /// <summary>The claim: PowerShell the host runs until the claim's connection closes.</summary>
    public const string HoldBody = "Start-Sleep -Seconds 3600";

    /// <summary>How long the claim waits for the host's answer before calling it unreachable.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(10);

    public static async Task<Outcome> TryRetireIdleAsync(Func<IIpcTransport> newTransport, int shutdownDeadlineMs, CancellationToken ct)
    {
        var (claim, refused) = await TryClaimIdleAsync(newTransport, ct).ConfigureAwait(false);
        if (claim is null) return refused;
        await using (claim) await claim.RetireAsync(shutdownDeadlineMs, ct).ConfigureAwait(false);
        return Outcome.Retired;
    }

    /// <summary>
    /// Takes an idle host's execution lease and keeps it until the claim is retired or disposed: meanwhile no one
    /// else's command starts on the host, and a launcher sees it busy (and spills past it) instead of retiring it.
    /// Returns no claim and why (<see cref="Outcome.Busy"/> or <see cref="Outcome.Unreachable"/>) when it was not idle.
    /// </summary>
    public static async Task<(Claim? Claim, Outcome Refused)> TryClaimIdleAsync(Func<IIpcTransport> newTransport, CancellationToken ct)
    {
        var holdTransport = newTransport();
        Claim? claim = null;
        Stream? hold = null;
        CancellationTokenSource? readCts = null;
        Task<int>? response = null;
        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(AnswerTimeout);
                try { hold = await holdTransport.ConnectAsync(connectCts.Token).ConfigureAwait(false); }
                catch (Exception ex) when (IsUnreachable(ex, ct)) { return (null, Outcome.Unreachable); }
            }

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                await HostProtocol.WriteRequestAsync(
                    hold, new Mode.Command(HoldBody, SessionMode.Framed, CurrentEnvironment()), ct).ConfigureAwait(false);
                response = HostProtocol.ReadResponseAsync(hold, (_, _) => { }, () => started.TrySetResult(), readCts.Token);
            }
            catch (Exception ex) when (IsUnreachable(ex, ct)) { return (null, Outcome.Unreachable); }

            var first = await Task.WhenAny(started.Task, response, Task.Delay(AnswerTimeout, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (first == started.Task)
            {
                claim = new Claim(newTransport, holdTransport, hold, readCts, response);   // it owns them now
                return (claim, Outcome.Retired);
            }
            readCts.Cancel();
            if (first == response && response.IsCompletedSuccessfully && response.Result == HostProtocol.ExecBusyExitCode)
                return (null, Outcome.Busy);
            // Answered with something other than STARTED or BUSY, closed, or said nothing in time: not ours to retire.
            return (null, Outcome.Unreachable);
        }
        finally
        {
            if (claim is null)
            {
                readCts?.Cancel();
                if (response is not null) { try { await response.ConfigureAwait(false); } catch { } }
                readCts?.Dispose();
                if (hold is not null) await hold.DisposeAsync().ConfigureAwait(false);
                await holdTransport.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>A host's execution lease, held by the claim command <see cref="HoldBody"/> until disposed.</summary>
    public sealed class Claim : IAsyncDisposable
    {
        private readonly Func<IIpcTransport> _newTransport;
        private readonly IIpcTransport _transport;
        private readonly Stream _hold;
        private readonly CancellationTokenSource _readCts;
        private readonly Task<int> _response;
        private int _disposed;

        internal Claim(Func<IIpcTransport> newTransport, IIpcTransport transport, Stream hold, CancellationTokenSource readCts, Task<int> response)
            => (_newTransport, _transport, _hold, _readCts, _response) = (newTransport, transport, hold, readCts, response);

        /// <summary>
        /// Shuts the host down while the lease is ours, so the only command it can interrupt is the claim, then ends
        /// the claim. A host that went away meanwhile is retired all the same.
        /// </summary>
        public async Task RetireAsync(int shutdownDeadlineMs, CancellationToken ct)
        {
            try
            {
                using var sdCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                sdCts.CancelAfter(AnswerTimeout);
                await using var control = _newTransport();
                using var stream = await control.ConnectAsync(sdCts.Token).ConfigureAwait(false);
                await HostProtocol.WriteRequestAsync(stream, new Mode.Shutdown(shutdownDeadlineMs), sdCts.Token).ConfigureAwait(false);
                await HostProtocol.ReadResponseAsync(stream, _ => { }, sdCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsUnreachable(ex, ct)) { /* it went away on its own */ }
            finally { await DisposeAsync().ConfigureAwait(false); }
        }

        /// <summary>Ends the claim: closing its connection ends the hold, whichever host build this is.</summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _readCts.Cancel();
            try { await _response.ConfigureAwait(false); } catch { }
            _readCts.Dispose();
            await _hold.DisposeAsync().ConfigureAwait(false);
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool IsUnreachable(Exception ex, CancellationToken ct)
        => ex is SocketException or IOException or TimeoutException or UnauthorizedAccessException
           || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    private static IReadOnlyList<KeyValuePair<string, string>> CurrentEnvironment()
    {
        var block = new List<KeyValuePair<string, string>>();
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) block.Add(new(k, v));
        return block;
    }
}
