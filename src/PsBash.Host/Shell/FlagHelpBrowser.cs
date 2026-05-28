using System.Text;

namespace PsBash.Host.Shell;

/// <summary>
/// A scrollable, alt-screen "man page" browser for a command's flags/parameters (P4). Shows one
/// option's detail at a time (head, description, long detail, examples); ↑↓ switches option, PgUp/
/// PgDn (or j/k) scrolls the detail, Enter returns the option's bare flag to insert, Esc/q closes.
/// Reached both from a dedicated key (F1) and by pressing → on a focused inline-panel row.
/// </summary>
/// <remarks>
/// Modeled on <see cref="CtrlRSearch"/>'s alt-screen lifecycle. The layout/format logic
/// (<see cref="DetailLines"/>, <see cref="WrapText"/>) is pure and unit-tested; the interactive
/// loop has a <see cref="Simulate"/> seam so navigation is testable without a real terminal.
/// </remarks>
internal sealed class FlagHelpBrowser
{
    private const string EnterAltScreen = "\x1b[?1049h";
    private const string ExitAltScreen = "\x1b[?1049l";
    private const string ClearScreen = "\x1b[2J";
    private const string Home = "\x1b[H";
    private const string HideCursor = "\x1b[?25l";
    private const string ShowCursor = "\x1b[?25h";
    private const string ClearLine = "\x1b[2K";
    private const string Gray = "\x1b[90m";
    private const string Bold = "\x1b[1m";
    private const string Reset = "\x1b[0m";

    private readonly string _title;
    private readonly IReadOnlyList<FlagHint> _hints;
    private int _selected;
    private int _detailScroll;
    private int _termWidth = 80;
    private int _termHeight = 24;

    public enum Result { Cancelled, Insert }

    public FlagHelpBrowser(string title, IReadOnlyList<FlagHint> hints, int initialSelected = 0)
    {
        _title = title;
        _hints = hints;
        _selected = hints.Count == 0 ? 0 : Math.Clamp(initialSelected, 0, hints.Count - 1);
    }

    /// <summary>Run the browser interactively. Returns the flag to insert, or null if cancelled.</summary>
    public (Result Result, string? Insert) Run()
    {
        if (_hints.Count == 0 || Console.IsInputRedirected)
            return (Result.Cancelled, null);

        UpdateSize();
        if (_termHeight < 6)
            return (Result.Cancelled, null);

        Console.Write(EnterAltScreen);
        Console.Write(HideCursor);
        try
        {
            Render();
            while (true)
            {
                ConsoleKeyInfo key;
                try { key = Console.ReadKey(intercept: true); }
                catch (InvalidOperationException) { return (Result.Cancelled, null); }

                var outcome = HandleKey(key);
                if (outcome is not null)
                    return outcome.Value;
                Render();
            }
        }
        finally
        {
            Console.Write(ShowCursor);
            Console.Write(ExitAltScreen);
        }
    }

    // ── input handling (shared by Run and Simulate) ───────────────────────────

    private (Result, string?)? HandleKey(ConsoleKeyInfo key)
    {
        int detailRows = Math.Max(1, _termHeight - 3); // rows available for the detail body
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                if (_selected > 0) { _selected--; _detailScroll = 0; }
                return null;
            case ConsoleKey.DownArrow:
                if (_selected < _hints.Count - 1) { _selected++; _detailScroll = 0; }
                return null;
            case ConsoleKey.PageDown:
                _detailScroll += detailRows;
                ClampDetailScroll(detailRows);
                return null;
            case ConsoleKey.PageUp:
                _detailScroll = Math.Max(0, _detailScroll - detailRows);
                return null;
            case ConsoleKey.J when key.Modifiers == 0:
                _detailScroll += 1;
                ClampDetailScroll(detailRows);
                return null;
            case ConsoleKey.K when key.Modifiers == 0:
                _detailScroll = Math.Max(0, _detailScroll - 1);
                return null;
            case ConsoleKey.Enter:
                return (Result.Insert, _hints[_selected].Insert);
            case ConsoleKey.Escape:
            case ConsoleKey.Q:
                return (Result.Cancelled, null);
            default:
                return null;
        }
    }

    private void ClampDetailScroll(int detailRows)
    {
        int totalDetail = DetailLines(_hints[_selected], DetailWidth()).Count;
        int max = Math.Max(0, totalDetail - detailRows);
        if (_detailScroll > max) _detailScroll = max;
        if (_detailScroll < 0) _detailScroll = 0;
    }

    private int DetailWidth() => Math.Max(20, _termWidth - 2);

    // ── rendering ──────────────────────────────────────────────────────────────

    private void Render()
    {
        var sb = new StringBuilder();
        sb.Append(Home).Append(ClearScreen).Append(Home);

        var h = _hints[_selected];
        // Header: title + position + key legend.
        var header = $"{_title}   [{_selected + 1}/{_hints.Count}]   ↑↓ option · PgUp/PgDn scroll · Enter insert · Esc close";
        sb.Append(Gray).Append(Truncate(header, _termWidth)).Append(Reset).Append("\r\n");
        sb.Append("\r\n");

        int detailRows = Math.Max(1, _termHeight - 3);
        var lines = DetailLines(h, DetailWidth());
        for (int row = 0; row < detailRows; row++)
        {
            int idx = _detailScroll + row;
            sb.Append(ClearLine);
            if (idx < lines.Count)
                sb.Append(Truncate(lines[idx], _termWidth));
            if (row < detailRows - 1)
                sb.Append("\r\n");
        }
        Console.Write(sb.ToString());
    }

    private void UpdateSize()
    {
        try { _termWidth = Math.Max(20, Console.WindowWidth); } catch { _termWidth = 80; }
        try { _termHeight = Math.Max(6, Console.WindowHeight); } catch { _termHeight = 24; }
    }

    // ── pure formatting (unit-tested) ──────────────────────────────────────────

    /// <summary>
    /// The man-page body for one hint, wrapped to <paramref name="width"/>: head, description,
    /// the long detail paragraph, then an Examples block. Pure — no console, no escapes.
    /// </summary>
    internal static IReadOnlyList<string> DetailLines(FlagHint h, int width)
    {
        var lines = new List<string> { h.Head };
        if (!string.IsNullOrEmpty(h.Desc))
            lines.Add(h.Desc);
        if (!string.IsNullOrEmpty(h.Detail))
        {
            lines.Add(string.Empty);
            lines.AddRange(WrapText(h.Detail!, width));
        }
        if (h.Examples is { Count: > 0 })
        {
            lines.Add(string.Empty);
            lines.Add("Examples:");
            foreach (var ex in h.Examples)
                lines.Add("  " + ex);
        }
        return lines;
    }

    /// <summary>Greedy word-wrap to <paramref name="width"/> columns (min 1). Pure.</summary>
    internal static IReadOnlyList<string> WrapText(string text, int width)
    {
        width = Math.Max(1, width);
        var outLines = new List<string>();
        foreach (var para in text.Replace("\r\n", "\n").Split('\n'))
        {
            var words = para.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) { outLines.Add(string.Empty); continue; }
            var cur = new StringBuilder();
            foreach (var w in words)
            {
                if (cur.Length == 0) cur.Append(w);
                else if (cur.Length + 1 + w.Length <= width) cur.Append(' ').Append(w);
                else { outLines.Add(cur.ToString()); cur.Clear(); cur.Append(w); }
            }
            if (cur.Length > 0) outLines.Add(cur.ToString());
        }
        return outLines;
    }

    private static string Truncate(string s, int width)
        => s.Length <= width ? s : s[..Math.Max(0, width)];

    // ── test seam ──────────────────────────────────────────────────────────────

    /// <summary>Drive the browser's key handling without a terminal (tests). Uses a fixed size.</summary>
    internal (Result Result, string? Insert) Simulate(Queue<ConsoleKeyInfo> keys, int width = 80, int height = 24)
    {
        _termWidth = width;
        _termHeight = height;
        while (keys.Count > 0)
        {
            var outcome = HandleKey(keys.Dequeue());
            if (outcome is not null)
                return outcome.Value;
        }
        return (Result.Cancelled, null);
    }

    internal int SelectedIndex => _selected;
    internal int DetailScroll => _detailScroll;
}
