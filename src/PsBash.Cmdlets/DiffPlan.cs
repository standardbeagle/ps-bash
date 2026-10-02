using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>diff's output styles (GNU: giving two different ones is <c>conflicting output style options</c>).</summary>
internal enum DiffStyle { Normal, Context, Unified }

/// <summary>
/// Every <c>diff</c> option resolved from the ordered scan with GNU diffutils 3.10's rules (oracle-checked): the
/// context length is the MAXIMUM of every <c>-C N</c> / <c>-U N</c> / <c>--context[=N]</c> given (<c>-U2 -U1</c> is 2) and
/// at least 3 when a style was chosen without a number; two different styles are an error; at most two
/// <c>--label</c>s. Pure — built from the scan and nothing else.
/// </summary>
internal sealed class DiffPlan
{
    public const string OptContext = "context", OptContextLong = "context-long", OptUnified = "unified",
        OptUnifiedLong = "unified-long", OptBrief = "brief", OptReport = "report-identical-files",
        OptRecursive = "recursive", OptNewFile = "new-file", OptIgnoreCase = "ignore-case",
        OptIgnoreAllSpace = "ignore-all-space", OptIgnoreSpaceChange = "ignore-space-change",
        OptIgnoreBlankLines = "ignore-blank-lines", OptStripCr = "strip-trailing-cr", OptLabel = "label",
        OptText = "text";

    public const string TryHelp = "\ndiff: Try 'diff --help' for more information.";

    public DiffStyle Style { get; private set; } = DiffStyle.Normal;
    public int Context { get; private set; } = 3;
    public bool Brief { get; private set; }
    public bool ReportIdentical { get; private set; }
    public bool Recursive { get; private set; }
    public bool NewFile { get; private set; }
    public bool IgnoreCase { get; private set; }
    public bool IgnoreAllSpace { get; private set; }
    public bool IgnoreSpaceChange { get; private set; }
    public bool IgnoreBlankLines { get; private set; }
    public bool StripTrailingCr { get; private set; }
    public bool Text { get; private set; }
    public List<string> Labels { get; } = new();
    public List<string> Operands { get; private set; } = new();

    /// <summary>The options as typed, in order, joined by spaces — what GNU prints after <c>diff</c> in the per-file
    /// header of a directory comparison (<c>diff -ru d1/a d2/a</c>). Values of separate-argument options are kept, operands
    /// and <c>--</c> are not.</summary>
    public string SwitchString { get; private set; } = "";

    /// <summary>Resolves <paramref name="parsed"/>; on failure <paramref name="error"/> is the complete diagnostic
    /// (with the Try line where GNU prints one) and the exit status is 2.</summary>
    public static bool TryBuild(string[] argv, ParsedArgs parsed, out DiffPlan plan, out string? error)
    {
        plan = new DiffPlan();
        var result = Build(argv, parsed, plan);
        error = result;
        return result is null;
    }

    /// <summary>Fills <paramref name="plan"/>; returns null on success, else the complete diagnostic.</summary>
    private static string? Build(string[] argv, ParsedArgs parsed, DiffPlan plan)
    {
        string? error = null;
        int context = -1;
        bool styleGiven = false;

        bool SetStyle(DiffStyle style)
        {
            if (styleGiven && plan.Style != style)
            {
                error = "diff: conflicting output style options" + TryHelp;
                return false;
            }
            plan.Style = style;
            styleGiven = true;
            return true;
        }

        foreach (var t in parsed.Tokens)
        {
            if (t.Kind != ArgTokKind.Option) continue;
            switch (t.OptId)
            {
                case OptContext:
                case OptContextLong:
                case OptUnified:
                case OptUnifiedLong:
                    {
                        bool unified = t.OptId is OptUnified or OptUnifiedLong;
                        if (!SetStyle(unified ? DiffStyle.Unified : DiffStyle.Context)) return error;
                        // -c / -u / --context / --unified without a number mean 3 lines; a number only ever raises the length.
                        int numval = 3;
                        if (t.Value is not null)
                        {
                            if (!int.TryParse(t.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numval))
                            {
                                return $"diff: invalid context length '{t.Value}'" + TryHelp;
                            }
                        }
                        if (context < numval) context = numval;
                        break;
                    }
                case OptBrief: plan.Brief = true; break;
                case OptReport: plan.ReportIdentical = true; break;
                case OptRecursive: plan.Recursive = true; break;
                case OptNewFile: plan.NewFile = true; break;
                case OptIgnoreCase: plan.IgnoreCase = true; break;
                case OptIgnoreAllSpace: plan.IgnoreAllSpace = true; break;
                case OptIgnoreSpaceChange: plan.IgnoreSpaceChange = true; break;
                case OptIgnoreBlankLines: plan.IgnoreBlankLines = true; break;
                case OptStripCr: plan.StripTrailingCr = true; break;
                case OptText: plan.Text = true; break;
                case OptLabel:
                    if (plan.Labels.Count >= 2)
                    {
                        return "diff: too many file label options";
                    }
                    plan.Labels.Add(t.Value!);
                    break;
            }
        }
        if (context >= 0) plan.Context = context;

        plan.Operands = parsed.Operands();
        if (plan.Operands.Count == 0)
        {
            return "diff: missing operand after 'diff'" + TryHelp;
        }
        if (plan.Operands.Count == 1)
        {
            return $"diff: missing operand after '{plan.Operands[0]}'" + TryHelp;
        }
        if (plan.Operands.Count > 2)
        {
            return $"diff: extra operand '{plan.Operands[2]}'" + TryHelp;
        }

        // The switch string: every argv element that is not an operand (nor the `--` marker), in order.
        var operandIndexes = new HashSet<int>();
        foreach (var t in parsed.Tokens)
            if (t.Kind is ArgTokKind.Operand or ArgTokKind.DoubleDash) operandIndexes.Add(t.ArgIndex);
        var switches = new List<string>();
        for (int i = 0; i < argv.Length; i++)
            if (!operandIndexes.Contains(i)) switches.Add(argv[i]);
        plan.SwitchString = switches.Count == 0 ? "" : " " + string.Join(' ', switches);
        return null;
    }
}
