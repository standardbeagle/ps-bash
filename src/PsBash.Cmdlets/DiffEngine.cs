namespace PsBash.Cmdlets;

/// <summary>One hunk of the edit script: <c>Deleted</c> lines of file 0 starting at 0-based <c>Line0</c> were
/// replaced by <c>Inserted</c> lines of file 1 starting at <c>Line1</c> (GNU diff's <c>struct change</c>).</summary>
internal sealed class DiffChange
{
    public int Line0, Line1, Deleted, Inserted;

    /// <summary>True when every line of the hunk is ignorable (<c>-B</c> blank lines, <c>-I</c> regexp): the hunk is
    /// not reported on its own, only as part of a hunk with real changes nearby.</summary>
    public bool Ignore;
}

/// <summary>
/// GNU diffutils 3.10's comparison core, ported so the edit scripts come out hunk for hunk the same as GNU's:
/// identical-end trimming, <c>discard_confusing_lines</c>, the bidirectional Myers search of <c>diffseq.h</c>
/// (<c>compareseq</c>/<c>diag</c>, without the speed heuristics that only matter for huge inputs),
/// <c>shift_boundaries</c> (the "prettier" slide of change runs) and <c>build_script</c>.
/// <para>Input is two arrays of EQUIVALENCE CLASSES (one int per line; equal ints = lines that compare equal under
/// <c>-i -b -w ...</c>); the engine never sees text.</para>
/// </summary>
internal static class DiffEngine
{
    public static List<DiffChange> Compute(int[] equiv0, int[] equiv1)
    {
        int n0 = equiv0.Length, n1 = equiv1.Length;

        // find_identical_ends: lines equal at both ends never take part in the comparison.
        int prefix = 0;
        while (prefix < n0 && prefix < n1 && equiv0[prefix] == equiv1[prefix]) prefix++;
        int suffix = 0;
        while (suffix < n0 - prefix && suffix < n1 - prefix && equiv0[n0 - 1 - suffix] == equiv1[n1 - 1 - suffix]) suffix++;

        int b0 = n0 - prefix - suffix, b1 = n1 - prefix - suffix;   // buffered (middle) line counts
        var changed0 = new bool[n0 + 2];                           // [i + 1]: slot 0 and n0 + 1 are the sentinels
        var changed1 = new bool[n1 + 2];

        if (b0 > 0 || b1 > 0)
        {
            var mid0 = new int[b0];
            var mid1 = new int[b1];
            Array.Copy(equiv0, prefix, mid0, 0, b0);
            Array.Copy(equiv1, prefix, mid1, 0, b1);
            CompareMiddle(mid0, mid1, changed0, changed1, prefix);
        }

        ShiftBoundaries(equiv0, changed0, changed1);
        ShiftBoundaries(equiv1, changed1, changed0);
        return BuildScript(changed0, changed1, n0, n1);
    }

    // ───────────── discard_confusing_lines + compareseq ─────────────

    private static void CompareMiddle(int[] x, int[] y, bool[] changed0, bool[] changed1, int offset)
    {
        int maxClass = 0;
        foreach (var v in x) if (v > maxClass) maxClass = v;
        foreach (var v in y) if (v > maxClass) maxClass = v;
        var count0 = new int[maxClass + 1];
        var count1 = new int[maxClass + 1];
        foreach (var v in x) count0[v]++;
        foreach (var v in y) count1[v]++;

        var disc0 = Discards(x, count1);
        var disc1 = Discards(y, count0);

        var ux = new List<int>(); var rx = new List<int>();
        var uy = new List<int>(); var ry = new List<int>();
        for (int i = 0; i < x.Length; i++)
        {
            if (disc0[i] == 0) { ux.Add(x[i]); rx.Add(i); }
            else changed0[offset + i + 1] = true;
        }
        for (int i = 0; i < y.Length; i++)
        {
            if (disc1[i] == 0) { uy.Add(y[i]); ry.Add(i); }
            else changed1[offset + i + 1] = true;
        }

        var ctx = new Seq(ux.ToArray(), uy.ToArray());
        ctx.Run();
        for (int i = 0; i < ctx.XChanged.Length; i++) if (ctx.XChanged[i]) changed0[offset + rx[i] + 1] = true;
        for (int i = 0; i < ctx.YChanged.Length; i++) if (ctx.YChanged[i]) changed1[offset + ry[i] + 1] = true;
    }

    /// <summary>0 = keep, 1 = discard (matches nothing in the other file), 2 = provisional (matches very many).</summary>
    private static byte[] Discards(int[] equivs, int[] otherCounts)
    {
        int end = equivs.Length;
        var discards = new byte[end];

        int many = 5;
        int tem = end / 64;
        while ((tem >>= 2) > 0) many *= 2;

        for (int i = 0; i < end; i++)
        {
            int nmatch = otherCounts[equivs[i]];
            if (nmatch == 0) discards[i] = 1;
            else if (nmatch > many) discards[i] = 2;
        }

        // Cancel provisional discards not in the middle of a run of discards.
        for (int i = 0; i < end; i++)
        {
            if (discards[i] == 2) discards[i] = 0;
            else if (discards[i] != 0)
            {
                int j, length, provisional = 0;
                for (j = i; j < end; j++)
                {
                    if (discards[j] == 0) break;
                    if (discards[j] == 2) ++provisional;
                }
                while (j > i && discards[j - 1] == 2) { discards[--j] = 0; --provisional; }
                length = j - i;

                if (provisional * 4 > length)
                {
                    while (j > i) if (discards[--j] == 2) discards[j] = 0;
                }
                else
                {
                    int consec, minimum = 1;
                    int t = length >> 2;
                    while (0 < (t >>= 2)) minimum <<= 1;
                    minimum++;

                    for (j = 0, consec = 0; j < length; j++)
                    {
                        if (discards[i + j] != 2) consec = 0;
                        else if (minimum == ++consec) j -= consec;
                        else if (minimum < consec) discards[i + j] = 0;
                    }

                    for (j = 0, consec = 0; j < length; j++)
                    {
                        if (j >= 8 && discards[i + j] == 1) break;
                        if (discards[i + j] == 2) { consec = 0; discards[i + j] = 0; }
                        else if (discards[i + j] == 0) consec = 0;
                        else consec++;
                        if (consec == 3) break;
                    }

                    i += length - 1;

                    for (j = 0, consec = 0; j < length; j++)
                    {
                        if (j >= 8 && discards[i - j] == 1) break;
                        if (discards[i - j] == 2) { consec = 0; discards[i - j] = 0; }
                        else if (discards[i - j] == 0) consec = 0;
                        else consec++;
                        if (consec == 3) break;
                    }
                }
            }
        }
        return discards;
    }

    /// <summary>diffseq.h <c>compareseq</c> over two reduced vectors; marks the lines that are in no common subsequence.</summary>
    private sealed class Seq
    {
        private readonly int[] _x, _y;
        private readonly int[] _fd, _bd;
        private readonly int _off;
        public readonly bool[] XChanged, YChanged;

        public Seq(int[] x, int[] y)
        {
            _x = x; _y = y;
            XChanged = new bool[x.Length];
            YChanged = new bool[y.Length];
            int diags = x.Length + y.Length + 3;
            _fd = new int[diags];
            _bd = new int[diags];
            _off = y.Length + 1;     // GNU: fdiag/bdiag are offset by (ylim + 1) so negative diagonals index validly
        }

        public void Run() => Compare(0, _x.Length, 0, _y.Length);

        private bool Eq(int xi, int yi) => _x[xi] == _y[yi];

        private void Compare(int xoff, int xlim, int yoff, int ylim)
        {
            while (xoff < xlim && yoff < ylim && Eq(xoff, yoff)) { xoff++; yoff++; }
            while (xoff < xlim && yoff < ylim && Eq(xlim - 1, ylim - 1)) { xlim--; ylim--; }

            if (xoff == xlim)
            {
                while (yoff < ylim) YChanged[yoff++] = true;
            }
            else if (yoff == ylim)
            {
                while (xoff < xlim) XChanged[xoff++] = true;
            }
            else
            {
                Diag(xoff, xlim, yoff, ylim, out int xmid, out int ymid);
                Compare(xoff, xmid, yoff, ymid);
                Compare(xmid, xlim, ymid, ylim);
            }
        }

        private void Diag(int xoff, int xlim, int yoff, int ylim, out int xmid, out int ymid)
        {
            var fd = _fd; var bd = _bd; int o = _off;
            int dmin = xoff - ylim;
            int dmax = xlim - yoff;
            int fmid = xoff - yoff;
            int bmid = xlim - ylim;
            int fmin = fmid, fmax = fmid;
            int bmin = bmid, bmax = bmid;
            bool odd = ((fmid - bmid) & 1) != 0;

            fd[o + fmid] = xoff;
            bd[o + bmid] = xlim;

            for (int c = 1; ; ++c)
            {
                // Extend the top-down search by an edit step in each diagonal.
                if (fmin > dmin) fd[o + --fmin - 1] = -1; else ++fmin;
                if (fmax < dmax) fd[o + ++fmax + 1] = -1; else --fmax;
                for (int d = fmax; d >= fmin; d -= 2)
                {
                    int tlo = fd[o + d - 1];
                    int thi = fd[o + d + 1];
                    int x0 = tlo < thi ? thi : tlo + 1;
                    int x, y;
                    for (x = x0, y = x0 - d; x < xlim && y < ylim && Eq(x, y); x++, y++) { }
                    fd[o + d] = x;
                    if (odd && bmin <= d && d <= bmax && bd[o + d] <= x)
                    {
                        xmid = x; ymid = y;
                        return;
                    }
                }

                // Similarly extend the bottom-up search.
                if (bmin > dmin) bd[o + --bmin - 1] = int.MaxValue; else ++bmin;
                if (bmax < dmax) bd[o + ++bmax + 1] = int.MaxValue; else --bmax;
                for (int d = bmax; d >= bmin; d -= 2)
                {
                    int tlo = bd[o + d - 1];
                    int thi = bd[o + d + 1];
                    int x0 = tlo < thi ? tlo : thi - 1;
                    int x, y;
                    for (x = x0, y = x0 - d; xoff < x && yoff < y && Eq(x - 1, y - 1); x--, y--) { }
                    bd[o + d] = x;
                    if (!odd && fmin <= d && d <= fmax && x <= fd[o + d])
                    {
                        xmid = x; ymid = y;
                        return;
                    }
                }
            }
        }
    }

    // ───────────── shift_boundaries ─────────────

    /// <summary>
    /// Slides each run of changes up while the line before it equals the run's last line (merging with earlier runs),
    /// then down as far as possible (merging with later ones), then back to line up with a run in the other file.
    /// <c>changed</c>/<c>otherChanged</c> carry a sentinel slot at both ends (index = line + 1).
    /// </summary>
    private static void ShiftBoundaries(int[] equivs, bool[] changedArr, bool[] otherArr)
    {
        int iEnd = equivs.Length;
        bool C(int i) => changedArr[i + 1];
        void SetC(int i, bool v) => changedArr[i + 1] = v;
        bool O(int j) => otherArr[j + 1];

        int i0 = 0, j0 = 0;
        while (true)
        {
            // Scan forwards to find the beginning of another run of changes, tracking the corresponding point in the other file.
            while (i0 < iEnd && !C(i0))
            {
                while (O(j0++)) { }
                i0++;
            }
            if (i0 == iEnd) break;

            int start = i0;
            while (C(++i0)) { }
            while (O(j0)) j0++;

            int runlength, corresponding;
            do
            {
                runlength = i0 - start;

                // Move the changed region back, so long as the previous unchanged line matches the last changed one.
                while (start > 0 && equivs[start - 1] == equivs[i0 - 1])
                {
                    SetC(--start, true);
                    SetC(--i0, false);
                    while (C(start - 1)) start--;
                    while (O(--j0)) { }
                }

                corresponding = O(j0 - 1) ? i0 : iEnd;

                // Move the changed region forward, so long as the first changed line matches the following unchanged one.
                while (i0 != iEnd && equivs[start] == equivs[i0])
                {
                    SetC(start++, false);
                    SetC(i0++, true);
                    while (C(i0)) i0++;
                    while (O(++j0)) corresponding = i0;
                }
            }
            while (runlength != i0 - start);

            // If possible, move the fully merged run of changes back to a corresponding run in the other file.
            while (corresponding < i0)
            {
                SetC(--start, true);
                SetC(--i0, false);
                while (O(--j0)) { }
            }
        }
    }

    // ───────────── build_script ─────────────

    private static List<DiffChange> BuildScript(bool[] changed0, bool[] changed1, int n0, int n1)
    {
        var script = new List<DiffChange>();
        int i0 = 0, i1 = 0;
        while (i0 < n0 || i1 < n1)
        {
            if (changed0[i0 + 1] | changed1[i1 + 1])
            {
                int line0 = i0, line1 = i1;
                while (changed0[i0 + 1]) ++i0;
                while (changed1[i1 + 1]) ++i1;
                script.Add(new DiffChange { Line0 = line0, Line1 = line1, Deleted = i0 - line0, Inserted = i1 - line1 });
            }
            i0++; i1++;
        }
        return script;
    }
}
