using System.Text;
using System.Text.RegularExpressions;
using static PsBash.Cmdlets.InvokeBashSedCommand;

namespace PsBash.Cmdlets;

/// <summary>
/// The sed cycle engine as a PUSH state machine: <see cref="Feed"/> hands it one input record at a time and it
/// emits output records as soon as a cycle completes, so retained memory is the pattern space plus a one-record
/// lookahead — never the stream. It is a line-for-line port of the whole-input <c>ProcessLines</c> it replaces
/// (same command semantics, same quirks: <c>N</c> does not advance the line number, a <c>D</c> restart discards the
/// aborted pass's queued output, a <c>c</c> over a range prints once at the range's last line), proven equal over
/// random scripts and inputs by <c>SedEngineEquivalenceTests</c>.
///
/// <para>Whole-input lookups became state. <c>$</c> needs "is there another record": the engine answers from its
/// lookahead queue, and when it cannot yet know (record not arrived, input not ended) the cycle PAUSES at that
/// command and resumes on the next <see cref="Feed"/> / <see cref="Finish"/> (every pause point is idempotent: it
/// re-evaluates the same command). <c>N</c> pauses the same way. The regex / numeric-to-regex ranges the old code
/// recomputed by rescanning <c>allLines</c> for every line are tracked incrementally per command
/// (<see cref="RangeState"/>), advanced over EVERY raw input record in order — including records an <c>N</c>
/// consumed — because the old rescan saw all of them.</para>
/// </summary>
internal sealed class SedEngine
{
    private readonly List<SedCommand> _cmds;
    private readonly bool _suppress;
    private readonly Action<string> _emit;

    // ---- input side ----
    private readonly Queue<string> _in = new();
    private bool _eof;
    private int _pulled;          // raw records handed to the cycle so far (cycle starts + N pulls), 1-based count

    // ---- per-command address state (null where the address needs none) ----
    private sealed class RangeState
    {
        public bool Active;             // RangeRegex: inside the range after the last raw line
        public bool CycleInRange;       // RangeRegex: in range AT the current cycle's start line
        public bool ActiveAfterStart;   // RangeRegex: Active as of the end of the cycle's start line
        public int ClosedAt;            // RangeNumToRegex: first line that closed the range (0 = still open)
    }
    private readonly RangeState?[] _range;

    // ---- cycle state ----
    private enum Stage { Start, Commands, Done }
    private Stage _stage = Stage.Start;
    private string _ps = "";
    private int _lineNum;
    private string? _rawNext;     // raw record lineNum+1 when an N already consumed it
    private bool _quit;
    private int _ci;
    private List<string> _printed = new(), _append = new(), _insert = new();
    private bool _deleted, _restart, _paused;

    public SedEngine(List<SedCommand> commands, bool suppressDefault, Action<string> emit)
    {
        _cmds = commands;
        _suppress = suppressDefault;
        _emit = emit;
        _range = new RangeState?[commands.Count];
        for (int i = 0; i < commands.Count; i++)
        {
            var t = commands[i].Address?.Type;
            if (t is AddressType.RangeRegex or AddressType.RangeNumToRegex) _range[i] = new RangeState();
        }
    }

    /// <summary>True once the script quit (<c>q</c>/<c>Q</c>, or the input ended): nothing more will be emitted.</summary>
    public bool Done => _stage == Stage.Done;

    /// <summary>True when the script quit before the input ended (so input remains unread).</summary>
    public bool QuitEarly { get; private set; }

    public void Feed(string line)
    {
        if (_stage == Stage.Done) return;
        _in.Enqueue(line);
        Pump();
    }

    public void Finish()
    {
        _eof = true;
        Pump();
    }

    private void Pump()
    {
        while (true)
        {
            if (_stage == Stage.Done) return;
            if (_stage == Stage.Start)
            {
                if (_in.Count == 0)
                {
                    if (_eof) _stage = Stage.Done;
                    return;
                }
                _ps = _in.Dequeue();
                _pulled++;
                _lineNum = _pulled;
                _rawNext = null;
                _quit = false;
                ObserveRaw(_ps, cycleStart: true);
                BeginPass();
                _stage = Stage.Commands;
            }

            if (!RunCommands()) return; // paused: wait for more input

            if (_restart)
            {
                BeginPass();
                continue;
            }
            FlushPass();
            if (_quit)
            {
                QuitEarly = _in.Count > 0 || !_eof;
                _stage = Stage.Done;
                return;
            }
            _stage = Stage.Start;
        }
    }

    private void BeginPass()
    {
        _ci = 0;
        _printed = new List<string>();
        _append = new List<string>();
        _insert = new List<string>();
        _deleted = false;
        _restart = false;
    }

    private void FlushPass()
    {
        foreach (var t in _insert) _emit(t);
        foreach (var p in _printed) _emit(p);
        if (!_deleted && !_suppress)
        {
            if (_ps.Contains('\n'))
            {
                foreach (var psLine in _ps.Split('\n')) _emit(psLine);
            }
            else
            {
                _emit(_ps);
            }
        }
        foreach (var a in _append) _emit(a);
    }

    /// <summary>Runs the commands from <see cref="_ci"/>; false = paused for input (state kept for the retry).</summary>
    private bool RunCommands()
    {
        while (_ci < _cmds.Count)
        {
            var cmd = _cmds[_ci];
            if (_deleted) break;
            if (_quit && cmd.Type != 'q' && cmd.Type != 'Q') { _ci++; continue; }

            int nl = _ps.IndexOf('\n');
            string firstLine = nl >= 0 ? _ps.Substring(0, nl) : _ps;

            bool matched = TestAddress(_ci, cmd, firstLine);
            if (_paused) { _paused = false; return false; }
            if (cmd.Negate) matched = !matched;
            if (!matched) { _ci++; continue; }

            switch (cmd.Type)
            {
                case 's':
                {
                    int target = cmd.Nth > 0 ? cmd.Nth : 1;
                    int count = 0;
                    bool subbed = false;
                    _ps = cmd.Regex!.Replace(_ps, m =>
                    {
                        count++;
                        bool hit = cmd.Global ? count >= target : count == target;
                        if (hit) subbed = true;
                        return hit ? m.Result(cmd.Replacement!) : m.Value;
                    });
                    if (cmd.PrintOnSub && subbed) _printed.Add(_ps);
                    break;
                }
                case 'd':
                    _deleted = true;
                    break;
                case 'D':
                {
                    int nlIdx = _ps.IndexOf('\n');
                    if (nlIdx >= 0)
                    {
                        _ps = _ps.Substring(nlIdx + 1);
                    }
                    else
                    {
                        _deleted = true;
                        _ps = string.Empty;
                    }
                    if (!_deleted && _ps.Length > 0) _restart = true;
                    break;
                }
                case 'p':
                    _printed.Add(_ps);
                    break;
                case 'P':
                {
                    int nlIdx = _ps.IndexOf('\n');
                    _printed.Add(nlIdx >= 0 ? _ps.Substring(0, nlIdx) : _ps);
                    break;
                }
                case 'N':
                    if (_in.Count > 0)
                    {
                        string next = _in.Dequeue();
                        _pulled++;
                        if (_pulled == _lineNum + 1) _rawNext = next;
                        ObserveRaw(next, cycleStart: false);
                        _ps += "\n" + next;
                    }
                    else if (_eof)
                    {
                        // No next line: GNU ends the run here without executing later commands; auto-print
                        // still applies (suppressed by -n). Later commands are skipped via quit.
                        _quit = true;
                    }
                    else
                    {
                        return false; // wait for the next record
                    }
                    break;
                case 'q':
                    _quit = true;
                    break;
                case 'Q':
                    _quit = true;
                    _deleted = true;
                    break;
                case '=':
                    _printed.Add(_lineNum.ToString());
                    break;
                case 'a':
                    _append.Add(cmd.Text!);
                    break;
                case 'i':
                    _insert.Add(cmd.Text!);
                    break;
                case 'c':
                {
                    // A range `c` prints its text ONCE for the whole range, at its final line.
                    bool continues = RangeContinuesPast(_ci, cmd);
                    if (_paused) { _paused = false; return false; }
                    _deleted = true;
                    if (!continues) _append.Add(cmd.Text!);
                    break;
                }
                case 'y':
                {
                    var sb = new StringBuilder(_ps.Length);
                    foreach (char ch in _ps)
                    {
                        int idx = cmd.Source!.IndexOf(ch);
                        sb.Append(idx >= 0 ? cmd.Dest![idx] : ch);
                    }
                    _ps = sb.ToString();
                    break;
                }
            }

            if (_restart) return true;
            _ci++;
        }
        return true;
    }

    // ---- addresses --------------------------------------------------------------------------------------

    /// <summary>Advance every range state over one raw input record (cycle start or N pull), in input order.</summary>
    private void ObserveRaw(string raw, bool cycleStart)
    {
        for (int i = 0; i < _range.Length; i++)
        {
            var st = _range[i];
            if (st is null) continue;
            var addr = _cmds[i].Address!;
            if (addr.Type == AddressType.RangeRegex)
            {
                if (!st.Active && Regex.IsMatch(raw, addr.StartPattern!)) st.Active = true;
                bool inRange = st.Active;
                if (st.Active && Regex.IsMatch(raw, addr.EndPattern!)) st.Active = false;
                if (cycleStart)
                {
                    st.CycleInRange = inRange;
                    st.ActiveAfterStart = st.Active;
                }
            }
            else // RangeNumToRegex (N,/re/ and the 0,/re/ idiom)
            {
                int begin = Math.Max(addr.Start, 1);
                bool canEndOnStart = addr.Start == 0;
                if (st.ClosedAt == 0 && _pulled >= begin && (_pulled > begin || canEndOnStart)
                    && Regex.IsMatch(raw, addr.EndPattern!))
                {
                    st.ClosedAt = _pulled;
                }
            }
        }
    }

    private bool TestAddress(int ci, SedCommand cmd, string line)
    {
        var addr = cmd.Address;
        if (addr == null) return true;
        int lineNum = _lineNum;

        switch (addr.Type)
        {
            case AddressType.Regex:
                return Regex.IsMatch(line, addr.Pattern!);
            case AddressType.Line:
                return lineNum == addr.Line;
            case AddressType.Last:
                // "this is the last record": nothing was pulled past it and the input has ended.
                if (lineNum != _pulled) return false;
                if (_in.Count > 0) return false;
                if (!_eof) { _paused = true; return false; }
                return true;
            case AddressType.RangeNum:
                return lineNum >= addr.Start && lineNum <= addr.End;
            case AddressType.Step:
                if (addr.Step <= 0) return lineNum == addr.Start;
                return lineNum >= Math.Max(addr.Start, 1) && (lineNum - addr.Start) % addr.Step == 0;
            case AddressType.RangeNumToRegex:
            {
                int begin = Math.Max(addr.Start, 1);
                if (lineNum < begin) return false;
                int closed = _range[ci]!.ClosedAt;
                return closed == 0 || lineNum <= closed;
            }
            case AddressType.RangeRegex:
                return _range[ci]!.CycleInRange;
        }
        return false;
    }

    /// <summary>
    /// True when a <c>c</c> command's address is a RANGE and the record after the current one is still inside
    /// it. Pauses (sets <see cref="_paused"/>) while it is not yet known whether that record exists.
    /// </summary>
    private bool RangeContinuesPast(int ci, SedCommand cmd)
    {
        var addr = cmd.Address;
        if (addr is null) return false;
        if (addr.Type is not (AddressType.RangeNum or AddressType.RangeRegex or AddressType.RangeNumToRegex))
            return false;

        string? next = _rawNext ?? (_in.Count > 0 ? _in.Peek() : null);
        if (next is null)
        {
            if (!_eof) _paused = true;
            return false;
        }

        int nextNum = _lineNum + 1;
        bool nextMatched;
        switch (addr.Type)
        {
            case AddressType.RangeNum:
                nextMatched = nextNum >= addr.Start && nextNum <= addr.End;
                break;
            case AddressType.RangeRegex:
                nextMatched = _range[ci]!.ActiveAfterStart || Regex.IsMatch(next, addr.StartPattern!);
                break;
            default: // RangeNumToRegex
            {
                int begin = Math.Max(addr.Start, 1);
                int closed = _range[ci]!.ClosedAt;
                nextMatched = nextNum >= begin && (closed == 0 || closed >= nextNum);
                break;
            }
        }
        return cmd.Negate ? !nextMatched : nextMatched;
    }

    // ---- pull-style adapter (the fused lane) ------------------------------------------------------------

    /// <summary>Lazy transform of an input record sequence: output appears as input arrives, and stops reading on quit.</summary>
    public static IEnumerable<string> Run(IEnumerable<string> input, List<SedCommand> commands, bool suppressDefault)
    {
        var outq = new Queue<string>();
        var engine = new SedEngine(commands, suppressDefault, outq.Enqueue);
        foreach (var line in input)
        {
            engine.Feed(line);
            while (outq.Count > 0) yield return outq.Dequeue();
            if (engine.Done) yield break;
        }
        engine.Finish();
        while (outq.Count > 0) yield return outq.Dequeue();
    }

    /// <summary>Whole-array convenience (tests and callers that already hold the lines).</summary>
    public static List<string> ProcessLines(string[] inputLines, List<SedCommand> commands, bool suppressDefault)
        => new(Run(inputLines, commands, suppressDefault));
}
