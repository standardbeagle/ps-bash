using System.Management.Automation;
using System.Runtime.InteropServices;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashInstall</c>
/// (REFACTOR-2). Copies files and sets attributes, with special handling for
/// in-use binaries on Windows. Behavioral parity oracle: the original psm1
/// function. Supports GNU coreutils <c>install</c> flag surface as the psm1
/// oracle implemented it:
///
/// <list type="bullet">
/// <item><c>-d</c> create-directories mode.</item>
/// <item><c>-D</c> create-leading-path-components for each copy.</item>
/// <item><c>-m MODE</c> mode (tracked, not enforced on Windows).</item>
/// <item><c>-v</c> verbose output.</item>
/// <item><c>-s</c> strip (no-op on Windows; preserved for arg compat).</item>
/// <item><c>-t TARGET_DIR</c> target directory.</item>
/// <item><c>-S SUFFIX</c> swap suffix (default <c>.old</c>).</item>
/// </list>
///
/// <para><b>Windows binary swap:</b> when the destination file already exists
/// and is locked (the running-binary case), the existing file is renamed to
/// <c>{dest}{suffix}</c>, the new file is copied to the destination, then the
/// renamed-aside file is scheduled for deletion at next reboot via Win32
/// <c>MoveFileEx</c> with <c>MOVEFILE_DELAY_UNTIL_REBOOT</c>. The psm1 oracle
/// did this via <c>Add-Type</c> P/Invoke; we use a <c>[DllImport]</c>
/// directly so the cmdlet stays AOT-safe.</para>
///
/// <para><b>Colliding flags (case-insensitive cmdlet binder):</b></para>
/// <list type="bullet">
/// <item><c>-d</c> prefix-collides with <c>-Debug</c> → declared as explicit
/// <see cref="SwitchParameter"/> <c>D</c>. The case-insensitive binder
/// collapses bash's <c>-d</c> (create-directories) and <c>-D</c>
/// (create-leading-path-components) onto the same <c>D</c> switch. The two
/// cannot be expressed independently in one invocation. Because <c>-D</c>
/// already implies the leaf must exist (it creates leading components for an
/// upcoming copy), we route both tokens through the same code branch: when
/// <c>D</c> is set <em>and</em> operands look like directories (no
/// <c>-t TARGET</c>), behave as <c>-d</c> create-dirs; when <c>-t TARGET</c>
/// is supplied or there are &gt;= 2 operands, treat the bit as
/// create-leading-components for the copy path. This is the documented gap.
/// </item>
/// <item><c>-v</c> prefix-collides with <c>-Verbose</c> → declared as
/// explicit <see cref="SwitchParameter"/> <c>V</c>.</item>
/// <item><c>-m MODE</c> has no common-parameter prefix collision and stays in
/// <c>Arguments</c>, parsed by the manual value-flag scan.</item>
/// <item><c>-t TARGET</c> has no common-parameter prefix collision and stays
/// in <c>Arguments</c>.</item>
/// <item><c>-s</c> / <c>-S SUFFIX</c> have no common-parameter prefix
/// collision (no <c>-S*</c> common parameter exists) and both stay in
/// <c>Arguments</c>. The case-insensitive binder collapses <c>-s</c> and
/// <c>-S</c> onto the same parameter name — but since neither is declared,
/// the catch-all <c>Arguments</c> consumes both. The manual scan walks them
/// case-sensitively (<see cref="string.Equals(string?, string?, StringComparison)"/>
/// with <see cref="StringComparison.Ordinal"/>) so the strip-flag (<c>-s</c>)
/// and the swap-suffix-value (<c>-S SUFFIX</c>) stay distinct.</item>
/// </list>
///
/// <para><b>Directive 12:</b> all operand and value tokens flow through
/// <see cref="System.IO"/> APIs directly. No <see cref="ScriptBlock"/> is
/// constructed; the only call back to psm1 is the parameter-bound
/// <c>Write-BashError</c> shim via <see cref="FileSystemHelpers.WriteBashError"/>.
/// A path containing <c>$(throw 'pwn')</c> reaches <see cref="File.Copy(string,string,bool)"/>
/// as a literal string and either succeeds or fails the usual no-such-file
/// branch.</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashInstall")]
[OutputType(typeof(string))]
public sealed class InvokeBashInstallCommand : PSCmdlet
{
    [Parameter] public SwitchParameter D { get; set; }
    [Parameter] public SwitchParameter V { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);

    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "install"))
            {
                WriteObject(line);
            }
            return;
        }

        // The cmdlet binder consumes -d / -D into the D switch. Both bash
        // flags (-d create-dirs, -D create-leading-components) live behind
        // that single bit; the documented gap is that -d and -D cannot be
        // distinguished in one invocation.
        bool dBit = D.IsPresent;
        bool verbose = V.IsPresent;
        string? mode = null;
        string? targetDir = null;
        string swapSuffix = ".old";
        bool sawStrip = false;
        var operands = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            // Case-sensitive scan: the case-insensitive cmdlet binder ran
            // BEFORE we got here, so any -d / -D that the binder would have
            // consumed is already gone — they bound to the D switch. The
            // remaining -d / -D tokens here can only arrive if the binder
            // declined them (it never does for declared switches), so this
            // arm is defensive: re-route a bare token to the D bit.
            if (a == "-d" || a == "-D")
            {
                dBit = true;
                continue;
            }
            if (a == "-v")
            {
                verbose = true;
                continue;
            }
            if (a == "-s")
            {
                // GNU install: strip the binary. No-op on Windows in the
                // oracle; preserved here.
                sawStrip = true;
                continue;
            }
            if (a == "-m")
            {
                if (i + 1 < args.Length)
                {
                    mode = args[++i];
                }
                continue;
            }
            if (a.StartsWith("-m", StringComparison.Ordinal) && a.Length > 2)
            {
                mode = a.Substring(2);
                continue;
            }
            if (a == "-t")
            {
                if (i + 1 < args.Length)
                {
                    targetDir = args[++i];
                }
                continue;
            }
            if (a.StartsWith("-t", StringComparison.Ordinal) && a.Length > 2)
            {
                targetDir = a.Substring(2);
                continue;
            }
            // Case-sensitive: bash's -S (uppercase) is the swap-suffix
            // value-bearing flag. The cmdlet binder doesn't claim -S/-s
            // (we don't declare them), so they pass through to Arguments
            // and we route them here.
            if (a == "-S")
            {
                if (i + 1 < args.Length)
                {
                    swapSuffix = args[++i];
                }
                continue;
            }
            if (a.StartsWith("-S", StringComparison.Ordinal) && a.Length > 2)
            {
                swapSuffix = a.Substring(2);
                continue;
            }
            if (a == "--")
            {
                // End of flags. Remainder are operands.
                for (int j = i + 1; j < args.Length; j++) operands.Add(args[j]);
                break;
            }
            operands.Add(a);
        }
        _ = mode;       // tracked, not enforced on Windows (oracle parity)
        _ = sawStrip;   // accepted for arg-compat (oracle parity)

        // --------------------------------------------------------------
        // -d / -D ambiguity resolution: the case-insensitive cmdlet binder
        // collapses bash -d (create-dirs) and -D (create-leading-components)
        // onto the same D switch. We disambiguate by looking at the operands:
        //   • dBit && no -t && no operand exists as a file → create-dirs.
        //   • dBit && (>=2 operands and the first IS a file) → copy with
        //     leading-component creation.
        // This works for the common cases: `install -d dirA dirB` (no
        // existing files) and `install -D src dest` (src exists).
        // --------------------------------------------------------------
        bool anyOperandIsFile = false;
        foreach (var op in operands)
        {
            var oabs = SessionState.Path.GetUnresolvedProviderPathFromPSPath(op);
            if (File.Exists(oabs)) { anyOperandIsFile = true; break; }
        }

        if (dBit && targetDir == null && !anyOperandIsFile && operands.Count >= 1)
        {
            // Treat as -d create-directories mode (oracle's first branch).
            foreach (var dir in operands)
            {
                var abs = SessionState.Path.GetUnresolvedProviderPathFromPSPath(dir);
                if (!Directory.Exists(abs) && !File.Exists(abs))
                {
                    try
                    {
                        Directory.CreateDirectory(abs);
                    }
                    catch (Exception ex)
                    {
                        FileSystemHelpers.WriteBashError(this,
                            $"install: cannot create directory '{dir}': {ex.Message}");
                        FileSystemHelpers.SetLastExitCode(this, 1);
                        continue;
                    }
                    if (verbose)
                    {
                        WriteObject(BashRuntime.NewBashObject(
                            $"install: creating directory '{FileSystemHelpers.ToBashPath(dir)}'\n"));
                    }
                }
            }
            return;
        }

        // --------------------------------------------------------------
        // Copy mode. Oracle: <2 operands without -t is a usage error.
        // --------------------------------------------------------------
        if (operands.Count < 2 && targetDir == null)
        {
            FileSystemHelpers.WriteBashError(this, "install: missing file operand");
            return;
        }

        string destRaw;
        List<string> sourceOperands;
        if (targetDir != null)
        {
            destRaw = targetDir;
            sourceOperands = operands;
        }
        else
        {
            destRaw = operands[^1];
            sourceOperands = operands.GetRange(0, operands.Count - 1);
        }

        var sources = new List<string>();
        foreach (var s in sourceOperands)
        {
            foreach (var expanded in FileSystemHelpers.ResolveOperandPaths(this, s))
            {
                sources.Add(expanded);
            }
        }

        var destAbs = SessionState.Path.GetUnresolvedProviderPathFromPSPath(destRaw);

        // -D / dBit on the copy path → create leading components of dest.
        if (dBit)
        {
            string? destParent = targetDir != null
                ? destAbs
                : Path.GetDirectoryName(destAbs);
            if (!string.IsNullOrEmpty(destParent) && !Directory.Exists(destParent))
            {
                try
                {
                    Directory.CreateDirectory(destParent);
                    if (verbose)
                    {
                        WriteObject(BashRuntime.NewBashObject(
                            $"install: creating directory '{FileSystemHelpers.ToBashPath(destParent)}'\n"));
                    }
                }
                catch (Exception ex)
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"install: cannot create directory '{destParent}': {ex.Message}");
                    FileSystemHelpers.SetLastExitCode(this, 1);
                    return;
                }
            }
        }

        bool hadError = false;
        bool destIsExistingDir = Directory.Exists(destAbs);
        // If -t TARGET was supplied, treat target as a directory even if it
        // doesn't yet exist (mirroring the oracle's `Resolve-BashGlob` slice
        // and dirent assumption).
        if (targetDir != null && !destIsExistingDir)
        {
            try { Directory.CreateDirectory(destAbs); } catch { /* fall through to per-source error */ }
            destIsExistingDir = Directory.Exists(destAbs);
        }

        foreach (var src in sources)
        {
            if (!File.Exists(src) && !Directory.Exists(src))
            {
                FileSystemHelpers.WriteBashError(this,
                    $"install: cannot stat '{src}': No such file or directory");
                hadError = true;
                continue;
            }

            string targetPath = destAbs;
            if (destIsExistingDir)
            {
                var basename = Path.GetFileName(src.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                targetPath = Path.Combine(destAbs, basename);
            }

            // Ensure the immediate parent dir exists. The oracle did this
            // unconditionally inside the copy loop too.
            var targetParent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetParent) && !Directory.Exists(targetParent))
            {
                try { Directory.CreateDirectory(targetParent); } catch { /* swallow — handled by copy error path */ }
            }

            bool swapped = false;
            string oldPath = targetPath + swapSuffix;

            if (File.Exists(targetPath))
            {
                // Windows binary-swap path. Try a move-aside; if it succeeds
                // we proceed to copy. If the move fails (target not locked,
                // for instance), fall back to a direct copy.
                try
                {
                    if (File.Exists(oldPath)) File.Delete(oldPath);
                    File.Move(targetPath, oldPath);
                    swapped = true;
                    if (verbose)
                    {
                        WriteObject(BashRuntime.NewBashObject(
                            $"install: swapped '{FileSystemHelpers.ToBashPath(targetPath)}' -> '{FileSystemHelpers.ToBashPath(oldPath)}'\n"));
                    }
                }
                catch
                {
                    // Move-aside failed; fall through to direct overwrite.
                    swapped = false;
                }
            }

            try
            {
                if (Directory.Exists(src))
                {
                    // Oracle behavior is file-oriented; a directory source
                    // emits a copy-failure error and continues.
                    FileSystemHelpers.WriteBashError(this,
                        $"install: cannot install '{src}': Is a directory");
                    hadError = true;
                    continue;
                }
                File.Copy(src, targetPath, overwrite: true);
            }
            catch (Exception ex)
            {
                FileSystemHelpers.WriteBashError(this,
                    $"install: cannot install '{src}': {ex.Message}");
                hadError = true;
                continue;
            }

            if (swapped)
            {
                bool scheduled = false;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
                        if (MoveFileEx(oldPath, null, MOVEFILE_DELAY_UNTIL_REBOOT))
                        {
                            scheduled = true;
                        }
                    }
                    catch
                    {
                        scheduled = false;
                    }
                }
                if (!scheduled)
                {
                    try { File.Delete(oldPath); }
                    catch
                    {
                        if (verbose)
                        {
                            WriteObject(BashRuntime.NewBashObject(
                                $"install: note: '{FileSystemHelpers.ToBashPath(oldPath)}' will be deleted when unlocked\n"));
                        }
                    }
                }
                else if (verbose)
                {
                    WriteObject(BashRuntime.NewBashObject(
                        $"install: scheduled deletion of '{FileSystemHelpers.ToBashPath(oldPath)}' on reboot\n"));
                }
            }

            if (verbose)
            {
                WriteObject(BashRuntime.NewBashObject(
                    $"'{FileSystemHelpers.ToBashPath(src)}' -> '{FileSystemHelpers.ToBashPath(targetPath)}'\n"));
            }
        }

        if (hadError) FileSystemHelpers.SetLastExitCode(this, 1);
    }
}
