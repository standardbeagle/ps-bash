using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashPaste</c> function
/// (REFACTOR-2 follow-on). Merges corresponding lines from multiple files,
/// joined by a delimiter (tab by default).
///
/// Behavioral parity oracle: the original psm1 function. Flag surface:
/// <list type="bullet">
/// <item><c>-d DELIM</c> / <c>-dDELIM</c> — set the delimiter. Multi-char
/// delimiters cycle through their characters in the row direction (per GNU
/// coreutils paste). The psm1 oracle stored the whole string in
/// <c>$delimiter</c> and joined fields with <c>-join $delimiter</c> in normal
/// mode — i.e. the oracle did NOT cycle. We reproduce that exactly (bit-for-bit
/// parity, not GNU-correct cycling). A future fix to add real cycling should
/// land in the oracle first.</item>
/// <item><c>-s</c> — serial mode: each file's lines are concatenated into one
/// line using the delimiter. One emitted line per file.</item>
/// <item><c>--</c> — end of flags; remaining args are operands.</item>
/// <item><c>--help</c> — delegate to psm1 <c>Show-BashHelp paste</c>.</item>
/// </list>
///
/// No PowerShell common-parameter prefix collisions. <c>-d</c> / <c>-s</c> do
/// not match any common-parameter prefix (<c>-Debug</c> starts with 'D' but
/// PSCmdlet binder requires <c>-d</c> to disambiguate <c>-Debug</c> vs
/// <c>-Arguments</c>; here <c>-d</c> is consumed by the manual scan from
/// <see cref="Arguments"/> via the catch-all). The <see cref="Arguments"/>
/// catch-all suffices for the entire flag surface.
///
/// File reads route through <see cref="FileSystemHelpers.ResolveOperandPaths"/>
/// (glob expansion via <c>SessionState.Path</c>, same slice cat/rev use); a
/// failure emits a bash-style error via the psm1 <c>Write-BashError</c> sink
/// (parameter-bound <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>,
/// AOT-safe) and the cmdlet returns early — matching the oracle's behavior
/// where a <c>Read-BashFileLines</c> failure returned <c>$null</c> and the
/// outer function returned with no output.
///
/// Output: bare strings via <see cref="BashRuntime.NewBashObject(string)"/>
/// (default <c>PsBash.TextOutput</c>).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashPaste")]
[OutputType(typeof(string))]
public sealed class InvokeBashPasteCommand : PSCmdlet
{
    /// <summary>
    /// Explicit value-bearing parameter for <c>-d DELIM</c>. The bare token
    /// <c>-d</c> prefix-collides with the PowerShell common parameter
    /// <c>-Debug</c> under <see cref="PSCmdlet"/> binding (same hazard the
    /// <c>sed</c> migration documented for <c>-e</c>): without an explicit
    /// declaration the binder would route <c>-d</c> to <c>-Debug</c> and the
    /// delimiter argument would land as the first operand. Aliased
    /// <c>d</c> so the binder accepts both <c>-d</c> and the long form
    /// equivalent. The joined form <c>-dDELIM</c> (no whitespace) still flows
    /// through <see cref="Arguments"/> and is recovered post-parse.
    /// </summary>
    [Parameter]
    [Alias("d")]
    public string? Delimiter { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    protected override void ProcessRecord()
    {
        // paste in the psm1 oracle never consumes pipeline input — file
        // operands are required. The pipeline param exists only to swallow
        // accidental upstream items quietly, matching the oracle's
        // ignore-pipeline-input semantics.
    }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "paste"))
            {
                WriteObject(line);
            }
            return;
        }

        // Delimiter source precedence:
        //   1. Explicit -d DELIM bound to the typed Delimiter parameter (the
        //      binder beats Arguments here because of the prefix collision
        //      with -Debug — see the parameter's docstring).
        //   2. Joined form -dDELIM, still landing in Arguments.
        //   3. Default tab.
        string delimiter = Delimiter ?? "\t";
        bool serial = false;
        var operands = new List<string>();
        bool pastDoubleDash = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];

            if (pastDoubleDash)
            {
                operands.Add(a);
                continue;
            }

            if (a == "--")
            {
                pastDoubleDash = true;
                continue;
            }

            // Case-sensitive (-ceq in oracle): "-S" is not "-s".
            if (string.Equals(a, "-s", StringComparison.Ordinal))
            {
                serial = true;
                continue;
            }

            // Defensive: if the binder did NOT consume -d (e.g. it appeared
            // after --), fall back to the oracle's manual scan so the value
            // is not treated as an operand.
            if (string.Equals(a, "-d", StringComparison.Ordinal))
            {
                if (i + 1 < args.Length)
                {
                    delimiter = args[++i];
                }
                continue;
            }

            // Joined form: -d<chars> (case-sensitive on the 'd').
            if (a.Length > 2 && a[0] == '-' && a[1] == 'd')
            {
                delimiter = a.Substring(2);
                continue;
            }

            operands.Add(a);
        }

        // Read all files first. On any read failure, emit error and return
        // (matches oracle's `if ($null -eq $fileLines) { return }`).
        var allFiles = new List<string[]>();
        foreach (var raw in operands)
        {
            foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
            {
                string[]? fileLines = ReadFileLines(filePath);
                if (fileLines == null)
                {
                    return;
                }
                allFiles.Add(fileLines);
            }
        }

        if (allFiles.Count == 0)
        {
            return;
        }

        if (serial)
        {
            // Serial mode: each file becomes one line with its fields joined.
            foreach (var fileLines in allFiles)
            {
                WriteObject(BashRuntime.NewBashObject(string.Join(delimiter, fileLines)));
            }
            return;
        }

        // Normal mode: merge files line by line, padding short files with
        // empty strings up to the max line count.
        int maxLines = 0;
        foreach (var fileLines in allFiles)
        {
            if (fileLines.Length > maxLines) maxLines = fileLines.Length;
        }

        for (int lineIdx = 0; lineIdx < maxLines; lineIdx++)
        {
            var parts = new string[allFiles.Count];
            for (int f = 0; f < allFiles.Count; f++)
            {
                parts[f] = lineIdx < allFiles[f].Length ? allFiles[f][lineIdx] : string.Empty;
            }
            WriteObject(BashRuntime.NewBashObject(string.Join(delimiter, parts)));
        }
    }

    /// <summary>
    /// Read a file into a line array. CRLF-normalized. A trailing newline
    /// does NOT yield a spurious empty final line (StreamReader.ReadLine()
    /// semantics — same as the psm1 Read-BashFileLines oracle which used
    /// StreamReader internally).
    /// </summary>
    private string[]? ReadFileLines(string path)
    {
        try
        {
            string content = BashFileSystem.ReadAllText(path);
            if (content.Length == 0)
            {
                return Array.Empty<string>();
            }
            bool trailingNl = content[content.Length - 1] == '\n';
            if (trailingNl)
            {
                content = content.Substring(0, content.Length - 1);
            }
            if (content.Length == 0)
            {
                // Source was exactly "\n" → one empty line.
                return new[] { string.Empty };
            }
            return content.Split('\n');
        }
        catch (Exception ex)
        {
            bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
                || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
            string msg = notFound ? "No such file or directory" : ex.Message;
            string normalized = path.Replace('\\', '/');
            FileSystemHelpers.WriteBashError(this, $"paste: {normalized}: {msg}");
            return null;
        }
    }
}
