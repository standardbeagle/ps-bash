namespace PsBash.Core.Parser;

/// <summary>A match that ran past a bound (work, memory): status 2, never a hang.</summary>
internal sealed class EreBudgetException() : Exception("REG_ESPACE");

/// <summary>
/// A compiled ERE: glibc's NFA (node indices, <c>nexts</c>, sorted <c>edests</c>) and the matcher.
/// <list type="number">
/// <item>Where: leftmost-longest by one forward pass that tracks, per NFA node, the leftmost start that reached it
/// (O(n·m), no backtracking).</item>
/// <item>Groups: glibc's <c>sift_states_backward</c> (which nodes can still reach the match end, per position)
/// and then its <c>set_regs</c>/<c>proceed_next_node</c>/<c>update_regs</c> walk, so every capture is the one
/// glibc reports.</item>
/// <item>Back-references: glibc's fail-stack search over (start, end) candidates, under the work budget.</item>
/// </list>
/// </summary>
internal sealed class BashEreProgram
{
    // Work units (node visits) one match may spend: ~a second of CPU. Past it the test is status 2.
    internal const long MaxWork = 200_000_000;
    // The group walk keeps one bit per (node, position) of the match: 32 MB.
    internal const long MaxViabilityWords = 1L << 22;

    private readonly EreKind[] _kind;
    private readonly int[] _value;
    private readonly EreSet?[] _set;
    private readonly bool[] _opt;
    private readonly int[] _next;
    private readonly int[][] _edests;
    private readonly int[] _predStart;
    private readonly int[] _pred;
    private readonly int[] _consuming;
    private readonly int[] _backrefs;
    private readonly int _init, _end, _nsub;
    private readonly int[] _subexpMap;
    private readonly bool _hasBackref, _icase;

    internal string? Error { get; }
    internal int GroupCount => _nsub;

    private BashEreProgram(string error)
    {
        Error = error;
        _kind = [];
        _value = _next = _predStart = _pred = _consuming = _backrefs = _subexpMap = [];
        _set = [];
        _opt = [];
        _edests = [];
    }

    private BashEreProgram(List<EreTree> nodes, int init, int end, int nsub, int[] subexpMap, bool hasBackref,
        bool icase)
    {
        int m = nodes.Count;
        _kind = new EreKind[m];
        _value = new int[m];
        _set = new EreSet?[m];
        _opt = new bool[m];
        _next = new int[m];
        _edests = new int[m][];
        _init = init;
        _end = end;
        _nsub = nsub;
        _subexpMap = subexpMap;
        _hasBackref = hasBackref;
        _icase = icase;
        var consuming = new List<int>();
        var backrefs = new List<int>();
        var predCount = new int[m + 1];
        for (int i = 0; i < m; i++)
        {
            var node = nodes[i];
            _kind[i] = node.Kind;
            _value[i] = node.Value;
            _set[i] = node.Set;
            _opt[i] = node.Opt;
            _next[i] = node.Next?.NodeIdx ?? -1;
            switch (node.Kind)
            {
                case EreKind.Alt:
                case EreKind.Star:
                    // link_nfa_nodes: re_node_set_init_2 keeps the two destinations sorted by node index.
                    int left = node.Left != null ? node.Left.First!.NodeIdx : node.Next!.NodeIdx;
                    int right = node.Right != null ? node.Right.First!.NodeIdx : node.Next!.NodeIdx;
                    _edests[i] = left == right ? [left] : left < right ? [left, right] : [right, left];
                    break;
                case EreKind.Anchor:
                case EreKind.OpenSub:
                case EreKind.CloseSub:
                case EreKind.BackRef:
                    _edests[i] = [node.Next!.NodeIdx];
                    break;
                default:
                    _edests[i] = [];
                    break;
            }
            if (node.Kind is EreKind.Char or EreKind.Set or EreKind.Period) consuming.Add(i);
            if (node.Kind == EreKind.BackRef) backrefs.Add(i);
            foreach (int d in _edests[i]) predCount[d + 1]++;
        }
        _consuming = consuming.ToArray();
        _backrefs = backrefs.ToArray();
        // Reverse epsilon edges (a back-reference counts as one: it can match the empty string).
        _predStart = new int[m + 1];
        for (int i = 0; i < m; i++) _predStart[i + 1] = _predStart[i] + predCount[i + 1];
        _pred = new int[_predStart[m]];
        var fill = (int[])_predStart.Clone();
        for (int i = 0; i < m; i++)
            foreach (int d in _edests[i])
                _pred[fill[d]++] = i;
    }

    internal static BashEreProgram Failed(string code) => new(code);

    internal static BashEreProgram FromTree(List<EreTree> nodes, int init, int end, int nsub, int[] subexpMap,
        bool hasBackref, bool icase) => new(nodes, init, end, nsub, subexpMap, hasBackref, icase);

    private static bool IsEpsilon(EreKind k) =>
        k is EreKind.Alt or EreKind.Star or EreKind.OpenSub or EreKind.CloseSub or EreKind.Anchor;

    /// <summary>The code points of <paramref name="s"/> (a lone surrogate is one) and each one's UTF-16 offset.</summary>
    internal static int[] CodePoints(string s, out int[] offsets)
    {
        var cps = new List<int>(s.Length);
        var offs = new List<int>(s.Length + 1);
        for (int i = 0; i < s.Length; i++)
        {
            offs.Add(i);
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                cps.Add(char.ConvertToUtf32(s[i], s[i + 1]));
                i++;
            }
            else cps.Add(s[i]);
        }
        offs.Add(s.Length);
        offsets = offs.ToArray();
        return cps.ToArray();
    }

    internal BashEreResult Run(string subject)
    {
        if (Error != null) return new BashEreResult(2, BashEreResult.NoGroups, Error);
        var cps = CodePoints(subject, out var offsets);
        if (_icase)
            for (int i = 0; i < cps.Length; i++) cps[i] = BashEreClasses.ToUpper(cps[i]);
        int[]? regs;
        try
        {
            var matcher = new Matcher(this, cps);
            regs = _hasBackref ? matcher.SearchWithBackrefs() : matcher.Search();
        }
        catch (EreBudgetException e)
        {
            return new BashEreResult(2, BashEreResult.NoGroups, e.Message);
        }
        if (regs == null) return new BashEreResult(1, BashEreResult.NoGroups);
        var groups = new string[_nsub + 1];
        for (int g = 0; g <= _nsub; g++)
        {
            int so = regs[2 * g], eo = regs[2 * g + 1];
            groups[g] = so >= 0 && eo >= so ? subject.Substring(offsets[so], offsets[eo] - offsets[so]) : "";
        }
        return new BashEreResult(0, groups);
    }

    private sealed class Matcher(BashEreProgram p, int[] cps)
    {
        private readonly int _n = cps.Length;
        private readonly int _m = p._kind.Length;
        private readonly int _words = (p._kind.Length + 63) >> 6;
        private long _work;

        private void Spend(long units)
        {
            _work += units;
            if (_work > MaxWork) throw new EreBudgetException();
        }

        private bool IsWordAt(int i) => i >= 0 && i < _n && BashEreClasses.IsWord(cps[i]);

        private bool Holds(int ctx, int pos) => ctx switch
        {
            EreAnchor.LineFirst or EreAnchor.BufFirst => pos == 0,
            EreAnchor.LineLast or EreAnchor.BufLast => pos == _n,
            EreAnchor.WordFirst => !IsWordAt(pos - 1) && IsWordAt(pos),
            EreAnchor.WordLast => IsWordAt(pos - 1) && !IsWordAt(pos),
            EreAnchor.InsideWord => IsWordAt(pos - 1) && IsWordAt(pos),
            EreAnchor.InsideNotWord => !IsWordAt(pos - 1) && !IsWordAt(pos),
            _ => false,
        };

        private bool Accepts(int node, int pos)
        {
            if (pos >= _n) return false;
            int c = cps[pos];
            return p._kind[node] switch
            {
                EreKind.Char => c == p._value[node],
                EreKind.Set => p._set[node]!.Matches(c),
                EreKind.Period => c != 0, // RE_DOT_NOT_NULL; a newline matches (no REG_NEWLINE)
                _ => false,
            };
        }

        // ---- where: leftmost-longest ------------------------------------------------------------------

        internal int[]? Search()
        {
            var (s, e) = LeftmostLongest();
            if (s < 0) return null;
            var regs = new int[2 * (p._nsub + 1)];
            Array.Fill(regs, -1);
            regs[0] = s;
            regs[1] = e;
            if (p._nsub == 0) return regs;
            var v = Viability(s, e, backrefs: false);
            return Walk(s, e, v, failStack: false);
        }

        private (int Start, int End) LeftmostLongest()
        {
            var stamp = new int[_m];
            var curNode = new int[_m];
            var curStart = new int[_m];
            var nxtNode = new int[_m];
            var nxtStart = new int[_m];
            var stack = new Stack<int>();
            int curCount = 0, nxtCount;
            int bestS = -1, bestE = -1;

            // Each list holds a node once, with the leftmost start that reached it: threads are added in
            // ascending start order, so the first arrival wins.
            void Close(int[] nodes, int[] starts, ref int count, int node, int start, int pos)
            {
                int gen = pos + 1;
                stack.Push(node);
                while (stack.Count > 0)
                {
                    int x = stack.Pop();
                    if (stamp[x] == gen) continue;
                    stamp[x] = gen;
                    Spend(1);
                    switch (p._kind[x])
                    {
                        case EreKind.End:
                            if (bestS < 0 || start < bestS) { bestS = start; bestE = pos; }
                            else if (start == bestS && pos > bestE) bestE = pos;
                            break;
                        case EreKind.Anchor:
                            if (Holds(p._value[x], pos)) stack.Push(p._edests[x][0]);
                            break;
                        case EreKind.Alt:
                        case EreKind.Star:
                        case EreKind.OpenSub:
                        case EreKind.CloseSub:
                            var ed = p._edests[x];
                            for (int k = ed.Length - 1; k >= 0; k--) stack.Push(ed[k]);
                            break;
                        default:
                            nodes[count] = x;
                            starts[count] = start;
                            count++;
                            break;
                    }
                }
            }

            for (int pos = 0; ; pos++)
            {
                if (bestS < 0) Close(curNode, curStart, ref curCount, p._init, pos, pos);
                if (pos == _n || (curCount == 0 && bestS >= 0)) break;
                nxtCount = 0;
                for (int t = 0; t < curCount; t++)
                {
                    int start = curStart[t];
                    if (bestS >= 0 && start > bestS) continue;
                    int node = curNode[t];
                    if (Accepts(node, pos)) Close(nxtNode, nxtStart, ref nxtCount, p._next[node], start, pos + 1);
                }
                (curNode, nxtNode) = (nxtNode, curNode);
                (curStart, nxtStart) = (nxtStart, curStart);
                curCount = nxtCount;
            }
            return (bestS, bestE);
        }

        // ---- groups: glibc's sifted states and set_regs -----------------------------------------------

        private bool Test(ulong[] v, int row, int node) => (v[row * _words + (node >> 6)] >> (node & 63) & 1) != 0;

        private void Mark(ulong[] v, int row, int node) => v[row * _words + (node >> 6)] |= 1UL << (node & 63);

        /// <summary>
        /// Row <c>pos - s</c>: the nodes from which, at <c>pos</c>, the match can still end exactly at
        /// <paramref name="e"/>. With back-references a reference counts as matching any length (the fail-stack
        /// walk checks the text).
        /// </summary>
        private ulong[] Viability(int s, int e, bool backrefs)
        {
            int rows = e - s + 1;
            if ((long)rows * _words > MaxViabilityWords) throw new EreBudgetException();
            var v = new ulong[rows * _words];
            var after = backrefs ? new ulong[_words] : null;
            var queue = new int[_m];
            for (int pos = e; pos >= s; pos--)
            {
                int row = pos - s;
                int qn = 0;
                if (pos == e)
                {
                    Mark(v, row, p._end);
                    queue[qn++] = p._end;
                }
                else
                {
                    foreach (int node in p._consuming)
                        if (Accepts(node, pos) && Test(v, row + 1, p._next[node]))
                        {
                            Mark(v, row, node);
                            queue[qn++] = node;
                        }
                }
                if (after != null)
                    foreach (int b in p._backrefs)
                    {
                        int nx = p._next[b];
                        if ((after[nx >> 6] >> (nx & 63) & 1) != 0 && !Test(v, row, b))
                        {
                            Mark(v, row, b);
                            queue[qn++] = b;
                        }
                    }
                for (int qi = 0; qi < qn; qi++)
                {
                    int d = queue[qi];
                    for (int k = p._predStart[d]; k < p._predStart[d + 1]; k++)
                    {
                        int pr = p._pred[k];
                        if (Test(v, row, pr)) continue;
                        var kind = p._kind[pr];
                        if (kind == EreKind.Anchor && !Holds(p._value[pr], pos)) continue;
                        if (kind == EreKind.BackRef && !backrefs) continue;
                        Mark(v, row, pr);
                        queue[qn++] = pr;
                    }
                }
                if (after != null)
                    for (int w = 0; w < _words; w++) after[w] |= v[row * _words + w];
                Spend(_m);
            }
            return v;
        }

        private sealed class ViaSet(int size)
        {
            private readonly int[] _mark = new int[size];
            private readonly List<int> _items = [];
            private int _gen = 1;

            internal void Add(int x)
            {
                if (_mark[x] == _gen) return;
                _mark[x] = _gen;
                _items.Add(x);
            }

            internal bool Contains(int x) => _mark[x] == _gen;

            internal void Clear()
            {
                _gen++;
                _items.Clear();
            }

            internal int[] Snapshot() => _items.ToArray();

            internal void Restore(int[] items)
            {
                Clear();
                foreach (int x in items) Add(x);
            }
        }

        private readonly record struct FailEntry(int Pos, int Node, int[] Regs, int[] Prev, int[] Via);

        /// <summary>glibc <c>set_regs</c>: the registers of the match [s, e), or null (REG_NOMATCH).</summary>
        private int[]? Walk(int s, int e, ulong[] v, bool failStack)
        {
            int nregs = p._nsub + 1;
            var pm = new int[2 * nregs];
            Array.Fill(pm, -1);
            pm[0] = s;
            pm[1] = e;
            var prev = (int[])pm.Clone();
            var via = new ViaSet(_m);
            var fs = failStack ? new Stack<FailEntry>() : null;
            int cur = p._init, pos = s;

            int Pop()
            {
                if (fs == null || fs.Count == 0) return -1;
                var ent = fs.Pop();
                pos = ent.Pos;
                Array.Copy(ent.Regs, pm, pm.Length);
                Array.Copy(ent.Prev, prev, prev.Length);
                via.Restore(ent.Via);
                return ent.Node;
            }

            while (pos <= e)
            {
                Spend(1);
                UpdateRegs(pm, prev, cur, pos);
                if ((pos == e && cur == p._end) || (fs != null && via.Contains(cur)))
                {
                    cur = -1;
                    if (fs != null)
                        for (int r = 0; r < nregs; r++)
                            if (pm[2 * r] > -1 && pm[2 * r + 1] == -1)
                            {
                                cur = Pop();
                                break;
                            }
                    if (cur < 0) return Finish(pm);
                }
                cur = Proceed(ref pos, cur, pm, prev, via, fs, v, s, e);
                if (cur < 0)
                {
                    cur = Pop();
                    if (cur < 0) return null;
                }
            }
            return Finish(pm);
        }

        private int[] Finish(int[] pm)
        {
            for (int i = 0; i < p._nsub; i++)
            {
                int to = p._subexpMap[i];
                if (to == i) continue;
                pm[2 * (i + 1)] = pm[2 * (to + 1)];
                pm[2 * (i + 1) + 1] = pm[2 * (to + 1) + 1];
            }
            return pm;
        }

        private void UpdateRegs(int[] pm, int[] prev, int cur, int pos)
        {
            var kind = p._kind[cur];
            if (kind == EreKind.OpenSub)
            {
                int r = p._value[cur] + 1;
                pm[2 * r] = pos;
                pm[2 * r + 1] = -1;
            }
            else if (kind == EreKind.CloseSub)
            {
                int r = p._value[cur] + 1;
                if (pm[2 * r] < pos)
                {
                    // A non-empty match, or not inside an optional subexpression: accept it.
                    pm[2 * r + 1] = pos;
                    Array.Copy(pm, prev, pm.Length);
                }
                else if (p._opt[cur] && prev[2 * r] != -1)
                    // An empty pass through an optional group after a real one, like (a?)*: undo it, and
                    // the inner groups with it (((a?))*).
                    Array.Copy(prev, pm, pm.Length);
                else
                    pm[2 * r + 1] = pos;
            }
        }

        // glibc proceed_next_node.
        private int Proceed(ref int pos, int cur, int[] pm, int[] prev, ViaSet via, Stack<FailEntry>? fs,
            ulong[] v, int s, int e)
        {
            var kind = p._kind[cur];
            if (IsEpsilon(kind))
            {
                via.Add(cur);
                int dest = -1;
                foreach (int cand in p._edests[cur])
                {
                    if (!Test(v, pos - s, cand)) continue;
                    if (dest == -1)
                    {
                        dest = cand;
                        continue;
                    }
                    // Avoid an endless loop like "(a*)*": take the second branch if the first was tried.
                    if (via.Contains(dest)) return cand;
                    fs?.Push(new FailEntry(pos, cand, (int[])pm.Clone(), (int[])prev.Clone(), via.Snapshot()));
                    break;
                }
                return dest;
            }
            int naccepted = 0;
            if (kind == EreKind.BackRef)
            {
                int r = p._value[cur] + 1;
                int so = pm[2 * r], eo = pm[2 * r + 1];
                naccepted = eo - so;
                if (fs != null)
                {
                    if (so == -1 || eo == -1 || naccepted < 0) return -1;
                    if (naccepted != 0)
                    {
                        if (_n - pos < naccepted) return -1;
                        for (int k = 0; k < naccepted; k++)
                            if (cps[so + k] != cps[pos + k]) return -1;
                    }
                }
                if (naccepted == 0)
                {
                    via.Add(cur);
                    int dest = p._edests[cur][0];
                    return pos <= e && Test(v, pos - s, dest) ? dest : -1;
                }
            }
            if (naccepted != 0 || Accepts(cur, pos))
            {
                int dest = p._next[cur];
                pos = naccepted == 0 ? pos + 1 : pos + naccepted;
                if (fs != null && (pos > e || !Test(v, pos - s, dest))) return -1;
                via.Clear();
                return dest;
            }
            return -1;
        }

        // ---- back-references ----------------------------------------------------------------------------

        /// <summary>
        /// Leftmost start, longest end, checked exactly: for each start, the ends a reference-as-any-length NFA
        /// can reach, longest first, each confirmed by glibc's fail-stack walk.
        /// </summary>
        internal int[]? SearchWithBackrefs()
        {
            for (int s = 0; s <= _n; s++)
            {
                var ends = Reach(s);
                if (ends == null) continue;
                for (int e = _n; e >= s; e--)
                {
                    if (!ends[e]) continue;
                    var v = Viability(s, e, backrefs: true);
                    if (!Test(v, 0, p._init)) continue;
                    var regs = Walk(s, e, v, failStack: true);
                    if (regs != null) return regs;
                }
            }
            return null;
        }

        // The positions where a match from s could end, a back-reference matching any length.
        private bool[]? Reach(int s)
        {
            var ends = new bool[_n + 1];
            bool any = false;
            var mark = new int[_m];
            var sticky = new List<int>();
            var stickyMark = new bool[_m];
            var cur = new List<int>();
            var nxt = new List<int>();
            var stack = new Stack<int>();
            int gen = 0;

            void Close(List<int> list, int node, int pos)
            {
                stack.Push(node);
                while (stack.Count > 0)
                {
                    int x = stack.Pop();
                    if (mark[x] == gen) continue;
                    mark[x] = gen;
                    Spend(1);
                    switch (p._kind[x])
                    {
                        case EreKind.End:
                            ends[pos] = any = true;
                            break;
                        case EreKind.Anchor:
                            if (Holds(p._value[x], pos)) stack.Push(p._edests[x][0]);
                            break;
                        case EreKind.BackRef:
                            int nx = p._next[x];
                            stack.Push(nx);
                            if (!stickyMark[nx])
                            {
                                stickyMark[nx] = true;
                                sticky.Add(nx);
                            }
                            break;
                        case EreKind.Alt:
                        case EreKind.Star:
                        case EreKind.OpenSub:
                        case EreKind.CloseSub:
                            foreach (int d in p._edests[x]) stack.Push(d);
                            break;
                        default:
                            list.Add(x);
                            break;
                    }
                }
            }

            gen++;
            Close(cur, p._init, s);
            for (int pos = s; pos < _n; pos++)
            {
                if (cur.Count == 0 && sticky.Count == 0) break;
                gen++;
                nxt.Clear();
                foreach (int node in cur)
                    if (Accepts(node, pos)) Close(nxt, p._next[node], pos + 1);
                for (int k = 0; k < sticky.Count; k++) Close(nxt, sticky[k], pos + 1);
                (cur, nxt) = (nxt, cur);
            }
            return any ? ends : null;
        }
    }
}
