using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// <c>exec N&lt;file</c>, <c>exec N&gt;file</c>, <c>exec N&gt;&gt;file</c>, <c>exec N&gt;&amp;M</c>, <c>exec N&lt;&amp;M</c>,
/// <c>exec N&lt;&amp;-</c> / <c>exec N&gt;&amp;-</c>: one redirection of a command-less <c>exec</c>, emitted by the
/// transpiler as <c>Invoke-BashExecFd N 'op' target</c>. Opens, duplicates or closes a numbered descriptor in
/// <see cref="BashFdTable"/>; exit status 1 with a bash-style message when it cannot.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashExecFd")]
public sealed class InvokeBashExecFdCommand : BashCmdlet
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
            // bash reports a failed exec redirection as any failed redirection: `s.sh: line 3: /nodir/f: No such file
            // or directory`, no "exec:" (spec shell-fds-and-bash-source.md).
            FileSystemHelpers.WriteBashError(this, ShellDiagnostic.Prefix(SessionState) + error);
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
        // `{ ...; } {name}<<< word`: Target is the here-string's text.
        if (Operator == "<<<") return BashFdTable.OpenText(fd, Target ?? "");
        if (Operator is ">&" or "<&")
        {
            if (Target == "-")
            {
                BashFdTable.Close(fd);
                return null;
            }
            // `exec 4>&3-`: fd 3 moves to 4 (a dup, then 3 is closed).
            if (Target is { Length: > 1 } moved && moved[^1] == '-'
                && int.TryParse(moved.AsSpan(0, moved.Length - 1), System.Globalization.NumberStyles.None, null, out var movedFrom))
                return BashFdTable.Move(fd, movedFrom);
            return int.TryParse(Target, System.Globalization.NumberStyles.None, null, out var from)
                ? BashFdTable.Dup(fd, from)
                : $"{Target}: ambiguous redirect";
        }
        // `3>$v` with v='a b' (PsEmitter.FdOpenStep's stand-in): bash's "$v: ambiguous redirect", nothing opened.
        if (PsBash.Core.RedirectTarget.IsAmbiguous(Target, out string shown)) return $"{shown}: ambiguous redirect";
        return BashFdTable.Open(fd, FileSystemHelpers.ProviderPath(this, Target ?? ""), Operator);
    }
}

/// <summary>Close every descriptor opened by <c>exec</c>: the host runs this when a new invocation starts.</summary>
[Cmdlet(VerbsCommon.Clear, "BashFd")]
public sealed class ClearBashFdCommand : BashCmdlet
{
    protected override void EndProcessing() => BashFdTable.CloseAllShells();
}

/// <summary>What the given numbered descriptors name now (PsBuild.ScopedDescriptors), for <c>Restore-BashFd</c>.</summary>
[Cmdlet(VerbsData.Save, "BashFd")]
public sealed class SaveBashFdCommand : BashCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public int[] Descriptor { get; set; } = [];

    protected override void EndProcessing() => WriteObject(BashFdTable.SaveFds(Descriptor));
}

/// <summary>Put back the descriptors <c>Save-BashFd</c> saved: <c>cmd 3&gt;f</c> leaves fd 3 as it found it.</summary>
[Cmdlet(VerbsData.Restore, "BashFd")]
public sealed class RestoreBashFdCommand : BashCmdlet
{
    [Parameter(Position = 0)]
    public object? State { get; set; }

    protected override void EndProcessing()
    {
        if ((State is PSObject p ? p.BaseObject : State) is BashFdTable.Snapshot s) BashFdTable.RestoreFds(s);
    }
}

/// <summary><c>cmd &gt;&amp;N</c>: the command's output goes to descriptor N (a pipeline sink).</summary>
[Cmdlet(VerbsCommunications.Write, "BashFd")]
public sealed class WriteBashFdCommand : BashCmdlet
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
        // `>&1` / `>&2` (`>&$fd`): the command's own stdout / stderr, wherever its redirections and the table send them.
        if (Descriptor is 1 or 2) _std = Descriptor;
        else if (!BashFdTable.IsOpen(Descriptor))
        {
            // bash fails the redirection before the command writes anything: one message, status 1 (set last, so the
            // command's own status cannot overwrite it).
            _bad = true;
            FileSystemHelpers.WriteBashError(this, ShellDiagnostic.Prefix(SessionState) + $"{Descriptor}: Bad file descriptor");
        }
    }

    protected override void ProcessRecord()
    {
        if (InputObject is null || _bad) return;
        try
        {
            int std = _std != 0 ? _std : BashFdTable.Write(Descriptor, BashRuntime.RecordBytes(InputObject));
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
        // The command's writes are in the file once it ends, as bash's write(2)s are (BashFdTable.Flush).
        else if (_std == 0) BashFdTable.Flush(Descriptor);
    }
}

/// <summary>
/// The final fd 1 and fd 2 of a command whose redirections name a table descriptor, a closed descriptor, or swap the two
/// (<c>cmd 2&gt;&amp;3</c>, <c>e=$(cmd 2&gt;&amp;1 1&gt;&amp;3)</c>, <c>cmd 3&gt;&amp;1 1&gt;&amp;2 2&gt;&amp;3</c>, <c>echo hi &gt;&amp;-</c>),
/// resolved left to right by the emitter (PsEmitter.TryAppendModelledRedirectTail) and emitted as
/// <c>cmd 2&gt;&amp;1 | Invoke-BashFdRoute -Out X -Err Y</c>. Each of X / Y is <c>out</c> (the command's stdout as the
/// context has it), <c>err</c> (its stderr), <c>null</c>, <c>close</c> (a closed descriptor), <c>pass</c> (left for the
/// <c>Invoke-BashRedirect</c> stage after this one: a file) or a table descriptor number (<see cref="BashFdTable"/>).
/// Spec: docs/specs/shell-fds-and-bash-source.md section The descriptor table.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashFdRoute")]
public sealed class InvokeBashFdRouteCommand : BashCmdlet, IStatusTransparentStage
{
    [Parameter]
    public string Out { get; set; } = "out";

    [Parameter]
    public string Err { get; set; } = "err";

    /// <summary>The command's name, for bash's <c>echo: write error: Bad file descriptor</c> (a closed stdout).</summary>
    [Parameter]
    public string? Name { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private bool _failed;
    private bool _writeErrorReported;

    /// <summary>For <see cref="StderrMerge.JoinsStdout"/>: a program's stderr may share its stdout pipe only when both
    /// end at the same place.</summary>
    internal bool KeepsStderrWithStdout() => string.Equals(Out, Err, StringComparison.Ordinal);

    // A descriptor that is not open is not this stage's to report: bash dups every redirection before the command runs,
    // in order, and reports a failed one where fd 2 points at that point of the list (`echo y 2>/dev/null >&5` is
    // silent). The emitter puts that check in the plan of the Invoke-BashRedirect stage after this one (`-OpenPlan 'd5'`,
    // PsEmitter.TryAppendModelledRedirectTail), which stops the command before it runs. A write that still fails (the
    // descriptor closed while the command ran) is reported once by Route.

    protected override void ProcessRecord()
    {
        if (InputObject is null || _failed) return;
        if (InputObject.BaseObject is ErrorRecord er)
        {
            string text = er.ErrorDetails?.Message ?? er.Exception?.Message ?? er.ToString();
            Route(Err, PsBash.Core.StderrRecord.Payload(text, er.TargetObject), isError: true);
            return;
        }
        Route(Out, BashRuntime.RecordFilePayload(InputObject), isError: false);
    }

    private void Route(string dest, string payload, bool isError)
    {
        switch (dest)
        {
            case "out":
                if (isError) { foreach (var line in BashRuntime.EmitBashLines(payload)) WriteObject(line); }
                else WriteObject(InputObject);
                return;
            case "err":
                FileSystemHelpers.WriteStderrBytes(this, payload);
                return;
            case "pass":
                WriteObject(InputObject);
                return;
            case "null":
                return;
            case "close":
                // A write to a closed stdout fails: bash's builtin reports it once (where fd 2 points) and fails.
                if (isError || payload.Length == 0 || _writeErrorReported) return;
                _writeErrorReported = true;
                string who = string.IsNullOrEmpty(Name) ? "" : Name + ": ";
                Route(Err, ShellDiagnostic.Prefix(SessionState) + who + "write error: Bad file descriptor\n", isError: true);
                return;
        }
        if (!IsTableFd(dest, out int fd)) { WriteObject(InputObject); return; }
        try
        {
            int std = BashFdTable.Write(fd, payload);
            if (std == 1) Route("out", payload, isError);
            else if (std == 2) Route("err", payload, isError);
        }
        catch (InvalidOperationException ex)
        {
            if (!_failed) FileSystemHelpers.WriteStderr(this, ShellDiagnostic.Prefix(SessionState) + ex.Message);
            _failed = true;
        }
    }

    /// <summary>
    /// <c>exec &gt;&amp;-; echo x</c>: when a write by <paramref name="cmdlet"/> since <paramref name="closedWritesBefore"/>
    /// (<see cref="BashFdTable.ClosedStdoutWrites"/>) reached the shell's closed fd 1, the builtin's write failed: bash's
    /// <c>NAME: write error: Bad file descriptor</c> on stderr, once, and true (the caller's status is 1).
    /// </summary>
    internal static bool FailClosedStdout(PSCmdlet cmdlet, string name, int closedWritesBefore)
    {
        if (BashFdTable.ClosedStdoutWrites == closedWritesBefore) return false;
        FileSystemHelpers.WriteStderrBytes(cmdlet,
            ShellDiagnostic.Prefix(cmdlet.SessionState) + name + ": write error: Bad file descriptor\n");
        FileSystemHelpers.SetLastExitCode(cmdlet, 1);
        return true;
    }

    private static bool IsTableFd(string dest, out int fd)
        => int.TryParse(dest, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out fd);

    protected override void EndProcessing()
    {
        foreach (var dest in new[] { Out, Err })
            if (IsTableFd(dest, out int fd)) BashFdTable.Flush(fd);
        if (_failed || _writeErrorReported) FileSystemHelpers.SetLastExitCode(this, 1);
    }
}

/// <summary>
/// Status 0 when table descriptor N is open, else 1, silently: the check of a <c>cmd &lt;&amp;N</c> before the command
/// runs (PsBuild.DescriptorInputGuard), whose "N: Bad file descriptor" goes where fd 2 points at that point of the
/// redirect list (<c>cat 2&gt;/dev/null &lt;&amp;5</c> is silent, <c>cat &lt;&amp;5 2&gt;/dev/null</c> is not).
/// </summary>
[Cmdlet(VerbsDiagnostic.Test, "BashFd")]
public sealed class TestBashFdCommand : BashCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public int Descriptor { get; set; }

    protected override void EndProcessing()
        => FileSystemHelpers.SetLastExitCode(this, BashFdTable.IsOpen(Descriptor) ? 0 : 1);
}

/// <summary><c>cmd &lt;&amp;N</c>: the rest of descriptor N, one record per line, as the command's stdin.</summary>
[Cmdlet(VerbsCommunications.Read, "BashFd")]
public sealed class ReadBashFdCommand : BashCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public int Descriptor { get; set; }

    protected override void EndProcessing()
    {
        if (BashFdTable.ReadsShellStdin(Descriptor))
        {
            // `exec 5<&0; cmd <&5`: the command reads the shell's stdin - the shared one when a scope or the launcher
            // forwards it (its records, through the one take); else there is nothing here to forward.
            var stdin = SharedStdin.Get(SessionState);
            while (SharedStdin.TryTake(stdin, out var record)) WriteObject(record);
            return;
        }
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
