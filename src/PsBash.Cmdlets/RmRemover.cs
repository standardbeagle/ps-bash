namespace PsBash.Cmdlets;

/// <summary>When <c>rm</c> asks before removing (<c>-i</c> / <c>-I</c> / <c>--interactive[=WHEN]</c>).</summary>
internal enum RmPrompt
{
    /// <summary>Never ask (the default, and <c>-f</c> / <c>--interactive=never</c>).</summary>
    Never,

    /// <summary>Ask once before a batch of more than three operands or any recursion (<c>-I</c>).</summary>
    Once,

    /// <summary>Ask before every removal (<c>-i</c>, <c>--interactive[=always]</c>).</summary>
    Always,
}

/// <summary>
/// The removal walk used whenever <c>rm</c> has something to SAY while it works: a prompt per entry
/// (<c>-i</c>), <c>-v</c> output, or <c>-d</c> on a directory. Quiet recursive deletes stay on the
/// native fast path (<see cref="FileSystemHelpers.DeleteDirectoryForce"/>); this walk is the same
/// traversal GNU's fts does — pre-order "descend into" prompt, children, post-order "remove
/// directory" — so the prompts, the <c>removed directory 'd'</c> wording and the order of the
/// <c>-v</c> lines match, and a directory is only removed after every child is gone.
/// <para>
/// Directory symlinks / junctions are unlinked, never descended into (a destructive escape out of
/// the tree otherwise). Every message names the operand as typed: <c>display</c> is threaded next
/// to each path and extended with <see cref="FileSystemHelpers.AppendDisplay"/> for children.
/// </para>
/// </summary>
internal sealed class RmRemover
{
    private readonly bool _recursive;
    private readonly bool _dirOnly;
    private readonly bool _promptEach;
    private readonly bool _verbose;
    private readonly Func<string, bool> _confirm;
    private readonly Action<string> _say;
    private readonly Action<string> _error;
    private bool _failed;
    private readonly bool _oneFileSystem;
    private readonly Func<string, string?> _deviceOf;
    private string? _rootDevice;

    /// <param name="recursive"><c>-r</c>: descend into directories.</param>
    /// <param name="dirOnly"><c>-d</c>: a directory without <c>-r</c> is removed when empty.</param>
    /// <param name="promptEach"><c>-i</c>: ask before every entry.</param>
    /// <param name="verbose"><c>-v</c>: report every removal.</param>
    /// <param name="confirm">Shows the prompt (complete text) and returns the user's yes/no.</param>
    /// <param name="say">Verbose output sink (stdout).</param>
    /// <param name="error">Diagnostic sink; also marks the walk failed.</param>
    /// <param name="oneFileSystem"><c>--one-file-system</c>: while descending, skip a directory that lives
    /// on a different device than the command-line operand.</param>
    /// <param name="deviceOf">Device id of a path (<see cref="FileIdentity.TryGetDeviceId"/>; a seam so
    /// the skip logic is testable without a second filesystem). Null id = unknown = never skip.</param>
    public RmRemover(bool recursive, bool dirOnly, bool promptEach, bool verbose,
        Func<string, bool> confirm, Action<string> say, Action<string> error,
        bool oneFileSystem = false, Func<string, string?>? deviceOf = null)
    {
        _oneFileSystem = oneFileSystem;
        _deviceOf = deviceOf ?? FileIdentity.TryGetDeviceId;
        _recursive = recursive;
        _dirOnly = dirOnly;
        _promptEach = promptEach;
        _verbose = verbose;
        _confirm = confirm;
        _say = say;
        _error = error;
    }

    /// <summary>Removes <paramref name="path"/> (already known to exist). True when nothing went wrong;
    /// an entry the user declined is not a failure (GNU exits 0).</summary>
    public bool Remove(string path, string display)
    {
        _failed = false;
        _rootDevice = _oneFileSystem && _recursive && Directory.Exists(path) ? _deviceOf(path) : null;
        RemoveEntry(path, display);
        return !_failed;
    }

    private void Fail(string message)
    {
        _failed = true;
        _error(message);
    }

    private void RemoveEntry(string path, string display)
    {
        if (FileSystemHelpers.IsReparsePoint(path))
        {
            RemoveLink(path, display);
            return;
        }

        if (Directory.Exists(path))
        {
            RemoveDirectory(path, display);
            return;
        }

        if (_promptEach)
        {
            var kind = IsEmptyFile(path) ? "regular empty file" : "regular file";
            if (!_confirm($"rm: remove {kind} '{display}'? ")) return;
        }

        try
        {
            FileSystemHelpers.DeleteFileForce(path);
        }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
        {
            Fail($"rm: cannot remove '{display}': {ex.Message}");
            return;
        }
        if (_verbose) _say($"removed '{display}'\n");
    }

    private void RemoveLink(string path, string display)
    {
        if (_promptEach && !_confirm($"rm: remove symbolic link '{display}'? ")) return;
        try
        {
            if (Directory.Exists(path))
            {
                FileSystemHelpers.ClearReadOnly(path);
                Directory.Delete(path, recursive: false);
            }
            else
            {
                FileSystemHelpers.DeleteFileForce(path);
            }
        }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
        {
            Fail($"rm: cannot remove '{display}': {ex.Message}");
            return;
        }
        if (_verbose) _say($"removed '{display}'\n");
    }

    private void RemoveDirectory(string path, string display)
    {
        if (!_recursive)
        {
            if (!_dirOnly)
            {
                Fail($"rm: cannot remove '{display}': Is a directory");
                return;
            }
            // -d: only an EMPTY directory may go, and GNU reports a non-empty one without asking.
            if (!IsEmptyDirectory(path))
            {
                Fail($"rm: cannot remove '{display}': Directory not empty");
                return;
            }
            RemoveEmptyDirectory(path, display);
            return;
        }

        if (_promptEach && !_confirm($"rm: descend into directory '{display}'? ")) return;

        string[] children;
        try
        {
            children = Directory.GetFileSystemEntries(path);
        }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
        {
            Fail($"rm: cannot remove '{display}': {ex.Message}");
            return;
        }

        foreach (var child in children)
        {
            var childDisplay = FileSystemHelpers.AppendDisplay(display, Path.GetFileName(child));
            // --one-file-system: a real directory on another device is skipped whole (GNU: the
            // command fails, and the directory — hence its parents — stay).
            if (_rootDevice is not null && !FileSystemHelpers.IsReparsePoint(child) && Directory.Exists(child)
                && _deviceOf(child) is { } dev && dev != _rootDevice)
            {
                Fail($"rm: skipping '{childDisplay}', since it's on a different device");
                continue;
            }
            RemoveEntry(child, childDisplay);
        }

        // A declined or failed child leaves the directory non-empty: keep it, and — like GNU —
        // do not ask about removing it.
        if (!IsEmptyDirectory(path)) return;
        RemoveEmptyDirectory(path, display);
    }

    private void RemoveEmptyDirectory(string path, string display)
    {
        if (_promptEach && !_confirm($"rm: remove directory '{display}'? ")) return;
        try
        {
            // Empty by construction, so the force (recursive, read-only-clearing) primitive can
            // only ever delete this one directory.
            FileSystemHelpers.DeleteDirectoryForce(path);
        }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
        {
            Fail($"rm: cannot remove '{display}': {ex.Message}");
            return;
        }
        if (_verbose) _say($"removed directory '{display}'\n");
    }

    private static bool IsEmptyFile(string path)
    {
        try { return new FileInfo(path).Length == 0; }
        catch { return false; }
    }

    private static bool IsEmptyDirectory(string path)
    {
        try
        {
            using var e = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            return !e.MoveNext();
        }
        catch { return false; }
    }
}
