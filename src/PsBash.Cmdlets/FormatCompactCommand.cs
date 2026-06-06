using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// <c>Format-Compact</c> — a token-efficient replacement for <c>Format-Table</c> in agent
/// contexts. Object input renders as a single-header, min-width-aligned table with all-empty
/// columns dropped and rows beyond <c>-MaxRows</c> collapsed to a <c>+N more rows</c> line; a
/// single object renders inline as <c>key=value</c>; scalars pass through as lines. <c>-Ultra</c>
/// switches to tab separation (no alignment) for maximum density. The rendering logic lives in
/// <see cref="CompactObjectFormatter"/>; this cmdlet only adapts PSObjects to it.
/// </summary>
[Cmdlet(VerbsCommon.Format, "Compact")]
[OutputType(typeof(string))]
public sealed class FormatCompactCommand : PSCmdlet
{
    // PowerShell adds these onto filesystem/provider objects; they are noise in a compact view.
    private static readonly HashSet<string> NoiseProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "PSPath", "PSParentPath", "PSChildName", "PSDrive", "PSProvider", "PSIsContainer",
    };

    private readonly List<PSObject> _rows = [];

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>Tab-separate columns with no padding for maximum density.</summary>
    [Parameter]
    public SwitchParameter Ultra { get; set; }

    /// <summary>Show at most this many data rows; the rest collapse to a <c>+N more rows</c> line.</summary>
    [Parameter]
    public int MaxRows { get; set; } = CompactObjectFormatter.DefaultMaxRows;

    protected override void ProcessRecord()
    {
        if (InputObject is not null) _rows.Add(InputObject);
    }

    protected override void EndProcessing()
    {
        if (_rows.Count == 0) return;

        // Pure scalars (string / int / bool / enum / DateTime …) have no useful property
        // bag — emit them as plain lines, matching how a stream of strings would print.
        if (_rows.All(IsScalar))
        {
            foreach (var row in _rows) WriteObject(row.BaseObject?.ToString() ?? string.Empty);
            return;
        }

        var columns = _rows.First(r => !IsScalar(r)).Properties
            .Where(p => p.IsGettable && !NoiseProperties.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();

        var dicts = _rows.Select(r => ToDictionary(r, columns)).ToList();

        if (_rows.Count == 1)
        {
            WriteObject(CompactObjectFormatter.RenderInline(columns, dicts[0]));
            return;
        }

        foreach (var line in CompactObjectFormatter.RenderTable(columns, dicts, Ultra.IsPresent, MaxRows))
            WriteObject(line);
    }

    private static bool IsScalar(PSObject o)
    {
        var b = o.BaseObject;
        return b is string || b is ValueType; // primitives, enums, DateTime, decimal, …
    }

    private static IReadOnlyDictionary<string, string?> ToDictionary(PSObject o, IReadOnlyList<string> columns)
    {
        var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            string? value = null;
            try { value = o.Properties[column]?.Value?.ToString(); }
            catch (GetValueException) { /* unreadable property → empty cell */ }
            dict[column] = value;
        }
        return dict;
    }
}
