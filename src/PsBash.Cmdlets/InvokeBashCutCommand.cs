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
                         "param($n) Show-BashHelp $n", "cut"))
            {
                WriteObject(line);
            }
            return;
        }

        // Default delimiter is a tab (oracle).
        string delimiter = D ?? "\t";
        string fieldSpec = string.Empty;
        string charSpec = C ?? string.Empty;
        var operands = new List<string>();
        bool pastDoubleDash = false;

        int i = 0;
        while (i < args.Length)
        {
            string a = args[i];
            if (pastDoubleDash)
            {
                operands.Add(a);
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
                delimiter = a.Substring(2, 1);
                i++;
                continue;
            }

            // Bare `-f` flag — value follows in next arg.
            if (a == "-f")
            {
                i++;
                if (i < args.Length)
                {
                    fieldSpec = args[i];
                }
                i++;
                continue;
            }

            // Joined `-fLIST` form (oracle: ^-f(.+)$).
            if (a.Length > 2 && a[0] == '-' && a[1] == 'f')
            {
                fieldSpec = a.Substring(2);
                i++;
                continue;
            }

            // Joined `-cLIST` form (oracle: ^-c(.+)$). The bare `-c` is
            // already bound to the C parameter by the binder.
            if (a.Length > 2 && a[0] == '-' && a[1] == 'c')
            {
                charSpec = a.Substring(2);
                i++;
                continue;
            }

            operands.Add(a);
            i++;
        }

        // Collect input lines.
        var lines = new List<string>();
        if (operands.Count == 0 && _pipeline.Count > 0)
        {
            foreach (var item in _pipeline)
            {
                string text = BashRuntime.GetBashText(item);
                string trimmed = text.TrimEnd('\n');
                if (trimmed.Contains('\n'))
                {
                    foreach (var sub in trimmed.Split('\n'))
                    {
                        lines.Add(sub);
                    }
                }
                else
                {
                    lines.Add(trimmed);
                }
            }
        }
        else
        {
            foreach (var raw in operands)
            {
                foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
                {
                    var fileLines = ReadFileLines(filePath);
                    if (fileLines == null) continue;
                    lines.AddRange(fileLines);
                }
            }
        }

        // Pre-parse the active spec once.
        int[]? indices = null;
        try
        {
            if (charSpec.Length > 0)
            {
                indices = ParseSpec(charSpec);
            }
            else if (fieldSpec.Length > 0)
            {
                indices = ParseSpec(fieldSpec);
            }
        }
        catch (FormatException ex)
        {
            // Oracle: [int]$part on a non-integer token throws; we surface
            // the same failure mode via a bash-style error and bail.
            FileSystemHelpers.WriteBashError(this, $"cut: invalid list: {ex.Message}");
            return;
        }

        foreach (var line in lines)
        {
            string result;
            if (charSpec.Length > 0)
            {
                var sb = new StringBuilder();
                foreach (var pos in indices!)
                {
                    int idx = pos - 1;
                    if (idx >= 0 && idx < line.Length)
                    {
                        sb.Append(line[idx]);
                    }
                }
                result = sb.ToString();
            }
            else if (fieldSpec.Length > 0)
            {
                // String.Split(string) — single-string separator overload
                // matches PowerShell's .Split($string) behavior when the
                // delimiter is a multi-char string. The oracle uses
                // $Line.Split($delimiter) where $delimiter may be 1 char or
                // a string passed via -d. .NET's Split(string) splits on the
                // string as a substring boundary.
                string[] fields = line.Split(new[] { delimiter }, StringSplitOptions.None);
                var picks = new List<string>();
                foreach (var pos in indices!)
                {
                    int fi = pos - 1;
                    if (fi >= 0 && fi < fields.Length)
                    {
                        picks.Add(fields[fi]);
                    }
                }
                result = string.Join(delimiter, picks);
            }
            else
            {
                result = line;
            }
            WriteObject(BashRuntime.NewBashObject(result));
        }
    }

    /// <summary>
    /// Parse a cut list spec (comma-separated, with optional <c>N-M</c>
    /// ranges). Mirrors the oracle's <c>$parseSpec</c> scriptblock exactly:
    /// each part either matches <c>^(\d+)-(\d+)$</c> (inclusive range) or
    /// falls through to <c>[int]$part</c>. Open ranges and bare dashes are
    /// not supported — the oracle throws, we throw <see cref="FormatException"/>.
    /// </summary>
    private static int[] ParseSpec(string spec)
    {
        var result = new List<int>();
        foreach (var partRaw in spec.Split(','))
        {
            string part = partRaw;
            int dash = part.IndexOf('-');
            if (dash > 0 && dash < part.Length - 1)
            {
                string lo = part.Substring(0, dash);
                string hi = part.Substring(dash + 1);
                if (lo.Length > 0 && hi.Length > 0 &&
                    AllDigits(lo) && AllDigits(hi))
                {
                    int start = int.Parse(lo);
                    int end = int.Parse(hi);
                    for (int n = start; n <= end; n++) result.Add(n);
                    continue;
                }
            }
            // Oracle falls through to [int]$part — non-integer throws.
            result.Add(int.Parse(part));
        }
        return result.ToArray();
    }

    private static bool AllDigits(string s)
    {
        foreach (var c in s)
        {
            if (c < '0' || c > '9') return false;
        }
        return true;
    }

    /// <summary>
    /// Reads a file and returns its lines with CRLF normalization. A trailing
    /// <c>\n</c> does NOT produce a spurious empty final line, matching the
    /// psm1 oracle's <c>StreamReader.ReadLine()</c> semantics. Returns
    /// <c>null</c> and emits a bash-style error on read failure (the oracle's
    /// <c>Read-BashFileLines</c> contract).
    /// </summary>
    private string[]? ReadFileLines(string path)
    {
        try
        {
            string text = File.ReadAllText(path).Replace("\r\n", "\n");
            if (text.Length == 0) return Array.Empty<string>();
            bool trailingNl = text.EndsWith("\n", StringComparison.Ordinal);
            if (trailingNl)
            {
                text = text.Substring(0, text.Length - 1);
            }
            if (text.Length == 0 && trailingNl) return new[] { string.Empty };
            return text.Split('\n');
        }
        catch (Exception ex)
        {
            bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
                || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
            string msg = notFound ? "No such file or directory" : ex.Message;
            string normalized = path.Replace('\\', '/');
            FileSystemHelpers.WriteBashError(this, $"cut: {normalized}: {msg}");
            return null;
        }
    }
}
