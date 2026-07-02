using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashExpand</c> function
/// (REFACTOR-2 follow-on). Converts tabs to spaces, with tab stops every
/// <c>-t N</c> columns (default 8).
///
/// Behavioral parity oracle: the original psm1 function. Supported flags
/// match the oracle exactly:
/// <list type="bullet">
/// <item><c>-t N</c>  — set uniform tab width to N (default 8).</item>
/// <item><c>-tN</c>   — joined form of <c>-t N</c>.</item>
/// <item><c>--tabs=N</c> — long form.</item>
/// </list>
/// The psm1 oracle does not implement multi-stop lists (<c>-t 4,8,12</c>);
/// passing one is preserved as a parity error (<c>[int]</c> cast throws on a
/// comma string — same in C# via <c>int.Parse</c>).
///
/// Two-path structure, matching the oracle:
/// <list type="bullet">
/// <item><b>Pipeline mode</b> — no operands + pipeline input present: each
/// pipeline item's <c>BashText</c> is split on <c>\n</c> after a trailing
/// newline trim; each resulting sub-line is fed through the tab-expansion
/// loop.</item>
/// <item><b>File mode</b> — operands are file paths (glob-expanded via
/// <see cref="FileSystemHelpers.ResolveOperandPaths"/>). Each file is read
/// with CRLF normalization and split into lines.</item>
/// </list>
///
/// Tab-expansion math (byte-for-byte parity with oracle): track current column
/// per line; on <c>\t</c>, emit <c>(tabWidth - col % tabWidth)</c> spaces and
/// advance the column by that amount; on any other char, append it and
/// advance the column by one.
///
/// Output: each expanded line is emitted via
/// <see cref="BashRuntime.NewBashObject(string)"/> — the same default
/// <c>PsBash.TextOutput</c> shape the psm1 oracle produced via
/// <c>New-BashObject -BashText</c>.
///
/// Flag-binding hazard: <c>-t</c> is a value flag whose name does NOT
/// prefix-collide with any PowerShell common parameter (no
/// <c>-Tab*</c> common parameters exist). It stays in the catch-all
/// <see cref="Arguments"/> array and is parsed by the manual scan in
/// <see cref="EndProcessing"/>. No <c>SwitchParameter</c> declarations are
/// needed.
///
/// On a file-read failure the cmdlet emits a bash-style error through the
/// psm1 <c>Write-BashError</c> sink and sets <c>$global:LASTEXITCODE = 1</c>,
/// matching the oracle's behavior (the oracle relied on
/// <c>Read-BashFileLines</c> to do the same thing).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashExpand")]
[OutputType(typeof(string))]
public sealed class InvokeBashExpandCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    // Valid GNU expand flags recognized but not implemented by ps-bash.
    // (-i/--first-only and --tabs in all forms are now implemented.)
    private static readonly HashSet<string> ExpandValidButUnsupported =
        new(StringComparer.Ordinal);

    // Parsed-once state.
    private bool _parsed;
    private int _tabWidth = 8;
    private bool _initialOnly;
    private List<string> _operands = new();
    // True when stdin must NOT be streamed: file operands present, or a
    // --help / --version request (both short-circuit the scan in the oracle —
    // important here because the scan can throw on a malformed -t value).
    private bool _suppressStdin;

    private void ParseOnce()
    {
        if (_parsed) return;
        _parsed = true;

        var args = Arguments ?? Array.Empty<string>();

        // --help / --version short-circuit before flag scanning (oracle order),
        // so a malformed -t value never throws ahead of a help request.
        if (Array.IndexOf(args, "--version") >= 0 || Array.IndexOf(args, "--help") >= 0)
        {
            _suppressStdin = true;
            return;
        }

        int i = 0;
        while (i < args.Length)
        {
            var a = args[i];
            // -tN (joined, digits only)
            if (a.Length > 2 && a[0] == '-' && a[1] == 't' && IsAllDigits(a, 2))
            {
                _tabWidth = BashRuntime.ParseCountClamped(a.AsSpan(2), fallback: 8);
                i++;
                continue;
            }
            // -t N (separate)
            if (a == "-t" && (i + 1) < args.Length)
            {
                _tabWidth = BashRuntime.ParseCountClamped(args[i + 1], fallback: 8);
                i += 2;
                continue;
            }
            // --tabs=N
            if (a.StartsWith("--tabs=", StringComparison.Ordinal))
            {
                _tabWidth = BashRuntime.ParseCountClamped(a.AsSpan("--tabs=".Length), fallback: 8);
                i++;
                continue;
            }
            // --tabs N (separate form)
            if (a == "--tabs" && (i + 1) < args.Length)
            {
                _tabWidth = BashRuntime.ParseCountClamped(args[i + 1], fallback: 8);
                i += 2;
                continue;
            }
            // -i / --first-only: convert only the leading (pre-text) tabs.
            if (a == "-i" || a == "--first-only")
            {
                _initialOnly = true;
                i++;
                continue;
            }
            _operands.Add(a);
            i++;
        }

        _suppressStdin = _operands.Count > 0;
    }

    protected override void ProcessRecord()
    {
        if (InputObject == null) return;

        ParseOnce();
        if (_suppressStdin) return;

        // Pipeline mode: expand each stdin sub-line as it arrives instead of
        // buffering the whole pipe.
        string text = BashRuntime.GetBashText(InputObject);
        string trimmed = text.TrimEnd('\n');
        if (trimmed.Contains('\n'))
        {
            foreach (var subLine in trimmed.Split('\n'))
            {
                WriteObject(BashRuntime.NewBashObject(ExpandTabs(subLine, _tabWidth, _initialOnly)));
            }
        }
        else
        {
            WriteObject(BashRuntime.NewBashObject(ExpandTabs(trimmed, _tabWidth, _initialOnly)));
        }
    }

    protected override void EndProcessing()
    {
        ParseOnce();

        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "expand", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "expand"))
            {
                WriteObject(line);
            }
            return;
        }

        if (FileSystemHelpers.TryWriteOperandOptionError(
                this, "expand", _operands, ExpandValidButUnsupported)) return;

        // Pipeline mode (no operands) was already streamed in ProcessRecord.
        if (_operands.Count == 0) return;

        bool hadError = false;
        foreach (var raw in _operands)
        {
            foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
            {
                try
                {
                    foreach (var line in BashFileSystem.ReadLines(filePath))
                    {
                        WriteObject(BashRuntime.NewBashObject(ExpandTabs(line, _tabWidth, _initialOnly)));
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
        if (hadError)
        {
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
    }

    private static string ExpandTabs(string line, int tabWidth, bool initialOnly = false)
    {
        var sb = new StringBuilder(line.Length);
        int col = 0;
        bool seenNonBlank = false;
        foreach (var ch in line)
        {
            if (ch == '\t')
            {
                // -i: once a non-blank char has appeared on the line, leave tabs
                // literal (GNU "do not convert tabs after non blanks").
                if (initialOnly && seenNonBlank)
                {
                    sb.Append('\t');
                    col++;
                    continue;
                }
                int spaces = tabWidth - (col % tabWidth);
                sb.Append(' ', spaces);
                col += spaces;
            }
            else
            {
                if (ch != ' ') seenNonBlank = true;
                sb.Append(ch);
                col++;
            }
        }
        return sb.ToString();
    }

    private static bool IsAllDigits(string s, int startIndex)
    {
        for (int k = startIndex; k < s.Length; k++)
        {
            if (s[k] < '0' || s[k] > '9') return false;
        }
        return true;
    }

    private void WriteReadError(string path, Exception ex)
    {
        bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
            || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
        string msg = notFound ? "No such file or directory" : ex.Message;
        string normalized = path.Replace('\\', '/');
        FileSystemHelpers.WriteBashError(this, $"expand: {normalized}: {msg}");
    }
}
