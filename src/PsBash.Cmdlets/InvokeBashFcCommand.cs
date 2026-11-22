using System.Globalization;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The <c>fc</c> builtin over the session history that <c>history</c> keeps (<see cref="InvokeBashHistoryCommand.Entries"/>):
/// <code>
/// fc -l [-nr] [first [last]]   list "N\t cmd" (-n: "\t cmd"), -r reversed; default: the last 16
/// fc -s [pat=rep ...] [cmd]    re-run an entry (default: the last) with every pat replaced by rep; also fc -e - ...
/// </code>
/// <c>first</c>/<c>last</c>/<c>cmd</c> are a history number, a negative offset from the current command, or the prefix
/// of a command (the most recent match), resolved by bash's <c>fc_gethnum</c> rules (<see cref="Resolve"/>). When the
/// shell records its own lines (<c>set -o history</c>, which an interactive shell sets) the last entry is this
/// <c>fc</c> command and is skipped, as bash skips it.
/// <para>Re-running: the command is echoed to stderr, replaces the LAST history entry (bash's <c>fc_replhist</c>, also
/// with history recording off), and is left in <c>$global:__BashFcRerun</c> (<c>PsBuild.FcRerunVariable</c>) for the
/// emitted code (<c>PsBuild.FcTakeRerun</c>) to run through the one run-time call of bash text
/// (<c>PsBuild.RunBashTextCall</c>, as eval), in the caller's scope. The cmdlet never runs script itself. Opening an
/// editor (<c>fc</c>, <c>fc -e ename</c>) is refused with status 1.</para>
/// Without this, PowerShell's <c>fc</c> alias (<c>Format-Custom</c>) ran.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashFc")]
public sealed class InvokeBashFcCommand : BashCmdlet
{
    // Decoy for DIRECT calls only: a bare -e would bind a common parameter. Put back in front of the arguments.
    [Parameter] public string? e { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    internal const string Usage = "fc: usage: fc [-e ename] [-lnr] [first] [last] or fc -s [pat=rep] [command]";
    internal const string EditorRefused = "fc: opening an editor is not supported";

    /// <summary>Pure: runs <paramref name="args"/> against <paramref name="history"/> (a re-run replaces its last entry).
    /// <paramref name="lastIsCurrent"/>: the last entry is this command itself (bash with history recording on).
    /// A re-run returns the command in <see cref="InvokeBashHistoryCommand.Result.Rerun"/>.</summary>
    internal static InvokeBashHistoryCommand.Result Run(IReadOnlyList<string> args, List<string> history, bool lastIsCurrent)
    {
        var o = new List<string>(); var err = new List<string>();
        InvokeBashHistoryCommand.Result Done(int s) => new(o, err, s);
        InvokeBashHistoryCommand.Result Fail(int s, string msg) { err.Add("fc: " + msg); return Done(s); }

        bool list = false, numbers = true, reverse = false, reexec = false;
        string? ename = null;
        int i = 0;
        for (; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--") { i++; break; }
            if (a.Length < 2 || a[0] != '-' || char.IsAsciiDigit(a[1])) break;
            for (int k = 1; k < a.Length; k++)
            {
                switch (a[k])
                {
                    case 'l': list = true; break;
                    case 'n': numbers = false; break;
                    case 'r': reverse = true; break;
                    case 's': reexec = true; break;
                    case 'e':
                        if (k + 1 == a.Length)
                        {
                            if (i + 1 >= args.Count) { err.Add("fc: -e: option requires an argument"); err.Add(Usage); return Done(2); }
                            ename = args[++i];
                        }
                        else ename = a[(k + 1)..];
                        k = a.Length;
                        break;
                    default:
                        err.Add($"fc: -{a[k]}: invalid option");
                        err.Add(Usage);
                        return Done(2);
                }
            }
        }
        var operands = args.Skip(i).ToList();
        // `fc -e - ...` is `fc -s ...`; either wins over -l (bash checks it first).
        reexec |= ename == "-";

        int count = history.Count;
        int last = lastIsCurrent && count > 0 ? count - 1 : count;   // the last entry fc considers (1-based number)

        if (reexec)
        {
            // Leading pat=rep words, then the command to re-run (default: the last entry).
            int s = 0;
            while (s < operands.Count && operands[s].Contains('=')) s++;
            if (last == 0) return Fail(1, "no command found");
            int? n = s < operands.Count ? Resolve(operands[s], history, last, first: false, listing: false) : last;
            if (n is not int num) return Fail(1, "no command found");
            var command = history[num - 1];
            for (int k = 0; k < s; k++)
            {
                int eq = operands[k].IndexOf('=');
                command = Substitute(command, operands[k][..eq], operands[k][(eq + 1)..]);
            }
            err.Add(command);
            if (command.Length > 0)
            {
                if (history.Count > 0) history.RemoveAt(history.Count - 1);
                history.Add(command);
            }
            return Done(0) with { Rerun = command };
        }

        if (!list)
        {
            if (last == 0) return Fail(1, "no command found");
            var spec = operands.FirstOrDefault();
            if (spec is not null && Resolve(spec, history, last, first: true, listing: false) is null) return Fail(1, "no command found");
            err.Add(EditorRefused);
            return Done(1);
        }

        if (count == 0) return Done(0);
        // bash's fc_gethnum: a last entry before the first is the first ("per POSIX"), so when this fc line is the only
        // entry every listing names it (`history -c` then `fc -l 0` prints "1\t fc -l 0").
        int listLast = Math.Max(1, last);
        int from, end;
        if (operands.Count == 0)
        {
            end = listLast;
            from = Math.Max(1, listLast - 15);
        }
        else
        {
            if (Resolve(operands[0], history, listLast, first: true, listing: true) is not int f) return Fail(1, "no command found");
            // One operand: up to the last entry, or the current line itself when that is what it named (`fc -l -0`).
            int? l = operands.Count > 1 ? Resolve(operands[1], history, listLast, first: false, listing: true) : f == count ? count : listLast;
            if (l is null) return Fail(1, "no command found");
            from = f;
            end = l.Value;
        }
        if (from > end) { (from, end) = (end, from); reverse = true; }   // -r never un-reverses
        for (int k = 0; k <= end - from; k++)
        {
            int n = reverse ? end - k : from + k;
            o.Add(numbers ? $"{n}\t {history[n - 1]}" : $"\t {history[n - 1]}");
        }
        return Done(0);
    }

    /// <summary>
    /// bash's <c>fc_gethnum</c>: the history number (1-based) an operand names. -N counts back from the current command
    /// (never before 1); 0 is the last entry before this command; -0 is the very last entry when listing (bash's
    /// <c>real_last</c>: under <c>set -o history</c> this <c>fc</c> line itself, <c>fc -l -0</c>), else nothing; a number
    /// at or past the last entry is the first entry when it is a range's <paramref name="first"/> end, else the last (so
    /// <c>fc -l 5</c> over five entries lists all five); any other text is the most recent entry starting with it. Null
    /// when nothing matches.
    /// </summary>
    private static int? Resolve(string spec, IReadOnlyList<string> history, int last, bool first, bool listing)
    {
        if (int.TryParse(spec, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
        {
            if (n < 0) return Math.Max(1, last + 1 + n);
            if (n == 0) return !spec.StartsWith('-') ? last : listing ? history.Count : null;
            if (n >= last) return first ? 1 : last;
            return n;
        }
        for (int j = last - 1; j >= 0; j--)
            if (history[j].StartsWith(spec, StringComparison.Ordinal)) return j + 1;
        return null;
    }

    /// <summary>bash's <c>fc_dosubs</c>: every <paramref name="pat"/> in <paramref name="command"/> becomes
    /// <paramref name="rep"/>; an empty pattern matches each character.</summary>
    private static string Substitute(string command, string pat, string rep)
    {
        if (pat.Length > 0) return command.Replace(pat, rep, StringComparison.Ordinal);
        var sb = new System.Text.StringBuilder(command.Length * Math.Max(1, rep.Length));
        for (int k = 0; k < command.Length; k++) sb.Append(rep);
        return sb.ToString();
    }

    protected override void EndProcessing()
    {
        var args = new List<string>();
        if (e is not null) { args.Add("-e"); args.Add(e); }
        args.AddRange(Arguments ?? Array.Empty<string>());
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "fc", args.ToArray())) return;

        var r = Run(args, InvokeBashHistoryCommand.Entries(SessionState), InvokeBashShoptCommand.IsEnabled("set:history"));
        foreach (var line in r.Out) WriteObject(line);
        // A re-run's stderr is the echoed command, as typed; every other line is a shell diagnostic and carries the
        // source prefix (`s.sh: line 5: fc: no command found`), as `history`'s do. A usage line has none (bash).
        foreach (var line in r.Err)
            FileSystemHelpers.WriteStderr(this, r.Rerun is not null || line.StartsWith("fc: usage:", StringComparison.Ordinal)
                ? line : ShellDiagnostic.Prefix(SessionState) + line);
        if (r.Rerun is not null) SessionState.PSVariable.Set("global:" + PsBash.Core.Parser.PsBuild.FcRerunVariable, r.Rerun);
        FileSystemHelpers.SetLastExitCode(this, r.Status);
    }
}
