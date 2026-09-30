using System.Linq;
using System.Management.Automation;
using PsBash.Cmdlets.Args;

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
/// failure the cmdlet emits a bash-style error through
/// <see cref="FileSystemHelpers.WriteBashError"/> and sets
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
    // an explicit value-bearing parameter resolves the collision for DIRECT
    // PowerShell calls (`Invoke-BashFold -w 5`). From the transpiler every
    // dash word is single-quoted (PsEmitter.OrderedArgCommands) and reaches
    // Arguments verbatim; ArgsWithDecoys re-injects the bound decoys as `-w N` / `-s`.
    [Parameter]
    [Alias("w")]
    public string? Width { get; set; }

    [Parameter]
    [Alias("s")]
    public SwitchParameter Spaces { get; set; }

    private const string OptBytes = "bytes", OptSpaces = "spaces", OptWidth = "width";

    /// <summary>
    /// fold's option surface (GNU coreutils 9.4: -b -s -w + long forms and the obsolete
    /// <c>-NUM</c> width). GNU fold has no other options, so there is no valid-but-unsupported set;
    /// <c>-b</c> is accepted and counts characters (== bytes for the ASCII text ps-bash folds).
    /// </summary>
    private static readonly OptSpecSet FoldSpec = new(
        new[]
        {
            new OptSpec(OptBytes, 'b', "bytes"),
            new OptSpec(OptSpaces, 's', "spaces"),
            new OptSpec(OptWidth, 'w', "width", OptKind.Value),
        },
        allowAbbrev: true,
        numericShorthandId: OptWidth,
        gnuInfoOptions: true);

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, FoldSpec);

    internal sealed class FoldArgs
    {
        public ParsedArgs Parsed = null!;
        public int Width = 80;
        public bool BreakSpaces;
        public List<string> Operands = new();
        public string? Error;

        public bool Declined =>
            Parsed.HasError || Error is not null
            || Parsed.Has(OptSpecSet.HelpId) || Parsed.Has(OptSpecSet.VersionId);
    }

    /// <summary>
    /// Scan + validate. GNU accepts only a positive decimal width (digits, no suffix): the old
    /// scan silently ignored <c>-w x</c> (kept 80) and turned <c>-w 0</c> / a negative into "never
    /// wrap"; GNU exits 1 with "invalid number of columns". Last <c>-w</c> wins.
    /// </summary>
    internal static FoldArgs Plan(string[] args)
    {
        var f = new FoldArgs { Parsed = ScanArgs(args) };
        f.Operands = f.Parsed.Operands();
        if (f.Parsed.HasError) return f;

        foreach (var tok in f.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            if (tok.OptId == OptSpaces) f.BreakSpaces = true;
            else if (tok.OptId == OptWidth)
            {
                if (!TryWidth(tok.Value!, out int w))
                {
                    f.Error = $"fold: invalid number of columns: '{tok.Value}'";
                    return f;
                }
                f.Width = w;
            }
        }
        return f;
    }

    private static bool TryWidth(string s, out int width)
    {
        width = 0;
        if (s.Length == 0) return false;
        long v = 0;
        foreach (char c in s)
        {
            if (c < '0' || c > '9') return false;
            v = Math.Min(v * 10 + (c - '0'), int.MaxValue);
        }
        width = (int)v;
        return width >= 1;
    }

    /// <summary>Arguments with the decoy-bound flags re-injected (<c>-s</c>, <c>-w N</c>).</summary>
    private string[] ArgsWithDecoys()
    {
        var args = Arguments ?? Array.Empty<string>();
        var pre = new List<string>();
        if (Spaces.IsPresent) pre.Add("-s");
        if (!string.IsNullOrEmpty(Width)) { pre.Add("-w"); pre.Add(Width); }
        return pre.Count == 0 ? args : pre.Concat(args).ToArray();
    }

    // Parsed-once state.
    private bool _parsed;
    private int _width = 80;
    private bool _breakSpaces;
    private List<string> _operands = new();
    private FoldArgs? _plan;
    // True when stdin must NOT be streamed: file operands present (file mode
    // ignores stdin), a scan/value error, or a --help / --version request.
    private bool _suppressStdin;

    private void ParseOnce()
    {
        if (_parsed) return;
        _parsed = true;

        var args = ArgsWithDecoys();

        if (Array.IndexOf(args, "--version") >= 0 || Array.IndexOf(args, "--help") >= 0)
        {
            _suppressStdin = true;
            return;
        }

        var plan = Plan(args);
        _plan = plan;
        _width = plan.Width;
        _breakSpaces = plan.BreakSpaces;
        _operands = plan.Operands;
        _suppressStdin = plan.Declined || _operands.Count > 0;
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

        var args = ArgsWithDecoys();

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

        if (_plan is { } plan)
        {
            if (FileSystemHelpers.TryWriteParseError(this, "fold", plan.Parsed)) return;
            if (FileSystemHelpers.TryHandleInfoOptions(this, "fold", plan.Parsed)) return;
            if (plan.Error is { } planError)
            {
                FileSystemHelpers.WriteBashError(this, planError);
                FileSystemHelpers.SetLastExitCode(this, 1);
                return;
            }
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
