using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashWc</c> function
/// (REFACTOR-2 Phase 1c). Counts lines, words, and bytes of pipeline or file
/// input, matching the bash <c>wc</c> command.
///
/// Behavioral parity oracle: the original psm1 function. This cmdlet reproduces
/// its exact behavior:
/// <list type="bullet">
/// <item>Flags <c>-l</c> / <c>-w</c> / <c>-c</c> select a single column; with
/// none, all three columns are emitted. Parsed via
/// <see cref="BashRuntime.ConvertFromBashArgs"/> (the same boolean-flag parser
/// the psm1 oracle used).</item>
/// <item>Pipeline mode: each input item is split on <c>\n</c>; words are
/// whitespace-delimited (space/tab/CR/LF, empties removed); bytes are the
/// UTF-8 byte count of each line plus one for the line's <c>\n</c>.</item>
/// <item>File mode: operands are resolved via the psm1 <c>Resolve-BashGlob</c>
/// (reachable because a <see cref="PSCmdlet"/> has
/// <see cref="PSCmdlet.SessionState"/>); a missing file emits a bash-style
/// error and is skipped; the byte count is the file length minus a UTF-8 BOM
/// if present; line/word counts come from the CRLF-normalized text.</item>
/// <item>Multiple files emit a trailing <c>total</c> row.</item>
/// <item>Each result is a typed <c>PsBash.WcResult</c> PSObject carrying
/// <c>Lines</c>, <c>Words</c>, <c>Bytes</c>, <c>FileName</c>, and the formatted
/// <c>BashText</c> — the same column padding the psm1 oracle produced
/// (7-wide single column; 7/8/8 for the three-column form), left-trimmed.</item>
/// </list>
///
/// psm1-only dependencies, and why a clean migration is still possible:
/// <c>Resolve-BashGlob</c> needs the <c>$PWD</c> path provider and the
/// <c>--help</c> path needs the script-scoped help tables — both are reachable
/// from a <see cref="PSCmdlet"/> via <see cref="PSCmdlet.SessionState"/> /
/// <c>InvokeCommand.InvokeScript</c> with a string body, so the cmdlet stays
/// AOT-safe with no ScriptBlock construction on the hot path. The
/// <c>Resolve-BashGlob</c> glob slice is reimplemented here in C# rather than
/// calling back into psm1, matching the oracle's logic exactly.
///
/// Common-parameter collision: the bash flag <c>-w</c> prefix-collides with the
/// PowerShell common parameters <c>-WarningAction</c> / <c>-WarningVariable</c>
/// — an unbound <c>-w</c> would be rejected as ambiguous before reaching
/// <see cref="Arguments"/>. It is therefore declared as an explicit
/// <see cref="SwitchParameter"/> (<see cref="W"/>): an exact parameter-name
/// match beats a common-parameter prefix match, so <c>Invoke-BashWc -w</c>
/// binds the way the psm1 oracle's <c>$args</c> scan did. <c>-c</c>
/// (bytes-only) likewise prefix-collides with <c>-Confirm</c> and is declared
/// as <see cref="C"/> (an earlier audit wrongly called <c>-c</c> collision-free
/// — a bare <c>-c</c> was silently bound to <c>-Confirm</c> and the byte count
/// dropped). <c>-l</c> has no colliding prefix and stays in
/// <see cref="Arguments"/>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashWc")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashWcCommand : PSCmdlet
{
    /// <summary>
    /// The bash <c>-w</c> (words-only) switch — declared explicitly because the
    /// bare token <c>-w</c> prefix-collides with <c>-WarningAction</c> /
    /// <c>-WarningVariable</c> common parameters. See the class remarks.
    /// </summary>
    [Parameter]
    public SwitchParameter W { get; set; }

    /// <summary>
    /// The bash <c>-c</c> (bytes-only) switch — declared explicitly because the
    /// bare token <c>-c</c> prefix-collides with the <c>-Confirm</c> common
    /// parameter and would otherwise be silently bound (the flag dropped, byte
    /// count never selected) before reaching <see cref="Arguments"/>. An exact
    /// parameter-name match beats the common-parameter prefix match.
    /// </summary>
    [Parameter]
    public SwitchParameter C { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _pipeline = new();

    /// <summary>Valid GNU <c>wc</c> options ps-bash does not implement (see
    /// <see cref="FileSystemHelpers.TryWriteOperandOptionError"/>).</summary>
    private static readonly HashSet<string> WcValidButUnsupported = new(StringComparer.Ordinal)
    {
        "-m", "-L",
        "--lines", "--words", "--bytes", "--chars", "--max-line-length",
        "--files0-from",
    };

    private static readonly char[] WhitespaceChars = { ' ', '\t', '\n', '\r' };

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

        if (FileSystemHelpers.TryHandleVersion(this, "wc", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "wc"))
            {
                WriteObject(line);
            }
            return;
        }

        // -w is bound via the explicit W switch (common-parameter collision);
        // -l and -c stay in Arguments and are parsed by ConvertFromBashArgs.
        var flagDefs = BashRuntime.NewFlagDefs(new[]
        {
            "-l", "line count only",
            "-c", "byte count only",
        });
        var parsed = BashRuntime.ConvertFromBashArgs(args, flagDefs);
        bool linesOnly = parsed.Flags["-l"];
        bool wordsOnly = W.IsPresent;
        bool bytesOnly = parsed.Flags["-c"] || C.IsPresent;
        var operands = parsed.Operands;

        // Bundled-flag recovery: a bundle like -lw or -wc reaches operands
        // intact (the explicit W switch only binds a bare -w, and
        // ConvertFromBashArgs turns the unrecognized -w bundle char into an
        // operand). Restore -l/-w/-c from such a bundle, matching the psm1
        // oracle's ConvertFrom-BashArgs which split bundled short flags.
        for (int bi = 0; bi < operands.Count; bi++)
        {
            var op = operands[bi];
            if (op.Length > 1 && op[0] == '-' && op[1] != '-'
                && op.Skip(1).All(c => "lwc".IndexOf(c) >= 0))
            {
                if (op.IndexOf('l') >= 0) linesOnly = true;
                if (op.IndexOf('w') >= 0) wordsOnly = true;
                if (op.IndexOf('c') >= 0) bytesOnly = true;
                operands.RemoveAt(bi);
                bi--;
            }
        }

        // Any remaining option-looking operand is an unknown flag that fell
        // through ConvertFromBashArgs, not a file — classify it (specific "not
        // supported" if a valid wc flag, else bash-parity "unrecognized option")
        // instead of reporting it as a missing file.
        if (FileSystemHelpers.TryWriteOperandOptionError(this, "wc", operands, WcValidButUnsupported))
        {
            return;
        }

        // Pipeline mode
        if (operands.Count == 0 && _pipeline.Count > 0)
        {
            int totalLines = 0, totalWords = 0, totalBytes = 0;
            foreach (var item in _pipeline)
            {
                string text = BashRuntime.GetBashText(item);
                string trimmed = text.TrimEnd('\n');
                if (trimmed.Contains('\n'))
                {
                    foreach (var subLine in trimmed.Split('\n'))
                    {
                        totalLines++;
                        totalWords += CountWords(subLine);
                        totalBytes += Encoding.UTF8.GetByteCount(subLine) + 1;
                    }
                }
                else
                {
                    totalLines++;
                    totalWords += CountWords(trimmed);
                    totalBytes += Encoding.UTF8.GetByteCount(trimmed) + 1;
                }
            }

            WriteObject(BuildResult(
                totalLines, totalWords, totalBytes, string.Empty,
                linesOnly, wordsOnly, bytesOnly));
            return;
        }

        // File mode
        int grandLines = 0, grandWords = 0, grandBytes = 0;
        bool multipleFiles = operands.Count > 1;

        foreach (var filePath in ResolveGlob(operands))
        {
            if (!File.Exists(filePath) && !Directory.Exists(filePath))
            {
                WriteBashError($"wc: {filePath}: No such file or directory");
                continue;
            }

            string rawText;
            try
            {
                rawText = BashFileSystem.ReadAllText(filePath);
            }
            catch (Exception ex)
            {
                string normalized = filePath.Replace('\\', '/');
                bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
                    || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
                string msg = notFound ? "No such file or directory" : ex.Message;
                WriteBashError($"wc: {normalized}: {msg}");
                continue;
            }

            long fileBytes;
            try
            {
                fileBytes = new FileInfo(filePath).Length;
                using var fs = BashFileSystem.OpenRead(filePath);
                var bom = new byte[3];
                if (fs.Read(bom, 0, 3) >= 3
                    && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                {
                    fileBytes -= 3;
                }
            }
            catch
            {
                // If BOM peek fails, use raw file size; if even FileInfo failed
                // the file was unreadable — fall back to 0 to mirror best-effort.
                try { fileBytes = new FileInfo(filePath).Length; }
                catch { fileBytes = 0; }
            }

            int lineCount = 0;
            foreach (char c in rawText)
            {
                if (c == '\n') lineCount++;
            }
            int wordCount = CountWords(rawText);

            grandLines += lineCount;
            grandWords += wordCount;
            grandBytes += (int)fileBytes;

            WriteObject(BuildResult(
                lineCount, wordCount, (int)fileBytes, filePath,
                linesOnly, wordsOnly, bytesOnly));
        }

        if (multipleFiles)
        {
            WriteObject(BuildResult(
                grandLines, grandWords, grandBytes, "total",
                linesOnly, wordsOnly, bytesOnly));
        }
    }

    private static int CountWords(string text)
    {
        return text.Split(WhitespaceChars, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static PSObject BuildResult(
        int lines, int words, int bytes, string fileName,
        bool linesOnly, bool wordsOnly, bool bytesOnly)
    {
        var sb = new StringBuilder();
        if (linesOnly)
        {
            sb.Append(lines.ToString().PadLeft(7));
        }
        else if (wordsOnly)
        {
            sb.Append(words.ToString().PadLeft(7));
        }
        else if (bytesOnly)
        {
            sb.Append(bytes.ToString().PadLeft(7));
        }
        else
        {
            sb.Append(lines.ToString().PadLeft(7));
            sb.Append(words.ToString().PadLeft(8));
            sb.Append(bytes.ToString().PadLeft(8));
        }
        if (fileName.Length > 0)
        {
            sb.Append(' ').Append(fileName);
        }

        // psm1 oracle: ($parts -join '') -replace '^\s+', ' ' then TrimStart().
        // Net effect is a plain TrimStart of leading whitespace.
        string bashText = sb.ToString().TrimStart();

        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.WcResult");
        obj.Properties.Add(new PSNoteProperty("Lines", lines));
        obj.Properties.Add(new PSNoteProperty("Words", words));
        obj.Properties.Add(new PSNoteProperty("Bytes", bytes));
        obj.Properties.Add(new PSNoteProperty("FileName", fileName));
        obj.Properties.Add(new PSNoteProperty("BashText", BashRuntime.NormalizeBashText(bashText)));
        return obj;
    }

    /// <summary>
    /// Reimplements the psm1 <c>Resolve-BashGlob</c> slice in C#: a path with
    /// <c>*</c> or <c>?</c> is expanded against the current location; if it
    /// matches nothing it passes through literally so the caller emits its own
    /// error. A literal path is resolved against the shell's <c>$PWD</c> via
    /// <see cref="PathIntrinsics.GetUnresolvedProviderPathFromPSPath"/> — the
    /// same provider-aware resolution the psm1 oracle used (not
    /// <see cref="Directory.GetCurrentDirectory"/>).
    /// </summary>
    private IEnumerable<string> ResolveGlob(IReadOnlyList<string> paths)
    {
        foreach (var p in paths)
        {
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
                    // No matches — fall through to literal passthrough.
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

    private void WriteBashError(string message)
    {
        FileSystemHelpers.WriteBashError(this, message);
    }
}
