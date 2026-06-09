using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashFold</c> function
/// (REFACTOR-2 follow-on). Wraps each input line at a fixed column width,
/// matching the GNU coreutils <c>fold</c> command.
///
/// Behavioral parity oracle: the original psm1 <c>Invoke-BashFold</c>. The
/// cmdlet preserves the oracle's exact wrap semantics:
/// <list type="bullet">
/// <item><b>Default width is 80.</b> Override with <c>-w N</c>, <c>-wN</c>, or
/// <c>--width=N</c>.</item>
/// <item><b>Hard wrap (default).</b> Each line is sliced into <c>width</c>-char
/// segments; the final segment carries the remainder.</item>
/// <item><b>Soft wrap (<c>-s</c>).</b> When the wrap point falls mid-word, walk
/// backward to the previous space within the current window and break just
/// after that space; the trailing space stays on the prior segment. If no
/// space exists in the window, fall back to a hard break at <c>width</c>
/// (GNU behavior).</item>
/// <item><b>Bytes (<c>-b</c>).</b> Accepted for arg compatibility; behaves
/// identically to the default char-counting path for the ASCII text the oracle
/// supports. Documented as a no-op flag.</item>
/// </list>
///
/// Two input paths reproduce the oracle:
/// <list type="bullet">
/// <item><b>Pipeline mode</b> — no operands and pipeline input present: each
/// pipeline item's <c>BashText</c> is split on <c>\n</c> after trailing-newline
/// trim and each sub-line is fed to the wrap engine.</item>
/// <item><b>File mode</b> — otherwise operands are file paths (glob-expanded
/// via <see cref="FileSystemHelpers.ResolveOperandPaths"/>); each file is read
/// with CRLF normalization and split into lines (StreamReader.ReadLine
/// semantics — a trailing newline does not produce a spurious empty final
/// line).</item>
/// </list>
///
/// Output: every wrapped segment is emitted via
/// <see cref="BashRuntime.NewBashObject(string)"/> — the same default
/// <c>PsBash.TextOutput</c> shape the psm1 oracle produced.
///
/// No PowerShell common-parameter prefix collision: <c>-w</c> / <c>-s</c> /
/// <c>-b</c> have no common-parameter prefix overlap and stay in
/// <see cref="Arguments"/>; the manual scan parses them. On a file-read
/// failure the cmdlet emits a bash-style error through the psm1
/// <c>Write-BashError</c> sink (parameter-bound
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>,
/// no <c>ScriptBlock</c> construction — AOT-safe) and sets
/// <c>$global:LASTEXITCODE = 1</c>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashFold")]
[OutputType(typeof(string))]
public sealed class InvokeBashFoldCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    // -w takes a numeric value (`fold -w 4 file`). The bare token `-w`
    // prefix-collides with the PowerShell common parameters `-WarningAction`
    // and `-WarningVariable` under PSCmdlet parameter binding: an exact param
    // name match beats a common-parameter prefix match, so declaring `-w` as
    // an explicit value-bearing parameter resolves the collision and lets the
    // standard `-w N` form reach the cmdlet. Joined `-wN` and `--width=N`
    // continue to flow through `Arguments`.
    [Parameter]
    [Alias("w")]
    public string? Width { get; set; }

    // Likewise `-s` is unambiguous on its own (no common-parameter prefix
    // overlap) but declaring it explicitly keeps the binder from later
    // re-routing it; pure paranoia given the `-w` lesson.
    [Parameter]
    [Alias("s")]
    public SwitchParameter Spaces { get; set; }

    // Parsed-once state.
    private bool _parsed;
    private int _width = 80;
    private bool _breakSpaces;
    private List<string> _operands = new();
    // True when stdin must NOT be streamed: file operands present (file mode
    // ignores stdin) or a --help / --version request. Matches the buffered
    // oracle, which only consumed the pipeline when there were no operands.
    private bool _suppressStdin;

    private void ParseOnce()
    {
        if (_parsed) return;
        _parsed = true;

        var args = Arguments ?? Array.Empty<string>();
        _breakSpaces = Spaces.IsPresent;

        if (!string.IsNullOrEmpty(Width) && int.TryParse(Width, out int wBound))
        {
            _width = wBound;
        }

        int i = 0;
        while (i < args.Length)
        {
            string a = args[i];
            // -wN  (joined form)
            if (a.Length > 2 && a.StartsWith("-w") && int.TryParse(a.AsSpan(2), out int wJoined))
            {
                _width = wJoined;
                i++;
                continue;
            }
            // -w N
            if (a == "-w" && i + 1 < args.Length && int.TryParse(args[i + 1], out int wSep))
            {
                _width = wSep;
                i += 2;
                continue;
            }
            // --width=N
            if (a.StartsWith("--width=") && int.TryParse(a.AsSpan(8), out int wLong))
            {
                _width = wLong;
                i++;
                continue;
            }
            if (a == "-s" || a == "--spaces")
            {
                _breakSpaces = true;
                i++;
                continue;
            }
            if (a == "-b" || a == "--bytes")
            {
                // bytes mode — no-op for the ASCII text path; oracle parity.
                i++;
                continue;
            }
            _operands.Add(a);
            i++;
        }

        if (_width <= 0)
        {
            // Guard against zero/negative widths producing infinite loops.
            // The psm1 oracle never explicitly validated; on an int <= 0 the
            // while-loop in PowerShell would either never enter or stall on
            // the chunk arithmetic. Treat as "emit each line unchanged".
            _width = int.MaxValue;
        }

        bool helpOrVersion = Array.IndexOf(args, "--help") >= 0
            || Array.IndexOf(args, "--version") >= 0;
        _suppressStdin = _operands.Count > 0 || helpOrVersion;
    }

    protected override void ProcessRecord()
    {
        if (InputObject == null) return;

        ParseOnce();
        if (_suppressStdin) return;

        // Pipeline mode: wrap each stdin sub-line as it arrives instead of
        // buffering the whole pipe.
        string text = BashRuntime.GetBashText(InputObject);
        string trimmed = text.TrimEnd('\n');
        if (trimmed.Contains('\n'))
        {
            foreach (var sub in trimmed.Split('\n'))
            {
                EmitWrapped(sub, _width, _breakSpaces);
            }
        }
        else
        {
            EmitWrapped(trimmed, _width, _breakSpaces);
        }
    }

    protected override void EndProcessing()
    {
        ParseOnce();

        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "fold", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "fold"))
            {
                WriteObject(line);
            }
            return;
        }

        // Pipeline mode (no operands) was already streamed in ProcessRecord.
        if (_operands.Count == 0) return;

        // File mode.
        bool hadError = false;
        foreach (var raw in _operands)
        {
            foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
            {
                try
                {
                    foreach (var line in BashFileSystem.ReadLines(filePath))
                    {
                        EmitWrapped(line, _width, _breakSpaces);
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

    private void EmitWrapped(string line, int width, bool breakSpaces)
    {
        if (line.Length <= width)
        {
            WriteObject(BashRuntime.NewBashObject(line));
            return;
        }

        int pos = 0;
        while (pos < line.Length)
        {
            int remaining = line.Length - pos;
            if (remaining <= width)
            {
                WriteObject(BashRuntime.NewBashObject(line.Substring(pos)));
                break;
            }
            int chunkEnd = pos + width;
            if (breakSpaces)
            {
                // LastIndexOf(' ', startIndex, count) scans backward from
                // startIndex over `count` chars. Matches the psm1 oracle's
                // `$line.LastIndexOf(' ', $chunkEnd - 1, $width)`.
                int spaceIdx = line.LastIndexOf(' ', chunkEnd - 1, width);
                if (spaceIdx > pos)
                {
                    chunkEnd = spaceIdx + 1;
                }
                // else: no space within the window → hard break at width.
            }
            WriteObject(BashRuntime.NewBashObject(line.Substring(pos, chunkEnd - pos)));
            pos = chunkEnd;
        }
    }

    private void WriteReadError(string path, Exception ex)
    {
        bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
            || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
        string msg = notFound ? "No such file or directory" : ex.Message;
        string normalized = path.Replace('\\', '/');
        FileSystemHelpers.WriteBashError(this, $"fold: {normalized}: {msg}");
    }
}
