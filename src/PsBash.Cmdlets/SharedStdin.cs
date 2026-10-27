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

    /// <summary>
    /// <c>read -d DELIM</c>: everything up to the first <paramref name="delim"/> (NUL for <c>-d ''</c>) across lines. The
    /// queue holds lines, so they are re-joined with newlines (the stream ends in one, as a heredoc / here-string / printf
    /// does); the text after the delimiter goes back to the front of the stream. <paramref name="terminated"/> is false
    /// when the stream ended first (bash assigns what it read and returns 1). False when the stdin is absent or at end.
    /// </summary>
    internal static bool TryDequeueRecord(SessionState state, char delim, out string? record, out bool terminated)
    {
        record = null;
        terminated = false;
        var items = new List<object>();
        switch (Get(state))
        {
            case StdinCursor cursor:
                while (cursor.Count > 0) items.Add(cursor.Dequeue());
                break;
            case Queue<object> queue:
                while (queue.Count > 0) items.Add(queue.Dequeue());
                break;
            default:
                return false;
        }
        if (items.Count == 0) return false;
        var text = string.Join("\n", items.Select(o => BashRuntime.GetBashText(o).Replace("\r\n", "\n").TrimEnd('\n')))
                   + "\n";
        int at = text.IndexOf(delim);
        string rest = "";
        if (at >= 0) { record = text.Substring(0, at); rest = text.Substring(at + 1); terminated = true; }
        else record = text;
        if (rest.Length > 0)
        {
            switch (Get(state))
            {
                case StdinCursor c: c.PushFront(new object[] { rest }); break;
                case Queue<object> q: q.Enqueue(rest); break;
            }
        }
        return true;
    }

    /// <summary>Put <paramref name="text"/> back at the FRONT of the shared stdin (nothing when it is empty).</summary>
    internal static void PushFront(SessionState state, string text)
    {
        if (text.Length == 0) return;
        switch (Get(state))
        {
            case StdinCursor c: c.PushFront(new object[] { text }); break;
            case Queue<object> q:
                var rest = new List<object> { text };
                while (q.Count > 0) rest.Add(q.Dequeue());
                foreach (var item in rest) q.Enqueue(item);
                break;
        }
    }
}
