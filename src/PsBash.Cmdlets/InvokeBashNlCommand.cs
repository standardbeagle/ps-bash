using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashNl</c> function
/// (REFACTOR-2 follow-on). Numbers input lines, GNU coreutils <c>nl</c>-style:
/// each numbered line is rendered as <c>"{N,6}\t{LINE}"</c> (6-column
/// right-aligned line number, tab, then the line). By default empty lines
/// are emitted unnumbered (a bare empty line); <c>-ba</c> (also accepted as
/// <c>-b a</c>) numbers every line including empty lines.
///
/// Behavioral parity oracle: the original psm1 <c>Invoke-BashNl</c> function.
/// The cmdlet reproduces its two-path structure:
/// <list type="bullet">
/// <item><b>Pipeline mode</b> — when there are no file operands and pipeline
/// input is present, each pipeline item's <c>BashText</c> is split on <c>\n</c>
/// after trailing-newline trim; each resulting sub-line is collected into the
/// line buffer.</item>
/// <item><b>File mode</b> — otherwise the operands are treated as file paths
/// (glob-expanded via <see cref="FileSystemHelpers.ResolveOperandPaths"/>).
/// Each file is read with CRLF normalization and split into lines using
/// <c>StreamReader.ReadLine()</c> semantics (no spurious trailing empty
/// line).</item>
/// </list>
///
/// Output: each line is emitted via <see cref="BashRuntime.NewBashObject(string)"/>
/// — the default <c>PsBash.TextOutput</c> shape the psm1 oracle produced via
/// <c>New-BashObject -BashText</c>.
///
/// Flag binding: <c>-ba</c> is a compound short flag (no PowerShell common-
/// parameter prefix collision — case-insensitive <c>-ba</c> does not abbreviate
/// any of <c>-Verbose</c> / <c>-Debug</c> / <c>-Confirm</c> / <c>-WhatIf</c> /
/// <c>-Error*</c> / <c>-Warning*</c> / <c>-Information*</c> / <c>-Out*</c> /
/// <c>-Progress*</c> / <c>-PipelineVariable</c>). The bare token therefore
/// stays in <see cref="Arguments"/> and is parsed by the manual scan, matching
/// the psm1 oracle byte-for-byte. The oracle also accepts the split form
/// <c>-b a</c> (two consecutive tokens) — preserved here.
///
/// On a file-read failure the cmdlet emits a bash-style error through the
/// psm1 <c>Write-BashError</c> sink (parameter-bound <c>InvokeScript</c> —
/// no <c>ScriptBlock</c> construction, AOT-safe) and continues with the next
/// operand. The psm1 oracle did not set <c>$LASTEXITCODE = 1</c> for nl (the
/// internal <c>Read-BashFileLines</c> returns <c>$null</c> silently on miss);
/// parity is preserved.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashNl")]
[OutputType(typeof(string))]
public sealed class InvokeBashNlCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>-w N (number width) decoy: bare -w is ambiguous with -WarningAction/-WarningVariable.</summary>
    [Parameter] public string? W { get; set; }
    /// <summary>-v N (start number) decoy: bare -v abbreviates -Verbose.</summary>
    [Parameter] public string? V { get; set; }
    /// <summary>-i N (increment) decoy: bare -i is ambiguous with -Information*.</summary>
    [Parameter] public string? I { get; set; }

    // Valid GNU nl flags recognized but not implemented by ps-bash.
    private static readonly HashSet<string> NlValidButUnsupported =
        new(StringComparer.Ordinal)
        {
            "-bt", "-bn", "--body-numbering", "--header-numbering",
            "--footer-numbering", "--starting-line-number", "--line-increment",
            "--join-blank-lines", "--number-format", "--number-width",
            "--number-separator", "--section-delimiter",
        };

    // Parsed-once state.
    private bool _parsed;
    private bool _numberAll;
    private bool _numberNone;
    private int _width = 6;
    private string _sep = "\t";
    private int _start = 1;
    private int _incr = 1;
    private string _style = "rn"; // rn=right, ln=left, rz=right zero-padded
    private List<string> _operands = new();
    // True when stdin must NOT be streamed: file operands present, or a
    // --help / --version request (both short-circuit the scan in the oracle).
    private bool _suppressStdin;
    // Numbering counter — instance state so a streamed stdin run and any
    // trailing file reads number continuously.
    private int _lineNum;
    private string? _blankNum;   // cached `-b n` blank number field (width is fixed post-parse)

    private void ParseOnce()
    {
        if (_parsed) return;
        _parsed = true;

        var args = Arguments ?? Array.Empty<string>();

        // --help / --version short-circuit before flag scanning (oracle order).
        if (Array.IndexOf(args, "--version") >= 0 || Array.IndexOf(args, "--help") >= 0)
        {
            _suppressStdin = true;
            return;
        }

        // Bare -w/-v/-i arrive via the decoy parameters (common-param collisions).
        _width = ParseIntOr(W, _width);
        _start = ParseIntOr(V, _start);
        _incr = ParseIntOr(I, _incr);

        // Parse flags manually (mirrors the psm1 oracle's while loop).
        bool pastDoubleDash = false;
        int i = 0;
        while (i < args.Length)
        {
            string arg = args[i];

            if (pastDoubleDash)
            {
                _operands.Add(arg);
                i++;
                continue;
            }

            if (arg == "--")
            {
                pastDoubleDash = true;
                i++;
                continue;
            }

            // -b STYLE (a=all, t=non-empty[default], n=none) — `-ba` joined or `-b a` split.
            if (arg.Length == 3 && arg[0] == '-' && arg[1] == 'b')
            {
                ApplyBodyStyle(arg[2]);
                i++;
                continue;
            }
            if (string.Equals(arg, "-b", StringComparison.Ordinal))
            {
                i++;
                if (i < args.Length && args[i].Length == 1) ApplyBodyStyle(args[i][0]);
                i++;
                continue;
            }

            // -n FORMAT (ln / rn / rz) — number style.
            if (arg == "-n" && i + 1 < args.Length) { _style = NormalizeStyle(args[i + 1]); i += 2; continue; }
            if (arg.Length > 2 && arg.StartsWith("-n", StringComparison.Ordinal)) { _style = NormalizeStyle(arg.Substring(2)); i++; continue; }

            // -s SEP (separator between number and line).
            if (arg == "-s" && i + 1 < args.Length) { _sep = args[i + 1]; i += 2; continue; }
            if (arg.Length > 2 && arg.StartsWith("-s", StringComparison.Ordinal)) { _sep = arg.Substring(2); i++; continue; }

            // Joined -wN / -vN / -iN (the bare forms came through W/V/I).
            if (arg.Length > 2 && arg.StartsWith("-w", StringComparison.Ordinal) && int.TryParse(arg.Substring(2), out var w)) { _width = w; i++; continue; }
            if (arg.Length > 2 && arg.StartsWith("-v", StringComparison.Ordinal) && int.TryParse(arg.Substring(2), out var v)) { _start = v; i++; continue; }
            if (arg.Length > 2 && arg.StartsWith("-i", StringComparison.Ordinal) && int.TryParse(arg.Substring(2), out var inc)) { _incr = inc; i++; continue; }

            _operands.Add(arg);
            i++;
        }

        // Seed the counter so the first numbered line is exactly _start.
        _lineNum = _start - _incr;
        _suppressStdin = _operands.Count > 0;
    }

    private void ApplyBodyStyle(char s)
    {
        switch (s)
        {
            case 'a': _numberAll = true; _numberNone = false; break;
            case 'n': _numberNone = true; _numberAll = false; break;
            case 't': _numberAll = false; _numberNone = false; break;
            // 'p<BRE>' regex numbering is not supported; ignore.
        }
    }

    private static int ParseIntOr(string? s, int def) =>
        !string.IsNullOrEmpty(s) && int.TryParse(s, out var v) ? v : def;

    private static string NormalizeStyle(string s) => s switch
    {
        "ln" or "rn" or "rz" => s,
        _ => "rn",
    };

    private void EmitNumbered(string line)
    {
        // -b n: never number; GNU still prints the blank number field + separator.
        if (_numberNone)
        {
            _blankNum ??= new string(' ', _width);
            WriteObject(BashRuntime.NewBashObject(_blankNum + _sep + line));
            return;
        }
        // Default (-b t): empty lines are unnumbered (bare empty — oracle parity).
        if (!_numberAll && line.Length == 0)
        {
            WriteObject(BashRuntime.NewBashObject(string.Empty));
            return;
        }

        _lineNum += _incr;
        string num = _style switch
        {
            "ln" => _lineNum.ToString().PadRight(_width),
            "rz" => _lineNum.ToString().PadLeft(_width, '0'),
            _ => _lineNum.ToString().PadLeft(_width),
        };
        WriteObject(BashRuntime.NewBashObject(num + _sep + line));
    }

    protected override void ProcessRecord()
    {
        if (InputObject == null) return;

        ParseOnce();
        if (_suppressStdin) return;

        // Pipeline mode: number each stdin sub-line as it arrives instead of
        // buffering the whole pipe.
        string text = BashRuntime.GetBashText(InputObject);
        string trimmed = text.TrimEnd('\n');
        if (trimmed.Contains('\n'))
        {
            foreach (var subLine in trimmed.Split('\n'))
            {
                EmitNumbered(subLine);
            }
        }
        else
        {
            EmitNumbered(trimmed);
        }
    }

    protected override void EndProcessing()
    {
        ParseOnce();

        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "nl", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "nl"))
            {
                WriteObject(line);
            }
            return;
        }

        if (FileSystemHelpers.TryWriteOperandOptionError(
                this, "nl", _operands, NlValidButUnsupported)) return;

        // Pipeline mode (no operands) was already streamed in ProcessRecord.
        if (_operands.Count == 0) return;

        foreach (var raw in _operands)
        {
            foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
            {
                try
                {
                    foreach (var line in BashFileSystem.ReadLines(filePath))
                    {
                        EmitNumbered(line);
                    }
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    WriteReadError(filePath, ex);
                }
            }
        }
    }

    private void WriteReadError(string path, Exception ex)
    {
        bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
            || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
        string msg = notFound ? "No such file or directory" : ex.Message;
        string normalized = path.Replace('\\', '/');
        FileSystemHelpers.WriteBashError(this, $"nl: {normalized}: {msg}");
    }
}
