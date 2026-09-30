using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashRedirect</c> — the
/// runtime target the emitter pipes stdout into for <c>&gt; file</c> / <c>&gt;&gt; file</c>
/// redirects (<c>... | Invoke-BashRedirect -Path file [-Append]</c>). It collects
/// the pipeline's BashText, joins the lines with <c>\n</c> (one trailing newline
/// when non-empty), and writes or appends to the file. A <c>$null</c> path
/// (e.g. <c>&gt; /dev/null</c>) is a no-op, matching the psm1.
///
/// <c>-Path</c> / <c>-Append</c> are emitter-generated PowerShell-style flags, not
/// bash flags, so they bind directly with no collision. The file path resolves
/// against the process working directory, which the emitted <c>cd</c> keeps in
/// sync with the shell cwd — byte-identical to the psm1's File.WriteAllText.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashRedirect")]
public sealed class InvokeBashRedirectCommand : PSCmdlet
{
    [Parameter(Position = 0)]
    public string? Path { get; set; }

    [Parameter]
    public SwitchParameter Append { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly System.Text.StringBuilder _content = new();

    protected override void ProcessRecord()
    {
        if (InputObject is null) return;
        // Same per-record rule as tee: record boundary "\n" unless the record is marked
        // NoTrailingNewline (printf x / echo -n x), so `printf x > f` writes exactly "x".
        _content.Append(BashRuntime.RecordFilePayload(InputObject));
    }

    protected override void EndProcessing()
    {
        if (Path is null) return;

        var content = _content.ToString();
        if (Append) File.AppendAllText(Path, content);
        else File.WriteAllText(Path, content);
    }
}
