namespace PsBash.Cmdlets;

/// <summary>One file's text as <c>diff</c> sees it: lines without terminators, and whether the LAST line had no newline.</summary>
internal sealed class DiffText
{
    public DiffText(string[] lines, bool lastUnterminated, bool binary)
    {
        Lines = lines;
        LastUnterminated = lastUnterminated;
        Binary = binary;
    }

    public string[] Lines { get; }
    public bool LastUnterminated { get; }

    /// <summary>A NUL in the first 4096 characters (GNU looks at the first buffer): the file is not diffed as text.</summary>
    public bool Binary { get; }

    /// <summary>Splits <paramref name="raw"/> (the exact decoded bytes) on <c>\n</c>; <paramref name="stripTrailingCr"/> drops a <c>\r</c>
    /// that precedes each newline (<c>--strip-trailing-cr</c>).</summary>
    public static DiffText Parse(string raw, bool stripTrailingCr, bool treatAsText)
    {
        bool binary = !treatAsText && raw.AsSpan(0, Math.Min(raw.Length, 4096)).Contains('\0');
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\n') continue;
            int end = i;
            if (stripTrailingCr && end > start && raw[end - 1] == '\r') end--;
            lines.Add(raw.Substring(start, end - start));
            start = i + 1;
        }
        bool unterminated = start < raw.Length;
        if (unterminated) lines.Add(raw.Substring(start));
        return new DiffText(lines.ToArray(), unterminated, binary);
    }

    public bool IsUnterminated(int index) => LastUnterminated && index == Lines.Length - 1;
}

/// <summary>Computing the edit script of two texts under the active <c>-i -b -w -B</c> flags (GNU diffutils 3.10 semantics).</summary>
internal static class DiffCompare
{
    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    /// <summary>The comparison key of a line: <c>-w</c> drops every white space character, <c>-b</c> folds runs to one space and drops
    /// trailing white space (leading white space still counts), <c>-i</c> folds case.</summary>
    internal static string Key(string line, DiffPlan plan)
    {
        string key = line;
        if (plan.IgnoreAllSpace)
        {
            var sb = new System.Text.StringBuilder(key.Length);
            foreach (char c in key) if (!IsSpace(c)) sb.Append(c);
            key = sb.ToString();
        }
        else if (plan.IgnoreSpaceChange)
        {
            var sb = new System.Text.StringBuilder(key.Length);
            bool inSpace = false;
            foreach (char c in key)
            {
                if (IsSpace(c)) { inSpace = true; continue; }
                if (inSpace) sb.Append(' ');
                inSpace = false;
                sb.Append(c);
            }
            // a run of white space at the end is dropped (inSpace never flushed); a leading run became one space
            key = sb.ToString();
        }
        if (plan.IgnoreCase) key = key.ToUpperInvariant();
        return key;
    }

    /// <summary>A line <c>-B</c> treats as blank: empty, or only white space when <c>-b</c>/<c>-w</c> make white space insignificant.</summary>
    private static bool IsBlank(string line, DiffPlan plan)
    {
        if (line.Length == 0) return true;
        if (!plan.IgnoreAllSpace && !plan.IgnoreSpaceChange) return false;
        foreach (char c in line) if (!IsSpace(c)) return false;
        return true;
    }

    public static List<DiffChange> Script(DiffText a, DiffText b, DiffPlan plan)
    {
        var classes = new Dictionary<(string Key, bool NoNewline), int>();
        int[] Classify(DiffText t)
        {
            var result = new int[t.Lines.Length];
            for (int i = 0; i < result.Length; i++)
            {
                var key = (Key(t.Lines[i], plan), t.IsUnterminated(i));
                if (!classes.TryGetValue(key, out int id)) classes[key] = id = classes.Count;
                result[i] = id;
            }
            return result;
        }
        var eq0 = Classify(a);
        var eq1 = Classify(b);
        var script = DiffEngine.Compute(eq0, eq1);

        if (plan.IgnoreBlankLines)
        {
            foreach (var c in script)
            {
                bool trivial = true;
                for (int i = 0; i < c.Deleted && trivial; i++) trivial = IsBlank(a.Lines[c.Line0 + i], plan);
                for (int i = 0; i < c.Inserted && trivial; i++) trivial = IsBlank(b.Lines[c.Line1 + i], plan);
                c.Ignore = trivial;
            }
        }
        return script;
    }

    public static bool HasRealChange(List<DiffChange> script) => script.Any(c => !c.Ignore);
}

/// <summary>
/// The three GNU output formats (normal, context, unified), hunk for hunk and byte for byte as diffutils 3.10 prints them: the
/// <c>find_hunk</c> grouping (changes closer than <c>2 * context + 1</c> lines share a hunk, <c>context</c> when one of the two is
/// ignorable), the unified range rule (<c>1</c> / <c>start,0</c> / <c>start,count</c>), the context range rule (<c>1</c> / <c>1,3</c>),
/// <c>!</c> for lines of a change that both deletes and inserts, an unchanged side of a context hunk printed only as its header, and
/// <c>\ No newline at end of file</c> after a last line without one. Pure.
/// </summary>
internal static class DiffFormatter
{
    private const string NoNewlineNotice = "\\ No newline at end of file";

    /// <summary>Writes the diff body. <paramref name="header0"/>/<paramref name="header1"/> are the text after <c>---</c>/<c>+++</c>
    /// (unified) or <c>***</c>/<c>---</c> (context): a name and tab-separated timestamp, or a <c>--label</c>.</summary>
    public static void Write(DiffPlan plan, DiffText a, DiffText b, List<DiffChange> script, string header0, string header1, Action<string> emit)
    {
        switch (plan.Style)
        {
            case DiffStyle.Unified: WriteHunks(plan, a, b, script, header0, header1, unified: true, emit); break;
            case DiffStyle.Context: WriteHunks(plan, a, b, script, header0, header1, unified: false, emit); break;
            default: WriteNormal(plan, a, b, script, emit); break;
        }
    }

    private static void Line(Action<string> emit, string prefix, DiffText t, int index)
    {
        emit(prefix + t.Lines[index]);
        if (t.IsUnterminated(index)) emit(NoNewlineNotice);
    }

    // ───────────── normal ─────────────

    private static string NormalRange(int start, int count) =>
        count == 0 ? start.ToString() : count == 1 ? (start + 1).ToString() : $"{start + 1},{start + count}";

    private static void WriteNormal(DiffPlan plan, DiffText a, DiffText b, List<DiffChange> script, Action<string> emit)
    {
        foreach (var c in script)
        {
            if (plan.IgnoreBlankLines && c.Ignore) continue;
            char letter = c.Deleted > 0 && c.Inserted > 0 ? 'c' : c.Deleted > 0 ? 'd' : 'a';
            emit(NormalRange(c.Line0, c.Deleted) + letter + NormalRange(c.Line1, c.Inserted));
            for (int i = 0; i < c.Deleted; i++) Line(emit, "< ", a, c.Line0 + i);
            if (c.Deleted > 0 && c.Inserted > 0) emit("---");
            for (int i = 0; i < c.Inserted; i++) Line(emit, "> ", b, c.Line1 + i);
        }
    }

    // ───────────── context / unified ─────────────

    /// <summary>GNU <c>find_hunk</c>: the index ranges of <paramref name="script"/> that print together.</summary>
    internal static List<(int First, int Last)> FindHunks(List<DiffChange> script, int context)
    {
        var hunks = new List<(int, int)>();
        long nonIgnorable = 2L * context + 1;
        int i = 0;
        while (i < script.Count)
        {
            int prev = i;
            while (true)
            {
                var cur = script[prev];
                int top0 = cur.Line0 + cur.Deleted;
                if (prev + 1 >= script.Count) break;
                var next = script[prev + 1];
                long thresh = cur.Ignore || next.Ignore ? context : nonIgnorable;
                if (next.Line0 - top0 >= thresh) break;
                prev++;
            }
            hunks.Add((i, prev));
            i = prev + 1;
        }
        return hunks;
    }

    private static string UnifiedRange(int start, int end)
    {
        int count = end - start;
        return count == 0 ? $"{start},0" : count == 1 ? (start + 1).ToString() : $"{start + 1},{count}";
    }

    private static string ContextRange(int start, int end) => end > start + 1 ? $"{start + 1},{end}" : end.ToString();

    private static void WriteHunks(DiffPlan plan, DiffText a, DiffText b, List<DiffChange> script, string header0, string header1,
        bool unified, Action<string> emit)
    {
        if (plan.IgnoreBlankLines && !DiffCompare.HasRealChange(script)) return;

        bool headerWritten = false;
        foreach (var (first, last) in FindHunks(script, plan.Context))
        {
            var hunk = script.GetRange(first, last - first + 1);
            if (plan.IgnoreBlankLines && hunk.All(c => c.Ignore)) continue;

            if (!headerWritten)
            {
                emit((unified ? "--- " : "*** ") + header0);
                emit((unified ? "+++ " : "--- ") + header1);
                headerWritten = true;
            }

            int first0 = Math.Max(hunk[0].Line0 - plan.Context, 0);
            int first1 = Math.Max(hunk[0].Line1 - plan.Context, 0);
            var lastChange = hunk[^1];
            int last0 = Math.Min(lastChange.Line0 + lastChange.Deleted + plan.Context, a.Lines.Length);
            int last1 = Math.Min(lastChange.Line1 + lastChange.Inserted + plan.Context, b.Lines.Length);

            if (unified) WriteUnifiedHunk(a, b, hunk, first0, last0, first1, last1, emit);
            else WriteContextHunk(a, b, hunk, first0, last0, first1, last1, emit);
        }
    }

    private static void WriteUnifiedHunk(DiffText a, DiffText b, List<DiffChange> hunk, int first0, int last0, int first1, int last1, Action<string> emit)
    {
        emit($"@@ -{UnifiedRange(first0, last0)} +{UnifiedRange(first1, last1)} @@");
        int i = first0;
        foreach (var c in hunk)
        {
            while (i < c.Line0) Line(emit, " ", a, i++);
            for (int k = 0; k < c.Deleted; k++) Line(emit, "-", a, c.Line0 + k);
            for (int k = 0; k < c.Inserted; k++) Line(emit, "+", b, c.Line1 + k);
            i = c.Line0 + c.Deleted;
        }
        while (i < last0) Line(emit, " ", a, i++);
    }

    private static void WriteContextHunk(DiffText a, DiffText b, List<DiffChange> hunk, int first0, int last0, int first1, int last1, Action<string> emit)
    {
        emit("***************");
        emit($"*** {ContextRange(first0, last0)} ****");
        if (hunk.Any(c => c.Deleted > 0))
        {
            int i = first0;
            foreach (var c in hunk)
            {
                while (i < c.Line0) Line(emit, "  ", a, i++);
                for (int k = 0; k < c.Deleted; k++) Line(emit, c.Inserted > 0 ? "! " : "- ", a, c.Line0 + k);
                i = c.Line0 + c.Deleted;
            }
            while (i < last0) Line(emit, "  ", a, i++);
        }
        emit($"--- {ContextRange(first1, last1)} ----");
        if (hunk.Any(c => c.Inserted > 0))
        {
            int j = first1;
            foreach (var c in hunk)
            {
                while (j < c.Line1) Line(emit, "  ", b, j++);
                for (int k = 0; k < c.Inserted; k++) Line(emit, c.Deleted > 0 ? "! " : "+ ", b, c.Line1 + k);
                j = c.Line1 + c.Inserted;
            }
            while (j < last1) Line(emit, "  ", b, j++);
        }
    }

    // ───────────── headers ─────────────

    /// <summary>GNU's file-header time: <c>2020-01-02 03:04:05.123456789 +0000</c> in the local zone (the tick resolution fills the first seven fraction digits).</summary>
    public static string FormatTimestamp(DateTimeOffset time)
    {
        var offset = time.Offset;
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        offset = offset.Duration();
        return time.ToString("yyyy-MM-dd HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture)
               + "00 " + sign + offset.Hours.ToString("00") + offset.Minutes.ToString("00");
    }
}
