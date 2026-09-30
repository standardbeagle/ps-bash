using System.Linq;
using System.Management.Automation;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashCat</c> function
/// (REFACTOR-2 Phase 1c). Concatenates pipeline and/or file input, matching the
/// bash <c>cat</c> command.
///
/// Behavioral parity oracle: the original psm1 function. This cmdlet reproduces
/// its exact two-path structure:
/// <list type="bullet">
/// <item><b>Fast path</b> — bare <c>cat</c> with no flags. Pipeline items pass
/// through (a multi-line item is split into its lines; a single-line item is
/// re-emitted as-is, preserving its typed object); file operands are read with
/// CRLF normalization and emitted one <c>PsBash.TextOutput</c> object per line
/// via <see cref="BashRuntime.EmitBashLines"/>.</item>
/// <item><b>Flagged path</b> — <c>-n</c> (number all lines), <c>-b</c> (number
/// non-blank lines), <c>-s</c> (squeeze consecutive blank lines), <c>-E</c>
/// (append <c>$</c> at line end), <c>-T</c> (render tabs as <c>^I</c>). Each
/// line becomes a typed <c>PsBash.CatLine</c> PSObject with <c>LineNumber</c>,
/// <c>Content</c>, <c>FileName</c>, and the formatted <c>BashText</c>. The
/// numbering, squeeze, and tab/end transforms reproduce the psm1 oracle's
/// <c>$emitLine</c> closure exactly (numbering is 6-wide, tab-separated;
/// <c>-b</c> only numbers non-blank lines; <c>-s</c> drops a blank line that
/// follows a blank line).</item>
/// </list>
/// Stdin is consumed only when there are no file operands or an explicit
/// <c>-</c> operand is present — matching the oracle. Flags are parsed via
/// <see cref="BashRuntime.ConvertFromBashArgs"/>.
///
/// <b>Streaming:</b> stdin records are emitted from <see cref="ProcessRecord"/>
/// as they arrive instead of being buffered into a list and processed in
/// <see cref="EndProcessing"/> — a bare <c>cat</c> on a huge pipe must not
/// materialize the whole stream in memory. Flags / operands are parsed lazily on
/// the first record (or in <see cref="EndProcessing"/> when there is no pipeline
/// input); the flagged-path numbering counters live on the instance so stdin
/// lines and any trailing file lines number continuously, exactly as the
/// buffered oracle did (stdin first, then files). File operands are still read
/// in <see cref="EndProcessing"/>.
///
/// psm1-only dependencies, and why a clean migration is still possible:
/// <c>Resolve-BashGlob</c> needs the <c>$PWD</c> path provider, reachable from a
/// <see cref="PSCmdlet"/> via <see cref="PSCmdlet.SessionState"/>; its glob
/// slice is reimplemented here in C#. A file-read error sets
/// <c>$global:LASTEXITCODE = 1</c> and emits a bash-style error through
/// <see cref="FileSystemHelpers.WriteBashError"/> (one ErrorRecord).
/// The <c>--help</c> path delegates to <c>Show-BashHelp</c>.
///
/// Common-parameter collision: the bash flag <c>-E</c> prefix-collides with the
/// PowerShell common parameters <c>-ErrorAction</c> / <c>-ErrorVariable</c> — an
/// unbound <c>-E</c> would be rejected as ambiguous before reaching
/// <see cref="Arguments"/>. It is therefore declared as an explicit
/// <see cref="SwitchParameter"/> (<see cref="E"/>): an exact parameter-name
/// match beats a common-parameter prefix match. <c>-n</c>, <c>-b</c>, <c>-s</c>,
/// and <c>-T</c> have no colliding prefix and stay in <see cref="Arguments"/>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashCat")]
[OutputType(typeof(PSObject))]
[OutputType(typeof(string))]
public sealed class InvokeBashCatCommand : PSCmdlet
{
    /// <summary>
    /// The bash <c>-E</c> (show <c>$</c> at line end) switch — declared
    /// explicitly because the bare token <c>-E</c> prefix-collides with
    /// <c>-ErrorAction</c> / <c>-ErrorVariable</c> common parameters. See the
    /// class remarks.
    /// </summary>
    [Parameter]
    public SwitchParameter E { get; set; }

    /// <summary>
    /// Decoy for the valid-but-unsupported <c>-A</c> (show-all). Bare <c>-A</c>
    /// prefix-matches the cmdlet's own <c>-Arguments</c> (the only param starting with
    /// 'a'), silently swallowing the next operand. Re-injected so the classifier fires.
    /// </summary>
    [Parameter]
    public SwitchParameter A { get; set; }

    /// <summary>
    /// Decoy for the valid-but-unsupported <c>-v</c> (show-nonprinting). Bare <c>-v</c>
    /// silently bound <c>-Verbose</c>, so cat produced wrong output with exit 0.
    /// </summary>
    [Parameter]
    public SwitchParameter V { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>
    /// Valid GNU <c>cat</c> options ps-bash does not implement, refused loudly (exit 2) by the
    /// shared parser: -A / -e / -t / -v (and --show-all / --show-nonprinting) all need the
    /// <c>-v</c> caret/M- notation for non-printing characters, which is not implemented.
    /// (A string[] on purpose: CommonParameterCollisionGuardTests enumerates static string sets.)
    /// </summary>
    private static readonly string[] CatValidButUnsupported =
    {
        "-A", "-t", "-v", "-e",
        "--show-all", "--show-nonprinting",
    };

    private const string OptNumber = "number", OptNonBlank = "nonblank", OptSqueeze = "squeeze",
        OptEnds = "ends", OptTabs = "tabs", OptIgnored = "ignored";

    /// <summary>
    /// cat's option surface (GNU coreutils 9.4: -A -b -e -E -n -s -t -T -u -v + --show-all
    /// --number-nonblank --show-ends --number --squeeze-blank --show-tabs --show-nonprinting).
    /// <c>-u</c> is "(ignored)" in GNU and is accepted as a no-op here too. Built once for the
    /// shared ordered parser.
    /// </summary>
    private static readonly OptSpecSet CatSpec = new(
        new[]
        {
            new OptSpec(OptNumber, 'n', "number"),
            new OptSpec(OptNonBlank, 'b', "number-nonblank"),
            new OptSpec(OptSqueeze, 's', "squeeze-blank"),
            new OptSpec(OptEnds, 'E', "show-ends"),
            new OptSpec(OptTabs, 'T', "show-tabs"),
            new OptSpec(OptIgnored, 'u', null),
        },
        validButUnsupported: CatValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true);

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, CatSpec);

    /// <summary>The resolved meaning of a cat argv (shared by the cmdlet and the fused core).</summary>
    internal sealed class CatArgs
    {
        public ParsedArgs Parsed = null!;
        public bool NumberAll, NumberNonBlank, Squeeze, ShowEnds, ShowTabs;
        public List<string> Operands = new();

        /// <summary>True when nothing further should execute: scan error or --help/--version.</summary>
        public bool Declined =>
            Parsed.HasError || Parsed.Has(OptSpecSet.HelpId) || Parsed.Has(OptSpecSet.VersionId);

        public bool HasFlags => NumberAll || NumberNonBlank || Squeeze || ShowEnds || ShowTabs;
    }

    /// <summary>Scan + resolve (any order, any bundling; repeats are harmless).</summary>
    internal static CatArgs Plan(string[] args)
    {
        var p = ScanArgs(args);
        return new CatArgs
        {
            Parsed = p,
            NumberAll = p.Has(OptNumber),
            NumberNonBlank = p.Has(OptNonBlank),
            Squeeze = p.Has(OptSqueeze),
            ShowEnds = p.Has(OptEnds),
            ShowTabs = p.Has(OptTabs),
            Operands = p.Operands(),
        };
    }

    /// <summary>Arguments with the decoy-bound flags re-injected (bare -A binds -Arguments, -v binds
    /// -Verbose, -E binds the E switch): a bound decoy would otherwise never reach the parser.</summary>
    private string[] ArgsWithDecoys()
        => BashRuntime.PrependDecoys(Arguments, (A.IsPresent, "-A"), (V.IsPresent, "-v"), (E.IsPresent, "-E"));

    private CatArgs? _plan;
    // Parsed-once flag / operand state.
    private bool _parsed;
    private bool _numberAll, _numberNonBlank, _squeezeBlanks, _showEnds, _showTabs, _hasFlags;
    private List<string> _operands = new();
    private bool _readStdin;
    // True when stdin must NOT be streamed: a file-only invocation, or a
    // --help / --version / unknown-option request whose output EndProcessing
    // produces instead. In every such case the buffered oracle ignored stdin.
    private bool _suppressStdin;

    // Flagged-path numbering counters — shared across the stdin stream and the
    // trailing file reads so numbering is continuous (stdin first, then files).
    private int _lineNum;
    private int _nonBlankNum;
    private bool _lastWasBlank;

    private bool _hadError;

    private void ParseOnce()
    {
        if (_parsed) return;
        _parsed = true;

        var args = ArgsWithDecoys();

        // Shared ordered parser: bundles (-nE, -Es), long forms (--number), unique-prefix
        // abbreviations (--squeeze), `--`, and the unsupported/unknown classifier in ONE scan.
        // A scan error is reported from EndProcessing; stdin is not streamed for it.
        var plan = Plan(args);
        _plan = plan;
        _numberAll = plan.NumberAll;
        _numberNonBlank = plan.NumberNonBlank;
        _squeezeBlanks = plan.Squeeze;
        _showEnds = plan.ShowEnds;
        _showTabs = plan.ShowTabs;
        _hasFlags = plan.HasFlags;
        _operands = plan.Operands;
        _readStdin = _operands.Count == 0 || _operands.Contains("-");

        // Help / version / a scan error all make EndProcessing emit something other than the
        // catenation and return early; the oracle ignored stdin in those cases. A file-only
        // invocation likewise never reads stdin. In all of these we must not stream the pipeline.
        bool helpOrVersion = Array.IndexOf(args, "--help") >= 0
            || Array.IndexOf(args, "--version") >= 0;
        _suppressStdin = !_readStdin || helpOrVersion || plan.Declined;
    }
    protected override void ProcessRecord()
    {
        if (InputObject == null) return;

        ParseOnce();
        if (_suppressStdin) return;

        if (!_hasFlags)
        {
            // Fast path: pass items through, splitting a multi-line item.
            string text = BashRuntime.GetBashText(InputObject);
            string trimmed = text.TrimEnd('\n');
            if (trimmed.Contains('\n'))
            {
                // Multi-line record: split into text lines, last keeps the missing newline.
                bool unterminated = BashRuntime.IsUnterminated(InputObject);
                var pieces = trimmed.Split('\n');
                for (int p = 0; p < pieces.Length; p++)
                    WriteObject(BashRuntime.TextRecord(pieces[p], unterminated && p == pieces.Length - 1));
            }
            else
            {
                WriteObject(InputObject);
            }
            return;
        }

        // Flagged path (-n/-b/-E/-T/-s) TRANSFORMS the line, so it emits fresh CatLine
        // text records, never the upstream object. A multi-line record (printf 'b\na') is
        // numbered line by line; the last line keeps the source's missing newline.
        string flagged = BashRuntime.GetBashText(InputObject);
        string flaggedTrimmed = flagged.TrimEnd('\n');
        if (flaggedTrimmed.Contains('\n'))
        {
            bool unterminated = BashRuntime.IsUnterminated(InputObject);
            var pieces = flaggedTrimmed.Split('\n');
            for (int p = 0; p < pieces.Length; p++)
                EmitLine(pieces[p], string.Empty, unterminated && p == pieces.Length - 1);
        }
        else
        {
            EmitLine(flaggedTrimmed, string.Empty, BashRuntime.IsUnterminated(InputObject));
        }
    }

    protected override void EndProcessing()
    {
        ParseOnce();

        var args = ArgsWithDecoys();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "cat", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "cat"))
            {
                WriteObject(line);
            }
            return;
        }

        if (_plan is { } plan)
        {
            if (FileSystemHelpers.TryWriteParseError(this, "cat", plan.Parsed)) return;
            if (FileSystemHelpers.TryHandleInfoOptions(this, "cat", plan.Parsed)) return;
        }
        // Stdin was already streamed from ProcessRecord; only files remain.
        var fileOperands = _operands.Where(o => o != "-").ToList();

        if (!_hasFlags)
        {
            // Fast path: read each file, one TextOutput object per line.
            foreach (var filePath in ResolveGlob(fileOperands))
            {
                try
                {
                    foreach (var line in BashFileSystem.ReadTextLines(filePath))
                    {
                        WriteObject(BashRuntime.NewBashObject(
                            line.Text,
                            noTrailingNewline: !line.HasTrailingNewline));
                    }
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    EmitReadError(filePath, "cat", ex);
                    _hadError = true;
                }
            }
        }
        else
        {
            // Flagged path: continue numbering from where the stdin stream left off.
            foreach (var filePath in ResolveGlob(fileOperands))
            {
                try
                {
                    foreach (var line in BashFileSystem.ReadLines(filePath))
                    {
                        EmitLine(line, filePath);
                    }
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    EmitReadError(filePath, "cat", ex);
                    _hadError = true;
                }
            }
        }

        if (_hadError)
        {
            SessionState.PSVariable.Set("global:LASTEXITCODE", 1);
        }
    }

    /// <summary>
    /// Flagged-path line emitter. Reproduces the psm1 oracle's <c>$emitLine</c>
    /// closure exactly: squeeze-blank drop, 6-wide tab-separated numbering
    /// (<c>-b</c> numbers non-blank only), <c>-T</c> tab rendering, <c>-E</c>
    /// end marker. Counters are instance state so a stdin stream and trailing
    /// file reads number continuously.
    /// </summary>
    private void EmitLine(string content, string fileName, bool unterminated = false)
    {
        bool isBlank = content.Length == 0;

        if (_squeezeBlanks && isBlank && _lastWasBlank)
        {
            return;
        }
        _lastWasBlank = isBlank;

        _lineNum++;
        if (!isBlank)
        {
            _nonBlankNum++;
        }

        string text = content;
        if (_showTabs)
        {
            text = text.Replace("\t", "^I");
        }
        if (_showEnds)
        {
            text += "$";
        }

        if (_numberNonBlank)
        {
            if (!isBlank)
            {
                text = _nonBlankNum.ToString().PadLeft(6) + "\t" + text;
            }
        }
        else if (_numberAll)
        {
            text = _lineNum.ToString().PadLeft(6) + "\t" + text;
        }

        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.CatLine");
        obj.Properties.Add(new PSNoteProperty("LineNumber", _lineNum));
        obj.Properties.Add(new PSNoteProperty("Content", content));
        obj.Properties.Add(new PSNoteProperty("FileName", fileName));
        obj.Properties.Add(new PSNoteProperty(
            "BashText", BashRuntime.NormalizeBashText(text + "\n")));
        if (unterminated) obj.Properties.Add(new PSNoteProperty("NoTrailingNewline", true));
        WriteObject(obj);
    }

    private void EmitReadError(string path, string command, Exception ex)
    {
        string normalized = path.Replace('\\', '/');
        bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
            || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
        string msg = notFound ? "No such file or directory" : ex.Message;
        FileSystemHelpers.WriteBashError(this, $"{command}: {normalized}: {msg}");
    }

    /// <summary>
    /// Reimplements the psm1 <c>Resolve-BashGlob</c> slice in C# (see
    /// <see cref="InvokeBashWcCommand"/> for the rationale): <c>*</c>/<c>?</c>
    /// patterns expand against the current location and pass through literally
    /// when nothing matches; literal paths resolve against the shell's
    /// <c>$PWD</c> via the path provider.
    /// </summary>
    private IEnumerable<string> ResolveGlob(IReadOnlyList<string> paths)
    {
        foreach (var rawP in paths)
        {
            var p = FileSystemHelpers.NormalizeOperandPath(rawP);
            if (p.IndexOf('*') >= 0 || p.IndexOf('?') >= 0)
            {
                var matched = new List<string>();
                try
                {
                    foreach (var resolved in SessionState.Path.GetResolvedProviderPathFromPSPath(
                                 p, out _))
                    {
                        matched.Add(resolved);
                    }
                }
                catch
                {
                    // No matches — literal passthrough.
                }

                if (matched.Count == 0)
                {
                    yield return p;
                }
                else
                {
                    foreach (var m in matched)
                    {
                        yield return m;
                    }
                }
            }
            else
            {
                yield return SessionState.Path.GetUnresolvedProviderPathFromPSPath(p);
            }
        }
    }
}
