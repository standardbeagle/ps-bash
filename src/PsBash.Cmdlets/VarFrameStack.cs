using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.CompilerServices;

namespace PsBash.Cmdlets;

/// <summary>
/// bash's variable contexts for scalar names: what <c>unset</c> and a value-less <c>local</c> do depend on which
/// function made a name local and which temp binding (<c>v=t f</c>, <c>v=t eval ...</c>) is in force. ps-bash keeps a
/// local's caller value in <c>$__lsd_NAME</c> and a temp binding's in <c>$__saved_NAME</c>, which <c>unset</c> cannot
/// see; this stack records the same frames for it. Rules (bash 5.2, the oils <c>[bash_unset]</c> cases):
/// <list type="bullet">
/// <item>A function call's temp binding is owned by the CALLED function (depth + 1); an eval's or builtin's by the
/// current depth.</item>
/// <item><c>local v</c> at depth d: a local of d already -> unchanged; a temp binding of d -> it becomes the local,
/// value kept; a temp binding further out -> a new local inheriting its value; anything else -> a new local, unset.</item>
/// <item><c>unset v</c> at depth d: a local of d -> value unset, still local; a local of an outer function (a helper
/// that unsets its caller's local) or a temp binding -> the frame goes and the value it hid comes back.</item>
/// <item>Leaving a function drops the frames it owns; the emitted code restores their values.</item>
/// </list>
/// A subshell keeps its depth: <c>( unset v )</c> in f is f's local-unset.
/// </summary>
public sealed class VarFrameStack
{
    private sealed class Frame
    {
        public bool Local;
        public int Depth;
        public string? Hidden;
        public long Id;
        /// <summary>A local's frame: whether the name it hides was exported (put back when the function returns).</summary>
        public bool WasExported;
    }

    private readonly Dictionary<string, List<Frame>> _frames = new(StringComparer.Ordinal);
    private long _nextId;

    /// <summary>The shell-function call depth (0 at top level).</summary>
    public int Depth { get; private set; }

    /// <summary>
    /// The source line of the statement running now (set by the emitted code before a statement that calls a function
    /// or may raise a shell diagnostic, and before every statement of a script that reads BASH_LINENO).
    /// </summary>
    public int Line { get; set; }

    /// <summary>
    /// Added to every recorded line: the text a run-time <c>eval</c> runs was transpiled alone, so its lines count from 1,
    /// and bash numbers them from the eval's own line (<see cref="BeginEvalLines"/>). 0 in a function body, whose lines are
    /// already the script's.
    /// </summary>
    public int LineBase { get; private set; }

    /// <summary>The line each active function was called from, outermost first (BASH_LINENO, reversed).</summary>
    private readonly List<int> _callLines = new();

    /// <summary>The caller's <see cref="LineBase"/> for each active function.</summary>
    private readonly List<int> _callLineBases = new();

    /// <summary>The <see cref="LineBase"/> and <see cref="Line"/> outside each running <c>eval</c> or sourced file (<see cref="EnterLines"/>).</summary>
    private readonly List<(int Base, int Line)> _lineScopes = new();

    /// <summary>
    /// For each active function, outermost first: the file it was defined in (its BASH_SOURCE entry, null when unknown:
    /// a frame pushed by <see cref="Enter()"/>), and how many files were being read (<c>$global:BASH_SOURCE</c>: the
    /// script, sourced files) when it was called. See <see cref="Compose"/>.
    /// </summary>
    private readonly List<(string? Source, int Outer)> _funcSources = new();

    public void Enter() => Enter(null, 0);

    /// <summary>A function defined in <paramref name="source"/> is called while <paramref name="outer"/> files are being read.</summary>
    public void Enter(string? source, int outer)
    {
        _funcSources.Add((source, outer));
        _callLines.Add(Line);
        _callLineBases.Add(LineBase);
        LineBase = 0;
        Depth++;
    }

    /// <summary>The function at the current depth returns: its frames go (their values are restored by the caller's code).</summary>
    public void Exit()
    {
        if (Depth == 0) return;
        foreach (var list in _frames.Values) list.RemoveAll(f => f.Depth >= Depth);
        _localArrays.RemoveAll(a => a.Depth >= Depth);
        for (int i = _localAttrs.Count - 1; i >= 0 && _localAttrs[i].Depth >= Depth; i--)
        {
            var held = _localAttrs[i];
            BashShellState.RestoreAttributes(held.Name, held.Attrs);
            if (!held.Readonly) BashShellState.UnmarkReadonly(held.Name);
            BashShellState.RestoreNameref(held.Name, held.Nameref);
            _localAttrs.RemoveAt(i);
        }
        Depth--;
        // The caller goes on at its call: a call inside the same statement (`f $(g)`) is still made from that line.
        if (_callLines.Count > 0) { Line = _callLines[^1]; _callLines.RemoveAt(_callLines.Count - 1); }
        if (_callLineBases.Count > 0) { LineBase = _callLineBases[^1]; _callLineBases.RemoveAt(_callLineBases.Count - 1); }
        if (_funcSources.Count > 0) _funcSources.RemoveAt(_funcSources.Count - 1);
    }

    /// <summary>
    /// <c>${BASH_SOURCE[@]}</c>, innermost first: one entry per frame, as bash keeps it beside FUNCNAME. A function's
    /// frame is the file it was defined in; the files being read (<paramref name="files"/>, <c>$global:BASH_SOURCE</c>:
    /// a sourced file, the script) sit between the function frames at the depth each was entered.
    /// </summary>
    public string[] Visible(IReadOnlyList<string> files) => Compose(files, _funcSources);

    /// <summary>The pure rule of <see cref="Visible"/> (unit-tested).</summary>
    internal static string[] Compose(IReadOnlyList<string> files, IReadOnlyList<(string? Source, int Outer)> frames)
    {
        var result = new List<string>(files.Count + frames.Count);
        int taken = 0;
        for (int k = frames.Count - 1; k >= 0; k--)
        {
            var (source, outer) = frames[k];
            if (source is null) continue;
            // The files read since this function was called (a `source` in its body) are above it.
            int above = Math.Max(0, files.Count - taken - outer);
            for (int i = 0; i < above; i++) result.Add(files[taken + i]);
            taken += above;
            result.Add(source);
        }
        for (int i = taken; i < files.Count; i++) result.Add(files[i]);
        return result.ToArray();
    }

    /// <summary>
    /// BASH_LINENO: the line each active function was called from, innermost first, then 0 for the script itself
    /// (<paramref name="inScriptFile"/>; <c>bash -c</c> and the interactive shell have no such frame, as FUNCNAME's main).
    /// </summary>
    public string[] CallLines(bool inScriptFile)
    {
        var result = new string[_callLines.Count + (inScriptFile ? 1 : 0)];
        for (int i = 0; i < _callLines.Count; i++)
            result[i] = _callLines[_callLines.Count - 1 - i].ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (inScriptFile) result[^1] = "0";
        return result;
    }

    /// <summary>A temp binding <c>name=value cmd</c> starts; <paramref name="hidden"/> is the value it hides.</summary>
    public long PushTemp(string name, string? hidden, bool functionCall)
    {
        var f = new Frame { Local = false, Depth = Depth + (functionCall ? 1 : 0), Hidden = hidden, Id = ++_nextId };
        List(name).Add(f);
        return f.Id;
    }

    /// <summary>The temp binding <paramref name="id"/> ends (a no-op once <c>unset</c> or the function's exit removed it).</summary>
    public void PopTemp(string name, long id)
    {
        if (_frames.TryGetValue(name, out var list)) list.RemoveAll(f => f.Id == id);
    }

    /// <summary>
    /// <c>local name</c> runs with <paramref name="current"/> as the name's value. Returns the value a <c>local</c>
    /// WITHOUT an initializer leaves (null = unset); a <c>local name=v</c> ignores it and assigns.
    /// </summary>
    public string? Local(string name, string? current)
    {
        var list = List(name);
        var top = list.Count > 0 ? list[^1] : null;
        if (top is { Local: true } && top.Depth == Depth) return current;
        if (top is { Local: false } && top.Depth == Depth) { top.Local = true; return current; }
        // The local keeps the attribute of the variable it hides (bash: `export v; f() { local v=2; }` exports 2); an
        // `export`/`declare +x` in the function changes it for the local only (LocalWasExported, at the return).
        list.Add(new Frame { Local = true, Depth = Depth, Hidden = current, Id = ++_nextId, WasExported = PsBash.Core.ShellExports.IsExported(name) });
        TakeAttributes(name);
        return top is { Local: false } ? current : null;
    }

    /// <summary>The local arrays each active function declares (held as PowerShell variables of its scope), by depth.</summary>
    private readonly List<(int Depth, string Name)> _localArrays = new();

    /// <summary>The function at the current depth holds <paramref name="names"/> as local arrays (its prologue says so).</summary>
    public void AddLocalArrays(string[] names)
    {
        foreach (var name in names) _localArrays.Add((Depth, name));
    }

    /// <summary>True while some active function holds <paramref name="name"/> as a local array.</summary>
    public bool IsLocalArray(string name)
    {
        foreach (var held in _localArrays)
            if (held.Name == name) return true;
        return false;
    }

    /// <summary>
    /// The depths of the innermost and outermost active functions that name <paramref name="name"/> as a local array
    /// (their prologue does, whether or not the <c>local</c> ran); both -1 when none does.
    /// </summary>
    public void LocalArrayDepths(string name, out int innermost, out int outermost)
    {
        innermost = -1;
        outermost = int.MaxValue;
        foreach (var held in _localArrays)
        {
            if (!string.Equals(held.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (held.Depth > innermost) innermost = held.Depth;
            if (held.Depth < outermost) outermost = held.Depth;
        }
        if (innermost < 0) outermost = -1;
    }

    /// <summary>The subshells' array-write journals open now, oldest first (concurrent pipe stages each have one).</summary>
    internal List<BashVariableLookup.ArrayWriteJournal> OpenJournals { get; } = new();

    /// <summary>The declare attributes (<see cref="BashShellState.MarkAttribute"/>) each function's locals hid, put back at its return.</summary>
    private readonly List<(int Depth, string Name, string? Attrs, bool Readonly, string? Nameref)> _localAttrs = new();

    /// <summary>
    /// A local of the current function is declared: it starts with no declare attributes of its own (bash: a local does
    /// not inherit <c>-i</c>, nor the declared-but-unset mark, nor a nameref), and the hidden variable's are put back
    /// when the function returns (<see cref="Exit"/>), when a read-only mark the local took goes too. Once per name per call; a no-op at top level. REGRESSION: attributes were one
    /// name-keyed table, so <c>f() { local -a o; }</c> left the caller's <c>o=()</c> printing as <c>declare -a o</c>.
    /// </summary>
    public void TakeAttributes(string name)
    {
        if (Depth == 0) return;
        foreach (var saved in _localAttrs)
            if (saved.Depth == Depth && saved.Name == name) return;
        // Read-only stays as it is (a local over a read-only name is refused before this); a nameref is the local's own.
        _localAttrs.Add((Depth, name, BashShellState.TakeAttributes(name), BashShellState.IsReadonly(name), BashShellState.TakeNameref(name)));
    }

    /// <summary>
    /// <c>unset name</c>. True when a frame went and <paramref name="revealed"/> is the value that comes back (null =
    /// unset); false for a plain unset (no frame, or a local of the current function, which stays local).
    /// </summary>
    public bool Unset(string name, out string? revealed)
    {
        revealed = null;
        if (!_frames.TryGetValue(name, out var list) || list.Count == 0) return false;
        var top = list[^1];
        if (top.Local && top.Depth == Depth) return false;
        list.RemoveAt(list.Count - 1);
        // Its value is revealed now; when the binding ends, the name is not put back (later writes went to the outer one).
        if (!top.Local) _unsetTemps.Add(top.Id);
        revealed = top.Hidden;
        return true;
    }

    private readonly HashSet<long> _unsetTemps = new();

    /// <summary>
    /// The temp binding <paramref name="id"/> ends: true when its value is to be restored, false when <c>unset</c>
    /// took its frame (<c>v=t f</c>, f: <c>unset v; v=new</c> leaves v=new). A frame the called function's exit
    /// dropped is still restored.
    /// </summary>
    public bool EndTemp(string name, long id)
    {
        if (_unsetTemps.Remove(id)) return false;
        PopTemp(name, id);
        return true;
    }

    /// <summary>
    /// The function at the current depth is about to return: true when its local <paramref name="name"/> is still
    /// in place, so the caller's value is to be restored; false after a helper unset it (<c>unlocal v</c>), when
    /// writes since went to the outer variable.
    /// </summary>
    public bool OwnsLocal(string name)
        => _frames.TryGetValue(name, out var list) && list.Exists(f => f.Local && f.Depth == Depth);

    /// <summary>
    /// Whether a running function (this one or a caller, bash's scope being dynamic) declared <paramref name="name"/>
    /// local: the name exists even while unset (<c>local x; declare -p x</c> prints <c>declare -- x</c>).
    /// </summary>
    public bool HasLocal(string name)
        => _frames.TryGetValue(name, out var list) && list.Exists(f => f.Local);

    /// <summary>The export attribute the current function's local <paramref name="name"/> hid; null when it owns no such local.</summary>
    public bool? LocalWasExported(string name)
    {
        if (!_frames.TryGetValue(name, out var list)) return null;
        for (int i = list.Count - 1; i >= 0; i--)
            if (list[i].Local && list[i].Depth == Depth) return list[i].WasExported;
        return null;
    }

    private readonly List<(string[] Names, bool[] Was)> _tempExports = new();

    /// <summary><c>NAME=v cmd</c> starts (BashVariableStore.TempExport): what its end needs to put the attributes back.</summary>
    public void PushTempExport(string[] names, bool[] was) => _tempExports.Add((names, was));

    /// <summary>The innermost <see cref="PushTempExport"/>, removed; null when there is none.</summary>
    public (string[] Names, bool[] Was)? PopTempExport()
    {
        if (_tempExports.Count == 0) return null;
        var top = _tempExports[^1];
        _tempExports.RemoveAt(_tempExports.Count - 1);
        return top;
    }

    private readonly List<(string Name, string? Saved, long FrameId)> _bindings = new();

    /// <summary>
    /// <c>name=v eval ...</c> / <c>source</c> / <c>.</c>: the command runs in the caller's PowerShell scope, so a binding
    /// inside it (<c>v=1 eval 'v=2 eval ...'</c>) would overwrite a saved-value variable; the value the binding hides is
    /// kept here instead, with its frame (owned by the current depth).
    /// </summary>
    public void Bind(string name, string? saved)
        => _bindings.Add((name, saved, PushTemp(name, saved, functionCall: false)));

    /// <summary>
    /// The innermost <see cref="Bind"/> of <paramref name="name"/> ends: returns the value the name is left with, the
    /// saved one, or <paramref name="current"/> when <c>unset</c> took the binding's frame.
    /// </summary>
    public string? Unbind(string name, string? current)
    {
        for (int i = _bindings.Count - 1; i >= 0; i--)
        {
            if (_bindings[i].Name != name) continue;
            var b = _bindings[i];
            _bindings.RemoveAt(i);
            return EndTemp(name, b.FrameId) ? b.Saved : current;
        }
        return current;
    }

    /// <summary>
    /// The innermost <see cref="Bind"/> of <paramref name="name"/> ends with the name keeping whatever value it has now:
    /// the POSIX-mode end of a prefix assignment before a special builtin (<c>set -o posix; v=t :</c> leaves v=t). The
    /// binding was still a frame while the builtin ran, so <c>v=t unset v</c> took the frame and revealed the value
    /// under it, exactly as outside POSIX mode. A frame <c>local</c> adopted stays: it is the function's local now.
    /// </summary>
    public void Persist(string name)
    {
        for (int i = _bindings.Count - 1; i >= 0; i--)
        {
            if (_bindings[i].Name != name) continue;
            long id = _bindings[i].FrameId;
            _bindings.RemoveAt(i);
            if (_unsetTemps.Remove(id)) return;
            if (_frames.TryGetValue(name, out var list)) list.RemoveAll(f => f.Id == id && !f.Local);
            return;
        }
    }

    public void Clear() { _frames.Clear(); _bindings.Clear(); _tempExports.Clear(); _unsetTemps.Clear(); _callLines.Clear(); _callLineBases.Clear(); _funcSources.Clear(); _lineScopes.Clear(); _localArrays.Clear(); _localAttrs.Clear(); OpenJournals.Clear(); Line = 0; LineBase = 0; Depth = 0; }

    /// <summary>A copy of the frames, for a subshell to put back when it ends (BashShellState.Save/Restore).</summary>
    public sealed class Saved
    {
        internal Saved(VarFrameStack copy) => Copy = copy;
        internal VarFrameStack Copy { get; }
    }

    /// <summary>
    /// <c>( unlocal v )</c> pops the caller's frame only for the subshell: the parent's frames come back when it ends
    /// (else f's return saw its local gone and kept the subshell's value).
    /// </summary>
    public Saved Save()
    {
        var copy = new VarFrameStack { Depth = Depth, Line = Line };
        CopyInto(this, copy);
        return new Saved(copy);
    }

    public void Restore(Saved saved)
    {
        Depth = saved.Copy.Depth;
        Line = saved.Copy.Line;
        CopyInto(saved.Copy, this);
    }

    private static void CopyInto(VarFrameStack from, VarFrameStack to)
    {
        to._frames.Clear();
        foreach (var (name, list) in from._frames)
        {
            if (list.Count == 0) continue;
            var frames = new List<Frame>(list.Count);
            foreach (var f in list) frames.Add(new Frame { Local = f.Local, Depth = f.Depth, Hidden = f.Hidden, Id = f.Id, WasExported = f.WasExported });
            to._frames[name] = frames;
        }
        to._bindings.Clear();
        to._bindings.AddRange(from._bindings);
        to._tempExports.Clear();
        to._tempExports.AddRange(from._tempExports);
        to._unsetTemps.Clear();
        to._unsetTemps.UnionWith(from._unsetTemps);
        to._callLines.Clear();
        to._callLines.AddRange(from._callLines);
        to._callLineBases.Clear();
        to._callLineBases.AddRange(from._callLineBases);
        to._funcSources.Clear();
        to._funcSources.AddRange(from._funcSources);
        // The local arrays and attributes each active function holds: a subshell's own calls end with it, and the parent's
        // come back (else a stale entry made a later write of that name look local to a function no longer running).
        to._localArrays.Clear();
        to._localArrays.AddRange(from._localArrays);
        to._localAttrs.Clear();
        to._localAttrs.AddRange(from._localAttrs);
        to.LineBase = from.LineBase;
        // Ids stay unique across the restore: a frame pushed after it never reuses one a saved frame holds.
        to._nextId = Math.Max(to._nextId, from._nextId);
    }

    private List<Frame> List(string name)
    {
        if (!_frames.TryGetValue(name, out var list)) _frames[name] = list = new List<Frame>();
        return list;
    }

    private static readonly ConditionalWeakTable<Runspace, VarFrameStack> PerRunspace = new();
    private static readonly VarFrameStack NoRunspace = new();

    /// <summary>The stack of the runspace running on this thread (a background job's runspace has its own).</summary>
    public static VarFrameStack Current
        => Runspace.DefaultRunspace is { } rs ? PerRunspace.GetValue(rs, _ => new VarFrameStack()) : NoRunspace;

    /// <summary>
    /// A new top-level command on <paramref name="runspace"/> starts with no frames (the host calls this per invocation),
    /// on line 1: the emitted script records a statement's line only once it is past line 1 (PsEmitter.EmitStatementCore),
    /// so a diagnostic from its first line still reads <c>bash: line 1:</c>.
    /// </summary>
    public static void ResetFor(Runspace runspace)
    {
        var stack = PerRunspace.GetValue(runspace, _ => new VarFrameStack());
        stack.Clear();
        stack.Line = 1;
    }

    // The emitted code's entry points (static, so the PowerShell text stays short). Values come in as object: PowerShell
    // turns a $null passed to a string parameter into "", and an unset name must stay null.
    public static void EnterFunction() => Current.Enter();
    public static void ExitFunction() => Current.Exit();

    /// <summary>
    /// A bash function body starts (<c>PsBuild.FunctionPrologue</c>; <paramref name="name"/> is the function's): a new frame, and the ERR trap put aside unless
    /// errtrace is on (<see cref="ErrTrapInheritance"/>). Returns what was put aside, for <see cref="ExitFunction(SessionState, object)"/>.
    /// </summary>
    public static object? EnterFunction(SessionState session, string? name)
    {
        // Its BASH_SOURCE frame: the file it was defined in (FunctionSources), above the files being read now.
        Current.Enter(FunctionSources.Of(session, name), Files(session.PSVariable.GetValue("global:BASH_SOURCE")).Length);
        return ErrTrapInheritance.Hide(session);
    }

    /// <summary>The value of <c>${BASH_SOURCE[@]}</c> (<see cref="Visible"/>); <paramref name="bashSource"/> is <c>$global:BASH_SOURCE</c>.</summary>
    public static string[] BashSource(object? bashSource) => Current.Visible(Files(bashSource));

    /// <summary>The files being read, innermost first, from <c>$global:BASH_SOURCE</c> (a string, an array, or nothing).</summary>
    internal static string[] Files(object? bashSource)
    {
        if (bashSource is PSObject w) bashSource = w.BaseObject;
        if (bashSource is null) return [];
        if (bashSource is string s) return s.Length > 0 ? [s] : [];
        if (bashSource is System.Collections.IEnumerable seq)
        {
            var list = new List<string>();
            foreach (var item in seq) list.Add((item is PSObject p ? p.BaseObject : item)?.ToString() ?? "");
            return list.ToArray();
        }
        return [bashSource.ToString() ?? ""];
    }

    /// <summary>The function body that <see cref="EnterFunction(SessionState, string)"/> started ends (<c>PsBuild.FunctionEpilogue</c>).</summary>
    public static void ExitFunction(SessionState session, object? hiddenErrTrap)
    {
        Current.Exit();
        ErrTrapInheritance.RestoreAfterFunction(session, hiddenErrTrap);
    }
    /// <summary>The statement on <paramref name="line"/> starts (BASH_LINENO and the shell-diagnostic prefix, ShellDiagnostic, read it).</summary>
    public static void SetLine(int line) => Current.RecordLine(line);

    /// <summary>
    /// A run-time <c>eval</c> starts on the current line: the lines of its text (from 1) are the script's from that line
    /// on (bash: <c>eval $'true\nx'</c> on line 7 reports x on line 8). <see cref="EndEvalLines"/> ends it.
    /// </summary>
    public static void BeginEvalLines() { var s = Current; s.EnterLines(Math.Max(0, s.Line - 1)); }

    /// <summary>The run-time <c>eval</c> that <see cref="BeginEvalLines"/> started has ended.</summary>
    public static void EndEvalLines() => Current.ExitLines();

    /// <summary>A sourced file starts: its lines are its own (<c>./lib.sh: line 2:</c>), also when sourced inside an eval.</summary>
    public static void BeginSourceLines() => Current.EnterLines(0);

    /// <summary>The sourced file that <see cref="BeginSourceLines"/> started has ended.</summary>
    public static void EndSourceLines() => Current.ExitLines();

    /// <summary>The statement on <paramref name="line"/> of the running text starts: <see cref="Line"/> is it plus <see cref="LineBase"/>.</summary>
    public void RecordLine(int line) => Line = LineBase + line;

    /// <summary>
    /// Text transpiled on its own (an eval's, a sourced file's) starts running: its line 1 is line
    /// <paramref name="lineBase"/> + 1. <see cref="ExitLines"/> puts back the base AND the line: bash's line after
    /// <c>eval "$three_lines"; x</c> or <c>. ./f; x</c> on line 1 is 1 again.
    /// </summary>
    public void EnterLines(int lineBase)
    {
        _lineScopes.Add((LineBase, Line));
        LineBase = lineBase;
    }

    /// <summary>The text <see cref="EnterLines"/> started has ended (an unbalanced end is a no-op).</summary>
    public void ExitLines()
    {
        if (_lineScopes.Count == 0) return;
        (LineBase, Line) = _lineScopes[^1];
        _lineScopes.RemoveAt(_lineScopes.Count - 1);
    }
    /// <summary>The value of <c>${BASH_LINENO[@]}</c>; <paramref name="bashSource"/> is <c>$global:BASH_SOURCE</c> (non-empty in a script file).</summary>
    public static string[] BashLineNo(object? bashSource) => Current.CallLines(InScriptFile(bashSource));

    internal static bool InScriptFile(object? bashSource)
    {
        if (bashSource is PSObject w) bashSource = w.BaseObject;
        if (bashSource is null) return false;
        if (bashSource is string s) return s.Length > 0;
        if (bashSource is System.Collections.ICollection c) return c.Count > 0;
        return true;
    }
    public static long Temp(string name, object? hidden, bool functionCall) => Current.PushTemp(name, hidden?.ToString(), functionCall);
    /// <summary>A function call's temp binding ends: the value the name is left with (<paramref name="saved"/>, or <paramref name="current"/> after <c>unset</c> took the frame).</summary>
    public static string? EndTemp(string name, long id, object? saved, object? current)
        => Current.EndTemp(name, id) ? saved?.ToString() : current?.ToString();
    public static string? DeclareLocal(string name, object? current) => Current.Local(name, current?.ToString());

    /// <summary>A function body starts holding <paramref name="names"/> as local arrays (PsBuild.LocalArrays, after the prologue).</summary>
    public static void LocalArrays(params string[] names) => Current.AddLocalArrays(names);

    /// <summary>
    /// The variable a function's write to array <paramref name="name"/> goes to when the function did not declare it
    /// local (PsBuild.ArraySlot): bash's scope is dynamic, so a calling function's local array of that name, the nearest
    /// one (PowerShell's scope chain follows the calls, as reads of <c>$name</c> already do); else the shell's global,
    /// made when missing. A PowerShell-owned name (<c>$args</c>, which every function has) is never a caller's local.
    /// REGRESSION: such writes went to the global, so <c>f() { local -A cfg; load; }; load() { cfg[b]=2; }</c> lost b.
    /// </summary>
    public static PSVariable ArraySlot(SessionState session, string name)
        => BashVariableLookup.ArrayWriteSlot(session, name,
            Current.IsLocalArray(name) && !PsBash.Core.PowerShellOwnedVariables.Contains(name));

    /// <summary>A local held as a PowerShell variable (<c>local -A m</c>) is declared: see <see cref="TakeAttributes"/>.</summary>
    public static void LocalAttributes(string name) => Current.TakeAttributes(name);

    /// <summary>
    /// <c>local -n NAME=$1</c> ran: NAME is the current function's nameref to <paramref name="target"/> (the run-time value),
    /// so code that resolves names when it runs (arithmetic: <c>(( NAME++ ))</c>) reaches the target. An outer nameref of
    /// that name comes back when the function returns. A target that is no variable name records nothing.
    /// </summary>
    public static void LocalNameref(string name, string? target)
    {
        Current.TakeAttributes(name);
        if (!string.IsNullOrEmpty(target) && target != name && BashArrayElement.IsTarget(target))
            BashShellState.SetNameref(name, target);
    }
    /// <summary>The function returns: the value its local leaves behind (the caller's, or <paramref name="current"/> after <c>unlocal</c>).</summary>
    public static string? LeaveLocal(string name, object? saved, object? current)
    {
        var stack = Current;
        if (!stack.OwnsLocal(name)) return current?.ToString();
        // The caller's variable comes back with its own export attribute (`f() { local v=1; export v; }` exports nothing).
        if (stack.LocalWasExported(name) is { } was) PsBash.Core.ShellExports.SetExported(name, was);
        return saved?.ToString();
    }
    public static void BindSameScope(string name, object? saved) => Current.Bind(name, saved?.ToString());
    public static string? UnbindSameScope(string name, object? current) => Current.Unbind(name, current?.ToString());
    public static void PersistSameScope(string name) => Current.Persist(name);
}
