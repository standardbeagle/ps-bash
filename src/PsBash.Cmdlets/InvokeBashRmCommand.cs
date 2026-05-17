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
                default: operands.Add(a); break;
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
                var protectedRoots = new List<string>
                {
                    Path.GetPathRoot(resolvedFull)?.TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? "",
                };
                if (!string.IsNullOrEmpty(homeDir))
                {
                    protectedRoots.Add(homeDir.TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }
                bool isProtected = false;
                foreach (var root in protectedRoots)
                {
                    if (!string.IsNullOrEmpty(root) &&
                        string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase))
                    {
                        FileSystemHelpers.WriteBashError(this,
                            $"rm: refusing to remove '{target}': protected path");
                        hadError = true;
                        isProtected = true;
                        break;
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
                    Directory.Delete(target, recursive: true);
                }
                else
                {
                    File.Delete(target);
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
}
