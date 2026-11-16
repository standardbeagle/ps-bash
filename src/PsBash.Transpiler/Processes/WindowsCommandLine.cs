using System.Text;

namespace PsBash.Core.Runtime.Processes;

/// <summary>
/// The Windows command line for an argv: <c>CommandLineToArgvW</c> splits it back into exactly the program followed
/// by its arguments - the quoting rules <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> applies.
/// </summary>
public static class WindowsCommandLine
{
    /// <summary>Quote <paramref name="exe"/> and each of <paramref name="args"/>, space-separated.</summary>
    public static string Build(string exe, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        AppendQuoted(sb, exe);
        for (int i = 0; i < args.Count; i++)
        {
            sb.Append(' ');
            AppendQuoted(sb, args[i]);
        }
        return sb.ToString();
    }

    /// <summary>Append one argument, quoted only when it needs it (empty, or holding a space, tab or quote).</summary>
    public static void AppendQuoted(StringBuilder sb, string arg)
    {
        if (arg.Length != 0 && arg.AsSpan().IndexOfAny(" \t\"") < 0) { sb.Append(arg); return; }
        sb.Append('"');
        int backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }

    /// <summary>
    /// Split a command-line tail the way <see cref="System.Diagnostics.ProcessStartInfo.Arguments"/> is split on
    /// Unix: whitespace separates, double quotes group, a backslash escapes a quote or a backslash before one.
    /// </summary>
    public static List<string> Split(string arguments)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false, have = false;
        for (int i = 0; i < arguments.Length; i++)
        {
            char c = arguments[i];
            if (c == '\\')
            {
                int run = 1;
                while (i + run < arguments.Length && arguments[i + run] == '\\') run++;
                bool quoteNext = i + run < arguments.Length && arguments[i + run] == '"';
                if (quoteNext)
                {
                    current.Append('\\', run / 2);
                    i += run - 1;
                    if (run % 2 == 1) { current.Append('"'); i++; }
                }
                else
                {
                    current.Append('\\', run);
                    i += run - 1;
                }
                have = true;
            }
            else if (c == '"') { inQuotes = !inQuotes; have = true; }
            else if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (have) { result.Add(current.ToString()); current.Clear(); have = false; }
            }
            else { current.Append(c); have = true; }
        }
        if (have) result.Add(current.ToString());
        return result;
    }
}
