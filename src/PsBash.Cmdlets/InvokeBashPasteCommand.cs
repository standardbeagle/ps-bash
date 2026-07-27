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

    // Valid GNU paste flags not implemented by ps-bash. Implemented flags
    // (-d/-s) are NOT in this set.
    private static readonly HashSet<string> PasteValidButUnsupported =
        new(StringComparer.Ordinal)
        {
            "-z",
            "--zero-terminated",
        };

    // Buffered stdin. GNU paste reads standard input when it has no file
    // operands (or for a `-` operand), which is exactly the shape of the common
    // `… | paste -sd,` join-the-lines idiom. The psm1 oracle ignored pipeline
    // input entirely, so that idiom produced NOTHING — silently — even though
    // the command reference documented paste as pipeline-capable.
    private readonly List<string> _stdin = new();

    protected override void ProcessRecord()
    {
        if (InputObject == null) return;
        // A record may carry its trailing newline (printf/cat) or not (seq);
        // split so each line is one field either way.
        string text = BashRuntime.GetBashText(InputObject);
        if (text.EndsWith('\n')) text = text[..^1];
        foreach (var line in text.Split('\n'))
            _stdin.Add(line);
    }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "paste", args)) return;
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

            // BUNDLED short flags, e.g. the very common `paste -sd,` — `-s`
            // followed by `-d` whose value is the rest of the token. Without this
            // the whole token was taken as a FILE OPERAND and paste reported
            // "invalid option -- 's'".
            if (a.Length > 1 && a[0] == '-' && TryParseBundle(a, ref serial, ref delimiter, ref i, args))
                continue;

            operands.Add(a);
        }

        // GNU paste interprets backslash escapes in the delimiter (`\n` `\t`
        // `\\` `\0`). The oracle stored the raw string, so `paste -d'\n'` joined
        // with a literal backslash-n instead of a newline. Expanding here is a
        // no-op for the default tab (a real \x09 with no backslash).
        delimiter = ExpandPasteDelimiter(delimiter);

        if (FileSystemHelpers.TryWriteOperandOptionError(
                this, "paste", operands, PasteValidButUnsupported)) return;

        var filePaths = new List<string>();
        foreach (var raw in operands)
        {
            foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
            {
                filePaths.Add(filePath);
            }
        }

        // No file operands: read STDIN, like GNU paste.
        if (filePaths.Count == 0)
        {
            if (_stdin.Count == 0) return;
            if (serial)
            {
                WriteObject(BashRuntime.NewBashObject(string.Join(delimiter, _stdin)));
                return;
            }
            // Without -s, a single input source pastes one field per line, i.e.
            // each line passes through unchanged.
            foreach (var line in _stdin)
                WriteObject(BashRuntime.NewBashObject(line));
            return;
        }

        if (serial)
        {
            // Serial mode: each file becomes one line with its fields joined.
            foreach (var filePath in filePaths)
            {
                string? line = ReadSerialLine(filePath, delimiter);
                if (line is null)
                {
                    return;
                }
                WriteObject(BashRuntime.NewBashObject(line));
            }
            return;
        }

        // Normal mode: merge files line by line, padding short files with
        // empty strings up to the max line count.
        EmitParallelPaste(filePaths, delimiter);
    }

    /// <summary>
    /// Parses a BUNDLED short-flag token (<c>-sd,</c>, <c>-sd</c> with the value in
    /// the next arg, <c>-ds,</c>). Returns false — leaving the token to be treated
    /// as an operand — the moment an unknown letter appears, so a real filename
    /// that happens to start with <c>-</c> still reaches the operand classifier
    /// and produces the oracle's error message rather than being silently eaten.
    /// <c>d</c> consumes the REST of the token as the delimiter (or the next arg),
    /// matching GNU's value-flag-ends-the-bundle rule.
    /// </summary>
    private static bool TryParseBundle(
        string token, ref bool serial, ref string delimiter, ref int i, string[] args)
    {
        bool sawFlag = false;
        for (int k = 1; k < token.Length; k++)
        {
            switch (token[k])
            {
                case 's':
                    serial = true;
                    sawFlag = true;
                    break;
                case 'd':
                    // Rest of the token is the value; empty means "next arg".
                    if (k + 1 < token.Length) delimiter = token[(k + 1)..];
                    else if (i + 1 < args.Length) delimiter = args[++i];
                    return true;
                default:
                    return false;   // unknown letter: not a bundle we understand
            }
        }
        return sawFlag;
    }

    private void EmitParallelPaste(IReadOnlyList<string> filePaths, string delimiter)
    {
        var enumerators = new List<IEnumerator<string>>(filePaths.Count);
        var current = new string?[filePaths.Count];

        try
        {
            for (int i = 0; i < filePaths.Count; i++)
            {
                var e = BashFileSystem.ReadLines(filePaths[i]).GetEnumerator();
                enumerators.Add(e);
                current[i] = e.MoveNext() ? e.Current : null;
            }

            while (AnyNonNull(current))
            {
                var parts = new string[filePaths.Count];
                for (int i = 0; i < current.Length; i++)
                {
                    parts[i] = current[i] ?? string.Empty;
                }
                WriteObject(BashRuntime.NewBashObject(string.Join(delimiter, parts)));

                for (int i = 0; i < enumerators.Count; i++)
                {
                    current[i] = current[i] is not null && enumerators[i].MoveNext()
                        ? enumerators[i].Current
                        : null;
                }
            }
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            string path = enumerators.Count < filePaths.Count
                ? filePaths[enumerators.Count]
                : filePaths[Math.Max(0, enumerators.Count - 1)];
            WriteReadError(path, ex);
        }
        finally
        {
            foreach (var e in enumerators)
            {
                e.Dispose();
            }
        }
    }

    // Allocation-free replacement for `current.Any(l => l is not null)` — that
    // LINQ form built an enumerator + closure on every merged output line.
    private static bool AnyNonNull(string?[] items)
    {
        for (int i = 0; i < items.Length; i++)
            if (items[i] is not null) return true;
        return false;
    }

    private string? ReadSerialLine(string path, string delimiter)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            bool first = true;
            foreach (var line in BashFileSystem.ReadLines(path))
            {
                if (!first) sb.Append(delimiter);
                sb.Append(line);
                first = false;
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            WriteReadError(path, ex);
            return null;
        }
    }

    private void WriteReadError(string path, Exception ex)
    {
        bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
            || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
        string msg = notFound ? "No such file or directory" : ex.Message;
        string normalized = path.Replace('\\', '/');
        FileSystemHelpers.WriteBashError(this, $"paste: {normalized}: {msg}");
    }

    /// <summary>
    /// Expand GNU paste's recognized delimiter backslash escapes: <c>\n</c>
    /// (newline), <c>\t</c> (tab), <c>\\</c> (backslash), <c>\0</c> (empty / no
    /// separator). An unrecognized <c>\x</c> degrades to the literal char
    /// <c>x</c>. No backslash → returned unchanged (so a real tab default is a
    /// no-op).
    /// </summary>
    private static string ExpandPasteDelimiter(string d)
    {
        if (d.IndexOf('\\') < 0) return d;
        var sb = new System.Text.StringBuilder(d.Length);
        for (int i = 0; i < d.Length; i++)
        {
            if (d[i] == '\\' && i + 1 < d.Length)
            {
                char n = d[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '\\': sb.Append('\\'); break;
                    case '0': break; // \0 → empty separator
                    default: sb.Append(n); break;
                }
            }
            else
            {
                sb.Append(d[i]);
            }
        }
        return sb.ToString();
    }
}
