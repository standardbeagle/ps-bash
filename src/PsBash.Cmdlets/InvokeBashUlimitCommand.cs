using System.Collections;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The <c>ulimit</c> builtin. Windows has no per-process resource limits to read or set, so each limit is emulated per
/// shell (runspace) with a soft and a hard value, starting from fixed defaults (most <c>unlimited</c>). Setting follows
/// bash on Linux for an unprivileged user: no <c>-S</c>/<c>-H</c> sets both; a soft limit above the hard one is
/// "Invalid argument"; raising a hard limit is "Operation not permitted"; a value whose byte count overflows 64 bits is
/// "limit out of range". Each failure is status 1 and changes nothing.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashUlimit")]
public sealed class InvokeBashUlimitCommand : BashCmdlet
{
    private const long Unlimited = long.MaxValue;

    // Flag, bash 5.2 `ulimit -a` label, name in error messages, unit multiplier, soft default, hard default.
    private static readonly (char Flag, string Label, string Name, long Factor, long Soft, long Hard)[] Limits =
    {
        ('c', "core file size              (blocks, -c)", "core file size", 512, 0, Unlimited),
        ('d', "data seg size               (kbytes, -d)", "data seg size", 1024, Unlimited, Unlimited),
        ('f', "file size                   (blocks, -f)", "file size", 512, Unlimited, Unlimited),
        ('n', "open files                          (-n)", "open files", 1, 1024, 1048576),
        ('s', "stack size                  (kbytes, -s)", "stack size", 1024, 8192, Unlimited),
        ('t', "cpu time                   (seconds, -t)", "cpu time", 1, Unlimited, Unlimited),
        ('u', "max user processes                  (-u)", "max user processes", 1, Unlimited, Unlimited),
        ('v', "virtual memory              (kbytes, -v)", "virtual memory", 1024, Unlimited, Unlimited),
    };

    /// <summary>The per-shell table (global variable of this runspace): "S:f" / "H:f" -> value. A static field leaked
    /// one script's limits into every later script of the host.</summary>
    private const string StateVariable = "__psbashUlimit";

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "ulimit", args)) return;

        char flag = 'f';
        bool all = false, soft = false, hard = false;
        string? value = null;
        bool optionsDone = false;
        foreach (var a in args)
        {
            if (!optionsDone && a == "--") { optionsDone = true; continue; }
            if (!optionsDone && a.Length >= 2 && a[0] == '-' && a[1] != '-')
            {
                foreach (var ch in a.AsSpan(1))
                {
                    if (ch == 'a') all = true;
                    else if (ch == 'S') soft = true;
                    else if (ch == 'H') hard = true;
                    else flag = ch;
                }
            }
            else value = a;
        }

        var state = State();
        if (all)
        {
            foreach (var l in Limits) WriteObject($"{l.Label} {Show(Get(state, l.Flag, hard && !soft))}");
            return;
        }
        int idx = Array.FindIndex(Limits, l => l.Flag == flag);
        if (idx < 0)
        {
            FileSystemHelpers.WriteStderr(this, $"ulimit: -{flag}: invalid option");
            FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }
        var limit = Limits[idx];
        // Showing: the soft limit unless only -H is given.
        if (value is null) { WriteObject(Show(Get(state, flag, hard && !soft))); return; }

        long n;
        if (value == "unlimited") n = Unlimited;
        else if (value is "hard" or "soft") n = Get(state, flag, value == "hard");
        else if (!long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n))
        {
            FileSystemHelpers.WriteStderr(this, $"ulimit: {value}: invalid number");
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }
        else if (n > Unlimited / limit.Factor - 1)
        {
            FileSystemHelpers.WriteStderr(this, $"ulimit: {value}: limit out of range");
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        if (!soft && !hard) soft = hard = true;
        long newSoft = soft ? n : Get(state, flag, false);
        long newHard = hard ? n : Get(state, flag, true);
        string? error = null;
        if (newSoft > newHard) error = "Invalid argument";
        else if (newHard > Get(state, flag, true)) error = "Operation not permitted";
        if (error != null)
        {
            FileSystemHelpers.WriteStderr(this, $"ulimit: {limit.Name}: cannot modify limit: {error}");
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }
        state["S:" + flag] = newSoft;
        state["H:" + flag] = newHard;
    }

    private Hashtable State()
    {
        if (SessionState.PSVariable.GetValue("global:" + StateVariable) is Hashtable h) return h;
        var created = new Hashtable(StringComparer.Ordinal);
        SessionState.PSVariable.Set("global:" + StateVariable, created);
        return created;
    }

    private static long Get(Hashtable state, char flag, bool hard)
    {
        if (state[(hard ? "H:" : "S:") + flag] is long v) return v;
        var l = Array.Find(Limits, x => x.Flag == flag);
        return hard ? l.Hard : l.Soft;
    }

    private static string Show(long v) =>
        v == Unlimited ? "unlimited" : v.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
