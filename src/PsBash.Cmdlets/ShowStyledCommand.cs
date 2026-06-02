using System.Management.Automation;
using Strata.Core;
using Strata.Css;
using Strata.Interaction;
using Strata.Properties.Styling;

namespace PsBash.Cmdlets;

/// <summary>
/// Interactive counterpart to <see cref="FormatStyledCommand"/>: renders a pipeline of objects as a
/// full-screen, navigable list where each row expands to a detail block, driven by the same Strata
/// stylesheets (<c>fs</c> / <c>procsvc</c> / <c>object</c> / <c>error</c>) through Strata's Spectre
/// projection and a <see cref="Console.ReadKey(bool)"/> loop (see <see cref="StyledInteractiveSession"/>).
/// </summary>
/// <remarks>
/// <para><b>Headless</b> (redirected stdin/stdout — tests, CI, pipes): builds the styled tree and
/// emits a one-line summary; no interactive loop. <b>Interactive</b> (a real terminal): runs the
/// navigable viewer in process, exactly as the <c>browse</c> workbench does, so it shares the host's
/// line-reader terminal path and exits cleanly.</para>
/// </remarks>
[Cmdlet(VerbsCommon.Show, "Styled")]
[OutputType(typeof(string))]
public sealed class ShowStyledCommand : PSCmdlet
{
    /// <summary>The stylesheet name (<c>fs</c>, <c>procsvc</c>, <c>object</c>, <c>error</c>, …), inline CSS, or a .css path. Omitted → auto-picked from the first row's kind.</summary>
    [Parameter(Position = 0)]
    [Alias("Style", "Stylesheet")]
    public string? Css { get; set; }

    /// <summary>The objects to display.</summary>
    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>Properties shown in the collapsed summary line, in order. Omitted → Name / ToString.</summary>
    [Parameter]
    public string[]? Property { get; set; }

    /// <summary>Property supplying class labels (space-separated). Default <c>class</c>.</summary>
    [Parameter]
    public string ClassProperty { get; set; } = "class";

    private readonly List<PSObject> _rows = new();

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (InputObject is not null)
        {
            _rows.Add(InputObject);
        }
    }

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var styleName = string.IsNullOrEmpty(Css)
            ? StyledStyles.AutoStyleForKind(StyledInteractiveSession.KindOf(_rows[0]))
            : Css!;

        // Headless (redirected I/O): build the view tree once, emit a summary, no interactive loop.
        if (Console.IsOutputRedirected || Console.IsInputRedirected)
        {
            EmitHeadlessSummary(styleName);
            return;
        }

        // A real terminal: run the navigable viewer in process. If there is no usable terminal after
        // all, fall back to the summary so data is never lost.
        if (StyledInteractiveSession.RunInteractive(_rows, styleName, ClassProperty, Property) < 0)
        {
            EmitHeadlessSummary(styleName);
        }
    }

    /// <summary>Build the styled tree once and write a one-line summary (no interactive loop).</summary>
    private void EmitHeadlessSummary(string styleName)
    {
        var css = StyledInteractiveSession.ResolveCss(styleName);
        var registry = StylingProperties.CreateRegistry();
        LayoutProperties.RegisterAll(registry);
        InteractionProperties.RegisterAll(registry);
        var stylesheet = new CssStylesheetParser(new CssSelectorLanguage(), registry).Parse(css);
        var cascade = new Cascade(registry);

        var surface = StyledInteractiveSession.BuildSurface(_rows, ClassProperty, Property);
        // Compute the cascade so the summary path exercises the real stylesheet (surfacing CSS errors).
        _ = cascade.Compute(surface, stylesheet);
        WriteObject(
            $"Show-Styled (headless): style '{styleName}', {surface.ChildNodes.Count} rows. " +
            "Run in a real terminal for the interactive viewer.");
    }
}
