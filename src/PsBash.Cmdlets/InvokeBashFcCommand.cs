using System.Globalization;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The <c>fc</c> builtin's listing form over the session history that <c>history</c> keeps
/// (<see cref="InvokeBashHistoryCommand.Entries"/>):
/// <code>
/// fc -l [-nr] [first [last]]   list "N\t cmd" (-n: "\t cmd"), -r reversed; default: the last 16
/// </code>
/// <c>first</c>/<c>last</c> are a history number, a negative offset from the current command, or the prefix of a
/// command (the most recent match). When the shell records its own lines (<c>set -o history</c>, which an
/// interactive shell sets) the last entry is this <c>fc</c> command and is skipped, as bash skips it.
/// Editing and re-executing (<c>fc [-e ename]</c>, <c>fc -s</c>) are not supported: after the history lookup they
/// fail with status 1. Without this, PowerShell's <c>fc</c> alias (<c>Format-Custom</c>) ran.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashFc")]
public sealed class InvokeBashFcCommand : PSCmdlet
{
    // Decoy for DIRECT calls only: a bare -e would bind a common parameter. Put back in front of the arguments.
    [Parameter] public string? e { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    internal const string Usage = "fc: usage: fc [-e ename] [-lnr] [first] [last] or fc -s [pat=rep] [command]";

    /// <summary>Pure: runs <paramref name="args"/> against <paramref name="history"/>. <paramref name="lastIsCurrent"/>:
    /// the last entry is this command itself (bash with history recording on).</summary>
    internal static InvokeBashHistoryCommand.Result Run(IReadOnlyList<string> args, IReadOnlyList<string> history, bool lastIsCurrent)
    {
        var o = new List<string>(); var err = new List<string>();
        InvokeBashHistoryCommand.Result Done(int s) => new(o, err, s);
        InvokeBashHistoryCommand.Result Fail(int s, string msg) { err.Add("fc: " + msg); return Done(s); }

        bool list = false, numbers = true, reverse = false, reexec = false;
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
                            i++;
                        }
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

        int count = history.Count;
        int last = lastIsCurrent && count > 0 ? count - 1 : count;   // the last entry fc considers (1-based number)

        if (reexec || !list)
        {
            if (last == 0) return Fail(1, "no command found");
            var spec = reexec ? operands.FirstOrDefault(op => !op.Contains('=')) : operands.FirstOrDefault();
            if (spec is not null && Resolve(spec, history, last) is null) return Fail(1, "no command found");
            return Fail(1, "editing and re-executing history is not supported");
        }

        if (last == 0) return Done(0);
        int first, end;
        if (operands.Count == 0)
        {
            end = last;
            first = Math.Max(1, last - 15);
        }
        else
        {
            if (Resolve(operands[0], history, last) is not int f) return Fail(1, "no command found");
            int? l = operands.Count > 1 ? Resolve(operands[1], history, last) : last;
            if (l is null) return Fail(1, "no command found");
            first = f > last ? 1 : f;   // bash: a first number past the end lists from the start
            end = Math.Min(l.Value, last);
        }
        if (first > end) { (first, end) = (end, first); reverse = true; }   // -r never un-reverses
        for (int k = 0; k <= end - first; k++)
        {
            int n = reverse ? end - k : first + k;
            o.Add(numbers ? $"{n}\t {history[n - 1]}" : $"\t {history[n - 1]}");
        }
        return Done(0);
    }

    /// <summary>A history number for a <c>first</c>/<c>last</c> operand: N, -N from the current command, 0 for the
    /// last, else the most recent entry starting with the text. Null when no entry matches.</summary>
    private static int? Resolve(string spec, IReadOnlyList<string> history, int last)
    {
        if (int.TryParse(spec, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
        {
            if (n < 0) return Math.Max(1, last + 1 + n);
            if (n == 0) return last;
            return n;
        }
        for (int j = last - 1; j >= 0; j--)
            if (history[j].StartsWith(spec, StringComparison.Ordinal)) return j + 1;
        return null;
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
        foreach (var line in r.Err) FileSystemHelpers.WriteStderr(this, line);
        FileSystemHelpers.SetLastExitCode(this, r.Status);
    }
}
