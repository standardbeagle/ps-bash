using System.Management.Automation;
using System.Management.Automation.Language;
using System.Reflection;

namespace PsBash.Cmdlets;

/// <summary>
/// What an in-process background job (<c>cmd &amp;</c>) runs, compiled before its launch, so that nothing on the job's way
/// to its first statement is compiled (docs/specs/background-jobs.md section 12.5, cause 7).
/// <para>
/// On Windows PowerShell scans every script it compiles with AMSI, under one process-wide lock, and a scan is slow when
/// Defender is busy (40-130 ms per job measured on a loaded host). A job used to run <c>AddScript(text)</c>: PowerShell
/// parses and compiles such text afresh on every invocation, even when the text repeats, so every job scanned its own
/// script on its way to its first statement, and the launcher's next .NET call waited behind that scan too (launches
/// 45-60 ms apart, measured). Now the job runs the job runspace's function <see cref="FunctionName"/> (the head and tail,
/// <see cref="Text"/>: one compile per process, shared by every runspace) with the body as a script block from
/// <see cref="Body"/>: created by text through PowerShell's script-block cache (one compile per distinct body per process,
/// shared by every job that runs it) and compiled before the launch: by the host before the command runs
/// (<see cref="Prepare"/>), else by the launcher before the job's clock starts. The scan is not skipped; it happens
/// earlier, on the shell's time, where bash spends its time parsing.
/// </para>
/// Guards: JobScriptTests (Cmdlets.Tests).
/// </summary>
public static class JobScript
{
    /// <summary>The job runspace's function that runs a job (defined at open, <see cref="BashJobRunspaces.InitialState"/>).</summary>
    public const string FunctionName = "__psbashJob";

    /// <summary>
    /// The job's head and tail around its body (<c>. $__bgBody</c>), dot-sourced at the runspace's global scope like the
    /// script text it replaces. Everything that differs between jobs arrives as an argument, never as text. Nothing before
    /// the body calls a .NET method: such a call is an AMSI report under the same lock (section 12.4).
    /// </summary>
    internal const string Text = """
param($__bgStatus, $__bgTurn, $__bgBody, $__bgLocation)
# What the job inherits (its directory, options, ERR trap, $BASHPID, loop variables, stdin, clock) was set in this
# runspace by the launcher, as values (BashJobRunspaces.NewJob). Its scalars ($env:NAME) are its own copy, taken at the &
# (ScalarOverlay, docs/specs/background-jobs.md section 3).
$__bgOk = $false
try {
    # The parent's directory, when this pooled runspace is elsewhere (BashJobRunspaces.Inherit). A directory that is gone
    # fails here, the job's error.
    if ($null -ne $__bgLocation) { Set-Location -LiteralPath $__bgLocation }
    # The body begins only after the job launched before it from the same runspace has begun (JobTurn); its clock
    # (JobClock: its sleeps count from its launch, not from this late start) begins here.
    Enter-BashJobTurn $__bgTurn $global:__psbashJobClock
    . $__bgBody
    $__bgOk = $true
} catch {
    # A failure that ends the job (set -u, ${x:?}) was already reported on its error stream, where the job's own
    # redirects apply; thrown on, it surfaced from EndInvoke in `wait` and the job's output was lost. Status 1, as bash's.
    $__bgReported = $false
    for ($__bgE = $_.Exception; $__bgE; $__bgE = $__bgE.InnerException) { if ($__bgE.Data['psbash.reported']) { $__bgReported = $true } }
    if (-not $__bgReported) { Write-Error -ErrorRecord $_ -ErrorAction Continue }
    $global:LASTEXITCODE = 1
} finally {
    # A body that ended early (`exit N` is `return` in a job body) leaves its status in LASTEXITCODE; an error with none is 1.
    $x = $global:LASTEXITCODE
    $__bgCode = if ($__bgOk) { if ($null -eq $x) { 0 } else { [int]$x } } elseif ($x) { [int]$x } else { 1 }
    # A pooled runspace must not keep the cursor for the next job.
    $global:__BashStdIn = $null
    $__bgStatus.Code = $__bgCode
    # A signal the job sent itself (`kill -USR1 $BASHPID`; ShellSignalTraps.Terminate) ended it: `wait` reports it.
    $__bgStatus.Signal = $global:__psbashJobSignal
    $__bgStatus.At = [System.Diagnostics.Stopwatch]::GetTimestamp()
    # Last: the runspace goes back for the next job without the globals this one created.
    [PsBash.Cmdlets.BashJobRunspaces]::EndJob($ExecutionContext.SessionState)
}
""";

    /// <summary>
    /// The script block a job runs for the body text <paramref name="text"/> (<c>$Command.ToString()</c>): from
    /// PowerShell's script-block cache, so every job with this body shares one compile, and compiled now if no job or
    /// <see cref="Prepare"/> compiled it before. Unbound: it runs in the job runspace's session state.
    /// </summary>
    public static ScriptBlock Body(string text)
    {
        var block = ScriptBlock.Create(text);
        Compile(block);
        return block;
    }

    /// <summary>
    /// Compile the body of every in-process job the PowerShell script <paramref name="script"/> can start (the
    /// transpiled command, before it runs), so the jobs find them compiled (<see cref="Body"/>). The host calls it as a
    /// command that uses <c>&amp;</c> is about to run: a body seen for the first time is scanned here, before the command
    /// starts, not between two of its launches (where it delayed the second <c>&amp;</c> by the scan, and
    /// <c>{ sleep 0.09; } &amp; { sleep 0.03; } &amp; wait -n</c> saw the first job end first). <c>-Isolated</c> jobs run
    /// in a host process of their own unless <paramref name="includeIsolated"/> (<c>PSBASH_BG_MODE=inproc</c>).
    /// Best effort: returns how many bodies it compiled; a body it misses is compiled at its launch.
    /// </summary>
    public static int Prepare(string script, bool includeIsolated)
    {
        if (string.IsNullOrEmpty(script) || !script.Contains(BackgroundCommand, StringComparison.Ordinal)) return 0;
        int compiled = 0;
        try
        {
            foreach (var text in BodyTexts(script, includeIsolated))
            {
                if (Compile(ScriptBlock.Create(text))) compiled++;
            }
        }
        catch (Exception)
        {
            // A script that does not parse fails when it runs, with its own error.
        }
        return compiled;
    }

    private const string BackgroundCommand = "Invoke-BashBackground";

    /// <summary>
    /// The body text (<c>ScriptBlock.ToString()</c>, the text <see cref="Body"/> gets) of every
    /// <c>Invoke-BashBackground { body }</c> in <paramref name="script"/>, nested ones included.
    /// </summary>
    internal static IEnumerable<string> BodyTexts(string script, bool includeIsolated)
    {
        var ast = System.Management.Automation.Language.Parser.ParseInput(script, out _, out var errors);
        if (errors.Length > 0) yield break;
        foreach (var found in ast.FindAll(static a => a is CommandAst, searchNestedScriptBlocks: true))
        {
            var command = (CommandAst)found;
            if (!string.Equals(command.GetCommandName(), BackgroundCommand, StringComparison.OrdinalIgnoreCase)) continue;
            ScriptBlockExpressionAst? body = null;
            bool isolated = false;
            foreach (var element in command.CommandElements)
            {
                if (element is CommandParameterAst p && p.ParameterName.Equals("Isolated", StringComparison.OrdinalIgnoreCase)) isolated = true;
                else if (element is ScriptBlockExpressionAst sb) body ??= sb;
            }
            if (body is null || (isolated && !includeIsolated)) continue;
            string? text = null;
            try { text = body.ScriptBlock.GetScriptBlock().ToString(); }
            catch (Exception) { }
            if (text is not null) yield return text;
        }
    }

    /// <summary>
    /// Compile <paramref name="block"/> for a dot-sourced run (<c>. $__bgBody</c>, a function called with
    /// <c>useLocalScope: false</c>): PowerShell's internal <c>ScriptBlock.Compile(optimized: false)</c>, which runs its
    /// AMSI scan as any compile does, once per compiled script block. False when it could not (the job then compiles it
    /// as it starts, as before): no such method in this PowerShell, or the compile failed (AMSI blocked the text, which
    /// the job's own compile then reports).
    /// </summary>
    internal static bool Compile(ScriptBlock block)
    {
        if (CompileMethod is null) return false;
        try
        {
            CompileMethod.Invoke(block, [false]);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Compile <see cref="Text"/>, the function every job runs: once per process (a job runspace's open).</summary>
    internal static void CompileFunction() => Compile(ScriptBlock.Create(Text));

    /// <summary>Test seam: PowerShell's compile entry point was found (a PowerShell upgrade that renames it fails the guard).</summary>
    internal static bool CanCompile => CompileMethod is not null;

    /// <summary>
    /// Test seam: <paramref name="block"/> is compiled for a dot-sourced run, so running it scans nothing
    /// (<c>CompiledScriptBlockData._compiledUnoptimized</c>). Null when PowerShell has no such field.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "A test seam over PowerShell's own assembly, which is never trimmed; a missing field returns null.")]
    internal static bool? IsCompiled(ScriptBlock block)
    {
        var data = typeof(ScriptBlock).GetField("_scriptBlockData", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(block);
        var flag = data?.GetType().GetField("_compiledUnoptimized", BindingFlags.Instance | BindingFlags.NonPublic);
        return flag?.GetValue(data) as bool?;
    }

    private static readonly MethodInfo? CompileMethod =
        typeof(ScriptBlock).GetMethod("Compile", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(bool)], null);
}
