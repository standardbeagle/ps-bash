using System.Diagnostics;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>The <c>times</c> builtin: user/system CPU time of the shell, then of its children (two lines).</summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashTimes")]
public sealed class InvokeBashTimesCommand : BashCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "times", Arguments ?? Array.Empty<string>())) return;
        // times has no options: `times -Q` is bash's usage error, status 2 (it printed the times, status 0).
        if (FileSystemHelpers.TryRejectBuiltinOptions(this, "times", Arguments ?? Array.Empty<string>(), "")) return;
        var p = Process.GetCurrentProcess();
        WriteObject($"{Fmt(p.UserProcessorTime)} {Fmt(p.PrivilegedProcessorTime)}");
        WriteObject($"{Fmt(TimeSpan.Zero)} {Fmt(TimeSpan.Zero)}");
    }

    internal static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}m{t.Seconds + t.Milliseconds / 1000.0:0.000}s";
}
