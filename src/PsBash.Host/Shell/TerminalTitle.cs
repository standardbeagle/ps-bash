using System.Text;

namespace PsBash.Host.Shell;

/// <summary>
/// Terminal tab/window title (OSC 0) for the interactive shell: the running command while it
/// executes, <c>ps-bash: &lt;dir&gt;</c> at the prompt. Opt out with <c>PSBASH_NO_TITLE=1</c>.
/// </summary>
internal static class TerminalTitle
{
    private const int MaxLength = 80;

    /// <summary>Title text with control characters collapsed to spaces and length bounded.</summary>
    internal static string Sanitize(string text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, MaxLength + 1));
        foreach (var c in text)
        {
            if (sb.Length >= MaxLength) { sb.Length = MaxLength - 1; sb.Append('…'); break; }
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString().Trim();
    }

    internal static string Running(string command) => Sanitize("ps-bash: " + command);

    internal static string Idle(string cwd)
    {
        var name = Path.GetFileName(cwd.TrimEnd('\\', '/'));
        return Sanitize("ps-bash: " + (name.Length > 0 ? name : cwd));
    }

    /// <summary>The OSC 0 sequence setting both icon name and window title.</summary>
    internal static string Sequence(string title) => "\u001b]0;" + title + "\u0007";

    internal static void Set(string title)
    {
        if (Console.IsOutputRedirected || Environment.GetEnvironmentVariable("PSBASH_NO_TITLE") == "1") return;
        try { Console.Out.Write(Sequence(title)); Console.Out.Flush(); }
        catch (IOException) { /* title is cosmetic */ }
    }
}
