using System.Management.Automation;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// The standard input of a COMPOUND command (<c>{ …; }</c>, <c>( … )</c>, loops, <c>if</c>,
/// <c>case</c> used as a pipe stage or given <c>&lt; file</c> / <c>&lt;&lt;&lt;</c>) or of the whole
/// <c>-c</c> script when the launcher forwards its own stdin, shared by every command inside it.
/// <c>$global:__BashStdIn</c> is a <see cref="StdinCursor"/> of the ORIGINAL pipeline records (so a
/// filter keeps typed objects and an unterminated <c>printf</c> record keeps its flag), pulled lazily:
/// stdin-reading commands inside the scope are fed from it (<c>PsBuild.StdinFeed</c>) and the
/// <c>read</c> builtin takes one LINE at a time through <see cref="TryDequeueLine"/>. Both advance the
/// same cursor, which is what makes <c>{ read x; sort; }</c> sort the REST. A plain
/// <c>Queue[object]</c> in the variable is still honoured (direct PowerShell callers and tests).
/// </summary>
internal static class SharedStdin
{
    internal const string VariableName = "global:__BashStdIn";

    /// <summary>The live cursor/queue, or null when no stdin scope is active.</summary>
    internal static object? Get(SessionState state)
    {
        var raw = state.PSVariable.GetValue(VariableName);
        if (raw is null) return null;
        var baseObject = PSObject.AsPSObject(raw).BaseObject;
        return baseObject is StdinCursor or Queue<object> ? baseObject : null;
    }

    /// <summary>
    /// Take ONE line (without its terminator) from the shared stdin. A record that carries several
    /// lines (<c>printf 'a\nb\n'</c> is one record) yields its first line and keeps the remainder at
    /// the FRONT of the stream. False when the stdin is absent or at end of input.
    /// </summary>
    internal static bool TryDequeueLine(SessionState state, out string? line)
    {
        line = null;
        switch (Get(state))
        {
            case StdinCursor cursor:
            {
                if (cursor.Count == 0) return false;
                var text = BashRuntime.GetBashText(cursor.Dequeue()).Replace("\r\n", "\n");
                int nl = text.IndexOf('\n');
                if (nl < 0 || nl == text.Length - 1)
                {
                    line = nl < 0 ? text : text.Substring(0, nl);
                    return true;
                }
                line = text.Substring(0, nl);
                cursor.PushFront(new object[] { text.Substring(nl + 1) });
                return true;
            }
            case Queue<object> queue:
            {
                if (queue.Count == 0) return false;
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
            default:
                return false;
        }
    }
}
