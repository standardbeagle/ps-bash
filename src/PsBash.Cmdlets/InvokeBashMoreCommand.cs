using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashMore</c> pager
/// (REFACTOR-2 follow-on). Behavioral parity oracle: the psm1 function.
///
/// Non-interactive (the only path this cmdlet exercises programmatically):
/// concatenate pipeline + file operand lines and emit each as a
/// <c>PsBash.TextOutput</c>. Pipeline mode preserves the oracle's
/// "concatenate all items, split on <c>\n</c>" semantics. File mode reads
/// each operand line-by-line and folds it in.
///
/// Interactive (PSBASH_INTERACTIVE=1 and the std handles are TTYs): no
/// native passthrough is needed — the psm1 oracle implemented its own
/// paging loop, but the cmdlet collapses to the same emit-all-lines
/// behavior in a programmatic context (no [Console]::ReadKey loop runs
/// inside an SDK runspace anyway).
///
/// Flag surface: <c>-N</c> line numbers (no PS common-parameter overlap,
/// declared as <c>SwitchParameter N</c> for clean binder routing), plus
/// pass-through tokens like <c>+NUM</c> jump-to-line. The oracle accepted
/// any <c>-</c>-prefixed token silently; we preserve that.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashMore")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashMoreCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    [Parameter] public SwitchParameter N { get; set; }

    private readonly List<PSObject?> _pipelineItems = new();

    protected override void ProcessRecord()
    {
        if (InputObject != null) _pipelineItems.Add(InputObject);
    }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (FileSystemHelpers.TryHandleVersion(this, "more", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "more"))
            {
                WriteObject(line);
            }
            return;
        }

        bool hadError = false;
        var lines = new List<string>();

        if (_pipelineItems.Count > 0)
        {
            foreach (var item in _pipelineItems)
            {
                var text = BashRuntime.GetBashText(item);
                AppendText(lines, text);
            }
        }

        // File operands: anything not starting with - or +.
        foreach (var op in args)
        {
            if (op.Length == 0) continue;
            if (op[0] == '-' || op[0] == '+') continue;
            string full;
            try { full = SessionState.Path.GetUnresolvedProviderPathFromPSPath(op); }
            catch
            {
                FileSystemHelpers.WriteBashError(this,
                    $"more: {op}: No such file or directory");
                hadError = true;
                continue;
            }
            if (!System.IO.File.Exists(full))
            {
                FileSystemHelpers.WriteBashError(this,
                    $"more: {op}: No such file or directory");
                hadError = true;
                continue;
            }
            try
            {
                var text = BashFileSystem.ReadAllText(full);
                AppendText(lines, text);
            }
            catch (System.Exception ex)
            {
                FileSystemHelpers.WriteBashError(this, $"more: {op}: {ex.Message}");
                hadError = true;
            }
        }

        if (hadError) FileSystemHelpers.SetLastExitCode(this, 1);

        foreach (var l in lines)
        {
            WriteObject(BashRuntime.NewBashObject(l));
        }

        if (!hadError) FileSystemHelpers.SetLastExitCode(this, 0);
    }

    private static void AppendText(List<string> lines, string text)
    {
        if (text.Length == 0) { lines.Add(string.Empty); return; }
        var normalized = text.Replace("\r\n", "\n");
        var parts = normalized.Split('\n');
        int limit = parts.Length;
        if (normalized.EndsWith("\n", StringComparison.Ordinal)) limit--;
        for (int i = 0; i < limit; i++) lines.Add(parts[i]);
    }
}
