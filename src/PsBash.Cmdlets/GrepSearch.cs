using System.Text;
using System.Text.RegularExpressions;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>GNU grep <c>--binary-files=TYPE</c> (<c>-a</c> = text, <c>-I</c> = without-match).</summary>
internal enum GrepBinaryMode { Binary, Text, WithoutMatch }

/// <summary>GNU grep <c>-d ACTION</c> / <c>--directories</c>; <c>-r</c> is <see cref="Recurse"/>.</summary>
internal enum GrepDirectories { Read, Skip, Recurse }

/// <summary>GNU grep <c>-D ACTION</c> / <c>--devices</c>.</summary>
internal enum GrepDevices { Read, Skip }

/// <summary>GNU grep <c>--color[=WHEN]</c>: <c>auto</c> colours only when the output is a terminal.</summary>
internal enum GrepColorMode { Never, Always, Auto }

/// <summary>
/// Everything the grep scanner needs to know about the resolved command line (pure data so the
/// scanner can be unit-tested without a cmdlet).
/// </summary>
internal sealed class GrepOptions
{
    public bool Invert, LineNumbers, Count, Quiet, FilesWith, FilesWithout, OnlyMatching;
    public bool NullData, NullAfterName, ByteOffset, InitialTab, ContextRequested;
    public int Max = int.MaxValue, After, Before;
    public GrepBinaryMode Binary = GrepBinaryMode.Binary;
    /// <summary>Group separator line between non-adjacent context groups; <c>null</c> = none.</summary>
    public string? GroupSeparator = "--";
    public GrepStyle Style = GrepStyle.Plain;

    public bool ListMode => FilesWith || FilesWithout;
}

/// <summary>
/// GNU grep's colour model: <c>GREP_COLORS</c> capabilities (<c>ms mc sl cx fn ln bn se rv ne</c>,
/// <c>mt</c> = ms+mc) with the 3.11 defaults, and the SGR sequences it wraps around each piece
/// (<c>ESC[..m ESC[K</c> start, <c>ESC[m ESC[K</c> end; <c>ne</c> drops the <c>ESC[K</c>).
/// </summary>
internal sealed class GrepStyle
{
    public bool Enabled;
    public string Ms = "01;31", Mc = "01;31", Sl = "", Cx = "", Fn = "35", Ln = "32", Bn = "32", Se = "36";
    public bool Rv, Ne;

    public static readonly GrepStyle Plain = new();

    public string Start(string sgr) => Ne ? "\u001b[" + sgr + "m" : "\u001b[" + sgr + "m\u001b[K";

    public string End => Ne ? "\u001b[m" : "\u001b[m\u001b[K";

    /// <summary><c>pr_sgr_start_if</c> + text + <c>pr_sgr_end_if</c>: nothing when colour is off or the capability is empty.</summary>
    public string Wrap(string sgr, string text)
        => Enabled && sgr.Length > 0 ? Start(sgr) + text + End : text;

    /// <summary>
    /// Is stdout "a terminal"? The host's stdout is always a pipe, so the signal is the launcher's PTY
    /// hand-off (<c>PSBASH_PTY_ATTACHED</c>, same as rg's); <c>PSBASH_GREP_TTY=1/0</c> overrides it. GNU also
    /// requires <c>TERM</c> set and not <c>dumb</c>.
    /// </summary>
    internal static bool StdoutIsTerminal(Func<string, string?> env)
    {
        bool tty;
        var o = env("PSBASH_GREP_TTY")?.Trim();
        if (o is { Length: > 0 }) tty = o is "1" or "true" or "yes" or "on";
        else tty = env("PSBASH_PTY_ATTACHED")?.Trim() is "1" or "true" or "yes" or "on";
        var term = env("TERM");
        return tty && !string.IsNullOrEmpty(term) && term != "dumb";
    }

    public static GrepStyle Create(GrepColorMode mode, Func<string, string?> env, Action<string> warn)
    {
        bool on = mode == GrepColorMode.Always || (mode == GrepColorMode.Auto && StdoutIsTerminal(env));
        if (!on) return Plain;

        var s = new GrepStyle { Enabled = true };

        // Deprecated GREP_COLOR (digits and ';' only) sets the match colour first; GREP_COLORS wins.
        string? user = env("GREP_COLOR");
        bool userValid = !string.IsNullOrEmpty(user) && user.All(c => c == ';' || (c >= '0' && c <= '9'));
        if (userValid) { s.Ms = s.Mc = user!; }

        s.ParseGrepColors(env("GREP_COLORS"));

        if (userValid && (s.Ms == user || s.Mc == user))
            warn($"grep: warning: GREP_COLOR='{user}' is deprecated; use GREP_COLORS='mt={user}'");
        return s;
    }

    private void ParseGrepColors(string? spec)
    {
        if (string.IsNullOrEmpty(spec)) return;
        foreach (var item in spec.Split(':'))
        {
            if (item.Length == 0) continue;
            int eq = item.IndexOf('=');
            string name = eq < 0 ? item : item[..eq];
            string? val = eq < 0 ? null : item[(eq + 1)..];
            if (val != null && !val.All(c => c == ';' || (c >= '0' && c <= '9')))
                return; // GNU stops at the first malformed value
            switch (name)
            {
                case "mt" when val != null: Ms = Mc = val; break;
                case "ms" when val != null: Ms = val; break;
                case "mc" when val != null: Mc = val; break;
                case "sl" when val != null: Sl = val; break;
                case "cx" when val != null: Cx = val; break;
                case "fn" when val != null: Fn = val; break;
                case "ln" when val != null: Ln = val; break;
                case "bn" when val != null: Bn = val; break;
                case "se" when val != null: Se = val; break;
                case "rv" when val == null: Rv = true; break;
                case "ne" when val == null: Ne = true; break;
            }
        }
    }
}

/// <summary>
/// grep's per-source search engine (GNU grep 3.11 semantics): ONE push state machine used for stdin and
/// for every file, so the output decorations (file name, line number, byte offset, <c>-T</c> tab, <c>-Z</c>
/// NUL, colours, <c>-o</c>, context + group separators, <c>-z</c> records) and the binary-file rules cannot
/// drift between the two paths. Retained state is the <c>-B</c> ring and the <c>-A</c> countdown, never the
/// stream length.
/// </summary>
internal sealed class GrepScanner
{
    /// <summary>(text, exact, fileLabel, lineNumber, rawLine, originalRecord): <c>exact</c> text carries its own terminator.</summary>
    internal delegate void EmitFn(string text, bool exact, string? label, int lineNo, string? line, object? original);

    private readonly GrepOptions _o;
    private readonly List<Regex> _rx;
    private readonly GrepStyle _st;
    private readonly EmitFn _emit;
    private readonly Action<string> _stderr;

    // ---- global (across sources)
    /// <summary>A line was selected somewhere (exit status 0).</summary>
    public bool AnyMatch;
    /// <summary><c>-q</c> found its match: stop everything.</summary>
    public bool QuitAll;
    private bool _used;

    // ---- per source
    private string _label = "";
    private bool _showName;
    private int _lineNum, _count, _afterLeft, _lastOut, _width;
    private long _offset;
    private bool _done, _binary, _binaryHit, _limitReached, _typed;
    private Queue<(int Li, string Text, long Off, int Term)>? _ring;

    public GrepScanner(GrepOptions o, List<Regex> regexes, EmitFn emit, Action<string> stderr)
    {
        _o = o; _rx = regexes; _st = o.Style; _emit = emit; _stderr = stderr;
    }

    /// <summary>The current source needs no more input.</summary>
    public bool SourceDone => _done;

    /// <summary>Selected lines in the current source.</summary>
    public int Count => _count;

    /// <param name="typed">A file source: its lines are typed <c>PsBash.GrepMatch</c> objects (stdin lines are plain text).</param>
    public void Begin(string label, bool showName, bool startBinary, long sizeHint, bool typed = false)
    {
        _label = label; _showName = showName; _typed = typed;
        _lineNum = 0; _count = 0; _afterLeft = 0; _lastOut = -1; _offset = 0;
        _done = false; _binary = false; _binaryHit = false; _limitReached = false;
        _ring = null;
        if (_o.ContextRequested && _o.Before > 0 && !_o.Count && !_o.ListMode && !_o.Quiet)
            _ring = new Queue<(int, string, long, int)>(Math.Min(_o.Before, 1024));
        if (_o.InitialTab)
        {
            // GNU: the number columns are as wide as the file's size (+1 for line numbers), a pipe counts as INTMAX.
            ulong num = sizeHint >= 0 ? (ulong)sizeHint : long.MaxValue;
            if (sizeHint >= 0 && _o.LineNumbers) num++;
            _width = 0;
            do _width++; while ((num /= 10) != 0);
        }
        if (startBinary && _o.Binary != GrepBinaryMode.Text && !_o.NullData)
        {
            _binary = true;
            if (_o.Binary == GrepBinaryMode.WithoutMatch) _done = true;
        }
        if (_o.Max <= 0) _done = true;
    }

    /// <summary>Feed one record (a line, or a NUL-terminated record under <c>-z</c>). Returns false when the source is satisfied.</summary>
    public bool Feed(string text, int termBytes, object? original)
    {
        if (_done) return false;
        int li = ++_lineNum;
        long off = _offset;
        if (_o.ByteOffset) _offset += RawBytes.GetByteCount(text) + termBytes;

        if (!_binary && _o.Binary != GrepBinaryMode.Text && !_o.NullData && text.AsSpan().IndexOf('\0') >= 0)
        {
            _binary = true; // a NUL beyond the probe window: binary from here on (GNU switches per buffer)
            if (_o.Binary == GrepBinaryMode.WithoutMatch) { _done = true; return false; }
        }

        bool selected = IsSelected(text);

        if (_limitReached)
        {
            // -m N reached: only the trailing -A window remains, printed as context even when it matches.
            if (_afterLeft > 0) { PrintLine(li, text, off, termBytes, selected: false, original); _afterLeft--; }
            if (_afterLeft <= 0) { _done = true; return false; }
            return true;
        }

        if (selected)
        {
            _count++;
            AnyMatch = true;
            if (_o.Quiet) { QuitAll = true; _done = true; return false; }
            if (_o.ListMode) { _done = true; return false; }
            if (_o.Count)
            {
                if (_count >= _o.Max) { _done = true; return false; }
                return true;
            }
            if (_binary && _o.Binary == GrepBinaryMode.Binary) { _binaryHit = true; _done = true; return false; }

            if (_ring is { Count: > 0 })
            {
                foreach (var (rli, rt, ro, rterm) in _ring) PrintLine(rli, rt, ro, rterm, selected: false, null);
                _ring.Clear();
            }
            PrintLine(li, text, off, termBytes, selected: true, original);
            _afterLeft = _o.After;
            if (_count >= _o.Max)
            {
                if (_afterLeft > 0) { _limitReached = true; return true; }
                _done = true;
                return false;
            }
            return true;
        }

        if (_o.Count || _o.ListMode || _o.Quiet) return true;
        if (_binary && _o.Binary == GrepBinaryMode.Binary) return true; // nothing is printed from a binary file
        if (_afterLeft > 0) { PrintLine(li, text, off, termBytes, selected: false, original); _afterLeft--; }
        else if (_ring is not null)
        {
            _ring.Enqueue((li, text, off, termBytes));
            if (_ring.Count > _o.Before) _ring.Dequeue();
        }
        return true;
    }

    /// <summary>Finish the current source: binary-match notice, then <c>-c</c> / <c>-l</c> / <c>-L</c> output.</summary>
    public void End()
    {
        bool quiet = _o.Quiet || _o.Count || _o.ListMode;
        if (!quiet && _o.Binary == GrepBinaryMode.Binary && _binaryHit)
            _stderr($"grep: {_label}: binary file matches");

        if (_o.Quiet) return;
        if (_o.ListMode)
        {
            bool list = _o.FilesWith ? _count > 0 : _count == 0;
            if (list)
            {
                string name = _st.Wrap(_st.Fn, _label);
                if (_o.NullAfterName) _emit(name + "\0", true, null, 0, null, null);
                else _emit(name, false, null, 0, null, null);
            }
            return;
        }
        if (_o.Count)
        {
            var sb = new StringBuilder();
            if (_showName)
            {
                sb.Append(_st.Wrap(_st.Fn, _label));
                sb.Append(_o.NullAfterName ? "\0" : _st.Wrap(_st.Se, ":"));
            }
            sb.Append(_count);
            _emit(sb.ToString(), false, null, 0, null, null);
        }
    }

    private bool IsSelected(string text)
    {
        bool matched = false;
        foreach (var rx in _rx)
            if (rx.IsMatch(text)) { matched = true; break; }
        return _o.Invert ? !matched : matched;
    }

    // ---- output ---------------------------------------------------------------------------------

    private void PrintLine(int li, string text, long off, int termBytes, bool selected, object? original)
    {
        char sep = selected ? ':' : '-';
        if (_o.OnlyMatching)
        {
            // -o prints only the matching parts of lines that are "matching" (selected, unless -v);
            // every other line prints nothing but still counts as output for the group-separator rule.
            bool matching = selected ^ _o.Invert;
            if (matching) PrintOnlyMatching(li, text, off, selected);
            else MarkPrinted(li, groupStart: true);
            return;
        }

        MarkPrinted(li, groupStart: true);

        var sb = new StringBuilder();
        sb.Append(Head(li, off, sep, text.Length));
        string head = sb.ToString();
        string body = Body(text, selected);
        string full = head + body;
        if (_o.NullData) _emit(full + "\0", true, null, 0, null, null);
        else _emit(full, false, _typed ? _label : null, li, text, full == text ? original : null);
    }

    private void PrintOnlyMatching(int li, string text, long off, bool selected)
    {
        var spans = AllMatchSpans(_rx, text);
        char sep = _o.Invert ? '-' : ':';
        bool any = false;
        foreach (var (idx, len) in spans)
        {
            string piece = text.Substring(idx, len);
            if (!any) { MarkPrinted(li, groupStart: true); any = true; }
            long pieceOff = _o.ByteOffset ? off + RawBytes.GetByteCount(text.AsSpan(0, idx)) : 0;
            string full = Head(li, pieceOff, sep, len) + _st.Wrap(selected ? _st.Ms : _st.Mc, piece);
            if (_o.NullData) _emit(full + "\0", true, null, 0, null, null);
            else _emit(full, false, _typed ? _label : null, li, text, null);
        }
        if (!any) MarkPrinted(li, groupStart: false);
    }

    /// <summary>Group separator (before a line not adjacent to the previous output) and the last-output bookkeeping.</summary>
    private void MarkPrinted(int li, bool groupStart)
    {
        if (groupStart && _o.ContextRequested && _used && _o.GroupSeparator != null && li != _lastOut + 1)
        {
            string sepLine = _st.Wrap(_st.Se, _o.GroupSeparator);
            if (_o.NullData) _emit(sepLine + "\n", true, null, 0, null, null);
            else _emit(sepLine, false, null, 0, null, null);
        }
        _lastOut = li;
        _used = true;
    }

    private string Head(int li, long off, char sep, int len)
    {
        var sb = new StringBuilder();
        string sepText = _st.Wrap(_st.Se, sep.ToString());
        if (_showName)
        {
            sb.Append(_st.Wrap(_st.Fn, _label));
            sb.Append(_o.NullAfterName ? "\0" : sepText);
        }
        if (_o.LineNumbers)
        {
            sb.Append(_st.Wrap(_st.Ln, Pad(li)));
            sb.Append(sepText);
        }
        if (_o.ByteOffset)
        {
            sb.Append(_st.Wrap(_st.Bn, Pad(off)));
            sb.Append(sepText);
        }
        if (_o.InitialTab && (_showName || _o.LineNumbers || _o.ByteOffset) && len > 0)
            sb.Append('\t');
        return sb.ToString();
    }

    private string Pad(long v) => _width > 0 ? v.ToString().PadLeft(_width) : v.ToString();

    /// <summary>GNU <c>prline</c>: line / match colours (sl, cx, ms, mc, rv) around the text.</summary>
    private string Body(string text, bool selected)
    {
        if (!_st.Enabled) return text;
        string lineColor = (selected ^ (_o.Invert && _st.Rv)) ? _st.Sl : _st.Cx;
        string matchColor = selected ? _st.Ms : _st.Mc;
        if (lineColor.Length == 0 && matchColor.Length == 0) return text;

        bool matching = selected ^ _o.Invert;
        var sb = new StringBuilder();
        int cur = 0;
        if (matching && matchColor.Length > 0)
        {
            foreach (var (idx, len) in AllMatchSpans(_rx, text))
            {
                if (lineColor.Length > 0) sb.Append(_st.Start(lineColor));
                sb.Append(text, cur, idx - cur);
                sb.Append(_st.Start(matchColor)).Append(text, idx, len).Append(_st.End);
                cur = idx + len;
            }
        }
        if (lineColor.Length > 0)
        {
            int end = text.Length;
            string eol = "";
            if (end > cur && text[end - 1] == '\r') { end--; eol = "\r"; }
            if (end > cur)
            {
                sb.Append(_st.Start(lineColor)).Append(text, cur, end - cur).Append(_st.End);
                cur = end;
            }
            sb.Append(eol);
            cur += eol.Length;
        }
        if (cur < text.Length) sb.Append(text, cur, text.Length - cur);
        return sb.ToString();
    }

    // ---- shared helpers -------------------------------------------------------------------------

    /// <summary>
    /// All non-overlapping, non-empty match spans on a line in left-to-right order. Gathers matches from
    /// every pattern (multiple <c>-e</c>), orders by start index (longest wins a tie) and drops any that
    /// overlap an already-reported match — GNU advances past each reported match.
    /// </summary>
    internal static List<(int Index, int Length)> AllMatchSpans(List<Regex> regexes, string text)
    {
        var found = new List<(int Index, int Length)>();
        foreach (var rx in regexes)
            foreach (Match m in rx.Matches(text))
                if (m.Length > 0) found.Add((m.Index, m.Length));
        found.Sort((a, b) => a.Index != b.Index ? a.Index - b.Index : b.Length - a.Length);

        var result = new List<(int, int)>(found.Count);
        int consumedTo = -1;
        foreach (var f in found)
        {
            if (f.Index <= consumedTo) continue;
            result.Add(f);
            consumedTo = f.Index + f.Length - 1;
        }
        return result;
    }
}

/// <summary>File-level helpers for grep: binary probe and NUL-record reading.</summary>
internal static class GrepIo
{
    /// <summary>GNU reads the first 96 KiB buffer and calls the file binary when it holds a NUL.</summary>
    internal const int ProbeBytes = 96 * 1024;

    internal static bool ProbeNul(string path)
    {
        using var fs = BashFileSystem.OpenRead(path);
        var buf = new byte[Math.Min(ProbeBytes, 16 * 1024)];
        int total = 0, n;
        while (total < ProbeBytes && (n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            if (Array.IndexOf(buf, (byte)0, 0, n) >= 0) return true;
            total += n;
        }
        return false;
    }

    /// <summary>
    /// The records of a file: lines (CRLF normalised unless <paramref name="exact"/>) or, for <c>-z</c>,
    /// NUL-terminated records read byte-exactly. <c>Term</c> is the terminator's byte length (0 for an
    /// unterminated final record).
    /// </summary>
    internal static IEnumerable<(string Text, int Term)> ReadRecords(string path, bool nullData, bool exact)
    {
        if (!nullData)
        {
            foreach (var l in BashFileSystem.ReadTextLines(path, skipBinary: false, exact: exact))
                yield return (l.Text, l.HasTrailingNewline ? 1 : 0);
            yield break;
        }

        using var fs = BashFileSystem.OpenRead(path);
        using var reader = BashFileSystem.OpenRawReader(fs, leaveOpen: true);
        var sb = new StringBuilder(256);
        var buf = new char[16384];
        int read;
        while ((read = reader.Read(buf, 0, buf.Length)) > 0)
        {
            int start = 0;
            for (int i = 0; i < read; i++)
            {
                if (buf[i] != '\0') continue;
                sb.Append(buf, start, i - start);
                yield return (sb.ToString(), 1);
                sb.Clear();
                start = i + 1;
            }
            if (start < read) sb.Append(buf, start, read - start);
        }
        if (sb.Length > 0) yield return (sb.ToString(), 0);
    }
}

/// <summary>
/// Turns stdin records (the objects of a PowerShell pipeline) into the scanner's records: a record that
/// spans several lines is split on <c>\n</c>; under <c>-z</c> the whole record stream is re-cut on NUL.
/// </summary>
internal sealed class GrepStdinFeeder
{
    private readonly GrepScanner _scanner;
    private readonly bool _nullData;
    private readonly StringBuilder _pending = new();

    public GrepStdinFeeder(GrepScanner scanner, bool nullData) { _scanner = scanner; _nullData = nullData; }

    /// <summary>Returns false once the scanner needs no more input.</summary>
    public bool Add(string recordText, bool unterminated, object? original)
    {
        if (!_nullData)
        {
            string trimmed = recordText.TrimEnd('\n');
            if (trimmed.Contains('\n'))
            {
                var pieces = trimmed.Split('\n');
                for (int i = 0; i < pieces.Length; i++)
                    if (!_scanner.Feed(pieces[i], unterminated && i == pieces.Length - 1 ? 0 : 1, null)) return false;
                return true;
            }
            return _scanner.Feed(trimmed, unterminated ? 0 : 1, original);
        }

        // -z: the byte stream is the records' text, each terminated by \n unless unterminated.
        string t = recordText.EndsWith('\n') && !unterminated ? recordText : recordText + (unterminated ? "" : "\n");
        int s = 0;
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i] != '\0') continue;
            _pending.Append(t, s, i - s);
            string rec = _pending.ToString();
            _pending.Clear();
            s = i + 1;
            if (!_scanner.Feed(rec, 1, null)) return false;
        }
        if (s < t.Length) _pending.Append(t, s, t.Length - s);
        return true;
    }

    /// <summary>End of input: the unterminated tail is the last record.</summary>
    public void Flush()
    {
        if (_nullData && _pending.Length > 0)
        {
            string rec = _pending.ToString();
            _pending.Clear();
            _scanner.Feed(rec, 0, null);
        }
    }
}
