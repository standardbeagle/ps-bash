namespace PsBash.Cmdlets;

/// <summary>
/// The copy walk behind <c>cp</c>: one entry (file, directory tree, symbolic link) from a source path to a
/// destination path, with every GNU 9.4 behaviour that happens per entry — identity and type-clash checks,
/// <c>-n</c>/<c>-i</c>/<c>-u</c>/<c>--update</c>, backups, <c>--remove-destination</c>, <c>-l</c>/<c>-s</c>,
/// <c>--reflink</c>, <c>--attributes-only</c>, <c>-L</c>/<c>-H</c>/<c>-P</c>, <c>-x</c>, <c>-v</c>/<c>--debug</c>.
/// The cmdlet resolves operands and shapes (<c>-t</c>, <c>-T</c>, <c>--parents</c>); this class never reads
/// the command line. Messages are the cmdlet's sinks, so the walk is testable without a runspace.
/// </summary>
internal sealed class CpEngine
{
    private readonly CpPlan _plan;
    private readonly CpPreserve _preserve;
    private readonly Func<string, bool> _confirm;
    private readonly Action<string> _say;
    private readonly Action<string> _error;
    private readonly Func<string, string?> _deviceOf;
    private readonly string _cwd;
    private string? _rootDevice;

    public CpEngine(CpPlan plan, CpPreserve preserve, Func<string, bool> confirm, Action<string> say,
        Action<string> error, string cwd, Func<string, string?>? deviceOf = null)
    {
        _plan = plan;
        _preserve = preserve;
        _confirm = confirm;
        _say = say;
        _error = error;
        _cwd = cwd;
        _deviceOf = deviceOf ?? FileIdentity.TryGetDeviceId;
    }

    /// <summary>
    /// Copies <paramref name="src"/> to exactly <paramref name="dest"/> (the caller already joined a
    /// directory destination). <paramref name="commandLine"/> is true for an operand the user typed, false for
    /// an entry found while recursing — it decides whether a symbolic link is followed. False when anything
    /// went wrong (a declined <c>-i</c> prompt counts, as in GNU).
    /// </summary>
    public bool CopyEntry(string src, string srcDisplay, string dest, string destDisplay, bool commandLine)
    {
        _rootDevice = _plan.OneFileSystem && commandLine && Directory.Exists(src) ? _deviceOf(src) : _rootDevice;
        return Copy(src, srcDisplay, dest, destDisplay, commandLine);
    }

    private bool Copy(string src, string srcDisplay, string dest, string destDisplay, bool commandLine)
    {
        bool srcIsLink = FileSystemHelpers.IsReparsePoint(src);
        if (srcIsLink && !FollowsLink(commandLine))
            return CopyLinkItself(src, srcDisplay, dest, destDisplay);

        bool srcIsFile = File.Exists(src);
        bool srcIsDir = !srcIsFile && Directory.Exists(src);
        if (!srcIsFile && !srcIsDir)
        {
            _error($"cp: cannot stat '{srcDisplay}': No such file or directory");
            return false;
        }
        if (srcIsDir && !_plan.Recursive)
        {
            _error($"cp: -r not specified; omitting directory '{srcDisplay}'");
            return false;
        }

        bool destExisted = File.Exists(dest) || Directory.Exists(dest) || FileSystemHelpers.IsReparsePoint(dest);

        // -n / --update=none skip an existing destination FILE before anything else is asked — even when it
        // is the same file (GNU: `cp -n a a` and `cp -n a hardlink-of-a` are silent no-ops).
        if (_plan.Existing == CpExisting.Skip && !srcIsDir && destExisted && !Directory.Exists(dest))
        {
            Skipped(destDisplay);
            return true;
        }

        // `cp -l a b` where b is already a hard link to a: GNU succeeds silently (nothing to link, -f or not).
        if (_plan.Link == CpLink.Hard && !srcIsDir && destExisted && !TransferValidation.IsSamePath(src, dest)
            && FileIdentity.SameEntryNotFollowing(src, dest))
            return true;

        var identityError = TransferValidation.CheckIdentity("cp", src, srcIsDir, dest, srcDisplay, destDisplay,
            makingLinks: _plan.Link != CpLink.None);
        if (identityError != null) { _error(identityError); return false; }

        var occupancyError = TransferValidation.CheckOccupancy("cp", src, srcIsDir, dest, replaceEmptyDirOnly: false,
            srcDisplay, destDisplay);
        if (occupancyError != null) { _error(occupancyError); return false; }

        // GNU never creates missing parent directories for the destination.
        var parent = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            _error($"cp: cannot create {(srcIsDir ? "directory" : "regular file")} '{destDisplay}': No such file or directory");
            return false;
        }

        try
        {
            return srcIsDir
                ? CopyDirectory(src, srcDisplay, dest, destDisplay)
                : CopyRegular(src, srcDisplay, dest, destDisplay, destExisted);
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            _error($"cp: cannot copy '{srcDisplay}' to '{destDisplay}': {FileTransfer.Reason(ex, src, srcDisplay)}");
            return false;
        }
    }

    /// <summary>Command-line operands follow a link under -L/-H (and by default when not recursive);
    /// entries found while recursing only under -L.</summary>
    private bool FollowsLink(bool commandLine) => commandLine
        ? _plan.Deref is CpDeref.Always or CpDeref.CommandLine || (_plan.Deref == CpDeref.Default && !_plan.Recursive)
        : _plan.Deref == CpDeref.Always;

    private void Skipped(string destDisplay)
    {
        if (_plan.Debug) _say($"skipped '{FileSystemHelpers.ToBashPath(destDisplay)}'\n");
    }

    // ───────────── regular files ─────────────

    private bool CopyRegular(string src, string srcDisplay, string dest, string destDisplay, bool destExisted)
    {
        if (destExisted)
        {
            // --update[=older] / -u: skip when the destination is not older than the source.
            if (_plan.UpdateOlder && File.Exists(dest)
                && File.GetLastWriteTimeUtc(src) <= File.GetLastWriteTimeUtc(dest))
            {
                Skipped(destDisplay);
                return true;
            }
            if (_plan.Existing == CpExisting.Ask && !_confirm($"cp: overwrite '{destDisplay}'? "))
                return false;
        }

        string? backupNote = null;
        if (destExisted)
        {
            if (_plan.BackupEnabled)
            {
                var suffix = BackupControl.MakeBackup(dest, _plan.Backup, _plan.BackupSuffix);
                if (suffix is not null) backupNote = $" (backup: '{FileSystemHelpers.ToBashPath(destDisplay)}{suffix}')";
            }
            // Still there after a backup (or none was asked): --remove-destination unlinks it first, and so
            // does -f for -l/-s (without -f the link primitive reports "File exists", as GNU does).
            if ((File.Exists(dest) || FileSystemHelpers.IsReparsePoint(dest))
                && (_plan.RemoveDestination || (_plan.Link != CpLink.None && _plan.Force)))
                FileSystemHelpers.DeleteFileForce(dest);
        }

        string Line() => $"'{FileSystemHelpers.ToBashPath(srcDisplay)}' -> '{FileSystemHelpers.ToBashPath(destDisplay)}'{backupNote}\n";
        bool say = _plan.Verbose || _plan.Debug;

        if (_plan.Link != CpLink.None)
        {
            if (say) _say(Line()); // GNU prints before attempting the link, even when it then fails
            return CreateLink(src, srcDisplay, dest, destDisplay);
        }

        if (_plan.Reflink == CpReflink.Always)
        {
            // No filesystem here clones extents (NTFS / ext4 without reflink): GNU's own refusal.
            if (say) _say(Line());
            _error($"cp: failed to clone '{destDisplay}' from '{srcDisplay}': Operation not supported");
            return false;
        }

        if (_plan.AttributesOnly)
        {
            if (!destExisted || backupNote is not null || !File.Exists(dest)) File.WriteAllBytes(dest, Array.Empty<byte>());
            ApplyAttributes(src, dest, isDir: false, _preserve, destExisted, previousMode: null);
        }
        else
        {
            if (_plan.Force) FileSystemHelpers.ClearReadOnly(dest);
            CopyFile(src, dest, _preserve);
        }

        if (say) _say(Line());
        if (_plan.Debug && !_plan.AttributesOnly)
        {
            var offload = _plan.Reflink == CpReflink.Never ? "avoided" : "yes";
            var reflink = _plan.Reflink == CpReflink.Never ? "no" : "unsupported";
            _say($"copy offload: {offload}, reflink: {reflink}, sparse detection: no\n");
        }
        return true;
    }

    private bool CreateLink(string src, string srcDisplay, string dest, string destDisplay)
    {
        if (_plan.Link == CpLink.Hard)
        {
            var reason = FileLinks.TryCreateHardLink(dest, src);
            if (reason is null) return true;
            _error($"cp: cannot create hard link '{destDisplay}' to '{srcDisplay}': {reason}");
            return false;
        }

        // -s: GNU refuses a relative source name unless the link lands in the current directory (the text
        // stored in the link is the source AS TYPED, so it would dangle from anywhere else).
        bool relative = !Path.IsPathRooted(srcDisplay);
        var destDir = Path.GetDirectoryName(Path.GetFullPath(dest)) ?? "";
        if (relative && !string.Equals(destDir.TrimEnd('/', '\\'), _cwd.TrimEnd('/', '\\'), PathComparison))
        {
            _error($"cp: {destDisplay}: can make relative symbolic links only in current directory");
            return false;
        }
        var sym = FileLinks.TryCreateSymlink(dest, srcDisplay, Directory.Exists(src));
        if (sym is null) return true;
        _error($"cp: cannot create symbolic link '{destDisplay}' to '{srcDisplay}': {sym}");
        return false;
    }

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    // ───────────── symbolic link operands copied AS links ─────────────

    private bool CopyLinkItself(string src, string srcDisplay, string dest, string destDisplay)
    {
        if (_plan.Existing == CpExisting.Skip && (File.Exists(dest) || FileSystemHelpers.IsReparsePoint(dest)))
        {
            Skipped(destDisplay);
            return true;
        }
        string? text;
        try { text = new FileInfo(src).LinkTarget ?? new DirectoryInfo(src).LinkTarget; }
        catch { text = null; }
        if (string.IsNullOrEmpty(text))
        {
            _error($"cp: cannot read symbolic link '{srcDisplay}': Invalid argument");
            return false;
        }

        string? backupNote = null;
        if (File.Exists(dest) || Directory.Exists(dest) || FileSystemHelpers.IsReparsePoint(dest))
        {
            if (_plan.Existing == CpExisting.Ask && !_confirm($"cp: overwrite '{destDisplay}'? ")) return false;
            if (_plan.BackupEnabled)
            {
                var suffix = BackupControl.MakeBackup(dest, _plan.Backup, _plan.BackupSuffix);
                if (suffix is not null) backupNote = $" (backup: '{FileSystemHelpers.ToBashPath(destDisplay)}{suffix}')";
            }
            if (File.Exists(dest) || FileSystemHelpers.IsReparsePoint(dest))
            {
                try
                {
                    if (Directory.Exists(dest) && FileSystemHelpers.IsReparsePoint(dest)) Directory.Delete(dest, false);
                    else FileSystemHelpers.DeleteFileForce(dest);
                }
                catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
                {
                    _error($"cp: cannot remove '{destDisplay}': {ex.Message}");
                    return false;
                }
            }
            else if (Directory.Exists(dest))
            {
                _error($"cp: cannot overwrite directory '{destDisplay}' with non-directory");
                return false;
            }
        }

        var reason = FileLinks.TryCreateSymlink(dest, text, Directory.Exists(src));
        if (reason is not null)
        {
            _error($"cp: cannot create symbolic link '{destDisplay}': {reason}");
            return false;
        }
        if (_plan.Verbose || _plan.Debug)
            _say($"'{FileSystemHelpers.ToBashPath(srcDisplay)}' -> '{FileSystemHelpers.ToBashPath(destDisplay)}'{backupNote}\n");
        return true;
    }

    // ───────────── directories ─────────────

    private bool CopyDirectory(string src, string srcDisplay, string dest, string destDisplay)
    {
        bool ok = true;
        bool destExisted = Directory.Exists(dest);
        int? previousMode = destExisted ? PlatformMode.TryGet(dest) : null;
        if (!destExisted)
        {
            Directory.CreateDirectory(dest);
            if (_plan.Verbose || _plan.Debug)
                _say($"'{FileSystemHelpers.ToBashPath(srcDisplay)}' -> '{FileSystemHelpers.ToBashPath(destDisplay)}'\n");
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(src))
        {
            var name = Path.GetFileName(entry);
            var childDest = Path.Combine(dest, name);
            var childSrcDisplay = FileSystemHelpers.JoinDisplay(srcDisplay, name);
            var childDestDisplay = FileSystemHelpers.JoinDisplay(destDisplay, name);

            // -x: do not descend into a directory on another file system (mount point).
            if (_rootDevice is not null && Directory.Exists(entry) && !FileSystemHelpers.IsReparsePoint(entry)
                && _deviceOf(entry) is { } dev && dev != _rootDevice)
                continue;

            if (!Copy(entry, childSrcDisplay, childDest, childDestDisplay, commandLine: false)) ok = false;
        }

        // Apply the directory's mode and timestamps LAST — writing children bumps the dir mtime
        // (GNU cp -p restores it after the contents are in place) and a read-only directory must
        // not block its own children.
        ApplyAttributes(src, dest, isDir: true, _preserve, destExisted, previousMode);
        return ok;
    }

    // ───────────── bytes and attributes ─────────────

    /// <summary>
    /// Copies one file and then applies the requested attribute policy (see <see cref="CpPreserve"/>). The
    /// destination's prior Unix mode is captured BEFORE the copy because <see cref="File.Copy(string, string, bool)"/>
    /// overwrites it with the source's. The bytes go through <see cref="FileTransfer.CopyFile"/> (an in-use
    /// destination is replaced, a held one reported with its process).
    /// </summary>
    private static void CopyFile(string src, string dest, CpPreserve preserve)
    {
        bool existed = File.Exists(dest);
        int? previousMode = existed ? PlatformMode.TryGet(dest) : null;
        FileTransfer.CopyFile(src, dest);
        ApplyAttributes(src, dest, isDir: false, preserve, existed, previousMode);
    }

    /// <summary>
    /// Applies the attribute policy to a finished copy, best-effort (a locked attribute or an unsupported
    /// timestamp must not fail the copy itself). <b>Mode</b> is the Unix permission bits on Linux/macOS — GNU
    /// semantics: preserved exactly, cleared to 0666/0777 masked by the umask, or by default the source's bits
    /// masked by the umask for a NEW file while an existing destination keeps its own; on Windows, where there
    /// are no mode bits, the read-only / hidden / archive attributes stand in. <b>Timestamps</b> are real on
    /// every OS. Ownership, links and xattr have nothing to do.
    /// </summary>
    private static void ApplyAttributes(string src, string dest, bool isDir, CpPreserve preserve,
        bool destExisted, int? previousMode)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (preserve.Mode == CpModePolicy.Preserve)
                {
                    if (isDir) new DirectoryInfo(dest).Attributes = new DirectoryInfo(src).Attributes;
                    else new FileInfo(dest).Attributes = new FileInfo(src).Attributes;
                }
                else if (preserve.Mode == CpModePolicy.Clear)
                {
                    FileSystemHelpers.ClearReadOnly(dest);
                }
            }
            else
            {
                PlatformMode.Apply(src, dest, isDir, preserve.Mode, destExisted, previousMode);
            }

            if (!preserve.Timestamps)
            {
                // File.Copy carries the source's modification time over on EVERY OS (Windows
                // CopyFile and .NET's Unix copy both do); GNU stamps the copy with "now" unless
                // timestamps are preserved.
                if (!isDir)
                {
                    var now = DateTime.UtcNow;
                    File.SetLastWriteTimeUtc(dest, now);
                    File.SetLastAccessTimeUtc(dest, now);
                }
            }
            else if (isDir)
            {
                var s = new DirectoryInfo(src);
                var d = new DirectoryInfo(dest);
                d.CreationTimeUtc = s.CreationTimeUtc;
                d.LastWriteTimeUtc = s.LastWriteTimeUtc;
                d.LastAccessTimeUtc = s.LastAccessTimeUtc;
            }
            else
            {
                File.SetCreationTimeUtc(dest, File.GetCreationTimeUtc(src));
                File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src));
                File.SetLastAccessTimeUtc(dest, File.GetLastAccessTimeUtc(src));
            }
        }
        catch
        {
            // Preservation is best-effort; see above.
        }
    }
}
