using System.Globalization;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// The shell's history list as a NON-interactive bash keeps it: empty at start, nothing recorded automatically
/// (<c>set -o history</c> is off), entries only from <c>history -s</c> / <c>history -r</c> / <c>fc -s</c>.
/// Lives in <c>$global:BashHistory</c> (one per shell; a subshell gets a copy via <see cref="BashShellState"/>).
/// <see cref="FileMark"/> = how many entries are already in the history file (<c>history -a</c> appends the rest).
/// The interactive shell keeps its own persisted history (<c>PsBash.Host.Shell.HistoryBuiltin</c>).
/// </summary>
public sealed class BashHistoryList : List<string>
{
    public int FileMark { get; set; }

    public BashHistoryList Clone()
    {
        var c = new BashHistoryList { FileMark = FileMark };
        c.AddRange(this);
        return c;
    }
}

/// <summary>
/// Pure cores of the <c>history</c> and <c>fc</c> builtins (bash 5.2 builtins/history.def, fc.def), over a
/// <see cref="BashHistoryList"/> and an <see cref="Io"/> seam, so every rule is testable without a runspace.
/// </summary>
public static class BashHistoryBuiltins
{
    /// <summary>Output seam. <c>ReadFile</c> returns null when the file cannot be read; <c>WriteFile</c>(path, lines, append) false on failure.</summary>
    public sealed record Io(
        Action<string> Out,
        Action<string> Err,
        Func<string, IReadOnlyList<string>?> ReadFile,
        Func<string, IEnumerable<string>, bool, bool> WriteFile,
        string? HistFile);

    public const string HistoryUsage =
        "history: usage: history [-c] [-d offset] [n] or history -anrw [filename] or history -ps arg [arg...]";

    public const string FcUsage = "fc: usage: fc [-e ename] [-lnr] [first] [last] or fc -s [pat=rep] [command]";

    /// <summary><c>history [-c] [-d offset] [n]</c> / <c>history -anrw [file]</c> / <c>history -ps arg...</c>. Returns the status.</summary>
    public static int History(BashHistoryList h, IReadOnlyList<string> args, Io io)
    {
        bool a = false, c = false, n = false, p = false, r = false, s = false, w = false;
        string? deleteArg = null;
        int i = 0;
        for (; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--") { i++; break; }
            if (arg.Length < 2 || arg[0] != '-') break;
            for (int k = 1; k < arg.Length; k++)
            {
                switch (arg[k])
                {
                    case 'a': a = true; break;
                    case 'c': c = true; break;
                    case 'n': n = true; break;
                    case 'p': p = true; break;
                    case 'r': r = true; break;
                    case 's': s = true; break;
                    case 'w': w = true; break;
                    case 'd':
                        if (k + 1 < arg.Length) deleteArg = arg[(k + 1)..];
                        else if (i + 1 < args.Count) deleteArg = args[++i];
                        else
                        {
                            io.Err("history: -d: option requires an argument");
                            io.Err(HistoryUsage);
                            return 2;
                        }
                        k = arg.Length;
                        break;
                    default:
                        io.Err($"history: -{arg[k]}: invalid option");
                        io.Err(HistoryUsage);
                        return 2;
                }
            }
        }
        var list = args.Skip(i).ToList();

        if ((a ? 1 : 0) + (n ? 1 : 0) + (r ? 1 : 0) + (w ? 1 : 0) > 1)
        {
            io.Err("history: cannot use more than one of -anrw");
            return 1;
        }
        if (c)
        {
            h.Clear();
            h.FileMark = 0;
            if (list.Count == 0) return 0;
        }
        if (s)
        {
            if (list.Count > 0) h.Add(string.Join(' ', list));
            return 0;
        }
        if (p) return ExpandAndPrint(h, list, io);
        if (deleteArg is not null) return Delete(h, deleteArg, io);
        if (!(a || r || n || w || c)) return Display(h, list, io);

        string? file = list.Count > 0 ? list[0] : io.HistFile;
        if (string.IsNullOrEmpty(file)) return r ? 1 : 0;
        if (a)
        {
            if (!io.WriteFile(file, h.Skip(h.FileMark), true)) return 1;
            h.FileMark = h.Count;
            return 0;
        }
        if (w)
        {
            if (!io.WriteFile(file, h, false)) return 1;
            h.FileMark = h.Count;
            return 0;
        }
        if (r || n)
        {
            var lines = io.ReadFile(file);
            if (lines is null) return 1;
            // -n: only the lines not read yet (the file grew since the last read / write).
            h.AddRange(r ? lines : lines.Skip(h.FileMark));
            h.FileMark = h.Count;
        }
        return 0;
    }

    private static int Display(BashHistoryList h, List<string> list, Io io)
    {
        long limit = h.Count;
        if (list.Count > 0)
        {
            if (!TryLegalNumber(list[0], out limit))
            {
                io.Err($"history: {list[0]}: numeric argument required");
                return 1;
            }
            if (list.Count > 1)
            {
                io.Err("history: too many arguments");
                return 1;
            }
            if (limit < 0) limit = -limit;
        }
        int start = (int)Math.Max(0, h.Count - Math.Min(limit, h.Count));
        for (int k = start; k < h.Count; k++)
            io.Out(string.Format(CultureInfo.InvariantCulture, "{0,5}  {1}", k + 1, h[k]));
        return 0;
    }

    private static int Delete(BashHistoryList h, string arg, Io io)
    {
        // A range `start-end` (each may be negative: `-3--1`); the separator is a '-' that is not the first char.
        int dash = arg.IndexOf('-', arg.StartsWith('-') ? 1 : 0);
        if (dash > 0)
        {
            if (!TryLegalNumber(arg[..dash], out long s0) || !TryLegalNumber(arg[(dash + 1)..], out long e0)
                || !TryHistIndex(h, s0, out int si) || !TryHistIndex(h, e0, out int ei) || ei < si)
                return OutOfRange(arg, io);
            h.RemoveRange(si, ei - si + 1);
            h.FileMark = Math.Min(h.FileMark, h.Count);
            return 0;
        }
        if (!TryLegalNumber(arg, out long off) || !TryHistIndex(h, off, out int idx)) return OutOfRange(arg, io);
        h.RemoveAt(idx);
        if (idx < h.FileMark) h.FileMark--;
        return 0;
    }

    /// <summary>1-based position, or negative = counted back from the end (-1 = the last entry), to a 0-based index.</summary>
    private static bool TryHistIndex(BashHistoryList h, long n, out int index)
    {
        long ix = n < 0 ? h.Count + n : n - 1;
        index = (int)Math.Clamp(ix, int.MinValue, int.MaxValue);
        return ix >= 0 && ix < h.Count;
    }

    private static int OutOfRange(string arg, Io io)
    {
        io.Err($"history: {arg}: history position out of range");
        return 1;
    }

    private static int ExpandAndPrint(BashHistoryList h, List<string> list, Io io)
    {
        foreach (var word in list)
        {
            var expanded = ExpandHistory(h, word);
            if (expanded is null)
            {
                io.Err($"history: {word}: history expansion failed");
                return 1;
            }
            io.Out(expanded);
        }
        return 0;
    }

    /// <summary>The event designators <c>!!</c>, <c>!N</c>, <c>!-N</c>, <c>!prefix</c>; null when an event is not found.</summary>
    internal static string? ExpandHistory(IReadOnlyList<string> h, string word)
    {
        if (word.IndexOf('!') < 0) return word;
        var sb = new StringBuilder();
        for (int i = 0; i < word.Length; i++)
        {
            char ch = word[i];
            if (ch != '!' || i + 1 >= word.Length || word[i + 1] is ' ' or '\t' or '=' or '(')
            {
                sb.Append(ch);
                continue;
            }
            string? ev;
            if (word[i + 1] == '!')
            {
                ev = h.Count > 0 ? h[^1] : null;
                i++;
            }
            else if (char.IsAsciiDigit(word[i + 1]) || (word[i + 1] == '-' && i + 2 < word.Length && char.IsAsciiDigit(word[i + 2])))
            {
                int j = i + 1 + (word[i + 1] == '-' ? 1 : 0);
                while (j < word.Length && char.IsAsciiDigit(word[j])) j++;
                long num = long.Parse(word.AsSpan(i + 1, j - i - 1), CultureInfo.InvariantCulture);
                long ix = num < 0 ? h.Count + num : num - 1;
                ev = ix >= 0 && ix < h.Count ? h[(int)ix] : null;
                i = j - 1;
            }
            else
            {
                int j = i + 1;
                while (j < word.Length && !char.IsWhiteSpace(word[j]) && word[j] != ':') j++;
                var prefix = word[(i + 1)..j];
                ev = null;
                for (int k = h.Count - 1; k >= 0; k--)
                    if (h[k].StartsWith(prefix, StringComparison.Ordinal)) { ev = h[k]; break; }
                i = j - 1;
            }
            if (ev is null) return null;
            sb.Append(ev);
        }
        return sb.ToString();
    }

    /// <summary>
    /// <c>fc [-e ename] [-lnr] [first] [last]</c> / <c>fc -s [pat=rep] [command]</c>. For <c>-s</c> the command to
    /// re-run comes back in <paramref name="execute"/> (already echoed to stderr and put in place of the last entry);
    /// the caller runs it and its status is fc's.
    /// </summary>
    public static int Fc(BashHistoryList h, IReadOnlyList<string> args, Io io, out string? execute)
    {
        execute = null;
        bool list = false, numbers = true, reverse = false, reexec = false;
        string? editor = null;
        int i = 0;
        for (; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--") { i++; break; }
            if (arg.Length < 2 || arg[0] != '-' || arg.Skip(1).All(char.IsAsciiDigit)) break;
            for (int k = 1; k < arg.Length; k++)
            {
                switch (arg[k])
                {
                    case 'l': list = true; break;
                    case 'n': numbers = false; break;
                    case 'r': reverse = true; break;
                    case 's': reexec = true; break;
                    case 'e':
                        if (k + 1 < arg.Length) editor = arg[(k + 1)..];
                        else if (i + 1 < args.Count) editor = args[++i];
                        else
                        {
                            io.Err("fc: -e: option requires an argument");
                            io.Err(FcUsage);
                            return 2;
                        }
                        k = arg.Length;
                        break;
                    default:
                        io.Err($"fc: -{arg[k]}: invalid option");
                        io.Err(FcUsage);
                        return 2;
                }
            }
        }
        var operands = args.Skip(i).ToList();
        if (editor == "-") reexec = true;

        if (reexec)
        {
            var subs = new List<(string Pat, string Rep)>();
            int o = 0;
            for (; o < operands.Count && operands[o].IndexOf('=') is var eq && eq >= 0; o++)
                subs.Add((operands[o][..eq], operands[o][(eq + 1)..]));
            string? spec = o < operands.Count ? operands[o] : null;
            int ix = h.Count == 0 ? -1 : spec is null ? h.Count - 1 : FindEntry(h, spec, listing: false, first: false);
            if (ix < 0)
            {
                io.Err("fc: no command found");
                return 1;
            }
            var command = h[ix];
            foreach (var (pat, rep) in subs)
                if (pat.Length > 0) command = command.Replace(pat, rep, StringComparison.Ordinal);
            io.Err(command);
            // bash drops the newest entry (the fc command itself in an interactive shell) and records the re-run one.
            h.RemoveAt(h.Count - 1);
            h.FileMark = Math.Min(h.FileMark, h.Count);
            h.Add(command);
            execute = command;
            return 0;
        }

        if (!list)
        {
            io.Err(h.Count == 0 ? "fc: no command found" : "fc: editing history in an editor is not supported by ps-bash; use fc -s or fc -l");
            return 1;
        }
        if (h.Count == 0) return 0;

        int last = h.Count - 1, beg, end;
        if (operands.Count == 0) { end = last; beg = Math.Max(0, end - 15); }
        else
        {
            beg = FindEntry(h, operands[0], listing: true, first: true);
            end = operands.Count > 1 ? FindEntry(h, operands[1], listing: true, first: false) : last;
            if (beg < 0 || end < 0)
            {
                io.Err("fc: no command found");
                return 1;
            }
        }
        if (end < beg) { (beg, end) = (end, beg); reverse = true; }
        for (int k = 0; k <= end - beg; k++)
        {
            int ix = reverse ? end - k : beg + k;
            io.Out(numbers ? $"{ix + 1}\t {h[ix]}" : $"\t {h[ix]}");
        }
        return 0;
    }

    /// <summary>
    /// A history specification, resolved exactly as bash's <c>fc_gethnum</c> does in a non-interactive shell:
    /// <c>-N</c> counts back from the newest, <c>0</c> is the newest (<c>-0</c>: the newest when listing, else
    /// none), and a positive number at or past the newest entry's number is out of range — the oldest entry
    /// for <paramref name="first"/> of a range, the newest otherwise (so <c>fc -l 5 1</c> on five entries lists
    /// only entry 1, as bash does). Other text names the newest entry starting with it. -1 = none.
    /// </summary>
    private static int FindEntry(BashHistoryList h, string spec, bool listing, bool first)
    {
        int newest = h.Count - 1;
        int sign = 1, p = 0;
        if (spec.StartsWith('-')) { sign = -1; p = 1; }
        if (p < spec.Length && char.IsAsciiDigit(spec[p]))
        {
            // atoi: the leading digits.
            long n = 0;
            for (; p < spec.Length && char.IsAsciiDigit(spec[p]) && n < int.MaxValue; p++) n = n * 10 + (spec[p] - '0');
            n *= sign;
            if (n < 0) return (int)Math.Max(0, n + newest + 1);
            if (n == 0) return sign == -1 ? (listing ? newest : -1) : newest;
            n -= 1;
            return n >= newest ? (first ? 0 : newest) : (int)n;
        }
        for (int k = newest; k >= 0; k--)
            if (h[k].StartsWith(spec, StringComparison.Ordinal)) return k;
        return -1;
    }

    /// <summary>bash <c>legal_number</c>: optional blanks and sign, decimal digits, trailing blanks.</summary>
    internal static bool TryLegalNumber(string s, out long value)
    {
        var t = s.Trim(' ', '\t', '\n');
        return long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) && t.Length > 0;
    }
}
