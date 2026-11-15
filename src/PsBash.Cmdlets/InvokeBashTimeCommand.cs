using System.Diagnostics;
using System.Globalization;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashTime</c>
/// (REFACTOR-2). Runs the wrapped command, measures wall-clock elapsed
/// time, and writes the timing summary to stderr — matching the bash
/// <c>time</c> builtin / GNU <c>time</c> semantic.
///
/// Behavioral parity oracle: the original psm1 function. Reproduces:
/// no-args → "time: missing command" error via <see cref="FileSystemHelpers.WriteBashError"/>;
/// happy path → invoke command, collect <c>ErrorRecord</c> output as bash
/// errors (each re-emitted through <see cref="FileSystemHelpers.WriteBashError"/>), accumulate
/// non-error output's <c>BashText</c> joined with <c>\n</c> as the result's
/// <c>BashText</c>, set <c>ExitCode = 1</c> if any error surfaced; finally
/// emit a typed <c>PsBash.TimeOutput</c> PSObject (<c>RealTime</c>,
/// <c>Command</c>, <c>ExitCode</c>, <c>BashText</c>) and write
/// <c>"real    {seconds:N3}s"</c> to <c>[Console]::Error</c>.
///
/// Security (Directive 12): the wrapped command name and its arguments are
/// passed positionally through <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>
/// with a fixed parameterless script body — never concatenated into the
/// body. A name containing <c>;</c>, <c>$()</c>, scriptblock chars, or
/// backticks therefore cannot be re-parsed as PowerShell syntax; it is
/// looked up as a literal command name and fails the usual
/// CommandNotFoundException path. No <see cref="ScriptBlock"/> construction
/// in C#; AOT-safe.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashTime")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashTimeCommand : PSCmdlet
{
    /// <summary>
    /// Decoys for GNU time's <c>-p</c>, <c>-v</c> and <c>-o</c>: called directly (<c>Invoke-BashTime -p true</c>) the
    /// binder would take them as prefixes of the common parameters (-PipelineVariable/-ProgressAction, -Verbose,
    /// -OutVariable/-OutBuffer). The emitter quotes every operand, so a transpiled script reaches <see cref="Arguments"/>.
    /// </summary>
    [Parameter] public SwitchParameter P { get; set; }
    [Parameter] public SwitchParameter V { get; set; }
    [Parameter] public string? O { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "time", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "time"))
            {
                WriteObject(line);
            }
            return;
        }

        // GNU time's own options come first (`command time -f '%e %M' cmd`, `env time -p cmd`).
        var opts = GnuTimeFormat.Parse(args);
        if (opts.Error is not null)
        {
            FileSystemHelpers.WriteBashError(this, opts.Error);
            FileSystemHelpers.WriteBashError(this, "Try 'time --help' for more information.");
            FileSystemHelpers.SetLastExitCode(this, 125);
            return;
        }
        string? unsupported = opts.Unsupported ?? (V.IsPresent ? "-v" : O is not null ? "-o" : null);
        if (unsupported is not null)
        {
            FileSystemHelpers.WriteBashError(this, $"time: option '{unsupported}' is recognized but not supported by ps-bash");
            FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }
        string? gnuFormat = opts.Format ?? (P.IsPresent ? GnuTimeFormat.Portable : null);
        if (opts.CommandIndex > 0) args = args[opts.CommandIndex..];

        if (args.Length == 0)
        {
            FileSystemHelpers.WriteBashError(this, "time: missing command");
            return;
        }

        var cmd = args[0];
        var cmdArgs = new object[args.Length - 1];
        for (int i = 1; i < args.Length; i++) cmdArgs[i - 1] = args[i];

        // Fixed script body: $args[0] is the command name, the rest are
        // bound positionally and splatted. The body never embeds any user
        // input as PS code.
        const string invokeBody =
            "$c = Resolve-BashCommand $args[0]; $rest = @(); " +
            "if ($args.Count -gt 1) { $rest = $args[1..($args.Count - 1)] }; " +
            "& $c @rest 2>&1";

        var allInvokeArgs = new object[args.Length];
        allInvokeArgs[0] = cmd;
        for (int i = 1; i < args.Length; i++) allInvokeArgs[i] = args[i];

        FileSystemHelpers.SetLastExitCode(this, 0);
        using var self = Process.GetCurrentProcess();
        TimeSpan user0 = self.UserProcessorTime, sys0 = self.PrivilegedProcessorTime;
        var sw = Stopwatch.StartNew();
        int exitCode = 0;
        bool faulted = false;
        var normal = new List<object?>();
        var errors = new List<ErrorRecord>();

        try
        {
            var output = InvokeCommand.InvokeScript(invokeBody, allInvokeArgs);
            sw.Stop();
            foreach (var item in output)
            {
                if (item == null) { normal.Add(null); continue; }
                var basePso = item is PSObject p ? p.BaseObject : item;
                if (basePso is ErrorRecord er)
                {
                    errors.Add(er);
                }
                else
                {
                    normal.Add(item);
                }
            }
            if (errors.Count > 0) { exitCode = 1; faulted = true; }
            else if (InvokeCommand.InvokeScript("[int]$global:LASTEXITCODE").FirstOrDefault()?.BaseObject is int status) exitCode = status;
            foreach (var er in errors)
            {
                FileSystemHelpers.WriteBashError(this, er.ToString());
            }
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            FileSystemHelpers.WriteBashError(this, ex.Message);
            exitCode = 1;
            faulted = true;
        }

        // Build the joined output text (oracle: BashText property if
        // present, else stringified).
        var parts = new List<string>(normal.Count);
        foreach (var item in normal)
        {
            parts.Add(BashRuntime.GetBashText(item));
        }
        var outputText = string.Join("\n", parts);

        var realTime = sw.Elapsed;
        // The error stream, not Console.Error: `{ time cmd; } 2>/dev/null` must drop the report.
        if (gnuFormat is not null)
        {
            // CPU and peak memory are this host process's (the command runs in it, or as its child): the closest
            // figures there are to GNU time's wait4() rusage of a separate child.
            self.Refresh();
            var stats = new GnuTimeStats(realTime, self.UserProcessorTime - user0, self.PrivilegedProcessorTime - sys0,
                self.PeakWorkingSet64 / 1024, exitCode, string.Join(" ", args));
            FileSystemHelpers.WriteStderrBytes(this, GnuTimeFormat.Render(gnuFormat, stats, opts.Quiet));
        }
        else
        {
            var formatted = string.Format(
                CultureInfo.InvariantCulture,
                "real    {0:N3}s",
                realTime.TotalSeconds);
            FileSystemHelpers.WriteStderrBytes(this, formatted + "\n");
        }

        var pso = new PSObject();
        pso.TypeNames.Insert(0, "PsBash.TimeOutput");
        pso.Properties.Add(new PSNoteProperty("RealTime", realTime));
        pso.Properties.Add(new PSNoteProperty("Command", cmd));
        pso.Properties.Add(new PSNoteProperty("ExitCode", exitCode));
        pso.Properties.Add(new PSNoteProperty("BashText", outputText));
        // A silent command still yields the timing record (direct callers read .RealTime), but as an unterminated
        // empty text: a plain empty TimeOutput would print a blank line bash does not.
        if (outputText.Length == 0 && !faulted) pso.Properties.Add(new PSNoteProperty("NoTrailingNewline", true));
        WriteObject(pso);
        FileSystemHelpers.SetLastExitCode(this, exitCode);
    }
}
