using System.Text;
using System.Text.RegularExpressions;
using PsBash.Core;
using static PsBash.Cmdlets.InvokeBashSedCommand;

namespace PsBash.Cmdlets;

/// <summary>
/// How one sed output record ends — GNU's rule for an input whose last line has no newline. The missing newline
/// belongs to the BUFFER that holds that line (GNU's <c>line.chomped</c>), not to "the last output record": only a
/// write of the pattern/hold space (<c>p P w W s///p s///w</c>, the autoprint) leaves it out, and the next write to
/// the same output supplies it first. Everything else sed writes ends in its own newline (<c>= l F i a c</c>), and
/// <c>r R e</c> copy bytes verbatim. REGRESSION: the writer gave the final record the input's terminator state
/// whatever it was, so <c>printf 'a\nb' | sed -n '$='</c> printed <c>2</c> with no newline (GNU <c>2\n</c>), and
/// <c>$a\ X</c>, <c>$c\ X</c>, <c>$l</c>, <c>1p</c>, <c>$r f</c> on such input lost or gained a newline too
/// (spec <c>runtime-functions.md</c> §Partial lines).
/// </summary>
internal enum SedEnd
{
    /// <summary>Ends in a newline.</summary>
    Newline,
    /// <summary>A buffer holding an input line that had no newline: written without one, and OWED — the next write to
    /// the same output first supplies it (GNU <c>output_missing_newline</c>).</summary>
    Owed,
    /// <summary>Raw bytes that do not end in a newline (<c>r</c>/<c>R</c>/<c>e</c> output): nothing is owed after them.</summary>
    Bare,
}

/// <summary>One record of sed output: its text (no terminator) and how it ends.</summary>
internal readonly record struct SedOut(string Text, SedEnd End);

/// <summary>
/// The sed cycle engine as a PUSH state machine: <see cref="Feed"/> hands it one input record at a time and it
/// emits output records as soon as a cycle completes, so retained memory is the pattern space plus a one-record
/// lookahead — never the stream. It is a line-for-line port of the whole-input <c>ProcessLines</c> it replaces
/// (same command semantics, same quirks: <c>N</c> does not advance the line number, a <c>c</c> over a range prints once
/// at the range's last line; a <c>D</c> restart writes the aborted pass's inserts and printed lines and keeps its
/// appends for the cycle's end, as GNU), proven equal over
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
    private readonly Action<string, SedEnd> _emit;
    private readonly SedRuntime? _rt;
    private readonly bool _chrono;

    // ---- input side ----
    private readonly Queue<(string Text, bool Terminated)> _in = new();
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
    private bool _psTerm = true;  // GNU line.chomped: the pattern space prints with its newline (see SedEnd)
    private int _lineNum;
    private string? _rawNext;     // raw record lineNum+1 when an N already consumed it
    private bool _quit;
    private int _ci;
    private List<SedOut> _printed = new(), _append = new(), _insert = new();
    private bool _deleted, _restart, _paused;
    private bool _tflag;          // a substitution succeeded since the last input line / taken `t`
    private bool _announced;      // --debug: the COMMAND: line of command _ci is already out (a paused command re-runs)
    private bool _noBanner;       // --debug: this cycle ends without END-OF-CYCLE: (q, Q, D with no newline)
    private bool _stdoutOwed;     // the last write to stdout owes its newline (SedEnd.Owed)
    private bool _quitWithNewline; // `q` ran: GNU settles stdout's owed newline and autoprints WITH a newline

    public SedEngine(List<SedCommand> commands, bool suppressDefault, Action<string, SedEnd> emit, SedRuntime? runtime = null)
    {
        _cmds = commands;
        _suppress = suppressDefault;
        _emit = (text, end) =>
        {
            _stdoutOwed = end == SedEnd.Owed;
            emit(text, end);
        };
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

    /// <summary>One input record; <paramref name="terminated"/> = false for a last line that had no newline.</summary>
    public void Feed(string line, bool terminated = true)
    {
        if (_stage == Stage.Done) return;
        _in.Enqueue((line, terminated));
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
                (_ps, _psTerm) = _in.Dequeue();
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
                // GNU writes i/p/P/= output as the command runs and dumps the append queue only when a cycle really
                // ends: a D restart flushes the former and keeps the latter (`sed 'N;P;D'` printed only the last line).
                foreach (var t in _insert) _emit(t.Text, t.End);
                foreach (var p in _printed) _emit(p.Text, p.End);
                var appends = _append;
                BeginPass();
                _append = appends;
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
        _printed = new List<SedOut>();
        _append = new List<SedOut>();
        _insert = new List<SedOut>();
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
        foreach (var t in _insert) _emit(t.Text, t.End);
        foreach (var p in _printed) _emit(p.Text, p.End);
        if (_quitWithNewline)
        {
            // GNU `q`: output_missing_newline, then the pattern space with its newline even when the input line had
            // none (`printf 'a\nb' | sed '$q'` is a b + newline; `sed -n '$p;$q'` is b + newline). `Q` does neither.
            if (!_deleted && !_suppress) { _psTerm = true; EmitPatternLines(); }
            else if (_stdoutOwed) _emit(string.Empty, SedEnd.Bare);
        }
        else if (!_deleted && !_suppress) EmitPatternLines();
        foreach (var a in _append) _emit(a.Text, a.End);
    }

    // ---- output helpers: per-cycle order for the original commands, chronological once a new command is used ----

    /// <summary>How a write of the pattern space ends: with its newline, or owing it (an unterminated last line).</summary>
    private SedEnd PsEnd => _psTerm ? SedEnd.Newline : SedEnd.Owed;

    /// <summary>The autoprint: the pattern space one line per record, only its last line inheriting <see cref="PsEnd"/>.</summary>
    private void EmitPatternLines()
    {
        int start = 0;
        for (int nl = _ps.IndexOf('\n'); nl >= 0; nl = _ps.IndexOf('\n', start))
        {
            _emit(_ps.Substring(start, nl - start), SedEnd.Newline);
            start = nl + 1;
        }
        _emit(start == 0 ? _ps : _ps.Substring(start), PsEnd);
    }

    /// <summary>Output of <c>p P = s///p</c>: queued for the cycle's flush (historical) or written at once (GNU).</summary>
    private void Out(string text, SedEnd end)
    {
        if (_chrono) _emit(text, end);
        else _printed.Add(new SedOut(text, end));
    }

    /// <summary>Output of <c>l</c>: possibly several lines, each ending in a newline, always written at once.</summary>
    private void OutLines(string text)
    {
        foreach (var l in text.Split('\n')) _emit(l, SedEnd.Newline);
    }

    /// <summary>The first line of the pattern space, as <c>P</c>/<c>W</c> write it: up to and with the first newline,
    /// or the whole pattern space ending like it (<see cref="PsEnd"/>) when it has none.</summary>
    private SedOut FirstLine()
    {
        int nlIdx = _ps.IndexOf('\n');
        return nlIdx >= 0 ? new SedOut(_ps.Substring(0, nlIdx), SedEnd.Newline) : new SedOut(_ps, PsEnd);
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

            // A regex address matches the WHOLE pattern space (after N/G it holds several lines), as GNU does;
            // testing only its first line hid `N;/b/p` and made the M flag meaningless.
            bool matched = TestAddress(_ci, cmd, _ps);
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
                    // A byte regex (`s/\xce//`) matches the pattern space one char per byte, then it is text again.
                    string subject = cmd.Bytes ? PsBash.Core.RawBytes.ToByteChars(_ps) : _ps;
                    _ps = cmd.Regex!.Replace(subject, m =>
                    {
                        count++;
                        first ??= m;
                        bool hit = cmd.Global ? count >= target : count == target;
                        if (hit) subbed = true;
                        return hit ? m.Result(cmd.Replacement!) : m.Value;
                    });
                    if (cmd.Bytes) _ps = PsBash.Core.RawBytes.FromByteChars(_ps);
                    if (Dbg && first != null) DbgRegisters(first);
                    if (subbed)
                    {
                        _tflag = true;
                        if (cmd.Eval && _rt?.RunCommand != null) _ps = TrimNewline(_rt.RunCommand(_ps));
                    }
                    if (cmd.PrintOnSub && subbed) Out(_ps, PsEnd);
                    if (subbed && cmd.FileName != null) WriteTo(cmd.FileName, new SedOut(_ps, PsEnd));
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
                    Out(_ps, PsEnd);
                    break;
                case 'P':
                {
                    var first = FirstLine();
                    Out(first.Text, first.End);
                    break;
                }
                case 'N':
                    if (_in.Count > 0)
                    {
                        var (next, nextTerm) = _in.Dequeue();
                        _pulled++;
                        if (_pulled == _lineNum + 1) _rawNext = next;
                        ObserveRaw(next, cycleStart: false);
                        _ps += "\n" + next;
                        _psTerm = nextTerm;
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
                        _printed = new List<SedOut>();
                        _append = new List<SedOut>();
                        _insert = new List<SedOut>();
                        (_ps, _psTerm) = _in.Dequeue();
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
                        if (!_suppress) EmitPatternLines();
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
                    _quitWithNewline = true;
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
                    Out(_lineNum.ToString(), SedEnd.Newline);
                    break;
                case 'a':
                    _append.Add(new SedOut(cmd.Text!, SedEnd.Newline));
                    break;
                case 'i':
                    if (_chrono) _emit(cmd.Text!, SedEnd.Newline);
                    else _insert.Add(new SedOut(cmd.Text!, SedEnd.Newline));
                    break;
                case 'c':
                {
                    // A range `c` prints its text ONCE for the whole range, at its final line.
                    bool continues = RangeContinuesPast(_ci, cmd);
                    if (_paused) { _paused = false; return false; }
                    _deleted = true;
                    if (!continues)
                    {
                        if (_chrono) _emit(cmd.Text!, SedEnd.Newline);
                        else _append.Add(new SedOut(cmd.Text!, SedEnd.Newline));
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
                // The buffer's terminated state travels with its text (GNU copies `chomped` on every one of these).
                case 'h':
                    _rt!.Hold = _ps;
                    _rt.HoldTerminated = _psTerm;
                    if (Dbg) DbgHold();
                    break;
                case 'H':
                    _rt!.Hold += "\n" + _ps;
                    _rt.HoldTerminated = _psTerm;
                    if (Dbg) DbgHold();
                    break;
                case 'g':
                    _ps = _rt!.Hold;
                    _psTerm = _rt.HoldTerminated;
                    if (Dbg) DbgHold();
                    break;
                case 'G':
                    _ps += "\n" + _rt!.Hold;
                    _psTerm = _rt.HoldTerminated;
                    if (Dbg) DbgPattern();
                    break;
                case 'x':
                {
                    (_ps, _rt!.Hold) = (_rt.Hold, _ps);
                    (_psTerm, _rt.HoldTerminated) = (_rt.HoldTerminated, _psTerm);
                    if (Dbg) { DbgPattern(); DbgHold(); }
                    break;
                }
                case 'z':
                    _ps = "";
                    if (Dbg) DbgPattern();
                    break;

                // ---- output ----
                case 'F':
                    _emit(_rt!.FileName, SedEnd.Newline);
                    break;
                case 'l':
                    OutLines(SedDebugFormat.List(_ps, cmd.IntArg >= 0 ? cmd.IntArg : _rt!.LineLength));
                    break;
                case 'r':
                {
                    string? text = _rt?.ReadFile?.Invoke(cmd.FileName!);
                    if (text != null) _append.AddRange(RawOutput(text));
                    break;
                }
                case 'R':
                {
                    var line = _rt?.ReadLine?.Invoke(cmd.FileName!);
                    if (line is { } l) _append.Add(new SedOut(l.Text, l.HasTrailingNewline ? SedEnd.Newline : SedEnd.Bare));
                    break;
                }
                case 'w':
                    WriteTo(cmd.FileName!, new SedOut(_ps, PsEnd));
                    break;
                case 'W':
                    WriteTo(cmd.FileName!, FirstLine());
                    break;
                case 'e':
                    if (_rt?.RunCommand != null)
                    {
                        if (cmd.Command!.Length > 0)
                        {
                            // `e COMMAND`: run it, its output goes out before the pattern space.
                            foreach (var o in RawOutput(_rt.RunCommand(cmd.Command))) _emit(o.Text, o.End);
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

    /// <summary>Bytes sed copies verbatim (<c>r</c> file, <c>e COMMAND</c> output) as output records: a final newline ends
    /// the last line instead of starting an empty one, and a last line without one is <see cref="SedEnd.Bare"/>.</summary>
    private static IEnumerable<SedOut> RawOutput(string text)
    {
        if (text.Length == 0) yield break;
        var lines = BashRuntime.SplitBytes(text.Replace("\r\n", "\n"), out bool unterminated);
        for (int i = 0; i < lines.Length; i++)
            yield return new SedOut(lines[i], unterminated && i == lines.Length - 1 ? SedEnd.Bare : SedEnd.Newline);
    }

    /// <summary><c>w FILE</c> (and <c>s///w</c>): <c>/dev/stdout</c> is the output stream itself, other names go to the runtime.</summary>
    private void WriteTo(string name, SedOut record)
    {
        if (DevicePath.Classify(name) == DeviceKind.Stdout) { _emit(record.Text, record.End); return; }
        _rt?.WriteFile?.Invoke(name, record.Text, record.End);
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
                if (!st.Active && addr.StartRx!.IsMatch(raw)) st.Active = true;
                bool inRange = st.Active;
                if (st.Active && (addr.EndRx is null ? _pulled >= addr.End : addr.EndRx.IsMatch(raw))) st.Active = false;
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
                    && addr.EndRx!.IsMatch(raw))
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
                return addr.Rx!.IsMatch(line);
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

        string? next = _rawNext ?? (_in.Count > 0 ? _in.Peek().Text : null);
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
                nextMatched = _range[ci]!.ActiveAfterStart || addr.StartRx!.IsMatch(next);
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
        // The fused lane feeds only terminated lines and declines r/R/e, so every record ends in a newline here.
        // An empty Bare write (q settling an owed newline) is no line.
        var engine = new SedEngine(commands, suppressDefault, (text, end) =>
        {
            if (text.Length > 0 || end != SedEnd.Bare) outq.Enqueue(text);
        });
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
