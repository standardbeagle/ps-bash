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
    /// THE take from a shared stdin: the next record, atomically, or false at end of input. A background job reads the
    /// same cursor as the shell (<see cref="BackgroundJobStdin"/>), so checking <c>Count</c> and then calling
    /// <c>Dequeue</c> raced: the other reader could take the record in between, and <c>Dequeue</c> then threw at end of
    /// input. Every reader here takes through this (guard: SharedStdinTakeGuardTests). A plain <c>Queue[object]</c> is
    /// single-threaded by construction (a job promotes it to a cursor first).
    /// </summary>
    internal static bool TryTake(object? stdin, out object? record)
    {
        switch (stdin)
        {
            case StdinCursor cursor: return cursor.TryDequeue(out record);
            case Queue<object> queue: return queue.TryDequeue(out record);
            default: record = null; return false;
        }
    }

    /// <summary>Every record left on <paramref name="stdin"/>, taken one at a time through <see cref="TryTake"/>.</summary>
    private static List<object> TakeAll(object? stdin)
    {
        var items = new List<object>();
        while (TryTake(stdin, out var item)) items.Add(item!);
        return items;
    }

    /// <summary>
    /// Take ONE line (without its terminator) from the shared stdin. A record that carries several
    /// lines (<c>printf 'a\nb\n'</c> is one record) yields its first line and keeps the remainder at
    /// the FRONT of the stream. False when the stdin is absent or at end of input.
    /// </summary>
    internal static bool TryDequeueLine(SessionState state, out string? line) => TryDequeueLine(state, out line, out _);

    /// <summary>As above; <paramref name="terminated"/> is false when the line was the unterminated tail of the input
    /// (<c>printf abc</c>, <c>echo -n abc</c>), which bash's <c>read</c> reports with status 1.</summary>
    internal static bool TryDequeueLine(SessionState state, out string? line, out bool terminated) =>
        TryDequeueLine(Get(state), out line, out terminated);

    /// <summary>
    /// As above, from <paramref name="stdin"/> (a <see cref="StdinCursor"/> or <c>Queue&lt;object&gt;</c>, PSObject-wrapped or
    /// not). An unterminated record (<c>printf a</c>) does not end a line: the next record continues it, so
    /// <c>{ printf a; echo b; }</c> reads <c>ab</c>, as bash's byte stream does. Only an unterminated END of input is
    /// unterminated.
    /// </summary>
    internal static bool TryDequeueLine(object? stdin, out string? line, out bool terminated)
    {
        line = null;
        terminated = true;
        if (stdin is not null && PSObject.AsPSObject(stdin).BaseObject is var b && b is StdinCursor or Queue<object>) stdin = b;
        if (stdin is not (StdinCursor or Queue<object>)) return false;
        if (!TryTake(stdin, out var item)) return false;
        var text = BashRuntime.GetBashText(item).Replace("\r\n", "\n");
        bool unterminated = BashRuntime.IsUnterminated(item);
        // Join the records that continue an unterminated one until a newline arrives (or the input ends).
        while (unterminated && text.IndexOf('\n') < 0 && TryTake(stdin, out var next))
        {
            text += BashRuntime.GetBashText(next).Replace("\r\n", "\n");
            unterminated = BashRuntime.IsUnterminated(next);
        }
        int nl = text.IndexOf('\n');
        if (nl < 0 && unterminated) terminated = false;
        if (nl < 0 || nl == text.Length - 1)
        {
            line = nl < 0 ? text : text.Substring(0, nl);
            return true;
        }
        line = text.Substring(0, nl);
        object rest = BashRuntime.TextRecord(text.Substring(nl + 1), unterminated);
        switch (stdin)
        {
            case StdinCursor c: c.PushFront(new[] { rest }); break;
            case Queue<object> q:
                var all = new List<object> { rest };
                while (q.Count > 0) all.Add(q.Dequeue());
                foreach (var o in all) q.Enqueue(o);
                break;
        }
        return true;
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
        var stdin = Get(state);
        if (stdin is null) return false;
        var items = TakeAll(stdin);
        if (items.Count == 0) return false;
        // Each record ends in a newline unless it is the unterminated tail (printf abc): the stream's exact bytes.
        var text = BashRuntime.RecordStreamText(items).Replace("\r\n", "\n");
        int at = text.IndexOf(delim);
        string rest = "";
        if (at >= 0) { record = text.Substring(0, at); rest = text.Substring(at + 1); terminated = true; }
        else record = text;
        if (rest.Length > 0)
        {
            bool tailUnterminated = BashRuntime.IsUnterminated(items[^1]);
            object restRec = BashRuntime.TextRecord(rest, tailUnterminated && !rest.EndsWith('\n'));
            switch (Get(state))
            {
                case StdinCursor c: c.PushFront(new object[] { restRec }); break;
                case Queue<object> q: q.Enqueue(restRec); break;
            }
        }
        return true;
    }

    /// <summary><c>read -N N</c>: exactly <paramref name="count"/> characters of the stream (delimiters and newlines
    /// included); the remainder goes back to the front. <paramref name="complete"/> is false when the input ended first.</summary>
    internal static bool TryDequeueChars(SessionState state, int count, out string? chars, out bool complete)
    {
        chars = null;
        complete = false;
        var stdin = Get(state);
        if (stdin is null) return false;
        var items = TakeAll(stdin);
        if (items.Count == 0) return false;
        var text = BashRuntime.RecordStreamText(items).Replace("\r\n", "\n");
        if (text.Length <= count) { chars = text; complete = text.Length == count; return true; }
        chars = text.Substring(0, count);
        complete = true;
        PushFront(state, text.Substring(count));
        return true;
    }

    /// <summary>
    /// Put <paramref name="text"/> back at the FRONT of the shared stdin (nothing when it is empty). Text that does not end
    /// in a newline is the unterminated tail of the input and goes back as such: a plain string is a whole LINE to every
    /// reader, so <c>printf abc | { read -n 2 x; cat; }</c> printed <c>c</c> with a newline bash never saw.
    /// </summary>
    internal static void PushFront(SessionState state, string text)
    {
        if (text.Length == 0) return;
        object record = text.EndsWith('\n') ? text : BashRuntime.TextRecord(text, unterminated: true);
        switch (Get(state))
        {
            case StdinCursor c: c.PushFront(new object[] { record }); break;
            case Queue<object> q:
                var rest = new List<object> { record };
                while (q.Count > 0) rest.Add(q.Dequeue());
                foreach (var item in rest) q.Enqueue(item);
                break;
        }
    }
}

/// <summary>
/// The stdin a background job (<c>cmd &amp;</c>) starts with. bash hands the job the stdin of the compound command it
/// was started in -- <c>echo hi | { cat &amp; wait; }</c> prints <c>hi</c>, and a <c>cat &amp;</c> in a read loop's body
/// takes the lines the loop has not read -- but not the shell's own: with job control off a job at the top of a script
/// reads <c>/dev/null</c>.
/// </summary>
public static class BackgroundJobStdin
{
    /// <summary>
    /// The cursor a job started now shares with the shell: the active compound stdin, or an empty one (end of input)
    /// when there is none or it is the shell's own (<see cref="StdinCursor.IsShellStdin"/>). The job's runspace reads it
    /// concurrently, so a plain queue is first replaced by a <see cref="StdinCursor"/>, whose buffer is locked.
    /// </summary>
    public static StdinCursor Inherit(SessionState state)
    {
        switch (SharedStdin.Get(state))
        {
            case StdinCursor cursor when !cursor.IsShellStdin:
                return cursor;
            case Queue<object> queue:
                var promoted = new StdinCursor();
                while (queue.Count > 0) promoted.Enqueue(queue.Dequeue());
                state.PSVariable.Set(SharedStdin.VariableName, promoted);
                return promoted;
            default:
                return new StdinCursor();
        }
    }
}

/// <summary>
/// The condition of a <c>while read</c> loop that reads a shared stdin (<c>$global:__BashStdIn</c>: a compound's, a pipe
/// stage's, the launcher's): ONE line per pass, through the same cursor <c>read</c>, <c>cat</c>, <c>head</c> and natives
/// in the body advance, so <c>while read a; do read b; …; done</c> pairs the lines as bash does. Taking a whole record
/// per pass (a record can carry several lines) skipped the lines a body reader should have had.
/// </summary>
public static class SharedStdinLine
{
    /// <summary>
    /// The next line of <paramref name="stdin"/> without its newline, or null at end of input. An unterminated last line
    /// is null too: bash's <c>read</c> fails on it, so the loop body never sees it.
    /// </summary>
    public static string? Next(object? stdin) =>
        SharedStdin.TryDequeueLine(stdin, out var line, out bool terminated) && terminated ? line : null;
}
