using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashJq</c> function
/// (REFACTOR-2 Phase F6 follow-on; ported from the psm1 jq interpreter web).
///
/// Behavioral parity oracle: the original psm1 <c>Invoke-BashJq</c> together
/// with its <c>Invoke-JqFilter</c>, <c>ConvertTo-JqJson</c>, and the
/// <c>Split-Jq*</c> / <c>Find-Jq*</c> / <c>Get-JqMatchingBracket</c> /
/// <c>Resolve-Jq*</c> / <c>Invoke-Jq*</c> helper web. The filter engine is
/// reimplemented in <see cref="JqEngine"/>; this cmdlet drives flag parsing,
/// file / pipeline collection, JSON parsing, and slurp / output emission.
///
/// Flags: <c>-r</c> raw-output, <c>-c</c> compact, <c>-S</c> sort-keys,
/// <c>-s</c> slurp (also their long forms). The flags <c>-S</c> / <c>-s</c>
/// are case-sensitive in the bash oracle (they mean different things). <c>-c</c>
/// (compact) prefix-collides with the <c>-Confirm</c> common parameter and is
/// declared as the <see cref="C"/> decoy (an earlier audit wrongly called it
/// collision-free — a bare <c>-c</c> was silently bound to <c>-Confirm</c> and
/// compact output dropped). <c>-s</c> / <c>-S</c> / <c>-r</c> have no
/// common-parameter prefix, so <see cref="Arguments"/> via
/// <c>ValueFromRemainingArguments</c> captures them and the manual loop in
/// <see cref="EndProcessing"/> distinguishes case.
///
/// File-input errors emit through the psm1 <c>Write-BashError</c> sink via
/// <see cref="PSCmdlet.InvokeCommand"/> (string-bodied — no ScriptBlock
/// construction). Output is one <c>BashObject</c> per filter result (matching
/// the oracle's <c>New-BashObject</c> calls).
///
/// Yq dependency: the psm1 <c>Invoke-BashYq</c> still calls <c>Invoke-JqFilter</c>
/// and <c>ConvertTo-JqJson</c> directly. Those two psm1 helpers therefore
/// remain in place this phase as legacy shims for yq; their removal is filed
/// as a follow-on task. This cmdlet replaces only the <c>jq</c> command
/// surface.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashJq")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashJqCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>
    /// <c>-c</c> compact-output — declared explicitly because the bare token
    /// <c>-c</c> prefix-collides with the <c>-Confirm</c> common parameter and
    /// would otherwise be silently bound (the flag dropped, pretty output used)
    /// before reaching <see cref="Arguments"/>.
    /// </summary>
    [Parameter]
    public SwitchParameter C { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

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
                         "param($n) Show-BashHelp $n", "jq"))
            {
                WriteObject(line);
            }
            return;
        }

        bool rawOutput = false;
        bool compact = C.IsPresent;
        bool sortKeys = false;
        bool slurp = false;
        string filterExpr = ".";
        bool filterSet = false;
        var files = new List<string>();
        bool pastDoubleDash = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (pastDoubleDash)
            {
                files.Add(arg);
                continue;
            }
            if (arg == "--")
            {
                pastDoubleDash = true;
                continue;
            }
            // Case-sensitive: -S and -s differ.
            if (arg == "-r" || arg == "--raw-output") { rawOutput = true; continue; }
            if (arg == "-c" || arg == "--compact-output") { compact = true; continue; }
            if (arg == "-S" || arg == "--sort-keys") { sortKeys = true; continue; }
            if (arg == "-s" || arg == "--slurp") { slurp = true; continue; }

            // First non-flag = filter, rest = files.
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

        // Collect JSON input
        var jsonTexts = new List<string>();
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
                    EmitError($"jq: {file}: {ex.Message}");
                    SessionState.PSVariable.Set("global:LASTEXITCODE", 2);
                    return;
                }
                if (!File.Exists(resolved))
                {
                    EmitError($"jq: {file}: No such file or directory");
                    SessionState.PSVariable.Set("global:LASTEXITCODE", 2);
                    return;
                }
                try
                {
                    jsonTexts.Add(File.ReadAllText(resolved));
                }
                catch (Exception ex)
                {
                    EmitError($"jq: {file}: {ex.Message}");
                    SessionState.PSVariable.Set("global:LASTEXITCODE", 2);
                    return;
                }
            }
        }
        else
        {
            // Pipeline input — concat as oracle did.
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
                jsonTexts.Add(combined);
            }
        }

        if (jsonTexts.Count == 0) return;

        // Parse all inputs into the same nested-hashtable / array shape the
        // oracle used (ConvertFrom-Json -AsHashtable).
        var allData = new List<object?>();
        foreach (var jsonText in jsonTexts)
        {
            object? parsed;
            try
            {
                parsed = JqEngine.ParseJson(jsonText);
            }
            catch (Exception ex)
            {
                EmitError($"jq: parse error: {ex.Message}");
                SessionState.PSVariable.Set("global:LASTEXITCODE", 5);
                return;
            }
            allData.Add(parsed);
        }

        // Slurp = wrap all inputs into a single array value processed once.
        List<object?> dataStream;
        if (slurp)
        {
            dataStream = new List<object?> { allData.ToArray() };
        }
        else
        {
            dataStream = allData;
        }

        foreach (var data in dataStream)
        {
            List<object?> results;
            try
            {
                results = JqEngine.Evaluate(data, filterExpr, variables: null);
            }
            catch (JqEngine.JqException ex)
            {
                EmitError(ex.Message);
                SessionState.PSVariable.Set("global:LASTEXITCODE", 3);
                return;
            }
            foreach (var result in results)
            {
                string text = JqEngine.ToJson(result, compact, sortKeys, rawOutput);
                WriteObject(BashRuntime.NewBashObject(text + "\n"));
            }
        }
    }

    private void EmitError(string message)
    {
        FileSystemHelpers.WriteBashError(this, message);
    }
}
