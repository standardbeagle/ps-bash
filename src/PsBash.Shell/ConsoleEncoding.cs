namespace PsBash.Shell;

/// <summary>
/// Pins the launcher's console output to UTF-8 (so non-ASCII command output, e.g. <c>echo $'café'</c>,
/// is emitted as UTF-8 bytes regardless of the inherited system code page) AND to the escaped-byte
/// codec (<see cref="PsBash.Core.RawBytes"/>), so bytes that are not valid UTF-8 (<c>printf '\351'</c>)
/// reach stdout as the single original byte instead of a replacement character.
///
/// Output text arrives at the launcher intact over IPC (HostProtocol frames it as UTF-8 with escaped-byte
/// markers). The final <c>Console.Write</c> in <c>IpcWorker.StreamFrame</c> is the last re-encoding step:
/// on a non-UTF-8 console (common on Windows CI runners) it would mojibake <c>é</c> through the OEM code
/// page, and a default UTF-8 encoder would turn a marker into U+FFFD. <see cref="PsBash.Core.RawBytes.Encoding"/>
/// is UTF-8 (code page 65001) for valid text and writes a marker back as its byte.
/// Dart z0GXccJmhX2H.
/// </summary>
internal static class ConsoleEncoding
{
    /// <summary>
    /// Sets <see cref="Console.OutputEncoding"/> (which also re-creates the
    /// <c>Console.Out</c>/<c>Console.Error</c> writers) to the byte-faithful UTF-8 codec. No-op on
    /// failure — setting the encoding can throw on some redirected/headless setups, and
    /// <see cref="PsBash.Core.Runtime.RawConsole.Install"/> then wraps the standard streams directly.
    /// </summary>
    public static void EnsureUtf8Output() => PsBash.Core.Runtime.RawConsole.Install();
}
