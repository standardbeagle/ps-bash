using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// <c>exec N&lt;file</c>, <c>exec N&gt;file</c>, <c>exec N&gt;&gt;file</c>, <c>exec N&gt;&amp;M</c>, <c>exec N&lt;&amp;M</c>,
/// <c>exec N&lt;&amp;-</c> / <c>exec N&gt;&amp;-</c>: one redirection of a command-less <c>exec</c>, emitted by the
/// transpiler as <c>Invoke-BashExecFd N 'op' target</c>. Opens, duplicates or closes a numbered descriptor in
/// <see cref="BashFdTable"/>; exit status 1 with a bash-style message when it cannot.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashExecFd")]
public sealed class InvokeBashExecFdCommand : PSCmdlet
{
    [Parameter(Position = 0)]
    public int Descriptor { get; set; }

    [Parameter(Position = 1, Mandatory = true)]
    public string Operator { get; set; } = "";

    [Parameter(Position = 2)]
    public string? Target { get; set; }

    /// <summary>
    /// <c>exec {name}&gt;file</c>: the descriptor is the lowest free one from 10 up, and its number is stored in the
    /// variable <c>name</c>. For a close (<c>exec {name}&gt;&amp;-</c>) the descriptor is the number the variable holds.
    /// </summary>
    [Parameter]
    public string? Variable { get; set; }

    protected override void EndProcessing()
    {
        string? error = Variable is null ? Run(Descriptor) : RunNamed(Variable);
        if (error is not null)
        {
            FileSystemHelpers.WriteBashError(this, $"exec: {error}");
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }
        FileSystemHelpers.SetLastExitCode(this, 0);
    }

    private string? RunNamed(string name)
    {
        if (Operator is ">&" or "<&" && Target == "-")
        {
            // bash closes the descriptor the variable names; a value that is not a descriptor number is an error.
            string? held = BashVariableStore.Get(name);
            return int.TryParse(held, System.Globalization.NumberStyles.None, null, out int open) && open >= 0
                ? Run(open)
                : $"{name}: ambiguous redirect";
        }
        int fd = BashFdTable.LowestFree(10);
        string? error = Run(fd);
        if (error is null) BashVariableStore.Set(name, fd.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return error;
    }

    private string? Run(int fd)
    {
        if (Operator is ">&" or "<&")
        {
            if (Target == "-")
            {
                BashFdTable.Close(fd);
                return null;
            }
            return int.TryParse(Target, out var from)
                ? BashFdTable.Dup(fd, from)
                : $"{Target}: ambiguous redirect";
        }
        return BashFdTable.Open(fd, FileSystemHelpers.ProviderPath(this, Target ?? ""), Operator);
    }
}

/// <summary>Close every descriptor opened by <c>exec</c>: the host runs this when a new invocation starts.</summary>
[Cmdlet(VerbsCommon.Clear, "BashFd")]
public sealed class ClearBashFdCommand : PSCmdlet
{
    protected override void EndProcessing() => BashFdTable.CloseAll();
}

/// <summary><c>cmd &gt;&amp;N</c>: the command's output goes to descriptor N (a pipeline sink).</summary>
[Cmdlet(VerbsCommunications.Write, "BashFd")]
public sealed class WriteBashFdCommand : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public int Descriptor { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>The shell's own stdout (1) or stderr (2) when the table has no entry of that number (<c>&gt;&amp;$fd</c>
    /// with fd=2); 0 for a table descriptor.</summary>
    private int _std;
    private bool _bad;

    protected override void BeginProcessing()
    {
        if (Descriptor is 1 or 2 && !BashFdTable.IsOpen(Descriptor)) _std = Descriptor;
        else if (!BashFdTable.IsOpen(Descriptor))
        {
            // bash fails the redirection before the command writes anything: one message, status 1 (set last, so the
            // command's own status cannot overwrite it).
            _bad = true;
            FileSystemHelpers.WriteBashError(this, $"{Descriptor}: Bad file descriptor");
        }
    }

    protected override void ProcessRecord()
    {
        if (InputObject is null || _bad) return;
        var text = BashRuntime.GetBashText(InputObject);
        try
        {
            int std = _std != 0 ? _std : BashFdTable.Write(Descriptor, BashRuntime.IsUnterminated(InputObject) ? text : text + "\n");
            if (std == 1)
                WriteObject(InputObject);   // a duplicate of stdout: the record just continues down the pipeline
            else if (std == 2)
                InvokeCommand.InvokeScript("param($o) $o | Write-BashHostStderr", InputObject);
        }
        catch (InvalidOperationException ex)
        {
            FileSystemHelpers.WriteBashError(this, ex.Message);
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
    }

    protected override void EndProcessing()
    {
        if (_bad) FileSystemHelpers.SetLastExitCode(this, 1);
    }
}

/// <summary><c>cmd &lt;&amp;N</c>: the rest of descriptor N, one record per line, as the command's stdin.</summary>
[Cmdlet(VerbsCommunications.Read, "BashFd")]
public sealed class ReadBashFdCommand : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public int Descriptor { get; set; }

    protected override void EndProcessing()
    {
        try
        {
            string? line;
            while ((line = BashFdTable.ReadLine(Descriptor)) is not null)
                WriteObject(line);
        }
        catch (InvalidOperationException ex)
        {
            FileSystemHelpers.WriteBashError(this, ex.Message);
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
    }
}
