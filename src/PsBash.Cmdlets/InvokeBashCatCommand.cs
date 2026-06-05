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

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _pipeline = new();

    /// <summary>Valid GNU <c>cat</c> options ps-bash does not implement (see
    /// <see cref="FileSystemHelpers.TryWriteOperandOptionError"/>). Note <c>-v</c>
    /// and <c>-e</c> are eaten by the binder (-Verbose / the -E switch) before
    /// reaching here; listed for completeness / bundle probing.</summary>
    private static readonly HashSet<string> CatValidButUnsupported = new(StringComparer.Ordinal)
    {
        "-A", "-t", "-u", "-v", "-e",
        "--show-all", "--show-nonprinting", "--number-nonblank", "--show-ends",
        "--number", "--squeeze-blank", "--show-tabs",
    };

    protected override void ProcessRecord()
    {
        if (InputObject != null)
        {
            _pipeline.Add(InputObject);
        }
    }

    protected override void EndProcessing()
    {
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
        bool numberAll = parsed.Flags["-n"];
        bool numberNonBlank = parsed.Flags["-b"];
        bool squeezeBlanks = parsed.Flags["-s"];
        bool showEnds = E.IsPresent;
        bool showTabs = parsed.Flags["-T"];

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
                if (op.IndexOf('n') >= 0) numberAll = true;
                if (op.IndexOf('b') >= 0) numberNonBlank = true;
                if (op.IndexOf('s') >= 0) squeezeBlanks = true;
                if (op.IndexOf('T') >= 0) showTabs = true;
                if (op.IndexOf('E') >= 0) showEnds = true;
                parsed.Operands.RemoveAt(bi);
                bi--;
            }
        }
        bool hasFlags = numberAll || numberNonBlank || squeezeBlanks || showEnds || showTabs;

        var operands = parsed.Operands;

        // Any remaining option-looking operand (not the lone "-" stdin marker)
        // is an unknown flag that fell through ConvertFromBashArgs, not a file —
        // classify it (specific "not supported" if a valid cat flag, else
        // bash-parity "unrecognized option") instead of reporting a missing file.
        if (FileSystemHelpers.TryWriteOperandOptionError(this, "cat", operands, CatValidButUnsupported))
        {
            return;
        }

        bool readStdin = operands.Count == 0 || operands.Contains("-");
        bool hadError = false;

        // Fast path: bare cat with no flags.
        if (!hasFlags)
        {
            if (readStdin && _pipeline.Count > 0)
            {
                foreach (var item in _pipeline)
                {
                    string text = BashRuntime.GetBashText(item);
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
                        WriteObject(item);
                    }
                }
            }

            var fileOperands = operands.Where(o => o != "-").ToList();
            foreach (var filePath in ResolveGlob(fileOperands))
            {
                string? content = ReadFileText(filePath, "cat");
                if (content == null)
                {
                    hadError = true;
                    continue;
                }
                foreach (var obj in BashRuntime.EmitBashLines(content))
                {
                    WriteObject(obj);
                }
            }

            if (hadError)
            {
                SessionState.PSVariable.Set("global:LASTEXITCODE", 1);
            }
            return;
        }

        // Flagged path: full CatLine objects with numbering / squeezing.
        int lineNum = 0;
        int nonBlankNum = 0;
        bool lastWasBlank = false;

        void EmitLine(string content, string fileName)
        {
            bool isBlank = content.Length == 0;

            if (squeezeBlanks && isBlank && lastWasBlank)
            {
                return;
            }
            lastWasBlank = isBlank;

            lineNum++;
            if (!isBlank)
            {
                nonBlankNum++;
            }

            string text = content;
            if (showTabs)
            {
                text = text.Replace("\t", "^I");
            }
            if (showEnds)
            {
                text += "$";
            }

            if (numberNonBlank)
            {
                if (!isBlank)
                {
                    text = nonBlankNum.ToString().PadLeft(6) + "\t" + text;
                }
            }
            else if (numberAll)
            {
                text = lineNum.ToString().PadLeft(6) + "\t" + text;
            }

            var obj = new PSObject();
            obj.TypeNames.Insert(0, "PsBash.CatLine");
            obj.Properties.Add(new PSNoteProperty("LineNumber", lineNum));
            obj.Properties.Add(new PSNoteProperty("Content", content));
            obj.Properties.Add(new PSNoteProperty("FileName", fileName));
            obj.Properties.Add(new PSNoteProperty(
                "BashText", BashRuntime.NormalizeBashText(text + "\n")));
            WriteObject(obj);
        }

        if (readStdin && _pipeline.Count > 0)
        {
            foreach (var item in _pipeline)
            {
                string content = BashRuntime.GetBashText(item);
                EmitLine(content, string.Empty);
            }
        }

        var flaggedFileOperands = operands.Where(o => o != "-").ToList();
        foreach (var filePath in ResolveGlob(flaggedFileOperands))
        {
            string? content = ReadFileLinesText(filePath, "cat");
            if (content == null)
            {
                hadError = true;
                continue;
            }
            // ReadLine semantics: split on \n after CRLF normalization; a
            // trailing newline does not yield a spurious empty final line.
            string body = content;
            bool trailingNl = body.EndsWith("\n");
            if (trailingNl)
            {
                body = body.Substring(0, body.Length - 1);
            }
            if (body.Length == 0 && trailingNl)
            {
                // File was exactly "\n" — one empty line.
                EmitLine(string.Empty, filePath);
            }
            else if (body.Length > 0 || !trailingNl)
            {
                foreach (var line in body.Split('\n'))
                {
                    EmitLine(line, filePath);
                }
            }
        }

        if (hadError)
        {
            SessionState.PSVariable.Set("global:LASTEXITCODE", 1);
        }
    }

    /// <summary>
    /// psm1 oracle: <c>Read-BashFileBytes</c> — reads a file as text with CRLF
    /// normalization; on failure emits a bash-style error and returns
    /// <c>null</c>.
    /// </summary>
    private string? ReadFileText(string path, string command)
    {
        try
        {
            return BashFileSystem.ReadAllText(path);
        }
        catch (Exception ex)
        {
            EmitReadError(path, command, ex);
            return null;
        }
    }

    /// <summary>
    /// psm1 oracle: the flagged path opened an <c>Open-BashFileReader</c> and
    /// looped <c>ReadLine()</c>. <see cref="StreamReader.ReadLine"/> already
    /// strips <c>\r\n</c> and <c>\n</c>, so reading the whole text with CRLF
    /// normalization and splitting is line-for-line equivalent.
    /// </summary>
    private string? ReadFileLinesText(string path, string command)
    {
        try
        {
            return BashFileSystem.ReadAllText(path);
        }
        catch (Exception ex)
        {
            EmitReadError(path, command, ex);
            return null;
        }
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
