using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashPushd</c> function
/// (REFACTOR-2 dir-stack batch). Implements the bash <c>pushd</c> builtin:
/// push current directory onto the location stack and chdir to a new path,
/// or with <c>+N</c> rotate the Nth stack entry to the top.
///
/// Behavioral parity oracle: the original psm1 function. The oracle uses
/// PowerShell's built-in location stack (the same one <c>Push-Location</c> /
/// <c>Pop-Location -Stack</c> / <c>Get-Location -Stack</c> manage), NOT a
/// separate <c>$global:BashDirStack</c> array. The cmdlet preserves that
/// identity exactly by delegating to those same cmdlets via parameter-bound
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>
/// (AOT-safe; no <see cref="ScriptBlock"/> construction). Sharing the
/// runspace location stack means <c>cd</c>, <c>Set-Location</c>, and bash
/// <c>pushd</c>/<c>popd</c>/<c>dirs</c> all see the same stack — exactly
/// what the oracle did.
///
/// <para>Flag surface: a single <c>+N</c> positional, or a single directory
/// path operand, or no operands (defaults to "."). No PowerShell common
/// parameter prefix collision — all tokens flow through <c>Arguments</c>.</para>
///
/// <para><c>--help</c> delegates to psm1 <c>Show-BashHelp</c> via parameter-bound
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>.</para>
///
/// <para><b>Every location change also writes <see cref="Environment.CurrentDirectory"/></b>
/// — see <see cref="SyncProcessWorkingDirectory"/>. Delegating to
/// <c>Push-Location</c> alone moved half of a bash working directory, which is a
/// silent-wrong-output bug, not an inconsistency: see that method's remarks.</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashPushd")]
public sealed class InvokeBashPushdCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "pushd", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "pushd"))
            {
                WriteObject(line);
            }
            return;
        }

        // +N rotation: oracle's `^\+(\d+)$` match.
        if (args.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(args[0], @"^\+(\d+)$"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(args[0], @"^\+(\d+)$");
            var n = BashRuntime.ParseCountClamped(m.Groups[1].Value);

            // Replicate the oracle slice byte-for-byte: get stack, pick Nth,
            // pop (N+1) entries, push the target. The delegation script reads
            // its arg via $args[0] so user input cannot reach the script body.
            InvokeCommand.InvokeScript(
                @"param($n)
                $stack = @(Get-Location -Stack)
                if ($n -ge 0 -and $n -lt $stack.Count) {
                    $target = $stack[$n]
                    for ($i = 0; $i -le $n; $i++) { Pop-Location -Stack -ErrorAction SilentlyContinue }
                    Push-Location -Path $target.Path
                }",
                n);
            SyncProcessWorkingDirectory(this);
            return;
        }

        // Default: push current location and chdir to the given path (or '.').
        // The path token is bound as a positional $args[0] so a path containing
        // ; / $() / scriptblock chars stays a literal path (Directive 12).
        var path = args.Length > 0 ? args[0] : ".";
        try
        {
            InvokeCommand.InvokeScript(
                "param($p) Push-Location -Path $p",
                path);
            SyncProcessWorkingDirectory(this);
        }
        catch (System.Management.Automation.RuntimeException ex)
        {
            // Surface a bash-style error rather than a raw PS exception.
            FileSystemHelpers.WriteBashError(this, $"pushd: {path}: {ex.Message}");
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
    }

    /// <summary>
    /// Copy the PowerShell current location into <see cref="Environment.CurrentDirectory"/>
    /// after any successful location change. Shared by <see cref="InvokeBashPopdCommand"/>.
    ///
    /// <para><b>Why this is required, not tidy.</b> A bash command's working directory is
    /// BOTH the PowerShell location (what <c>SessionState.Path</c>-based cmdlets resolve
    /// against) and <c>[System.Environment]::CurrentDirectory</c> (what every raw .NET
    /// path API resolves against, including <see cref="Path.GetFullPath(string)"/> in the
    /// fused streaming lane's <c>CatFileStage</c>). The host keeps the two equal by
    /// construction: <c>SdkRunspace</c> seeds the runspace location FROM
    /// <c>CurrentDirectory</c> at creation, and the emitter's <c>cd</c> writes BOTH on
    /// every change (<c>docs/specs/emitter-strategy.md</c> §4 <c>EmitCd</c>).
    /// <c>pushd</c>/<c>popd</c> wrote only the first, so a relative operand read through
    /// the .NET side resolved against the PRE-<c>pushd</c> directory — streaming a
    /// different file at exit code 0, with no error anywhere. Restoring the invariant
    /// here fixes every present and future reader of <c>CurrentDirectory</c>, rather than
    /// making one consumer defensive against a working directory that is half-moved.</para>
    ///
    /// <para><c>dirs</c> is deliberately NOT included: none of its paths move the
    /// location. <c>-c</c> drains the stack with <c>Pop-Location -Stack</c>, which never
    /// pops anything — <c>-Stack</c> prefix-matches the <c>-StackName</c> PARAMETER and
    /// binds "missing an argument". That failure is a <c>ParameterBindingException</c>
    /// thrown at bind time, which ESCAPES <c>-ErrorAction SilentlyContinue</c> (the
    /// preference applies to the cmdlet's own error records, not to binder failures) — so
    /// it surfaces, it is not swallowed. Either way the location does not move and
    /// <c>dirs</c> needs no cwd sync; the earlier "silent no-op, swallowed" wording named
    /// the wrong mechanism. Probed in
    /// <c>InvokeBashPushdCommandTests</c>. An oracle-inherited quirk, not something to fix
    /// under a cwd-sync change.</para>
    ///
    /// <para>Best-effort by design. <c>CurrentFileSystemLocation</c> throws when the
    /// session is on a non-filesystem provider (<c>HKLM:</c>), and there is no meaningful
    /// process working directory for such a location; the assignment itself can race a
    /// directory deleted underneath us. Neither is worth failing a <c>pushd</c> that
    /// already succeeded, and neither can be reported on the bash side, so both leave the
    /// previous value in place.</para>
    /// </summary>
    internal static void SyncProcessWorkingDirectory(PSCmdlet cmdlet)
    {
        try
        {
            var path = cmdlet.SessionState.Path.CurrentFileSystemLocation?.Path;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                Environment.CurrentDirectory = path;
            }
        }
        catch
        {
            // Non-filesystem provider or a vanished directory — keep the old value.
        }
    }
}
