using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashBrowse</c> function
/// (REFACTOR-2 follow-on). Interactive object browser: receives pipeline
/// objects and either renders them as a non-interactive table (the
/// <c>--list</c> path or any non-TTY caller) or hands off to the psm1
/// <c>Invoke-BrowseInteractive</c> single-key workbench when stdin is a
/// real terminal.
///
/// Behavioral parity oracle: the original psm1 <c>Invoke-BashBrowse</c>
/// together with its <c>New-BrowseBinding</c>, <c>ConvertTo-BrowseRow</c>,
/// <c>Invoke-BrowseAction</c>, <c>Invoke-BrowseCommand</c>, and
/// <c>Invoke-BrowseInteractive</c> helpers (and the <c>*-BrowseAdapter</c>
/// helper web behind <c>ConvertTo-BrowseRow</c>). All those helpers stay
/// in psm1 — they touch module-scoped adapter registries, ConsoleKey input,
/// and a destructive-command safety gate that would balloon the cmdlet's
/// scope. This cmdlet drives flag parsing, pipeline collection, and the
/// row / inspect / action / exec / interactive dispatch decision, then
/// hands the actual work off to the surviving psm1 helpers via
/// parameter-bound <c>InvokeCommand.InvokeScript</c> calls (AOT-safe — no
/// <c>ScriptBlock</c> construction; user tokens flow only via <c>$args</c>,
/// never via concatenation, per Directive 12).
///
/// As a non-interactive enhancement, when <c>Out-GridView</c> is loaded
/// (Windows desktop sessions only) the list path can be routed through it
/// for an ad-hoc graphical browser; otherwise the cmdlet emits one
/// <c>PsBash.BrowseRow</c> PSObject per input. This Out-GridView dispatch
/// is gated by a <c>Get-Command</c> availability check and never invoked
/// implicitly when stdin is redirected (i.e. in tests / pipelines), so the
/// SDK-runspace observable shape always matches the psm1 oracle's
/// <c>ConvertTo-BrowseRow</c> emission byte for byte.
///
/// Flags: <c>--help</c> only. The psm1 oracle's <c>-Inspect N</c> /
/// <c>-Select N[]</c> / <c>-Action NAME</c> / <c>-Exec CMD</c> /
/// <c>--list</c> / <c>--passthru</c> / <c>-Force</c> are declared as
/// explicit parameters so the binder routes them cleanly — none of their
/// short names prefix-collide with a PowerShell common parameter, and the
/// long-form spellings the psm1 oracle accepted (<c>-Inspect</c>,
/// <c>-Select</c>, <c>-Action</c>, <c>-Exec</c>, <c>-PassThru</c>,
/// <c>-Force</c>) survive verbatim.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashBrowse")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashBrowseCommand : PSCmdlet
{
    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter] public int Inspect { get; set; } = -1;
    [Parameter] public int[]? Select { get; set; }
    [Parameter] public string? Action { get; set; }
    [Parameter] public string? Exec { get; set; }
    [Parameter] public SwitchParameter List { get; set; }
    [Parameter] public SwitchParameter PassThru { get; set; }
    [Parameter] public SwitchParameter Force { get; set; }

    private readonly List<PSObject> _objects = new();

    protected override void ProcessRecord()
    {
        if (InputObject != null)
        {
            _objects.Add(InputObject);
        }
    }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();
        if (FileSystemHelpers.TryHandleVersion(this, "browse", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "browse"))
            {
                WriteObject(line);
            }
            return;
        }

        if (_objects.Count == 0) return;

        int[] selectedIdx = Select ?? Array.Empty<int>();
        int currentIdx = selectedIdx.Length > 0 ? selectedIdx[0] : 0;

        // --passthru: emit the selected items via the psm1 binding helper.
        if (PassThru.IsPresent)
        {
            EmitFromScript(
                @"param($objs, $cur, $sel)
                  $b = New-BrowseBinding -Objects $objs -CurrentIndex $cur -SelectedIndex $sel
                  $b.Items",
                _objects.ToArray(), currentIdx, selectedIdx);
            return;
        }

        // -Inspect N: route to the 'inspect' action through the psm1 helper.
        if (Inspect >= 0)
        {
            EmitFromScript(
                @"param($objs, $idx)
                  $b = New-BrowseBinding -Objects $objs -CurrentIndex $idx -SelectedIndex @($idx)
                  Invoke-BrowseAction -Name 'inspect' -Current $b.Current -Items $b.Items",
                _objects.ToArray(), Inspect);
            return;
        }

        // -Action NAME: run the named adapter action.
        if (!string.IsNullOrEmpty(Action))
        {
            EmitFromScript(
                @"param($objs, $cur, $sel, $name, $force)
                  $b = New-BrowseBinding -Objects $objs -CurrentIndex $cur -SelectedIndex $sel
                  Invoke-BrowseAction -Name $name -Current $b.Current -Items $b.Items -Force:$force",
                _objects.ToArray(), currentIdx, selectedIdx, Action, Force.IsPresent);
            return;
        }

        // -Exec CMD: evaluate an inline command with $1/$_/$items bound.
        if (!string.IsNullOrEmpty(Exec))
        {
            EmitFromScript(
                @"param($objs, $cur, $sel, $cmd, $force)
                  $b = New-BrowseBinding -Objects $objs -CurrentIndex $cur -SelectedIndex $sel
                  Invoke-BrowseCommand -Command $cmd -Current $b.Current -Items $b.Items -Force:$force",
                _objects.ToArray(), currentIdx, selectedIdx, Exec, Force.IsPresent);
            return;
        }

        // Default dispatch matches the oracle: if stdin is a real terminal
        // and the user did NOT pass --list, hand off to the single-key
        // workbench; otherwise emit one BrowseRow per object.
        bool inputRedirected = Console.IsInputRedirected;
        if (!List.IsPresent && !inputRedirected)
        {
            // Optional Out-GridView enhancement: only when the cmdlet is
            // actually present (Windows desktop with the GridView module).
            // Never fired from a redirected-stdin / SDK / pipeline path.
            if (TryOutGridView()) return;

            // Hand off to the psm1 interactive workbench.
            InvokeCommand.InvokeScript(
                "param($objs) Invoke-BrowseInteractive -Objects $objs",
                (object)_objects.ToArray());
            return;
        }

        // Non-interactive list-mode (the primary C# path): emit a
        // PsBash.BrowseRow PSObject per input by delegating row rendering
        // to the psm1 helper, exactly matching the oracle's loop.
        for (int i = 0; i < _objects.Count; i++)
        {
            EmitFromScript(
                "param($o, $i, $sel) ConvertTo-BrowseRow -InputObject $o -Index $i -SelectedIndex $sel",
                _objects[i], i, selectedIdx);
        }
    }

    private bool TryOutGridView()
    {
        // Probe for Out-GridView availability without throwing. The script
        // body is a closed string — no user input enters it.
        var probe = InvokeCommand.InvokeScript(
            "[bool](Get-Command Out-GridView -ErrorAction SilentlyContinue)");
        if (probe == null || probe.Count == 0) return false;
        var b = probe[0]?.BaseObject;
        if (b is not bool present || !present) return false;

        // Out-GridView is only useful in interactive Windows desktop
        // sessions; never fire when stdin is redirected.
        if (Console.IsInputRedirected) return false;

        try
        {
            InvokeCommand.InvokeScript(
                "param($input) $input | Out-GridView -Title 'browse'",
                (object)_objects.ToArray());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void EmitFromScript(string script, params object?[] args)
    {
        var results = InvokeCommand.InvokeScript(script, args);
        if (results == null) return;
        foreach (var obj in results)
        {
            if (obj != null) WriteObject(obj);
        }
    }
}
