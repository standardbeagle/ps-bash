using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet for <c>od</c> (GNU coreutils): dump bytes as octal / hex / decimal / characters.
/// <para>Supported: <c>-A RADIX</c> (o d x n), <c>-t TYPE</c> (a c d o u x with sizes 1 2 4 8, repeatable), the
/// traditional <c>-a -b -c -d -o -s -x -i -l</c>, <c>-j SKIP</c>, <c>-N COUNT</c>, <c>-w[BYTES]</c>, <c>-v</c>, and
/// their long forms. Input is the file operands in order, or stdin when there are none (or for <c>-</c>).</para>
/// <para>The formatting is <see cref="OdLineWriter"/>, fed AS THE BYTES ARRIVE (a byte filter streams:
/// <see cref="ByteFilterStreaming"/>): stdin records through <see cref="RecordByteEncoder"/> (the same stream
/// <c>md5sum</c> hashes) inside <c>ProcessRecord</c> when stdin is the first input, a file chunk by chunk. Only stdin
/// that follows a file operand (<c>od f -</c>) is held until its turn. The single-letter flags that collide with
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
    // Stdin is the first input (no operand, or `-` first): fed to the writer as it arrives.
    private bool _stdinFirst;
    // Stdin that comes after a file operand: held until its turn.
    private MemoryStream? _heldStdin;
    private RecordByteEncoder? _encoder;

    private OdPlan Planned()
    {
        if (_plan is not null) return _plan;
        _plan = OdFormat.Plan(Arguments ?? Array.Empty<string>());
        if (_plan.Error is null && !_plan.Help && !_plan.Version)
        {
            _writer = new OdLineWriter(_plan, line => WriteObject(BashRuntime.NewBashObject(line)));
            _stdinFirst = _plan.Operands.Count == 0 || _plan.Operands[0] == "-";
        }
        return _plan;
    }

    private void FeedStdin(ReadOnlySpan<byte> bytes)
    {
        if (_stdinFirst) _writer!.Append(bytes);
        else (_heldStdin ??= new MemoryStream()).Write(bytes);
    }

    protected override void ProcessRecord()
    {
        if (InputObject is null) return;
        Planned();
        if (_writer is null || (_stdinFirst && _writer.Done)) return;
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
        _encoder?.Finish(FeedStdin);
        bool hadError = false;
        // Stdin is read once: a second `-` finds it at end of input, as GNU's does.
        bool stdinUsed = _stdinFirst;
        for (int oi = _stdinFirst ? 1 : 0; oi < plan.Operands.Count; oi++)
        {
            string raw = plan.Operands[oi];
            if (writer.Done) break;
            if (raw == "-")
            {
                if (!stdinUsed && _heldStdin is not null) writer.Append(_heldStdin.GetBuffer().AsSpan(0, (int)_heldStdin.Length));
                stdinUsed = true;
                _heldStdin = null;
                continue;
            }
            foreach (var op in FileSystemHelpers.ResolveOperands(this, raw))
            {
                if (writer.Done) break;
                if (Directory.Exists(op.Path))
                {
                    FileSystemHelpers.WriteBashError(this, $"od: {op.Display}: read error: Is a directory");
                    hadError = true;
                    continue;
                }
                if (!File.Exists(op.Path) && !PsBash.Core.DevicePath.IsNull(op.Path)
                    && PsBash.Core.DevicePath.Classify(op.Path) is not (PsBash.Core.DeviceKind.Zero or PsBash.Core.DeviceKind.Random or PsBash.Core.DeviceKind.Full))
                {
                    FileSystemHelpers.WriteBashError(this, $"od: {op.Display}: No such file or directory");
                    hadError = true;
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
                    hadError = true;
                }
            }
        }

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
}
