using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet for <c>od</c> (GNU coreutils): dump bytes as octal / hex / decimal / characters.
/// <para>Supported: <c>-A RADIX</c> (o d x n), <c>-t TYPE</c> (a c d o u x with sizes 1 2 4 8, repeatable), the
/// traditional <c>-a -b -c -d -o -s -x -i -l</c>, <c>-j SKIP</c>, <c>-N COUNT</c>, <c>-w[BYTES]</c>, <c>-v</c>, and
/// their long forms. Input is the file operands in order, or stdin when there are none (or for <c>-</c>).</para>
/// <para>The work is in <see cref="OdFormat"/> (pure). Stdin records are turned back into their exact bytes by
/// <see cref="RecordByteEncoder"/>, the same stream <c>md5sum</c> hashes. The single-letter flags that collide with
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

    private readonly MemoryStream _stdin = new();
    private RecordByteEncoder? _encoder;

    private void AppendStdin(ReadOnlySpan<byte> bytes) => _stdin.Write(bytes);

    protected override void ProcessRecord()
    {
        if (InputObject is null) return;
        (_encoder ??= new RecordByteEncoder()).Encode(InputObject, AppendStdin);
    }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();
        FileSystemHelpers.SetLastExitCode(this, 0);
        var plan = OdFormat.Plan(args);
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

        _encoder?.Finish(AppendStdin);
        bool hadError = false;
        using var all = new MemoryStream();
        if (plan.Operands.Count == 0) _stdin.WriteTo(all);
        foreach (var raw in plan.Operands)
        {
            if (raw == "-") { _stdin.WriteTo(all); continue; }
            foreach (var op in FileSystemHelpers.ResolveOperands(this, raw))
            {
                if (Directory.Exists(op.Path))
                {
                    FileSystemHelpers.WriteBashError(this, $"od: {op.Display}: read error: Is a directory");
                    hadError = true;
                    continue;
                }
                if (!File.Exists(op.Path))
                {
                    FileSystemHelpers.WriteBashError(this, $"od: {op.Display}: No such file or directory");
                    hadError = true;
                    continue;
                }
                try
                {
                    using var s = BashFileSystem.OpenRead(op.Path);
                    s.CopyTo(all);
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    FileSystemHelpers.WriteBashError(this, $"od: {op.Display}: {ex.Message}");
                    hadError = true;
                }
            }
        }

        byte[] data = all.ToArray();
        // Nothing could be read at all: GNU prints only the errors, not an address line.
        if (hadError && data.Length == 0)
        {
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }
        if (plan.Skip > data.Length)
        {
            FileSystemHelpers.WriteBashError(this, "od: cannot skip past end of combined input");
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }
        long start = plan.Skip;
        long count = data.Length - start;
        if (plan.Limit >= 0 && plan.Limit < count) count = plan.Limit;
        byte[] window = start == 0 && count == data.Length ? data : data.AsSpan((int)start, (int)count).ToArray();

        foreach (var line in OdFormat.Lines(plan, window, start))
            WriteObject(BashRuntime.NewBashObject(line));
        if (hadError) FileSystemHelpers.SetLastExitCode(this, 1);
    }
}
