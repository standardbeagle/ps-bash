namespace PsBash.Cmdlets;

/// <summary>
/// Whether a command's pipeline is a redirected REGULAR file (<c>wc -lc &lt; f</c>) rather than a pipe: what bash's
/// child sees with <c>fstat(0)</c>. The emitter feeds <c>cmd &lt; f</c> as <c>Invoke-BashCat f -RawStdinBytes | cmd</c>,
/// so the reader only sees records. <c>Invoke-BashCat</c> offers the FIRST record it writes for such a file with the
/// file's size and full path; a reader whose first input record is that very object (reference equality: the same
/// string instance, not equal text) claims it. Anything in between that makes new records (a pipe through another
/// command, a <c>read</c> loop) breaks the identity, so the reader correctly sees a pipe. Pipeline commands run on one
/// thread, so the slot is thread-static; a claim or a new offer clears the previous one.
/// <para>
/// The path lets a reader see that its stdin is the file the shell's fd 1 names (<c>cat &lt; log</c> after
/// <c>exec &gt;log</c>: GNU cat and grep refuse it, BashCmdlet.ReadsTheShellsOutput).
/// </para>
/// <para>
/// Known gap: an empty file writes no record, so <c>wc -lc &lt; empty</c> is laid out as for a pipe.
/// </para>
/// </summary>
internal static class RegularStdin
{
    [ThreadStatic] private static object? t_first;
    [ThreadStatic] private static long t_size;
    [ThreadStatic] private static string? t_path;

    /// <summary>Called by the <c>&lt; file</c> reader with the first record it writes, the file's size and its full path.
    /// An empty string is never offered: it may be the shared <see cref="string.Empty"/> instance another command also
    /// writes.</summary>
    internal static void Offer(object firstRecord, long size, string path)
    {
        if (firstRecord is string { Length: 0 }) { t_first = null; t_path = null; return; }
        t_first = firstRecord;
        t_size = size;
        t_path = path;
    }

    /// <summary>True (with the file's size) when <paramref name="record"/> is the record last offered; clears the offer.</summary>
    internal static bool TryClaim(object? record, out long size) => TryClaim(record, out size, out _);

    /// <summary>True (with the file's size and full path) when <paramref name="record"/> is the record last offered;
    /// clears the offer.</summary>
    /// <remarks>A string record reaches the reader wrapped in a PSObject whose base is that string; a typed record (a
    /// PSObject the writer made) arrives as itself — its base may be the shared PSCustomObject instance, so it is never
    /// unwrapped.</remarks>
    internal static bool TryClaim(object? record, out long size, out string? path)
    {
        size = 0;
        path = null;
        if (record is null || t_first is null) return false;
        bool same = ReferenceEquals(record, t_first)
                    || (record is System.Management.Automation.PSObject { BaseObject: string s } && ReferenceEquals(s, t_first));
        if (!same) return false;
        size = t_size;
        path = t_path;
        t_first = null;
        t_path = null;
        return true;
    }
}
