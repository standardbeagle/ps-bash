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
///   <code>Get-Process | Format-Styled procs.pcss -Property Name,CPU</code>
/// </example>
/// </remarks>
[Cmdlet(VerbsCommon.Format, "Styled")]
[OutputType(typeof(string))]
public sealed class FormatStyledCommand : PSCmdlet
{
    /// <summary>
    /// The stylesheet to apply. Accepts inline CSS text, a path to a <c>.pcss</c>/<c>.css</c> file,
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

        // Curated per-type view: when the rows are a recognised output type (FileSystemInfo,
        // ps-bash ls/find/du/ps/stat, or any BashText text-line type) and the user did not drive
        // columns with -Property, replace the dump-every-property grid with a compact, human-oriented
        // table (or the plain text lines) coloured by the type's auto-sheet. See TryBuildCuratedView.
        if (TryBuildCuratedView(out var curatedRows, out var curatedColumns, out var curatedSheet, out var textOnly))
        {
            if (textOnly)
            {
                // A text-line type (grep/cat/tree/wc/…): the BashText string IS the record. Emit the
                // lines, not a one-column grid — matches the un-styled default and avoids the huge
                // every-property table the user hit piping `ls`/`grep`/… into Format-Styled.
                foreach (var row in curatedRows)
                {
                    WriteObject(PropStr(row, "BashText"));
                }
                return;
            }

            _rows.Clear();
            _rows.AddRange(curatedRows);
            Property = curatedColumns;
            if (string.IsNullOrEmpty(Css))
            {
                Css = curatedSheet; // apply the type's colours (the user gave no sheet).
            }
            RenderGridMode(asTable: true);
            return;
        }

        // Auto-sheet for the generic grid: colour a recognised uniform kind (Process, Git*, ErrorRecord,
        // ping/trace) with its family stylesheet even when the user named no sheet. Unknown kinds keep
        // the neutral `default` palette, so this never recolours arbitrary objects.
        if (string.IsNullOrEmpty(Css))
        {
            var kind = UniformKind();
            var auto = kind is null ? null : StyledStyles.AutoStyleForKind(kind);
            if (auto is not null && auto != "object")
            {
                Css = auto;
            }
        }

        // Mode selection. Explicit -Table / -List win; otherwise auto-select the way PowerShell's
        // own default formatting does — a single object renders as a property list, multiple
        // objects as a table. This makes the no-flag default a real layout (columns / aligned
        // key-value) instead of space-joined values.
        bool asTable = Table.IsPresent || (!List.IsPresent && _rows.Count > 1);
        RenderGridMode(asTable);
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

            // A column is numeric when every non-empty value parses as a number; such columns
            // (and their headers) are right-aligned so digits line up by place value.
            var numeric = new bool[props.Length];
            for (var c = 0; c < props.Length; c++)
            {
                numeric[c] = IsNumericColumn(props[c]);
            }

            // Header row: synthetic Kind so user/semantic Kind rules don't match it; numeric
            // headers carry `num` so the column (its first cell) right-aligns.
            for (var c = 0; c < props.Length; c++)
            {
                cells.Add(MakeCell(props[c], "StrataHeader", numeric[c] ? "header num" : "header"));
            }

            // Data rows: each value cell keeps the SOURCE object's Kind + class so semantic
            // stylesheet rules (Process { … }, .busy { … }) still colour it, layered with the
            // structural classes (cell / primary / num).
            foreach (var row in _rows)
            {
                var kind = KindOf(row);
                var rowClass = ClassOf(row);
                for (var c = 0; c < props.Length; c++)
                {
                    cells.Add(MakeCell(CellText(row, props[c]), kind,
                        CellClass(rowClass, "cell", isPrimary: c == 0, isNumeric: numeric[c])));
                }
            }
        }
        else
        {
            columns = 2;
            foreach (var row in _rows)
            {
                var kind = KindOf(row);
                var rowClass = ClassOf(row);
                foreach (var p in props)
                {
                    cells.Add(MakeCell(p, "StrataName", "property-name"));
                    cells.Add(MakeCell(CellText(row, p), kind,
                        CellClass(rowClass, "property-value", isPrimary: false, isNumeric: false)));
                }
            }
        }

        // CSS cascade (later wins): the injected grid rule, then the SEMANTIC sheet (user -Css,
        // else the built-in `default` palette) which colours data cells by Kind/class, then the
        // structural sheet (table/list) which only sets structure — header weight/rule, primary
        // weight, numeric alignment, key colour — and never colour on data cells, so semantic
        // colours are not clobbered by the higher-specificity structural classes.
        var gridRule = $"StrataRoot {{ display: grid; grid-template-columns: {string.Join(" ", Enumerable.Repeat("auto", columns))} }}";
        var semanticCss = string.IsNullOrEmpty(Css) ? ResolveStylesheet("default") : ResolveCss(Css);
        var structuralCss = ResolveStylesheet(asTable ? "table" : "list");
        var css = $"{gridRule}\n{semanticCss}\n{structuralCss}";

        var registry = StylingProperties.CreateRegistry();
        // The grid path declares layout properties (display, grid-template-columns); register
        // their descriptors too or the parser rejects them as unknown.
        LayoutProperties.RegisterAll(registry);
        // The button/expansion stylesheets (fs, procsvc, object, error) declare the `command:`
        // interaction property. Register its descriptor so they parse here too; the bindings are
        // inert under the static Spectre projection (no input loop) but must not be a parse error.
        Strata.Interaction.InteractionProperties.RegisterAll(registry);
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

    /// <summary>
    /// Properties to render: the explicit <see cref="Property"/> list; else the first row's
    /// <c>DefaultDisplayPropertySet</c> (the same curated column set PowerShell's own Format-Table
    /// uses, so a typed object — <c>PsBash.GitCommit</c>, <c>FileInfo</c>, … — shows its sensible
    /// columns instead of every property); else all gettable properties.
    /// </summary>
    private string[] ResolveProperties()
    {
        if (Property is { Length: > 0 })
        {
            return Property;
        }

        var defaults = DefaultDisplayProperties(_rows[0]);
        if (defaults is { Length: > 0 })
        {
            return defaults
                .Where(n => !string.Equals(n, ClassProperty, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(n, CellTextProperty, StringComparison.Ordinal))
                .ToArray();
        }

        return _rows[0].Properties
            .Where(p => p.IsGettable
                && !string.Equals(p.Name, ClassProperty, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(p.Name, CellTextProperty, StringComparison.Ordinal))
            .Select(p => p.Name)
            .ToArray();
    }

    /// <summary>The object's <c>PSStandardMembers.DefaultDisplayPropertySet</c> column names, or null when it declares none.</summary>
    private static string[]? DefaultDisplayProperties(PSObject row)
    {
        try
        {
            if (row.Members["PSStandardMembers"] is PSMemberSet standard
                && standard.Members["DefaultDisplayPropertySet"] is PSPropertySet set
                && set.ReferencedPropertyNames.Count > 0)
            {
                return set.ReferencedPropertyNames.ToArray();
            }
        }
        catch
        {
            // Malformed / inaccessible member set — fall back to all properties.
        }

        return null;
    }

    // ── Curated per-type views (cross-indexed by object type) ─────────────────────────────────
    //
    // Format-Styled recognises the common output object types from both PowerShell and ps-bash and
    // replaces the dump-every-property grid with a compact, human-oriented table (or, for the many
    // text-line types, the plain lines). Adding a type is one `case` in the switch below plus a pure
    // Project* row-builder — all unit-testable without a runspace.
    //
    //   FileSystemInfo / LsEntry / StatEntry  → fs view  (tag+name, size+meter, modified)   sheet fs
    //   FindEntry                             → fs view over the match path                  sheet fs
    //   DuEntry                               → size + path                                  sheet object
    //   PsEntry                               → PID / User / %CPU / MEM / Command            sheet ps
    //   any *.BashText text-line type         → the BashText lines (grep/cat/tree/wc/…)      (no grid)
    //   everything else (Process/Git/…)       → generic grid, auto-coloured by AutoStyleForKind

    private static readonly string[] FsColumns = { "Name", "Size", "Modified" };

    /// <summary>
    /// Project the accumulated rows into a curated view when one applies (no explicit
    /// <see cref="Property"/>, and the effective sheet permits it). On success sets
    /// <paramref name="rows"/>/<paramref name="columns"/> to the curated table and
    /// <paramref name="sheet"/> to the type's auto-sheet; <paramref name="textOnly"/> signals a
    /// BashText-line type that should render as plain lines rather than a grid. Returns false to fall
    /// through to the generic property grid.
    /// </summary>
    private bool TryBuildCuratedView(out List<PSObject> rows, out string[] columns, out string sheet, out bool textOnly)
    {
        rows = new List<PSObject>();
        columns = Array.Empty<string>();
        sheet = "object";
        textOnly = false;

        if (Property is { Length: > 0 })
        {
            return false; // explicit columns: the user is driving — don't override.
        }

        // (1) Get-ChildItem output: every row is a live FileSystemInfo (kind may mix File/Directory).
        var infos = new List<FileSystemInfo>(_rows.Count);
        bool allFsi = _rows.Count > 0;
        foreach (var row in _rows)
        {
            if (row.BaseObject is FileSystemInfo fsi) { infos.Add(fsi); }
            else { allFsi = false; break; }
        }
        if (allFsi)
        {
            if (!SheetAllows("fs")) { return false; }
            long max = 0;
            foreach (var i in infos) { if (i is FileInfo f) { long s; try { s = f.Length; } catch { s = 0; } max = Math.Max(max, s); } }
            foreach (var i in infos) { rows.Add(BuildFsRow(i, max)); }
            columns = FsColumns; sheet = "fs"; return true;
        }

        // (2) ps-bash typed output: dispatch by the row's uniform Strata kind.
        var kind = UniformKind();
        if (kind is null)
        {
            return false; // mixed kinds → generic grid.
        }

        switch (kind)
        {
            case "LsEntry":
            case "StatEntry":
                if (!SheetAllows("fs")) { return false; }
                { long max = MaxSizeBytes(); foreach (var r in _rows) { rows.Add(ProjectFsLike(r, max)); } }
                columns = FsColumns; sheet = "fs"; return true;

            case "FindEntry":
                if (!SheetAllows("fs")) { return false; }
                { long max = MaxSizeBytes(); foreach (var r in _rows) { rows.Add(ProjectFindEntry(r, max)); } }
                columns = FsColumns; sheet = "fs"; return true;

            case "DuEntry":
                if (!SheetAllows("object")) { return false; }
                foreach (var r in _rows) { rows.Add(ProjectDuEntry(r)); }
                columns = new[] { "Size", "Path" }; sheet = "object"; return true;

            case "PsEntry":
                if (!SheetAllows("ps")) { return false; }
                foreach (var r in _rows) { rows.Add(ProjectPsEntry(r)); }
                columns = new[] { "PID", "User", "%CPU", "MEM", "Command" }; sheet = "ps"; return true;

            default:
                // Text-line types (grep/rg/cat/tree/wc/env/date/…): a single BashText string is the
                // whole record. Collapse to plain lines instead of a one-column property grid.
                // Only when the user did not name a sheet (which would have nothing to colour anyway).
                if (string.IsNullOrEmpty(Css) && AllHaveBashText())
                {
                    rows.AddRange(_rows);
                    textOnly = true; return true;
                }
                return false;
        }
    }

    /// <summary>The shared Strata kind of every row, or null when the rows are of mixed kinds.</summary>
    private string? UniformKind()
    {
        string? kind = null;
        foreach (var row in _rows)
        {
            var k = KindOf(row);
            if (kind is null) { kind = k; }
            else if (!string.Equals(kind, k, StringComparison.Ordinal)) { return null; }
        }
        return kind;
    }

    /// <summary>True when the caller gave no sheet or named exactly <paramref name="name"/> — so a curated view for that sheet applies.</summary>
    private bool SheetAllows(string name)
        => string.IsNullOrEmpty(Css) || Css!.Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when every row exposes a gettable <c>BashText</c> property (a text-line type).</summary>
    private bool AllHaveBashText()
    {
        foreach (var row in _rows)
        {
            var p = row.Properties["BashText"];
            if (p is null || !p.IsGettable) { return false; }
        }
        return true;
    }

    /// <summary>Largest <c>SizeBytes</c> across the rows (for the size meter's log scale); 0 when none.</summary>
    private long MaxSizeBytes()
    {
        long max = 0;
        foreach (var row in _rows)
        {
            if (!PropBool(row, "IsDirectory")) { max = Math.Max(max, PropLong(row, "SizeBytes")); }
        }
        return max;
    }

    // ── property readers (null / type-mismatch safe) ──────────────────────────────────────────
    private static string PropStr(PSObject row, string name)
    {
        try { return row.Properties[name]?.Value?.ToString() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static long PropLong(PSObject row, string name)
    {
        try
        {
            var v = row.Properties[name]?.Value;
            return v is null ? 0L : Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch { return 0L; }
    }

    private static bool PropBool(PSObject row, string name)
    {
        try { return row.Properties[name]?.Value is bool b && b; }
        catch { return false; }
    }

    private static DateTime PropDate(PSObject row, string name)
    {
        try { return row.Properties[name]?.Value is DateTime d ? d : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    // ── row projectors (pure; unit-tested) ────────────────────────────────────────────────────

    /// <summary>Build the fs-view row for a live <see cref="FileSystemInfo"/> (Get-ChildItem path).</summary>
    private static PSObject BuildFsRow(FileSystemInfo info, long max)
    {
        bool isDir = info is DirectoryInfo;
        bool isLink = (info.Attributes & FileAttributes.ReparsePoint) != 0;
        bool hidden = (info.Attributes & FileAttributes.Hidden) != 0;
        long size = 0;
        if (info is FileInfo fi) { try { size = fi.Length; } catch { size = 0; } }
        return BuildFsRow(info.Name, isDir, isLink, size, info.LastWriteTime, hidden, max);
    }

    /// <summary>Project a ps-bash <c>LsEntry</c>/<c>StatEntry</c> PSObject into the fs view via its typed properties.</summary>
    internal static PSObject ProjectFsLike(PSObject row, long max)
        => BuildFsRow(PropStr(row, "Name"), PropBool(row, "IsDirectory"), PropBool(row, "IsSymlink"),
            PropLong(row, "SizeBytes"), PropDate(row, "LastModified"), hidden: false, max);

    /// <summary>Project a ps-bash <c>FindEntry</c> into the fs view keyed on the match <c>Path</c>.</summary>
    internal static PSObject ProjectFindEntry(PSObject row, long max)
        => BuildFsRow(PropStr(row, "Path"), PropBool(row, "IsDirectory"), isLink: false,
            PropLong(row, "SizeBytes"), PropDate(row, "LastModified"), hidden: false, max);

    /// <summary>Project a ps-bash <c>DuEntry</c> into a right-aligned human size + path row (the <c>total</c> line gets a class).</summary>
    internal static PSObject ProjectDuEntry(PSObject row)
    {
        bool isTotal = PropBool(row, "IsTotal");
        var o = new PSObject();
        o.TypeNames.Insert(0, "DuEntry");
        o.Properties.Add(new PSNoteProperty("Size", $"{PropStr(row, "SizeHuman"),8}"));
        o.Properties.Add(new PSNoteProperty("Path", PropStr(row, "Path")));
        o.Properties.Add(new PSNoteProperty("class", isTotal ? "total" : string.Empty));
        return o;
    }

    /// <summary>Project a ps-bash <c>PsEntry</c> into the compact process view; <c>busy</c> class when CPU &gt; 50 so the ps sheet can highlight it.</summary>
    internal static PSObject ProjectPsEntry(PSObject row)
    {
        var cpu = PropStr(row, "CPU");
        bool busy = double.TryParse(cpu, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var c) && c > 50;
        var o = new PSObject();
        // Kind "Process" so the ps stylesheet (which targets `Process`) colours these rows.
        o.TypeNames.Insert(0, "Process");
        o.Properties.Add(new PSNoteProperty("PID", PropStr(row, "PID")));
        o.Properties.Add(new PSNoteProperty("User", PropStr(row, "User")));
        o.Properties.Add(new PSNoteProperty("%CPU", cpu));
        o.Properties.Add(new PSNoteProperty("MEM", PropStr(row, "MemoryMB")));
        o.Properties.Add(new PSNoteProperty("Command", PropStr(row, "Command")));
        o.Properties.Add(new PSNoteProperty("class", busy ? "busy" : string.Empty));
        return o;
    }

    /// <summary>
    /// Build one fs-view row from primitives: <c>Name</c> (type tag + name, dirs keep a trailing
    /// <c>/</c>), <c>Size</c> (human size + a log-scaled ▃▁ meter — filled thickness keyed to the byte
    /// range, dim baseline for the empty track — or <c>—</c> for a dir), and
    /// <c>Modified</c> (relative time), plus the file-type <c>class</c> the fs sheet colours by.
    /// Shared by the FileSystemInfo, LsEntry/StatEntry, and FindEntry paths.
    /// </summary>
    internal static PSObject BuildFsRow(string name, bool isDir, bool isLink, long sizeBytes, DateTime modified, bool hidden, long max)
    {
        var (tag, cls) = ClassifyFsByExt(isDir, isLink, GetExtension(name));
        // `hidden` is appended LAST so the fs sheet's `.hidden` rule wins over the type colour.
        var classList = hidden ? cls + " hidden" : cls;

        // A fixed-width lowercase type tag (colour conveys the same bucket via the fs sheet) is the
        // terminal-oriented replacement for the old type emoji: meaningful, ASCII, and calm.
        string display = isDir ? $"{tag}  {name}/" : $"{tag}  {name}";
        string sizeCell = isDir ? $"{"—",6}" : $"{HumanSize(sizeBytes),6}  {SizeBar(sizeBytes, max)}";

        var o = new PSObject();
        o.TypeNames.Insert(0, isDir ? "DirectoryInfo" : "FileInfo");
        o.Properties.Add(new PSNoteProperty("Name", display));
        o.Properties.Add(new PSNoteProperty("Size", sizeCell));
        o.Properties.Add(new PSNoteProperty("Modified", HumanTime(modified)));
        o.Properties.Add(new PSNoteProperty("class", classList));
        return o;
    }

    /// <summary>Lowercase extension without the dot, or empty; tolerant of a full path or bare name.</summary>
    private static string GetExtension(string name)
    {
        var ext = Path.GetExtension(name);
        return string.IsNullOrEmpty(ext) ? string.Empty : ext.TrimStart('.').ToLowerInvariant();
    }

    /// <summary>Classify a live <see cref="FileSystemInfo"/> into a (display tag, fs-sheet class) pair.</summary>
    internal static (string Tag, string Class) ClassifyFs(FileSystemInfo info)
        => ClassifyFsByExt(info is DirectoryInfo, (info.Attributes & FileAttributes.ReparsePoint) != 0,
            info.Extension.TrimStart('.').ToLowerInvariant());

    /// <summary>
    /// Classify a filesystem entry into a (display tag, fs-sheet class) pair from primitives.
    /// The tag is a fixed-width lowercase ASCII mnemonic (<c>dir</c> / <c>src</c> / <c>img</c> …) —
    /// a terminal-oriented, colour-reinforced marker in place of a type emoji.
    /// </summary>
    internal static (string Tag, string Class) ClassifyFsByExt(bool isDir, bool isLink, string extension)
    {
        if (isDir)
        {
            return ("dir", "dir");
        }

        if (isLink)
        {
            return ("lnk", "symlink");
        }

        var ext = extension.TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "png" or "jpg" or "jpeg" or "gif" or "bmp" or "svg" or "webp" or "ico" or "tif" or "tiff" or "heic" or "avif"
                => ("img", "image"),
            "mp4" or "mkv" or "mov" or "avi" or "webm" or "wmv" or "flv" or "m4v" or "mpg" or "mpeg"
                => ("vid", "video"),
            "mp3" or "wav" or "flac" or "ogg" or "m4a" or "aac" or "wma" or "opus"
                => ("aud", "audio"),
            "zip" or "tar" or "gz" or "tgz" or "bz2" or "bz" or "xz" or "7z" or "rar" or "zst" or "lz" or "lzma"
                => ("arc", "archive"),
            "exe" or "dll" or "msi" or "com" or "sys" or "so" or "dylib"
                => ("exe", "app"),
            "sh" or "bash" or "zsh" or "ps1" or "psm1" or "cmd" or "bat"
                => ("scr", "script"),
            "cs" or "c" or "cpp" or "cc" or "h" or "hpp" or "js" or "mjs" or "cjs" or "ts" or "tsx" or "jsx"
                or "py" or "rb" or "go" or "rs" or "java" or "kt" or "swift" or "php" or "scala" or "lua"
                or "pl" or "r" or "dart" or "vue" or "svelte"
                => ("src", "code"),
            "pdf" or "doc" or "docx" or "odt" or "rtf" or "ppt" or "pptx" or "xls" or "xlsx" or "ods" or "csv" or "tsv" or "md"
                => ("doc", "doc"),
            "json" or "yaml" or "yml" or "xml" or "toml" or "ini" or "cfg" or "conf" or "sql" or "db" or "sqlite" or "parquet"
                => ("dat", "data"),
            _ => ("txt", "text"),
        };
    }

    /// <summary>
    /// Bucket a byte count into its binary-unit exponent (0=B, 1=K, 2=M, 3=G, 4=T, 5=P, capped at
    /// P) and the value scaled into that unit. The single source of the 1024-power bucketing that
    /// both <see cref="HumanSize"/> and <see cref="SizeExponent"/> consume, so they can never drift.
    /// Cap is keyed to <see cref="FilledByExponent"/> so <see cref="SizeExponent"/> stays a valid
    /// index into it.
    /// </summary>
    private static (int Exp, double Scaled) SizeBucket(long bytes)
    {
        double value = bytes;
        int exp = 0;
        while (value >= 1024 && exp < FilledByExponent.Length - 1)
        {
            value /= 1024;
            exp++;
        }

        return (exp, value);
    }

    /// <summary>Human-readable byte size: <c>0B</c>, <c>820B</c>, <c>1.4K</c>, <c>340M</c>, <c>2.1G</c> (binary units).</summary>
    internal static string HumanSize(long bytes)
    {
        var (exp, value) = SizeBucket(bytes);
        if (exp == 0)
        {
            return bytes + "B";
        }

        string[] units = { "K", "M", "G", "T", "P" };
        var num = value < 10
            ? value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        return num + units[exp - 1];
    }

    /// <summary>
    /// The filled bar glyph per binary-unit exponent (0=B … 5=P): a rising lower-block whose
    /// height (visual "thickness") grows with the size's magnitude, so a GB file's bar reads as
    /// heavier than a KB file's at a glance — the range is legible from the glyph weight alone.
    /// </summary>
    private static readonly char[] FilledByExponent = { '▃', '▄', '▅', '▆', '▇', '█' };

    /// <summary>The empty-track glyph: a thin dim baseline sliver, far lighter than any filled glyph so the filled/empty split reads as high vs low intensity (not the old ▰/▱ near-tie).</summary>
    private const char EmptyGlyph = '▁';

    /// <summary>Binary-unit exponent of a byte count: 0=B, 1=K, 2=M, 3=G, 4=T, 5=P — the same bucketing <see cref="HumanSize"/> uses.</summary>
    internal static int SizeExponent(long bytes) => SizeBucket(bytes).Exp;

    /// <summary>
    /// A fixed-width unicode meter, log-scaled to <paramref name="max"/>. The empty track is a dim
    /// baseline sliver (<c>▁</c>); the filled run uses a rising lower-block whose thickness is keyed
    /// to <paramref name="bytes"/>' magnitude (B/K/M/G/T/P), so both the fill fraction AND the byte
    /// range are visible in one compact, monochrome mark.
    /// </summary>
    internal static string SizeBar(long bytes, long max, int width = 6)
    {
        char fill = FilledByExponent[SizeExponent(Math.Max(bytes, 0))];
        if (bytes <= 0 || max <= 0)
        {
            return new string(EmptyGlyph, width);
        }

        double frac = Math.Log(bytes + 1) / Math.Log(max + 1);
        int filled = Math.Clamp((int)Math.Round(frac * width), 0, width);
        return new string(fill, filled) + new string(EmptyGlyph, width - filled);
    }

    /// <summary>Relative modified time: <c>just now</c>, <c>5m ago</c>, <c>3h ago</c>, <c>2d ago</c>, then <c>MMM d</c> / <c>MMM d yyyy</c>.</summary>
    internal static string HumanTime(DateTime when)
    {
        var now = DateTime.Now;
        var delta = now - when;
        if (delta.TotalSeconds < 0)
        {
            // Future timestamp (clock skew / freshly touched): show the absolute date, no "ago".
            return when.Year == now.Year
                ? when.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture)
                : when.ToString("MMM d yyyy", System.Globalization.CultureInfo.InvariantCulture);
        }
        if (delta.TotalMinutes < 1)
        {
            return "just now";
        }
        if (delta.TotalMinutes < 60)
        {
            return $"{(int)delta.TotalMinutes}m ago";
        }
        if (delta.TotalHours < 24)
        {
            return $"{(int)delta.TotalHours}h ago";
        }
        if (delta.TotalDays < 7)
        {
            return $"{(int)delta.TotalDays}d ago";
        }
        return when.Year == now.Year
            ? when.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture)
            : when.ToString("MMM d yyyy", System.Globalization.CultureInfo.InvariantCulture);
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

    /// <summary>The Strata Kind of a source row: its first type name with the namespace stripped (matches the adapter's Kind derivation), so semantic <c>Process { … }</c>-style rules match data cells.</summary>
    private static string KindOf(PSObject row)
    {
        var type = row.TypeNames.Count > 0 ? row.TypeNames[0] : string.Empty;
        var dot = type.LastIndexOf('.');
        return dot >= 0 ? type[(dot + 1)..] : type;
    }

    /// <summary>The source row's class label(s) from the <see cref="ClassProperty"/> property, or empty.</summary>
    private string ClassOf(PSObject row)
    {
        try
        {
            return row.Properties[ClassProperty]?.Value?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Compose a data cell's class list: the source row's class(es) first (so semantic rules win), then the structural markers.</summary>
    private static string CellClass(string rowClass, string structural, bool isPrimary, bool isNumeric)
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrEmpty(rowClass))
        {
            parts.Add(rowClass);
        }

        parts.Add(structural);
        if (isPrimary)
        {
            parts.Add("primary");
        }

        if (isNumeric)
        {
            parts.Add("num");
        }

        return string.Join(" ", parts);
    }

    /// <summary>A column is numeric when it has at least one value and every non-empty value parses as a number (invariant culture).</summary>
    private bool IsNumericColumn(string property)
    {
        var any = false;
        foreach (var row in _rows)
        {
            var text = CellText(row, property);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            any = true;
            if (!double.TryParse(text, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                return false;
            }
        }

        return any;
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
                return BashFileSystem.ReadAllTextRaw(resolved);
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
    /// Resolve a stylesheet <paramref name="name"/> (e.g. <c>default</c>, <c>ls</c>, <c>ps</c>)
    /// to CSS text via the shared <see cref="StyledStyles.Resolve"/> (built-in + user override
    /// cascade). Throws <see cref="ItemNotFoundException"/> when neither exists.
    /// </summary>
    private static string ResolveStylesheet(string name) => StyledStyles.Resolve(name);

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
