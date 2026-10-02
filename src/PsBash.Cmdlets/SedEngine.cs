using System.Text;
using System.Text.RegularExpressions;
using PsBash.Core;
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
///
/// <para><b>The rest of GNU's command set</b> (blocks <c>{ }</c>, <c>: b t T</c>, hold space <c>h H g G x</c>,
/// <c>n</c>, <c>z</c>, <c>F</c>, <c>l</c>, <c>r R w W</c>, <c>e</c>, <c>s///w</c>/<c>s///e</c>, and <c>--debug</c>) runs in
/// the same loop through a <see cref="SedRuntime"/>. A script that uses any of them runs CHRONOLOGICALLY — output
/// of <c>p = i l F e c</c> appears at the moment the command runs, as in GNU — and a script made only of the
/// original commands keeps the historical per-cycle order (inserts, then printed lines, then the pattern space,
/// then appends) that the equivalence test pins.</para>
/// </summary>
internal sealed class SedEngine
{
    private readonly List<SedCommand> _cmds;
    private readonly bool _suppress;
    private readonly Action<string> _emit;
    private readonly SedRuntime? _rt;
    private readonly bool _chrono;

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
    private bool _tflag;          // a substitution succeeded since the last input line / taken `t`
    private bool _announced;      // --debug: the COMMAND: line of command _ci is already out (a paused command re-runs)
    private bool _noBanner;       // --debug: this cycle ends without END-OF-CYCLE: (q, Q, D with no newline)

    public SedEngine(List<SedCommand> commands, bool suppressDefault, Action<string> emit, SedRuntime? runtime = null)
    {
        _cmds = commands;
        _suppress = suppressDefault;
        _emit = emit;
        _chrono = runtime?.Debug != null || UsesNewCommands(commands);
        _rt = runtime ?? (_chrono ? new SedRuntime() : null);
        _range = new RangeState?[commands.Count];
        for (int i = 0; i < commands.Count; i++)
        {
            var t = commands[i].Address?.Type;
            if (t is AddressType.RangeRegex or AddressType.RangeNumToRegex) _range[i] = new RangeState();
        }
    }

    /// <summary>The commands the engine ran before blocks, labels and the hold space existed.</summary>
    private static bool IsLegacyType(char t) =>
        t is 's' or 'd' or 'D' or 'p' or 'P' or 'N' or '=' or 'q' or 'Q' or 'a' or 'i' or 'c' or 'y';

    /// <summary>True when the script uses a command (or an <c>s</c> flag) beyond the original set.</summary>
    internal static bool UsesNewCommands(List<SedCommand> commands)
    {
        foreach (var c in commands)
        {
            if (!IsLegacyType(c.Type)) return true;
            if (c.Type == 's' && (c.FileName != null || c.Eval)) return true;
        }
        return false;
    }

    /// <summary>True when the script needs a <see cref="SedRuntime"/>: a new command, or a <c>q</c>/<c>Q</c> exit status.</summary>
    internal static bool UsesRuntimeFeatures(List<SedCommand> commands)
    {
        if (UsesNewCommands(commands)) return true;
        foreach (var c in commands)
            if (c.Type is 'q' or 'Q' && c.ExitCode != 0) return true;
        return false;
    }

    /// <summary>True once the script quit (<c>q</c>/<c>Q</c>, or the input ended): nothing more will be emitted.</summary>
    public bool Done => _stage == Stage.Done;

    /// <summary>True when the script quit before the input ended (so input remains unread).</summary>
    public bool QuitEarly { get; private set; }

    /// <summary>True when a <c>q</c>/<c>Q</c> ended the script.</summary>
    public bool Quit { get; private set; }

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

    private bool Dbg => _rt?.Debug != null;

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
                _tflag = false;
                ObserveRaw(_ps, cycleStart: true);
                if (Dbg)
                {
                    _rt!.Debug!($"INPUT:   '{(_rt.FileName == "-" ? "STDIN" : _rt.FileName)}' line {_pulled}");
                    DbgPattern();
                }
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
        _announced = false;
        _printed = new List<string>();
        _append = new List<string>();
        _insert = new List<string>();
        _deleted = false;
        _restart = false;
        _noBanner = false;
    }

    private void FlushPass()
    {
        if (Dbg && !_noBanner) _rt!.Debug!("END-OF-CYCLE:");
        FlushOutput();
    }

    /// <summary>The cycle's queued output in order: inserts, printed lines, the autoprint, then appends.</summary>
    private void FlushOutput()
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

    // ---- output helpers: per-cycle order for the original commands, chronological once a new command is used ----

    /// <summary>Output of <c>p P = s///p</c>: queued for the cycle's flush (historical) or written at once (GNU).</summary>
    private void Out(string text)
    {
        if (_chrono) _emit(text);
        else _printed.Add(text);
    }

    /// <summary>Output of <c>l F e</c>: possibly several lines, always written at once.</summary>
    private void OutLines(string text)
    {
        foreach (var l in text.Split('\n')) _emit(l);
    }

    private void Next() { _ci++; _announced = false; }

    private void GoTo(int index) { _ci = index; _announced = false; }

    // ---- --debug ----

    private void DbgPattern() => _rt!.Debug!("PATTERN: " + SedDebugFormat.EscapeText(_ps));

    private void DbgHold() => _rt!.Debug!("HOLD:    " + SedDebugFormat.EscapeText(_rt.Hold));

    private void DbgRegisters(Match m)
    {
        _rt!.Debug!("MATCHED REGEX REGISTERS");
        for (int g = 0; g < m.Groups.Count; g++)
        {
            var grp = m.Groups[g];
            if (!grp.Success) break;
            int start = RawBytes.GetByteCount(_psBeforeSubst.AsSpan(0, grp.Index).ToString());
            int end = start + RawBytes.GetByteCount(grp.Value);
            _rt.Debug($"  regex[{g}] = {start}-{end} '{SedDebugFormat.EscapeText(grp.Value)}'");
        }
    }

    private string _psBeforeSubst = "";

    // ---- commands ----

    /// <summary>Runs the commands from <see cref="_ci"/>; false = paused for input (state kept for the retry).</summary>
    private bool RunCommands()
    {
        while (_ci < _cmds.Count)
        {
            var cmd = _cmds[_ci];
            if (_deleted) break;
            if (_quit && cmd.Type != 'q' && cmd.Type != 'Q') { Next(); continue; }

            if (Dbg && !_announced)
            {
                SedDebugFormat.PrintCommandLine(cmd, _rt!);
                _announced = true;
            }

            int nl = _ps.IndexOf('\n');
            string firstLine = nl >= 0 ? _ps.Substring(0, nl) : _ps;

            bool matched = TestAddress(_ci, cmd, firstLine);
            if (_paused) { _paused = false; return false; }
            if (cmd.Negate) matched = !matched;
            if (!matched)
            {
                // An unmatched `{` skips to its `}` (which still runs, as a no-op).
                if (cmd.Type == '{') GoTo(cmd.Jump); else Next();
                continue;
            }

            switch (cmd.Type)
            {
                case 's':
                {
                    int target = cmd.Nth > 0 ? cmd.Nth : 1;
                    int count = 0;
                    bool subbed = false;
                    Match? first = null;
                    if (Dbg) _psBeforeSubst = _ps;
                    _ps = cmd.Regex!.Replace(_ps, m =>
                    {
                        count++;
                        first ??= m;
                        bool hit = cmd.Global ? count >= target : count == target;
                        if (hit) subbed = true;
                        return hit ? m.Result(cmd.Replacement!) : m.Value;
                    });
                    if (Dbg && first != null) DbgRegisters(first);
                    if (subbed)
                    {
                        _tflag = true;
                        if (cmd.Eval && _rt?.RunCommand != null) _ps = TrimNewline(_rt.RunCommand(_ps));
                    }
                    if (cmd.PrintOnSub && subbed) Out(_ps);
                    if (subbed && cmd.FileName != null) WriteTo(cmd.FileName, _ps);
                    if (Dbg) DbgPattern();
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
                        _noBanner = true;
                    }
                    if (!_deleted && _ps.Length > 0)
                    {
                        _restart = true;
                        if (Dbg) DbgPattern();
                    }
                    break;
                }
                case 'p':
                    Out(_ps);
                    break;
                case 'P':
                {
                    int nlIdx = _ps.IndexOf('\n');
                    Out(nlIdx >= 0 ? _ps.Substring(0, nlIdx) : _ps);
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
                        // GNU's line counter follows the input; the historical engine kept the cycle's start line.
                        if (_chrono) { _lineNum = _pulled; _rawNext = null; }
                        if (Dbg) DbgPattern();
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
                case 'n':
                    if (_in.Count > 0)
                    {
                        // Print the pattern space (unless -n), flush appends, then replace it by the next line.
                        FlushOutput();
                        _printed = new List<string>();
                        _append = new List<string>();
                        _insert = new List<string>();
                        _ps = _in.Dequeue();
                        _pulled++;
                        _lineNum = _pulled;
                        _rawNext = null;
                        _tflag = false;
                        ObserveRaw(_ps, cycleStart: true);
                        if (Dbg) DbgPattern();
                    }
                    else if (_eof)
                    {
                        // No next input: GNU prints the pattern space (unless -n) and exits without running
                        // the rest of the script.
                        if (!_suppress) OutLines(_ps);
                        _deleted = true;
                        _quit = true;
                    }
                    else
                    {
                        return false; // wait for the next record
                    }
                    break;
                case 'q':
                    _quit = true;
                    Quit = true;
                    _noBanner = true;
                    if (_rt != null) _rt.ExitCode = cmd.ExitCode;
                    break;
                case 'Q':
                    _quit = true;
                    Quit = true;
                    _deleted = true;
                    _noBanner = true;
                    if (_rt != null) _rt.ExitCode = cmd.ExitCode;
                    break;
                case '=':
                    Out(_lineNum.ToString());
                    break;
                case 'a':
                    _append.Add(cmd.Text!);
                    break;
                case 'i':
                    if (_chrono) _emit(cmd.Text!);
                    else _insert.Add(cmd.Text!);
                    break;
                case 'c':
                {
                    // A range `c` prints its text ONCE for the whole range, at its final line.
                    bool continues = RangeContinuesPast(_ci, cmd);
                    if (_paused) { _paused = false; return false; }
                    _deleted = true;
                    if (!continues)
                    {
                        if (_chrono) _emit(cmd.Text!);
                        else _append.Add(cmd.Text!);
                    }
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
                    if (Dbg) DbgPattern();
                    break;
                }

                // ---- control flow ----
                case '{': case '}': case ':': case 'v':
                    break;
                case 'b':
                    GoTo(cmd.Jump);
                    continue;
                case 't':
                    if (_tflag)
                    {
                        _tflag = false;
                        GoTo(cmd.Jump);
                        continue;
                    }
                    break;
                case 'T':
                    if (!_tflag)
                    {
                        GoTo(cmd.Jump);
                        continue;
                    }
                    _tflag = false;
                    break;

                // ---- hold space ----
                case 'h':
                    _rt!.Hold = _ps;
                    if (Dbg) DbgHold();
                    break;
                case 'H':
                    _rt!.Hold += "\n" + _ps;
                    if (Dbg) DbgHold();
                    break;
                case 'g':
                    _ps = _rt!.Hold;
                    if (Dbg) DbgHold();
                    break;
                case 'G':
                    _ps += "\n" + _rt!.Hold;
                    if (Dbg) DbgPattern();
                    break;
                case 'x':
                {
                    string swap = _ps;
                    _ps = _rt!.Hold;
                    _rt.Hold = swap;
                    if (Dbg) { DbgPattern(); DbgHold(); }
                    break;
                }
                case 'z':
                    _ps = "";
                    if (Dbg) DbgPattern();
                    break;

                // ---- output ----
                case 'F':
                    _emit(_rt!.FileName);
                    break;
                case 'l':
                    OutLines(SedDebugFormat.List(_ps, cmd.IntArg >= 0 ? cmd.IntArg : _rt!.LineLength));
                    break;
                case 'r':
                {
                    string? text = _rt?.ReadFile?.Invoke(cmd.FileName!);
                    if (text != null) foreach (var line in FileLines(text)) _append.Add(line);
                    break;
                }
                case 'R':
                {
                    string? line = _rt?.ReadLine?.Invoke(cmd.FileName!);
                    if (line != null) _append.Add(line);
                    break;
                }
                case 'w':
                    WriteTo(cmd.FileName!, _ps);
                    break;
                case 'W':
                {
                    int nlIdx = _ps.IndexOf('\n');
                    WriteTo(cmd.FileName!, nlIdx >= 0 ? _ps.Substring(0, nlIdx) : _ps);
                    break;
                }
                case 'e':
                    if (_rt?.RunCommand != null)
                    {
                        if (cmd.Command!.Length > 0)
                        {
                            // `e COMMAND`: run it, its output goes out before the pattern space.
                            foreach (var line in FileLines(_rt.RunCommand(cmd.Command))) _emit(line);
                        }
                        else
                        {
                            // `e`: run the pattern space as a command and replace it by the output.
                            _ps = TrimNewline(_rt.RunCommand(_ps));
                        }
                    }
                    break;
            }

            if (_restart) return true;
            Next();
        }
        return true;
    }

    private static string TrimNewline(string s) => s.EndsWith('\n') ? s.Substring(0, s.Length - 1) : s;

    /// <summary>The lines of a file's text: a final newline ends the last line instead of starting an empty one.</summary>
    private static IEnumerable<string> FileLines(string text)
    {
        if (text.Length == 0) yield break;
        text = text.Replace("\r\n", "\n");
        if (text.EndsWith('\n')) text = text.Substring(0, text.Length - 1);
        foreach (var l in text.Split('\n')) yield return l;
    }

    /// <summary><c>w FILE</c> (and <c>s///w</c>): <c>/dev/stdout</c> is the output stream itself, other names go to the runtime.</summary>
    private void WriteTo(string name, string text)
    {
        if (name == "/dev/stdout") { _emit(text); return; }
        _rt?.WriteFile?.Invoke(name, text);
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
                // "this is the last record": nothing was pulled past it and the input has ended. GNU asks
                // whether the INPUT is exhausted (after an N that is the pulled line); the historical engine
                // also required the cycle's start line to be the last one, kept for the original command set.
                if (!_chrono && lineNum != _pulled) return false;
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
