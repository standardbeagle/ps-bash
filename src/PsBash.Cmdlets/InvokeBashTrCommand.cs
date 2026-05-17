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

    private readonly List<PSObject> _pipeline = new();

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

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "tr"))
            {
                WriteObject(line);
            }
            return;
        }

        bool deleteMode = D.IsPresent;
        bool complementMode = C.IsPresent;
        bool squeezeMode = false;
        bool truncateMode = false;
        var operands = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (arg == "--complement") { complementMode = true; continue; }
            if (arg == "--truncate-set1") { truncateMode = true; continue; }
            if (arg == "--delete") { deleteMode = true; continue; }
            if (arg == "--squeeze-repeats") { squeezeMode = true; continue; }

            if (arg == "-d") { deleteMode = true; continue; }
            if (arg == "-s") { squeezeMode = true; continue; }

            if (arg.Length > 1 && arg[0] == '-')
            {
                // Bundled / single short flags. Matches the psm1 oracle's
                // per-char scan over arg.Substring(1).
                foreach (char ch in arg.AsSpan(1))
                {
                    switch (ch)
                    {
                        case 'd': deleteMode = true; break;
                        case 's': squeezeMode = true; break;
                        case 'c': complementMode = true; break;
                        case 'C': complementMode = true; break;
                        case 't': truncateMode = true; break;
                    }
                }
                continue;
            }

            operands.Add(arg);
        }

        // Expand C-style escape sequences in operands before class expansion.
        for (int oi = 0; oi < operands.Count; oi++)
        {
            operands[oi] = BashRuntime.ExpandEscapeSequences(operands[oi]);
        }

        // Collect all pipeline text. The oracle joins items with '\n' and
        // strips a single trailing '\n' before splitting on '\n' to drive the
        // per-line transform loop.
        var allText = new StringBuilder();
        foreach (var item in _pipeline)
        {
            allText.Append(BashRuntime.GetBashText(item));
            allText.Append('\n');
        }
        string inputText = allText.ToString();
        if (inputText.EndsWith('\n'))
        {
            inputText = inputText.Substring(0, inputText.Length - 1);
        }

        // Empty input -> no output. (The oracle would split "" into a single
        // empty line and emit one empty object; matching that for parity.)
        if (_pipeline.Count == 0)
        {
            return;
        }

        foreach (var line in inputText.Split('\n'))
        {
            string transformed = TransformLine(
                line, operands, deleteMode, squeezeMode,
                complementMode, truncateMode);
            WriteObject(BashRuntime.NewBashObject(transformed));
        }
    }

    private static string TransformLine(
        string text,
        List<string> operands,
        bool deleteMode,
        bool squeezeMode,
        bool complementMode,
        bool truncateMode)
    {
        // Delete mode — uses SET1 only.
        if (deleteMode)
        {
            if (operands.Count == 0) return text;
            string set = ExpandClass(operands[0]);
            var sb = new StringBuilder();
            foreach (char ch in text)
            {
                bool inSet = set.IndexOf(ch) >= 0;
                if (complementMode)
                {
                    // Complement + delete: keep chars that ARE in set
                    // (oracle behavior — preserved).
                    if (inSet) sb.Append(ch);
                }
                else
                {
                    if (!inSet) sb.Append(ch);
                }
            }
            return sb.ToString();
        }

        // Squeeze-only mode — single SET, no translation.
        if (squeezeMode && operands.Count == 1)
        {
            string set = ExpandClass(operands[0]);
            var sb = new StringBuilder();
            char prevChar = '\0';
            bool prevInSet = false;
            foreach (char ch in text)
            {
                bool inSet = set.IndexOf(ch) >= 0;
                if (complementMode) inSet = !inSet;
                if (inSet && prevInSet && ch == prevChar) continue;
                sb.Append(ch);
                prevChar = ch;
                prevInSet = inSet;
            }
            return sb.ToString();
        }

        // Translation mode — SET1 -> SET2.
        if (operands.Count >= 2)
        {
            string set1 = ExpandClass(operands[0]);
            string set2 = ExpandClass(operands[1]);

            if (truncateMode && set2.Length > set1.Length)
            {
                set2 = set2.Substring(0, set1.Length);
            }

            if (complementMode)
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

            var sb = new StringBuilder();
            foreach (char ch in text)
            {
                int idx = set1.IndexOf(ch);
                if (idx >= 0 && idx < set2.Length) sb.Append(set2[idx]);
                else if (idx >= 0 && set2.Length > 0) sb.Append(set2[set2.Length - 1]);
                else if (idx >= 0) { /* set2 empty: drop (oracle parity) */ }
                else sb.Append(ch);
            }
            string result = sb.ToString();

            if (squeezeMode)
            {
                var sb2 = new StringBuilder();
                char prevCh = '\0';
                bool prevInSet2 = false;
                foreach (char ch in result)
                {
                    bool inSet2 = set2.IndexOf(ch) >= 0;
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
