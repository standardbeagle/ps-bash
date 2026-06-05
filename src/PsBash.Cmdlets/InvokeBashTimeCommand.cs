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
/// no-args → "time: missing command" error via psm1 <c>Write-BashError</c>;
/// happy path → invoke command, collect <c>ErrorRecord</c> output as bash
/// errors (each routed through <c>Write-BashError</c>), accumulate
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
            "$c = $args[0]; $rest = @(); " +
            "if ($args.Count -gt 1) { $rest = $args[1..($args.Count - 1)] }; " +
            "& $c @rest 2>&1";

        var allInvokeArgs = new object[args.Length];
        allInvokeArgs[0] = cmd;
        for (int i = 1; i < args.Length; i++) allInvokeArgs[i] = args[i];

        var sw = Stopwatch.StartNew();
        int exitCode = 0;
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
            if (errors.Count > 0) exitCode = 1;
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
        var formatted = string.Format(
            CultureInfo.InvariantCulture,
            "real    {0:N3}s",
            realTime.TotalSeconds);
        System.Console.Error.WriteLine(formatted);

        var pso = new PSObject();
        pso.TypeNames.Insert(0, "PsBash.TimeOutput");
        pso.Properties.Add(new PSNoteProperty("RealTime", realTime));
        pso.Properties.Add(new PSNoteProperty("Command", cmd));
        pso.Properties.Add(new PSNoteProperty("ExitCode", exitCode));
        pso.Properties.Add(new PSNoteProperty("BashText", outputText));
        WriteObject(pso);
    }
}
