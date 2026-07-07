using System.Management.Automation;

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
/// <c>$global:LASTEXITCODE = 1</c> and emits a bash-style error through the
/// psm1 <c>Write-BashError</c> sink (string-bodied
/// <c>InvokeCommand.InvokeScript</c>, no ScriptBlock construction — AOT-safe).
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

    /// <summary>Valid GNU <c>cat</c> options ps-bash does not implement (see
    /// <see cref="FileSystemHelpers.TryWriteOperandOptionError"/>). Note <c>-v</c>
    /// and <c>-e</c> are eaten by the binder (-Verbose / the -E switch) before
    /// reaching here; listed for completeness / bundle probing.</summary>
    private static readonly HashSet<string> CatValidButUnsupported = new(StringComparer.Ordinal)
    {
        // -A / -e / -t / -v all require -v non-printing (caret/M- notation),
        // which is not implemented. The long-form aliases of the SUPPORTED short
        // flags (--number, --number-nonblank, --squeeze-blank, --show-ends,
        // --show-tabs) are now parsed and are NOT listed here.
        "-A", "-t", "-u", "-v", "-e",
        "--show-all", "--show-nonprinting",
    };

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

        // Re-inject decoy-bound classifier flags (bare -A/-v never reach Arguments —
        // -A binds -Arguments, -v binds -Verbose) so TryWriteOperandOptionError fires.
        var args = BashRuntime.PrependDecoys(Arguments, (A.IsPresent, "-A"), (V.IsPresent, "-v"));

        // Translate the GNU long forms that are exact aliases of the supported
        // short flags into their short spelling before ConvertFromBashArgs sees
        // them (it parses short flags). --show-ends maps to the E switch, so it
        // is tracked separately and dropped from the arg stream.
        bool longShowEnds = false;
        {
            var translated = new List<string>(args.Length);
            bool sawDashDash = false;
            foreach (var a in args)
            {
                // After a bare `--`, every token is a filename (GNU) — copy verbatim,
                // never translate a file literally named like a long flag.
                if (sawDashDash) { translated.Add(a); continue; }
                if (a == "--") { sawDashDash = true; translated.Add(a); continue; }
                switch (a)
                {
                    case "--number": translated.Add("-n"); break;
                    case "--number-nonblank": translated.Add("-b"); break;
                    case "--squeeze-blank": translated.Add("-s"); break;
                    case "--show-tabs": translated.Add("-T"); break;
                    case "--show-ends": longShowEnds = true; break;
                    default: translated.Add(a); break;
                }
            }
            args = translated.ToArray();
        }

        // -E is bound via the explicit E switch (common-parameter collision);
        // the rest stay in Arguments and are parsed by ConvertFromBashArgs.
        var flagDefs = BashRuntime.NewFlagDefs(new[]
        {
            "-n", "number all lines",
            "-b", "number non-blank lines",
            "-s", "squeeze blank lines",
            "-T", "show ^I for tabs",
        });
        var parsed = BashRuntime.ConvertFromBashArgs(args, flagDefs);
        _numberAll = parsed.Flags["-n"];
        _numberNonBlank = parsed.Flags["-b"];
        _squeezeBlanks = parsed.Flags["-s"];
        _showEnds = E.IsPresent || longShowEnds;
        _showTabs = parsed.Flags["-T"];

        // Bundled-flag recovery: a bundle like -nE or -Es reaches Arguments
        // intact (the explicit E switch only binds a bare -E). ConvertFromBashArgs
        // turns an unrecognized bundle char into an operand, so -E inside a
        // bundle of otherwise-known cat flags would be lost. Detect that case
        // and restore -n/-b/-s/-T/-E from the bundle, matching the psm1 oracle's
        // ConvertFrom-BashArgs which split bundled short flags.
        for (int bi = 0; bi < parsed.Operands.Count; bi++)
        {
            var op = parsed.Operands[bi];
            if (op.Length > 1 && op[0] == '-' && op[1] != '-'
                && op.Skip(1).All(c => "nbsTE".IndexOf(c) >= 0))
            {
                if (op.IndexOf('n') >= 0) _numberAll = true;
                if (op.IndexOf('b') >= 0) _numberNonBlank = true;
                if (op.IndexOf('s') >= 0) _squeezeBlanks = true;
                if (op.IndexOf('T') >= 0) _showTabs = true;
                if (op.IndexOf('E') >= 0) _showEnds = true;
                parsed.Operands.RemoveAt(bi);
                bi--;
            }
        }
        _hasFlags = _numberAll || _numberNonBlank || _squeezeBlanks || _showEnds || _showTabs;
        _operands = parsed.Operands;
        _readStdin = _operands.Count == 0 || _operands.Contains("-");

        // Help / version / an unknown option all make EndProcessing emit
        // something other than the catenation and return early; the oracle
        // ignored stdin in those cases. A file-only invocation likewise never
        // reads stdin. In all of these we must not stream the pipeline.
        bool helpOrVersion = Array.IndexOf(args, "--help") >= 0
            || Array.IndexOf(args, "--version") >= 0;
        bool unknownOption = _operands.Any(FileSystemHelpers.IsOptionLike);
        _suppressStdin = !_readStdin || helpOrVersion || unknownOption;
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
                foreach (var subLine in trimmed.Split('\n'))
                {
                    WriteObject(subLine);
                }
            }
            else
            {
                WriteObject(InputObject);
            }
            return;
        }

        // Flagged path: one CatLine per stdin item (no multi-line split — the
        // oracle's flagged stdin path numbered each pipeline item as one line).
        EmitLine(BashRuntime.GetBashText(InputObject), string.Empty);
    }

    protected override void EndProcessing()
    {
        ParseOnce();

        var args = Arguments ?? Array.Empty<string>();

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

        // Any remaining option-looking operand (not the lone "-" stdin marker)
        // is an unknown flag that fell through ConvertFromBashArgs, not a file —
        // classify it (specific "not supported" if a valid cat flag, else
        // bash-parity "unrecognized option") instead of reporting a missing file.
        if (FileSystemHelpers.TryWriteOperandOptionError(this, "cat", _operands, CatValidButUnsupported))
        {
            return;
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
    private void EmitLine(string content, string fileName)
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
