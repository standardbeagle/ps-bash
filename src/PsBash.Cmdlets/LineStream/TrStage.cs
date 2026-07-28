using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// <c>tr</c> pipeline mode for the fused streaming lane (S3 of the fan-out epic).
///
/// <para><b>WHOLE-RECORD semantics — this is a real historical bug, not a style choice.</b>
/// To <c>tr</c> the newline is an ORDINARY translatable character, not a record boundary:
/// <c>tr</c> is a byte-stream filter. An earlier version of the CMDLET split each record on
/// <c>\n</c> before transforming, which was wrong twice over — it ADDED a line
/// (<c>printf "a\nb\n" | tr x y | wc -l</c> answered 3 where bash says 2, because the record
/// <c>"a\n"</c> split into <c>["a", ""]</c>), and the <c>\n</c> never reached the transform at
/// all, so <c>tr -d '\n'</c> and <c>tr '\n' ','</c> were silent no-ops. See the <c>tr</c> row
/// of <c>docs/specs/runtime-command-reference.md</c>. This stage therefore transforms each
/// record WHOLE and does NOT use <see cref="LineStreamRecord.SubLines"/> — the helper every
/// other S3 core uses. The <c>WholeRecord_*</c> parity tests feed records that carry a
/// trailing <c>\n</c>, so a splitting re-implementation fails instead of passing quietly.</para>
///
/// <para>The one place a record IS touched is the trailing-<c>\n</c> normalization applied to
/// the RESULT: the cmdlet emits through <c>BashRuntime.NewBashObject</c>, which strips a single
/// trailing <c>\n</c> from BashText (the serializer owns record boundaries). The streaming lane
/// renders each yielded line as <c>line + Environment.NewLine</c>, so skipping that strip would
/// emit one blank line per record. <see cref="BashRuntime.NormalizeBashText"/> is the SHARED
/// helper, not a re-derivation.</para>
///
/// <para><b>The known source/host gap is NOT fixed here.</b> Producers disagree about whether a
/// record's text carries its trailing <c>\n</c> (<c>printf</c> does, <c>seq</c> and <c>cat</c>
/// do not), so <c>seq 1 3 | tr -d "\n"</c> still sees no newline to delete. That belongs to the
/// source/host contract; the cmdlet has the same gap, and matching the cmdlet is the contract.</para>
///
/// <para><b>Certified argv subset:</b> <c>-d</c>/<c>--delete</c>, <c>-s</c>/<c>--squeeze-repeats</c>,
/// <c>-c</c>/<c>-C</c>/<c>--complement</c>, <c>-t</c>/<c>--truncate-set1</c>, any bundle of
/// <c>d/s/c/C/t</c>, and the SET1 / SET2 operands (POSIX classes, ranges, and C escapes all
/// expanded by the SAME <see cref="BashRuntime.ExpandEscapeSequences"/> the cmdlet uses).
/// DECLINED: <c>--help</c> / <c>--version</c> (cmdlet-owned output), any unknown short or long
/// flag (the cmdlet owns the error text and exit code), and a reverse range such as
/// <c>tr 'z-a' x</c> (exit 1 with a message the streaming lane has no stderr for).</para>
///
/// <para>Lazy: a pure per-record transform, so a downstream <c>head</c> still early-exits.
/// The translation tables are built ONCE in <see cref="TryCreate"/>, exactly as the cmdlet
/// builds them once in <c>ParseOnce</c>, so each record costs O(1) lookups per char.</para>
/// </summary>
internal sealed class TrStage : ILineStreamStage
{
    private readonly bool _deleteMode, _complementMode, _squeezeMode;
    private readonly int _operandCount;
    private readonly HashSet<char>? _membershipSet;
    private readonly Dictionary<char, char>? _translateMap;
    private readonly HashSet<char>? _translateDrop;
    private readonly HashSet<char>? _squeezeSet2;

    private TrStage(bool deleteMode, bool complementMode, bool squeezeMode, int operandCount,
                    HashSet<char>? membershipSet, Dictionary<char, char>? translateMap,
                    HashSet<char>? translateDrop, HashSet<char>? squeezeSet2)
    {
        _deleteMode = deleteMode; _complementMode = complementMode; _squeezeMode = squeezeMode;
        _operandCount = operandCount; _membershipSet = membershipSet; _translateMap = translateMap;
        _translateDrop = translateDrop; _squeezeSet2 = squeezeSet2;
    }

    /// <summary>tr's only non-zero exits are the flag-error and reverse-range paths, both
    /// declined in <see cref="TryCreate"/> before a record is touched.</summary>
    public int ExitCode => 0;

    /// <summary>Argv gate — the scan mirrors <c>InvokeBashTrCommand.ParseOnce</c> order:
    /// long forms, then bare <c>-d</c>/<c>-s</c>, then the per-char bundle loop, then
    /// operands.</summary>
    internal static ILineStreamStage? TryCreate(string[] argv)
    {
        bool deleteMode = false, complementMode = false, squeezeMode = false, truncateMode = false;
        var operands = new List<string>();

        foreach (var arg in argv)
        {
            if (arg == "--help" || arg == "--version") return null; // cmdlet-owned output

            if (arg == "--complement") { complementMode = true; continue; }
            if (arg == "--truncate-set1") { truncateMode = true; continue; }
            if (arg == "--delete") { deleteMode = true; continue; }
            if (arg == "--squeeze-repeats") { squeezeMode = true; continue; }
            if (arg.StartsWith("--", StringComparison.Ordinal)) return null; // unknown long flag

            if (arg == "-d") { deleteMode = true; continue; }
            if (arg == "-s") { squeezeMode = true; continue; }

            if (arg.Length > 1 && arg[0] == '-')
            {
                foreach (char ch in arg.AsSpan(1))
                {
                    switch (ch)
                    {
                        case 'd': deleteMode = true; break;
                        case 's': squeezeMode = true; break;
                        case 'c': case 'C': complementMode = true; break;
                        case 't': truncateMode = true; break;
                        default: return null; // unknown flag char → cmdlet emits the error
                    }
                }
                continue;
            }

            operands.Add(BashRuntime.ExpandEscapeSequences(arg));
        }

        // ---- table construction (port of BuildTablesCore) ----
        HashSet<char>? membership = null;
        Dictionary<char, char>? translateMap = null;
        HashSet<char>? translateDrop = null;
        HashSet<char>? squeezeSet2 = null;

        try
        {
            if (deleteMode)
            {
                if (operands.Count > 0) membership = new HashSet<char>(ExpandClass(operands[0]));
            }
            else if (squeezeMode && operands.Count == 1)
            {
                membership = new HashSet<char>(ExpandClass(operands[0]));
            }
            else if (operands.Count >= 2)
            {
                string set1 = ExpandClass(operands[0]);
                string set2 = ExpandClass(operands[1]);

                if (truncateMode && set2.Length > set1.Length) set2 = set2.Substring(0, set1.Length);

                if (complementMode)
                {
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
                        var ext = new StringBuilder(set2);
                        char last = set2[^1];
                        while (ext.Length < set1.Length) ext.Append(last);
                        set2 = ext.ToString();
                    }
                }

                translateMap = new Dictionary<char, char>();
                translateDrop = new HashSet<char>();
                for (int idx = 0; idx < set1.Length; idx++)
                {
                    char from = set1[idx];
                    if (translateMap.ContainsKey(from) || translateDrop.Contains(from)) continue;
                    if (idx < set2.Length) translateMap[from] = set2[idx];
                    else if (set2.Length > 0) translateMap[from] = set2[^1];
                    else translateDrop.Add(from);
                }

                if (squeezeMode) squeezeSet2 = new HashSet<char>(set2);
            }
        }
        catch (TrRangeError)
        {
            // `tr 'z-a' x` is exit 1 plus a stderr message. The streaming lane has neither,
            // so the cmdlet must own it.
            return null;
        }

        return new TrStage(deleteMode, complementMode, squeezeMode, operands.Count,
                           membership, translateMap, translateDrop, squeezeSet2);
    }

    /// <summary>Per-record transform. NO trim, NO split — see the class remarks.</summary>
    public IEnumerable<string> Run(IEnumerable<string> input)
    {
        foreach (var record in input)
        {
            yield return BashRuntime.NormalizeBashText(TransformLine(record));
        }
    }

    /// <summary>Port of <c>InvokeBashTrCommand.TransformLine</c>.</summary>
    private string TransformLine(string text)
    {
        if (_deleteMode)
        {
            if (_membershipSet == null) return text;
            var sb = new StringBuilder(text.Length);
            foreach (char ch in text)
            {
                bool inSet = _membershipSet.Contains(ch);
                // Complement + delete KEEPS chars that are in the set — the cmdlet's
                // behavior, ported as-is (matching the oracle beats matching GNU).
                if (_complementMode ? inSet : !inSet) sb.Append(ch);
            }
            return sb.ToString();
        }

        if (_squeezeMode && _operandCount == 1)
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

        if (_translateMap != null)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char ch in text)
            {
                if (_translateMap.TryGetValue(ch, out char mapped)) sb.Append(mapped);
                else if (_translateDrop != null && _translateDrop.Contains(ch)) { /* SET2 empty: drop */ }
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

    private sealed class TrRangeError : Exception
    {
        public TrRangeError(string message) : base(message) { }
    }

    /// <summary>Port of the cmdlet's class expander: POSIX class names first, then ranges.</summary>
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
                if (start > end) throw new TrRangeError("reverse range");
                for (int c = start; c <= end; c++) sb.Append((char)c);
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
        => spec
            .Replace("[:alnum:]", "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")
            .Replace("[:alpha:]", "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ")
            .Replace("[:digit:]", "0123456789")
            .Replace("[:upper:]", "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
            .Replace("[:lower:]", "abcdefghijklmnopqrstuvwxyz")
            .Replace("[:space:]", " \t\n\r\f\v")
            .Replace("[:punct:]", "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~");
}
