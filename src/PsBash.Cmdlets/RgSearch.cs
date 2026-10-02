using System.Text;
using System.Text.RegularExpressions;
using PsBash;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>One output record of an rg run: the text plus what the cmdlet needs to build its typed object.</summary>
internal readonly record struct RgEmit(string Text, string? File = null, int LineNumber = 0, string? Line = null,
    object? Original = null, bool Unterminated = false);

/// <summary>Everything the searcher needs, resolved from the argv by the cmdlet.</summary>
internal sealed class RgSettings
{
    internal Regex Regex = null!;
    internal bool Invert, Multiline, OnlyMatching, Quiet, Json, Passthru, Trim, Null, ByteOffset, Column, Vimgrep;
    internal bool CountOnly, CountMatches, FilesWith, FilesWithout;
    internal bool LineNumbers, UseHeading, ShowPath, Stats;
    internal string? Replace;
    internal int Before, After;
    internal int MaxCount = int.MaxValue;
    internal RgColors? Colors;

    internal bool NeedSpans => OnlyMatching || Vimgrep || Column || Json || Stats || Replace is not null || CountMatches
        || (Colors is { } c && !c.Match.IsNone);
}

/// <summary>Run-wide counters behind <c>--stats</c> and the JSON summary.</summary>
internal sealed class RgStats
{
    internal long Matches, MatchedLines, FilesWithMatch, Searches, BytesPrinted, BytesSearched;
}

/// <summary>One searchable input: a file (already decoded into lines) or the pipeline.</summary>
internal sealed class RgSource
{
    internal string Display = "";
    internal List<string> Lines = new();
    internal bool FinalNewline = true;
    internal List<object?>? Items;
    internal long ByteSize;
    internal bool IsStdin;

    /// <summary>Offset of the first NUL byte when an explicitly named binary file is searched (ripgrep prints a message instead of lines).</summary>
    internal long BinaryOffset = -1;
}

/// <summary>
/// The rg search + print engine: finds the matching lines of one <see cref="RgSource"/> (per line, or across lines with
/// <c>-U</c>), plans context, and prints in ripgrep's formats — plain, coloured (termcolor-exact), <c>-o</c>,
/// <c>--replace</c>, <c>--column</c>/<c>--vimgrep</c>, <c>-b</c>, <c>-0</c>, <c>--passthru</c>, <c>-c</c>/<c>-l</c>,
/// <c>--json</c>. Output goes out through the <c>emit</c> callback so the cmdlet decides the record type.
/// </summary>
internal sealed class RgSearcher
{
    private readonly RgSettings _s;
    private readonly Action<RgEmit> _emit;
    private readonly RgStats _stats;

    private bool _printedAny, _anyHeading;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    internal RgSearcher(RgSettings settings, Action<RgEmit> emit, RgStats stats)
    {
        _s = settings;
        _emit = emit;
        _stats = stats;
    }

    private sealed class Hit
    {
        internal int First, Last;
        internal List<Span> Spans = new();
    }

    private readonly record struct Span(int Start, int Length, Match M);

    private readonly record struct PlanLine(int Line, int Last, bool IsMatch, Hit? Hit);

    private void Out(RgEmit e, bool counted = true)
    {
        if (counted) _stats.BytesPrinted += RawBytes.GetByteCount(e.Text) + (e.Unterminated ? 0 : 1);
        _emit(e);
    }

    private string PaintPath(string p) => _s.Colors is { } c ? c.Paint(c.Path, p) : p;

    // ------------------------------------------------------------------ search

    /// <summary>Search one source; true when it had a selected line.</summary>
    internal bool SearchOne(RgSource src)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _stats.Searches++;
        _stats.BytesSearched += src.ByteSize;

        var hits = FindHits(src);
        bool matched = hits.Count > 0;
        long fileLines = 0, fileMatches = 0;
        foreach (var h in hits) { fileLines += h.Last - h.First + 1; fileMatches += h.Spans.Count; }
        if (matched)
        {
            _stats.FilesWithMatch++;
            _stats.MatchedLines += fileLines;
            _stats.Matches += fileMatches;
        }

        if (_s.Quiet) return matched;

        if (_s.FilesWithout)
        {
            if (!matched) Out(PathRecord(src), counted: false);
            return matched;
        }
        if (!matched) return false;
        if (_s.FilesWith) { Out(PathRecord(src), counted: false); return true; }
        if (_s.CountOnly || _s.CountMatches)
        {
            long n = _s.CountMatches ? fileMatches : hits.Count;
            string count = n.ToString();
            if (_s.ShowPath)
                Out(new RgEmit(PaintPath(src.Display) + (_s.Null ? "\0" : ":") + count), counted: false);
            else
                Out(new RgEmit(count), counted: false);
            return true;
        }
        if (src.BinaryOffset >= 0)
        {
            string prefix = _s.ShowPath ? src.Display + ": " : "";
            Out(new RgEmit($"{prefix}binary file matches (found \"\\0\" byte around offset {src.BinaryOffset})"));
            return true;
        }

        if (_s.Json) PrintJson(src, hits, sw, fileLines, fileMatches);
        else PrintNormal(src, hits);
        return true;
    }

    private RgEmit PathRecord(RgSource src)
    {
        string p = PaintPath(src.Display);
        return _s.Null ? new RgEmit(p + "\0", Unterminated: true) : new RgEmit(p);
    }

    private List<Hit> FindHits(RgSource src)
    {
        var hits = new List<Hit>();
        var lines = src.Lines;
        bool needSpans = _s.NeedSpans;
        int max = _s.MaxCount;
        if (max <= 0) return hits;

        if (!_s.Multiline)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                Hit? h = null;
                if (needSpans && !_s.Invert)
                {
                    var ms = _s.Regex.Matches(line);
                    if (ms.Count > 0)
                    {
                        h = new Hit { First = i, Last = i };
                        foreach (Match m in ms) h.Spans.Add(new Span(m.Index, m.Length, m));
                    }
                }
                else
                {
                    bool m = _s.Regex.IsMatch(line);
                    if (m != _s.Invert) h = new Hit { First = i, Last = i };
                }
                if (h is null) continue;
                hits.Add(h);
                if (hits.Count >= max) break;
            }
            return hits;
        }

        // -U: the regex runs over the whole text; a match selects every line it touches.
        var text = JoinedText(src);
        var starts = LineStarts(lines);
        if (!_s.Invert)
        {
            Hit? cur = null;
            foreach (Match m in _s.Regex.Matches(text))
            {
                int first = LineOf(starts, m.Index);
                int last = m.Length == 0 ? first : LineOf(starts, m.Index + m.Length - 1);
                if (cur is not null && first <= cur.Last)
                {
                    cur.Last = Math.Max(cur.Last, last);
                }
                else
                {
                    if (cur is not null) { hits.Add(cur); if (hits.Count >= max) { cur = null; break; } }
                    cur = new Hit { First = first, Last = last };
                }
                cur.Spans.Add(new Span(m.Index - starts[cur.First], m.Length, m));
            }
            if (cur is not null && hits.Count < max) hits.Add(cur);
            return hits;
        }

        var covered = new bool[lines.Count];
        foreach (Match m in _s.Regex.Matches(text))
        {
            int first = LineOf(starts, m.Index);
            int last = m.Length == 0 ? first : LineOf(starts, m.Index + m.Length - 1);
            for (int i = first; i <= last; i++) covered[i] = true;
        }
        for (int i = 0; i < lines.Count; i++)
        {
            if (covered[i]) continue;
            hits.Add(new Hit { First = i, Last = i });
            if (hits.Count >= max) break;
        }
        return hits;
    }

    private static string JoinedText(RgSource src) => string.Join("\n", src.Lines);

    private static int[] LineStarts(List<string> lines)
    {
        var starts = new int[lines.Count];
        int pos = 0;
        for (int i = 0; i < lines.Count; i++) { starts[i] = pos; pos += lines[i].Length + 1; }
        return starts;
    }

    private static int LineOf(int[] starts, int index)
    {
        int lo = 0, hi = starts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (starts[mid] <= index) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    // ------------------------------------------------------------------ context plan

    private List<PlanLine> BuildPlan(List<Hit> hits, int lineCount)
    {
        var plan = new List<PlanLine>();
        int lastPrinted = -1;
        int before = _s.Before, after = _s.After;
        for (int k = 0; k < hits.Count; k++)
        {
            var h = hits[k];
            if (k > 0 && after > 0)
            {
                int afterTo = Math.Min(hits[k - 1].Last + after, h.First - 1);
                for (int l = lastPrinted + 1; l <= afterTo; l++) plan.Add(new PlanLine(l, l, false, null));
                lastPrinted = Math.Max(lastPrinted, afterTo);
            }
            int bFrom = (int)Math.Max((long)h.First - before, lastPrinted + 1);
            for (int l = bFrom; l < h.First; l++) plan.Add(new PlanLine(l, l, false, null));
            plan.Add(new PlanLine(h.First, h.Last, true, h));
            lastPrinted = h.Last;
        }
        if (hits.Count > 0 && after > 0)
        {
            int endTo = (int)Math.Min((long)hits[^1].Last + after, lineCount - 1);
            for (int l = lastPrinted + 1; l <= endTo; l++) plan.Add(new PlanLine(l, l, false, null));
        }
        return plan;
    }

    // ------------------------------------------------------------------ normal printing

    private long[]? _offsets;
    private RgSource? _offsetsFor;

    private long[] Offsets(RgSource src)
    {
        if (_offsets is not null && ReferenceEquals(_offsetsFor, src)) return _offsets;
        var offs = new long[src.Lines.Count + 1];
        long pos = 0;
        for (int i = 0; i < src.Lines.Count; i++)
        {
            offs[i] = pos;
            pos += RawBytes.GetByteCount(src.Lines[i]) + 1;
        }
        offs[src.Lines.Count] = pos;
        _offsets = offs;
        _offsetsFor = src;
        return offs;
    }

    private void PrintNormal(RgSource src, List<Hit> hits)
    {
        bool contextOn = !_s.Passthru && (_s.Before > 0 || _s.After > 0);
        var plan = BuildPlan(hits, src.Lines.Count);
        if (plan.Count == 0) return;

        bool inlinePath = _s.ShowPath && !_s.UseHeading;
        if (_s.UseHeading)
        {
            if (_anyHeading) Out(new RgEmit(""));
            Out(new RgEmit(PaintPath(src.Display)));
            _anyHeading = true;
        }
        else if (contextOn && _printedAny)
        {
            Out(new RgEmit("--"));
        }
        _printedAny = true;

        int prev = -2;
        foreach (var pl in plan)
        {
            if (contextOn && prev >= 0 && pl.Line != prev + 1) Out(new RgEmit("--"));
            prev = pl.Last;
            if (pl.IsMatch) EmitMatch(src, pl.Hit!, inlinePath);
            else EmitContext(src, pl.Line, inlinePath);
        }
    }

    private string Prefix(RgSource src, bool inlinePath, int lineIndex, int? column, long? offset, char sep)
    {
        var sb = new StringBuilder();
        if (inlinePath)
        {
            sb.Append(PaintPath(src.Display));
            sb.Append(_s.Null ? '\0' : sep);
        }
        if (_s.LineNumbers)
        {
            string n = (lineIndex + 1).ToString();
            sb.Append(_s.Colors is { } c ? c.Paint(c.Line, n) : n).Append(sep);
        }
        if (column is { } col)
        {
            string n = col.ToString();
            sb.Append(_s.Colors is { } c ? c.Paint(c.Column, n) : n).Append(sep);
        }
        if (offset is { } off) sb.Append(off).Append(sep);
        return sb.ToString();
    }

    private bool ColorMatches => _s.Colors is { } c && !c.Match.IsNone;

    private string TrimIf(string text) => _s.Trim ? text.TrimStart(' ', '\t') : text;

    private void EmitContext(RgSource src, int line, bool inlinePath)
    {
        string raw = src.Lines[line];
        long? off = _s.ByteOffset ? Offsets(src)[line] : null;
        string prefix = Prefix(src, inlinePath, line, null, off, '-');
        string text = TrimIf(raw);
        bool plain = prefix.Length == 0 && text == raw;
        Out(new RgEmit(prefix + text, src.IsStdin ? null : src.Display, line + 1, raw,
            plain && src.Items is { } items ? items[line] : null));
    }

    private void EmitMatch(RgSource src, Hit hit, bool inlinePath)
    {
        var lines = src.Lines;
        // The text the spans index into: the matched line, or the lines of a multi-line hit joined by \n.
        string hitText = hit.First == hit.Last ? lines[hit.First] : string.Join("\n", lines.Skip(hit.First).Take(hit.Last - hit.First + 1));

        if (_s.OnlyMatching)
        {
            foreach (var sp in hit.Spans)
            {
                if (sp.Length == 0) continue;
                Locate(hitText, sp.Start, out int lineInHit, out int colChars, out int lineStartInHit);
                int lineIdx = hit.First + lineInHit;
                string lineText = LineAt(hitText, lineStartInHit);
                int? col = _s.Column || _s.Vimgrep ? 1 + RawBytes.GetByteCount(lineText.AsSpan(0, colChars)) : null;
                long? off = _s.ByteOffset ? Offsets(src)[lineIdx] + RawBytes.GetByteCount(lineText.AsSpan(0, colChars)) : null;
                string body = _s.Replace is not null ? ReplaceExpand(sp.M) : hitText.Substring(sp.Start, sp.Length);
                if (ColorMatches) body = _s.Colors!.Paint(_s.Colors.Match, body);
                Out(new RgEmit(Prefix(src, inlinePath, lineIdx, col, off, ':') + body, src.IsStdin ? null : src.Display, lineIdx + 1, lines[lineIdx]));
            }
            return;
        }

        if (_s.Vimgrep)
        {
            string rendered = TrimIf(RenderHit(hitText, hit.Spans));
            var spans = hit.Spans.Count > 0 ? hit.Spans : new List<Span> { default };
            foreach (var sp in spans)
            {
                int lineInHit = 0, colChars = 0;
                if (sp.M is not null) Locate(hitText, sp.Start, out lineInHit, out colChars, out _);
                int lineIdx = hit.First + lineInHit;
                string lineText = sp.M is null ? "" : LineAt(hitText, LineStartOf(hitText, lineInHit));
                int col = 1 + RawBytes.GetByteCount(lineText.AsSpan(0, Math.Min(colChars, lineText.Length)));
                long? off = _s.ByteOffset ? Offsets(src)[lineIdx] : null;
                var split = rendered.Split('\n');
                string one = lineInHit < split.Length ? split[lineInHit] : rendered;
                Out(new RgEmit(Prefix(src, inlinePath, lineIdx, col, off, ':') + one, src.IsStdin ? null : src.Display, lineIdx + 1, lines[lineIdx]));
            }
            return;
        }

        string renderedText = RenderHit(hitText, hit.Spans);
        var parts = hit.First == hit.Last ? new[] { renderedText } : renderedText.Split('\n');
        for (int j = 0; j < parts.Length; j++)
        {
            int lineIdx = hit.First + j;
            int? col = null;
            if (_s.Column && j == 0)
            {
                int colChars = hit.Spans.Count > 0 ? Math.Min(hit.Spans[0].Start, lines[hit.First].Length) : 0;
                col = 1 + RawBytes.GetByteCount(lines[hit.First].AsSpan(0, colChars));
            }
            else if (_s.Column) col = 1;
            long? off = _s.ByteOffset ? Offsets(src)[lineIdx] : null;
            string prefix = Prefix(src, inlinePath, lineIdx, col, off, ':');
            string text = TrimIf(parts[j]);
            bool plain = prefix.Length == 0 && text == lines[lineIdx];
            Out(new RgEmit(prefix + text, src.IsStdin ? null : src.Display, lineIdx + 1, lines[lineIdx],
                plain && src.Items is { } items ? items[lineIdx] : null));
        }
    }

    private static void Locate(string hitText, int start, out int lineInHit, out int colChars, out int lineStart)
    {
        lineInHit = 0;
        lineStart = 0;
        for (int i = 0; i < start && i < hitText.Length; i++)
            if (hitText[i] == '\n') { lineInHit++; lineStart = i + 1; }
        colChars = start - lineStart;
    }

    private static int LineStartOf(string hitText, int lineInHit)
    {
        int s = 0;
        for (int n = 0; n < lineInHit; n++) s = hitText.IndexOf('\n', s) + 1;
        return s;
    }

    private static string LineAt(string hitText, int lineStart)
    {
        int end = hitText.IndexOf('\n', lineStart);
        return end < 0 ? hitText.Substring(lineStart) : hitText.Substring(lineStart, end - lineStart);
    }

    private string RenderHit(string hitText, List<Span> spans)
    {
        bool color = ColorMatches;
        if ((_s.Replace is null && !color) || spans.Count == 0) return hitText;
        var sb = new StringBuilder(hitText.Length + 16);
        int pos = 0;
        foreach (var sp in spans)
        {
            if (sp.Start < pos) continue;
            if (sp.Length == 0 && _s.Replace is null) continue;
            sb.Append(hitText, pos, sp.Start - pos);
            string t = _s.Replace is not null ? ReplaceExpand(sp.M) : hitText.Substring(sp.Start, sp.Length);
            sb.Append(color ? _s.Colors!.Paint(_s.Colors.Match, t) : t);
            pos = sp.Start + sp.Length;
        }
        sb.Append(hitText, pos, hitText.Length - pos);
        return sb.ToString();
    }

    private string ReplaceExpand(Match m) => ExpandReplacement(_s.Replace!, m, _s.Regex);

    /// <summary>
    /// ripgrep / Rust-regex replacement syntax: <c>$name</c> (longest run of <c>[0-9A-Za-z_]</c>), <c>${name}</c>,
    /// <c>$$</c> for a literal dollar; a name that is all digits is a group number, anything else a group name; an
    /// unknown group expands to nothing (so <c>$1x</c> is the group called "1x").
    /// </summary>
    internal static string ExpandReplacement(string rep, Match m, Regex rx)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < rep.Length; i++)
        {
            char c = rep[i];
            if (c != '$' || i + 1 >= rep.Length) { sb.Append(c); continue; }
            char nx = rep[i + 1];
            if (nx == '$') { sb.Append('$'); i++; continue; }
            if (nx == '{')
            {
                int close = rep.IndexOf('}', i + 2);
                if (close > 0)
                {
                    sb.Append(GroupValue(rep.Substring(i + 2, close - i - 2), m, rx));
                    i = close;
                    continue;
                }
            }
            else if (IsNameChar(nx))
            {
                int j = i + 1;
                while (j < rep.Length && IsNameChar(rep[j])) j++;
                sb.Append(GroupValue(rep.Substring(i + 1, j - i - 1), m, rx));
                i = j - 1;
                continue;
            }
            sb.Append('$');
        }
        return sb.ToString();
    }

    private static bool IsNameChar(char c) => c == '_' || (c is >= '0' and <= '9') || (c is >= 'a' and <= 'z') || (c is >= 'A' and <= 'Z');

    private static string GroupValue(string name, Match m, Regex rx)
    {
        if (name.Length > 0 && name.All(char.IsAsciiDigit))
        {
            if (int.TryParse(name, out int idx) && idx < m.Groups.Count) return m.Groups[idx].Success ? m.Groups[idx].Value : "";
            return "";
        }
        if (rx.GroupNumberFromName(name) >= 0) { var g = m.Groups[name]; return g.Success ? g.Value : ""; }
        return "";
    }

    // ------------------------------------------------------------------ --json

    private void PrintJson(RgSource src, List<Hit> hits, System.Diagnostics.Stopwatch sw, long matchedLines, long matches)
    {
        var plan = BuildPlan(hits, src.Lines.Count);
        var offs = Offsets(src);
        long printed = 0;
        void J(string json)
        {
            printed += RawBytes.GetByteCount(json) + 1;
            _stats.BytesPrinted += RawBytes.GetByteCount(json) + 1;
            _emit(new RgEmit(json));
        }
        string pathJson = JsonText(src.IsStdin ? "<stdin>" : src.Display);
        J("{\"type\":\"begin\",\"data\":{\"path\":" + pathJson + "}}");
        foreach (var pl in plan)
        {
            int first = pl.Line, last = pl.Last;
            var sbLines = new StringBuilder();
            for (int l = first; l <= last; l++)
            {
                sbLines.Append(src.Lines[l]);
                if (l < src.Lines.Count - 1 || src.FinalNewline) sbLines.Append('\n');
            }
            string linesText = sbLines.ToString();
            var sub = new StringBuilder();
            if (pl.IsMatch)
            {
                bool firstSpan = true;
                foreach (var sp in pl.Hit!.Spans)
                {
                    if (!firstSpan) sub.Append(',');
                    firstSpan = false;
                    int bs = RawBytes.GetByteCount(linesText.AsSpan(0, sp.Start));
                    int be = bs + RawBytes.GetByteCount(linesText.AsSpan(sp.Start, sp.Length));
                    sub.Append("{\"match\":").Append(JsonText(linesText.Substring(sp.Start, sp.Length)))
                       .Append(",\"start\":").Append(bs).Append(",\"end\":").Append(be).Append('}');
                }
            }
            J("{\"type\":\"" + (pl.IsMatch ? "match" : "context") + "\",\"data\":{\"path\":" + pathJson
              + ",\"lines\":" + JsonText(linesText) + ",\"line_number\":" + (first + 1)
              + ",\"absolute_offset\":" + offs[first] + ",\"submatches\":[" + sub + "]}}");
        }
        string elapsed = Elapsed(sw.Elapsed, summary: false);
        J("{\"type\":\"end\",\"data\":{\"path\":" + pathJson + ",\"binary_offset\":null,\"stats\":{\"elapsed\":" + elapsed
          + ",\"searches\":1,\"searches_with_match\":1,\"bytes_searched\":" + src.ByteSize + ",\"bytes_printed\":" + printed
          + ",\"matched_lines\":" + matchedLines + ",\"matches\":" + matches + "}}}");
        _printedAny = true;
    }

    /// <summary>The closing <c>summary</c> message (and nothing else) for a <c>--json</c> run.</summary>
    internal void WriteJsonSummary(System.Diagnostics.Stopwatch total, TimeSpan searching)
    {
        string json = "{\"data\":{\"elapsed_total\":" + Elapsed(total.Elapsed, summary: true) + ",\"stats\":{\"bytes_printed\":" + _stats.BytesPrinted
            + ",\"bytes_searched\":" + _stats.BytesSearched + ",\"elapsed\":" + Elapsed(searching, summary: true)
            + ",\"matched_lines\":" + _stats.MatchedLines + ",\"matches\":" + _stats.Matches + ",\"searches\":" + _stats.Searches
            + ",\"searches_with_match\":" + _stats.FilesWithMatch + "}},\"type\":\"summary\"}";
        _emit(new RgEmit(json));
    }

    private static string Elapsed(TimeSpan t, bool summary)
    {
        long secs = (long)t.TotalSeconds;
        long nanos = (t.Ticks % TimeSpan.TicksPerSecond) * 100;
        string human = $"{secs}.{nanos / 1000:D6}s";
        return summary
            ? "{\"human\":\"" + human + "\",\"nanos\":" + nanos + ",\"secs\":" + secs + "}"
            : "{\"secs\":" + secs + ",\"nanos\":" + nanos + ",\"human\":\"" + human + "\"}";
    }

    /// <summary><c>{"text":"..."}</c>, or <c>{"bytes":"base64"}</c> when the text carries escaped (non-UTF-8) bytes.</summary>
    internal static string JsonText(string s)
    {
        foreach (char ch in s)
        {
            if (ch is >= '\uDC80' and <= '\uDCFF')
            {
                int idx = s.IndexOf(ch);
                bool lone = idx == 0 || !char.IsHighSurrogate(s[idx - 1]);
                if (lone) return "{\"bytes\":\"" + Convert.ToBase64String(RawBytes.GetBytes(s)) + "\"}";
            }
        }
        var sb = new StringBuilder("{\"text\":\"");
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append("\"}").ToString();
    }
}
