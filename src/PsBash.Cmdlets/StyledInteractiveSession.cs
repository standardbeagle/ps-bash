using System.Management.Automation;
using Spectre.Console;
using Spectre.Console.Rendering;
using Strata;
using Strata.Core;
using Strata.Css;
using Strata.Interaction;
using Strata.Properties.Styling;
using Strata.Render.Spectre;

namespace PsBash.Cmdlets;

/// <summary>
/// The interactive viewer for <c>Show-Styled</c>: a full-screen, navigable list rendered by Strata's
/// Spectre projection and driven by a <see cref="Console.ReadKey(bool)"/> loop — the same proven,
/// terminal-clean pattern the <c>browse</c> workbench uses (verified by its PTY parity test). Each
/// keystroke mutates the focused row's pseudo-state (<c>:focused</c> / <c>:expanded</c>), re-runs the
/// Strata cascade, and repaints the frame.
/// </summary>
/// <remarks>
/// <para>This deliberately uses the Spectre projection rather than Strata's Terminal.Gui projection:
/// Terminal.Gui v2 (prealpha) drives the tty through its own input loop / native termios calls and
/// leaves the host's stdin unusable after it exits, breaking the REPL. A <see cref="Console.ReadKey"/>
/// loop over the Spectre frame shares the exact terminal path the line editor uses, so it exits
/// cleanly. The AOT-clean Spectre engine is also what <c>Format-Styled</c> already ships.</para>
/// <para>Tree shape: <c>Surface → (Row | Detail)*</c>. Rows are leaves carrying the summary text;
/// when a row is expanded a <c>Detail</c> container (with one <c>DetailLine</c> leaf per property) is
/// stacked as the row's following sibling. The cascade styles rows by Kind/class plus the
/// <c>:focused</c> / <c>:expanded</c> pseudo-states from the active stylesheet.</para>
/// </remarks>
public static class StyledInteractiveSession
{
    /// <summary>
    /// Run the interactive viewer over <paramref name="rows"/> until the user quits (q / Esc).
    /// Returns 0 normally, or -1 when there is no interactive terminal (the caller then renders the
    /// headless summary).
    /// </summary>
    public static int RunInteractive(IReadOnlyList<PSObject> rows, string? style, string classProperty, string[]? property)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return -1; // no real terminal — caller falls back to the summary
        }

        var styleName = string.IsNullOrEmpty(style) ? StyledStyles.AutoStyleForKind(KindOf(rows[0])) : style!;
        var css = ResolveCss(styleName);

        var registry = StylingProperties.CreateRegistry();
        LayoutProperties.RegisterAll(registry);
        InteractionProperties.RegisterAll(registry);
        var stylesheet = new CssStylesheetParser(new CssSelectorLanguage(), registry).Parse(css);
        var cascade = new Cascade(registry);
        var projection = new SpectreProjection { TextSelector = NodeText };

        // Row leaves (focus ring) + an external expanded-set; the Detail blocks are rebuilt into the
        // surface each frame so the cascade sees the current expansion.
        var rowNodes = rows.Select(r =>
        {
            var n = new StyledNode(KindOf(r), id: null, classes: ClassesOf(r, classProperty)) { Source = r };
            n.SetAttribute("Name", Summary(r, property));
            return n;
        }).ToList();
        var expanded = new HashSet<StyledNode>();
        var focus = 0;
        rowNodes[0].AddPseudoState("focused");

        var surface = new StyledNode("Surface");

        void Rebuild()
        {
            var children = new List<StyledNode>(rowNodes.Count * 2);
            foreach (var row in rowNodes)
            {
                children.Add(row);
                if (expanded.Contains(row))
                {
                    children.Add(BuildDetail(row.Source, property, classProperty));
                }
            }

            surface.SetChildren(children);
        }

        try
        {
            Console.Write("\x1b[?1049h"); // alternate screen buffer
            try { Console.CursorVisible = false; } catch { /* unsupported host */ }

            while (true)
            {
                Rebuild();
                var result = cascade.Compute(surface, stylesheet);
                var frame = RenderToAnsi(projection.Project(surface, result));
                var footer = $"\n[{focus + 1}/{rowNodes.Count}]  ↑↓/jk move · Enter expand · q quit";
                Console.Write("\x1b[2J\x1b[H" + frame + footer);

                var key = Console.ReadKey(intercept: true);
                if (key.Key is ConsoleKey.Q or ConsoleKey.Escape)
                {
                    break;
                }

                if (key.Key is ConsoleKey.DownArrow || key.KeyChar == 'j')
                {
                    MoveFocus(rowNodes, ref focus, +1);
                }
                else if (key.Key is ConsoleKey.UpArrow || key.KeyChar == 'k')
                {
                    MoveFocus(rowNodes, ref focus, -1);
                }
                else if (key.Key is ConsoleKey.Enter or ConsoleKey.Spacebar)
                {
                    var row = rowNodes[focus];
                    if (!expanded.Remove(row))
                    {
                        expanded.Add(row);
                        row.AddPseudoState("expanded");
                    }
                    else
                    {
                        row.RemovePseudoState("expanded");
                    }
                }
            }
        }
        finally
        {
            Console.Write("\x1b[2J\x1b[H\x1b[?1049l"); // clear + leave the alternate screen
            try { Console.CursorVisible = true; } catch { /* unsupported host */ }
        }

        return 0;
    }

    private static void MoveFocus(List<StyledNode> rowNodes, ref int focus, int delta)
    {
        rowNodes[focus].RemovePseudoState("focused");
        focus = Math.Clamp(focus + delta, 0, rowNodes.Count - 1);
        rowNodes[focus].AddPseudoState("focused");
    }

    /// <summary>Build the collapsed <c>Surface → Row*</c> tree (no Detail) — used by the cmdlet's headless summary.</summary>
    internal static StyledNode BuildSurface(IReadOnlyList<PSObject> rows, string classProperty, string[]? property)
    {
        var surface = new StyledNode("Surface");
        foreach (var row in rows)
        {
            var node = new StyledNode(KindOf(row), id: null, classes: ClassesOf(row, classProperty)) { Source = row };
            node.SetAttribute("Name", Summary(row, property));
            surface.Add(node);
        }

        return surface;
    }

    /// <summary>The <c>Detail</c> container for a row: one <c>DetailLine</c> leaf per shown property.</summary>
    private static StyledNode BuildDetail(PSObject? source, string[]? property, string classProperty)
    {
        var detail = new StyledNode("Detail");
        foreach (var name in DetailProperties(source, property, classProperty))
        {
            var line = new StyledNode("DetailLine", classes: new[] { "key" });
            line.SetAttribute("Name", $"  {name}: {CellText(source, name)}");
            detail.Add(line);
        }

        return detail;
    }

    /// <summary>Text for a node, by kind: Row summary / DetailLine "key: value"; containers render no text.</summary>
    public static string NodeText(ITreeNode node)
    {
        if (node.Kind is "Surface" or "Detail")
        {
            return string.Empty;
        }

        return node.TryGetAttribute("Name", out var v) ? v?.ToString() ?? string.Empty : string.Empty;
    }

    /// <summary>Render a Spectre renderable to an ANSI frame at the current console width (honors NO_COLOR).</summary>
    private static string RenderToAnsi(IRenderable renderable)
    {
        var writer = new StringWriter();
        var noColor = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = noColor ? AnsiSupport.No : AnsiSupport.Yes,
            ColorSystem = noColor ? ColorSystemSupport.NoColors : ColorSystemSupport.Standard,
            Out = new AnsiConsoleOutput(writer),
        });

        try
        {
            var width = Console.WindowWidth;
            if (width > 0)
            {
                console.Profile.Width = width;
            }
        }
        catch { /* width unknown — let Spectre pick a default */ }

        console.Write(renderable);
        return writer.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>Resolve a style name / inline CSS / .css path to CSS text (plain File.Exists — no PowerShell provider).</summary>
    public static string ResolveCss(string nameOrInline)
    {
        if (nameOrInline.Contains('{') || nameOrInline.Contains('\n'))
        {
            return nameOrInline;
        }

        if (File.Exists(nameOrInline))
        {
            return BashFileSystem.ReadAllTextRaw(nameOrInline);
        }

        return StyledStyles.Resolve(nameOrInline);
    }

    private static string Summary(PSObject row, string[]? property)
    {
        if (property is { Length: > 0 })
        {
            return string.Join("  ", property.Select(p => CellText(row, p)));
        }

        var name = CellText(row, "Name");
        return string.IsNullOrEmpty(name) ? (row.BaseObject?.ToString() ?? row.ToString()) : name;
    }

    private static IEnumerable<string> DetailProperties(PSObject? row, string[]? property, string classProperty)
    {
        if (property is { Length: > 0 })
        {
            return property;
        }

        if (row is null)
        {
            return Array.Empty<string>();
        }

        return row.Properties
            .Where(p => p.IsGettable && !string.Equals(p.Name, classProperty, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>The Strata Kind of a row: its first type name with the namespace stripped (e.g. System.Diagnostics.Process → Process).</summary>
    public static string KindOf(PSObject row)
    {
        var type = row.TypeNames.Count > 0 ? row.TypeNames[0] : string.Empty;
        var dot = type.LastIndexOf('.');
        return dot >= 0 ? type[(dot + 1)..] : type;
    }

    private static IEnumerable<string> ClassesOf(PSObject row, string classProperty)
    {
        try
        {
            var raw = row.Properties[classProperty]?.Value?.ToString();
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
}
