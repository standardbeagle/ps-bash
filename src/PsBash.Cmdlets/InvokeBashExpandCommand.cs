using System.Management.Automation;
using PsBash.Cmdlets.Args;
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
/// On a file-read failure the cmdlet emits a bash-style error through
/// <see cref="FileSystemHelpers.WriteBashError"/> and sets <c>$global:LASTEXITCODE = 1</c>,
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

    private const string OptInitial = "initial", OptTabs = "tabs";

    /// <summary>
    /// expand's option surface (GNU coreutils 9.4: -i/--initial, -t/--tabs=N|LIST, and the obsolete
    /// <c>-NUM</c> tab size). <c>--first-only</c> is a ps-bash spelling of <c>--initial</c> (older
    /// releases accepted it; kept because tests and scripts use it). GNU expand has no other options.
    /// </summary>
    private static readonly OptSpecSet ExpandSpec = new(
        new[]
        {
            new OptSpec(OptInitial, 'i', "initial"),
            new OptSpec(OptInitial, '\0', "first-only"),
            new OptSpec(OptTabs, 't', "tabs", OptKind.Value),
        },
        allowAbbrev: true,
        numericShorthandId: OptTabs,
        gnuInfoOptions: true);

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, ExpandSpec);

    internal sealed class ExpandArgs
    {
        public ParsedArgs Parsed = null!;
        public TabStopList Tabs = TabStopList.Default;
        public bool InitialOnly;
        public List<string> Operands = new();
        public string? Error;

        public bool Declined =>
            Parsed.HasError || Error is not null
            || Parsed.Has(OptSpecSet.HelpId) || Parsed.Has(OptSpecSet.VersionId);
    }

    /// <summary>
    /// Scan + resolve the tab stops. Every <c>-t</c> is concatenated into one list (GNU). The old
    /// scan silently used 8 for anything unparsable (<c>-t x</c>, <c>-t 0</c>, <c>-t 4,8</c> all
    /// expanded at 8) where GNU exits 1 ("tab size cannot be 0", "tab sizes must be ascending")
    /// or honours the list.
    /// </summary>
    internal static ExpandArgs Plan(string[] args)
    {
        var e = new ExpandArgs { Parsed = ScanArgs(args) };
        e.Operands = e.Parsed.Operands();
        if (e.Parsed.HasError) return e;

        e.InitialOnly = e.Parsed.Has(OptInitial);
        var values = new List<string>();
        foreach (var t in e.Parsed.All(OptTabs)) values.Add(t.Value!);
        if (values.Count > 0)
        {
            if (!TabStopList.TryParse(values, out var tabs, out var err))
            {
                e.Error = $"expand: {err}";
                return e;
            }
            e.Tabs = tabs;
        }
        return e;
    }

    // Parsed-once state.
    private bool _parsed;
    private TabStopList _tabs = TabStopList.Default;
    private bool _initialOnly;
    private List<string> _operands = new();
    private ExpandArgs? _plan;
    // True when stdin must NOT be streamed: file operands present, a scan/value error, or a
    // --help / --version request (all short-circuit before the scan can fail).
    private bool _suppressStdin;

    private void ParseOnce()
    {
        if (_parsed) return;
        _parsed = true;

        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--version") >= 0 || Array.IndexOf(args, "--help") >= 0)
        {
            _suppressStdin = true;
            return;
        }

        var plan = Plan(args);
        _plan = plan;
        _tabs = plan.Tabs;
        _initialOnly = plan.InitialOnly;
        _operands = plan.Operands;
        _suppressStdin = plan.Declined || _operands.Count > 0;
    }
    protected override void ProcessRecord()
    {
        if (InputObject == null) return;

        ParseOnce();
        if (_suppressStdin) return;

        // Pipeline mode: expand each stdin sub-line as it arrives instead of
        // buffering the whole pipe.
        // TRANSFORMER: fresh text; the missing final newline is copied through (GNU).
        foreach (var (subLine, unterminated) in BashRuntime.RecordLines(InputObject))
            WriteObject(BashRuntime.TextRecord(ExpandTabs(subLine, _tabs, _initialOnly), unterminated));
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

        if (_plan is { } plan)
        {
            if (FileSystemHelpers.TryWriteParseError(this, "expand", plan.Parsed)) return;
            if (FileSystemHelpers.TryHandleInfoOptions(this, "expand", plan.Parsed)) return;
            if (plan.Error is { } planError)
            {
                FileSystemHelpers.WriteBashError(this, planError);
                FileSystemHelpers.SetLastExitCode(this, 1);
                return;
            }
        }

        // Pipeline mode (no operands) was already streamed in ProcessRecord.
        if (_operands.Count == 0) return;

        bool hadError = false;
        foreach (var raw in _operands)
        {
            foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
            {
                try
                {
                    foreach (var line in BashFileSystem.ReadTextLines(filePath))
                    {
                        WriteObject(BashRuntime.TextRecord(
                            ExpandTabs(line.Text, _tabs, _initialOnly), !line.HasTrailingNewline));
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

    internal static string ExpandTabs(string line, TabStopList tabs, bool initialOnly = false)
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
                int spaces = tabs.SpacesAt(col);
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

    private void WriteReadError(string path, Exception ex)
    {
        string msg = FileSystemHelpers.ReadErrorMessage(ex);
        string normalized = path.Replace('\\', '/');
        FileSystemHelpers.WriteBashError(this, $"expand: {normalized}: {msg}");
    }
}
