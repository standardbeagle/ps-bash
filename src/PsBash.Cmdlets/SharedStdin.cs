using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The standard input of a COMPOUND command (<c>{ …; }</c>, <c>( … )</c>, loops, <c>if</c>,
/// <c>case</c> used as a pipe stage or given <c>&lt; file</c> / <c>&lt;&lt;&lt;</c>), shared by every
/// command inside it. The emitter's stdin scope (<c>PsBuild.StdinScope</c>) drains the incoming
/// pipeline into <c>$global:__BashStdIn</c>, a <c>Queue[object]</c> of the ORIGINAL pipeline
/// records (so a filter keeps typed objects and an unterminated <c>printf</c> record keeps its flag);
/// stdin-reading commands inside the scope are fed from it lazily (<c>PsBuild.StdinFeed</c>) and
/// the <c>read</c> builtin takes one LINE at a time through <see cref="TryDequeueLine"/>. Both
/// advance the same cursor, which is what makes <c>{ read x; sort; }</c> sort the REST.
/// </summary>
internal static class SharedStdin
{
    internal const string VariableName = "global:__BashStdIn";

    /// <summary>The live queue, or null when no compound stdin scope is active.</summary>
    internal static Queue<object>? Get(SessionState state)
    {
        var raw = state.PSVariable.GetValue(VariableName);
        if (raw is null) return null;
        return PSObject.AsPSObject(raw).BaseObject as Queue<object>;
    }

    /// <summary>
    /// Take ONE line (without its terminator) from the shared stdin. A record that carries several
    /// lines (<c>printf 'a\nb\n'</c> is one record) yields its first line and keeps the remainder at
    /// the FRONT of the queue. False when the queue is absent or empty.
    /// </summary>
    internal static bool TryDequeueLine(SessionState state, out string? line)
    {
        line = null;
        var queue = Get(state);
        if (queue is null || queue.Count == 0) return false;

        var text = BashRuntime.GetBashText(queue.Dequeue()).Replace("\r\n", "\n");
        int nl = text.IndexOf('\n');
        if (nl < 0 || nl == text.Length - 1)
        {
            line = nl < 0 ? text : text.Substring(0, nl);
            return true;
        }

        line = text.Substring(0, nl);
        var rest = new List<object> { text.Substring(nl + 1) };
        while (queue.Count > 0) rest.Add(queue.Dequeue());
        foreach (var item in rest) queue.Enqueue(item);
        return true;
    }
}
