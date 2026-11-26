using System.Text;

namespace PsBash.Core.Runtime.Processes;

/// <summary>
/// How a Windows batch file (<c>.cmd</c> / <c>.bat</c>: <c>npm</c>, <c>npx</c>, <c>yarn</c>, <c>mvn</c>, ...) is started:
/// through <c>cmd.exe</c> with a command line built here, so every argument reaches the script as the literal text the
/// shell gave it. Spec: <c>docs/specs/program-files.md</c> §Batch files.
/// <para>
/// <b>Why.</b> A batch file always runs in <c>cmd.exe</c>, which parses its command line again: <c>&amp;</c>, <c>|</c>,
/// <c>&lt;</c>, <c>&gt;</c> outside double quotes start another command, <c>^</c> escapes, and <c>%NAME%</c> expands
/// everywhere. Neither PowerShell's launcher nor the C runtime quoting of <c>CreateProcess</c> /
/// <c>ProcessStartInfo.ArgumentList</c> accounts for that (CVE-2024-24576, "BatBadBut"). Measured with PowerShell
/// 7.6: <c>tool.cmd 'a&amp;b'</c> ran <c>b</c>, <c>'a|b'</c> piped into <c>b</c>, <c>'a^b'</c> arrived as <c>ab</c>,
/// <c>'%PATH%'</c> arrived expanded, and an empty argument was dropped.
/// </para>
/// <para>
/// The escaping is the one Rust's standard library adopted for that CVE: an argument is double-quoted unless every
/// character is known to be inert to <c>cmd.exe</c>; inside quotes a <c>"</c> is doubled (with the backslashes before it
/// doubled too) and a <c>%</c> is followed by the zero-length expansion <c>%cd:~,%</c>, which stops a <c>%NAME%</c>
/// pair from being read as a variable. A line break cannot be passed at all (<c>cmd.exe</c> ends the command there),
/// so it is refused rather than truncated. <c>/d</c> skips the AutoRun registry commands, <c>/v:OFF</c> keeps
/// <c>!NAME!</c> literal, and <c>/e:ON</c> is what makes the <c>%cd:~,%</c> substring work.
/// </para>
/// </summary>
public static class BatchFile
{
    /// <summary>Characters that never need quoting for <c>cmd.exe</c>, besides ASCII letters and digits.</summary>
    private const string Inert = @"#$*+-./:?@\_";

    /// <summary>True on Windows for a <c>.cmd</c> / <c>.bat</c> path: <c>cmd.exe</c> runs it (<see cref="Arguments"/>).</summary>
    public static bool Is(string path) =>
        OperatingSystem.IsWindows()
        && (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The command line that follows <c>cmd.exe</c> to run <paramref name="script"/> with <paramref name="args"/>, each
    /// arriving as exactly its text (<c>%1</c> holds it quoted when it needed quotes, as a batch file expects).
    /// Throws <see cref="ArgumentException"/> for an argument holding a line break or NUL, which <c>cmd.exe</c> cannot
    /// pass on, and for a script path holding a double quote (no Windows file name has one).
    /// </summary>
    public static string Arguments(string script, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(args);
        if (script.Length == 0 || script.Contains('"') || script.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
            throw new ArgumentException($"not a batch file path: {script}", nameof(script));

        // The outer pair of quotes is cmd's own: with more than two quotes on the line, /c drops the first and the last
        // and runs the rest as typed.
        var sb = new StringBuilder("/e:ON /v:OFF /d /c \"\"");
        AppendEscaped(sb, script);
        sb.Append('"');
        foreach (var arg in args)
        {
            ArgumentNullException.ThrowIfNull(arg, nameof(args));
            if (arg.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
                throw new ArgumentException("an argument with a line break cannot be passed to a batch file");
            sb.Append(' ');
            AppendArgument(sb, arg);
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static void AppendArgument(StringBuilder sb, string arg)
    {
        // Empty: quoted, or it disappears. Ending in a backslash: quoted, so a script's "%~1" cannot have its closing
        // quote escaped by it.
        bool quote = arg.Length == 0 || arg[^1] == '\\';
        foreach (char c in arg)
        {
            if (char.IsControl(c) || (c < 0x80 && !char.IsAsciiLetterOrDigit(c) && Inert.IndexOf(c) < 0))
            {
                quote = true;
                break;
            }
        }
        if (!quote)
        {
            sb.Append(arg);
            return;
        }
        sb.Append('"');
        int backslashes = AppendEscaped(sb, arg);
        sb.Append('\\', backslashes);
        sb.Append('"');
    }

    /// <summary>
    /// Appends <paramref name="text"/> for use inside double quotes: <c>"</c> doubled (and the backslashes before it),
    /// <c>%</c> defused. Returns the count of trailing backslashes, which the caller doubles before a closing quote.
    /// </summary>
    private static int AppendEscaped(StringBuilder sb, string text)
    {
        int backslashes = 0;
        foreach (char c in text)
        {
            if (c == '\\')
            {
                backslashes++;
                sb.Append(c);
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes);
                sb.Append('"');
            }
            else if (c == '%')
            {
                sb.Append("%%cd:~,");
            }
            backslashes = 0;
            sb.Append(c);
        }
        return backslashes;
    }
}
