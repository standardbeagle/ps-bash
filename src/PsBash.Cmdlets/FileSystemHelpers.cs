using System.Globalization;
using System.Collections.Generic;
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
    /// (<c>*</c> / <c>?</c> / a <c>[..]</c> character class) expand via
    /// <see cref="System.Management.Automation.PathIntrinsics.GetResolvedProviderPathFromPSPath(string, out ProviderInfo)"/>
    /// and fall through as literal if nothing matches. Same slice
    /// <see cref="InvokeBashCatCommand"/> and the ChecksumEngine use.
    /// </summary>
    /// <summary>
    /// <c>GetUnresolvedProviderPathFromPSPath</c> for a file operand, minus its one throw that is not
    /// a path error: a drive PowerShell does not know (<c>Q:\x</c>, or <c>/q/x</c> mapped under
    /// <c>PSBASH_UNIX_PATHS</c>) raised <c>DriveNotFoundException</c> ("Cannot find drive") out of the
    /// cmdlet — outside its per-file error handling, so <c>cat /x</c> printed PowerShell's text and
    /// exited 0. Such a path is returned as typed; the file access that follows then fails with the
    /// ordinary not-found error every cmdlet already reports bash-style (status 1).
    /// </summary>
    public static string ProviderPath(PSCmdlet cmdlet, string psPath)
    {
        // An EMPTY operand names no file. PowerShell resolves "" to the CURRENT DIRECTORY, so
        // `rm -rf "$unset"` deleted everything in the cwd (bash: nothing — `rm: cannot remove ''`,
        // silent under -f) and `touch ''` hit the directory. Keep it empty: the access that follows
        // fails "No such file or directory" like any missing operand. Checked BEFORE the mapping below
        // so no path policy can ever turn "" into a real path.
        if (string.IsNullOrEmpty(psPath)) return string.Empty;
        // A device name is not a path (DevicePath): it is returned as written, never resolved against the current
        // drive - `/dev/stdout` became `C:\dev\stdout`, or `<root>\dev\stdout` on a PSDrive, a file to create.
        if (DevicePath.IsDevice(psPath)) return psPath;
        // The runtime path policy (RuntimePath.Map: /tmp → $env:TEMP, unix drive paths) — this is
        // the chokepoint most cmdlets resolve operands through, so an expanded `$d/f` (d=/tmp) reaches
        // the same file the emitter's literal /tmp rewrite does. Idempotent on an already-mapped path.
        string typed = psPath;
        psPath = NormalizeOperandPath(psPath);
        string resolved;
        try { resolved = cmdlet.SessionState.Path.GetUnresolvedProviderPathFromPSPath(psPath); }
        catch (System.Management.Automation.DriveNotFoundException) { return psPath; }
        // A diagnostic names the operand as typed (OperandDisplay). Without this, a message about an operand under the
        // working directory fell to OperandDisplay's cwd-stripping guess: `cat "$PWD/x"` and `cat ./x` both said
        // `cat: x: ...` where GNU says `cat: /full/x` and `cat: ./x`.
        OperandDisplay.Remember(cmdlet, resolved, typed);
        return resolved;
    }

    /// <summary>
    /// True when the cmdlet runs in a runspace that executes TRANSPILED bash (the ps-bash host sets
    /// <c>$global:__BashOperandsExpanded</c>, as does a background job's runspace). There the shell has already
    /// expanded every unquoted pattern, so an operand that still holds <c>*</c> / <c>?</c> / <c>[</c> was quoted or
    /// escaped and names a file literally: <c>head 'two-*'</c> is "cannot open 'two-*'", never <c>two-bar</c>.
    /// A direct PowerShell call (<c>Invoke-BashHead *.txt</c>) has no shell in front of it, so the cmdlet keeps
    /// globbing its operands there.
    /// </summary>
    public static bool OperandsShellExpanded(PSCmdlet cmdlet)
    {
        try { return LanguagePrimitives.IsTrue(cmdlet.SessionState.PSVariable.GetValue("global:__BashOperandsExpanded")); }
        catch (InvalidOperationException) { return false; }   // not running in a session (unit-constructed cmdlet)
    }

    /// <summary>
    /// Whether a file operand is a pattern the CMDLET must expand: it holds <c>*</c> or <c>?</c> and no shell
    /// expanded it first (<see cref="OperandsShellExpanded"/>). The per-cmdlet glob slices route through this.
    /// </summary>
    public static bool IsCmdletGlobOperand(PSCmdlet cmdlet, string path) =>
        (path.IndexOf('*') >= 0 || path.IndexOf('?') >= 0) && !OperandsShellExpanded(cmdlet);

    public static IEnumerable<string> ResolveOperandPaths(PSCmdlet cmdlet, string raw, bool expandWildcards = true)
    {
        string typed = raw;
        raw = NormalizeOperandPath(raw);
        expandWildcards = expandWildcards && !OperandsShellExpanded(cmdlet);
        // *, ?, or a [..] character class → wildcard. A lone unmatched '[' matches
        // nothing and falls through to literal passthrough (bash-literal semantics).
        if (!expandWildcards || (raw.IndexOf('*') < 0 && raw.IndexOf('?') < 0 && raw.IndexOf('[') < 0))
        {
            string resolved = FileSystemHelpers.ProviderPath(cmdlet, raw);
            OperandDisplay.Remember(cmdlet, resolved, typed);
            yield return resolved;
            yield break;
        }

        // The shell has already expanded an unquoted pattern, so what arrives here is a NAME: a file
        // that literally exists under this spelling (`a[1]`, whose class form would match `a1`) is
        // that file, never a pattern.
        string literal = FileSystemHelpers.ProviderPath(cmdlet, raw);
        if (File.Exists(literal) || Directory.Exists(literal))
        {
            OperandDisplay.Remember(cmdlet, literal, typed);
            yield return literal;
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
    /// A resolved operand: <see cref="Path"/> is what the cmdlet touches (absolute, provider-resolved),
    /// <see cref="Display"/> is what a diagnostic must show — the operand AS TYPED, because GNU
    /// quotes the argv element (<c>rm: cannot remove 'nosuch'</c>), never the resolved full path.
    /// </summary>
    public readonly record struct OperandPath(string Path, string Display)
    {
        /// <summary>
        /// Make this operand the name <see cref="OperandDisplay"/> shows for <see cref="Path"/> in a message
        /// built from the path. Call it as each operand's turn comes when the operands were resolved AHEAD of
        /// use: two operands naming one file (<c>wc ./f f</c>) share a Path, and the table holds the latest
        /// spelling, so without a claim every message about the first said <c>f</c>. Output a cmdlet builds
        /// itself (a header, wc's name column) uses <see cref="Display"/> directly.
        /// </summary>
        public void Claim(PSCmdlet cmdlet)
        {
            if (Path != "-") OperandDisplay.Remember(cmdlet, Path, Display);
        }
    }

    /// <summary>
    /// The file operands of a reader that takes <c>-</c> as the pipeline (head, tail, wc), in argv order:
    /// <c>-</c> stays <c>-</c>, everything else is <see cref="ResolveOperands"/>. The one resolver for them
    /// (each kept a private copy that answered with bare paths, so a name had to be guessed back from the path).
    /// </summary>
    public static IEnumerable<OperandPath> ResolveReaderOperands(PSCmdlet cmdlet, IEnumerable<string> raws)
    {
        foreach (var raw in raws)
        {
            if (raw == "-") { yield return new OperandPath("-", "-"); continue; }
            foreach (var op in ResolveOperands(cmdlet, raw)) yield return op;
        }
    }

    /// <summary>
    /// <see cref="ResolveReaderOperands"/> for a cmdlet that needs only the paths: LAZY, each operand
    /// <see cref="OperandPath.Claim"/>ed as it is yielded, so a diagnostic about it names it as typed.
    /// Enumerate it once, processing each path before taking the next (never <c>ToList()</c> it - use
    /// <see cref="ResolveReaderOperands"/> and claim at each turn instead).
    /// </summary>
    public static IEnumerable<string> ClaimedReaderPaths(PSCmdlet cmdlet, IEnumerable<string> raws)
    {
        foreach (var op in ResolveReaderOperands(cmdlet, raws))
        {
            op.Claim(cmdlet);
            yield return op.Path;
        }
    }

    /// <summary>
    /// <see cref="ResolveOperandPaths"/> plus the name to print for each result. A literal operand
    /// (or a wildcard that matched nothing) displays exactly as typed; a wildcard match displays as
    /// the shell would have expanded it — relative to the working directory for a relative pattern,
    /// full path for an absolute one — with forward slashes.
    /// </summary>
    public static IEnumerable<OperandPath> ResolveOperands(PSCmdlet cmdlet, string raw, bool expandWildcards = true)
    {
        expandWildcards = expandWildcards && !OperandsShellExpanded(cmdlet);
        bool wildcard = expandWildcards && (raw.IndexOf('*') >= 0 || raw.IndexOf('?') >= 0 || raw.IndexOf('[') >= 0);
        var paths = new List<string>(ResolveOperandPaths(cmdlet, raw, expandWildcards));
        string? cwd = null;
        try { cwd = cmdlet.SessionState.Path.CurrentFileSystemLocation.ProviderPath; } catch { /* non-filesystem location */ }

        foreach (var p in paths)
        {
            // Literal operand, or a wildcard nothing matched (ResolveOperandPaths passes it through
            // normalized, not resolved): show what was typed.
            if (!wildcard || !System.IO.Path.IsPathRooted(p))
            {
                yield return new OperandPath(p, raw);
                continue;
            }

            string display = p;
            if (!System.IO.Path.IsPathRooted(raw) && cwd is not null)
            {
                try { display = System.IO.Path.GetRelativePath(cwd, p); } catch { display = p; }
            }
            yield return new OperandPath(p, BashPath.ToShell(display));
        }
    }

    /// <summary>
    /// The diagnostic name of <paramref name="src"/> landing inside the directory operand
    /// <paramref name="destDisplay"/>: GNU joins the destination AS TYPED with the source's basename
    /// (<c>cp f dir/</c> reports <c>'dir/f'</c>, <c>cp f .</c> reports <c>'./f'</c>).
    /// </summary>
    public static string JoinDisplay(string destDisplay, string srcDisplay) =>
        AppendDisplay(destDisplay, System.IO.Path.GetFileName(srcDisplay.TrimEnd('/', '\\')));

    /// <summary>
    /// <paramref name="relative"/> below the directory operand <paramref name="dirDisplay"/> as typed
    /// (<c>rm -rv d</c> reports <c>'d/sub/f'</c>): one slash between, forward slashes throughout.
    /// </summary>
    public static string AppendDisplay(string dirDisplay, string relative)
    {
        var dir = dirDisplay.Length > 1 ? dirDisplay.TrimEnd('/', '\\') : dirDisplay;
        relative = BashPath.ToShell(relative);
        return dir.EndsWith('/') ? dir + relative : dir + "/" + relative;
    }

    /// <summary>
    /// The strerror text for a failed read open: a missing file / directory, or a name Windows cannot
    /// even parse (<c>*</c>, <c>?</c> in an unmatched glob: ERROR_INVALID_NAME), is ENOENT's
    /// "No such file or directory"; anything else keeps the exception's own message. The one
    /// definition every reader uses (the per-cmdlet copies missed the invalid-name case, so
    /// <c>cat '*.zz'</c> said "The filename, directory name, or volume label syntax is incorrect").
    /// </summary>
    /// <summary>
    /// <see cref="ReadErrorMessage(Exception)"/> knowing the path: on a drive that does not exist
    /// (<c>/x</c> → <c>X:\</c> under <c>PSBASH_UNIX_PATHS</c>) .NET raises "Access to the path 'X:\'
    /// is denied" for the bare root; bash reports the path missing.
    /// </summary>
    public static string ReadErrorMessage(Exception ex, string path)
    {
        if (OperatingSystem.IsWindows() && Path.GetPathRoot(path) is { Length: 3 } root
            && root[1] == ':' && !Directory.Exists(root))
            return "No such file or directory";
        return ReadErrorMessage(ex);
    }

    public static string ReadErrorMessage(Exception ex)
    {
        static bool NotFound(Exception e) =>
            e is FileNotFoundException or DirectoryNotFoundException
            || (OperatingSystem.IsWindows() && e is IOException && (e.HResult & 0xFFFF) == 0x7B); // ERROR_INVALID_NAME
        // EACCES: no access, or (Windows only) a file another process holds open without sharing —
        // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION. The raw .NET text ("The process cannot
        // access the file … because it is being used by another process") is no bash message.
        static bool Denied(Exception e) =>
            e is UnauthorizedAccessException
            || (OperatingSystem.IsWindows() && e is IOException && (e.HResult & 0xFFFF) is 0x20 or 0x21);
        if (NotFound(ex) || (ex.InnerException is { } inner && NotFound(inner)))
            return "No such file or directory";
        if (Denied(ex) || (ex.InnerException is { } inner2 && Denied(inner2)))
            return "Permission denied";
        return ex.Message;
    }

    /// <summary>
    /// Emit a bash-style error to the cmdlet's error stream so that callers
    /// using <c>2&gt;$null</c> can suppress it, <c>2&gt;&amp;1</c> can merge
    /// it into the pipeline, and the ps-bash host (SdkWorker) prints it to
    /// stderr inline, exactly once. Sets <c>$global:LASTEXITCODE = 1</c>.
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
        WriteStderr(cmdlet, message);
    }

    /// <summary>
    /// One line on the command's stderr WITHOUT touching the exit status: the error stream is the
    /// only channel the host delivers to stderr, so an interactive prompt (<c>rm -i</c>) that GNU
    /// writes there rides it too. The host ends the line; GNU's prompt has no newline.
    /// </summary>
    public static void WriteStderr(PSCmdlet cmdlet, string message)
    {
        // Every diagnostic names an operand as typed, not its resolved full path (OperandDisplay).
        WriteStderrRecord(cmdlet, OperandDisplay.Rewrite(cmdlet, message), null);
    }

    /// <summary>
    /// Exact bytes on the command's stderr (a child process's captured stderr): not rewritten, and no newline is
    /// added when the text does not end in one (<see cref="PsBash.Core.StderrRecord"/>). Through the error stream,
    /// so <c>2&gt; f</c>, <c>2&gt;&amp;1</c> and <c>$( … 2&gt;&amp;1)</c> see it — Console.Error bypasses them.
    /// </summary>
    public static void WriteStderrBytes(PSCmdlet cmdlet, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        WriteStderrRecord(cmdlet, text, text.EndsWith('\n') ? null : PsBash.Core.StderrRecord.NoTrailingNewlineMarker);
    }

    private static void WriteStderrRecord(PSCmdlet cmdlet, string message, object? targetObject)
    {
        var record = new ErrorRecord(
            new System.IO.IOException(message),
            "BashError",
            ErrorCategory.NotSpecified,
            targetObject);
        WriteErrorRecord(cmdlet, record);
    }

    /// <summary>
    /// <paramref name="record"/> on the cmdlet's error stream, never terminating: bash stderr does not stop a command,
    /// and <c>set -e</c> is modelled by the transpiler, not by <c>$ErrorActionPreference</c>. Also for a record
    /// forwarded from a nested invocation (<c>command time true</c> under <c>set -o errexit</c> stopped on the
    /// report <c>time</c> wrote).
    /// </summary>
    public static void WriteErrorRecord(PSCmdlet cmdlet, ErrorRecord record)
    {
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
        catch (Exception ex) when (!IsPipelineStop(ex))
        {
            // Defensive — should not throw with EAP=Continue, but if any
            // host swaps the runtime semantics we keep going. A downstream stop
            // (`cmd 2>&1 | head -1`) is NOT swallowed: it must unwind the producer.
        }
        finally
        {
            if (restoreEap)
            {
                try { cmdlet.SessionState.PSVariable.Set("ErrorActionPreference", prevEap); }
                catch { /* ignore */ }
            }
        }

        // The ErrorRecord above is the ONLY channel. Do not also call the psm1
        // Write-BashError here: in Bash error mode it writes through
        // $Host.UI.WriteErrorLine, which PowerShell redirection cannot see — so
        // `cat missing 2>/dev/null` still printed, and an unredirected error
        // printed twice (host line inline + the record SdkWorker delivers).
        // The host renders the record itself, inline (SdkWorker streams
        // Streams.Error as records arrive).
    }

    /// <summary>
    /// True when <paramref name="ex"/> is the engine's pipeline-stop signal: a
    /// <see cref="System.Management.Automation.PipelineStoppedException"/>: what the
    /// commands before <c>Invoke-BashHead</c> / <c>Select-Object -First</c> see when
    /// it stops them (the engine turns its internal <c>StopUpstreamCommandsException</c>,
    /// a FlowControlException, into this). Only a steppable pipeline's host sees the
    /// raw request (<see cref="UpstreamStop.RequestedBy"/>; InvokeBashPipeStageCommand).
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
        if (cmdlet is BashCmdlet own) own.OwnStatus = code; // re-asserted after the stages ahead of it (BashCmdlet)
        InvokeBashPipeStageCommand.NoteStatus(code); // the stage's own status (PIPESTATUS when a later stage stops it)
    }

    /// <summary>
    /// <paramref name="cmdlet"/> ran another command (<c>command ls</c>, <c>env prog</c>, <c>flock f cmd</c>) and that
    /// command's status is now the cmdlet's own. A wrapper sets 0 as it starts and the command it runs sets
    /// <c>$LASTEXITCODE</c> as ITSELF (a native, a script, another cmdlet's instance), so the wrapper's recorded status
    /// stayed that 0, and <see cref="BashCmdlet"/> re-asserted it over the command's (<c>echo x | command ls /nosuch</c>
    /// gave 0, GNU 2). Call this right after the nested command returns.
    /// </summary>
    public static void AdoptLastExitCode(PSCmdlet cmdlet)
    {
        if (cmdlet.SessionState.PSVariable.GetValue("global:LASTEXITCODE") is { } v
            && LanguagePrimitives.TryConvertTo<int>(v, out int code))
            SetLastExitCode(cmdlet, code);
    }

    /// <summary>
    /// A WORD failed to expand (<c>$((1/0))</c>): status 1, and <c>$global:__BashExpansionFailed</c> is set to the
    /// word's source position (<c>line:column</c>) so the command it belongs to can abandon itself -- echo through
    /// <see cref="TakeExpansionFailed"/>, a hoisted word through the emitter's <c>PsBuild.AbandonIfExpansionFailed</c>.
    /// </summary>
    public static void MarkExpansionFailed(PSCmdlet cmdlet)
    {
        cmdlet.SessionState.PSVariable.Set(new PSVariable("global:LASTEXITCODE", 1));
        var at = cmdlet.MyInvocation;
        cmdlet.SessionState.PSVariable.Set("global:__BashExpansionFailed",
            at.ScriptLineNumber.ToString(CultureInfo.InvariantCulture) + ":" + at.OffsetInLine.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// True when a word of THIS command failed to expand: the mark's position lies inside the command's own
    /// statement text. The mark is cleared either way, so one left by an earlier command (which did not look at
    /// it: <c>true $((1/0))</c>) can never make a later command abandon itself.
    /// </summary>
    public static bool TakeExpansionFailed(PSCmdlet cmdlet)
    {
        object? mark = cmdlet.SessionState.PSVariable.GetValue("global:__BashExpansionFailed");
        if (mark is null or false) return false;
        var me = cmdlet.MyInvocation;
        bool mine = IsMarkInStatement(mark, me);
        // Every element of a pipeline begins before the first one ends, so `echo "$((1/0))" 2>>f` (echo | Invoke-BashRedirect)
        // has the redirect look first. A later element leaves a mark it does not own for the earlier one to take.
        if (mine || me.PipelinePosition <= 1)
            cmdlet.SessionState.PSVariable.Set("global:__BashExpansionFailed", false);
        return mine;
    }

    private static bool IsMarkInStatement(object mark, InvocationInfo me)
    {
        if (mark is not string where) return false;
        int colon = where.IndexOf(':');
        if (colon < 0 || !int.TryParse(where.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out int line)
            || !int.TryParse(where.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int column))
            return false;
        string statement = me.Statement ?? "";
        int firstLineLength = statement.IndexOf('\n') is var nlAt and >= 0 ? nlAt : statement.Length;
        if (line == me.ScriptLineNumber) return column >= me.OffsetInLine && column < me.OffsetInLine + firstLineLength;
        // A statement written over several lines: the word is on one of its later lines.
        int extraLines = statement.AsSpan().Count('\n');
        return line > me.ScriptLineNumber && line <= me.ScriptLineNumber + extraLines;
    }

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
        => RuntimePath.Map(raw);

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

    /// <summary>
    /// Report the first scan error of the shared ordered parser
    /// (<see cref="Args.ParsedArgs.Error"/>) with its GNU wording and exit status
    /// (<see cref="Args.ParsedArgs.ErrorExitCode"/>: 2 for a valid-but-unsupported option, else the
    /// command's usage status — 1 for GNU file tools), and return <c>true</c> so the caller can bail. Returns <c>false</c> when the parse was clean.
    /// The message text lives in <see cref="Args.ArgError.Message"/> (pure, unit-tested); this
    /// is only the cmdlet-side sink.
    /// </summary>
    public static bool TryWriteParseError(PSCmdlet cmdlet, string cmd, Args.ParsedArgs parsed)
    {
        if (parsed.Error is not { } err) return false;
        string message = err.Message(cmd);
        // A usage error is followed by the tool's hint line(s); ps-bash's own "recognized but not supported" is not one.
        if (err.Kind != Args.ArgErrorKind.ValidButUnsupported && UsageHint(cmd) is { } hint) message += "\n" + hint;
        WriteBashError(cmdlet, message);
        SetLastExitCode(cmdlet, parsed.ErrorExitCode);
        return true;
    }

    /// <summary>
    /// GNU's "missing operand" usage error, the ONE spelling every command uses: <c>CMD: missing operand</c> (or
    /// <c>missing operand after 'LAST'</c>), then the tool's <see cref="UsageHint"/>, status <paramref name="exitCode"/>
    /// (1 for coreutils; <c>expr</c> 2, <c>nohup</c>/<c>env</c> 125). Guard: <c>MissingOperandGuardTests</c>.
    /// </summary>
    public static void WriteMissingOperand(PSCmdlet cmdlet, string cmd, string? after = null, int exitCode = 1, string? why = null)
    {
        WriteBashError(cmdlet, MissingOperandMessage(cmd, after, why));
        SetLastExitCode(cmdlet, exitCode);
    }

    /// <summary>
    /// The directory that holds the entry <paramref name="path"/> names: trailing separators are not a path component
    /// (<c>two/</c> names <c>two</c>, whose parent is <c>.</c>), so they are dropped first, a root kept as it is.
    /// <see cref="Path.GetDirectoryName(string)"/> of <c>C:\x\two\</c> is <c>C:\x\two</c> itself. REGRESSION:
    /// <c>mkdir two/</c> failed "No such file or directory" because that "parent" did not exist yet.
    /// </summary>
    public static string? ParentDirectory(string path) => Path.GetDirectoryName(WithoutTrailingSeparators(path));

    /// <summary><paramref name="path"/> without its trailing separators (a root is kept as it is).</summary>
    private static string WithoutTrailingSeparators(string path)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(path);
        while (trimmed.Length < path.Length && trimmed.Length > 0)
        {
            path = trimmed;
            trimmed = Path.TrimEndingDirectorySeparator(path);
        }
        return path;
    }

    /// <summary>How a command is about to use a name, for <see cref="SlashedNameErrno"/>: the system call decides the errno.</summary>
    public enum NameUse
    {
        /// <summary>stat(2): cp, mv and install look the destination up first; touch stamps it.</summary>
        Stat,
        /// <summary>open(2) with O_CREAT for writing: a redirect, tee, <c>sort -o</c>, uniq's OUTPUT.</summary>
        OpenForWrite,
        /// <summary>link(2) / symlink(2): ln.</summary>
        Link,
    }

    /// <summary>ENOENT's text, as <see cref="SlashedNameErrno"/> returns it.</summary>
    public const string NoSuchFileOrDirectory = "No such file or directory";

    /// <summary>
    /// POSIX path resolution: a name that ends in <c>/</c> must resolve to a DIRECTORY. The errno text the system call of
    /// <paramref name="use"/> fails with when <paramref name="path"/> ends in a separator and is not an existing directory
    /// (a link to one is), else null - the caller then goes on as usual. open(O_CREAT) is always "Is a directory"; stat is
    /// "Not a directory" for an existing non-directory, else ENOENT; link/symlink are "File exists" for any existing
    /// entry, else ENOENT. Windows and .NET drop the slash, so without this <c>ln a eight/</c> made a file <c>eight</c>
    /// and <c>install a thirteen/</c> a directory. Guard: <c>TrailingSlashOperandGuardTests</c>.
    /// </summary>
    public static string? SlashedNameErrno(string path, NameUse use)
    {
        if (!Path.EndsInDirectorySeparator(path) || Directory.Exists(path)) return null;
        var name = WithoutTrailingSeparators(path);
        if (name.Length == path.Length) return null;   // a root
        if (use == NameUse.OpenForWrite) return "Is a directory";
        bool exists = File.Exists(name) || IsReparsePoint(name);
        return use == NameUse.Link
            ? exists ? "File exists" : NoSuchFileOrDirectory
            : exists ? "Not a directory" : NoSuchFileOrDirectory;
    }

    /// <summary>
    /// The one check before a file is opened for writing by name (<see cref="RedirectFileWriter.OpenFile"/>, a redirect,
    /// <c>sort -o</c>): throws an <see cref="IOException"/> whose message is the errno text (<see cref="SlashedNameErrno"/>,
    /// <see cref="NameUse.OpenForWrite"/>), which <see cref="ReadErrorMessage(Exception)"/> passes through.
    /// </summary>
    public static void ThrowIfNotWritableName(string path)
    {
        if (SlashedNameErrno(path, NameUse.OpenForWrite) is { } why) throw new IOException(why);
    }

    /// <summary>
    /// The file tools' spelling of <see cref="WriteMissingOperand"/> (cp, mv, ln, install, touch): <c>CMD: missing file
    /// operand</c>, or <c>CMD: missing destination file operand after 'SRC'</c> (the source quoted as <c>quoteaf</c>),
    /// then the usage hint, status 1. REGRESSION: touch and install left out the hint.
    /// </summary>
    public static void WriteMissingFileOperand(PSCmdlet cmdlet, string cmd, string? destinationAfter = null)
    {
        string message = destinationAfter is null
            ? $"{cmd}: missing file operand"
            : $"{cmd}: missing destination file operand after {GnuQuote.Always(destinationAfter)}";
        WriteBashError(cmdlet, UsageHint(cmd) is { } hint ? message + "\n" + hint : message);
        SetLastExitCode(cmdlet, 1);
    }

    /// <summary>
    /// GNU's "extra operand" usage error (<see cref="ExtraOperandMessage"/>), status <paramref name="status"/> (1; sort
    /// and diff 2).
    /// </summary>
    public static void WriteExtraOperand(PSCmdlet cmdlet, string cmd, string operand, string? why = null, int status = 1)
    {
        WriteBashError(cmdlet, ExtraOperandMessage(cmd, operand, why));
        SetLastExitCode(cmdlet, status);
    }

    /// <summary>
    /// The text of GNU's "extra operand" usage error, the one spelling every command uses (guard
    /// <c>ExtraOperandGuardTests</c>): <c>CMD: extra operand ‘X’</c>, then <paramref name="sameLine"/> on that line (sort
    /// -c's " not allowed with -c"), an optional explanation line (<paramref name="why"/>: tr's "Only one string may be
    /// given ..."), and the tool's usage hint unless <paramref name="hint"/> is false. The operand is quoted as the tool
    /// quotes it (<see cref="QuotesExtraOperandForTheShell"/>). REGRESSION: comm, base64, uniq and split spelled it
    /// themselves with ASCII quotes and no hint, date with ASCII quotes, basename named no operand at all.
    /// </summary>
    public static string ExtraOperandMessage(string cmd, string operand, string? why = null, string? sameLine = null, bool hint = true)
    {
        string quoted = QuotesExtraOperandForTheShell(cmd) ? GnuQuote.Always(operand) : GnuQuote.Locale(operand);
        string message = $"{cmd}: extra operand {quoted}{sameLine}";
        if (why is not null) message += "\n" + why;
        if (hint && UsageHint(cmd) is { } usage) message += "\n" + usage;
        return message;
    }

    /// <summary>
    /// The tools whose "extra operand" quotes the operand for the shell (<c>quoteaf</c>: <c>'x'</c>, also under a UTF-8
    /// locale) rather than with the locale's quotation marks (<c>quote()</c>: <c>‘x’</c>): GNU coreutils 9.4 cp, mv, ln,
    /// install, join, sort and wc, and diffutils' diff (WSL Ubuntu-24.04, C.UTF-8).
    /// </summary>
    internal static bool QuotesExtraOperandForTheShell(string cmd)
        => cmd is "cp" or "mv" or "ln" or "install" or "join" or "sort" or "wc" or "diff";

    /// <summary>
    /// The text of <see cref="WriteMissingOperand"/> for a pure plan that carries its error as a string: the
    /// first line (the operand after which one is missing quoted as gnulib <c>quote()</c>,
    /// <c>chmod: missing operand after ‘755’</c>), then the usage hint.
    /// </summary>
    public static string MissingOperandMessage(string cmd, string? after = null, string? why = null)
    {
        string message = after is null ? $"{cmd}: missing operand" : $"{cmd}: missing operand after {GnuQuote.Locale(after)}";
        if (why is not null) message += "\n" + why; // tr: "Two strings must be given when translating."
        return UsageHint(cmd) is { } hint ? message + "\n" + hint : message;
    }

    /// <summary>What a tool prints after a usage error's first line: <see cref="PsBash.Core.UsageErrors.Hint"/>.</summary>
    internal static string? UsageHint(string cmd) => PsBash.Core.UsageErrors.Hint(cmd);

    /// <summary>
    /// Act on a <c>--help</c> / <c>--version</c> the shared parser resolved from an ABBREVIATION
    /// (<c>--vers</c>); the exact spellings are handled earlier by each cmdlet. Requires the
    /// command's <see cref="Args.OptSpecSet"/> to be built with <c>gnuInfoOptions: true</c>.
    /// Returns true when it produced the output and the cmdlet should return.
    /// </summary>
    public static bool TryHandleInfoOptions(PSCmdlet cmdlet, string cmd, Args.ParsedArgs parsed)
    {
        if (parsed.Has(Args.OptSpecSet.VersionId))
            return TryHandleVersion(cmdlet, cmd, new[] { "--version" });

        if (parsed.Has(Args.OptSpecSet.HelpId))
        {
            foreach (var line in cmdlet.InvokeCommand.InvokeScript("param($n) Show-BashHelp $n", cmd))
                cmdlet.WriteObject(line);
            return true;
        }
        return false;
    }

    public static void WriteOptionError(
        PSCmdlet cmdlet, string cmd, string token,
        ISet<string> validButUnsupported, int usageExitCode = 2)
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
        if (UsageHint(cmd) is { } hint) WriteBashError(cmdlet, hint);
        SetLastExitCode(cmdlet, usageExitCode);
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
        StepOutOfDirectory(dir);
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // IOException too: a read-only DIRECTORY itself (not just a read-only descendant) makes
            // the native recursive delete throw IOException on Windows. The fallback clears the bit
            // and retries; a genuine failure resurfaces from the fallback.
            try { ForceDeleteDirectoryRecursive(dir); }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                // A sharing violation is often transient (an indexer/AV scan still holding a freshly written
                // file): retry a few times with a short backoff before reporting it.
                for (int attempt = 1; ; attempt++)
                {
                    // A handle this process opened and abandoned (an enumerator or stream the pipeline stopped
                    // reading, closed only by its finalizer) blocks the delete on Windows but never on bash:
                    // release those first, then back off 100..500 ms (~9 s in all) for an external scanner.
                    // Bounded: a finalizer closing a handle with a pending synchronous read can block for good.
                    if (attempt == 1) WindowsFileLockers.ReleaseOwnCompatSessions(dir);
                    if (attempt <= 3)
                        System.Threading.Tasks.Task.Run(() => { GC.Collect(); GC.WaitForPendingFinalizers(); }).Wait(2000);
                    System.Threading.Thread.Sleep(Math.Min(100 * attempt, 500));
                    try { ForceDeleteDirectoryRecursive(dir); return; }
                    catch (IOException) when (attempt < 20) { }
                }
            }
        }
    }

    /// <summary>
    /// bash can remove its own working directory (`cd "$tmp"; rm -rf "$tmp"` is the standard EXIT-trap cleanup);
    /// Windows refuses while the process's current directory is inside it. Move the process out, to the parent
    /// of the directory being removed, first.
    /// </summary>
    internal static void StepOutOfDirectory(string dir)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
            string cwd = Path.TrimEndingDirectorySeparator(Directory.GetCurrentDirectory());
            if (cwd.Equals(full, StringComparison.OrdinalIgnoreCase)
                || cwd.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                if (Path.GetDirectoryName(full) is { } parent) Directory.SetCurrentDirectory(parent);
            }
        }
        catch { /* best effort: the delete below reports a genuine failure */ }
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
            // A directory symlink / junction must be UNLINKED, not recursed into.
            // Recursing would enumerate and delete the link TARGET's contents — a
            // destructive escape out of the tree being removed — and a cycle
            // (link -> ancestor) would recurse until the stack overflows.
            // Directory.Delete(recursive:false) removes the link itself, matching
            // what the fast Directory.Delete(recursive:true) path already does.
            if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0)
            {
                ClearReadOnly(sub);
                Directory.Delete(sub, recursive: false);
            }
            else
            {
                ForceDeleteDirectoryRecursive(sub);
            }
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
    public static bool IsNullDevice(string? path) => DevicePath.IsNull(path);

    /// <summary>
    /// True when <paramref name="operand"/> names the command's own standard input: <c>-</c>, or the device names
    /// <c>/dev/stdin</c> / <c>/dev/fd/0</c> (<see cref="DevicePath"/>). A cmdlet's stdin is its pipeline input, so it
    /// is read by the cmdlet, never opened: <see cref="BashFileSystem.OpenRead"/> refuses the device name.
    /// </summary>
    public static bool IsStdinOperand(string? operand) =>
        operand == "-" || DevicePath.Classify(operand) == DeviceKind.Stdin;

    /// <summary>
    /// True when <paramref name="path"/> is a device a command can read from or stat without a file behind it
    /// (<see cref="DevicePath"/>; everything but an unopened <c>/dev/fd/N</c>): the existence pre-checks treat it as
    /// present instead of reporting "No such file or directory" for <c>C:\dev\zero</c>.
    /// </summary>
    public static bool IsDevicePresent(string? path)
    {
        var kind = DevicePath.Classify(path, out int fd);
        return kind == DeviceKind.Fd ? BashFdTable.IsOpen(fd) : kind != DeviceKind.None;
    }

    /// <summary>
    /// Applies a compiled chmod mode (<see cref="FileModeSpec"/>) to a directory. Unix sets the exact
    /// permission bits (<see cref="File.SetUnixFileMode(string, UnixFileMode)"/>, which also carries
    /// setuid/setgid/sticky). Windows has no mode bits and no faithful ACL mapping, so only the
    /// representable part is honoured: a mode WITHOUT the owner-write bit sets the read-only
    /// attribute, one with it clears it. <see cref="DeleteDirectoryForce"/> / <see cref="ClearReadOnly"/>
    /// already cope with a read-only directory.
    /// </summary>
    public static void ApplyDirectoryMode(string path, int mode)
    {
        if (OperatingSystem.IsWindows())
        {
            var attrs = File.GetAttributes(path);
            var wanted = FileModeSpec.OwnerCanWrite(mode)
                ? attrs & ~FileAttributes.ReadOnly
                : attrs | FileAttributes.ReadOnly;
            if (wanted != attrs) File.SetAttributes(path, wanted);
        }
        else
        {
            File.SetUnixFileMode(path, (UnixFileMode)mode);
        }
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

    // ───────────────────────── Reparse-point-safe enumeration (OS interface) ─────────────────────────
    //
    // The ONE place that knows how to walk a tree WITHOUT following directory symlinks /
    // junctions. Recursing into a reparse point is a destructive escape out of the tree being
    // walked (cp -r would copy / tar -c would pack / find -delete would remove the link
    // TARGET's contents) and a self- or ancestor-referential link recurses unbounded. The
    // delete path (ForceDeleteDirectoryRecursive) already treats reparse points as leaves;
    // cp -r / find / tar -c route their recursive walk through EnumerateNoFollow so they do too.

    private static readonly EnumerationOptions _noFollowEnumOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        RecurseSubdirectories = false,
    };

    /// <summary>True when <paramref name="path"/> is a directory symlink / junction (reparse point).</summary>
    public static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    /// <summary>
    /// Depth-first pre-order enumeration of <paramref name="root"/>'s descendants that treats a
    /// directory reparse point (junction / symlink) as a LEAF: the link entry is yielded but is
    /// NEVER descended into. Mirrors GNU find's default (no <c>-follow</c>) and the leaf treatment
    /// in <see cref="ForceDeleteDirectoryRecursive"/>. <paramref name="maxDepth"/> is the maximum
    /// relative depth below <paramref name="root"/> (root's direct children are depth 1);
    /// <see cref="int.MaxValue"/> = unlimited. Per-directory enumeration errors are swallowed
    /// (best-effort), matching the callers' prior behaviour.
    /// </summary>
    public static IEnumerable<FileSystemInfo> EnumerateNoFollow(DirectoryInfo root, int maxDepth = int.MaxValue)
        => EnumerateNoFollowCore(root, 1, maxDepth);

    /// <summary>
    /// One directory's entries (files, subdirectories, links; no <c>.</c>/<c>..</c>), in the OS's own listing
    /// order, with per-directory enumeration errors swallowed (best-effort: an unreadable directory lists as empty).
    /// The shared listing step of <see cref="EnumerateNoFollow"/> and of walkers that decide per entry whether to
    /// descend (<c>find -prune</c>).
    /// </summary>
    public static List<FileSystemInfo> ListChildrenNoFollow(DirectoryInfo dir)
    {
        try { return new List<FileSystemInfo>(dir.EnumerateFileSystemInfos("*", _noFollowEnumOptions)); }
        catch { return new List<FileSystemInfo>(); }
    }

    private static IEnumerable<FileSystemInfo> EnumerateNoFollowCore(DirectoryInfo dir, int depth, int maxDepth)
    {
        var children = ListChildrenNoFollow(dir);

        foreach (var child in children)
        {
            yield return child;
            if (depth < maxDepth
                && child is DirectoryInfo sub
                && (sub.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                foreach (var descendant in EnumerateNoFollowCore(sub, depth + 1, maxDepth))
                    yield return descendant;
            }
        }
    }

    /// <summary>
    /// Best-effort recreate a directory symlink / junction at <paramref name="destLink"/> pointing
    /// at the same target as <paramref name="srcLink"/>, WITHOUT copying the target's contents.
    /// Used by <c>cp -r</c> so a junction in the source tree is copied as a link, never followed.
    /// If the platform / privileges disallow link creation (Windows without Developer Mode), the
    /// link is silently skipped — safer than recursing into the target.
    /// </summary>
    public static void TryCopyDirectoryLink(string srcLink, string destLink)
    {
        try
        {
            string? target = new DirectoryInfo(srcLink).LinkTarget;
            if (string.IsNullOrEmpty(target)) return;
            if (Directory.Exists(destLink) || File.Exists(destLink)) return;
            Directory.CreateSymbolicLink(destLink, target);
        }
        catch { /* best-effort: unsupported / unprivileged — skip rather than follow the link */ }
    }
}
