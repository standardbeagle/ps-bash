using PsBash.Core;
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
    private static readonly string[] UniqValidButUnsupported = Array.Empty<string>();

    private const string OptCount = "count", OptRepeated = "repeated", OptAllRepeated = "allrepeated",
        OptSkipFields = "skipfields", OptSkipChars = "skipchars", OptIgnoreCase = "ignorecase",
        OptUnique = "unique", OptCheckChars = "checkchars", OptGroup = "group", OptZero = "zero";

    /// <summary>GNU uniq long_options[] order; getopt_long lists ambiguous-prefix candidates in it.</summary>
    private static readonly string[] UniqLongOptionOrder = { "count", "check-chars", "skip-fields", "skip-chars" };

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
            new OptSpec(OptGroup, '\0', "group", OptKind.OptionalValue),
            new OptSpec(OptZero, 'z', "zero-terminated"),
        },
        validButUnsupported: UniqValidButUnsupported,
        allowAbbrev: true,
        numericShorthandId: OptSkipFields,
        gnuInfoOptions: true,
        longOptionOrder: UniqLongOptionOrder);

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, UniqSpec);

    /// <summary>The resolved meaning of a uniq argv (shared by the cmdlet and the fused core).</summary>
    internal sealed class UniqArgs
    {
        public ParsedArgs Parsed = null!;
        public bool Count, Repeated, Unique, IgnoreCase, AllRepeated;
        /// <summary><c>none</c> (default), <c>prepend</c> or <c>separate</c> — only meaningful with <see cref="AllRepeated"/>.</summary>
        public string AllRepeatedMethod = "none";
        /// <summary><c>-z</c>: NUL-terminated records in and out.</summary>
        public bool Zero;
        /// <summary><c>--group[=METHOD]</c> (null = off): <c>separate</c> (default) | <c>prepend</c> | <c>append</c> | <c>both</c>.</summary>
        public string? GroupMethod;
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
            Zero = p.Has(OptZero),
            Operands = p.Operands(),
        };
        if (p.HasError) return u;

        // Operands in order. GNU: a non-option word `+N` (not after `--`) is the obsolete skip-chars
        // (-s N) — later of it and -s wins, like any option — and a third real operand is an error
        // (uniq takes INPUT [OUTPUT]).
        bool plusOk = ObsoletePlusAllowed(BashVariableStore.Get("_POSIX2_VERSION"));
        u.Operands = new List<string>();
        foreach (var tok in p.Tokens)
        {
            if (tok.Kind == ArgTokKind.Operand)
            {
                if (plusOk && !tok.AfterDoubleDash && TryParseObsoletePlus(tok.Raw, out int plusSkip))
                {
                    u.SkipChars = plusSkip;
                    continue;
                }
                if (u.Operands.Count == 2)
                {
                    u.Error = $"uniq: extra operand '{tok.Raw}'";
                    return u;
                }
                u.Operands.Add(tok.Raw);
                continue;
            }
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
                case OptGroup:
                    // GNU argmatch: an exact name or a unique prefix; no METHOD = separate.
                    string? g = tok.Value is null ? "separate" : MatchGroupMethod(tok.Value);
                    if (g is null)
                    {
                        u.Error = $"uniq: invalid argument '{tok.Value}' for '--group'\n"
                                  + "Valid arguments are:\n  - 'prepend'\n  - 'append'\n  - 'separate'\n  - 'both'";
                        return u;
                    }
                    u.GroupMethod = g;
                    break;
            }
        }

        if (u.GroupMethod is not null && (u.Count || u.Repeated || u.AllRepeated || u.Unique))
        {
            u.Error = "uniq: --group is mutually exclusive with -c/-d/-D/-u";
            return u;
        }
        if (u.AllRepeated && u.Count)
            u.Error = "uniq: printing all duplicated lines and repeat counts is meaningless";
        return u;
    }

    /// <summary>
    /// GNU uniq accepts the obsolete <c>+N</c> (skip N chars) unless <c>_POSIX2_VERSION</c> names a
    /// POSIX level that dropped it: oracle-checked, it is honoured for versions below 200112 and from
    /// 200809 up (and when unset), refused for 200112..200808, where <c>+N</c> is a file name.
    /// </summary>
    internal static bool ObsoletePlusAllowed(string? posix2Version)
    {
        if (string.IsNullOrEmpty(posix2Version)) return true;
        if (!long.TryParse(posix2Version, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long v)) return true;
        return v < 200112 || v >= 200809;
    }

    /// <summary><c>+DIGITS</c>: an unsigned decimal that fits 64 bits (a larger one is a file name, as in GNU); saturates to int.</summary>
    private static bool TryParseObsoletePlus(string raw, out int skip)
    {
        skip = 0;
        if (raw.Length < 2 || raw[0] != '+') return false;
        var digits = raw.AsSpan(1);
        foreach (char c in digits)
        {
            if (c < '0' || c > '9') return false;
        }
        if (!ulong.TryParse(digits, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out ulong n)) return false;
        skip = n > int.MaxValue ? int.MaxValue : (int)n;
        return true;
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

    private static string? MatchGroupMethod(string v)
    {
        if (v.Length == 0) return null;
        foreach (var m in new[] { "prepend", "append", "separate", "both" })
            if (m.StartsWith(v, StringComparison.Ordinal)) return m;
        return null;
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
    // OUTPUT operand (`uniq in out`): results go to this file instead of the pipeline. Opened on
    // the first line written, or at the end when the input is read without error (GNU creates it
    // even for an empty result, but never when the input could not be opened).
    private string? _outputFile;
    private StreamWriter? _outWriter;
    private bool _outFailed;

    private bool _zero;
    private string? _groupMethod;
    private readonly List<object> _zeroInput = new();

    private void Emit(object record)
    {
        if (_outputFile is null) { WriteObject(record); return; }
        var w = EnsureOutput();
        if (w is null) return;
        var text = BashRuntime.GetBashText(record);
        if (_zero) { w.Write(text); return; }   // a NUL record already carries its terminator
        if (text.EndsWith('\n')) text = text[..^1];
        w.Write(text);
        w.Write('\n');
    }

    /// <summary>A fresh output line: under <c>-z</c> an exact NUL-terminated record (its text may hold <c>\n</c>).</summary>
    private object Line(string text) => _zero ? NulRecords.Record(text) : BashRuntime.NewBashObject(text);

    private StreamWriter? EnsureOutput()
    {
        if (_outWriter is not null || _outFailed || _outputFile is null) return _outWriter;
        try
        {
            var path = SessionState.Path.GetUnresolvedProviderPathFromPSPath(_outputFile);
            _outWriter = new StreamWriter(path, false, RawBytes.Encoding);
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            _outFailed = true;
            _hadError = true;
            string why = ex switch
            {
                DirectoryNotFoundException => "No such file or directory",
                UnauthorizedAccessException => "Permission denied",
                _ => ex.Message,
            };
            FileSystemHelpers.WriteBashError(this, $"uniq: {_outputFile}: {why}");
        }
        return _outWriter;
    }

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
        _zero = plan.Zero;
        _groupMethod = plan.GroupMethod;
        _skipFields = plan.SkipFields;
        _skipChars = plan.SkipChars;
        _checkChars = plan.CheckChars;
        // uniq [INPUT [OUTPUT]]: the FIRST operand is the input (`-` = stdin), the SECOND the file the
        // result is written to (`-` = stdout) — it is never a second input.
        _operands = plan.Operands.Count > 0 && plan.Operands[0] != "-"
            ? new List<string> { plan.Operands[0] }
            : new List<string>();
        _outputFile = plan.Operands.Count > 1 && plan.Operands[1] != "-" ? plan.Operands[1] : null;
        _suppressStdin = plan.Declined || _operands.Count > 0;
    }
    private void FlushRun()
    {
        if (_prevLine == null) return;

        // --group: EVERY line of EVERY run (unique lines included), groups divided by an empty
        // line per METHOD: separate = between, prepend = before each, append = after each, both.
        if (_groupMethod is { } gm)
        {
            // both = a blank before every group (so one between groups and one at the start) plus
            // ONE after the last group (written at the end of the input, EndProcessing).
            if (gm is "prepend" or "both" || (gm == "separate" && _groupsEmitted > 0)) Emit(Line(string.Empty));
            _groupsEmitted++;
            foreach (var (memberLine, memberObj) in _runMembers)
                Emit(memberObj != null ? BashRuntime.PassTerminated(memberObj) : Line(memberLine));
            if (gm == "append") Emit(Line(string.Empty));
            return;
        }

        // -D / --all-repeated: emit EVERY line of a duplicate run (count >= 2),
        // not a single representative. No count prefix (GNU rejects -cD).
        if (_allRepeated)
        {
            if (_runCount < 2) return;
            // --all-repeated=prepend: a blank line before EVERY group; =separate: between groups.
            if (_allRepeatedMethod == "prepend" || (_allRepeatedMethod == "separate" && _groupsEmitted > 0))
                Emit(Line(string.Empty));
            _groupsEmitted++;
            // -D is a FILTER too: every member of the run is one of the input lines (with
            // -f/-s/-w they may differ textually), so emit each member's ORIGINAL object.
            foreach (var (memberLine, memberObj) in _runMembers)
            {
                Emit(memberObj != null
                    ? BashRuntime.PassTerminated(memberObj)
                    : Line(memberLine));
            }
            return;
        }

        if (_duplicatesOnly && _runCount < 2) return;
        if (_uniqueOnly && _runCount > 1) return;

        if (_countMode)
        {
            string text = string.Format("{0,7} {1}", _runCount, _prevLine);
            Emit(Line(text));
        }
        else
        {
            // Plain uniq / -d / -u is a FILTER: the output line IS the first line of its run,
            // so the ORIGINAL upstream object passes through (uniq always terminates the line,
            // hence PassTerminated strips a stale missing-newline flag).
            Emit(_prevObject != null
                ? BashRuntime.PassTerminated(_prevObject)
                : Line(_prevLine));
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
            if (KeepMembers) _runMembers.Add((line, original));
            return;
        }

        FlushRun();
        _prevLine = line;
        _prevObject = original;
        _prevKey = key;
        _runCount = 1;
        if (KeepMembers)
        {
            _runMembers.Clear();
            _runMembers.Add((line, original));
        }
    }

    private bool KeepMembers => _allRepeated || _groupMethod is not null;

    protected override void ProcessRecord()
    {
        if (InputObject == null) return;

        ParseOnce();
        if (_suppressStdin) return;
        if (_zero) { _zeroInput.Add(InputObject); return; }   // -z: the whole byte stream is re-split on NUL at the end

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
        if (_zero && _zeroInput.Count > 0)
            foreach (var rec in NulRecords.FromPipeline(_zeroInput)) ProcessLine(rec);
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
                        foreach (var line in _zero ? NulRecords.ReadFile(filePath) : BashFileSystem.ReadLines(filePath))
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
        if (_groupMethod == "both" && _groupsEmitted > 0) Emit(Line(string.Empty));
        if (_outputFile is not null && !_hadError) EnsureOutput();
        try { _outWriter?.Dispose(); }
        catch (IOException ex)
        {
            _hadError = true;
            FileSystemHelpers.WriteBashError(this, $"uniq: {_outputFile}: {ex.Message}");
        }
        _outWriter = null;

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
