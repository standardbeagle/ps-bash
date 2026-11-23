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
/// <item>Back-references: one backward pass (a reference counting as any length) names the positions a match can
/// start at; from each, leftmost first, an exact search over (node, position, referenced groups' spans), each state
/// once, finds the ends; the states on a real path to the longest (glibc's sifted states) steer its fail-stack walk
/// to the groups.</item>
/// </list>
/// Every loop is charged to the work budget and every allocation to the memory budget: past either, status 2.
/// </summary>
internal sealed class BashEreProgram
{
    // Work units (node visits, characters compared, words allocated) one match may spend: ~a second of CPU.
    internal const long MaxWork = 200_000_000;
    // One bit per (node, position): 32 MB.
    internal const long MaxViabilityWords = 1L << 22;
    // Memory one match may hold (bitsets, search states, the fail stack).
    internal const long MaxMemoryBytes = 128L << 20;
    // Distinct back-reference search states from one start.
    internal const int MaxStates = 1 << 20;

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
    // Group index -> its slot among the groups some back-reference names (-1: none does).
    private readonly int[] _refSlot;
    private readonly int _nref;
    private readonly bool _hasBackref, _icase;

    internal string? Error { get; }
    internal int GroupCount => _nsub;

    private BashEreProgram(string error)
    {
        Error = error;
        _kind = [];
        _value = _next = _predStart = _pred = _consuming = _backrefs = _subexpMap = _refSlot = [];
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
        _refSlot = new int[nsub];
        Array.Fill(_refSlot, -1);
        foreach (int b in _backrefs)
            if (_value[b] >= 0 && _value[b] < nsub && _refSlot[_value[b]] < 0) _refSlot[_value[b]] = _nref++;
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
        var cps = new int[s.Length];
        var offs = new int[s.Length + 1];
        int k = 0;
        for (int i = 0; i < s.Length; i++)
        {
            offs[k] = i;
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                cps[k++] = char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
            }
            else cps[k++] = s[i];
        }
        offs[k] = s.Length;
        if (k < s.Length)
        {
            Array.Resize(ref cps, k);
            Array.Resize(ref offs, k + 1);
        }
        offsets = offs;
        return cps;
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
        private long _work, _memory;

        private void Spend(long units)
        {
            _work += units;
            if (_work > MaxWork) throw new EreBudgetException();
        }

        // Charge an allocation of `bytes` (work: one unit per 8 bytes) that stays held until Release.
        private void Hold(long bytes)
        {
            _memory += bytes;
            if (_memory > MaxMemoryBytes) throw new EreBudgetException();
            Spend(1 + (bytes >> 3));
        }

        private void Release(long bytes) => _memory -= bytes;

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
            // An invalid byte (RawBytes' marker) is only ever itself: glibc's `.` and every bracket, negated ones
            // included, refuse it (its single-byte bit is masked out in UTF-8, and it has no wide character).
            if (c >= RawBytes.FirstMarker && c <= RawBytes.LastMarker)
                return p._kind[node] == EreKind.Char && c == p._value[node];
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
            var v = Viability(s, e);
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
        /// glibc's sifted states for a regex without back-references: row <c>pos - s</c> holds the nodes from which,
        /// at <c>pos</c>, the match can still end exactly at <paramref name="e"/>.
        /// </summary>
        private ulong[] Viability(int s, int e)
        {
            int rows = e - s + 1;
            if ((long)rows * _words > MaxViabilityWords) throw new EreBudgetException();
            Hold(8L * rows * _words);
            var v = new ulong[rows * _words];
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
                Close(v, row, pos, queue, qn, backrefs: false);
                Spend(_m + _words);
            }
            return v;
        }

        // Backward epsilon closure of the `qn` queued nodes of a row (each edge once: O(edges)).
        private void Close(ulong[] v, int row, int pos, int[] queue, int qn, bool backrefs)
        {
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
            Spend(_m);
            var via = new ViaSet(_m);
            var fs = failStack ? new Stack<FailEntry>() : null;
            int cur = p._init, pos = s;

            int Pop()
            {
                if (fs == null || fs.Count == 0) return -1;
                var ent = fs.Pop();
                Release(EntryBytes(ent));
                Spend(ent.Regs.Length + ent.Via.Length);
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
                    if (cur < 0) return Done(Finish(pm));
                }
                cur = Proceed(ref pos, cur, pm, prev, via, fs, v, s, e);
                if (cur < 0)
                {
                    cur = Pop();
                    if (cur < 0) return Done(null);
                }
            }
            return Done(Finish(pm));

            int[]? Done(int[]? result)
            {
                if (fs != null)
                    foreach (var ent in fs) Release(EntryBytes(ent));
                return result;
            }
        }

        private static long EntryBytes(FailEntry ent) => 64 + 4L * (ent.Regs.Length + ent.Prev.Length + ent.Via.Length);

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
                    if (fs != null)
                    {
                        var ent = new FailEntry(pos, cand, (int[])pm.Clone(), (int[])prev.Clone(), via.Snapshot());
                        Hold(EntryBytes(ent));
                        fs.Push(ent);
                    }
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
                        Spend(naccepted);
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

        private readonly record struct State(int Node, int Pos, int[] Regs);

        private sealed class StateComparer : IEqualityComparer<State>
        {
            internal static readonly StateComparer Instance = new();

            public bool Equals(State a, State b) =>
                a.Node == b.Node && a.Pos == b.Pos &&
                (ReferenceEquals(a.Regs, b.Regs) || a.Regs.AsSpan().SequenceEqual(b.Regs));

            public int GetHashCode(State st)
            {
                var h = new HashCode();
                h.Add(st.Node);
                h.Add(st.Pos);
                foreach (int r in st.Regs) h.Add(r);
                return h.ToHashCode();
            }
        }

        /// <summary>
        /// The exact search's states (node, position, spans of the referenced groups), each once, and its edges kept
        /// reversed: which states lead to which. The states persist across starts.
        /// </summary>
        private sealed class StateGraph
        {
            internal readonly Dictionary<State, int> Index = new(StateComparer.Instance);
            internal readonly List<State> States = [];
            internal readonly List<bool> Expanded = [];
            internal readonly List<int> FirstIn = [];   // per state: its first incoming edge, -1 none
            internal readonly List<int> EdgeFrom = [];
            internal readonly List<int> NextIn = [];    // per edge: the next incoming edge of the same state
            internal readonly List<int> EndStates = [];
        }

        // What one state holds (its index entry, list slots, stack slot), and one edge.
        private const long StateBytes = 96;
        private const long EdgeBytes = 8;

        /// <summary>
        /// Leftmost start, longest end, checked exactly. <see cref="Live"/> names the starts a match is possible from
        /// (a reference counting as any length); from each, leftmost first, <see cref="Ends"/> searches exactly, each
        /// state once (the states persist across starts: none of an earlier start's reached the end, so the search is
        /// bounded by the distinct states, not starts × states). Then, longest end first, the states on a real path
        /// to that end (<see cref="ExactViability"/>, glibc's sifted states) steer glibc's fail-stack walk to the
        /// groups.
        /// </summary>
        internal int[]? SearchWithBackrefs()
        {
            var live = Live(out var starts, out bool full);
            if (live == null) return null;
            var prune = full ? live : null;
            var graph = new StateGraph();
            var stack = new Stack<int>();
            Hold(_n + 1L);
            var endMark = new bool[_n + 1];
            var ends = new List<int>();
            var unset = new int[2 * p._nref];
            Array.Fill(unset, -1);
            for (int s = 0; s <= _n; s++)
            {
                if (starts[s >> 6] == 0)
                {
                    Spend(1);
                    s |= 63;
                    continue;
                }
                if ((starts[s >> 6] >> (s & 63) & 1) == 0) continue;
                Ends(s, prune, graph, stack, endMark, ends, unset);
                if (ends.Count == 0) continue;
                ends.Sort();
                for (int k = ends.Count - 1; k >= 0; k--)
                {
                    int e = ends[k];
                    long before = _memory;
                    var v = ExactViability(s, e, graph);
                    var regs = Test(v, 0, p._init) ? Walk(s, e, v, failStack: true) : null;
                    Release(_memory - before);
                    if (regs != null) return regs;
                }
                // A real match the walk cannot retrace: its span, no groups.
                var whole = new int[2 * (p._nsub + 1)];
                Array.Fill(whole, -1);
                whole[0] = s;
                whole[1] = ends[^1];
                return whole;
            }
            return null;
        }

        /// <summary>
        /// One backward pass, a back-reference matching any length: row <c>pos</c> holds the nodes from which, at
        /// <c>pos</c>, a match can still end somewhere. Bit <c>pos</c> of <paramref name="starts"/>: a match can
        /// start there. All rows are kept when they fit (<paramref name="full"/>: the search prunes with them),
        /// else two. Null: no start.
        /// </summary>
        private ulong[]? Live(out ulong[] starts, out bool full)
        {
            full = (_n + 1L) * _words <= MaxViabilityWords;
            int rows = full ? _n + 1 : 2;
            Hold(8L * rows * _words);
            var v = new ulong[rows * _words];
            int startWords = (_n >> 6) + 1;
            Hold(8L * startWords);
            starts = new ulong[startWords];
            var after = new ulong[_words];
            var queue = new int[_m];
            bool any = false;
            for (int pos = _n; pos >= 0; pos--)
            {
                int row = full ? pos : pos & 1;
                if (!full) Array.Clear(v, row * _words, _words);
                int qn = 0;
                Mark(v, row, p._end);
                queue[qn++] = p._end;
                if (pos < _n)
                {
                    int nextRow = full ? pos + 1 : (pos + 1) & 1;
                    foreach (int node in p._consuming)
                        if (Accepts(node, pos) && Test(v, nextRow, p._next[node]))
                        {
                            Mark(v, row, node);
                            queue[qn++] = node;
                        }
                }
                foreach (int b in p._backrefs)
                {
                    int nx = p._next[b];
                    if ((after[nx >> 6] >> (nx & 63) & 1) != 0 && !Test(v, row, b))
                    {
                        Mark(v, row, b);
                        queue[qn++] = b;
                    }
                }
                Close(v, row, pos, queue, qn, backrefs: true);
                for (int w = 0; w < _words; w++) after[w] |= v[row * _words + w];
                if (Test(v, row, p._init))
                {
                    starts[pos >> 6] |= 1UL << (pos & 63);
                    any = true;
                }
                Spend(_m + _words);
            }
            return any ? v : null;
        }

        /// <summary>
        /// The ends of the matches from <paramref name="s"/>, exactly (a reference compares the text its group took),
        /// into <paramref name="ends"/>; the states and edges into <paramref name="g"/>. Each state is expanded once,
        /// and none outside <paramref name="live"/>.
        /// </summary>
        private void Ends(int s, ulong[]? live, StateGraph g, Stack<int> stack, bool[] endMark, List<int> ends,
            int[] unset)
        {
            long regsBytes = 24 + 8L * p._nref;
            stack.Push(Intern(new State(p._init, s, unset)));
            while (stack.Count > 0)
            {
                int at = stack.Pop();
                if (g.Expanded[at]) continue;
                g.Expanded[at] = true;
                Spend(1);
                var st = g.States[at];
                int x = st.Node, pos = st.Pos;
                var regs = st.Regs;
                switch (p._kind[x])
                {
                    case EreKind.End:
                        g.EndStates.Add(at);
                        if (!endMark[pos])
                        {
                            endMark[pos] = true;
                            ends.Add(pos);
                        }
                        // Nothing ends later than the subject.
                        if (pos == _n) stack.Clear();
                        break;
                    case EreKind.Anchor:
                        if (Holds(p._value[x], pos)) Push(at, p._edests[x][0], pos, regs);
                        break;
                    case EreKind.Alt:
                    case EreKind.Star:
                        foreach (int d in p._edests[x]) Push(at, d, pos, regs);
                        break;
                    case EreKind.OpenSub:
                    case EreKind.CloseSub:
                        int slot = p._refSlot[p._value[x]];
                        if (slot >= 0)
                        {
                            Hold(regsBytes);
                            var copy = (int[])regs.Clone();
                            if (p._kind[x] == EreKind.OpenSub)
                            {
                                copy[2 * slot] = pos;
                                copy[2 * slot + 1] = -1;
                            }
                            else copy[2 * slot + 1] = pos;
                            regs = copy;
                        }
                        Push(at, p._edests[x][0], pos, regs);
                        break;
                    case EreKind.BackRef:
                        int r = p._refSlot[p._value[x]];
                        int so = regs[2 * r], eo = regs[2 * r + 1];
                        if (so < 0 || eo < so) break;
                        int len = eo - so;
                        if (len > _n - pos) break;
                        Spend(len);
                        if (!cps.AsSpan(so, len).SequenceEqual(cps.AsSpan(pos, len))) break;
                        Push(at, p._next[x], pos + len, regs);
                        break;
                    default:
                        if (Accepts(x, pos)) Push(at, p._next[x], pos + 1, regs);
                        break;
                }
            }

            void Push(int from, int node, int pos, int[] regs)
            {
                if (live != null && !Test(live, pos, node)) return;
                int to = Intern(new State(node, pos, regs));
                Hold(EdgeBytes);
                g.EdgeFrom.Add(from);
                g.NextIn.Add(g.FirstIn[to]);
                g.FirstIn[to] = g.EdgeFrom.Count - 1;
                if (!g.Expanded[to]) stack.Push(to);
            }

            int Intern(State st)
            {
                if (g.Index.TryGetValue(st, out int i)) return i;
                if (g.States.Count >= MaxStates) throw new EreBudgetException();
                Hold(StateBytes);
                i = g.States.Count;
                g.Index[st] = i;
                g.States.Add(st);
                g.Expanded.Add(false);
                g.FirstIn.Add(-1);
                return i;
            }
        }

        /// <summary>
        /// glibc's sifted states for the match [s, e): row <c>pos - s</c> holds the nodes of the states on a real path
        /// from <paramref name="s"/> to the end at <paramref name="e"/> (backward over the search's edges), so the
        /// walk never follows a reference-as-any-length route the text does not allow.
        /// </summary>
        private ulong[] ExactViability(int s, int e, StateGraph g)
        {
            int rows = e - s + 1;
            if ((long)rows * _words > MaxViabilityWords) throw new EreBudgetException();
            Hold(8L * rows * _words);
            var v = new ulong[rows * _words];
            int count = g.States.Count;
            Hold(5L * count); // seen, and the queue at its largest
            var seen = new bool[count];
            var queue = new Queue<int>();
            foreach (int at in g.EndStates)
                if (g.States[at].Pos == e)
                {
                    seen[at] = true;
                    queue.Enqueue(at);
                }
            while (queue.Count > 0)
            {
                int at = queue.Dequeue();
                Spend(1);
                var st = g.States[at];
                if (st.Pos >= s && st.Pos <= e) Mark(v, st.Pos - s, st.Node);
                for (int edge = g.FirstIn[at]; edge >= 0; edge = g.NextIn[edge])
                {
                    int from = g.EdgeFrom[edge];
                    if (seen[from]) continue;
                    seen[from] = true;
                    queue.Enqueue(from);
                }
            }
            return v;
        }
    }
}
