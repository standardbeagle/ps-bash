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
        await using var holdTransport = newTransport();
        Stream hold;
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(AnswerTimeout);
            try { hold = await holdTransport.ConnectAsync(connectCts.Token).ConfigureAwait(false); }
            catch (Exception ex) when (IsUnreachable(ex, ct)) { return Outcome.Unreachable; }
        }

        await using (hold)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task<int> response;
            try
            {
                await HostProtocol.WriteRequestAsync(
                    hold, new Mode.Command(HoldBody, SessionMode.Framed, CurrentEnvironment()), ct).ConfigureAwait(false);
                response = HostProtocol.ReadResponseAsync(hold, (_, _) => { }, () => started.TrySetResult(), readCts.Token);
            }
            catch (Exception ex) when (IsUnreachable(ex, ct)) { return Outcome.Unreachable; }

            var first = await Task.WhenAny(started.Task, response, Task.Delay(AnswerTimeout, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (first != started.Task)
            {
                readCts.Cancel();
                if (first == response && response.IsCompletedSuccessfully && response.Result == HostProtocol.ExecBusyExitCode)
                    return Outcome.Busy;
                // Answered with something other than STARTED or BUSY, closed, or said nothing in time: not ours to retire.
                try { await response.ConfigureAwait(false); } catch { }
                return Outcome.Unreachable;
            }

            // The lease is ours, so no one else's command can start on this host now. Shut it down while holding it.
            try
            {
                using var sdCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                sdCts.CancelAfter(AnswerTimeout);
                await using var control = newTransport();
                using var stream = await control.ConnectAsync(sdCts.Token).ConfigureAwait(false);
                await HostProtocol.WriteRequestAsync(stream, new Mode.Shutdown(shutdownDeadlineMs), sdCts.Token).ConfigureAwait(false);
                await HostProtocol.ReadResponseAsync(stream, _ => { }, sdCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsUnreachable(ex, ct)) { /* it went away on its own: retired all the same */ }
            finally
            {
                readCts.Cancel();   // closing the claim's connection ends the hold, whichever host build this is
                try { await response.ConfigureAwait(false); } catch { }
            }
            return Outcome.Retired;
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
