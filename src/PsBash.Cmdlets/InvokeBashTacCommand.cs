using System.Management.Automation;
using PsBash.Cmdlets.Args;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashTac</c> function
/// (REFACTOR-2 follow-on). Reverses the list of input lines, matching the
/// GNU coreutils <c>tac</c> command (reverse cat).
///
/// Behavioral parity oracle: the original psm1 function. <c>tac</c>'s flag
/// surface is <c>-s SEP</c> / <c>--separator=SEP</c> (custom record separator)
/// plus <c>--help</c>. The cmdlet reproduces its two-path structure:
/// <list type="bullet">
/// <item><b>Pipeline mode</b> — when there are no operands and pipeline input
/// is present, each pipeline item's <c>BashText</c> is trimmed of trailing
/// newlines and split on <c>\n</c>; the resulting line list is accumulated.</item>
/// <item><b>File mode</b> — otherwise the operands are treated as file paths
/// (glob-expanded via <see cref="FileSystemHelpers.ResolveOperandPaths"/>).
/// Each file is streamed with CRLF normalization and split into lines (no
/// trailing newline carried per line — matching
/// <c>StreamReader.ReadLine()</c>).</item>
/// </list>
/// After collection: when <c>-s SEP</c> is set, the lines are joined with
/// <c>\n</c>, split on <c>SEP</c>, the chunks reversed, and each emitted;
/// otherwise the lines themselves are reversed and emitted.
///
/// Output: each chunk/line is emitted via
/// <see cref="BashRuntime.NewBashObject(string)"/> — the same default
/// <c>PsBash.TextOutput</c> shape the psm1 oracle produced via
/// <c>New-BashObject -BashText</c>.
///
/// No PowerShell common-parameter prefix collision: <c>-s</c> has no
/// colliding common parameter (no <c>-S*</c> common params), so it stays in
/// <see cref="Arguments"/> and is parsed by the manual value-flag scan.
///
/// On a file-read failure the cmdlet emits a bash-style error through
/// <see cref="FileSystemHelpers.WriteBashError"/> and sets
/// <c>$global:LASTEXITCODE = 1</c>, matching the oracle's behavior for missing
/// targets.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashTac")]
[OutputType(typeof(string))]
public sealed class InvokeBashTacCommand : BashCmdlet
{
    /// <summary>
    /// Valid GNU <c>tac</c> options ps-bash does not implement, refused loudly (exit 2) by the
    /// shared parser: <c>-r/--regex</c> (regex separator) and <c>-b/--before</c> (attach the
    /// separator before). (A string[] on purpose: CommonParameterCollisionGuardTests enumerates
    /// static string sets.)
    /// </summary>
    private static readonly string[] TacValidButUnsupported =
    {
        "-r", "--regex",
        "-b", "--before",
    };

    private const string OptSeparator = "separator";

    /// <summary>
    /// tac's option surface (GNU coreutils 9.4: -b -r -s + --before --regex --separator=STRING).
    /// Built once for the shared ordered parser.
    /// </summary>
    private static readonly OptSpecSet TacSpec = new(
        new[]
        {
            new OptSpec(OptSeparator, 's', "separator", OptKind.Value),
        },
        validButUnsupported: TacValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true);

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, TacSpec);

    /// <summary>The resolved meaning of a tac argv (shared by the cmdlet and the fused core).</summary>
    internal sealed class TacArgs
    {
        public ParsedArgs Parsed = null!;
        /// <summary>The last <c>-s</c> / <c>--separator</c> value, or null.</summary>
        public string? Separator;
        public List<string> Operands = new();

        /// <summary>True when nothing further should execute: scan error or --help/--version.</summary>
        public bool Declined =>
            Parsed.HasError || Parsed.Has(OptSpecSet.HelpId) || Parsed.Has(OptSpecSet.VersionId);
    }

    /// <summary>Scan + resolve (last -s wins, as in getopt).</summary>
    internal static TacArgs Plan(string[] args)
    {
        var p = ScanArgs(args);
        return new TacArgs
        {
            Parsed = p,
            Separator = p.Last(OptSeparator)?.Value,
            Operands = p.Operands(),
        };
    }
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _pipeline = new();

    // Records become LINES first: a piece without its newline (`printf a`) waits for the rest (LineAssembler).
    private readonly LineAssembler _lines = new();

    protected override void ProcessRecord()
    {
        if (_lines.Feed(InputObject) is { } record) { InputObject = record; ProcessInputRecord(); }
    }

    private void ProcessInputRecord()
    {
        if (InputObject != null)
        {
            _pipeline.Add(InputObject);
        }
    }

    protected override void EndProcessing()
    {
        if (_lines.Flush() is { } last) { InputObject = last; ProcessInputRecord(); }
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "tac", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "tac"))
            {
                WriteObject(line);
            }
            return;
        }

        // Shared ordered parser: attached values (-sX), --separator=X / abbreviations (--sep X),
        // `--`, options after operands, and the unsupported/unknown classifier in ONE scan. (A
        // dangling -s used to become a file operand.)
        var plan = Plan(args);
        if (FileSystemHelpers.TryWriteParseError(this, "tac", plan.Parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "tac", plan.Parsed)) return;
        string? separator = plan.Separator;
        var operands = plan.Operands;
        var lines = new List<string>();
        // Pipeline mode only: the records themselves, so the default-separator path can emit
        // the ORIGINAL objects reversed (GNU: a missing final newline stays glued to that
        // record, so `printf 'b\na' | tac` is `ab\n` — the flagged record just comes first).
        var pipelineRecords = new List<object>();
        bool hadError = false;

        if (operands.Count == 0 && _pipeline.Count > 0)
        {
            // Pipeline mode — trim trailing newlines, split on \n, accumulate.
            foreach (var item in _pipeline)
            {
                var pieces = BashRuntime.SplitRecord(item, out bool unterminated);
                if (pieces.Length == 1)
                {
                    lines.Add(pieces[0]);
                    // tac is a FILTER: a single-line record is emitted as itself.
                    pipelineRecords.Add(item);
                    continue;
                }
                for (int p = 0; p < pieces.Length; p++)
                {
                    lines.Add(pieces[p]);
                    pipelineRecords.Add(BashRuntime.TextRecord(
                        pieces[p], unterminated && p == pieces.Length - 1));
                }
            }
        }
        else if (separator == null)
        {
            // File mode — read each operand (glob-expanded), split on \n.
            foreach (var raw in operands)
            {
                foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
                {
                    try
                    {
                        foreach (var l in BashFileSystem.ReadLines(filePath))
                        {
                            lines.Add(l);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                        WriteReadError(filePath, ex);
                        hadError = true;
                    }
                }
            }
        }

        if (separator != null)
        {
            // -s SEP: records END with SEP (attached after), each source is reversed on its own and written as
            // exact bytes — `printf 'a,b,c' | tac -s ,` is `cb,a,`, the unterminated last record first.
            var sources = new List<string>();
            if (operands.Count == 0) sources.Add(BashRuntime.RecordStreamText(_pipeline));
            else
            {
                foreach (var raw in operands)
                    foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
                    {
                        try { sources.Add(BashFileSystem.ReadAllText(filePath)); }
                        catch (Exception ex)
                        {
                            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                            WriteReadError(filePath, ex);
                            hadError = true;
                        }
                    }
            }
            foreach (var text in sources)
            {
                string reversed = ReverseRecords(text, separator);
                if (reversed.Length > 0) WriteObject(BashRuntime.BytesRecord(reversed));
            }
        }        else if (pipelineRecords.Count > 0)
        {
            for (int r = pipelineRecords.Count - 1; r >= 0; r--)
                WriteObject(pipelineRecords[r]);
        }
        else
        {
            lines.Reverse();
            foreach (var line in lines)
            {
                WriteObject(BashRuntime.NewBashObject(line));
            }
        }

        if (hadError)
        {
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
    }

    private void WriteReadError(string path, Exception ex)
    {
        // GNU: tac: failed to open 'x' for reading: No such file or directory
        FileSystemHelpers.WriteBashError(this,
            $"tac: failed to open '{BashPath.ToShell(path)}' for reading: {FileSystemHelpers.ReadErrorMessage(ex)}");
    }

    /// <summary>
    /// GNU <c>tac -s SEP</c> over one source: split into records that each END with <paramref name="sep"/> (the
    /// last may lack it), reverse their order, concatenate exactly. An empty SEP matches nothing: one record.
    /// </summary>
    internal static string ReverseRecords(string text, string sep)
    {
        if (sep.Length == 0 || text.Length == 0) return text;
        var records = new List<string>();
        int start = 0;
        while (start < text.Length)
        {
            int hit = text.IndexOf(sep, start, StringComparison.Ordinal);
            int end = hit < 0 ? text.Length : hit + sep.Length;
            records.Add(text.Substring(start, end - start));
            start = end;
        }
        records.Reverse();
        return string.Concat(records);
    }
}