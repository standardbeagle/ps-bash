using System.Text;

namespace PsBash.Shell;

/// <summary>
/// Forces the launcher's console output to UTF-8 so non-ASCII command output
/// (e.g. <c>echo $'café'</c>) is emitted as UTF-8 bytes regardless of the
/// inherited system code page.
///
/// Output text arrives at the launcher intact over IPC (HostProtocol frames it
/// as UTF-8, no BOM). The only lossy step is the final <c>Console.Write</c> in
/// <c>IpcWorker.SendRequestAsync</c>: on a non-UTF-8 console — common on Windows
/// CI runners and default consoles — it re-encodes that text through the OEM
/// code page, mojibaking it (<c>café</c> → <c>cafΘ</c>, the <c>é</c> U+00E9
/// collapsing to the single byte 0xE9 = CP437/CP850). Pinning the output
/// encoding to UTF-8 makes <c>Console.Write</c> emit the original bytes.
/// Dart z0GXccJmhX2H.
/// </summary>
internal static class ConsoleEncoding
{
    /// <summary>
    /// Sets <see cref="Console.OutputEncoding"/> (which also re-creates the
    /// <c>Console.Out</c>/<c>Console.Error</c> writers) to UTF-8 without a BOM,
    /// unless it is already UTF-8. No-op on failure — setting the encoding can
    /// throw on some redirected/headless setups, and there is nothing better to
    /// fall back to than the inherited default.
    /// </summary>
    public static void EnsureUtf8Output()
    {
        try
        {
            if (Console.OutputEncoding.CodePage != Encoding.UTF8.CodePage)
            {
                Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }
        }
        catch
        {
            // Best-effort: leave the inherited encoding in place.
        }
    }
}
