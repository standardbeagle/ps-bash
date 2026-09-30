using System.Linq;
using System.Management.Automation;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashNl</c> function
/// (REFACTOR-2 follow-on). Numbers input lines, GNU coreutils <c>nl</c>-style:
/// each numbered line is rendered as <c>"{N,6}\t{LINE}"</c> (6-column
/// right-aligned line number, tab, then the line). By default empty lines
/// are emitted unnumbered (a bare empty line); <c>-ba</c> (also accepted as
/// <c>-b a</c>) numbers every line including empty lines.
///
/// Behavioral parity oracle: the original psm1 <c>Invoke-BashNl</c> function.
/// The cmdlet reproduces its two-path structure:
/// <list type="bullet">
/// <item><b>Pipeline mode</b> — when there are no file operands and pipeline
/// input is present, each pipeline item's <c>BashText</c> is split on <c>\n</c>
/// after trailing-newline trim; each resulting sub-line is collected into the
/// line buffer.</item>
/// <item><b>File mode</b> — otherwise the operands are treated as file paths
/// (glob-expanded via <see cref="FileSystemHelpers.ResolveOperandPaths"/>).
/// Each file is read with CRLF normalization and split into lines using
/// <c>StreamReader.ReadLine()</c> semantics (no spurious trailing empty
/// line).</item>
/// </list>
///
/// Output: each line is emitted via <see cref="BashRuntime.NewBashObject(string)"/>
/// — the default <c>PsBash.TextOutput</c> shape the psm1 oracle produced via
/// <c>New-BashObject -BashText</c>.
///
/// Flag binding: <c>-ba</c> is a compound short flag (no PowerShell common-
/// parameter prefix collision — case-insensitive <c>-ba</c> does not abbreviate
/// any of <c>-Verbose</c> / <c>-Debug</c> / <c>-Confirm</c> / <c>-WhatIf</c> /
/// <c>-Error*</c> / <c>-Warning*</c> / <c>-Information*</c> / <c>-Out*</c> /
/// <c>-Progress*</c> / <c>-PipelineVariable</c>). The bare token therefore
/// stays in <see cref="Arguments"/> and is parsed by the manual scan, matching
/// the psm1 oracle byte-for-byte. The oracle also accepts the split form
/// <c>-b a</c> (two consecutive tokens) — preserved here.
///
/// On a file-read failure the cmdlet emits a bash-style error through
/// <see cref="FileSystemHelpers.WriteBashError"/> and continues with the next
/// operand. The psm1 oracle did not set <c>$LASTEXITCODE = 1</c> for nl (the
/// internal <c>Read-BashFileLines</c> returns <c>$null</c> silently on miss);
/// parity is preserved.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashNl")]
[OutputType(typeof(string))]
public sealed class InvokeBashNlCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>-w N (number width) decoy: bare -w is ambiguous with -WarningAction/-WarningVariable.</summary>
    [Parameter] public string? W { get; set; }
    /// <summary>-v N (start number) decoy: bare -v abbreviates -Verbose.</summary>
    [Parameter] public string? V { get; set; }
    /// <summary>-i N (increment) decoy: bare -i is ambiguous with -Information*.</summary>
    [Parameter] public string? I { get; set; }

    /// <summary>
    /// Valid GNU <c>nl</c> options ps-bash does not implement, refused loudly (exit 2) by the
    /// shared parser. They all concern logical pages / sections (<c>-d</c> delimiter, <c>-f</c>
    /// footer and <c>-h</c> header styles, <c>-p</c> no-renumber) or blank-line grouping
    /// (<c>-l</c>) — ps-bash nl numbers a single body section. (A string[] on purpose:
    /// CommonParameterCollisionGuardTests enumerates static string sets.)
    /// </summary>
    private static readonly string[] NlValidButUnsupported =
    {
        "-d", "--section-delimiter",
        "-f", "--footer-numbering",
        "-h", "--header-numbering",
        "-l", "--join-blank-lines",
        "-p", "--no-renumber",
    };

    private const string OptBody = "body", OptFormat = "format", OptSeparator = "sep",
        OptWidth = "width", OptStart = "start", OptIncrement = "incr";

    /// <summary>GNU nl long_options[] order; getopt_long lists ambiguous-prefix candidates in it.</summary>
    private static readonly string[] NlLongOptionOrder = { "header-numbering", "help", "no-renumber", "number-separator", "number-width", "number-format", "starting-line-number", "section-delimiter" };

    /// <summary>
    /// nl's option surface (GNU coreutils 9.4: -b -d -f -h -i -l -n -p -s -v -w + long forms).
    /// Implemented: -b (a/t/n), -n (ln/rn/rz), -s, -w, -v, -i. Built once for the shared parser.
    /// </summary>
    private static readonly OptSpecSet NlSpec = new(
        new[]
        {
            new OptSpec(OptBody, 'b', "body-numbering", OptKind.Value),
            new OptSpec(OptFormat, 'n', "number-format", OptKind.Value),
            new OptSpec(OptSeparator, 's', "number-separator", OptKind.Value),
            new OptSpec(OptWidth, 'w', "number-width", OptKind.Value),
            new OptSpec(OptStart, 'v', "starting-line-number", OptKind.Value),
            new OptSpec(OptIncrement, 'i', "line-increment", OptKind.Value),
        },
        validButUnsupported: NlValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true,
        longOptionOrder: NlLongOptionOrder);

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, NlSpec);

    /// <summary>The resolved meaning of an nl argv (shared by the cmdlet and the fused core).</summary>
    internal sealed class NlArgs
    {
        public ParsedArgs Parsed = null!;
        public bool NumberAll, NumberNone;
        public string Style = "rn"; // rn=right, ln=left, rz=right zero-padded
        public string Sep = "\t";
        public int Width = 6, Start = 1, Incr = 1;
        public List<string> Operands = new();
        /// <summary>A value error the scan itself cannot see (bad style/format/number); see <see cref="ErrorExit"/>.</summary>
        public string? Error;
        /// <summary>1 for a usage error (GNU), 2 for a valid-but-unsupported value (<c>-bp&lt;RE&gt;</c>).</summary>
        public int ErrorExit = 1;

        /// <summary>True when nothing further should execute: scan error, value error, --help/--version.</summary>
        public bool Declined =>
            Parsed.HasError || Error is not null
            || Parsed.Has(OptSpecSet.HelpId) || Parsed.Has(OptSpecSet.VersionId);
    }

    /// <summary>
    /// Scan + resolve, validating every value like GNU (last occurrence of an option wins, but a
    /// bad value anywhere is an error): <c>-b</c> a|t|n (or the unsupported <c>pREGEX</c>), <c>-n</c>
    /// ln|rn|rz, <c>-w</c> a positive width, <c>-v</c>/<c>-i</c> integers. The old scan silently
    /// ignored bad values (<c>nl -n xx</c> = right-aligned, <c>-w x</c> = width 6).
    /// </summary>
    internal static NlArgs Plan(string[] args)
    {
        var n = new NlArgs { Parsed = ScanArgs(args) };
        n.Operands = n.Parsed.Operands();
        if (n.Parsed.HasError) return n;

        foreach (var tok in n.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            string v = tok.Value!;
            switch (tok.OptId)
            {
                case OptBody:
                    switch (v)
                    {
                        case "a": n.NumberAll = true; n.NumberNone = false; break;
                        case "n": n.NumberNone = true; n.NumberAll = false; break;
                        case "t": n.NumberAll = false; n.NumberNone = false; break;
                        default:
                            if (v.Length > 0 && v[0] == 'p')
                            {
                                n.Error = $"nl: body numbering style '{v}' (regex numbering) is recognized but not supported by ps-bash";
                                n.ErrorExit = ArgError.UnsupportedExitCode;
                            }
                            else n.Error = $"nl: invalid body numbering style: '{v}'";
                            return n;
                    }
                    break;
                case OptFormat:
                    if (v is "ln" or "rn" or "rz") n.Style = v;
                    else { n.Error = $"nl: invalid line numbering format: '{v}'"; return n; }
                    break;
                case OptSeparator:
                    n.Sep = v;
                    break;
                case OptWidth:
                    if (!TryInt(v, out int w) || w < 1) { n.Error = $"nl: invalid line number field width: '{v}'"; return n; }
                    n.Width = w;
                    break;
                case OptStart:
                    if (!TryInt(v, out int start)) { n.Error = $"nl: invalid starting line number: '{v}'"; return n; }
                    n.Start = start;
                    break;
                case OptIncrement:
                    if (!TryInt(v, out int incr)) { n.Error = $"nl: invalid line number increment: '{v}'"; return n; }
                    n.Incr = incr;
                    break;
            }
        }
        return n;
    }

    private static bool TryInt(string s, out int value) =>
        int.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out value);

    /// <summary>Arguments with the value decoys re-injected (bare -w/-v/-i bind -WarningAction /
    /// -Verbose / -Information*): each becomes the two elements <c>-x VALUE</c>.</summary>
    private string[] ArgsWithDecoys()
    {
        var args = Arguments ?? Array.Empty<string>();
        var pre = new List<string>();
        if (W is not null) { pre.Add("-w"); pre.Add(W); }
        if (V is not null) { pre.Add("-v"); pre.Add(V); }
        if (I is not null) { pre.Add("-i"); pre.Add(I); }
        return pre.Count == 0 ? args : pre.Concat(args).ToArray();
    }

    // Parsed-once state.
    private bool _parsed;
    private bool _numberAll;
    private bool _numberNone;
    private int _width = 6;
    private string _sep = "\t";
    private int _start = 1;
    private int _incr = 1;
    private string _style = "rn"; // rn=right, ln=left, rz=right zero-padded
    private List<string> _operands = new();
    // True when stdin must NOT be streamed: file operands present, a scan/value error, or a
    // --help / --version request (all short-circuit in EndProcessing).
    private bool _suppressStdin;
    // Numbering counter — instance state so a streamed stdin run and any
    // trailing file reads number continuously.
    private int _lineNum;
    private string? _blankNum;   // cached `-b n` blank number field (width is fixed post-parse)

    private NlArgs? _plan;

    private void ParseOnce()
    {
        if (_parsed) return;
        _parsed = true;

        var args = ArgsWithDecoys();

        // --help / --version short-circuit before flag scanning (oracle order).
        if (Array.IndexOf(args, "--version") >= 0 || Array.IndexOf(args, "--help") >= 0)
        {
            _suppressStdin = true;
            return;
        }

        // Shared ordered parser: bundles (-ba), attached values (-w3, -s:), long forms and
        // unique-prefix abbreviations, `--`, and the unsupported/unknown classifier in ONE scan.
        // A scan or value error is reported from EndProcessing; stdin is not streamed for it.
        var plan = Plan(args);
        _plan = plan;
        _numberAll = plan.NumberAll;
        _numberNone = plan.NumberNone;
        _width = plan.Width;
        _start = plan.Start;
        _incr = plan.Incr;
        _sep = plan.Sep;
        _style = plan.Style;
        _operands = plan.Operands;

        // Seed the counter so the first numbered line is exactly _start.
        _lineNum = _start - _incr;
        _suppressStdin = plan.Declined || _operands.Count > 0;
    }
    private void EmitNumbered(string line)
    {
        // -b n: never number; GNU still prints the blank number field + separator.
        if (_numberNone)
        {
            _blankNum ??= new string(' ', _width);
            WriteObject(BashRuntime.NewBashObject(_blankNum + _sep + line));
            return;
        }
        // Default (-b t): empty lines are unnumbered (bare empty — oracle parity).
        if (!_numberAll && line.Length == 0)
        {
            WriteObject(BashRuntime.NewBashObject(string.Empty));
            return;
        }

        _lineNum += _incr;
        string num = _style switch
        {
            "ln" => _lineNum.ToString().PadRight(_width),
            "rz" => _lineNum.ToString().PadLeft(_width, '0'),
            _ => _lineNum.ToString().PadLeft(_width),
        };
        WriteObject(BashRuntime.NewBashObject(num + _sep + line));
    }

    protected override void ProcessRecord()
    {
        if (InputObject == null) return;

        ParseOnce();
        if (_suppressStdin) return;

        // Pipeline mode: number each stdin sub-line as it arrives instead of
        // buffering the whole pipe.
        string text = BashRuntime.GetBashText(InputObject);
        string trimmed = text.TrimEnd('\n');
        if (trimmed.Contains('\n'))
        {
            foreach (var subLine in trimmed.Split('\n'))
            {
                EmitNumbered(subLine);
            }
        }
        else
        {
            EmitNumbered(trimmed);
        }
    }

    protected override void EndProcessing()
    {
        ParseOnce();

        var args = ArgsWithDecoys();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "nl", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "nl"))
            {
                WriteObject(line);
            }
            return;
        }

        if (_plan is { } plan)
        {
            if (FileSystemHelpers.TryWriteParseError(this, "nl", plan.Parsed)) return;
            if (FileSystemHelpers.TryHandleInfoOptions(this, "nl", plan.Parsed)) return;
            if (plan.Error is { } planError)
            {
                FileSystemHelpers.WriteBashError(this, planError);
                FileSystemHelpers.SetLastExitCode(this, plan.ErrorExit);
                return;
            }
        }

        // Pipeline mode (no operands) was already streamed in ProcessRecord.
        if (_operands.Count == 0) return;

        foreach (var raw in _operands)
        {
            foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
            {
                try
                {
                    foreach (var line in BashFileSystem.ReadLines(filePath))
                    {
                        EmitNumbered(line);
                    }
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    WriteReadError(filePath, ex);
                }
            }
        }
    }

    private void WriteReadError(string path, Exception ex)
    {
        bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
            || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
        string msg = notFound ? "No such file or directory" : ex.Message;
        string normalized = path.Replace('\\', '/');
        FileSystemHelpers.WriteBashError(this, $"nl: {normalized}: {msg}");
    }
}
