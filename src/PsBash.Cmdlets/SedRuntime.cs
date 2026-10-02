using System.Text;
using PsBash.Core;
using static PsBash.Cmdlets.InvokeBashSedCommand;

namespace PsBash.Cmdlets;

/// <summary>
/// Everything a <see cref="SedEngine"/> needs from the outside world that is not "records in, records out":
/// the hold space (shared by every engine of one run, so <c>-s</c>/<c>-i</c> keep it across files like GNU),
/// the current input file name (<c>F</c>, <c>--debug</c>), the file/command hooks behind
/// <c>r R w W e</c> and <c>s///w</c>/<c>s///e</c>, the <c>l</c> wrap width, the <c>q</c>/<c>Q</c> exit status, and
/// the <c>--debug</c> sink. A null runtime (the fused lane) means "legacy commands only, nothing external".
/// </summary>
internal sealed class SedRuntime
{
    /// <summary>Non-null = <c>--debug</c>: receives one logical output line (no terminator) per call.</summary>
    public Action<string>? Debug;

    /// <summary>GNU's global <c>block_level</c>: it survives a cycle that leaves a block early (<c>d</c>, <c>b</c>), exactly as upstream.</summary>
    public int BlockLevel;

    public string Hold = "";

    /// <summary>The input being read, as typed ("-" for stdin).</summary>
    public string FileName = "-";

    /// <summary>The <c>-l N</c> / default 70 wrap width of the <c>l</c> command (0 = never wrap).</summary>
    public int LineLength = 70;

    /// <summary>Exit status requested by <c>q</c>/<c>Q</c>.</summary>
    public int ExitCode;

    /// <summary><c>r FILE</c>: the whole file, or null when it cannot be read (silently ignored by GNU).</summary>
    public Func<string, string?>? ReadFile;

    /// <summary><c>R FILE</c>: the next line of FILE (kept open between calls), or null at end/unreadable.</summary>
    public Func<string, string?>? ReadLine;

    /// <summary><c>w FILE</c>: appends one line (no terminator) to FILE (opened/truncated at program start).</summary>
    public Action<string, string>? WriteFile;

    /// <summary><c>e</c>: runs a shell command line through ps-bash and returns its stdout.</summary>
    public Func<string, string>? RunCommand;
}

/// <summary>GNU sed 4.9 <c>--debug</c> text: the canonical program listing and the per-command annotations.</summary>
internal static class SedDebugFormat
{
    /// <summary>GNU <c>debug_print_char</c> over the UTF-8 bytes of <paramref name="s"/>.</summary>
    internal static string EscapeText(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (byte b in RawBytes.GetBytes(s))
        {
            if (b >= 0x20 && b < 0x7f && b != (byte)'\\') { sb.Append((char)b); continue; }
            sb.Append('\\');
            switch (b)
            {
                case 7: sb.Append('a'); break;
                case 12: sb.Append('f'); break;
                case 13: sb.Append('r'); break;
                case 9: sb.Append('t'); break;
                case 11: sb.Append('v'); break;
                case 10: sb.Append('n'); break;
                case (byte)'\\': sb.Append('\\'); break;
                default: sb.Append('o').Append(Convert.ToString(b, 8).PadLeft(3, '0')); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// A regex as GNU stores it: <c>\n</c> and <c>\t</c> are already real control characters, every other
    /// backslash survives (and is then doubled by <see cref="EscapeText"/>).
    /// </summary>
    private static string RegexSource(string typed)
    {
        var sb = new StringBuilder(typed.Length);
        for (int i = 0; i < typed.Length; i++)
        {
            if (typed[i] == '\\' && i + 1 < typed.Length)
            {
                char n = typed[i + 1];
                if (n == 'n') { sb.Append('\n'); i++; continue; }
                if (n == 't') { sb.Append('\t'); i++; continue; }
                sb.Append('\\').Append(n); i++;
                continue;
            }
            sb.Append(typed[i]);
        }
        return sb.ToString();
    }

    /// <summary>The replacement as GNU's compiled form prints it: <c>\1</c> and <c>&amp;</c> stay, escapes become characters.</summary>
    private static string ReplacementSource(string typed)
    {
        var sb = new StringBuilder(typed.Length);
        for (int i = 0; i < typed.Length; i++)
        {
            char c = typed[i];
            if (c == '\\' && i + 1 < typed.Length)
            {
                char n = typed[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'a': sb.Append('\a'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'v': sb.Append('\v'); break;
                    case >= '0' and <= '9': sb.Append('\\').Append(n); break;
                    default: sb.Append(n); break;   // \& \\ \/ ... : the escaped character itself
                }
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Addr(SedAddress a)
    {
        switch (a.Type)
        {
            case AddressType.Regex: return "/" + EscapeText(RegexSource(a.Pattern!)) + "/";
            case AddressType.Line: return a.Line.ToString();
            case AddressType.Last: return "$";
            case AddressType.Step: return a.Start + "~" + a.Step;
            case AddressType.RangeNum:
                return a.Start + "," + (a.End == int.MaxValue ? "$" : a.End.ToString());
            case AddressType.RangeRegex:
                return "/" + EscapeText(RegexSource(a.StartPattern!)) + "/,/" + EscapeText(RegexSource(a.EndPattern!)) + "/";
            case AddressType.RangeNumToRegex:
                return a.Start + ",/" + EscapeText(RegexSource(a.EndPattern!)) + "/";
        }
        return "";
    }

    /// <summary>One command as <c>debug_print_command</c> prints it (without indentation and without the final newline).</summary>
    internal static string Command(SedCommand c)
    {
        var sb = new StringBuilder();
        if (c.Address != null) sb.Append(Addr(c.Address));
        if (c.Negate) sb.Append('!');
        if (c.Address != null) sb.Append(' ');
        sb.Append(c.Type);
        switch (c.Type)
        {
            case ':': sb.Append(c.Label); break;
            case 'a': case 'i': case 'c':
                sb.Append('\\').Append(c.Text).Append('\n');
                break;
            case 'b': case 't': case 'T':
                if (c.Label != null) sb.Append(' ').Append(c.Label);
                break;
            case 'e':
                sb.Append(' ').Append(c.Command);
                if (!string.IsNullOrEmpty(c.Command)) sb.Append('\n');
                break;
            case 'l': case 'q': case 'Q':
                if (c.IntArg != -1) sb.Append(' ').Append(c.IntArg);
                break;
            case 'r': case 'R': sb.Append(' ').Append(c.FileName); break;
            case 'w': case 'W': sb.Append(c.FileName); break;
            case 's':
                sb.Append('/').Append(EscapeText(RegexSource(c.SrcRegex ?? "")))
                  .Append('/').Append(ReplacementSource(c.SrcReplacement ?? "")).Append('/');
                if (c.IgnoreCase) sb.Append('i');
                if (c.Multiline) sb.Append('m');
                if (c.Global) sb.Append('g');
                if (c.Eval) sb.Append('e');
                if (c.PrintOnSub) sb.Append('p');
                if (c.Nth > 0) sb.Append(c.Nth);
                if (c.FileName != null) sb.Append('w').Append(c.FileName);
                break;
            case 'y': sb.Append('/').Append(c.Source).Append('/').Append(c.Dest).Append('/'); break;
        }
        return sb.ToString();
    }

    /// <summary>GNU prints the program once, before any input: every command indented by its block depth.</summary>
    internal static void PrintProgram(List<SedCommand> program, SedRuntime rt)
    {
        var emit = rt.Debug!;
        emit("SED PROGRAM:");
        int level = 1;
        foreach (var c in program)
        {
            if (c.Type == '}') level--;
            Lines(new string(' ', 2 * level) + Command(c), emit);
            if (c.Type == '{') level++;
        }
        rt.BlockLevel = 0;
    }

    /// <summary>The <c>COMMAND:</c> line of one command about to run (indent = the run-time block level).</summary>
    internal static void PrintCommandLine(SedCommand c, SedRuntime rt)
    {
        if (c.Type == '}') rt.BlockLevel--;
        Lines("COMMAND: " + new string(' ', 2 * rt.BlockLevel) + Command(c), rt.Debug!);
        if (c.Type == '{') rt.BlockLevel++;
    }

    /// <summary>Emits <paramref name="text"/> as records: a trailing newline is the terminator of the last one, as with puts().</summary>
    private static void Lines(string text, Action<string> emit)
    {
        foreach (var l in text.Split('\n')) emit(l);
    }

    /// <summary>
    /// <c>l</c>: the pattern space as unambiguous text (<c>\n \t \\ \ooo</c> over the UTF-8 bytes), wrapped so
    /// every output line is at most <paramref name="lineLen"/> characters including its trailing <c>\</c>;
    /// 0 never wraps. Ends with <c>$</c>. Escapes are never split.
    /// </summary>
    internal static string List(string ps, int lineLen)
    {
        var sb = new StringBuilder();
        int width = 0;
        foreach (byte b in RawBytes.GetBytes(ps))
        {
            string o;
            if (b >= 0x20 && b < 0x7f && b != (byte)'\\') o = ((char)b).ToString();
            else
            {
                switch (b)
                {
                    case 7: o = "\\a"; break;
                    case 8: o = "\\b"; break;
                    case 12: o = "\\f"; break;
                    case 10: o = "\\n"; break;
                    case 13: o = "\\r"; break;
                    case 9: o = "\\t"; break;
                    case 11: o = "\\v"; break;
                    case (byte)'\\': o = "\\\\"; break;
                    default: o = "\\" + Convert.ToString(b, 8).PadLeft(3, '0'); break;
                }
            }
            if (lineLen > 0 && width + o.Length > lineLen - 1)
            {
                sb.Append("\\\n");
                width = 0;
            }
            sb.Append(o);
            width += o.Length;
        }
        sb.Append('$');
        return sb.ToString();
    }
}
