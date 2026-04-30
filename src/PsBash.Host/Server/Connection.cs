using PsBash.Core.Runtime.Ipc;
using PsBash.Host.Runtime;

namespace PsBash.Host.Server;

/// <summary>
/// Handles a single accepted connection: reads one request, executes it
/// against the shared <see cref="SdkWorker"/>, and streams the response.
/// </summary>
internal sealed class Connection
{
    // Prepended to every command to reset globals that accumulate across
    // invocations on the shared runspace.  'set -e' emits __BashErrexit = true;
    // positional params are set by BuildPositionalPreamble — both must be cleared
    // between invocations so state from one -c call does not affect the next.
    private const string PerInvocationReset =
        "$global:__BashErrexit = $false; " +
        "$ErrorActionPreference = 'Continue'; " +
        "$global:BashPositional = $null; " +
        "$global:BashPositional0 = $null; ";

    private readonly Stream _stream;
    private readonly SdkWorker _worker;

    internal Connection(Stream stream, SdkWorker worker)
    {
        _stream = stream;
        _worker = worker;
    }

    internal async Task HandleAsync(CancellationToken ct)
    {
        Mode mode;
        try
        {
            mode = await HostProtocol.ReadRequestAsync(_stream, ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException)
        {
            try
            {
                await HostProtocol.WriteResponseLineAsync(_stream, $"error: {ex.Message}", ct);
                await HostProtocol.WriteExitAsync(_stream, 2, ct);
            }
            catch { }
            return;
        }

        string? command = mode switch
        {
            Mode.Command c => c.Body,
            Mode.Stdin s => s.Body,
            Mode.Script sc => sc.Body,
            Mode.Interactive => null,
            _ => null
        };

        if (command == null)
        {
            await HostProtocol.WriteExitAsync(_stream, 0, ct);
            return;
        }

        // Reset per-invocation state so globals set by one -c or script run
        // (e.g. set -e → $global:__BashErrexit, positional params) do not
        // leak into the next invocation on the shared SdkWorker runspace.
        command = PerInvocationReset + command;

        void WriteOutput(string line)
        {
            // Trim trailing newlines: PS BashObjects include a trailing \n in BashText.
            // WriteResponseLineAsync adds its own \n, so we must strip to avoid double-newlines.
            var trimmed = line.TrimEnd('\n', '\r');
            HostProtocol.WriteResponseLineAsync(_stream, trimmed, ct).GetAwaiter().GetResult();
        }

        var exitCode = await _worker.ExecuteWithOutputAsync(command, WriteOutput, ct);
        await HostProtocol.WriteExitAsync(_stream, exitCode, ct);
    }
}
