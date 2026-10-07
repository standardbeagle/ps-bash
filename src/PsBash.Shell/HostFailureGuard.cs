using PsBash.Core.Runtime;

namespace PsBash.Shell;

/// <summary>
/// Maps host infrastructure failures (host won't start, hangs, the IPC connection breaks or the host
/// process dies) to a one-line <c>ps-bash: …</c> diagnostic and a defined exit code — never an
/// unhandled-exception stack trace. Every path that runs a command on the host goes through
/// <see cref="RunAsync"/>: the <c>-c</c> path had this mapping inline while the <c>.sh</c> and
/// <c>.ps1</c> script paths did not, so a host crash under a script dumped
/// "Unhandled exception. System.IO.IOException" with a CLR crash code instead of exit 125.
/// </summary>
internal static class HostFailureGuard
{
    public static async Task<int> RunAsync(Func<Task<int>> run, TextWriter? stderr = null)
    {
        stderr ??= Console.Error;
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            // Mirror GNU `timeout`'s exit code so callers can detect the condition.
            stderr.WriteLine(ex.Message.StartsWith("ps-bash:", StringComparison.Ordinal) ? ex.Message : $"ps-bash: {ex.Message}");
            return 124;
        }
        catch (HostUnavailableException ex)
        {
            stderr.WriteLine($"ps-bash: {ex.Message}");
            return 125;
        }
        catch (Exception ex) when (ex is IOException
                                      or System.Net.Sockets.SocketException
                                      or OperationCanceledException)
        {
            stderr.WriteLine($"ps-bash: host communication failed: {ex.Message}");
            return 125;
        }
    }
}
