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
    }

    private readonly Dictionary<string, List<Frame>> _frames = new(StringComparer.Ordinal);
    private long _nextId;

    /// <summary>The shell-function call depth (0 at top level).</summary>
    public int Depth { get; private set; }

    public void Enter() => Depth++;

    /// <summary>The function at the current depth returns: its frames go (their values are restored by the caller's code).</summary>
    public void Exit()
    {
        if (Depth == 0) return;
        foreach (var list in _frames.Values) list.RemoveAll(f => f.Depth >= Depth);
        Depth--;
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
        list.Add(new Frame { Local = true, Depth = Depth, Hidden = current, Id = ++_nextId });
        return top is { Local: false } ? current : null;
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

    public void Clear() { _frames.Clear(); _bindings.Clear(); _unsetTemps.Clear(); Depth = 0; }

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
        var copy = new VarFrameStack { Depth = Depth };
        CopyInto(this, copy);
        return new Saved(copy);
    }

    public void Restore(Saved saved)
    {
        Depth = saved.Copy.Depth;
        CopyInto(saved.Copy, this);
    }

    private static void CopyInto(VarFrameStack from, VarFrameStack to)
    {
        to._frames.Clear();
        foreach (var (name, list) in from._frames)
        {
            if (list.Count == 0) continue;
            var frames = new List<Frame>(list.Count);
            foreach (var f in list) frames.Add(new Frame { Local = f.Local, Depth = f.Depth, Hidden = f.Hidden, Id = f.Id });
            to._frames[name] = frames;
        }
        to._bindings.Clear();
        to._bindings.AddRange(from._bindings);
        to._unsetTemps.Clear();
        to._unsetTemps.UnionWith(from._unsetTemps);
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

    /// <summary>A new top-level command on <paramref name="runspace"/> starts with no frames (the host calls this per invocation).</summary>
    public static void ResetFor(Runspace runspace)
    {
        if (PerRunspace.TryGetValue(runspace, out var stack)) stack.Clear();
    }

    // The emitted code's entry points (static, so the PowerShell text stays short). Values come in as object: PowerShell
    // turns a $null passed to a string parameter into "", and an unset name must stay null.
    public static void EnterFunction() => Current.Enter();
    public static void ExitFunction() => Current.Exit();
    public static long Temp(string name, object? hidden, bool functionCall) => Current.PushTemp(name, hidden?.ToString(), functionCall);
    /// <summary>A function call's temp binding ends: the value the name is left with (<paramref name="saved"/>, or <paramref name="current"/> after <c>unset</c> took the frame).</summary>
    public static string? EndTemp(string name, long id, object? saved, object? current)
        => Current.EndTemp(name, id) ? saved?.ToString() : current?.ToString();
    public static string? DeclareLocal(string name, object? current) => Current.Local(name, current?.ToString());
    /// <summary>The function returns: the value its local leaves behind (the caller's, or <paramref name="current"/> after <c>unlocal</c>).</summary>
    public static string? LeaveLocal(string name, object? saved, object? current)
        => Current.OwnsLocal(name) ? saved?.ToString() : current?.ToString();
    public static void BindSameScope(string name, object? saved) => Current.Bind(name, saved?.ToString());
    public static string? UnbindSameScope(string name, object? current) => Current.Unbind(name, current?.ToString());
}
