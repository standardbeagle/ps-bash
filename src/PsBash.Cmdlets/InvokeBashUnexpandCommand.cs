using System;
using System.Collections.Generic;
using System.IO;
using System.Management.Automation;
using PsBash.Cmdlets.Args;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashUnexpand</c> function
/// (REFACTOR-2 follow-on). Converts runs of spaces back to tabs, matching the
/// GNU coreutils <c>unexpand</c> command — the inverse of <c>expand</c>.
///
/// Behavioral parity oracle: the original psm1 function. The cmdlet reproduces
/// its two-mode structure byte-for-byte:
/// <list type="bullet">
/// <item><b>Default (leading-only) mode</b> — count L leading spaces, emit
/// <c>floor(L/tabWidth)</c> tabs followed by <c>L%tabWidth</c> spaces, then the
/// remainder of the line unchanged. Partial runs that don't reach a tabstop stay
/// as spaces.</item>
/// <item><b><c>-a</c> (all) mode</b> — walk every character. On each space,
/// bump a column counter and a space-run counter; when the column reaches a
/// tab-stop boundary (<c>col % tabWidth == 0</c>) AND at least two spaces have
/// accumulated, emit one tab and reset the run. On any non-space, flush any
/// pending spaces as literals and append the character. A partial run at end of
/// line stays as literal spaces.</item>
/// </list>
///
/// Flag surface: <c>-t N</c> / <c>-tN</c> / <c>--tabs=N</c> (tab width, default
/// 8), <c>-a</c> / <c>--all</c> (all-mode), <c>--first-only</c> (default mode —
/// preserved for arg-compat). <c>-a</c> is declared as an explicit
/// <see cref="SwitchParameter"/> because the bare token <c>-a</c> would
/// otherwise prefix-match the cmdlet's own <c>-Arguments</c> parameter — the
/// same hazard the <c>uname</c> migration handled. <c>-t</c> has no PowerShell
/// common-parameter prefix collision and is scanned out of
/// <see cref="Arguments"/> by the manual value-flag loop.
///
/// Pipeline + file dual mode follows the rev/strings pattern: pipeline mode
/// when there are no operands and pipeline input is present; file mode
/// otherwise, with glob expansion via
/// <see cref="FileSystemHelpers.ResolveOperandPaths"/>. Files are read with
/// <see cref="File.ReadAllText(string)"/> and CRLF-normalized to LF.
///
/// Output uses <see cref="BashRuntime.NewBashObject(string)"/> — the same
/// default <c>PsBash.TextOutput</c> shape the psm1 oracle produced.
///
/// <c>--help</c> delegates to psm1 <c>Show-BashHelp</c> via parameter-bound
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>
/// (AOT-safe: fixed script body, user-controlled tokens never concatenated
/// into the body).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashUnexpand")]
[OutputType(typeof(string))]
public sealed class InvokeBashUnexpandCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>
    /// <c>-a</c> declared as an explicit <see cref="SwitchParameter"/> because
    /// the bare token <c>-a</c> would otherwise prefix-match the cmdlet's own
    /// <c>-Arguments</c> parameter under PowerShell parameter binding (it is
    /// the only declared parameter starting with 'a'), causing a "Missing an
    /// argument for parameter 'Arguments'" error. Same hazard <c>uname</c>
    /// handled.
    /// </summary>
    [Parameter]
    public SwitchParameter a { get; set; }

    private const string OptAll = "all", OptFirstOnly = "first", OptTabs = "tabs", OptTabsNum = "tabsnum";

    /// <summary>
    /// unexpand's option surface (GNU coreutils 9.4: -a/--all, --first-only, -t/--tabs=N|LIST and the
    /// obsolete <c>-NUM</c> tab size). Like GNU, <c>-t</c> ENABLES <c>-a</c> and <c>--first-only</c>
    /// overrides it wherever it appears. GNU unexpand has no other options.
    /// </summary>
    private static readonly OptSpecSet UnexpandSpec = new(
        new[]
        {
            new OptSpec(OptAll, 'a', "all"),
            new OptSpec(OptFirstOnly, '\0', "first-only"),
            new OptSpec(OptTabs, 't', "tabs", OptKind.Value),
        },
        allowAbbrev: true,
        numericShorthandId: OptTabsNum,   // -NUM sets the tab size but, unlike -t, does NOT imply -a (oracle: GNU 9.4)
        gnuInfoOptions: true);

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, UnexpandSpec);

    internal sealed class UnexpandArgs
    {
        public ParsedArgs Parsed = null!;
        public TabStopList Tabs = TabStopList.Default;
        /// <summary>Convert blanks anywhere on the line, not just the leading run.</summary>
        public bool AllBlanks;
        public List<string> Operands = new();
        public string? Error;

        public bool Declined =>
            Parsed.HasError || Error is not null
            || Parsed.Has(OptSpecSet.HelpId) || Parsed.Has(OptSpecSet.VersionId);
    }

    /// <summary>Scan + resolve. FIX: <c>-t N</c> now implies <c>-a</c> (it was leading-only), and a
    /// bad/zero/non-ascending tab spec exits 1 instead of silently becoming 8.</summary>
    internal static UnexpandArgs Plan(string[] args)
    {
        var u = new UnexpandArgs { Parsed = ScanArgs(args) };
        u.Operands = u.Parsed.Operands();
        if (u.Parsed.HasError) return u;

        var values = new List<string>();
        foreach (var t in u.Parsed.Tokens)
        {
            if (t.Kind == ArgTokKind.Option && (t.OptId == OptTabs || t.OptId == OptTabsNum)) values.Add(t.Value!);
        }
        if (values.Count > 0)
        {
            if (!TabStopList.TryParse(values, out var tabs, out var err))
            {
                u.Error = $"unexpand: {err}";
                return u;
            }
            u.Tabs = tabs;
        }
        u.AllBlanks = (u.Parsed.Has(OptAll) || u.Parsed.Has(OptTabs)) && !u.Parsed.Has(OptFirstOnly);
        return u;
    }

    /// <summary>Arguments with the decoy-bound <c>-a</c> re-injected.</summary>
    private string[] ArgsWithDecoys() => BashRuntime.PrependDecoys(Arguments, (a.IsPresent, "-a"));

    // Parsed-once state.
    private bool _parsed;
    private TabStopList _tabs = TabStopList.Default;
    private bool _allSpaces;
    private List<string> _operands = new();
    private UnexpandArgs? _plan;
    // True when stdin must NOT be streamed: file operands present, a scan/value error, or a
    // --help / --version request.
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
        _tabs = plan.Tabs;
        _allSpaces = plan.AllBlanks;
        _operands = plan.Operands;
        _suppressStdin = plan.Declined || _operands.Count > 0;
    }
    // TRANSFORMER: fresh text; the missing final newline is copied through (GNU).
    private void EmitTransformed(string line, bool unterminated)
    {
        string transformed = _allSpaces
            ? UnexpandAll(line, _tabs)
            : UnexpandLeading(line, _tabs);
        WriteObject(BashRuntime.TextRecord(transformed, unterminated));
    }

    protected override void ProcessRecord()
    {
        if (InputObject == null) return;

        ParseOnce();
        if (_suppressStdin) return;

        // Pipeline mode: transform each stdin sub-line as it arrives instead of
        // buffering the whole pipe.
        foreach (var (sub, unterminated) in BashRuntime.RecordLines(InputObject))
            EmitTransformed(sub, unterminated);
    }

    protected override void EndProcessing()
    {
        ParseOnce();

        var args = ArgsWithDecoys();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "unexpand", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "unexpand"))
            {
                WriteObject(line);
            }
            return;
        }

        if (_plan is { } plan)
        {
            if (FileSystemHelpers.TryWriteParseError(this, "unexpand", plan.Parsed)) return;
            if (FileSystemHelpers.TryHandleInfoOptions(this, "unexpand", plan.Parsed)) return;
            if (plan.Error is { } planError)
            {
                FileSystemHelpers.WriteBashError(this, planError);
                FileSystemHelpers.SetLastExitCode(this, 1);
                return;
            }
        }

        // Pipeline mode (no operands) was already streamed in ProcessRecord.
        if (_operands.Count == 0) return;

        foreach (var raw in _operands)
        {
            foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
            {
                try
                {
                    foreach (var line in BashFileSystem.ReadTextLines(filePath))
                    {
                        EmitTransformed(line.Text, !line.HasTrailingNewline);
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

    /// <summary>
    /// Default mode: convert only the leading run of spaces to tabs (greedy up to each tab stop;
    /// leftover spaces stay). Mirrors the psm1 oracle's leading-only branch for a uniform tab size.
    /// </summary>
    internal static string UnexpandLeading(string line, TabStopList tabs)
    {
        int leading = 0;
        while (leading < line.Length && line[leading] == ' ')
        {
            leading++;
        }
        if (leading == 0) return line;

        var sb = new StringBuilder(line.Length);
        int pos = 0;
        while (true)
        {
            int next = tabs.NextStop(pos);
            if (next < 0 || next > leading) break;
            sb.Append('\t');
            pos = next;
        }
        sb.Append(' ', leading - pos);
        sb.Append(line, leading, line.Length - leading);
        return sb.ToString();
    }

    /// <summary>
    /// -a mode: convert every run of spaces (at any column) that reaches a tab stop with at least
    /// two spaces in the run. Mirrors the psm1 oracle's all-spaces branch byte-for-byte for a
    /// uniform tab size; a tab list has no stops past its last element, so nothing converts there.
    /// </summary>
    internal static string UnexpandAll(string line, TabStopList tabs)
    {
        var sb = new StringBuilder(line.Length);
        int col = 0;
        int spaceRun = 0;
        int nextStop = tabs.NextStop(0);
        foreach (var ch in line)
        {
            if (ch == ' ')
            {
                spaceRun++;
                col++;
                if (nextStop >= 0 && col == nextStop)
                {
                    if (spaceRun >= 2)
                    {
                        sb.Append('\t');
                        spaceRun = 0;
                    }
                    nextStop = tabs.NextStop(col);
                }
            }
            else
            {
                if (spaceRun > 0)
                {
                    sb.Append(' ', spaceRun);
                    spaceRun = 0;
                }
                sb.Append(ch);
                col++;
                if (nextStop >= 0 && col >= nextStop) nextStop = tabs.NextStop(col);
            }
        }
        if (spaceRun > 0)
        {
            sb.Append(' ', spaceRun);
        }
        return sb.ToString();
    }
    private void WriteReadError(string path, Exception ex)
    {
        string msg = FileSystemHelpers.ReadErrorMessage(ex);
        string normalized = path.Replace('\\', '/');
        FileSystemHelpers.WriteBashError(this, $"unexpand: {normalized}: {msg}");
    }
}
