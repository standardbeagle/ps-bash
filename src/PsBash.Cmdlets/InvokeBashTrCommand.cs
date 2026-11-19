using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashTr</c> function
/// (REFACTOR-2 follow-on). Translates / deletes / squeezes characters from
/// pipeline input, matching GNU coreutils <c>tr</c>.
///
/// Behavioral parity oracle: the original psm1 function. Pipeline-only — the
/// psm1 oracle never accepted file operands; non-flag positional operands are
/// always interpreted as SET1 / SET2.
///
/// Supported flags (preserving the oracle's surface byte-for-byte):
/// <list type="bullet">
/// <item><c>-d</c> / <c>--delete</c> — delete chars in SET1.</item>
/// <item><c>-s</c> / <c>--squeeze-repeats</c> — squeeze runs of chars from the
/// last SET.</item>
/// <item><c>-c</c> / <c>-C</c> / <c>--complement</c> — complement SET1.</item>
/// <item><c>-t</c> / <c>--truncate-set1</c> — truncate SET1 to the length of
/// SET2.</item>
/// <item>POSIX character classes <c>[:alpha:] [:digit:] [:alnum:] [:upper:]
/// [:lower:] [:space:] [:punct:]</c> in both SETs.</item>
/// <item>Ranges <c>a-z</c> in both SETs.</item>
/// <item>C-style escape sequences (<c>\n</c>, <c>\NNN</c>; an unknown escape drops its
/// backslash) in both SETs via <see cref="BashEscapes.ExpandTrSet"/>.</item>
/// </list>
///
/// Two PowerShell common-parameter prefix collisions, both resolved by
/// declaring single-letter <see cref="SwitchParameter"/>s (exact-name match
/// beats common-parameter prefix match):
/// <list type="bullet">
/// <item><c>-d</c> prefix-collides with <c>-Debug</c> → declared as
/// <see cref="D"/>.</item>
/// <item><c>-c</c> prefix-collides with <c>-Confirm</c> → declared as
/// <see cref="C"/>.</item>
/// </list>
/// <c>-s</c> and <c>-t</c> have no common-parameter prefix collision and stay
/// in <see cref="Arguments"/>; bundled forms (<c>-ds</c>, <c>-cs</c>, …) are
/// recovered by the manual post-parse scan, matching the oracle.
///
/// Output: each transformed line is emitted via
/// <see cref="BashRuntime.NewBashObject(string)"/> as a bare
/// <c>PsBash.TextOutput</c> string — the same shape the oracle produced via
/// <c>New-BashObject -BashText</c>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashTr")]
[OutputType(typeof(string))]
public sealed class InvokeBashTrCommand : BashCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>
    /// <c>-d</c> (delete) — declared explicitly because <c>-d</c>
    /// prefix-collides with <c>-Debug</c>.
    /// </summary>
    [Parameter]
    public SwitchParameter D { get; set; }

    /// <summary>
    /// <c>-c</c> (complement) — declared explicitly because <c>-c</c>
    /// prefix-collides with <c>-Confirm</c>.
    /// </summary>
    [Parameter]
    public SwitchParameter C { get; set; }

    /// <summary>
    /// Valid GNU <c>tr</c> options ps-bash does not implement. GNU tr's
    /// flag surface is fully covered by this cmdlet (complement/delete/
    /// squeeze/truncate), so this set is empty — any unknown option-looking
    /// token is classified as "unrecognized/invalid" (bash parity).
    /// </summary>
    private static readonly HashSet<string> TrValidButUnsupported =
        new(StringComparer.Ordinal);

    // Parsed-once state. tr has no file mode — operands are the SET1/SET2
    // translate sets, and input always comes from the pipeline — so the only
    // reason to suppress streaming is a --help / --version request.
    private bool _parsed;
    private bool _deleteMode;
    private bool _complementMode;
    private bool _squeezeMode;
    private bool _truncateMode;
    private List<string> _operands = new();
    private bool _suppress;
    private string? _rangeErrorMsg;
    private Action<PSCmdlet>? _operandError; // a SET count GNU rejects (missing / extra operand), reported at End
    // Deferred parse failure: an unrecognized flag found in ParseOnce.
    // Emitted in EndProcessing so ProcessRecord's early-return path stays clean.
    private string? _optionErrorToken;

    // Translation tables built ONCE in ParseOnce (the SET expansion, complement
    // construction, and per-char membership/mapping are constant across every
    // input line — building them per line was O(lines × set) churn). After
    // BuildTables runs, each line is transformed with O(1) lookups.
    private HashSet<char>? _membershipSet;   // delete / squeeze-only: chars in SET1
    private Dictionary<char, char>? _translateMap; // translate: SET1 char -> SET2 char
    private HashSet<char>? _translateDrop;   // translate: SET1 chars dropped (SET2 empty)
    private HashSet<char>? _squeezeSet;      // stage 2: squeeze runs of these (SET1 alone, else SET2)
    private bool _squeezeComplement;         // -cs with one SET: squeeze what is NOT in SET1
    private HashSet<char>? _complementOriginal; // -c translate: the SET1 a wide character is tested against
    private char _complementLast;            // -c translate: SET2's last character

    /// <summary>
    /// How many SETs GNU tr takes (validate()): translating two, <c>-d</c> or <c>-s</c> alone one, <c>-ds</c> two,
    /// <c>-s</c> one or two. The fused lane (TrStage) declines outside these so the cmdlet reports the usage error.
    /// </summary>
    internal static (int Min, int Max) OperandBounds(bool delete, bool squeeze)
        => (delete == squeeze ? 2 : 1, delete && !squeeze ? 1 : 2);

    private void ParseOnce()
    {
        if (_parsed) return;
        _parsed = true;

        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--version") >= 0 || Array.IndexOf(args, "--help") >= 0)
        {
            _suppress = true;
            return;
        }

        _deleteMode = D.IsPresent;
        _complementMode = C.IsPresent;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (arg == "--complement") { _complementMode = true; continue; }
            if (arg == "--truncate-set1") { _truncateMode = true; continue; }
            if (arg == "--delete") { _deleteMode = true; continue; }
            if (arg == "--squeeze-repeats") { _squeezeMode = true; continue; }

            // Unknown long flag (all known --xxx forms were matched above).
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                _optionErrorToken = arg;
                _suppress = true;
                return;
            }

            if (arg == "-d") { _deleteMode = true; continue; }
            if (arg == "-s") { _squeezeMode = true; continue; }

            if (arg.Length > 1 && arg[0] == '-')
            {
                // Bundled / single short flags. Matches the psm1 oracle's
                // per-char scan over arg.Substring(1).
                bool unknownFlag = false;
                foreach (char ch in arg.AsSpan(1))
                {
                    switch (ch)
                    {
                        case 'd': _deleteMode = true; break;
                        case 's': _squeezeMode = true; break;
                        case 'c': _complementMode = true; break;
                        case 'C': _complementMode = true; break;
                        case 't': _truncateMode = true; break;
                        default:
                            _optionErrorToken = $"-{ch}";
                            unknownFlag = true;
                            break;
                    }
                    if (unknownFlag) break;
                }
                if (unknownFlag) { _suppress = true; return; }
                continue;
            }

            _operands.Add(arg);
        }

        // GNU tr's operand count (validate()): translating needs two SETs, -d or -s alone one, -ds two; anything else
        // is a usage error before any input is read. REGRESSION: `tr`, `tr a` and `tr -d a b` copied the input, rc 0.
        int n = _operands.Count;
        var (min, max) = OperandBounds(_deleteMode, _squeezeMode);
        if (n < min)
        {
            string? after = n == 0 ? null : _operands[n - 1];
            string? why = n == 0 ? null : _squeezeMode
                ? "Two strings must be given when both deleting and squeezing repeats."
                : "Two strings must be given when translating.";
            _operandError = c => FileSystemHelpers.WriteMissingOperand(c, "tr", after, why: why);
            _suppress = true;
            return;
        }
        if (n > max)
        {
            string extra = _operands[max];
            string? why = n == 2 ? "Only one string may be given when deleting without squeezing repeats." : null;
            _operandError = c => FileSystemHelpers.WriteExtraOperand(c, "tr", extra, why);
            _suppress = true;
            return;
        }

        // GNU warns (stderr, exit unchanged) once per SET ending in a lone backslash. Expanding each
        // operand here also surfaces the warning before any output, as GNU does.
        for (int oi = 0; oi < _operands.Count && oi < 2; oi++)
        {
            bool trailing;
            try { BashEscapes.ExpandTrSet(_operands[oi], out trailing); }
            catch (TrSetException) { continue; } // BuildTables reports a reverse range
            if (trailing)
            {
                FileSystemHelpers.WriteBashError(this, "tr: warning: an unescaped backslash at end of string is not portable");
                FileSystemHelpers.SetLastExitCode(this, 0);
            }
        }

        BuildTables();
    }

    /// <summary>
    /// Precomputes the membership / translation tables once. The expanded SETs,
    /// the complement construction, and the SET1→SET2 mapping are all invariant
    /// across input lines, so doing this once turns each line into O(1)-per-char
    /// lookups instead of re-expanding the SETs and scanning them with IndexOf.
    /// Mirrors the per-line logic in <see cref="TransformLine"/> exactly.
    /// </summary>
    private void BuildTables()
    {
        // A reversed range (`a-A`) makes ExpandClass throw; GNU tr rejects it with
        // exit 1 instead of silently expanding to nothing. Defer the message to the
        // EndProcessing emit and suppress output.
        try
        {
            BuildTablesCore();
        }
        catch (TrSetException ex) { _rangeErrorMsg = ex.Message; _suppress = true; }
    }

    // Squeeze state carried ACROSS records: a run of the same squeezed character continues over a
    // record boundary when the terminator is itself squeezed (`tr -s '\n'`), and that is the only
    // cross-record state tr has — delete and translate are per-character.
    private char _sqPrevChar = '\0';
    private bool _sqPrevInSet;

    /// <summary>One expanded SET operand (escapes, classes, ranges); see <see cref="BashEscapes.ExpandTrSet"/>.</summary>
    private static string ExpandClass(string spec) => BashEscapes.ExpandTrSet(spec, out _);

    private void BuildTablesCore()
    {
        // GNU tr is two stages over one byte stream: DELETE-or-TRANSLATE, then SQUEEZE. Each stage's set is
        // fixed here. Squeeze set: SET1 (complemented by -c) when -s is the only action, else SET2 — the
        // set the characters are in AFTER the first stage (`tr -ds a b`, `tr -s ab xy`).
        if (_deleteMode)
        {
            if (_operands.Count == 0) return;
            _membershipSet = new HashSet<char>(ExpandClass(_operands[0]));
            if (_squeezeMode && _operands.Count >= 2) _squeezeSet = new HashSet<char>(ExpandClass(_operands[1]));
            return;
        }
        if (_squeezeMode && _operands.Count == 1)
        {
            _squeezeSet = new HashSet<char>(ExpandClass(_operands[0]));
            _squeezeComplement = _complementMode;
            return;
        }

        if (_operands.Count >= 2)
        {
            string set1 = ExpandClass(_operands[0]);
            string set2 = ExpandClass(_operands[1]);

            // -t: SET1 is cut to SET2's length (GNU `tr -t abc xy` maps only a->x, b->y).
            if (_truncateMode && !_complementMode && set1.Length > set2.Length)
                set1 = set1.Substring(0, set2.Length);

            if (_complementMode)
            {
                // SET1 becomes every byte MINUS the original SET1. A character past one byte (a UTF-8
                // sequence) is in the complement too; TransformLine maps each of its bytes to SET2's last.
                var compSb = new StringBuilder();
                var set1Hash = new HashSet<char>(set1);
                for (int c = 0; c <= 255; c++)
                {
                    char ch = (char)c;
                    if (!set1Hash.Contains(ch)) compSb.Append(ch);
                }
                set1 = compSb.ToString();
                if (set2.Length > 0)
                {
                    _complementOriginal = set1Hash;
                    _complementLast = set2[set2.Length - 1];
                    var ext = new StringBuilder(set2);
                    while (ext.Length < set1.Length) ext.Append(_complementLast);
                    set2 = ext.ToString();
                }
            }

            // Build the char->char map honoring first-occurrence (IndexOf
            // returns the first index, so the first SET1 occurrence wins).
            _translateMap = new Dictionary<char, char>();
            _translateDrop = new HashSet<char>();
            for (int idx = 0; idx < set1.Length; idx++)
            {
                char from = set1[idx];
                if (_translateMap.ContainsKey(from) || _translateDrop.Contains(from))
                    continue; // first occurrence already recorded
                if (idx < set2.Length) _translateMap[from] = set2[idx];
                else if (set2.Length > 0) _translateMap[from] = set2[set2.Length - 1];
                else _translateDrop.Add(from); // set2 empty: drop (oracle parity)
            }

            if (_squeezeMode) _squeezeSet = new HashSet<char>(set2);
        }
    }
    protected override void ProcessRecord()
    {
        if (InputObject == null) return;

        ParseOnce();
        if (_suppress) return;

        // `tr` is a BYTE-STREAM filter: the newline is an ordinary character it can translate or
        // delete, not a record boundary to preserve. The record contract (BashRuntime.NewBashObject /
        // SdkWorker.GetOutputText): a normal record is a line WITHOUT its terminator, which the
        // serializer appends; a record marked NoTrailingNewline (printf, echo -n) carries its exact
        // bytes, embedded newlines included. So this record's byte-stream contribution is
        // BashText + "\n" for a normal record and BashText verbatim for a marked one — which is what
        // makes `seq 1 3 | tr -d '\n'` (seq emits bare lines) "123" exactly like `printf '1\n2\n3\n'`.
        // A plain record whose text already ends in a "\n" (a bare "a\n" string) still gets the boundary one:
        // the one record → bytes rule, BashRuntime.RecordBytes.
        string chunk = BashRuntime.RecordBytes(InputObject);

        // STREAMING, whatever the table: each record is transformed and written as it arrives
        // (`tail -f log | tr '\n' ' '` emits per line; `yes | tr '\n' x | head -c 5` terminates).
        // A table that eats the terminator yields exact-bytes records, whose concatenation is the
        // exact output; `$(...)` capture glues them (ConvertTo-BashCapture).
        EmitStream(TransformLine(chunk));
    }

    /// <summary>
    /// Writes transformed stream bytes via <see cref="BashRuntime.BytesRecord"/>: bytes ending in one
    /// terminator go out as an ordinary record (the serializer re-adds that <c>\n</c>; embedded newlines
    /// stay inside the record); bytes without one (terminator deleted or translated, or input that never
    /// had one) or ending in an empty line (<c>printf 'a\n\n' | tr a b</c>) go out as exact bytes.
    /// </summary>
    private void EmitStream(string bytes)
    {
        if (bytes.Length == 0) return;
        WriteObject(BashRuntime.BytesRecord(bytes));
    }

    protected override void EndProcessing()
    {
        ParseOnce();

        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "tr", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "tr"))
            {
                WriteObject(line);
            }
            return;
        }

        if (_optionErrorToken != null)
        {
            FileSystemHelpers.WriteOptionError(this, "tr", _optionErrorToken, TrValidButUnsupported, usageExitCode: 1); // GNU tr: usage errors are 1
            return;
        }

        if (_operandError != null)
        {
            _operandError(this);
            return;
        }

        if (_rangeErrorMsg != null)
        {
            FileSystemHelpers.WriteBashError(this, $"tr: {_rangeErrorMsg}");
            return;
        }

        // Every record was streamed in ProcessRecord (empty input produces no output, matching the
        // oracle's count==0 guard); nothing is held back.
    }

    /// <summary>
    /// Transforms one chunk of the byte stream with the tables built by <see cref="BuildTables"/>: stage 1
    /// deletes or translates, stage 2 squeezes runs of a <see cref="_squeezeSet"/> character. All per-char tests
    /// are O(1) hash lookups; the SETs were expanded once.
    /// </summary>
    private string TransformLine(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (_deleteMode)
            {
                // Complement + delete keeps the SET1 characters; plain delete drops them.
                if (_membershipSet != null && (_membershipSet.Contains(ch) != _complementMode)) continue;
                Squeeze(sb, ch);
            }
            else if (_translateMap != null)
            {
                if (_translateMap.TryGetValue(ch, out char mapped)) Squeeze(sb, mapped);
                else if (_translateDrop != null && _translateDrop.Contains(ch)) { /* drop */ }
                else if (ch > 0xFF && _complementOriginal != null && !_complementOriginal.Contains(ch))
                {
                    // In the complement: each UTF-8 byte of it becomes SET2's last character.
                    int bytes = char.IsSurrogate(ch) ? 2 : ch < 0x800 ? 2 : 3;
                    for (int k = 0; k < bytes; k++) Squeeze(sb, _complementLast);
                }
                else Squeeze(sb, ch);
            }
            else Squeeze(sb, ch);
        }
        return sb.ToString();
    }

    /// <summary>Stage 2: append <paramref name="ch"/> unless it repeats the previous squeezed character.</summary>
    private void Squeeze(StringBuilder sb, char ch)
    {
        if (_squeezeSet == null) { sb.Append(ch); return; }
        bool inSet = _squeezeSet.Contains(ch) != _squeezeComplement;
        if (inSet && _sqPrevInSet && ch == _sqPrevChar) return;
        sb.Append(ch);
        _sqPrevChar = ch;
        _sqPrevInSet = inSet;
    }

}