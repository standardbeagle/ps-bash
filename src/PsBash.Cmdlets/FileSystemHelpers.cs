using System.IO;
using System.Management.Automation;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// Shared utilities for the file-system mutator cmdlets — mkdir, rmdir, cp,
/// mv, rm — migrated from PsBash.psm1 in REFACTOR-2. Each method reproduces a
/// helper the psm1 oracle used (<c>Resolve-BashGlob</c>, <c>Get-BashItem</c>,
/// <c>Write-BashError</c>) so the cmdlets stay off the script-level helper
/// surface on their hot path.
/// </summary>
internal static class FileSystemHelpers
{
    /// <summary>
    /// Replicates the psm1 <c>Resolve-BashGlob</c> contract for a single
    /// operand: literal paths fall through unchanged so the caller can emit a
    /// bash-style "no such file" error on a missing target; wildcard paths
    /// (<c>*</c> / <c>?</c>) expand via
    /// <see cref="System.Management.Automation.PathIntrinsics.GetResolvedProviderPathFromPSPath(string, out ProviderInfo)"/>
    /// and fall through as literal if nothing matches. Same slice
    /// <see cref="InvokeBashCatCommand"/> and the ChecksumEngine use.
    /// </summary>
    public static IEnumerable<string> ResolveOperandPaths(PSCmdlet cmdlet, string raw)
    {
        raw = NormalizeOperandPath(raw);
        if (raw.IndexOf('*') < 0 && raw.IndexOf('?') < 0)
        {
            yield return cmdlet.SessionState.Path.GetUnresolvedProviderPathFromPSPath(raw);
            yield break;
        }

        var matched = new List<string>();
        try
        {
            foreach (var resolved in cmdlet.SessionState.Path
                         .GetResolvedProviderPathFromPSPath(raw, out _))
            {
                matched.Add(resolved);
            }
        }
        catch
        {
            // No matches — fall through to literal passthrough.
        }

        if (matched.Count == 0)
        {
            yield return raw;
        }
        else
        {
            foreach (var m in matched) yield return m;
        }
    }

    /// <summary>
    /// Emit a bash-style error to the cmdlet's error stream so that callers
    /// using <c>2&gt;$null</c> can suppress it, <c>2&gt;&amp;1</c> can merge
    /// it into the pipeline, and bash-mode production code prints to host
    /// stderr. Sets <c>$global:LASTEXITCODE = 1</c>.
    /// <para>
    /// Pester sets <c>$ErrorActionPreference = Stop</c> inside <c>It</c>
    /// blocks. The PowerShell runtime translates a non-terminating
    /// <see cref="PSCmdlet.WriteError"/> into a terminating
    /// <see cref="System.Management.Automation.PipelineStoppedException"/>
    /// AFTER the record has already been deposited into the cmdlet's
    /// error stream. We catch and swallow that exception so the cmdlet
    /// continues running; the ErrorRecord remains in the stream and is
    /// captured by the outer <c>2&gt;&amp;1</c> or filtered by
    /// <c>2&gt;$null</c>. This matches bash's "errors don't terminate the
    /// script unless <c>set -e</c>" semantics — which the transpiler models
    /// elsewhere through explicit <c>$global:__BashErrexit</c> flow.
    /// </para>
    /// </summary>
    public static void WriteBashError(PSCmdlet cmdlet, string message)
    {
        SetLastExitCode(cmdlet, 1);

        var record = new ErrorRecord(
            new System.IO.IOException(message),
            "BashError",
            ErrorCategory.NotSpecified,
            null);

        // Temporarily override $ErrorActionPreference to Continue so the
        // runtime treats WriteError as non-terminating regardless of what
        // the caller (Pester sets Stop inside It blocks) had configured.
        // The ErrorRecord still lands in the cmdlet's error stream — the
        // override only affects whether the pipeline terminates. Restore
        // the prior preference in a finally so we don't leak our setting
        // beyond the call. Bash's contract is non-terminating; the
        // transpiler models set -e explicitly via $global:__BashErrexit.
        object? prevEap = null;
        bool restoreEap = false;
        try
        {
            prevEap = cmdlet.SessionState.PSVariable.GetValue("ErrorActionPreference");
            cmdlet.SessionState.PSVariable.Set("ErrorActionPreference", "Continue");
            restoreEap = true;
        }
        catch { /* ignore — write below will still try */ }

        try
        {
            cmdlet.WriteError(record);
        }
        catch
        {
            // Defensive — should not throw with EAP=Continue, but if any
            // host swaps the runtime semantics we keep going.
        }
        finally
        {
            if (restoreEap)
            {
                try { cmdlet.SessionState.PSVariable.Set("ErrorActionPreference", prevEap); }
                catch { /* ignore */ }
            }
        }

        // Bash-mode formatting for the production host launcher path.
        try
        {
            cmdlet.InvokeCommand.InvokeScript(
                "param($m) Write-BashError -Message $m", message);
        }
        catch { /* benign — already emitted via WriteError */ }
    }

    /// <summary>
    /// True when <paramref name="ex"/> is the engine's pipeline-stop signal: a
    /// <see cref="System.Management.Automation.PipelineStoppedException"/>, which
    /// the internal <c>StopUpstreamCommandsException</c> raised by
    /// <c>Invoke-BashHead</c> / <c>Select-Object -First</c> also derives from.
    /// <para>
    /// A producer cmdlet's catch-all around its <c>WriteObject</c> loop MUST
    /// rethrow this instead of formatting it as a read error. When a downstream
    /// consumer stops the pipeline early — <c>cat file | head -n 5</c>,
    /// <c>rm -v * | head -1</c> — the stop unwinds through the producer's
    /// <c>WriteObject</c>; swallowing it emits a spurious
    /// <c>"cat: file: The pipeline has been stopped"</c> and sets exit 1, which
    /// is wrong (the consumer asked to stop; that is success, not an error).
    /// Guard every such catch with <c>if (FileSystemHelpers.IsPipelineStop(ex)) throw;</c>.
    /// </para>
    /// </summary>
    public static bool IsPipelineStop(Exception ex)
        => ex is System.Management.Automation.PipelineStoppedException
           || ex.InnerException is System.Management.Automation.PipelineStoppedException;

    /// <summary>
    /// Sets the bash-visible exit code via the global LASTEXITCODE. The psm1
    /// mutator oracles all do this on the last error in a multi-operand run;
    /// we mirror it exactly.
    /// </summary>
    public static void SetLastExitCode(PSCmdlet cmdlet, int code)
    {
        cmdlet.SessionState.PSVariable.Set(new PSVariable("global:LASTEXITCODE", code));
    }

    /// <summary>
    /// Bash-style path normalization for verbose output: backslash → slash.
    /// </summary>
    public static string ToBashPath(string winPath) => winPath.Replace('\\', '/');

    /// <summary>
    /// Map a raw file operand to a native Windows path before it reaches the
    /// PowerShell path provider, via the shared <see cref="WindowsPath"/> mapper.
    /// On Windows this rewrites unix-style drive paths (<c>/c/..</c>, <c>/mnt/c/..</c>)
    /// and canonicalizes native drive variants (<c>c:/..</c>) so a user (or LLM)
    /// who types a unix-shaped path gets the file they meant instead of a
    /// "No such file or directory" resolved against <c>C:\c\..</c>. No-op on
    /// non-Windows, where <c>/c/..</c> and <c>/mnt/c/..</c> may be real paths.
    /// This is the runtime safety net for the direct/interactive case; the
    /// transpiler handles the wrapper case (PSBASH_UNIX_PATHS) ahead of time.
    /// Both paths share the SAME <see cref="WindowsPath"/> rules.
    /// </summary>
    public static string NormalizeOperandPath(string raw)
        => OperatingSystem.IsWindows() ? WindowsPath.Normalize(raw) : raw;

    /// <summary>
    /// True when <paramref name="token"/> looks like an option (a dash flag),
    /// as opposed to an operand (file / pattern). A lone <c>-</c> (stdin) and
    /// the bare <c>--</c> end-of-options marker are NOT option-like — callers
    /// handle those separately. Mirrors how the GNU getopt parsers decide a
    /// token is an option before deciding it is unknown.
    /// </summary>
    public static bool IsOptionLike(string token)
        => token.Length > 1 && token[0] == '-' && token != "--";

    /// <summary>
    /// Emit the message + exit code for an option-looking token a cmdlet could
    /// not consume, classifying it against the command's known-valid flag
    /// universe (<paramref name="validButUnsupported"/>):
    /// <list type="bullet">
    /// <item><b>Valid bash flag we don't implement</b> (in the set) → a specific
    /// "recognized but not supported by ps-bash" message. This deliberately
    /// diverges from bash (which would honor the flag) in favor of a clear
    /// refusal over silently-wrong output — the project's stated policy that
    /// every valid bash parameter maps to *something*.</item>
    /// <item><b>Not a real flag</b> (typo / garbage) → bash-parity
    /// <c>unrecognized option '--foo'</c> (long) or <c>invalid option -- 'x'</c>
    /// (short), matching GNU getopt_long.</item>
    /// </list>
    /// Both set <c>$LASTEXITCODE = 2</c> (grep/getopt usage-error convention).
    /// The <paramref name="validButUnsupported"/> lookup strips any <c>=value</c>
    /// suffix from long options so <c>--include=*.c</c> matches <c>--include</c>.
    /// </summary>
    /// <summary>
    /// Scans a resolved operand list for an option-looking token (an unknown
    /// flag that fell through a cmdlet's parser into the operand/file list) and,
    /// if found, emits the classified option error (<see cref="WriteOptionError"/>)
    /// for the FIRST such token and returns <c>true</c> so the caller can bail
    /// before treating the flag as a filename. Returns <c>false</c> when every
    /// operand is genuine. Use this in file-mode cmdlets whose flag parser (e.g.
    /// <c>ConvertFromBashArgs</c> or a static scan) routes unknown flags into the
    /// operand list. Do NOT use it for commands whose operands may legitimately
    /// start with <c>-</c> (echo / printf / seq treat a leading dash as literal).
    /// </summary>
    public static bool TryWriteOperandOptionError(
        PSCmdlet cmdlet, string cmd, IEnumerable<string> operands,
        ISet<string> validButUnsupported)
    {
        foreach (var op in operands)
        {
            if (IsOptionLike(op))
            {
                WriteOptionError(cmdlet, cmd, op, validButUnsupported);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Handle a <c>--version</c> request uniformly across commands so tooling can identify ps-bash.
    /// When <paramref name="args"/> contains <c>--version</c>, emits a single GNU-style line
    /// <c>{commandName} (ps-bash) {version}</c> and returns true (the caller should then return).
    /// The version is the real ps-bash module version, read from the runspace global
    /// <c>$global:PsBashVersion</c> the module sets at load; falls back to "unknown" if unavailable.
    /// </summary>
    public static bool TryHandleVersion(PSCmdlet cmdlet, string commandName, string[]? args)
    {
        if (args is null || Array.IndexOf(args, "--version") < 0)
            return false;

        string version;
        try { version = cmdlet.GetVariableValue("global:PsBashVersion") as string ?? "unknown"; }
        catch { version = "unknown"; }

        cmdlet.WriteObject(BashRuntime.NewBashObject($"{commandName} (ps-bash) {version}\n"));
        return true;
    }

    public static void WriteOptionError(
        PSCmdlet cmdlet, string cmd, string token,
        ISet<string> validButUnsupported)
    {
        string lookup = token;
        if (token.StartsWith("--", StringComparison.Ordinal))
        {
            int eq = token.IndexOf('=');
            if (eq >= 0) lookup = token.Substring(0, eq);
        }

        bool isLong = token.StartsWith("--", StringComparison.Ordinal);
        // For a short bundle (-Zx) the offending option is the first char
        // that is not recognized; the catalog stores single-letter short
        // flags as e.g. "-P", so probe the bundle char-by-char.
        if (!isLong && token.Length > 2)
        {
            foreach (var ch in token.Substring(1))
            {
                string single = "-" + ch;
                if (validButUnsupported.Contains(single))
                {
                    lookup = single;
                    break;
                }
            }
        }

        if (validButUnsupported.Contains(lookup))
        {
            WriteBashError(cmdlet, $"{cmd}: option '{lookup}' is recognized but not supported by ps-bash");
        }
        else if (isLong)
        {
            WriteBashError(cmdlet, $"{cmd}: unrecognized option '{token}'");
        }
        else
        {
            // GNU getopt reports the first offending short char.
            char bad = token.Length > 1 ? token[1] : '-';
            WriteBashError(cmdlet, $"{cmd}: invalid option -- '{bad}'");
        }
        SetLastExitCode(cmdlet, 2);
    }

    // ───────────────────────── Destructive filesystem ops (OS interface) ─────────────────────────
    //
    // The ONE place that knows how to force-delete on every platform. The native
    // Directory.Delete / File.Delete throw UnauthorizedAccessException on a read-only
    // descendant on Windows (.git pack/object files, some node_modules), even though a
    // bash force-delete (rm -rf, mv overwrite, cp -rf overwrite, find -delete) should
    // remove it. Every destructive cmdlet routes through these so the read-only fallback
    // is fixed once, not re-derived (or forgotten) per command. Linux unlink needs no
    // fallback — dir-write suffices, the bit is absent, and the fast native path wins.

    /// <summary>
    /// Recursively delete <paramref name="dir"/>. Tries the fast native recursive delete
    /// first; on <see cref="UnauthorizedAccessException"/> (a read-only descendant on
    /// Windows) falls back to a bottom-up walk that clears the read-only bit on each entry.
    /// </summary>
    public static void DeleteDirectoryForce(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
            ForceDeleteDirectoryRecursive(dir);
        }
    }

    /// <summary>
    /// Delete a single file. On <see cref="UnauthorizedAccessException"/> (Windows
    /// read-only bit) clears the attribute and retries once.
    /// </summary>
    public static void DeleteFileForce(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException)
        {
            ClearReadOnly(path);
            File.Delete(path);
        }
    }

    /// <summary>
    /// Delete a single filesystem entry — directory (recursive, force) or file — choosing
    /// the right force-delete primitive. Convenience for callers (e.g. <c>find -delete</c>)
    /// that hold a mixed list of matched paths.
    /// </summary>
    public static void DeleteEntryForce(string path, bool isDirectory)
    {
        if (isDirectory) DeleteDirectoryForce(path);
        else DeleteFileForce(path);
    }

    /// <summary>
    /// Bottom-up recovery walk: clear the read-only attribute on each entry before deleting.
    /// Uses the array <c>GetFiles</c>/<c>GetDirectories</c> (safe to delete during iteration)
    /// — this is the rare recovery path, not the hot one, so the extra arrays are acceptable.
    /// </summary>
    private static void ForceDeleteDirectoryRecursive(string dir)
    {
        foreach (var file in Directory.GetFiles(dir))
        {
            ClearReadOnly(file);
            File.Delete(file);
        }
        foreach (var sub in Directory.GetDirectories(dir))
        {
            ForceDeleteDirectoryRecursive(sub);
        }
        ClearReadOnly(dir);
        Directory.Delete(dir, recursive: false);
    }

    /// <summary>
    /// True when <paramref name="path"/> denotes the platform null device — bash's
    /// <c>/dev/null</c> (also the Windows provider-resolved form <c>X:\dev\null</c> that
    /// <c>GetUnresolvedProviderPathFromPSPath</c> produces) or the Windows <c>NUL</c> device.
    /// As a command OPERAND the null device is an empty file: <see cref="BashFileSystem.OpenRead"/>
    /// serves it from the OS-native device, and the existence pre-checks in grep/wc treat it as
    /// present rather than emitting "No such file or directory".
    /// </summary>
    public static bool IsNullDevice(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string slashed = path.Replace('\\', '/');
        if (slashed.Equals("/dev/null", StringComparison.OrdinalIgnoreCase)
            || slashed.EndsWith("/dev/null", StringComparison.OrdinalIgnoreCase))
            return true;
        if (OperatingSystem.IsWindows()
            && (path.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || slashed.EndsWith("/NUL", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    /// <summary>Clear the read-only attribute on <paramref name="path"/> if set. Best-effort.</summary>
    public static void ClearReadOnly(string path)
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
