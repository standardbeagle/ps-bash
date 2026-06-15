using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashCut</c> function
/// (REFACTOR-2 follow-on). Reproduces GNU coreutils <c>cut</c>: extract a
/// list of fields (<c>-f LIST</c> with <c>-d DELIM</c>) or byte/char ranges
/// (<c>-c LIST</c>) from each input line.
///
/// Behavioral parity oracle: the original psm1 <c>Invoke-BashCut</c>. File +
/// pipeline dual mode:
/// <list type="bullet">
/// <item><b>Pipeline mode</b> — when there are no operands and pipeline input
/// is present, each pipeline item's <c>BashText</c> is split on <c>\n</c>
/// after trailing-newline trim; each resulting sub-line is processed and
/// emitted as a <c>PsBash.TextOutput</c> object (matching the oracle's
/// defensive-split path).</item>
/// <item><b>File mode</b> — operands are treated as file paths (glob-expanded
/// via <see cref="FileSystemHelpers.ResolveOperandPaths"/>). Each file is
/// read with CRLF normalization (matching the oracle's
/// <c>Read-BashFileLines</c> / <c>StreamReader.ReadLine()</c> semantics — a
/// trailing newline does NOT yield a spurious empty final line). Missing
/// files emit a bash-style error via the psm1 <c>Write-BashError</c> sink
/// and are skipped (the oracle's <c>$null</c>-from-Read-BashFileLines branch
/// continues to the next operand).</item>
/// </list>
///
/// Per-line behavior matches the oracle byte-for-byte:
/// <list type="bullet">
/// <item><c>-c LIST</c> selects character positions (1-based). Each parsed
/// index becomes a single char in the output; out-of-range indices are
/// silently dropped (the oracle's <c>$idx -ge 0 -and $idx -lt $Line.Length</c>
/// guard).</item>
/// <item><c>-f LIST</c> splits the line on <c>-d DELIM</c> (default tab) and
/// selects the listed fields (1-based), joined back with the same delimiter.
/// Missing-delim lines: the oracle's <c>Split($delimiter)</c> returns the
/// whole line as one field, so <c>-f 1</c> emits the whole line and
/// <c>-f 2</c> emits nothing (no field index 2 exists). This is GNU
/// <c>cut</c>'s actual behavior only when neither <c>-s</c> nor a different
/// flag is specified; the psm1 oracle does not implement <c>-s</c>, and we
/// preserve that.</item>
/// <item>No <c>-c</c> and no <c>-f</c> — the line passes through unchanged.</item>
/// </list>
///
/// List parsing (<c>ParseSpec</c>) matches the oracle: comma-separated parts,
/// each part either <c>N-M</c> (inclusive range) or <c>N</c> (single index).
/// The oracle does NOT implement open ranges (<c>N-</c> / <c>-M</c>) — neither
/// branch matches <c>^\d+-\d+$</c>, so a bare dash-token in either position
/// falls through to <c>[int]$part</c> and throws. We preserve that exact
/// failure surface here (we do not add open-range support beyond the oracle).
///
/// Flag binding: <c>-d</c> prefix-collides with the <c>-Debug</c> common
/// parameter and <c>-c</c> prefix-collides with <c>-Confirm</c>. Both are
/// declared as explicit value-bearing parameters with single-letter names
/// (<see cref="D"/> / <see cref="C"/> — both <c>string?</c>); the binder
/// routes a bare token by exact parameter name, which beats a
/// common-parameter prefix match. A <see cref="System.Management.Automation.AliasAttribute"/>
/// on a longer parameter name would NOT be sufficient (aliases lose to
/// common-parameter prefix matches under the cmdlet binder). <c>-f</c> has no
/// PowerShell common-parameter prefix collision and is recovered from
/// <see cref="Arguments"/> by a manual scan. The joined short forms
/// <c>-dC</c>, <c>-fLIST</c>, <c>-cLIST</c> (per the oracle's
/// <c>^-d(.)$</c> / <c>^-f(.+)$</c> / <c>^-c(.+)$</c> patterns) are recovered
/// from <see cref="Arguments"/> post-parse. <c>--</c> ends flag parsing.
///
/// AOT safety: no <see cref="ScriptBlock"/> construction; <c>--help</c>
/// delegates to psm1 <c>Show-BashHelp</c> via parameter-bound
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>.
/// File-read failures route through
/// <see cref="FileSystemHelpers.WriteBashError"/>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashCut")]
[OutputType(typeof(string))]
public sealed class InvokeBashCutCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>
    /// The bash <c>-d DELIM</c> (delimiter) value flag — declared explicitly
    /// because the bare token <c>-d</c> prefix-collides with the <c>-Debug</c>
    /// common parameter. Exact parameter-name match beats a common-parameter
    /// prefix match, so the parameter is literally named <c>D</c>. The joined
    /// form <c>-dC</c> (single-char delimiter immediately after the flag)
    /// lands in <see cref="Arguments"/> and is recovered post-parse to match
    /// the oracle's <c>^-d(.)$</c> branch.
    /// </summary>
    [Parameter]
    public string? D { get; set; }

    /// <summary>
    /// The bash <c>-c LIST</c> (character-positions list) value flag —
    /// declared explicitly because the bare token <c>-c</c> prefix-collides
    /// with the <c>-Confirm</c> common parameter. Exact parameter-name match
    /// beats a common-parameter prefix match, so the parameter is literally
    /// named <c>C</c>. The joined form <c>-cLIST</c> lands in
    /// <see cref="Arguments"/> and is recovered post-parse to match the
    /// oracle's <c>^-c(.+)$</c> branch.
    /// </summary>
    [Parameter]
    public string? C { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    // Parsed-once state.
    private bool _parsed;
    private string _delimiter = "\t";
    private string _fieldSpec = string.Empty;
    private string _charSpec = string.Empty;
    private List<string> _operands = new();
    // Selected ranges (1-based, inclusive). hi == int.MaxValue means "to the
    // end of the line/record" — an OPEN range (`-f2-`). These are resolved
    // against each line's actual char/field count in EmitCutLine, which is why
    // open ranges can't be flattened to a fixed index array at parse time.
    private List<(int Lo, int Hi)>? _ranges;
    // -s: in field mode, drop lines that contain no delimiter (GNU --only-delimited).
    private bool _suppressNonDelimited;
    // --output-delimiter=STR: rejoin selected fields with STR instead of the input delimiter.
    private string? _outputDelimiter;
    // Deferred parse failures: emitted in EndProcessing (ProcessRecord runs
    // first and must not write the error twice). Either of these also
    // suppresses stdin streaming.
    private string? _optionErrorToken;
    private string? _invalidListMsg;
    private string? _cutRangeMsg;
    // True when stdin must NOT be streamed: file operands present, a
    // --help / --version request, or a deferred parse error. Matches the
    // buffered oracle, which only consumed the pipeline in pipeline mode.
    private bool _suppressStdin;

    /// <summary>
    /// Valid GNU <c>cut</c> options ps-bash does not implement. Hitting one
    /// yields a specific "recognized but not supported" message via
    /// <see cref="FileSystemHelpers.WriteOptionError"/> instead of the old
    /// misleading "No such file or directory" (the token used to fall through
    /// to the file-operand list). Anything option-looking NOT in this set is
    /// reported as unrecognized/invalid (bash parity). Representative — see the
    /// per-command flag-catalog rollout.
    /// </summary>
    private static readonly HashSet<string> ValidButUnsupported = new(StringComparer.Ordinal)
    {
        "-b", "-n", "-z",
        "--bytes", "--characters", "--fields", "--delimiter",
        "--complement",
        "--zero-terminated",
    };

    private void ParseOnce()
    {
        if (_parsed) return;
        _parsed = true;

        var args = Arguments ?? Array.Empty<string>();

        // --help / --version short-circuit before any flag/spec parsing
        // (oracle order — EndProcessing checked them first).
        if (Array.IndexOf(args, "--version") >= 0 || Array.IndexOf(args, "--help") >= 0)
        {
            _suppressStdin = true;
            return;
        }

        // Default delimiter is a tab (oracle). PowerShell's colon-syntax
        // (-Param:Value) treats `-d:` as "param -d with value...", consuming
        // the NEXT token as the value. So `-d: -f2` ends up binding
        // D="-f2", and `-d: -f1,3` errors on the array conversion. To honor
        // the bash convention (`-d:` means delimiter is colon), detect the
        // joined literal in MyInvocation.Line and override.
        _delimiter = D ?? "\t";
        var rawLine = MyInvocation?.Line ?? string.Empty;
        if (!string.IsNullOrEmpty(rawLine))
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                rawLine, @"(?<![A-Za-z0-9])-d(\S)(?!\w)");
            if (m.Success)
            {
                _delimiter = m.Groups[1].Value;
                // The PowerShell binder may have consumed the NEXT token as
                // D's value (e.g. -d: -f2 → D="-f2"). Re-inject that token
                // into Arguments so the manual scan picks up the -f.
                if (D != null && D.StartsWith("-"))
                {
                    var newArgs = new List<string>(args.Length + 1) { D };
                    newArgs.AddRange(args);
                    args = newArgs.ToArray();
                }
            }
        }
        _charSpec = C ?? string.Empty;
        bool pastDoubleDash = false;

        int i = 0;
        while (i < args.Length)
        {
            string a = args[i];
            if (pastDoubleDash)
            {
                _operands.Add(a);
                i++;
                continue;
            }

            if (a == "--")
            {
                pastDoubleDash = true;
                i++;
                continue;
            }

            // Bare `-d` is bound to the D parameter by the binder, but a
            // joined `-dC` lands here (oracle: ^-d(.)$).
            if (a.Length == 3 && a[0] == '-' && a[1] == 'd')
            {
                _delimiter = a.Substring(2, 1);
                i++;
                continue;
            }

            // Bare `-f` flag — value follows in next arg.
            if (a == "-f")
            {
                i++;
                if (i < args.Length)
                {
                    _fieldSpec = args[i];
                }
                i++;
                continue;
            }

            // Joined `-fLIST` form (oracle: ^-f(.+)$).
            if (a.Length > 2 && a[0] == '-' && a[1] == 'f')
            {
                _fieldSpec = a.Substring(2);
                i++;
                continue;
            }

            // Joined `-cLIST` form (oracle: ^-c(.+)$). The bare `-c` is
            // already bound to the C parameter by the binder.
            if (a.Length > 2 && a[0] == '-' && a[1] == 'c')
            {
                _charSpec = a.Substring(2);
                i++;
                continue;
            }

            // -s / --only-delimited: field mode drops lines with no delimiter.
            if (a == "-s" || a == "--only-delimited")
            {
                _suppressNonDelimited = true;
                i++;
                continue;
            }

            // --output-delimiter=STR (and the rare separate-arg form).
            if (a.StartsWith("--output-delimiter=", StringComparison.Ordinal))
            {
                _outputDelimiter = a.Substring("--output-delimiter=".Length);
                i++;
                continue;
            }
            if (a == "--output-delimiter")
            {
                i++;
                if (i < args.Length) _outputDelimiter = args[i];
                i++;
                continue;
            }

            // Any remaining option-looking token is a flag cut doesn't handle,
            // not a file operand: valid-but-unsupported → specific refusal,
            // otherwise bash-parity "unrecognized option". A lone "-" (stdin)
            // is not option-like and falls through to operands. The error is
            // deferred to EndProcessing (ProcessRecord runs first).
            if (FileSystemHelpers.IsOptionLike(a))
            {
                _optionErrorToken = a;
                _suppressStdin = true;
                return;
            }

            _operands.Add(a);
            i++;
        }

        // Pre-parse the active spec once into (possibly open-ended) ranges.
        try
        {
            if (_charSpec.Length > 0)
            {
                _ranges = ParseRanges(_charSpec, isField: false);
            }
            else if (_fieldSpec.Length > 0)
            {
                _ranges = ParseRanges(_fieldSpec, isField: true);
            }
        }
        catch (CutRangeError ex)
        {
            // GNU cut rejects a zero position (`fields are numbered from 1`) and a
            // decreasing range (`-f3-1`) with a dedicated message and exit 1 —
            // distinct from the generic "invalid list" malformed-token path. Before
            // this they silently produced empty output.
            _cutRangeMsg = ex.Message;
            _suppressStdin = true;
            return;
        }
        catch (FormatException ex)
        {
            // Oracle: [int]$part on a non-integer token throws; we surface
            // the same failure mode via a bash-style error and bail (deferred
            // to EndProcessing).
            _invalidListMsg = ex.Message;
            _suppressStdin = true;
            return;
        }

        _suppressStdin = _operands.Count > 0;
    }

    private void EmitCutLine(string line)
    {
        string result;
        if (_charSpec.Length > 0)
        {
            var sb = new StringBuilder();
            foreach (var pos in ExpandRanges(_ranges!, line.Length))
            {
                int idx = pos - 1;
                if (idx >= 0 && idx < line.Length)
                {
                    sb.Append(line[idx]);
                }
            }
            result = sb.ToString();
        }
        else if (_fieldSpec.Length > 0)
        {
            // String.Split(string) — single-string separator overload
            // matches PowerShell's .Split($string) behavior when the
            // delimiter is a multi-char string. The oracle uses
            // $Line.Split($delimiter) where $delimiter may be 1 char or
            // a string passed via -d. .NET's Split(string) splits on the
            // string as a substring boundary.
            // Single-string Split overload — same substring-boundary semantics
            // as Split(new[]{ _delimiter }, None) but without allocating a
            // one-element separator array on every line.
            string[] fields = line.Split(_delimiter, StringSplitOptions.None);
            // -s: a line with no delimiter splits into a single field; GNU
            // --only-delimited suppresses it entirely.
            if (_suppressNonDelimited && fields.Length <= 1) return;
            var picks = new List<string>();
            foreach (var pos in ExpandRanges(_ranges!, fields.Length))
            {
                int fi = pos - 1;
                if (fi >= 0 && fi < fields.Length)
                {
                    picks.Add(fields[fi]);
                }
            }
            result = string.Join(_outputDelimiter ?? _delimiter, picks);
        }
        else
        {
            result = line;
        }
        WriteObject(BashRuntime.NewBashObject(result));
    }

    protected override void ProcessRecord()
    {
        if (InputObject == null) return;

        ParseOnce();
        if (_suppressStdin) return;

        // Pipeline mode: cut each stdin sub-line as it arrives instead of
        // buffering the whole pipe.
        string text = BashRuntime.GetBashText(InputObject);
        string trimmed = text.TrimEnd('\n');
        if (trimmed.Contains('\n'))
        {
            foreach (var sub in trimmed.Split('\n'))
            {
                EmitCutLine(sub);
            }
        }
        else
        {
            EmitCutLine(trimmed);
        }
    }

    protected override void EndProcessing()
    {
        ParseOnce();

        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "cut", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "cut"))
            {
                WriteObject(line);
            }
            return;
        }

        // Deferred parse failures (captured in ParseOnce).
        if (_optionErrorToken != null)
        {
            FileSystemHelpers.WriteOptionError(this, "cut", _optionErrorToken, ValidButUnsupported);
            return;
        }
        if (_cutRangeMsg != null)
        {
            FileSystemHelpers.WriteBashError(this, $"cut: {_cutRangeMsg}");
            return;
        }
        if (_invalidListMsg != null)
        {
            FileSystemHelpers.WriteBashError(this, $"cut: invalid list: {_invalidListMsg}");
            return;
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
                        EmitCutLine(line);
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

    /// <summary>
    /// Parse a cut list spec (comma-separated) into 1-based inclusive ranges.
    /// Each part is one of GNU cut's forms:
    /// <list type="bullet">
    /// <item><c>N</c> — single position → <c>(N, N)</c>.</item>
    /// <item><c>N-M</c> — closed range → <c>(N, M)</c>.</item>
    /// <item><c>N-</c> — open from N to end → <c>(N, int.MaxValue)</c>.</item>
    /// <item><c>-M</c> — open from start to M → <c>(1, M)</c>.</item>
    /// </list>
    /// Open ranges are resolved per line in <see cref="ExpandRanges"/> against
    /// the actual char/field count. A non-numeric token still throws
    /// <see cref="FormatException"/> (bash/oracle parity: invalid list).
    /// </summary>
    private static List<(int Lo, int Hi)> ParseRanges(string spec, bool isField)
    {
        // GNU cut: position 0 is rejected; the wording differs for -f vs -c/-b.
        string numbered = isField
            ? "fields are numbered from 1"
            : "byte/character positions are numbered from 1";
        var result = new List<(int, int)>();
        foreach (var partRaw in spec.Split(','))
        {
            string part = partRaw;
            int dash = part.IndexOf('-');
            if (dash >= 0)
            {
                string lo = part.Substring(0, dash);
                string hi = part.Substring(dash + 1);
                // -M  (open start)
                if (lo.Length == 0 && hi.Length > 0 && AllDigits(hi))
                {
                    int m = int.Parse(hi);
                    if (m == 0) throw new CutRangeError(numbered);
                    result.Add((1, m));
                    continue;
                }
                // N-  (open end)
                if (hi.Length == 0 && lo.Length > 0 && AllDigits(lo))
                {
                    int nlo = int.Parse(lo);
                    if (nlo == 0) throw new CutRangeError(numbered);
                    result.Add((nlo, int.MaxValue));
                    continue;
                }
                // N-M (closed)
                if (lo.Length > 0 && hi.Length > 0 && AllDigits(lo) && AllDigits(hi))
                {
                    int a = int.Parse(lo), b = int.Parse(hi);
                    if (a == 0 || b == 0) throw new CutRangeError(numbered);
                    if (a > b) throw new CutRangeError("invalid decreasing range");
                    result.Add((a, b));
                    continue;
                }
                // Malformed (e.g. bare "-", "a-b") — surface as invalid list.
                throw new FormatException($"invalid byte/character position '{part}'");
            }
            // Single index: [int]$part — non-integer throws (oracle parity).
            int n = int.Parse(part);
            if (n == 0) throw new CutRangeError(numbered);
            result.Add((n, n));
        }
        return result;
    }

    /// <summary>A GNU-specific cut list error (zero position / decreasing range)
    /// that is reported verbatim, not wrapped in the generic "invalid list" form.</summary>
    private sealed class CutRangeError : Exception
    {
        public CutRangeError(string message) : base(message) { }
    }

    /// <summary>
    /// Expand parsed ranges into the concrete 1-based positions for a line of
    /// <paramref name="max"/> chars/fields, preserving spec order (the oracle's
    /// behavior). An open range (<c>Hi == int.MaxValue</c>) runs to
    /// <paramref name="max"/>; positions past <paramref name="max"/> are simply
    /// not produced (EmitCutLine's index guard also drops any stragglers).
    /// </summary>
    private static IEnumerable<int> ExpandRanges(List<(int Lo, int Hi)> ranges, int max)
    {
        foreach (var (lo, hi) in ranges)
        {
            int end = hi == int.MaxValue ? max : hi;
            for (int n = lo; n <= end && n <= max; n++)
            {
                if (n >= 1) yield return n;
            }
        }
    }

    private static bool AllDigits(string s)
    {
        foreach (var c in s)
        {
            if (c < '0' || c > '9') return false;
        }
        return true;
    }

    private void WriteReadError(string path, Exception ex)
    {
        bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
            || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
        string msg = notFound ? "No such file or directory" : ex.Message;
        string normalized = path.Replace('\\', '/');
        FileSystemHelpers.WriteBashError(this, $"cut: {normalized}: {msg}");
    }
}
