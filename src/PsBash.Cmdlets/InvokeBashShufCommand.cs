using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashShuf</c> function
/// (REFACTOR-2 follow-on). Random shuffle of input lines, matching GNU
/// coreutils <c>shuf</c>.
///
/// Three input modes, matching the psm1 oracle byte-for-byte:
/// <list type="bullet">
/// <item><b>Echo mode</b> (<c>-e</c>): treat the args after <c>-e</c> as
/// input items, not file paths. A subsequent <c>-</c>-prefixed token ends
/// the item run; the run resumes when another <c>-e</c> is seen.</item>
/// <item><b>Range mode</b> (<c>-i LO-HI</c>): items are the integers
/// <c>LO..HI</c> rendered as strings.</item>
/// <item><b>File / pipeline mode</b> (default): the first positional
/// operand is read as a file path; if there are no operands and the
/// pipeline has data, items come from pipeline <c>BashText</c>.</item>
/// </list>
///
/// <c>-n N</c> caps the output to the first N items after shuffle.
///
/// Output: each item is emitted via <see cref="BashRuntime.NewBashObject(string)"/>
/// as a default <c>PsBash.TextOutput</c> bare string — exact match for the
/// oracle's <c>Emit-BashLine -Text $item</c> output shape (since each item
/// is a single line, <c>Emit-BashLine</c> and <c>NewBashObject</c> produce
/// the same observable result).
///
/// Flag binding:
/// <list type="bullet">
/// <item><c>-e</c> (echo) is declared as an explicit <see cref="SwitchParameter"/>
/// — bare <c>-e</c> prefix-collides with PowerShell's
/// <c>-ErrorAction</c> / <c>-ErrorVariable</c> common parameters under
/// <see cref="PSCmdlet"/> binding. An exact param-name match beats the
/// common-parameter prefix match, so the declaration is salvageable here
/// (unlike <c>echo</c>, which has two colliding flags).</item>
/// <item><c>-i</c>, <c>-n</c>, and <c>--head-count=N</c> are value flags
/// with no PowerShell common-parameter prefix collision and stay in
/// <see cref="Arguments"/>; they are parsed by a manual scan.</item>
/// </list>
///
/// Shuffle determinism: like the psm1 oracle, the cmdlet uses
/// <see cref="Random"/> with no fixed seed, so the per-run permutation is
/// non-deterministic. Tests assert that the output is a permutation of the
/// input (multiset equality), never an exact ordering.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashShuf")]
[OutputType(typeof(string))]
public sealed class InvokeBashShufCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>
    /// Echo mode: treat args as input items, not file paths. Declared as a
    /// switch because bare <c>-e</c> prefix-collides with
    /// <c>-ErrorAction</c> / <c>-ErrorVariable</c> under
    /// <see cref="PSCmdlet"/> parameter binding.
    /// </summary>
    [Parameter]
    [Alias("e")]
    public SwitchParameter EchoMode { get; set; }

    /// <summary>
    /// Range mode: input is the integers LO..HI rendered as strings.
    /// Declared as an explicit string parameter because bare <c>-i</c>
    /// prefix-collides with PowerShell common parameters
    /// <c>-InformationAction</c> / <c>-InformationVariable</c> /
    /// <c>-InputObject</c> under <see cref="PSCmdlet"/> binding.
    /// </summary>
    [Parameter]
    [Alias("i")]
    public string? Range { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

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
        var rawArgs = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "shuf", rawArgs)) return;
        if (Array.IndexOf(rawArgs, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "shuf"))
            {
                WriteObject(line);
            }
            return;
        }

        // First pass — same shape as the psm1 oracle's switch loop. Parses
        // -n N (and --head-count=N), -i LO-HI, picks up the first non-flag
        // operand as inputFile. Also flips echoMode on if -e is seen here
        // (covers callers that pass `-e` literally instead of binding the
        // SwitchParameter).
        int? count = null;
        string? inputFile = null;
        bool echoMode = EchoMode.IsPresent;
        int? rangeStart = null;
        int? rangeEnd = null;

        // If the binder consumed `-i LO-HI` into `Range`, parse it now.
        if (!string.IsNullOrEmpty(Range))
        {
            int dash = Range.IndexOf('-');
            if (dash > 0
                && int.TryParse(Range.Substring(0, dash), out var lo0)
                && int.TryParse(Range.Substring(dash + 1), out var hi0))
            {
                rangeStart = lo0;
                rangeEnd = hi0;
            }
        }

        int i = 0;
        while (i < rawArgs.Length)
        {
            var a = rawArgs[i];
            if (a == "-n")
            {
                i++;
                if (i < rawArgs.Length && int.TryParse(rawArgs[i], out var n))
                {
                    count = n;
                }
            }
            else if (a == "-e")
            {
                echoMode = true;
            }
            else if (a == "-i")
            {
                i++;
                if (i < rawArgs.Length)
                {
                    var rangePart = rawArgs[i];
                    int dashIdx = rangePart.IndexOf('-');
                    if (dashIdx > 0
                        && int.TryParse(rangePart.Substring(0, dashIdx), out var lo)
                        && int.TryParse(rangePart.Substring(dashIdx + 1), out var hi))
                    {
                        rangeStart = lo;
                        rangeEnd = hi;
                    }
                }
            }
            else if (a.StartsWith("--head-count="))
            {
                if (int.TryParse(a.Substring("--head-count=".Length), out var n))
                {
                    count = n;
                }
            }
            else
            {
                if (a.StartsWith("-") && a != "-")
                {
                    // Unknown flag — oracle silently ignores via switch
                    // default fall-through (it does fail the StartsWith('-')
                    // path but then writes an error & returns). To match the
                    // oracle byte-for-byte we emit a bash-style error and
                    // exit early.
                    FileSystemHelpers.WriteBashError(this, $"shuf: invalid option '{a}'");
                    FileSystemHelpers.SetLastExitCode(this, 1);
                    return;
                }
                if (inputFile == null && a != "-")
                {
                    inputFile = a;
                }
            }
            i++;
        }

        var items = new List<string>();

        if (echoMode)
        {
            // Echo-mode item collection.
            //
            // Two binding cases to cover:
            //   (a) PowerShell parameter binding consumed `-e` into the
            //       `EchoMode` switch — `rawArgs` contains only the item
            //       tokens (plus any `-n N` / `-i ...` / `--head-count=` that
            //       was already handled in the first pass).
            //   (b) The caller passed `-e` literally inside `Arguments`
            //       (e.g. through `ValueFromRemainingArguments` collection
            //       — happens when the transpiler emits a string that
            //       contains a `-e` after a positional that already bound).
            //
            // The unified scan handles both: walk `rawArgs`; when we hit
            // `-e` advance past it and start collecting; when we hit
            // any other `-`-prefixed token, skip it AND its value (for
            // value-bearing flags we know about); otherwise the token is
            // an item iff EchoMode is bound — case (a).
            int j = 0;
            bool seenLiteralE = Array.IndexOf(rawArgs, "-e") >= 0;
            while (j < rawArgs.Length)
            {
                var t = rawArgs[j];
                if (t == "-e")
                {
                    j++;
                    while (j < rawArgs.Length && !rawArgs[j].StartsWith("-"))
                    {
                        items.Add(rawArgs[j]);
                        j++;
                    }
                    continue;
                }
                if (t == "-n" || t == "-i")
                {
                    // Skip the value-flag and its argument; the first pass
                    // already extracted them.
                    j += 2;
                    continue;
                }
                if (t.StartsWith("--head-count="))
                {
                    j++;
                    continue;
                }
                if (t.StartsWith("-"))
                {
                    // Other `-`-prefixed token — skip.
                    j++;
                    continue;
                }
                // Positional token. In case (a) — EchoMode bound by
                // PowerShell, no literal `-e` in args — every positional is
                // an item. In case (b), positionals outside a `-e ... -`
                // run are NOT items (oracle parity).
                if (!seenLiteralE)
                {
                    items.Add(t);
                }
                j++;
            }
        }
        else if (rangeStart.HasValue && rangeEnd.HasValue)
        {
            for (int n = rangeStart.Value; n <= rangeEnd.Value; n++)
            {
                items.Add(n.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        else if (inputFile != null)
        {
            foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, inputFile))
            {
                try
                {
                    foreach (var line in BashFileSystem.ReadLines(filePath))
                    {
                        items.Add(line);
                    }
                }
                catch
                {
                    // Oracle uses `-ErrorAction SilentlyContinue` — no
                    // emission, no items added.
                }
            }
        }
        else if (_pipeline.Count > 0)
        {
            foreach (var obj in _pipeline)
            {
                items.Add(BashRuntime.GetBashText(obj));
            }
        }

        // Shuffle: Fisher-Yates with System.Random (no seed, matching the
        // oracle's `[System.Random]::new()`).
        var rng = new Random();
        for (int k = items.Count - 1; k > 0; k--)
        {
            int swap = rng.Next(k + 1);
            (items[k], items[swap]) = (items[swap], items[k]);
        }

        int emitCount = count.HasValue && count.Value < items.Count
            ? Math.Max(0, count.Value)
            : items.Count;

        for (int k = 0; k < emitCount; k++)
        {
            WriteObject(BashRuntime.NewBashObject(items[k]));
        }
    }
}
