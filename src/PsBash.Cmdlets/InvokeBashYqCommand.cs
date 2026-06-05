using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashYq</c> function
/// (REFACTOR-2 follow-on; paired with the F6 jq migration).
///
/// Behavioral parity oracle: the original psm1 <c>Invoke-BashYq</c> together
/// with its <c>ConvertFrom-SimpleYaml</c> / <c>ConvertFrom-YamlValue</c> /
/// <c>ConvertTo-SimpleYaml</c> YAML helpers and the
/// <c>Invoke-JqFilter</c> / <c>ConvertTo-JqJson</c> jq helpers. The YAML
/// parser/emitter and the jq helpers remain in psm1 (no .NET YAML lib in
/// stdlib; the jq helpers are kept for yq parity since the C# <c>JqEngine</c>
/// operates on a System.Text.Json graph rather than the psm1 hashtable graph
/// that <c>ConvertFrom-SimpleYaml</c> produces). This cmdlet drives flag
/// parsing, file / pipeline collection, and dispatches the per-document
/// parse + filter + emit loop to the surviving psm1 helpers via a single
/// parameter-bound <c>InvokeCommand.InvokeScript</c> call (AOT-safe — no
/// <c>ScriptBlock</c> construction; user tokens never concatenate into the
/// script body, per Directive 12).
///
/// Flags: <c>-r</c> raw-output, <c>-o FORMAT</c> (yaml|json; default json),
/// plus their long forms (<c>--raw-output</c>, <c>--output-format</c>). The
/// PowerShell common-parameter set has no <c>-r</c> / <c>-o</c> prefix
/// collision, so both stay in <see cref="Arguments"/> via
/// <c>ValueFromRemainingArguments</c> and a manual loop in
/// <see cref="EndProcessing"/> decodes them.
///
/// File-input errors emit through the psm1 <c>Write-BashError</c> sink via
/// <see cref="PSCmdlet.InvokeCommand"/>. Output is one <c>BashObject</c> per
/// filter result (matching the oracle's <c>New-BashObject</c> calls).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashYq")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashYqCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>
    /// Output format (json|yaml). Bare token <c>-o</c> prefix-matches the
    /// PowerShell common parameters <c>-OutVariable</c> / <c>-OutBuffer</c>
    /// under cmdlet binding; declared as an explicit value-bearing parameter
    /// with single-letter name so the binder routes the bare token by exact
    /// name match. The long form <c>--output-format</c> still flows through
    /// <see cref="Arguments"/> and is recovered post-parse.
    /// </summary>
    [Parameter]
    public string? O { get; set; }

    private readonly List<PSObject> _pipeline = new();

    protected override void ProcessRecord()
    {
        if (InputObject != null)
        {
            _pipeline.Add(InputObject);
        }
    }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "yq"))
            {
                WriteObject(line);
            }
            return;
        }

        bool rawOutput = false;
        // -o bound via the explicit O parameter (common-param prefix collision).
        string outputFormat = O ?? "json";
        string filterExpr = ".";
        bool filterSet = false;
        var files = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "-r" || arg == "--raw-output")
            {
                rawOutput = true;
                continue;
            }
            // -o is bound by the explicit parameter; only the long form
            // remains in Arguments.
            if (arg == "--output-format")
            {
                i++;
                if (i < args.Length) outputFormat = args[i];
                continue;
            }

            if (!filterSet)
            {
                filterExpr = arg;
                filterSet = true;
            }
            else
            {
                files.Add(arg);
            }
        }

        // Collect YAML input
        var yamlTexts = new List<string>();
        if (files.Count > 0)
        {
            foreach (var file in files)
            {
                string resolved;
                try
                {
                    resolved = SessionState.Path.GetUnresolvedProviderPathFromPSPath(file);
                }
                catch (Exception ex)
                {
                    EmitError($"yq: {file}: {ex.Message}");
                    return;
                }
                if (!File.Exists(resolved))
                {
                    EmitError($"yq: {file}: No such file or directory");
                    return;
                }
                try
                {
                    yamlTexts.Add(BashFileSystem.ReadAllTextRaw(resolved));
                }
                catch (Exception ex)
                {
                    EmitError($"yq: {file}: {ex.Message}");
                    return;
                }
            }
        }
        else
        {
            var textParts = new StringBuilder();
            foreach (var item in _pipeline)
            {
                string text = BashRuntime.GetBashText(item);
                textParts.Append(text);
                textParts.Append('\n');
            }
            string combined = textParts.ToString().Trim();
            if (combined.Length > 0)
            {
                yamlTexts.Add(combined);
            }
        }

        if (yamlTexts.Count == 0) return;

        // Dispatch the entire parse/filter/emit loop to psm1 — this preserves
        // byte-for-byte parity with the oracle since the helpers (and their
        // hashtable graph) live there. The script body is a closed string;
        // user tokens flow in only via $args, never via concatenation.
        const string emitScript = @"
param($yamlText, $filterExpr, $outputFormat, $rawOutput)
try {
    $parsed = ConvertFrom-SimpleYaml -Text $yamlText
} catch {
    Write-BashError -Message ""yq: parse error: $($_.Exception.Message)""
    return
}
$results = @(Invoke-JqFilter -Data $parsed -Filter $filterExpr)
foreach ($result in $results) {
    if ($outputFormat -eq 'yaml') {
        $text = ConvertTo-SimpleYaml -Data $result
    } else {
        $text = ConvertTo-JqJson -Value $result -Compact $false -SortKeys $false -RawOutput $rawOutput
    }
    New-BashObject -BashText $text
}
";

        foreach (var yamlText in yamlTexts)
        {
            var emitted = InvokeCommand.InvokeScript(
                emitScript,
                yamlText, filterExpr, outputFormat, rawOutput);
            if (emitted == null) continue;
            foreach (var obj in emitted)
            {
                if (obj != null) WriteObject(obj);
            }
        }
    }

    private void EmitError(string message)
    {
        FileSystemHelpers.WriteBashError(this, message);
    }
}
