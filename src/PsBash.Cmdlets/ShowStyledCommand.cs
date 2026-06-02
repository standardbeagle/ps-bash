using System.Management.Automation;
using Strata;
using Strata.Core;
using Strata.Css;
using Strata.Interaction;
using Strata.Properties.Styling;

namespace PsBash.Cmdlets;

/// <summary>
/// Interactive counterpart to <see cref="FormatStyledCommand"/>: renders a pipeline of objects as a
/// full-screen, navigable list where each row expands to a detail block and exposes action buttons,
/// driven by the same Strata stylesheets (<c>fs</c> / <c>procsvc</c> / <c>object</c> / <c>error</c>).
/// </summary>
/// <remarks>
/// <para>The tree is <c>Surface → Row* (→ Detail → DetailLine* + Button*)</c>. A Row's
/// <c>:expanded</c> pseudo-state (toggled with Enter) controls whether its Detail subtree is
/// present; arrow keys / j / k move <c>:focused</c>. The cascade + <see cref="Strata.Render.TerminalGui.TerminalGuiProjection"/>
/// reconcile a live Terminal.Gui View tree in place.</para>
/// <para><b>Headless</b> (redirected stdin/stdout — tests, CI, pipes): the view tree is built once to
/// prove the projection path, and a one-line summary object is emitted instead of entering the
/// interactive loop (which needs a real terminal driver). This mirrors Strata's Show-Processes
/// sample. The interactive loop only runs with a real console.</para>
/// </remarks>
[Cmdlet(VerbsCommon.Show, "Styled")]
[OutputType(typeof(string))]
public sealed class ShowStyledCommand : PSCmdlet
{
    /// <summary>The stylesheet name (<c>fs</c>, <c>procsvc</c>, <c>object</c>, <c>error</c>, …). Omitted → auto-picked from the first row's kind.</summary>
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

        var surface = BuildTree();
        var styleName = string.IsNullOrEmpty(Css) ? StyledStyles.AutoStyleForKind(KindOf(_rows[0])) : Css!;
        var css = ResolveCss(styleName);

        var registry = StylingProperties.CreateRegistry();
        LayoutProperties.RegisterAll(registry);
        InteractionProperties.RegisterAll(registry);
        var stylesheet = new CssStylesheetParser(new CssSelectorLanguage(), registry).Parse(css);
        var cascade = new Cascade(registry);

        // Headless (redirected I/O): build the view tree once, emit a summary, do not enter the
        // interactive loop. Keeps the cmdlet usable (and unit-testable) without a real terminal.
        if (Console.IsOutputRedirected || Console.IsInputRedirected)
        {
            using var projection = new Strata.Render.TerminalGui.TerminalGuiProjection { TextSelector = NodeText };
            var result = cascade.Compute(surface, stylesheet);
            var view = projection.Project(surface, result);
            WriteObject(
                $"Show-Styled (headless): style '{styleName}', {_rows.Count} rows, " +
                $"{projection.LiveViewCount} views, root has {view.Subviews.Count} top-level views. " +
                "Run in a real terminal for the interactive UI.");
            return;
        }

        RunInteractive(surface, stylesheet, cascade, styleName);
    }

    /// <summary>Build the <c>Surface → Row*</c> tree. Rows start collapsed (no Detail subtree).</summary>
    private StyledNode BuildTree()
    {
        var surface = new StyledNode("Surface");
        foreach (var row in _rows)
        {
            var node = new StyledNode(KindOf(row), id: null, classes: ClassesOf(row)) { Source = row };
            node.SetAttribute("Name", Summary(row));
            surface.Add(node);
        }

        return surface;
    }

    /// <summary>Populate (or clear) a Row's expandable Detail subtree: a DetailLine per property, then the family action buttons.</summary>
    private void SetExpanded(StyledNode row, bool expanded)
    {
        if (!expanded)
        {
            row.RemovePseudoState("expanded");
            row.SetChildren(Array.Empty<StyledNode>());
            return;
        }

        row.AddPseudoState("expanded");
        var children = new List<StyledNode>();
        var detail = new StyledNode("Detail");
        foreach (var name in DetailProperties(row.Source))
        {
            var line = new StyledNode("DetailLine", classes: new[] { "key" });
            line.SetAttribute("Name", $"{name}: {CellText(row.Source, name)}");
            detail.Add(line);
        }

        children.Add(detail);
        row.SetChildren(children);
    }

    /// <summary>Text for a node, by kind: Row summary, DetailLine "key: value", Button label; containers render no text.</summary>
    private static string NodeText(ITreeNode node)
    {
        if (node.Kind is "Surface" or "Detail")
        {
            return string.Empty;
        }

        return node.TryGetAttribute("Name", out var v) ? v?.ToString() ?? string.Empty : string.Empty;
    }

    private string ResolveCss(string nameOrInline)
    {
        if (nameOrInline.Contains('{') || nameOrInline.Contains('\n'))
        {
            return nameOrInline;
        }

        try
        {
            var resolved = GetUnresolvedProviderPathFromPSPath(nameOrInline);
            if (File.Exists(resolved))
            {
                return File.ReadAllText(resolved);
            }
        }
        catch (Exception ex) when (ex is System.Management.Automation.DriveNotFoundException or ProviderNotFoundException or ItemNotFoundException)
        {
            // Not a resolvable path — treat as a built-in / user stylesheet name.
        }

        return StyledStyles.Resolve(nameOrInline);
    }

    /// <summary>The collapsed summary text for a row: the -Property values joined, else Name, else ToString.</summary>
    private string Summary(PSObject row)
    {
        if (Property is { Length: > 0 })
        {
            return string.Join("  ", Property.Select(p => CellText(row, p)));
        }

        var name = CellText(row, "Name");
        return string.IsNullOrEmpty(name) ? (row.BaseObject?.ToString() ?? row.ToString()) : name;
    }

    /// <summary>Properties shown in a row's expanded detail: the explicit -Property list, else all gettable properties (sans class).</summary>
    private IEnumerable<string> DetailProperties(PSObject? row)
    {
        if (Property is { Length: > 0 })
        {
            return Property;
        }

        if (row is null)
        {
            return Array.Empty<string>();
        }

        return row.Properties
            .Where(p => p.IsGettable && !string.Equals(p.Name, ClassProperty, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name);
    }

    private static string CellText(PSObject? row, string name)
    {
        if (row is null)
        {
            return string.Empty;
        }

        try
        {
            return row.Properties[name]?.Value?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string KindOf(PSObject row)
    {
        var type = row.TypeNames.Count > 0 ? row.TypeNames[0] : string.Empty;
        var dot = type.LastIndexOf('.');
        return dot >= 0 ? type[(dot + 1)..] : type;
    }

    private IEnumerable<string> ClassesOf(PSObject row)
    {
        try
        {
            var raw = row.Properties[ClassProperty]?.Value?.ToString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Array.Empty<string>();
            }

            return raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The interactive Terminal.Gui loop: navigation toggles <c>:focused</c>, Enter toggles a row's
    /// <c>:expanded</c> Detail subtree, each change re-cascades and re-projects in place. Only runs
    /// with a real terminal (the headless branch returns before here); not exercised by unit tests,
    /// which always see redirected I/O.
    /// </summary>
    private void RunInteractive(StyledNode surface, IStylesheet stylesheet, Cascade cascade, string styleName)
    {
        Terminal.Gui.Application.Init();
        var projection = new Strata.Render.TerminalGui.TerminalGuiProjection { TextSelector = NodeText };
        try
        {
            var top = new Terminal.Gui.Toplevel();
            var window = new Terminal.Gui.Window
            {
                Title = $"Show-Styled [{styleName}] — Enter expands, ↑↓/jk move, q quits",
                X = 0,
                Y = 0,
                Width = Terminal.Gui.Dim.Fill(),
                Height = Terminal.Gui.Dim.Fill(),
            };
            top.Add(window);

            using var input = new Strata.Render.TerminalGui.TerminalGuiInputSource();
            var commands = new CommandRegistry();
            using var host = new InteractionHost(input, commands);

            var rows = surface.ChildNodes.ToArray();
            var current = cascade.Compute(surface, stylesheet);

            FocusController focus = null!;
            void Refresh()
            {
                current = cascade.Compute(surface, stylesheet);
                projection.Project(surface, current);
                host.Reconcile(surface, current);
                if (focus.Focused is { } f && projection.TryGetView(f, out var fv))
                {
                    fv.SetFocus();
                }

                window.SetNeedsDisplay();
            }

            focus = new FocusController(rows, onChange: _ => Refresh());
            SampleCommands.RegisterNavigation(commands, focus);
            commands.Register("toggle-expand", _ =>
            {
                if (focus.Focused is StyledNode row && row.Kind != "Surface")
                {
                    SetExpanded(row, !row.PseudoStates.Contains("expanded"));
                    Refresh();
                }
            });

            var rootView = projection.Project(surface, current);
            window.Add(rootView);
            host.Reconcile(surface, current);

            window.KeyDown += (_, key) =>
            {
                if (key.KeyCode is Terminal.Gui.KeyCode.Q or Terminal.Gui.KeyCode.Esc)
                {
                    Terminal.Gui.Application.RequestStop(top);
                    key.Handled = true;
                    return;
                }

                if (input.HandleKey(key) is not null)
                {
                    key.Handled = true;
                }
            };

            Terminal.Gui.Application.Run(top);
            top.Dispose();
        }
        finally
        {
            projection.Dispose();
            Terminal.Gui.Application.Shutdown();
        }
    }
}
