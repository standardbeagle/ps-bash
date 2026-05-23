using System.Management.Automation;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;
using Strata;
using Strata.Adapters.PSObject;
using Strata.Core;
using Strata.Css;
using Strata.Layout.Yoga;
using Strata.Properties.Styling;
using Strata.Render.Spectre;

namespace PsBash.Cmdlets;

/// <summary>
/// Styles a pipeline of objects with a CSS stylesheet and renders them to the host
/// via the Strata selector engine + Spectre.Console projection.
/// </summary>
/// <remarks>
/// <para>Pipeline: input objects -&gt; <see cref="PsObjectTreeAdapter"/> (flat tree under a
/// synthetic root) -&gt; CSS cascade -&gt; <see cref="SpectreProjection"/> -&gt; ANSI text.</para>
/// <para>Selectors match each row by Strata <c>Kind</c> (the object's type name with the
/// namespace stripped, e.g. <c>Process</c>), by <c>Id</c> (the <c>Id</c> or <c>Name</c>
/// property), and by class labels read from the property named by <c>-ClassProperty</c>
/// (default <c>class</c>, space-separated). Example stylesheet:</para>
/// <code>
/// Process { color: grey }
/// .high-cpu { color: red; font-weight: bold }
/// .zombie  { color: yellow; text-decoration: strikethrough }
/// </code>
/// <example>
///   <code>Get-Process | Format-Styled procs.css -Property Name,CPU</code>
/// </example>
/// </remarks>
[Cmdlet(VerbsCommon.Format, "Styled")]
[OutputType(typeof(string))]
public sealed class FormatStyledCommand : PSCmdlet
{
    /// <summary>
    /// The stylesheet to apply. Accepts inline CSS text, a path to a <c>.css</c> file,
    /// or the name of a built-in / user stylesheet (<c>default</c>, <c>ls</c>, <c>ps</c>,
    /// …). Omitted entirely → the built-in <c>default</c> stylesheet. A value containing
    /// <c>{</c> or a newline is treated as inline CSS; a value resolving to an existing
    /// file is read as that file; otherwise it is a stylesheet name resolved against the
    /// built-ins plus any user override (see <see cref="ResolveStylesheet"/>).
    /// </summary>
    [Parameter(Position = 0)]
    [Alias("Style", "Stylesheet")]
    public string? Css { get; set; }

    /// <summary>The objects to style. Accumulated across the pipeline and rendered together.</summary>
    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>
    /// Properties to render per row, in order. When omitted, each row renders via its
    /// <see cref="object.ToString"/>.
    /// </summary>
    [Parameter]
    public string[]? Property { get; set; }

    /// <summary>
    /// Name of the property whose value supplies class labels (string of space-separated
    /// names, or a string sequence). Defaults to <c>class</c>.
    /// </summary>
    [Parameter]
    public string ClassProperty { get; set; } = "class";

    /// <summary>
    /// Render each object as a property list (Format-List style): a two-column grid of
    /// bold property names and their values, laid out via Strata's <c>display: grid</c>.
    /// When no stylesheet is given, the built-in <c>list</c> sheet supplies the styling.
    /// </summary>
    [Parameter]
    public SwitchParameter List { get; set; }

    /// <summary>
    /// Render the objects as a grid table (Format-Table style): one column per property
    /// with a bold header row, laid out via Strata's <c>display: grid</c>. When no
    /// stylesheet is given, the built-in <c>table</c> sheet supplies the styling.
    /// </summary>
    [Parameter]
    public SwitchParameter Table { get; set; }

    /// <summary>Note-property name carrying a synthetic grid cell's display text.</summary>
    private const string CellTextProperty = "__StrataCellText";

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

        if (List.IsPresent || Table.IsPresent)
        {
            RenderGridMode(asTable: Table.IsPresent);
            return;
        }

        var css = string.IsNullOrEmpty(Css) ? ResolveStylesheet("default") : ResolveCss(Css);

        var registry = StylingProperties.CreateRegistry();
        var stylesheet = new CssStylesheetParser(new CssSelectorLanguage(), registry).Parse(css);

        // Synthetic root holds the pipeline rows as its children; rows themselves are leaves.
        var rootSource = new PSObject();
        rootSource.TypeNames.Insert(0, "StrataRoot");

        var adapter = new PsObjectTreeAdapter(
            childAccessor: src => ReferenceEquals(src, rootSource)
                ? _rows
                : Array.Empty<PSObject>(),
            classes: PsObjectTreeAdapter.ClassesFromProperty(ClassProperty));

        var root = adapter.Wrap(rootSource);
        var cascade = new Cascade(registry).Compute(root, stylesheet);

        var projection = new SpectreProjection { TextSelector = RenderRowText };
        var renderable = projection.Project(root, cascade);

        WriteObject(RenderToAnsi(renderable));
    }

    /// <summary>
    /// Render <see cref="_rows"/> as a Strata <c>display: grid</c>: a property list (two-column
    /// name/value grid, <paramref name="asTable"/> = false) or a grid table (one column per
    /// property with a header row, <paramref name="asTable"/> = true). Cells are synthetic
    /// single-leaf objects; the grid rule is injected on the synthetic root so the column count
    /// matches the data, and the cascade + Yoga layout pass drive the Spectre grid projection.
    /// </summary>
    private void RenderGridMode(bool asTable)
    {
        var props = ResolveProperties();
        if (props.Length == 0)
        {
            return;
        }

        var cells = new List<PSObject>();
        int columns;
        if (asTable)
        {
            columns = props.Length;
            foreach (var p in props)
            {
                cells.Add(MakeCell(p, "StrataHeader", "header"));
            }

            foreach (var row in _rows)
            {
                foreach (var p in props)
                {
                    cells.Add(MakeCell(CellText(row, p), "StrataCell", "cell"));
                }
            }
        }
        else
        {
            columns = 2;
            foreach (var row in _rows)
            {
                foreach (var p in props)
                {
                    cells.Add(MakeCell(p, "StrataName", "property-name"));
                    cells.Add(MakeCell(CellText(row, p), "StrataValue", "property-value"));
                }
            }
        }

        // Built-in styling per mode unless the caller supplied a sheet. The injected grid rule
        // (display + the data-driven column count) always comes first so user/built-in colour
        // and weight rules cascade on top of it.
        var baseCss = string.IsNullOrEmpty(Css)
            ? ResolveStylesheet(asTable ? "table" : "list")
            : ResolveCss(Css);
        var tracks = string.Join(" ", Enumerable.Repeat("auto", columns));
        var css = $"StrataRoot {{ display: grid; grid-template-columns: {tracks} }}\n{baseCss}";

        var registry = StylingProperties.CreateRegistry();
        // The grid path declares layout properties (display, grid-template-columns); register
        // their descriptors too or the parser rejects them as unknown.
        LayoutProperties.RegisterAll(registry);
        var stylesheet = new CssStylesheetParser(new CssSelectorLanguage(), registry).Parse(css);

        var rootSource = new PSObject();
        rootSource.TypeNames.Insert(0, "StrataRoot");

        var adapter = new PsObjectTreeAdapter(
            childAccessor: src => ReferenceEquals(src, rootSource)
                ? cells
                : Array.Empty<PSObject>(),
            classes: PsObjectTreeAdapter.ClassesFromProperty(ClassProperty));

        var root = adapter.Wrap(rootSource);
        var cascade = new Cascade(registry).Compute(root, stylesheet);
        // A grid container makes the tree non-trivial, so the 3-arg projection honours the grid.
        var layout = YogaLayoutPass.Compute(root, cascade, Strata.Layout.Yoga.Size.Unbounded);

        var projection = new SpectreProjection { TextSelector = CellTextSelector };
        var renderable = projection.Project(root, cascade, layout);

        WriteObject(RenderToAnsi(renderable));
    }

    /// <summary>Properties to render: the explicit <see cref="Property"/> list, else the first row's gettable properties.</summary>
    private string[] ResolveProperties()
    {
        if (Property is { Length: > 0 })
        {
            return Property;
        }

        return _rows[0].Properties
            .Where(p => p.IsGettable
                && !string.Equals(p.Name, ClassProperty, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(p.Name, CellTextProperty, StringComparison.Ordinal))
            .Select(p => p.Name)
            .ToArray();
    }

    /// <summary>Read property <paramref name="name"/> off <paramref name="row"/> as text; calculated-property failures render empty.</summary>
    private static string CellText(PSObject row, string name)
    {
        try
        {
            return row.Properties[name]?.Value?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Build a synthetic single-leaf grid cell: <paramref name="kind"/> as its Strata Kind, <paramref name="cssClass"/> as its class, and <paramref name="text"/> as its display text.</summary>
    private static PSObject MakeCell(string text, string kind, string cssClass)
    {
        var cell = new PSObject();
        cell.TypeNames.Insert(0, kind);
        cell.Properties.Add(new PSNoteProperty("class", cssClass));
        cell.Properties.Add(new PSNoteProperty(CellTextProperty, text));
        return cell;
    }

    /// <summary>Text selector for synthetic grid cells: reads the cell's stored display text.</summary>
    private static string CellTextSelector(ITreeNode node)
    {
        var source = node is PsObjectNode psNode ? psNode.Source : null;
        return source?.Properties[CellTextProperty]?.Value?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Resolve the stylesheet argument to CSS text: inline CSS as-is, an existing file's
    /// contents, or a built-in / user stylesheet by name (via <see cref="ResolveStylesheet"/>).
    /// </summary>
    private string ResolveCss(string value)
    {
        // Inline CSS always contains a rule block (or spans lines); a path/name never does.
        // Skipping path resolution here also avoids PowerShell reading the text before a
        // `color:` declaration as a drive name (DriveNotFoundException).
        if (value.Contains('{') || value.Contains('\n'))
        {
            return value;
        }

        try
        {
            var resolved = GetUnresolvedProviderPathFromPSPath(value);
            if (File.Exists(resolved))
            {
                return File.ReadAllText(resolved);
            }
        }
        catch (Exception ex) when (ex is System.Management.Automation.DriveNotFoundException or ProviderNotFoundException or ItemNotFoundException)
        {
            // Not a resolvable provider path — fall through and treat the argument as a name.
        }

        // Not inline and not a file: treat it as a built-in / user stylesheet name.
        return ResolveStylesheet(value);
    }

    /// <summary>
    /// Resolve a stylesheet <paramref name="name"/> (e.g. <c>default</c>, <c>ls</c>,
    /// <c>ps</c>) to CSS text. The built-in sheet (embedded in this assembly) is loaded
    /// first; a user override of the same name — found under <c>$PSBASH_STYLE_PATH</c>
    /// (a dir or PATH-separated list) or <c>~/.config/ps-bash/styles</c> /
    /// <c>~/.psbash/styles</c> — is appended after it, so the user's rules win via the
    /// CSS cascade. Throws <see cref="ItemNotFoundException"/> when neither exists.
    /// </summary>
    private static string ResolveStylesheet(string name)
    {
        var builtin = ReadEmbeddedStyle(name);
        var user = ReadUserOverride(name);
        if (builtin is null && user is null)
        {
            var names = string.Join(", ", BuiltinStyleNames());
            throw new ItemNotFoundException(
                $"Format-Styled: no stylesheet named '{name}'. Built-in: {names}. " +
                "Pass inline CSS, a .css path, or drop '<name>.css' in $PSBASH_STYLE_PATH or ~/.config/ps-bash/styles.");
        }

        // Cascade: built-in first, user override appended last so later rules win.
        return string.Join("\n", new[] { builtin, user }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>Read the embedded built-in stylesheet <c>styles/&lt;name&gt;.css</c>, or null.</summary>
    private static string? ReadEmbeddedStyle(string name)
    {
        var asm = typeof(FormatStyledCommand).Assembly;
        var suffix = $".styles.{name}.css";
        var resource = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (resource is null)
        {
            return null;
        }

        using var stream = asm.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Names of the embedded built-in stylesheets (for error messages).</summary>
    private static IEnumerable<string> BuiltinStyleNames()
    {
        const string mid = ".styles.";
        foreach (var n in typeof(FormatStyledCommand).Assembly.GetManifestResourceNames())
        {
            var i = n.IndexOf(mid, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && n.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            {
                yield return n.Substring(i + mid.Length, n.Length - (i + mid.Length) - ".css".Length);
            }
        }
    }

    /// <summary>Read a user override stylesheet <c>&lt;name&gt;.css</c> from the style dirs, or null.</summary>
    private static string? ReadUserOverride(string name)
    {
        foreach (var dir in UserStyleDirs())
        {
            try
            {
                var path = Path.Combine(dir, name + ".css");
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }
            }
            catch
            {
                // Malformed path entry — skip and try the next dir.
            }
        }

        return null;
    }

    /// <summary>User stylesheet search dirs: $PSBASH_STYLE_PATH (dir or list), then ~/.config and ~/.psbash.</summary>
    private static IEnumerable<string> UserStyleDirs()
    {
        var env = Environment.GetEnvironmentVariable("PSBASH_STYLE_PATH");
        if (!string.IsNullOrEmpty(env))
        {
            foreach (var d in env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return d;
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            yield return Path.Combine(home, ".config", "ps-bash", "styles");
            yield return Path.Combine(home, ".psbash", "styles");
        }
    }

    /// <summary>Per-row display text: selected properties joined, or the object's string form.</summary>
    private string RenderRowText(ITreeNode node)
    {
        var source = node is PsObjectNode psNode ? psNode.Source : null;
        if (source is null)
        {
            return string.Empty;
        }

        if (Property is { Length: > 0 })
        {
            var parts = new string[Property.Length];
            for (var i = 0; i < Property.Length; i++)
            {
                parts[i] = source.Properties[Property[i]]?.Value?.ToString() ?? string.Empty;
            }

            return string.Join("  ", parts);
        }

        return source.ToString() ?? string.Empty;
    }

    /// <summary>Render a Spectre renderable to an ANSI string sized to the host width.</summary>
    private string RenderToAnsi(IRenderable renderable)
    {
        var writer = new StringWriter();
        var width = TryGetHostWidth();

        // We always render into a StringWriter (not a terminal handle), so AnsiSupport.Detect
        // would disable color even in a real terminal. Force ANSI on — the emitted string is
        // handed to PowerShell, which prints the escapes in the host — and honor NO_COLOR.
        var noColor = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = noColor ? AnsiSupport.No : AnsiSupport.Yes,
            ColorSystem = noColor ? ColorSystemSupport.NoColors : ColorSystemSupport.Standard,
            Out = new AnsiConsoleOutput(writer),
        });

        if (width > 0)
        {
            console.Profile.Width = width;
        }

        console.Write(renderable);
        return writer.ToString().TrimEnd('\r', '\n');
    }

    private int TryGetHostWidth()
    {
        // Best-effort probe. A non-interactive host (in-process SDK runspace, redirected
        // output) throws HostException from RawUI.WindowSize; any failure means "unknown",
        // and we let Spectre pick its default width. Width detection must never break render.
        try
        {
            var width = Host?.UI?.RawUI?.WindowSize.Width ?? 0;
            return width > 0 ? width : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
