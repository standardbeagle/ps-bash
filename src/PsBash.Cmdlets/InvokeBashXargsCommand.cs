using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashXargs</c>
/// (REFACTOR-2). Reads items from pipeline input, builds and runs a command
/// per item-batch.
///
/// Oracle parity: the psm1 oracle implemented <c>-0</c> (NUL-separated input
/// vs newline-separated default), <c>-I REPLACE</c> (replace-token mode —
/// run command once per input line with REPLACE substituted in each arg),
/// <c>-n N</c> (batch N items per invocation), <c>--</c> end-of-flags, and
/// the leading-word "if a runtime function named <c>Invoke-Bash{Cmd}</c>
/// exists, route through it" resolution. Default (no <c>-I</c> / <c>-n</c>):
/// all collected items joined as args to a single invocation. This cmdlet
/// reproduces every oracle branch byte-for-byte.
///
/// Beyond the oracle, the cmdlet also accepts <c>-r</c> /
/// <c>--no-run-if-empty</c> (skip the invocation entirely when no items
/// were read), <c>-t</c> (echo the command + args to stderr before each
/// run), and <c>-L N</c> (run command per N input lines — synonymous with
/// <c>-n N</c> in this implementation since the oracle's input is
/// already line-segmented). <c>-p</c> (interactive prompt), <c>-P N</c>
/// (parallel) are accepted but ignored — the oracle had no concept of
/// either, and a PowerShell runspace cannot prompt nor fork.
///
/// Pipeline-only — the oracle never accepted file operands; non-flag
/// positional tokens are always the command and its leading args.
///
/// Security (Directive 12): the command name and every arg are passed
/// positionally through
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>
/// with a fixed parameterless script body — never concatenated into the
/// body. A token containing <c>;</c>, <c>$()</c>, scriptblock chars, or
/// backticks therefore cannot be re-parsed as PowerShell syntax; it is
/// looked up as a literal command name (or value) and passed through
/// unevaluated. No <see cref="ScriptBlock"/> construction; AOT-safe.
///
/// Flag collisions: <c>-I REPLACE</c> prefix-collides with
/// <c>-InformationAction</c> / <c>-InformationVariable</c>, declared as
/// value-bearing <c>string? I</c>. <c>-P N</c> prefix-collides with
/// <c>-PipelineVariable</c> / <c>-ProgressAction</c>, declared as nullable
/// <c>int? P</c>. <c>-n N</c>, <c>-L N</c>, <c>-r</c>, <c>-t</c>, <c>-0</c>,
/// <c>-p</c>, <c>--</c> have no PowerShell common-parameter prefix
/// collision and stay in <c>Arguments</c>, parsed by a manual scan
/// matching the oracle's <c>-ceq</c> dispatch (case-sensitive on the
/// numeric / NUL-separator forms).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashXargs")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashXargsCommand : PSCmdlet
{
    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>
    /// <c>-I REPLACE</c> — value-bearing. Declared as a literal single-letter
    /// parameter so the binder routes the bare token by exact name match
    /// (beats the <c>-InformationAction</c> / <c>-InformationVariable</c>
    /// common-parameter prefix match).
    /// </summary>
    [Parameter]
    public string? I { get; set; }

    /// <summary>
    /// <c>-P N</c> — value-bearing. Declared literally as <c>P</c> for the
    /// same reason as <c>I</c> above (prefix-collides with
    /// <c>-PipelineVariable</c> / <c>-ProgressAction</c>). Accepted for
    /// argv compatibility but no parallelization is performed — the oracle
    /// did not implement it.
    /// </summary>
    [Parameter]
    public int? P { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    private readonly List<PSObject?> _pipelineItems = new();

    protected override void ProcessRecord()
    {
        _pipelineItems.Add(InputObject);
    }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "xargs"))
            {
                WriteObject(line);
            }
            return;
        }

        string? replaceStr = I;  // may also be set by -IREPLACE joined form
        int maxArgs = 0;
        int maxLines = 0;
        bool nullDelim = false;
        bool noRunIfEmpty = false;
        bool traceCmd = false;
        // P is accepted via parameter binding but otherwise unused.
        _ = P;

        var operands = new List<string>();
        bool pastDoubleDash = false;

        int i = 0;
        while (i < args.Length)
        {
            var arg = args[i];

            if (pastDoubleDash)
            {
                operands.Add(arg);
                i++;
                continue;
            }

            if (arg == "--")
            {
                pastDoubleDash = true;
                i++;
                continue;
            }

            if (string.Equals(arg, "-0", System.StringComparison.Ordinal) ||
                string.Equals(arg, "--null", System.StringComparison.Ordinal))
            {
                nullDelim = true;
                i++;
                continue;
            }

            if (string.Equals(arg, "-r", System.StringComparison.Ordinal) ||
                string.Equals(arg, "--no-run-if-empty", System.StringComparison.Ordinal))
            {
                noRunIfEmpty = true;
                i++;
                continue;
            }

            if (string.Equals(arg, "-t", System.StringComparison.Ordinal) ||
                string.Equals(arg, "--verbose", System.StringComparison.Ordinal))
            {
                traceCmd = true;
                i++;
                continue;
            }

            if (string.Equals(arg, "-p", System.StringComparison.Ordinal) ||
                string.Equals(arg, "--interactive", System.StringComparison.Ordinal))
            {
                // Interactive prompt — accepted but ignored (the oracle had
                // no concept of it and a PS runspace cannot prompt).
                i++;
                continue;
            }

            if (string.Equals(arg, "-I", System.StringComparison.Ordinal))
            {
                i++;
                if (i < args.Length) replaceStr = args[i];
                i++;
                continue;
            }

            // -IREPLACE joined form (oracle: arg.Length > 2 && arg.StartsWith("-I")).
            if (arg.Length > 2 && arg.StartsWith("-I", System.StringComparison.Ordinal))
            {
                replaceStr = arg.Substring(2);
                i++;
                continue;
            }

            if (string.Equals(arg, "-n", System.StringComparison.Ordinal))
            {
                i++;
                if (i < args.Length && int.TryParse(args[i], out var n)) maxArgs = n;
                i++;
                continue;
            }

            if (arg.Length > 2 && arg.StartsWith("-n", System.StringComparison.Ordinal))
            {
                var tail = arg.Substring(2);
                if (int.TryParse(tail, out var n)) maxArgs = n;
                i++;
                continue;
            }

            if (string.Equals(arg, "-L", System.StringComparison.Ordinal))
            {
                i++;
                if (i < args.Length && int.TryParse(args[i], out var n)) maxLines = n;
                i++;
                continue;
            }

            if (arg.Length > 2 && arg.StartsWith("-L", System.StringComparison.Ordinal))
            {
                var tail = arg.Substring(2);
                if (int.TryParse(tail, out var n)) maxLines = n;
                i++;
                continue;
            }

            // -PN joined form for accepted-but-ignored parallel flag.
            if (arg.Length > 2 && arg.StartsWith("-P", System.StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            operands.Add(arg);
            i++;
        }

        if (operands.Count == 0)
        {
            FileSystemHelpers.WriteBashError(this, "xargs: no command specified");
            return;
        }

        // Resolve command: if the leading token matches an Invoke-Bash*
        // function, route through it (oracle parity).
        var cmd = operands[0];
        var bashCmdCandidate = "Invoke-Bash" +
            char.ToUpperInvariant(cmd[0]) + cmd.Substring(1);
        bool bashCmdExists = false;
        try
        {
            var probe = InvokeCommand.InvokeScript(
                "param($n) [bool](Get-Command $n -ErrorAction SilentlyContinue)",
                bashCmdCandidate);
            foreach (var p in probe)
            {
                var baseObj = p is PSObject pso ? pso.BaseObject : p;
                if (baseObj is bool b && b) { bashCmdExists = true; break; }
            }
        }
        catch
        {
            bashCmdExists = false;
        }
        if (bashCmdExists) cmd = bashCmdCandidate;

        var cmdArgs = new List<string>();
        for (int k = 1; k < operands.Count; k++) cmdArgs.Add(operands[k]);

        // Split pipeline input by delimiter (NUL or newline).
        var inputLines = new List<string>();
        var delim = nullDelim ? "\0" : "\n";
        foreach (var item in _pipelineItems)
        {
            var text = BashRuntime.GetBashText(item);
            // Strip trailing delimiter exactly once.
            if (text.Length > 0 && text.EndsWith(delim, System.StringComparison.Ordinal))
            {
                text = text.Substring(0, text.Length - delim.Length);
            }
            if (text.Length == 0) continue;
            if (text.Contains(delim))
            {
                foreach (var part in text.Split(new[] { delim }, System.StringSplitOptions.None))
                {
                    if (part.Length > 0) inputLines.Add(part);
                }
            }
            else
            {
                inputLines.Add(text);
            }
        }

        if (noRunIfEmpty && inputLines.Count == 0)
        {
            return;
        }

        // Batch dispatch.
        if (!string.IsNullOrEmpty(replaceStr))
        {
            // Replacement mode: one invocation per input line.
            foreach (var line in inputLines)
            {
                var replaced = new List<string>(cmdArgs.Count);
                foreach (var a in cmdArgs)
                {
                    replaced.Add(a.Replace(replaceStr, line));
                }
                InvokeOne(cmd, replaced, traceCmd);
            }
        }
        else
        {
            int batchSize = maxArgs > 0 ? maxArgs : (maxLines > 0 ? maxLines : 0);
            if (batchSize > 0 && inputLines.Count > 0)
            {
                for (int bi = 0; bi < inputLines.Count; bi += batchSize)
                {
                    var end = System.Math.Min(bi + batchSize, inputLines.Count);
                    var batchArgs = new List<string>(cmdArgs);
                    for (int j = bi; j < end; j++) batchArgs.Add(inputLines[j]);
                    InvokeOne(cmd, batchArgs, traceCmd);
                }
            }
            else
            {
                // Default: single invocation with all items appended.
                var allArgs = new List<string>(cmdArgs);
                allArgs.AddRange(inputLines);
                InvokeOne(cmd, allArgs, traceCmd);
            }
        }
    }

    /// <summary>
    /// Invokes <paramref name="cmd"/> with <paramref name="callArgs"/> bound
    /// positionally through <c>$args</c>. The script body is a fixed string
    /// (Directive 12). When <paramref name="trace"/> is true, writes the
    /// command + args to stderr first (xargs <c>-t</c>).
    /// </summary>
    private void InvokeOne(string cmd, List<string> callArgs, bool trace)
    {
        if (trace)
        {
            System.Console.Error.WriteLine(
                callArgs.Count == 0
                    ? cmd
                    : cmd + " " + string.Join(" ", callArgs));
        }

        // Build $args: [cmd, arg1, arg2, ...]. The body splats $args[1..] to
        // the command at $args[0]. No user-controlled string is ever embedded
        // in the body.
        const string invokeBody =
            "$c = $args[0]; $rest = @(); " +
            "if ($args.Count -gt 1) { $rest = $args[1..($args.Count - 1)] }; " +
            "& $c @rest";

        var allInvokeArgs = new object[callArgs.Count + 1];
        allInvokeArgs[0] = cmd;
        for (int j = 0; j < callArgs.Count; j++) allInvokeArgs[j + 1] = callArgs[j];

        try
        {
            var output = InvokeCommand.InvokeScript(invokeBody, allInvokeArgs);
            foreach (var o in output)
            {
                WriteObject(o);
            }
        }
        catch (System.Exception ex)
        {
            FileSystemHelpers.WriteBashError(this, ex.Message);
        }
    }
}
