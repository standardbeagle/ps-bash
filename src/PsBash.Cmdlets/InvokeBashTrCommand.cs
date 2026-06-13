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
/// <item>C-style escape sequences (<c>\n</c>, <c>\t</c>, <c>\r</c>, etc.) in
/// both SETs via <see cref="BashRuntime.ExpandEscapeSequences"/>.</item>
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
public sealed class InvokeBashTrCommand : PSCmdlet
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

    // Translation tables built ONCE in ParseOnce (the SET expansion, complement
    // construction, and per-char membership/mapping are constant across every
    // input line — building them per line was O(lines × set) churn). After
    // BuildTables runs, each line is transformed with O(1) lookups.
    private HashSet<char>? _membershipSet;   // delete / squeeze-only: chars in SET1
    private Dictionary<char, char>? _translateMap; // translate: SET1 char -> SET2 char
    private HashSet<char>? _translateDrop;   // translate: SET1 chars dropped (SET2 empty)
    private HashSet<char>? _squeezeSet2;     // squeeze-after-translate: chars in SET2

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

            if (arg == "-d") { _deleteMode = true; continue; }
            if (arg == "-s") { _squeezeMode = true; continue; }

            if (arg.Length > 1 && arg[0] == '-')
            {
                // Bundled / single short flags. Matches the psm1 oracle's
                // per-char scan over arg.Substring(1).
                foreach (char ch in arg.AsSpan(1))
                {
                    switch (ch)
                    {
                        case 'd': _deleteMode = true; break;
                        case 's': _squeezeMode = true; break;
                        case 'c': _complementMode = true; break;
                        case 'C': _complementMode = true; break;
                        case 't': _truncateMode = true; break;
                    }
                }
                continue;
            }

            _operands.Add(arg);
        }

        // Expand C-style escape sequences in operands before class expansion.
        for (int oi = 0; oi < _operands.Count; oi++)
        {
            _operands[oi] = BashRuntime.ExpandEscapeSequences(_operands[oi]);
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
        // Delete mode and squeeze-only mode test membership against SET1 (the
        // complement flag flips the test at use-site, not the set contents).
        if (_deleteMode)
        {
            if (_operands.Count == 0) return;
            _membershipSet = new HashSet<char>(ExpandClass(_operands[0]));
            return;
        }
        if (_squeezeMode && _operands.Count == 1)
        {
            _membershipSet = new HashSet<char>(ExpandClass(_operands[0]));
            return;
        }

        if (_operands.Count >= 2)
        {
            string set1 = ExpandClass(_operands[0]);
            string set2 = ExpandClass(_operands[1]);

            if (_truncateMode && set2.Length > set1.Length)
            {
                set2 = set2.Substring(0, set1.Length);
            }

            if (_complementMode)
            {
                // SET1 becomes all 256 chars MINUS the original SET1.
                var compSb = new StringBuilder();
                var set1Hash = new HashSet<char>(set1);
                for (int c = 0; c <= 255; c++)
                {
                    char ch = (char)c;
                    if (!set1Hash.Contains(ch)) compSb.Append(ch);
                }
                set1 = compSb.ToString();
                // Extend SET2 by repeating last char to match new SET1 length.
                if (set2.Length > 0)
                {
                    var ext = new StringBuilder(set2);
                    char last = set2[set2.Length - 1];
                    while (ext.Length < set1.Length) ext.Append(last);
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

            if (_squeezeMode) _squeezeSet2 = new HashSet<char>(set2);
        }
    }

    protected override void ProcessRecord()
    {
        if (InputObject == null) return;

        ParseOnce();
        if (_suppress) return;

        // Stream the per-line transform instead of buffering the whole pipe.
        // The buffered oracle joined items with '\n' and stripped one trailing
        // '\n' before splitting on '\n'; that is exactly equivalent to
        // splitting each record on '\n' (NO trailing trim) and transforming
        // each sub-line — the inter-item join '\n' is the same separator the
        // split would produce, so item boundaries are line boundaries. Squeeze
        // / translate are already per-line (TransformLine takes one line), so
        // no cross-record state is needed.
        string text = BashRuntime.GetBashText(InputObject);
        foreach (var line in text.Split('\n'))
        {
            WriteObject(BashRuntime.NewBashObject(TransformLine(line)));
        }
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

        // Pipeline records (if any) were streamed in ProcessRecord; empty
        // input produces no output, matching the oracle's count==0 guard.
    }

    /// <summary>
    /// Transforms one line using the tables built by <see cref="BuildTables"/>.
    /// All per-char tests are O(1) hash lookups; the SETs were expanded once.
    /// </summary>
    private string TransformLine(string text)
    {
        // Delete mode — uses SET1 only.
        if (_deleteMode)
        {
            if (_membershipSet == null) return text;
            var sb = new StringBuilder(text.Length);
            foreach (char ch in text)
            {
                bool inSet = _membershipSet.Contains(ch);
                // Complement + delete keeps chars that ARE in set (oracle
                // behavior — preserved); plain delete drops them.
                if (_complementMode ? inSet : !inSet) sb.Append(ch);
            }
            return sb.ToString();
        }

        // Squeeze-only mode — single SET, no translation.
        if (_squeezeMode && _operands.Count == 1)
        {
            if (_membershipSet == null) return text;
            var sb = new StringBuilder(text.Length);
            char prevChar = '\0';
            bool prevInSet = false;
            foreach (char ch in text)
            {
                bool inSet = _membershipSet.Contains(ch);
                if (_complementMode) inSet = !inSet;
                if (inSet && prevInSet && ch == prevChar) continue;
                sb.Append(ch);
                prevChar = ch;
                prevInSet = inSet;
            }
            return sb.ToString();
        }

        // Translation mode — SET1 -> SET2.
        if (_translateMap != null)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char ch in text)
            {
                if (_translateMap.TryGetValue(ch, out char mapped)) sb.Append(mapped);
                else if (_translateDrop != null && _translateDrop.Contains(ch)) { /* drop */ }
                else sb.Append(ch);
            }
            string result = sb.ToString();

            if (_squeezeMode && _squeezeSet2 != null)
            {
                var sb2 = new StringBuilder(result.Length);
                char prevCh = '\0';
                bool prevInSet2 = false;
                foreach (char ch in result)
                {
                    bool inSet2 = _squeezeSet2.Contains(ch);
                    if (inSet2 && prevInSet2 && ch == prevCh) continue;
                    sb2.Append(ch);
                    prevCh = ch;
                    prevInSet2 = inSet2;
                }
                return sb2.ToString();
            }

            return result;
        }

        return text;
    }

    /// <summary>
    /// Reproduces the psm1 oracle's class expander: POSIX class names get
    /// substituted first, then ranges (<c>a-z</c>) expand into the full
    /// inclusive character sequence.
    /// </summary>
    private static string ExpandClass(string spec)
    {
        spec = ExpandPosixClasses(spec);
        var sb = new StringBuilder();
        int i = 0;
        while (i < spec.Length)
        {
            if (i + 2 < spec.Length && spec[i + 1] == '-')
            {
                int start = spec[i];
                int end = spec[i + 2];
                for (int c = start; c <= end; c++)
                {
                    sb.Append((char)c);
                }
                i += 3;
            }
            else
            {
                sb.Append(spec[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    private static string ExpandPosixClasses(string spec)
    {
        // Char sets reproduce the oracle's hashtable byte-for-byte.
        return spec
            .Replace("[:alnum:]", "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")
            .Replace("[:alpha:]", "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ")
            .Replace("[:digit:]", "0123456789")
            .Replace("[:upper:]", "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
            .Replace("[:lower:]", "abcdefghijklmnopqrstuvwxyz")
            .Replace("[:space:]", " \t\n\r\f\v")
            .Replace("[:punct:]", "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~");
    }
}
