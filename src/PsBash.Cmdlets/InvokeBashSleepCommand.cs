using System.Management.Automation;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashSleep</c>
/// (REFACTOR-2). Sleeps for the sum of operand durations, matching GNU
/// coreutils <c>sleep</c>. Each operand is a decimal number with an
/// optional unit suffix: <c>s</c> (seconds, default), <c>m</c> (minutes),
/// <c>h</c> (hours), <c>d</c> (days).
///
/// Behavioral parity oracle: the original psm1 function. Branches preserved:
/// no operands → "missing operand" error; non-numeric or negative → "invalid
/// time interval" error; otherwise sleep for the summed duration at ms precision
/// (ceiling of seconds × 1000) through <see cref="PreciseSleep"/>, which wakes on time
/// rather than on the 15.6 ms Windows tick. In an in-process background job the duration is settled against the
/// job's start lag first (<see cref="JobClock"/>).
///
/// Termination matches bash: the sleep respects the
/// pipeline's stop signal because <see cref="PSCmdlet.Stopping"/> is checked
/// between operand iterations and around the sleep window itself via a
/// chunked-sleep loop. A pipeline shutdown (Ctrl-C, downstream
/// <c>Select-Object -First N</c>) returns within at most one chunk.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashSleep")]
[OutputType(typeof(void))]
public sealed class InvokeBashSleepCommand : BashCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    private static readonly Regex SuffixedNumber = new(
        @"^([\d.]+)([smhd])$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "sleep", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "sleep"))
            {
                WriteObject(line);
            }
            return;
        }

        if (args.Length == 0)
        {
            FileSystemHelpers.WriteMissingOperand(this, "sleep");
            return;
        }

        double totalSeconds = 0.0;
        foreach (var arg in args)
        {
            double multiplier = 1.0;
            string numStr = arg;

            var m = SuffixedNumber.Match(arg);
            if (m.Success)
            {
                numStr = m.Groups[1].Value;
                multiplier = m.Groups[2].Value switch
                {
                    "s" => 1.0,
                    "m" => 60.0,
                    "h" => 3600.0,
                    "d" => 86400.0,
                    _ => 1.0,
                };
            }

            if (!double.TryParse(numStr,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var val) || val < 0)
            {
                FileSystemHelpers.WriteBashError(this, $"sleep: invalid time interval '{arg}'");
                return;
            }

            totalSeconds += val * multiplier;
        }

        if (totalSeconds <= 0) return;

        // Chunked sleep so PSCmdlet.Stopping is polled regularly — a
        // downstream Ctrl-C or pipeline-stop interrupts within ~100ms
        // rather than waiting out the full duration.
        var totalMs = (long)Math.Ceiling(totalSeconds * 1000);
        // In a background job the sleep counts from the `&` that launched it, not from the late start of its body
        // (JobClock, docs/specs/background-jobs.md section 12.5).
        if (JobClock.Current(SessionState) is { } clock)
        {
            clock.Blocking();
            totalMs = clock.Settle(totalMs);
        }
        const int ChunkMs = 100;
        while (totalMs > 0)
        {
            if (Stopping) return;
            var step = totalMs > ChunkMs ? ChunkMs : (int)totalMs;
            PreciseSleep.For(step);
            totalMs -= step;
        }
    }
}
