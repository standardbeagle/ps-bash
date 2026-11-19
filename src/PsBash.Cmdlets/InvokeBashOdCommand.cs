using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet for <c>od</c> (GNU coreutils): dump bytes as octal / hex / decimal / characters.
/// <para>Supported: <c>-A RADIX</c> (o d x n), <c>-t TYPE</c> (a c d o u x with sizes 1 2 4 8, repeatable), the
/// traditional <c>-a -b -c -d -o -s -x -i -l</c>, <c>-j SKIP</c>, <c>-N COUNT</c>, <c>-w[BYTES]</c>, <c>-v</c>, and
/// their long forms. Input is the file operands in order, or stdin when there are none (or for <c>-</c>).</para>
/// <para>The formatting is <see cref="OdLineWriter"/>, fed AS THE BYTES ARRIVE (a byte filter streams:
/// <see cref="ByteFilterStreaming"/>): stdin records through <see cref="RecordByteEncoder"/> (the same stream
/// <c>md5sum</c> hashes) inside <c>ProcessRecord</c>, a file chunk by chunk. The files before the first <c>-</c>
/// (<c>od f -</c>) are dumped in <c>BeginProcessing</c>, before any record arrives, so stdin streams in every operand
/// order and is never held; with no <c>-</c> among the operands stdin is not read. The single-letter flags that collide with
/// PowerShell common parameters (<c>-a -c -d -o -v -w</c>) reach the cmdlet quoted from the transpiler.</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashOd")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashOdCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private OdPlan? _plan;
    private OdLineWriter? _writer;
    // Stdin is read (no operand, or a `-` operand): fed to the writer as it arrives, its turn having come already.
    private bool _stdinRead;
    // The operand EndProcessing resumes at: the one after the first `-` (the files before it were dumped first).
    private int _nextOperand;
    private bool _hadError;
    private RecordByteEncoder? _encoder;

    private OdPlan Planned()
    {
        if (_plan is not null) return _plan;
        _plan = OdFormat.Plan(Arguments ?? Array.Empty<string>());
        if (_plan.Error is null && !_plan.Help && !_plan.Version)
        {
            _writer = new OdLineWriter(_plan, line => WriteObject(BashRuntime.NewBashObject(line)));
            int dash = _plan.Operands.IndexOf("-");
            _stdinRead = _plan.Operands.Count == 0 || dash >= 0;
            _nextOperand = dash + 1;
        }
        return _plan;
    }

    private void FeedStdin(ReadOnlySpan<byte> bytes) => _writer!.Append(bytes);

    protected override void BeginProcessing()
    {
        // `od f -`: f comes before stdin, so it is dumped now, before the first record; stdin then streams in
        // ProcessRecord exactly as when it is the first input (it used to be held in memory until f was done).
        Planned();
        if (_writer is null || _nextOperand <= 1) return;
        for (int oi = 0; oi < _nextOperand - 1 && !_writer.Done; oi++) DumpOperand(_plan!.Operands[oi]);
    }

    protected override void ProcessRecord()
    {
        if (InputObject is null) return;
        Planned();
        if (_writer is null || !_stdinRead || _writer.Done) return;
        (_encoder ??= new RecordByteEncoder()).Encode(InputObject, FeedStdin);
    }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();
        FileSystemHelpers.SetLastExitCode(this, 0);
        var plan = Planned();
        if (plan.Error is { } error)
        {
            FileSystemHelpers.WriteBashError(this, error);
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }
        if (plan.Version && FileSystemHelpers.TryHandleVersion(this, "od", args)) return;
        if (plan.Help)
        {
            foreach (var line in InvokeCommand.InvokeScript("param($n) Show-BashHelp $n", "od")) WriteObject(line);
            return;
        }

        var writer = _writer!;
        if (_stdinRead) _encoder?.Finish(FeedStdin);
        // Stdin is read once: a later `-` finds it at end of input, as GNU's does.
        for (int oi = _nextOperand; oi < plan.Operands.Count && !writer.Done; oi++)
        {
            if (plan.Operands[oi] != "-") DumpOperand(plan.Operands[oi]);
        }
        bool hadError = _hadError;

        // Nothing could be read at all: GNU prints only the errors, not an address line.
        if (hadError && writer.InputBytes == 0)
        {
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }
        if (writer.SkipPastEnd)
        {
            FileSystemHelpers.WriteBashError(this, "od: cannot skip past end of combined input");
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }
        writer.Finish();
        if (hadError) FileSystemHelpers.SetLastExitCode(this, 1);
    }

    /// <summary>One file operand (globbed) into the writer, chunk by chunk; a missing file or a directory is reported.</summary>
    private void DumpOperand(string raw)
    {
        var writer = _writer!;
        foreach (var op in FileSystemHelpers.ResolveOperands(this, raw))
        {
            if (writer.Done) break;
            if (Directory.Exists(op.Path))
            {
                FileSystemHelpers.WriteBashError(this, $"od: {op.Display}: read error: Is a directory");
                _hadError = true;
                continue;
            }
            if (!File.Exists(op.Path) && !PsBash.Core.DevicePath.IsNull(op.Path)
                && PsBash.Core.DevicePath.Classify(op.Path) is not (PsBash.Core.DeviceKind.Zero or PsBash.Core.DeviceKind.Random or PsBash.Core.DeviceKind.Full))
            {
                FileSystemHelpers.WriteBashError(this, $"od: {op.Display}: No such file or directory");
                _hadError = true;
                continue;
            }
            try
            {
                // An endless device (/dev/zero, /dev/urandom) is read as far as -N or a downstream stop asks.
                using var s = BashFileSystem.OpenRead(op.Path);
                ByteFilterStreaming.Pump(s, this, writer.Append, () => writer.Done);
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                FileSystemHelpers.WriteBashError(this, $"od: {op.Display}: {ex.Message}");
                _hadError = true;
            }
        }
    }
}
