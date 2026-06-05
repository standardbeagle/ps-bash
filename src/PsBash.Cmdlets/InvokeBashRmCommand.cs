using System.Linq;
using System.Management.Automation;
using System.Runtime.InteropServices;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashRm</c> (REFACTOR-2).
/// Removes each operand, matching GNU coreutils <c>rm</c> semantics with the
/// project's added safety guards (path-root protection, Windows
/// reserved-device-name refusal). Supports <c>-r</c> / <c>-R</c> (recursive),
/// <c>-f</c> (force — suppresses missing-file errors and the missing-operand
/// error), <c>-v</c> (verbose).
///
/// Behavioral parity oracle: the original psm1 function. Branches preserved:
/// <list type="bullet">
/// <item>No operands and <c>-f</c> not set → "missing operand" error.
/// With <c>-f</c>, silent.</item>
/// <item>Windows reserved device name in the leaf → refuse with "Windows
/// reserved device name" error and $LASTEXITCODE=1. Preserves the psm1
/// oracle's CON / PRN / AUX / NUL / COM1-9 / LPT1-9 list.</item>
/// <item>Path equals a drive root or user-profile root → refuse with
/// "refusing to remove: protected path".</item>
/// <item>Target missing and <c>-f</c> not set → "No such file or directory"
/// error and $LASTEXITCODE=1. With <c>-f</c>, silent skip.</item>
/// <item>Target is a directory and <c>-r</c> not set → "Is a directory"
/// error and $LASTEXITCODE=1.</item>
/// <item>Verbose mode emits <c>removed '&lt;path&gt;'\n</c> per file; with
/// <c>-rv</c> over a directory, lists each child first, then the directory.</item>
/// </list>
/// <para>
/// <b>One colliding flag</b> declared explicitly: <c>-v</c> vs
/// <c>-Verbose</c>. <c>-r</c> / <c>-R</c> / <c>-f</c> stay in
/// <c>Arguments</c>.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashRm")]
[OutputType(typeof(string))]
public sealed class InvokeBashRmCommand : PSCmdlet
{
    [Parameter] public SwitchParameter v { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    private static readonly HashSet<string> WinReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "rm"))
            {
                WriteObject(line);
            }
            return;
        }

        bool recursive = false;
        bool force = false;
        bool verbose = v.IsPresent;
        var operands = new List<string>();

        foreach (var a in args)
        {
            switch (a)
            {
                case "-r": case "-R": recursive = true; break;
                case "-f": force = true; break;
                case "-v": verbose = true; break;
                default:
                    // Bundled short flags: -rf, -fr, -rvf, etc. Each char
                    // maps to one of r/R/f/v. Anything else is an operand.
                    if (a.Length > 2 && a[0] == '-'
                        && a.Skip(1).All(ch => ch == 'r' || ch == 'R' || ch == 'f' || ch == 'v'))
                    {
                        foreach (var ch in a.Skip(1))
                        {
                            if (ch == 'r' || ch == 'R') recursive = true;
                            else if (ch == 'f') force = true;
                            else if (ch == 'v') verbose = true;
                        }
                    }
                    else
                    {
                        operands.Add(a);
                    }
                    break;
            }
        }

        if (operands.Count == 0)
        {
            if (!force)
            {
                FileSystemHelpers.WriteBashError(this, "rm: missing operand");
            }
            return;
        }

        var resolved = new List<string>();
        foreach (var op in operands)
        {
            foreach (var expanded in FileSystemHelpers.ResolveOperandPaths(this, op))
            {
                resolved.Add(expanded);
            }
        }

        bool hadError = false;
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var target in resolved)
        {
            // Windows reserved-device-name guard (psm1 oracle parity).
            if (isWindows)
            {
                var leaf = Path.GetFileName(target.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var baseName = leaf.Contains('.')
                    ? leaf.Substring(0, leaf.IndexOf('.'))
                    : leaf;
                if (WinReservedNames.Contains(baseName))
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"rm: cannot remove '{target}': Windows reserved device name");
                    hadError = true;
                    continue;
                }
            }

            // Protected-path guard: refuse to delete the drive root or the
            // current user's home directory. Mirrors the psm1 oracle's
            // "refusing to remove: protected path" branch.
            string? resolvedFull = null;
            try
            {
                if (File.Exists(target) || Directory.Exists(target))
                {
                    resolvedFull = Path.GetFullPath(target);
                }
            }
            catch { /* fall through */ }

            if (resolvedFull is not null)
            {
                var normalized = resolvedFull.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var pathRoot = Path.GetPathRoot(resolvedFull) ?? string.Empty;
                var normalizedRoot = pathRoot.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                bool isProtected = false;

                // Filesystem root: on POSIX `Path.GetPathRoot("/")` returns
                // "/" which trims to "" — both the path and root are empty
                // after trimming. Catch this case explicitly so the protected
                // guard fires for "/" (not just for a non-empty Windows
                // drive root like "C:").
                if (string.IsNullOrEmpty(normalized) && !string.IsNullOrEmpty(pathRoot))
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"rm: refusing to remove '{target}': protected path");
                    hadError = true;
                    isProtected = true;
                }
                else if (!string.IsNullOrEmpty(normalizedRoot) &&
                         string.Equals(normalized, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"rm: refusing to remove '{target}': protected path");
                    hadError = true;
                    isProtected = true;
                }
                else if (!string.IsNullOrEmpty(homeDir))
                {
                    var normalizedHome = homeDir.TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (!string.IsNullOrEmpty(normalizedHome) &&
                        string.Equals(normalized, normalizedHome, StringComparison.OrdinalIgnoreCase))
                    {
                        FileSystemHelpers.WriteBashError(this,
                            $"rm: refusing to remove '{target}': protected path");
                        hadError = true;
                        isProtected = true;
                    }
                }
                if (isProtected) continue;
            }

            bool isFile = File.Exists(target);
            bool isDir = !isFile && Directory.Exists(target);

            if (!isFile && !isDir)
            {
                if (!force)
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"rm: cannot remove '{target}': No such file or directory");
                    hadError = true;
                }
                continue;
            }

            if (isDir && !recursive)
            {
                FileSystemHelpers.WriteBashError(this,
                    $"rm: cannot remove '{target}': Is a directory");
                hadError = true;
                continue;
            }

            if (verbose && isDir && recursive)
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(
                             target, "*", SearchOption.AllDirectories))
                {
                    WriteObject(BashRuntime.NewBashObject(
                        $"removed '{FileSystemHelpers.ToBashPath(child)}'\n"));
                }
            }

            if (verbose)
            {
                WriteObject(BashRuntime.NewBashObject(
                    $"removed '{FileSystemHelpers.ToBashPath(target)}'\n"));
            }

            try
            {
                if (isDir)
                {
                    try
                    {
                        Directory.Delete(target, recursive: true);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // A read-only file/dir in the tree makes the native recursive delete throw on
                        // Windows (e.g. .git pack/object files, some node_modules), even though
                        // non-interactive `rm` should remove it. Retry clearing the read-only bit as
                        // we go. Linux unlink doesn't need this (dir-write suffices), so the bit is
                        // simply absent there and this branch rarely fires.
                        ForceDeleteDirectory(target);
                    }
                }
                else
                {
                    try
                    {
                        File.Delete(target);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        ClearReadOnly(target);
                        File.Delete(target);
                    }
                }
            }
            catch (Exception ex)
            {
                FileSystemHelpers.WriteBashError(this,
                    $"rm: cannot remove '{target}': {ex.Message}");
                hadError = true;
                continue;
            }
        }

        if (hadError) FileSystemHelpers.SetLastExitCode(this, 1);
    }

    /// <summary>
    /// Recursively delete <paramref name="dir"/> bottom-up, clearing the read-only attribute on each
    /// entry first. Fallback for when the native <see cref="Directory.Delete(string,bool)"/> throws
    /// <see cref="UnauthorizedAccessException"/> on a read-only descendant (common on Windows). Uses
    /// the array <c>GetFiles</c>/<c>GetDirectories</c> (safe to delete during iteration) — this is the
    /// rare recovery path, not the hot one, so the extra arrays are acceptable.
    /// </summary>
    private static void ForceDeleteDirectory(string dir)
    {
        foreach (var file in Directory.GetFiles(dir))
        {
            ClearReadOnly(file);
            File.Delete(file);
        }
        foreach (var sub in Directory.GetDirectories(dir))
        {
            ForceDeleteDirectory(sub);
        }
        ClearReadOnly(dir);
        Directory.Delete(dir, recursive: false);
    }

    /// <summary>Clear the read-only attribute on <paramref name="path"/> if set. Best-effort.</summary>
    private static void ClearReadOnly(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch { /* best-effort: if attrs can't be read/set, let the delete surface the real error */ }
    }
}
