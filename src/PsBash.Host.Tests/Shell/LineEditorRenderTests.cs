using System.Text;
using PsBash.Host.Shell;
using Xunit;

namespace PsBash.Host.Tests.Shell;

/// <summary>
/// Deterministic validation for the multi-row line-editor rendering (wrap + wide
/// characters). Rather than a live PTY, these drive <see cref="LineEditor.ComputeRender"/>
/// — the pure escape-sequence builder — and replay its output through a tiny terminal-grid
/// simulator (<see cref="TermSim"/>) that models the SAME auto-wrap semantics the renderer
/// assumes. The assertions are on the resulting visible grid + cursor cell, which is the
/// property that actually matters and the one the old code got wrong.
/// </summary>
public class LineEditorRenderTests
{
    // ── DisplayWidth ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("你好", 4)]              // 2 CJK glyphs × 2 cols
    [InlineData("a你b", 4)]             // 1 + 2 + 1
    [InlineData("😀", 2)]               // emoji, 2 cols (surrogate pair, 1 glyph)
    [InlineData("é", 1)]          // e + combining acute = 1 col
    public void DisplayWidth_CountsColumnsNotCodeUnits(string s, int expected)
        => Assert.Equal(expected, LineEditor.DisplayWidth(s));

    // ── the reported bug: typing past the wrap must NOT add a row per character ──

    [Fact]
    public void TypingPastWrap_DoesNotAddARowPerCharacter()
    {
        const int cols = 10;
        var sim = new TermSim(cols);
        const string prompt = "> ";            // 2 columns
        int prevCursorRow = 0;

        // Simulate the key loop: append one char at a time and redraw, threading the
        // previous cursor row into the next erase (exactly what LineEditor does).
        var text = new StringBuilder();
        for (int i = 0; i < 15; i++)           // 2 + 15 = 17 cols -> exactly 2 rows in width 10
        {
            text.Append('a');
            var r = LineEditor.ComputeRender(
                prompt, LineEditor.DisplayWidth(prompt), text.ToString(), text.Length,
                ghost: null, panel: [], cols: cols, prevCursorRow: prevCursorRow);
            sim.Feed(r.Sequence);
            prevCursorRow = r.CursorRow;
        }

        // The whole line occupies exactly 2 rows — NOT 15. (The bug re-rendered one row
        // lower per keystroke, ballooning the grid.)
        Assert.Equal(2, sim.NonEmptyRowCount);
        Assert.Equal("> " + new string('a', 15), sim.VisibleText);
        // Cursor sits just after the last character: col 17 -> row 1, col 7.
        Assert.Equal((1, 7), sim.Cursor);
    }

    [Fact]
    public void WideCharacters_WrapAtColumnBoundary_CursorLandsCorrectly()
    {
        const int cols = 8;
        var sim = new TermSim(cols);
        const string prompt = "";
        // 5 CJK glyphs = 10 columns -> wraps at 8: row0 holds 4 glyphs (8 cols), row1 holds 1.
        var r = LineEditor.ComputeRender(
            prompt, 0, "你好世界地", cursor: 5, ghost: null, panel: [],
            cols: cols, prevCursorRow: 0);
        sim.Feed(r.Sequence);

        Assert.Equal(2, sim.NonEmptyRowCount);
        Assert.Equal("你好世界地", sim.VisibleText);
        // Cursor after the 5th glyph: 10 cols -> row 1, col 2.
        Assert.Equal((1, 2), sim.Cursor);
    }

    [Fact]
    public void CursorInMiddleOfWrappedLine_PlacedOnCorrectRowAndColumn()
    {
        const int cols = 10;
        var sim = new TermSim(cols);
        const string prompt = "> ";
        var text = new string('x', 20);        // 2 + 20 = 22 cols -> 3 rows
        var r = LineEditor.ComputeRender(
            prompt, 2, text, cursor: 3, ghost: null, panel: [], cols: cols, prevCursorRow: 0);
        sim.Feed(r.Sequence);

        // cursorW = 2 + 3 = 5 -> row 0, col 5.
        Assert.Equal((0, 5), sim.Cursor);
        Assert.Equal("> " + text, sim.VisibleText);
    }

    [Fact]
    public void ExactWidthFill_DoesNotDesyncCursor()
    {
        const int cols = 10;
        var sim = new TermSim(cols);
        const string prompt = "";
        var text = new string('y', 10);        // exactly fills one row
        var r = LineEditor.ComputeRender(
            prompt, 0, text, cursor: 10, ghost: null, panel: [], cols: cols, prevCursorRow: 0);
        sim.Feed(r.Sequence);

        Assert.Equal(text, sim.VisibleText);
        // At an exact fill the cursor moves to the start of the next row.
        Assert.Equal((1, 0), sim.Cursor);
    }

    [Fact]
    public void ShrinkingRegion_ErasesTheRowsBelow()
    {
        // Render a wrapped (2-row) line, then a short (1-row) line reusing the geometry.
        // The old cursor-relative erase left the second row behind; the fix erases it.
        const int cols = 10;
        var sim = new TermSim(cols);
        const string prompt = "> ";

        var r1 = LineEditor.ComputeRender(prompt, 2, new string('a', 15), 15, null, [], cols, 0);
        sim.Feed(r1.Sequence);
        Assert.Equal(2, sim.NonEmptyRowCount);

        var r2 = LineEditor.ComputeRender(prompt, 2, "ab", 2, null, [], cols, r1.CursorRow);
        sim.Feed(r2.Sequence);

        Assert.Equal(1, sim.NonEmptyRowCount);
        Assert.Equal("> ab", sim.VisibleText);
        Assert.Equal((0, 4), sim.Cursor);
    }

    // ── minimal terminal-grid simulator ─────────────────────────────────────────

    /// <summary>
    /// Interprets the subset of terminal control sequences the renderer emits, using
    /// pending-wrap auto-wrap semantics (wrap when the next glyph would not fit). Wide
    /// glyphs occupy two cells (the trailing cell is a spacer). SGR sequences are ignored.
    /// </summary>
    private sealed class TermSim
    {
        private readonly int _cols;
        private readonly List<List<string>> _rows = [[]];
        private int _cr;   // cursor row
        private int _cc;   // cursor col (0.._cols)

        public TermSim(int cols) => _cols = Math.Max(1, cols);

        public (int Row, int Col) Cursor => (_cr, _cc);

        public int NonEmptyRowCount
        {
            get
            {
                int last = -1;
                for (int r = 0; r < _rows.Count; r++)
                    if (_rows[r].Any(cell => cell.Length > 0 && cell != "\0")) last = r;
                return last + 1;
            }
        }

        /// <summary>All rendered glyphs, rows concatenated (spacers/blank cells dropped).</summary>
        public string VisibleText
        {
            get
            {
                var sb = new StringBuilder();
                foreach (var row in _rows)
                    foreach (var cell in row)
                        if (cell.Length > 0 && cell != "\0") sb.Append(cell);
                return sb.ToString();
            }
        }

        public void Feed(string s)
        {
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\x1b') { i = ApplyEsc(s, i); continue; }
                if (c == '\r') { _cc = 0; i++; continue; }
                if (c == '\n') { _cr++; EnsureRow(_cr); _cc = 0; i++; continue; }
                i = PutRune(s, i);
            }
        }

        private int PutRune(string s, int i)
        {
            int consumed = char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? 2 : 1;
            string glyph = s.Substring(i, consumed);
            int w = LineEditor.DisplayWidth(glyph);
            if (w == 0)
            {
                // combining mark: attach to previous cell, no advance
                return i + consumed;
            }
            if (_cc + w > _cols) { _cr++; EnsureRow(_cr); _cc = 0; }
            EnsureRow(_cr);
            SetCell(_cr, _cc, glyph);
            if (w == 2) SetCell(_cr, _cc + 1, "\0");   // spacer for the wide cell
            _cc += w;
            return i + consumed;
        }

        private int ApplyEsc(string s, int i)
        {
            // Expect CSI: ESC '[' params final
            if (i + 1 >= s.Length || s[i + 1] != '[') return i + 1;
            int j = i + 2;
            int numStart = j;
            while (j < s.Length && (char.IsDigit(s[j]) || s[j] == ';')) j++;
            if (j >= s.Length) return j;
            char final = s[j];
            string paramStr = s.Substring(numStart, j - numStart);
            int n = int.TryParse(paramStr.Split(';')[0], out var v) ? v : 0;
            switch (final)
            {
                case 'A': _cr = Math.Max(0, _cr - Math.Max(1, n)); break;      // up
                case 'B': _cr += Math.Max(1, n); EnsureRow(_cr); break;         // down
                case 'C': _cc = Math.Min(_cols, _cc + Math.Max(1, n)); break;   // right
                case 'D': _cc = Math.Max(0, _cc - Math.Max(1, n)); break;       // left
                case 'J':
                    if (n == 0)   // erase cursor -> end of screen
                    {
                        EnsureRow(_cr);
                        var row = _rows[_cr];
                        if (_cc < row.Count) row.RemoveRange(_cc, row.Count - _cc);
                        if (_cr + 1 < _rows.Count) _rows.RemoveRange(_cr + 1, _rows.Count - (_cr + 1));
                    }
                    break;
                case 'K':
                    if (n == 0 || n == 2) { EnsureRow(_cr); _rows[_cr] = []; }   // clear line
                    break;
                case 'm': break;   // SGR — ignore
            }
            return j + 1;
        }

        private void EnsureRow(int r) { while (_rows.Count <= r) _rows.Add([]); }

        private void SetCell(int r, int col, string glyph)
        {
            EnsureRow(r);
            var row = _rows[r];
            while (row.Count <= col) row.Add("");
            row[col] = glyph;
        }
    }
}
