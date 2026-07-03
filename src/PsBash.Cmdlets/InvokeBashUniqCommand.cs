using System.Management.Automation;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashUniq</c> function
/// (REFACTOR-2 follow-on). Collapses adjacent duplicate lines and supports
/// <c>-c</c> count-prefix, <c>-d</c> duplicates-only, <c>-u</c> uniques-only,
/// <c>-i</c> case-insensitive, <c>-f N</c> skip N whitespace-separated fields,
/// <c>-s N</c> skip N chars, <c>-w N</c> compare at most N chars after skips.
///
/// File + pipeline dual mode. Pipeline mode splits each item's <c>BashText</c>
/// on <c>\n</c> (after trailing-newline trim). File mode reads via
/// <see cref="File.ReadAllText(string)"/> with CRLF normalization and splits
/// on <c>\n</c> (StreamReader.ReadLine semantics — trailing newline does not
/// produce a spurious empty final line). Glob expansion routes through
/// <see cref="FileSystemHelpers.ResolveOperandPaths(PSCmdlet, string)"/>; a
/// file-read failure emits a bash-style error via
/// <see cref="FileSystemHelpers.WriteBashError(PSCmdlet, string)"/> and the
/// cmdlet continues with remaining operands.
///
/// Flag binding: three bare-token short flags prefix-collide with PowerShell
/// common parameters and are declared as explicit <see cref="SwitchParameter"/>s
/// (an exact param-name match beats a common-parameter prefix match):
/// <list type="bullet">
/// <item><c>-c</c> (count) vs <c>-Confirm</c> — declared as <c>C</c>.</item>
/// <item><c>-d</c> (dupes-only) vs <c>-Debug</c> — declared as <c>D</c>.</item>
/// <item><c>-i</c> (case-insensitive) vs <c>-InformationAction</c> /
/// <c>-InformationVariable</c> — declared as <c>I</c>.</item>
/// </list>
/// <c>-u</c>, <c>-f</c>, <c>-s</c>, <c>-w</c> have no PS common-parameter
/// prefix collision and stay in <see cref="Arguments"/>; they are parsed
/// (separated and joined forms) by the manual value-flag scan. Bundled forms
/// like <c>-cd</c>, <c>-ci</c>, <c>-cdi</c> survive the binder by landing in
/// <see cref="Arguments"/> intact (they don't bind to any switch's exact name)
/// and are recovered post-parse against the oracle's per-char dispatch.
///
/// Output uses <see cref="BashRuntime.NewBashObject(string)"/> with the
/// default <c>PsBash.TextOutput</c> shape.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashUniq")]
[OutputType(typeof(string))]
public sealed class InvokeBashUniqCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter]
    public SwitchParameter C { get; set; }

    [Parameter]
    public SwitchParameter D { get; set; }

    [Parameter]
    public SwitchParameter I { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>
    /// Valid GNU <c>uniq</c> options ps-bash does not implement. An
    /// option-looking token in this set yields "recognized but not supported"
    /// instead of the old misleading "No such file or directory". Anything
    /// option-looking NOT here is reported as unrecognized/invalid (bash parity).
    /// </summary>
    private static readonly HashSet<string> UniqValidButUnsupported =
        new(StringComparer.Ordinal)
        {
            "-z", "--zero-terminated",
            "--group",
            "--output-delimiter",
        };

    // Parsed-once state.
    private bool _parsed;
    private bool _countMode, _duplicatesOnly, _ignoreCase, _uniqueOnly, _allRepeated;
    private int _skipFields, _skipChars, _checkChars;
    private List<string> _operands = new();
    // True when stdin must NOT be streamed: file operands present (file mode
    // ignores stdin) or a --help / --version request.
    private bool _suppressStdin;
    // Deferred parse failure: an unrecognized flag found in ParseOnce.
    // Emitted in EndProcessing so the stdin-streaming path stays clean.
    private string? _optionErrorToken;
    // Adjacent-dedup state — uniq only needs the current run, never the whole
    // pipe. Instance state so a streamed stdin run carries across records.
    private string? _prevLine;
    private string? _prevKey;
    private int _runCount;
    private bool _hadError;

    private void ParseOnce()
    {
        if (_parsed) return;
        _parsed = true;

        var rawArgs = Arguments ?? Array.Empty<string>();

        // --help / --version short-circuit before flag scanning (oracle order).
        if (Array.IndexOf(rawArgs, "--version") >= 0 || Array.IndexOf(rawArgs, "--help") >= 0)
        {
            _suppressStdin = true;
            return;
        }

        bool countMode = C.IsPresent;
        bool duplicatesOnly = D.IsPresent;
        bool ignoreCase = I.IsPresent;
        bool uniqueOnly = false;
        int skipFields = 0;
        int skipChars = 0;
        int checkChars = 0;
        var operands = new List<string>();

        int i = 0;
        while (i < rawArgs.Length)
        {
            var arg = rawArgs[i];

            if (arg == "--")
            {
                i++;
                while (i < rawArgs.Length)
                {
                    operands.Add(rawArgs[i]);
                    i++;
                }
                break;
            }

            if (arg == "--ignore-case")
            {
                ignoreCase = true;
                i++;
                continue;
            }

            // -D / --all-repeated[=METHOD]: print ALL lines of each duplicate run.
            if (arg == "--all-repeated" || arg.StartsWith("--all-repeated=", StringComparison.Ordinal))
            {
                _allRepeated = true;
                i++;
                continue;
            }

            if (arg.StartsWith("--skip-fields=", StringComparison.Ordinal))
            {
                if (int.TryParse(arg.Substring("--skip-fields=".Length), out var sf))
                {
                    skipFields = sf;
                }
                i++;
                continue;
            }

            if (arg.StartsWith("--skip-chars=", StringComparison.Ordinal))
            {
                if (int.TryParse(arg.Substring("--skip-chars=".Length), out var sc))
                {
                    skipChars = sc;
                }
                i++;
                continue;
            }

            if (arg.StartsWith("--check-chars=", StringComparison.Ordinal))
            {
                if (int.TryParse(arg.Substring("--check-chars=".Length), out var cc))
                {
                    checkChars = cc;
                }
                i++;
                continue;
            }

            // Unknown long flag (all known --xxx forms were matched above).
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                _optionErrorToken = arg;
                _suppressStdin = true;
                return;
            }

            // Short-flag bundle. Numeric-leading (e.g. "-5") is treated as an
            // operand (oracle parity).
            if (arg.StartsWith("-", StringComparison.Ordinal) && arg.Length > 1 && !IsNumericFlag(arg))
            {
                var body = arg.Substring(1);
                int j = 0;
                while (j < body.Length)
                {
                    char ch = body[j];
                    switch (ch)
                    {
                        case 'c': countMode = true; j++; break;
                        case 'd': duplicatesOnly = true; j++; break;
                        case 'u': uniqueOnly = true; j++; break;
                        case 'i': ignoreCase = true; j++; break;
                        case 'f':
                        {
                            string rest = body.Substring(j + 1);
                            if (rest.Length > 0 && IsDigitRun(rest, out var n))
                            {
                                skipFields = n;
                                j = body.Length;
                            }
                            else
                            {
                                i++;
                                if (i < rawArgs.Length && int.TryParse(rawArgs[i], out var nv))
                                {
                                    skipFields = nv;
                                }
                                j = body.Length;
                            }
                            break;
                        }
                        case 's':
                        {
                            string rest = body.Substring(j + 1);
                            if (rest.Length > 0 && IsDigitRun(rest, out var n))
                            {
                                skipChars = n;
                                j = body.Length;
                            }
                            else
                            {
                                i++;
                                if (i < rawArgs.Length && int.TryParse(rawArgs[i], out var nv))
                                {
                                    skipChars = nv;
                                }
                                j = body.Length;
                            }
                            break;
                        }
                        case 'w':
                        {
                            string rest = body.Substring(j + 1);
                            if (rest.Length > 0 && IsDigitRun(rest, out var n))
                            {
                                checkChars = n;
                                j = body.Length;
                            }
                            else
                            {
                                i++;
                                if (i < rawArgs.Length && int.TryParse(rawArgs[i], out var nv))
                                {
                                    checkChars = nv;
                                }
                                j = body.Length;
                            }
                            break;
                        }
                        default:
                            _optionErrorToken = $"-{ch}";
                            j = body.Length; // exit inner while
                            break;
                    }
                }
                if (_optionErrorToken != null) { _suppressStdin = true; return; }
                i++;
                continue;
            }

            operands.Add(arg);
            i++;
        }

        // Publish the parsed flags to instance state so the streamed
        // ProcessRecord and EndProcessing share them.
        // Bare -D binds to the -d decoy (case-insensitive), so recover the
        // distinct uppercase -D from the raw invocation line.
        // Scope the -D recovery scan to uniq's own pipeline segment so another command's
        // uppercase -D cannot leak in as uniq's --all-repeated.
        var rawLine = BashRuntime.CurrentPipelineSegment(MyInvocation);
        if (System.Text.RegularExpressions.Regex.IsMatch(rawLine, @"(?<![\w-])-D(?![\w])"))
        {
            _allRepeated = true;
        }

        _countMode = countMode;
        _duplicatesOnly = duplicatesOnly;
        _ignoreCase = ignoreCase;
        _uniqueOnly = uniqueOnly;
        _skipFields = skipFields;
        _skipChars = skipChars;
        _checkChars = checkChars;
        _operands = operands;
        _suppressStdin = operands.Count > 0;
    }

    private void FlushRun()
    {
        if (_prevLine == null) return;

        // -D / --all-repeated: emit EVERY line of a duplicate run (count >= 2),
        // not a single representative. No count prefix (GNU rejects -cD).
        if (_allRepeated)
        {
            if (_runCount < 2) return;
            for (int k = 0; k < _runCount; k++)
            {
                WriteObject(BashRuntime.NewBashObject(_prevLine));
            }
            return;
        }

        if (_duplicatesOnly && _runCount < 2) return;
        if (_uniqueOnly && _runCount > 1) return;

        if (_countMode)
        {
            string text = string.Format("{0,7} {1}", _runCount, _prevLine);
            WriteObject(BashRuntime.NewBashObject(text));
        }
        else
        {
            WriteObject(BashRuntime.NewBashObject(_prevLine));
        }
    }

    private void ProcessLine(string line)
    {
        string key = GetUniqKey(line, _skipFields, _skipChars, _checkChars);
        bool same;
        if (_prevKey == null)
        {
            same = false;
        }
        else if (_ignoreCase)
        {
            same = string.Equals(key, _prevKey, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            same = string.Equals(key, _prevKey, StringComparison.Ordinal);
        }

        if (same)
        {
            _runCount++;
            return;
        }

        FlushRun();
        _prevLine = line;
        _prevKey = key;
        _runCount = 1;
    }

    protected override void ProcessRecord()
    {
        if (InputObject == null) return;

        ParseOnce();
        if (_suppressStdin) return;

        // Stream the adjacent dedup instead of buffering the pipe — uniq only
        // needs the current run (prev line/key + count), never the whole input.
        string text = BashRuntime.GetBashText(InputObject);
        string trimmed = text.TrimEnd('\n');
        if (trimmed.Contains('\n'))
        {
            foreach (var subLine in trimmed.Split('\n'))
            {
                ProcessLine(subLine);
            }
        }
        else
        {
            ProcessLine(trimmed);
        }
    }

    protected override void EndProcessing()
    {
        ParseOnce();

        var rawArgs = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "uniq", rawArgs)) return;
        if (Array.IndexOf(rawArgs, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "uniq"))
            {
                WriteObject(line);
            }
            return;
        }

        // Deferred parse failure: an unrecognized or unsupported flag was found.
        if (_optionErrorToken != null)
        {
            FileSystemHelpers.WriteOptionError(this, "uniq", _optionErrorToken, UniqValidButUnsupported);
            return;
        }

        // File mode: stdin was suppressed; read each operand. Pipeline mode
        // (no operands) already streamed its lines through ProcessRecord.
        if (_operands.Count > 0)
        {
            foreach (var raw in _operands)
            {
                foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
                {
                    try
                    {
                        foreach (var line in BashFileSystem.ReadLines(filePath))
                        {
                            ProcessLine(line);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                        WriteReadError(filePath, ex);
                        _hadError = true;
                    }
                }
            }
        }

        // Flush the final run (the buffered oracle's single trailing FlushRun).
        FlushRun();

        if (_hadError)
        {
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
    }

    private static bool IsNumericFlag(string arg)
    {
        if (arg.Length < 2) return false;
        return char.IsDigit(arg[1]);
    }

    private static bool IsDigitRun(string s, out int value)
    {
        int end = 0;
        while (end < s.Length && char.IsDigit(s[end])) end++;
        if (end == 0 || end != s.Length)
        {
            value = 0;
            return false;
        }
        return int.TryParse(s, out value);
    }

    private static string GetUniqKey(string line, int skipFields, int skipChars, int checkChars)
    {
        string key = line;

        if (skipFields > 0)
        {
            int unboundedFieldCount = CountWhitespaceFields(key);
            if (unboundedFieldCount > skipFields)
            {
                key = ConsumeSkipFields(line, skipFields);
            }
            else
            {
                key = "";
            }
        }

        if (skipChars > 0 && key.Length > skipChars)
        {
            key = key.Substring(skipChars);
        }
        else if (skipChars > 0)
        {
            key = "";
        }

        if (checkChars > 0 && key.Length > checkChars)
        {
            key = key.Substring(0, checkChars);
        }

        return key;
    }

    private static readonly Regex s_wsRun = new(@"\s+", RegexOptions.Compiled);

    private static int CountWhitespaceFields(string s)
    {
        // Same shape as PowerShell's `$s -split '\s+'` (unbounded). `Regex.Split`
        // yields (whitespace-run count + 1) pieces because `\s+` never matches
        // empty; count the runs allocation-free with the same `\s+` semantics
        // instead of materializing — and discarding — the split array per line.
        int runs = 0;
        foreach (var _ in s_wsRun.EnumerateMatches(s)) runs++;
        return runs + 1;
    }

    private static string ConsumeSkipFields(string line, int skipFields)
    {
        // Reproduce `$line -split '\s+', (N+1)` then `parts[N]` selection
        // by walking N fields. A leading whitespace run counts as an empty
        // first field.
        int idx = 0;
        int len = line.Length;
        for (int n = 0; n < skipFields; n++)
        {
            while (idx < len && !char.IsWhiteSpace(line[idx])) idx++;
            while (idx < len && char.IsWhiteSpace(line[idx])) idx++;
        }
        if (idx >= len) return "";
        return line.Substring(idx);
    }

    private void WriteReadError(string path, Exception ex)
    {
        bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
            || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
        string msg = notFound ? "No such file or directory" : ex.Message;
        string normalized = path.Replace('\\', '/');
        FileSystemHelpers.WriteBashError(this, $"uniq: {normalized}: {msg}");
    }
}
