namespace PsBash.Core.Runtime;

/// <summary>
/// Points the process console at the escaped-byte codec (<see cref="RawBytes.Encoding"/>) so every
/// <c>Console.Write</c> / <c>Console.Error.Write</c> writes an escaped-byte marker back as the single
/// original byte (<c>printf '\351'</c> reaches the terminal / pipe as byte E9, not as U+FFFD or C3 A9), and
/// every native child that PowerShell captures is decoded byte-faithfully (PowerShell decodes a native
/// command's stdout with <see cref="Console.OutputEncoding"/>).
/// <para>
/// Valid UTF-8 is unaffected — <see cref="RawBytes.Encoding"/> IS UTF-8 (code page 65001) for every valid
/// sequence — so installing it is a no-op for ordinary text. Called once at process start by the launcher
/// (<c>ps-bash.exe</c>) and the host (<c>ps-bash-host.exe</c>).
/// </para>
/// </summary>
public static class RawConsole
{
    /// <summary>
    /// Install the codec on the console. Best-effort: on a headless / redirected Windows setup
    /// <see cref="Console.OutputEncoding"/> can throw (no console to set the code page on); the standard
    /// streams are then wrapped directly so the write path is still byte-faithful.
    /// </summary>
    public static void Install()
    {
        try
        {
            Console.OutputEncoding = RawBytes.Encoding;
        }
        catch
        {
            try
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), RawBytes.Encoding, 4096) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError(), RawBytes.Encoding, 4096) { AutoFlush = true });
            }
            catch
            {
                // Nothing better than the inherited encoding.
            }
        }
    }
}
