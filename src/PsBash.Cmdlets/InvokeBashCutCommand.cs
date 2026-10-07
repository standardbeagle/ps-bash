using System.Management.Automation;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet for GNU coreutils <c>cut</c>: select byte (<c>-b</c>), character (<c>-c</c>) or
/// field (<c>-f</c> with <c>-d</c>) ranges from each input line, from files or the pipeline.
///
/// <para><b>Arguments</b> go through the shared ordered parser (<see cref="CutSpec"/>;
/// <c>cut</c> is on <c>PsEmitter.OrderedArgCommands</c>, so every flag reaches <c>Arguments</c>
/// verbatim and in order). <see cref="Plan"/> resolves and validates the whole argv into a
/// <see cref="CutPlan"/> with GNU's rules and messages (exit 1): exactly one of <c>-b/-c/-f</c>
/// ("only one list may be specified"), a single-character <c>-d</c> only with <c>-f</c>,
/// <c>-s</c> only with <c>-f</c>, a valid list (positions from 1, no decreasing range, no
/// overflow). <c>-z</c> is valid-but-unsupported (exit 2); <c>-n</c> is accepted and ignored, as
/// GNU 9.4. The per-line engine is <see cref="CutPlan.Apply"/> (shared with the fused core).</para>
///
/// <para><b>Direct PowerShell calls</b>: <c>-d</c> and <c>-c</c> prefix-collide with the
/// <c>-Debug</c> / <c>-Confirm</c> common parameters, so they are declared value-bearing
/// parameters <see cref="D"/> / <see cref="C"/> and re-injected as <c>-d VALUE</c> /
/// <c>-c VALUE</c> pairs. The colon spelling <c>Invoke-BashCut -d: -f2</c> binds <c>D</c> to the
/// NEXT token; it is recovered from the invocation segment and that token is re-injected.</para>
///
/// <para>Output is a transformer: fresh text records (see runtime-functions.md "Pipeline record
/// kinds"); <c>cut</c> always terminates the last line.</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashCut")]
[OutputType(typeof(string))]
public sealed class InvokeBashCutCommand : PSCmdlet
{
    private const string OptBytes = "bytes", OptChars = "characters", OptFields = "fields",
        OptDelim = "delimiter", OptOnly = "only-delimited", OptOutDelim = "output-delimiter",
        OptComplement = "complement";

    /// <summary>GNU <c>cut</c> options ps-bash refuses (exit 2): NUL-terminated records cannot be
    /// carried by the line-record pipeline.</summary>
    private static readonly string[] CutValidButUnsupported = { "-z", "--zero-terminated" };

    /// <summary>
    /// cut's option surface (GNU coreutils 9.4). Ambiguity lists follow GNU's table order
    /// (<c>--o</c> = <c>'--only-delimited' '--output-delimiter'</c>, <c>--c</c> = characters, complement).
    /// </summary>
    private static readonly OptSpecSet CutSpec = new(
        new[]
        {
            new OptSpec(OptBytes, 'b', "bytes", OptKind.Value),
            new OptSpec(OptChars, 'c', "characters", OptKind.Value),
            new OptSpec(OptFields, 'f', "fields", OptKind.Value),
            new OptSpec(OptDelim, 'd', "delimiter", OptKind.Value),
            new OptSpec("ignored-n", 'n', null),
            new OptSpec(OptOnly, 's', "only-delimited"),
            new OptSpec(OptOutDelim, '\0', "output-delimiter", OptKind.Value),
            new OptSpec(OptComplement, '\0', "complement"),
        },
        CutValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true,
        longOptionOrder: new[]
        {
            "bytes", "characters", "fields", "delimiter", "only-delimited", "output-delimiter",
            "complement", "zero-terminated",
        });

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, CutSpec);

    internal sealed class CutArgs
    {
        public ParsedArgs Parsed = null!;
        public CutPlan? Selection;
        public List<string> Operands = new();
        public string? Error;

        /// <summary>True when nothing may be processed: scan error, plan error, --help/--version.</summary>
        public bool Declined =>
            Parsed.HasError || Error is not null
            || Parsed.Has(OptSpecSet.HelpId) || Parsed.Has(OptSpecSet.VersionId);
    }

    /// <summary>
    /// Scan + validate in GNU's order. Fixes over the old hand scan: sorted/merged selection (old:
    /// spec order with duplicates), a delimiter-less line is printed whole in field mode (old: empty
    /// for <c>-f2</c>), <c>--complement</c>, <c>-b</c>, <c>--output-delimiter</c> between
    /// byte/char ranges, <c>-d</c> must be one character, only one of -b/-c/-f, <c>-s</c>/<c>-d</c>
    /// with -b/-c are errors, a missing list is an error, overflow is an error, options after
    /// operands, bundles, abbreviations.
    /// </summary>
    internal static CutArgs Plan(string[] args)
    {
        var c = new CutArgs { Parsed = ScanArgs(args) };
        c.Operands = c.Parsed.Operands();
        if (c.Parsed.HasError) return c;
        if (c.Parsed.Has(OptSpecSet.HelpId) || c.Parsed.Has(OptSpecSet.VersionId)) return c;

        CutMode? mode = null;
        string? list = null;
        string delimiter = "\t";
        bool delimSpecified = false, only = false, complement = false;
        string? outDelim = null;

        foreach (var tok in c.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            switch (tok.OptId)
            {
                case OptBytes or OptChars or OptFields:
                    if (list is not null) { c.Error = "cut: only one list may be specified"; return c; }
                    mode = tok.OptId == OptBytes ? CutMode.Bytes : tok.OptId == OptChars ? CutMode.Chars : CutMode.Fields;
                    list = tok.Value!;
                    break;
                case OptDelim:
                    {
                        string v = tok.Value!;
                        bool single = v.Length == 0 || v.Length == 1
                            || (v.Length == 2 && char.IsHighSurrogate(v[0]) && char.IsLowSurrogate(v[1]));
                        if (!single) { c.Error = "cut: the delimiter must be a single character"; return c; }
                        delimiter = v.Length == 0 ? "\0" : v; // GNU: an empty DELIM is NUL
                        delimSpecified = v.Length != 0;
                        break;
                    }
                case OptOnly: only = true; break;
                case OptOutDelim: outDelim = tok.Value!; break;
                case OptComplement: complement = true; break;
            }
        }

        if (mode is null) { c.Error = "cut: you must specify a list of bytes, characters, or fields"; return c; }
        if (delimSpecified && mode != CutMode.Fields)
        { c.Error = "cut: an input delimiter may be specified only when operating on fields"; return c; }
        if (only && mode != CutMode.Fields)
        { c.Error = "cut: suppressing non-delimited lines makes sense\n\tonly when operating on fields"; return c; }

        var ranges = ParseList(list!, mode == CutMode.Fields, out string? listError);
        if (listError is not null) { c.Error = $"cut: {listError}"; return c; }

        c.Selection = new CutPlan(mode.Value, ranges!, delimiter, complement, only, outDelim);
        return c;
    }

    /// <summary>
    /// GNU list syntax: elements separated by commas or blanks; <c>N</c>, <c>N-M</c>, <c>N-</c>, <c>-M</c>.
    /// Returns null with <paramref name="error"/> (GNU wording, ASCII quotes) on: a zero or empty
    /// position, a decreasing range, a bare <c>-</c>, a non-digit, a number beyond 64 bits.
    /// </summary>
    internal static List<(int Lo, int Hi)>? ParseList(string spec, bool isField, out string? error)
    {
        error = null;
        string numbered = isField ? "fields are numbered from 1" : "byte/character positions are numbered from 1";
        string invalid = isField ? "invalid field value" : "invalid byte/character position";
        string tooLarge = isField ? "field number" : "byte/character offset";
        var result = new List<(int, int)>();

        foreach (var part in spec.Split(',', ' ', '\t'))
        {
            int dash = part.IndexOf('-');
            if (part == "-") { error = "invalid range with no endpoint: -"; return null; }
            if (dash >= 0 && part.IndexOf('-', dash + 1) >= 0)
            { error = "invalid byte or character range"; return null; }

            string loS = dash < 0 ? part : part.Substring(0, dash);
            string hiS = dash < 0 ? part : part.Substring(dash + 1);
            int lo = 1, hi = int.MaxValue;
            if (dash < 0 || loS.Length > 0)
            {
                if (!TryNumber(loS, part, invalid, tooLarge, out lo, out error)) return null;
                if (lo == 0) { error = numbered; return null; }
            }
            if (dash < 0) hi = lo;
            else if (hiS.Length > 0)
            {
                if (!TryNumber(hiS, hiS, invalid, tooLarge, out hi, out error)) return null;
                if (hi == 0) { error = numbered; return null; }
                if (lo > hi) { error = "invalid decreasing range"; return null; }
            }
            result.Add((lo, hi));
        }
        return result;
    }

    // Digits only; a trailing junk char is the reported token; > ulong overflow is "too large";
    // anything above int.MaxValue clamps (positions past the end of a line select nothing).
    private static bool TryNumber(string s, string whole, string invalid, string tooLarge,
                                  out int value, out string? error)
    {
        value = 0;
        error = null;
        if (s.Length == 0) { return true; } // empty element: position 0 -> "numbered from 1"
        int bad = -1;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] < '0' || s[i] > '9') { bad = i; break; }
        }
        if (bad >= 0)
        {
            // `x-2` reports the whole token, `1x` only the offending tail (GNU).
            string tok = whole.Length > s.Length && bad == 0 ? whole : s.Substring(bad);
            error = $"{invalid} '{tok}'";
            return false;
        }
        if (!ulong.TryParse(s, out ulong big)) { error = $"{tooLarge} '{s}' is too large"; return false; }
        value = big > int.MaxValue ? int.MaxValue : (int)big;
        return true;
    }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>
    /// The bash <c>-d DELIM</c> value flag — declared because the bare token <c>-d</c> prefix-collides
    /// with <c>-Debug</c>. Exact parameter-name match beats a common-parameter prefix match.
    /// </summary>
    [Parameter]
    public string? D { get; set; }

    /// <summary>The bash <c>-c LIST</c> value flag — declared because bare <c>-c</c> collides with <c>-Confirm</c>.</summary>
    [Parameter]
    public string? C { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    // Parsed-once state.
    private bool _parsed;
    private CutArgs? _plan;
    private string[] _args = Array.Empty<string>();
    // True when stdin must NOT be streamed: file operands present, a scan/plan error, or a
    // --help / --version request (file mode ignores stdin, like the buffered oracle).
    private bool _suppressStdin;

    /// <summary>
    /// Arguments with the decoy-bound flags re-injected (<c>-c LIST</c>, <c>-d DELIM</c>). The colon
    /// spelling <c>-d:</c> (PowerShell binds D to the NEXT token) is recovered from this command's
    /// own pipeline segment; that swallowed token is re-injected when it is another option. Only
    /// consulted when D was actually bound — a transpiled call never binds D.
    /// </summary>
    private string[] ArgsWithDecoys()
    {
        var args = Arguments ?? Array.Empty<string>();
        var pre = new List<string>();
        if (C is not null) { pre.Add("-c"); pre.Add(C); }
        if (D is not null)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                BashRuntime.CurrentPipelineSegment(MyInvocation) ?? string.Empty,
                @"(?<![A-Za-z0-9])-d(\S)(?!\w)");
            if (m.Success)
            {
                pre.Add("-d" + m.Groups[1].Value);
                if (D.StartsWith('-')) pre.Add(D);
            }
            else
            {
                pre.Add("-d");
                pre.Add(D);
            }
        }
        return pre.Count == 0 ? args : pre.Concat(args).ToArray();
    }

    private void ParseOnce()
    {
        if (_parsed) return;
        _parsed = true;

        _args = ArgsWithDecoys();
        if (Array.IndexOf(_args, "--version") >= 0 || Array.IndexOf(_args, "--help") >= 0)
        {
            _suppressStdin = true;
            return;
        }

        _plan = Plan(_args);
        _suppressStdin = _plan.Declined || _plan.Operands.Count > 0;
    }

    private void EmitCutLine(string line)
    {
        string? result = _plan!.Selection!.Apply(line);
        if (result is not null) WriteObject(BashRuntime.NewBashObject(result));
    }

    protected override void ProcessRecord()
    {
        if (InputObject == null) return;

        ParseOnce();
        if (_suppressStdin) return;

        // Pipeline mode: cut each stdin sub-line as it arrives instead of buffering the whole pipe.
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

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "cut", _args)) return;
        if (Array.IndexOf(_args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "cut"))
            {
                WriteObject(line);
            }
            return;
        }

        var plan = _plan!;
        if (FileSystemHelpers.TryWriteParseError(this, "cut", plan.Parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "cut", plan.Parsed)) return;
        if (plan.Error is { } planError)
        {
            FileSystemHelpers.WriteBashError(this, planError);
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        // Pipeline mode (no operands) was already streamed in ProcessRecord.
        if (plan.Operands.Count == 0) return;

        bool hadError = false;
        foreach (var raw in plan.Operands)
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
                    hadError = true;
                }
            }
        }
        if (hadError) FileSystemHelpers.SetLastExitCode(this, 1);
    }

    private void WriteReadError(string path, Exception ex)
    {
        string msg = FileSystemHelpers.ReadErrorMessage(ex);
        string normalized = path.Replace('\\', '/');
        FileSystemHelpers.WriteBashError(this, $"cut: {normalized}: {msg}");
    }
}
