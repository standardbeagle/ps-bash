using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The lines a command would read from its standard input, for the few file mutators that ask a
/// question (<c>rm -i</c>, <c>rm -I</c>). In order: the records piped into the cmdlet (what
/// <c>echo y | rm -i f</c> transpiles to), then a brace group's shared stdin queue
/// (<c>$global:__BashStdIn</c>), then — only for a real interactive console — the keyboard.
/// A redirected or closed stdin with nothing queued is end-of-file: <see cref="ReadLine"/> returns
/// null, which GNU treats as "no" (it never blocks a test host whose stdin is open but empty).
/// </summary>
internal sealed class StdinLineSource
{
    /// <summary>Host-config switch: never read the keyboard (see <see cref="ReadLine"/>).</summary>
    internal const string NoTtyEnvVar = "PSBASH_NO_TTY";

    private static bool NoTty => BashRuntime.IsHostConfigTruthy(NoTtyEnvVar);

    private readonly PSCmdlet _cmdlet;
    private readonly Queue<string> _piped = new();

    public StdinLineSource(PSCmdlet cmdlet, IEnumerable<PSObject> pipeline)
    {
        _cmdlet = cmdlet;
        foreach (var item in pipeline)
        {
            var text = (BashRuntime.GetBashText(item) ?? "").Replace("\r\n", "\n");
            // A record is one line; text that already carries newlines contributes one line each.
            if (text.EndsWith('\n')) text = text.Substring(0, text.Length - 1);
            foreach (var line in text.Split('\n')) _piped.Enqueue(line);
        }
    }

    /// <summary>The next line of standard input without its terminator, or null at end of input.</summary>
    public string? ReadLine()
    {
        if (_piped.Count > 0) return _piped.Dequeue();

        if (SharedStdin.TryDequeueLine(_cmdlet.SessionState, out var shared))
            return shared;

        // Only a real console can produce an answer; a redirected stdin that nothing fed is EOF.
        // PSBASH_NO_TTY forces EOF even on a console: a test host started from a terminal has console
        // stdin, and an unanswered prompt would otherwise block on the keyboard (Cmdlets.Tests sets it).
        if (NoTty || Console.IsInputRedirected) return null;
        try { return Console.In.ReadLine(); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException) { return null; }
    }

    /// <summary>GNU <c>rpmatch</c> in the C locale: an answer is "yes" iff it starts with <c>y</c> or <c>Y</c>.</summary>
    public static bool IsYes(string? answer) => !string.IsNullOrEmpty(answer) && (answer[0] == 'y' || answer[0] == 'Y');
}
