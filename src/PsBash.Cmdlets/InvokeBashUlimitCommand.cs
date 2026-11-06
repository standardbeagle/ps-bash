using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The <c>ulimit</c> builtin. Windows has no per-process resource limits to read or set, so every limit reports
/// <c>unlimited</c> (except the open-file limit, 1024) and setting one is accepted and remembered for the shell's lifetime.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashUlimit")]
public sealed class InvokeBashUlimitCommand : PSCmdlet
{
    private static readonly (char Flag, string Label, string Default)[] Limits =
    {
        ('c', "core file size          (blocks, -c)", "0"),
        ('d', "data seg size           (kbytes, -d)", "unlimited"),
        ('f', "file size               (blocks, -f)", "unlimited"),
        ('n', "open files                      (-n)", "1024"),
        ('s', "stack size              (kbytes, -s)", "8192"),
        ('t', "cpu time               (seconds, -t)", "unlimited"),
        ('u', "max user processes              (-u)", "unlimited"),
        ('v', "virtual memory          (kbytes, -v)", "unlimited"),
    };

    private static readonly Dictionary<char, string> Values = new();

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "ulimit", args)) return;

        char flag = 'f';
        bool all = false;
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
                    else if (ch is 'S' or 'H') { }
                    else flag = ch;
                }
            }
            else value = a;
        }

        if (all)
        {
            foreach (var (f, label, _) in Limits) WriteObject($"{label} {Get(f)}");
            return;
        }
        if (Array.FindIndex(Limits, l => l.Flag == flag) < 0)
        {
            FileSystemHelpers.WriteStderr(this, $"ulimit: -{flag}: invalid option");
            FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }
        if (value is null) { WriteObject(Get(flag)); return; }
        if (value != "unlimited" && (!long.TryParse(value, out var n) || n < 0))
        {
            FileSystemHelpers.WriteStderr(this, $"ulimit: {value}: invalid number");
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }
        Values[flag] = value;
    }

    private static string Get(char flag) =>
        Values.TryGetValue(flag, out var v) ? v : Array.Find(Limits, l => l.Flag == flag).Default;
}
