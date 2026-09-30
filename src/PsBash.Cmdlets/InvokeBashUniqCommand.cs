using System.Linq;
using System.Management.Automation;
using System.Text.RegularExpressions;
using PsBash.Cmdlets.Args;

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
/// <c>-u</c>, <c>-f</c>, <c>-s</c> have no PS common-parameter prefix collision
/// and stay in <see cref="Arguments"/>; they are parsed (separated and joined
/// forms) by the manual value-flag scan. <c>-w N</c> (compare at most N chars) is
/// the exception: a bare <c>-w</c> prefix-collides with <c>-WarningAction</c> /
/// <c>-WarningVariable</c> and the binder crashes ("ambiguous"), so the SEPARATE
/// form is captured by the declared value-bearing decoy <see cref="W"/>; the
/// joined (<c>-w3</c>) and bundled forms still land in <see cref="Arguments"/> and
/// are recovered by the scan. Bundled forms
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

    /// <summary>
    /// <c>-w N</c> (compare at most N chars). Value-bearing decoy for the
    /// <c>-WarningAction</c>/<c>-WarningVariable</c> ambiguous-prefix collision on a
    /// bare <c>-w</c>. Joined/bundled forms are still recovered from
    /// <see cref="Arguments"/> by the manual scan.
    /// </summary>
    [Parameter]
    public int? W { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>
    /// Valid GNU <c>uniq</c> options ps-bash does not implement, refused loudly (exit 2) by the
    /// shared parser: <c>-z/--zero-terminated</c> (NUL records) and <c>--group[=METHOD]</c>.
    /// (A string[] on purpose: CommonParameterCollisionGuardTests enumerates static string sets.)
    /// </summary>
    private static readonly string[] UniqValidButUnsupported =
    {
        "-z", "--zero-terminated",
        "--group",
    };

    private const string OptCount = "count", OptRepeated = "repeated", OptAllRepeated = "allrepeated",
        OptSkipFields = "skipfields", OptSkipChars = "skipchars", OptIgnoreCase = "ignorecase",
        OptUnique = "unique", OptCheckChars = "checkchars";

    /// <summary>
    /// uniq's option surface (GNU coreutils 9.4: -c -d -D -f -i -s -u -w -z + --count --repeated
    /// --all-repeated[=METHOD] --skip-fields --group[=METHOD] --ignore-case --skip-chars --unique
    /// --check-chars --zero-terminated; obsolete <c>-N</c> = <c>-f N</c>). <c>-D</c> and
    /// <c>--all-repeated</c> share an id: the short form takes no value, the long an optional
    /// attached METHOD. Built once for the shared ordered parser.
    /// </summary>
    private static readonly OptSpecSet UniqSpec = new(
        new[]
        {
            new OptSpec(OptCount, 'c', "count"),
            new OptSpec(OptRepeated, 'd', "repeated"),
            new OptSpec(OptAllRepeated, 'D', null),
            new OptSpec(OptAllRepeated, '\0', "all-repeated", OptKind.OptionalValue),
            new OptSpec(OptSkipFields, 'f', "skip-fields", OptKind.Value),
            new OptSpec(OptIgnoreCase, 'i', "ignore-case"),
            new OptSpec(OptSkipChars, 's', "skip-chars", OptKind.Value),
            new OptSpec(OptUnique, 'u', "unique"),
            new OptSpec(OptCheckChars, 'w', "check-chars", OptKind.Value),
        },
        validButUnsupported: UniqValidButUnsupported,
        allowAbbrev: true,
        numericShorthandId: OptSkipFields,
        gnuInfoOptions: true);

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, UniqSpec);

    /// <summary>The resolved meaning of a uniq argv (shared by the cmdlet and the fused core).</summary>
    internal sealed class UniqArgs
    {
        public ParsedArgs Parsed = null!;
        public bool Count, Repeated, Unique, IgnoreCase, AllRepeated;
        /// <summary><c>none</c> (default), <c>prepend</c> or <c>separate</c> — only meaningful with <see cref="AllRepeated"/>.</summary>
        public string AllRepeatedMethod = "none";
        public int SkipFields, SkipChars;
        /// <summary>-w N: compare at most N chars; -1 = unlimited (unset). NOTE <c>-w 0</c> means "compare nothing" (GNU).</summary>
        public int CheckChars = -1;
        public List<string> Operands = new();
        /// <summary>A usage error the scan itself cannot see (bad number, bad METHOD, -c with -D); exit 1.</summary>
        public string? Error;

        /// <summary>True when nothing further should execute: scan error, value error, --help/--version.</summary>
        public bool Declined =>
            Parsed.HasError || Error is not null
            || Parsed.Has(OptSpecSet.HelpId) || Parsed.Has(OptSpecSet.VersionId);
    }

    /// <summary>
    /// Scan + resolve with GNU's value rules: -f/-s/-w take a non-negative decimal (anything else is
    /// "invalid number of fields to skip" etc., exit 1; huge values saturate), <c>--all-repeated=M</c>
    /// takes any unique prefix of none|prepend|separate, and <c>-c</c> with <c>-D</c> is refused
    /// ("printing all duplicated lines and repeat counts is meaningless").
    /// </summary>
    internal static UniqArgs Plan(string[] args)
    {
        var p = ScanArgs(args);
        var u = new UniqArgs
        {
            Parsed = p,
            Count = p.Has(OptCount),
            Repeated = p.Has(OptRepeated),
            Unique = p.Has(OptUnique),
            IgnoreCase = p.Has(OptIgnoreCase),
            AllRepeated = p.Has(OptAllRepeated),
            Operands = p.Operands(),
        };
        if (p.HasError) return u;

        foreach (var tok in p.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            switch (tok.OptId)
            {
                case OptSkipFields:
                    if (!TryCount(tok.Value!, out int sf)) { u.Error = $"uniq: {tok.Value}: invalid number of fields to skip"; return u; }
                    u.SkipFields = sf;
                    break;
                case OptSkipChars:
                    if (!TryCount(tok.Value!, out int sc)) { u.Error = $"uniq: {tok.Value}: invalid number of bytes to skip"; return u; }
                    u.SkipChars = sc;
                    break;
                case OptCheckChars:
                    if (!TryCount(tok.Value!, out int cc)) { u.Error = $"uniq: {tok.Value}: invalid number of bytes to compare"; return u; }
                    u.CheckChars = cc;
                    break;
                case OptAllRepeated when tok.Value is { } method:
                    string? full = MatchMethod(method);
                    if (full is null)
                    {
                        u.Error = $"uniq: invalid argument '{method}' for '--all-repeated'\n"
                                  + "Valid arguments are:\n  - 'none'\n  - 'prepend'\n  - 'separate'";
                        return u;
                    }
                    u.AllRepeatedMethod = full;
                    break;
            }
        }

        if (u.AllRepeated && u.Count)
            u.Error = "uniq: printing all duplicated lines and repeat counts is meaningless";
        return u;
    }

    /// <summary>Non-negative decimal (no sign, no suffix); values beyond int saturate.</summary>
    private static bool TryCount(string s, out int value)
    {
        value = 0;
        if (s.Length == 0) return false;
        long acc = 0;
        foreach (char c in s)
        {
            if (c < '0' || c > '9') return false;
            acc = Math.Min(acc * 10 + (c - '0'), int.MaxValue);
        }
        value = (int)acc;
        return true;
    }

    private static string? MatchMethod(string v)
    {
        if (v.Length == 0) return null;
        foreach (var m in new[] { "none", "prepend", "separate" })
            if (m.StartsWith(v, StringComparison.Ordinal)) return m;
        return null;
    }

    /// <summary>
    /// Arguments with the decoy-bound flags re-injected (bare -c/-d/-i/-w bind -Confirm-like
    /// switches, -Debug, -Information*, -WarningAction). A bare <c>-D</c> binds the -d decoy
    /// case-insensitively, so the distinct uppercase form is recovered from the raw invocation
    /// line — scoped to uniq's own pipeline segment so another command's -D cannot leak in.
    /// A transpiled <c>'-D'</c> is a quoted string in Arguments and never needs this.
    /// </summary>
    private string[] ArgsWithDecoys()
    {
        var raw = Arguments ?? Array.Empty<string>();
        var pre = new List<string>();
        if (C.IsPresent) pre.Add("-c");
        if (D.IsPresent)
        {
            var rawLine = BashRuntime.CurrentPipelineSegment(MyInvocation);
            pre.Add(Regex.IsMatch(rawLine, @"(?<![\w-])-D(?![\w])") ? "-D" : "-d");
        }
        if (I.IsPresent) pre.Add("-i");
        if (W.HasValue) { pre.Add("-w"); pre.Add(W.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        return pre.Count == 0 ? raw : pre.Concat(raw).ToArray();
    }

    // Parsed-once state.
    private bool _parsed;
    private UniqArgs? _plan;
    private bool _countMode, _duplicatesOnly, _ignoreCase, _uniqueOnly, _allRepeated;
    private string _allRepeatedMethod = "none";
    private int _groupsEmitted;
    private int _skipFields, _skipChars, _checkChars = -1;
    private List<string> _operands = new();
    // True when stdin must NOT be streamed: file operands present (file mode
    // ignores stdin), a scan/value error, or a --help / --version request.
    private bool _suppressStdin;
    // Adjacent-dedup state — uniq only needs the current run, never the whole
    // pipe. Instance state so a streamed stdin run carries across records.
    private string? _prevLine;
    private object? _prevObject;   // the upstream record of _prevLine (single-line pipeline items only)
    private string? _prevKey;
    private int _runCount;
    // -D only: every line of the current run with its upstream record (null for a split
    // multi-line record or a file line), so the run is emitted as the input lines themselves.
    private readonly List<(string Line, object? Obj)> _runMembers = new();
    private bool _hadError;

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

        // Shared ordered parser: bundles (-cdi), attached values (-f1, --skip-fields=1), long forms
        // and unique-prefix abbreviations, the obsolete -N, `--`, options after operands, and the
        // unsupported/unknown classifier in ONE scan. A scan or value error is reported from
        // EndProcessing; stdin is not streamed for it.
        var plan = Plan(args);
        _plan = plan;
        _countMode = plan.Count;
        _duplicatesOnly = plan.Repeated;
        _ignoreCase = plan.IgnoreCase;
        _uniqueOnly = plan.Unique;
        _allRepeated = plan.AllRepeated;
        _allRepeatedMethod = plan.AllRepeatedMethod;
        _skipFields = plan.SkipFields;
        _skipChars = plan.SkipChars;
        _checkChars = plan.CheckChars;
        _operands = plan.Operands;
        _suppressStdin = plan.Declined || _operands.Count > 0;
    }
    private void FlushRun()
    {
        if (_prevLine == null) return;

        // -D / --all-repeated: emit EVERY line of a duplicate run (count >= 2),
        // not a single representative. No count prefix (GNU rejects -cD).
        if (_allRepeated)
        {
            if (_runCount < 2) return;
            // --all-repeated=prepend: a blank line before EVERY group; =separate: between groups.
            if (_allRepeatedMethod == "prepend" || (_allRepeatedMethod == "separate" && _groupsEmitted > 0))
                WriteObject(BashRuntime.NewBashObject(string.Empty));
            _groupsEmitted++;
            // -D is a FILTER too: every member of the run is one of the input lines (with
            // -f/-s/-w they may differ textually), so emit each member's ORIGINAL object.
            foreach (var (memberLine, memberObj) in _runMembers)
            {
                WriteObject(memberObj != null
                    ? BashRuntime.PassTerminated(memberObj)
                    : BashRuntime.NewBashObject(memberLine));
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
            // Plain uniq / -d / -u is a FILTER: the output line IS the first line of its run,
            // so the ORIGINAL upstream object passes through (uniq always terminates the line,
            // hence PassTerminated strips a stale missing-newline flag).
            WriteObject(_prevObject != null
                ? BashRuntime.PassTerminated(_prevObject)
                : BashRuntime.NewBashObject(_prevLine));
        }
    }

    private void ProcessLine(string line, object? original = null)
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
            if (_allRepeated) _runMembers.Add((line, original));
            return;
        }

        FlushRun();
        _prevLine = line;
        _prevObject = original;
        _prevKey = key;
        _runCount = 1;
        if (_allRepeated)
        {
            _runMembers.Clear();
            _runMembers.Add((line, original));
        }
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
            ProcessLine(trimmed, InputObject);
        }
    }

    protected override void EndProcessing()
    {
        ParseOnce();

        var rawArgs = ArgsWithDecoys();

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

        if (_plan is { } plan)
        {
            if (FileSystemHelpers.TryWriteParseError(this, "uniq", plan.Parsed)) return;
            if (FileSystemHelpers.TryHandleInfoOptions(this, "uniq", plan.Parsed)) return;
            if (plan.Error is { } planError)
            {
                FileSystemHelpers.WriteBashError(this, planError); // exit 1, GNU's usage status
                return;
            }
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

        // -w N compares at most N chars; N = 0 compares NOTHING (GNU), -1 = unlimited.
        if (checkChars >= 0 && key.Length > checkChars)
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
