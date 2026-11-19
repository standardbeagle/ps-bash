using System.Management.Automation;
using PsBash.Cmdlets.Args;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// GNU coreutils <c>shuf</c>: a random permutation of its input lines.
///
/// Input is one of:
/// <list type="bullet">
/// <item><b>Echo mode</b> (<c>-e</c>): every operand is an input line.</item>
/// <item><b>Range mode</b> (<c>-i LO-HI</c>): the integers <c>LO..HI</c> (<c>HI = LO-1</c> is empty).</item>
/// <item><b>File / pipeline</b> (default): the one FILE operand, or stdin when there is none or it is <c>-</c>.</item>
/// </list>
/// <c>-n COUNT</c> caps the output (the smallest of several wins, as in GNU); <c>-r</c> picks with replacement
/// (<c>COUNT</c> lines, or until the reader stops); <c>-o FILE</c> writes the output there after all input is read
/// (<c>shuf -o f f</c> works).
///
/// ARGV: the shared ordered parser (<see cref="ShufSpec"/>), so options after <c>-e</c> are options
/// (<c>shuf -r -n 2 -e a</c> prints two lines). REGRESSION: a hand scan took everything after <c>-e</c> up to the
/// next dash as a line and ignored <c>-r</c> / <c>-o</c>; <c>-o</c> failed in the binder as ambiguous
/// (<c>-OutVariable</c> / <c>-OutBuffer</c>). The transpiler passes every word quoted
/// (<c>PsEmitter.OrderedArgCommands</c>); the decoys below exist for DIRECT calls only and are put back in front
/// of the arguments. They are single letters so no other bash flag prefix-matches them (<c>-r</c> once bound a
/// parameter named <c>Range</c>).
///
/// Shuffle determinism: <see cref="Random"/> with no fixed seed; tests assert permutations, never an order.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashShuf")]
[OutputType(typeof(string))]
public sealed class InvokeBashShufCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Decoy for a direct <c>-e</c> (bare <c>-e</c> prefix-collides with <c>-ErrorAction</c>).</summary>
    [Parameter]
    public SwitchParameter e { get; set; }

    /// <summary>Decoy for a direct <c>-i LO-HI</c> (bare <c>-i</c> prefix-collides with <c>-InformationAction</c>).</summary>
    [Parameter]
    public string? i { get; set; }

    /// <summary>Decoy for a direct <c>-o FILE</c> (bare <c>-o</c> prefix-collides with <c>-OutVariable</c>).</summary>
    [Parameter]
    public string? o { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private const string OptEcho = "echo", OptRange = "input-range", OptCount = "head-count",
        OptOutput = "output", OptRepeat = "repeat";

    // (A string[] field on purpose: CommonParameterCollisionGuardTests enumerates cmdlets' static string sets.)
    private static readonly string[] ShufValidButUnsupported =
    {
        "-z", "--zero-terminated", "--random-source",
    };

    /// <summary>shuf's whole option surface, built once for the shared ordered parser.</summary>
    internal static readonly OptSpecSet ShufSpec = new(
        new[]
        {
            new OptSpec(OptEcho, 'e', "echo"),
            new OptSpec(OptRange, 'i', "input-range", OptKind.Value),
            new OptSpec(OptCount, 'n', "head-count", OptKind.Value),
            new OptSpec(OptOutput, 'o', "output", OptKind.Value),
            new OptSpec(OptRepeat, 'r', "repeat"),
        },
        validButUnsupported: ShufValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true);

    private readonly List<PSObject> _pipeline = new();

    // Records become LINES first: a piece without its newline (`printf a`) waits for the rest (LineAssembler).
    private readonly LineAssembler _lines = new();

    protected override void ProcessRecord()
    {
        if (_lines.Feed(InputObject) is { } record) _pipeline.Add(record);
    }

    protected override void EndProcessing()
    {
        if (_lines.Flush() is { } last) _pipeline.Add(last);

        var args = BashRuntime.PrependDecoys(Arguments, (e.IsPresent, "-e"));
        if (i is not null) args = Prepend(args, "-i", i);
        if (o is not null) args = Prepend(args, "-o", o);

        FileSystemHelpers.SetLastExitCode(this, 0);
        var parsed = ArgParser.Parse(args, ShufSpec);
        if (FileSystemHelpers.TryWriteParseError(this, "shuf", parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "shuf", parsed)) return;

        bool echo = parsed.Has(OptEcho);
        bool repeat = parsed.Has(OptRepeat);
        string? output = parsed.Last(OptOutput)?.Value;
        int ranges = 0;
        foreach (var _ in parsed.All(OptRange)) ranges++;
        if (ranges > 1) { Fail("shuf: multiple -i options specified", hint: true); return; }
        if (echo && ranges == 1) { Fail("shuf: cannot combine -e and -i options", hint: true); return; }

        long? count = null;
        foreach (var t in parsed.All(OptCount))
        {
            if (!long.TryParse(t.Value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                Fail($"shuf: invalid line count: {GnuQuote.Locale(t.Value ?? "")}", hint: false);
                return;
            }
            count = count is { } prev ? Math.Min(prev, n) : n;
        }

        var operands = parsed.Operands();
        var items = new List<string>();
        // Pipeline mode only: the upstream record behind each item (null = split piece or
        // non-pipeline source). shuf is a FILTER — each output line is one of its input lines.
        var origins = new List<object?>();

        if (echo)
        {
            items.AddRange(operands);
        }
        else if (parsed.Last(OptRange)?.Value is { } range)
        {
            if (operands.Count > 0) { FileSystemHelpers.WriteExtraOperand(this, "shuf", operands[0]); return; }
            if (!TryParseRange(range, out long lo, out long hi))
            {
                Fail($"shuf: invalid input range: {GnuQuote.Locale(range)}", hint: false);
                return;
            }
            for (long n = lo; n <= hi; n++) items.Add(n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        else if (operands.Count > 1)
        {
            FileSystemHelpers.WriteExtraOperand(this, "shuf", operands[1]);
            return;
        }
        else if (operands.Count == 1 && operands[0] != "-")
        {
            string inputFile = operands[0];
            foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, inputFile))
            {
                try
                {
                    foreach (var line in BashFileSystem.ReadLines(filePath)) items.Add(line);
                }
                catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
                {
                    // GNU: `shuf: NAME: No such file or directory`, status 1, nothing shuffled.
                    FileSystemHelpers.WriteBashError(this,
                        $"shuf: {GnuQuote.IfNeeded(inputFile)}: {FileSystemHelpers.ReadErrorMessage(ex, filePath)}");
                    return;
                }
            }
        }
        else
        {
            foreach (var obj in _pipeline)
            {
                var pieces = BashRuntime.SplitRecord(obj, out _);
                if (pieces.Length == 1)
                {
                    items.Add(BashRuntime.GetBashText(obj));
                    origins.Add(obj);
                }
                else
                {
                    // A multi-line record (printf 'a\nb') is several lines to shuffle.
                    foreach (var piece in pieces)
                    {
                        items.Add(piece);
                        origins.Add(null);
                    }
                }
            }
        }
        while (origins.Count < items.Count) origins.Add(null);

        if (repeat && items.Count == 0 && count != 0)
        {
            Fail("shuf: no lines to repeat", hint: false);
            return;
        }

        RedirectFileWriter? sink = null;
        if (output is not null)
        {
            try { sink = RedirectFileWriter.OpenFile(FileSystemHelpers.ProviderPath(this, output), append: false, this); }
            catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
            {
                Fail($"shuf: {GnuQuote.IfNeeded(output)}: {FileSystemHelpers.ReadErrorMessage(ex)}", hint: false);
                return;
            }
        }

        try
        {
            var rng = new Random();
            if (repeat)
            {
                // With replacement: COUNT picks, or until the reader stops (`shuf -r -e a | head -3`).
                for (long k = 0; (count is null || k < count) && !Stopping; k++)
                {
                    int pick = rng.Next(items.Count);
                    Emit(sink, items[pick], origins[pick]);
                }
                return;
            }

            // Fisher-Yates.
            for (int k = items.Count - 1; k > 0; k--)
            {
                int swap = rng.Next(k + 1);
                (items[k], items[swap]) = (items[swap], items[k]);
                (origins[k], origins[swap]) = (origins[swap], origins[k]);
            }
            long emitCount = count is { } c && c < items.Count ? c : items.Count;
            for (int k = 0; k < emitCount; k++) Emit(sink, items[k], origins[k]);
        }
        finally
        {
            sink?.Dispose();
        }
    }

    private void Emit(RedirectFileWriter? sink, string item, object? origin)
    {
        if (sink is not null)
        {
            sink.Write(RawBytes.GetBytes(item + "\n"));
            return;
        }
        // shuf terminates every output line, so a stale missing-newline flag is stripped.
        WriteObject(origin is not null ? BashRuntime.PassTerminated(origin) : BashRuntime.NewBashObject(item));
    }

    private void Fail(string message, bool hint)
    {
        if (hint && FileSystemHelpers.UsageHint("shuf") is { } h) message += "\n" + h;
        FileSystemHelpers.WriteBashError(this, message);
        FileSystemHelpers.SetLastExitCode(this, 1);
    }

    private static string[] Prepend(string[] args, string flag, string value)
    {
        var list = new List<string>(args.Length + 2) { flag, value };
        list.AddRange(args);
        return list.ToArray();
    }

    /// <summary>GNU's <c>-i LO-HI</c>: two unsigned numbers, <c>HI</c> at least <c>LO-1</c>. Pure.</summary>
    internal static bool TryParseRange(string range, out long lo, out long hi)
    {
        lo = hi = 0;
        int dash = range.IndexOf('-');
        if (dash <= 0) return false;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        const System.Globalization.NumberStyles digits = System.Globalization.NumberStyles.None;
        return long.TryParse(range.AsSpan(0, dash), digits, inv, out lo)
            && long.TryParse(range.AsSpan(dash + 1), digits, inv, out hi)
            && hi >= lo - 1;
    }
}
