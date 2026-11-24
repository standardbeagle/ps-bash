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
/// <para>Flag surface: <c>-n</c>, then a <c>+N</c> / <c>-N</c> rotation or one directory operand; no operand swaps
/// the top two entries. The stack arithmetic is <see cref="DirectoryStack"/>; a rotation or swap rebuilds the
/// location stack (<see cref="BashShellState.SetDirectoryStack"/>). No PowerShell common parameter prefix
/// collision — all tokens flow through <c>Arguments</c>.</para>
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
public sealed class InvokeBashPushdCommand : BashCmdlet
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

        // bash's pushd (pushd.def): -n keeps the directory, `--` ends the options, a lone `-` is an operand, and any other
        // +/- word is a stack index (not a number: "invalid number" with the usage line, status 2; outside the stack:
        // status 1, DirectoryStack). The first operand ends the scan.
        bool noCd = false;
        string? rotateWord = null;
        int rotateTo = 0, i = 0;
        for (; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "-n") { noCd = true; continue; }
            if (a == "--") { i++; break; }
            if (a == "-" || !(a.Length > 0 && a[0] is '+' or '-')) break;
            if (!DirectoryStack.TryParseIndex(a, out bool fromLeft, out long n))
            {
                FileSystemHelpers.WriteBuiltinUsageError(this, "pushd", $"pushd: {a}: invalid number");
                return;
            }
            int offset = BashShellState.ReadDirectoryStack(SessionState).Count - 1;
            rotateTo = DirectoryStack.Position(fromLeft, n, offset);
            if (rotateTo < 0)
            {
                FileSystemHelpers.WriteBuiltinError(this, "pushd: " + DirectoryStack.RangeError(offset, a));
                return;
            }
            rotateWord = a;
        }

        if (args.Length == 0)
        {
            // No operand: the top two swap (bash: "no other directory" when nothing is pushed).
            var list = BashShellState.ReadDirectoryStack(SessionState);
            if (list.Count < 2)
            {
                FileSystemHelpers.WriteBuiltinError(this, "pushd: no other directory");
                return;
            }
            ChangeStack(DirectoryStack.Swap(list), print: true);
            return;
        }

        if (rotateWord is not null)
        {
            // +N / -N: that entry comes first and becomes the directory; with -n the directory stays (and the entry that
            // would have replaced it drops out, as in bash). +0 is the directory itself.
            if (rotateTo == 0)
            {
                if (!noCd) InvokeBashDirsCommand.PrintStack(this);
                return;
            }
            var list = BashShellState.ReadDirectoryStack(SessionState);
            ChangeStack(noCd ? DirectoryStack.RotateInPlace(list, rotateTo) : DirectoryStack.Rotate(list, rotateTo), print: !noCd);
            return;
        }

        // `pushd -n`, `pushd --`: no operand left, nothing to do.
        if (i >= args.Length) return;
        if (args.Length - i > 1)
        {
            FileSystemHelpers.WriteBuiltinError(this, "pushd: too many arguments");
            return;
        }

        // Push the current location and chdir to the operand. The path is bound as a positional $args[0] so a path
        // containing ; / $() / scriptblock chars stays a literal path (Directive 12).
        var path = args[i];
        // A bare `/tmp` (or `/tmp/x` built at run time) is the temp dir, as for `cd`; the emitter only rewrites literal `/tmp/…` words.
        string target = path.StartsWith('/') ? BashRuntime.MapPath(path) : path;
        string full = System.IO.Path.GetFullPath(target, Environment.CurrentDirectory);
        if (!System.IO.Directory.Exists(full))
        {
            FileSystemHelpers.WriteBuiltinError(this, $"pushd: {path}: " + (System.IO.File.Exists(full) ? "Not a directory" : "No such file or directory"));
            return;
        }
        if (noCd)
        {
            // -n DIR: DIR goes on top of the stack, the directory stays. bash keeps the word as typed; PowerShell's
            // location stack holds full paths, so a relative DIR is shown resolved.
            ChangeStack(DirectoryStack.InsertBelowCurrent(BashShellState.ReadDirectoryStack(SessionState), full), print: true);
            return;
        }
        try
        {
            var before = LogicalLocation(this);
            InvokeCommand.InvokeScript(
                "param($p) Push-Location -Path $p",
                target);
            SyncProcessWorkingDirectory(this, before);
            InvokeBashDirsCommand.PrintStack(this);
        }
        catch (System.Management.Automation.RuntimeException ex)
        {
            // Surface a bash-style error rather than a raw PS exception.
            FileSystemHelpers.WriteBuiltinError(this, $"pushd: {path}: {ex.Message}");
        }
    }

    private void ChangeStack(List<string> list, bool print)
    {
        if (!ApplyStack(this, "pushd", list)) return;
        if (print) InvokeBashDirsCommand.PrintStack(this);
    }

    /// <summary>
    /// Makes the directory stack <paramref name="list"/> (<see cref="DirectoryStack"/> order) and moves the whole bash
    /// working directory to its first entry (<see cref="SyncProcessWorkingDirectory"/>). A first entry that no longer
    /// exists is bash's cd failure: status 1, the stack as it was. Shared by <see cref="InvokeBashPopdCommand"/>.
    /// </summary>
    internal static bool ApplyStack(PSCmdlet cmdlet, string builtin, List<string> list)
    {
        bool exists;
        try { exists = System.IO.Directory.Exists(cmdlet.SessionState.Path.GetUnresolvedProviderPathFromPSPath(list[0])); }
        catch (Exception) { exists = false; }
        if (!exists)
        {
            FileSystemHelpers.WriteBuiltinError(cmdlet, $"{builtin}: {PsBash.Core.BashPath.ToShell(list[0])}: No such file or directory");
            return false;
        }
        var before = LogicalLocation(cmdlet);
        try
        {
            BashShellState.SetDirectoryStack(cmdlet.SessionState, list);
        }
        catch (Exception ex) when (ex is RuntimeException or IOException or UnauthorizedAccessException)
        {
            FileSystemHelpers.WriteBuiltinError(cmdlet, $"{builtin}: {PsBash.Core.BashPath.ToShell(list[0])}: {ex.Message}");
            return false;
        }
        SyncProcessWorkingDirectory(cmdlet, before);
        return true;
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
    /// <param name="cmdlet">The cmdlet that moved the location.</param>
    /// <param name="previousLocation">The location before the move, from <see cref="LogicalLocation"/>: <c>$OLDPWD</c>
    /// is the directory as <c>pwd</c> showed it. The process working directory is physical, so on macOS it read
    /// <c>/var/folders/...</c> back as <c>/private/var/folders/...</c> (a resolved symlink) where bash keeps the logical path.</param>
    internal static void SyncProcessWorkingDirectory(PSCmdlet cmdlet, string? previousLocation)
    {
        try
        {
            var path = cmdlet.SessionState.Path.CurrentFileSystemLocation?.Path;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                var previous = string.IsNullOrEmpty(previousLocation) ? Environment.CurrentDirectory : previousLocation;
                Environment.CurrentDirectory = path;
                // The same three writes the emitter's `cd` makes: `pwd` reads __PsBashCwd, `$PWD`/`$OLDPWD` read the env store
                // and hold the script-visible shell form (C:/x, docs/specs/paths.md).
                cmdlet.SessionState.PSVariable.Set("global:__PsBashCwd", path);
                if (!string.Equals(previous, path, StringComparison.Ordinal))
                {
                    BashVariableStore.Set("OLDPWD", PsBash.Core.BashPath.ToShell(previous));
                }
                BashVariableStore.Set("PWD", PsBash.Core.BashPath.ToShell(path));
            }
        }
        catch
        {
            // Non-filesystem provider or a vanished directory — keep the old value.
        }
    }

    /// <summary>The session's current filesystem location as PowerShell holds it (logical, symlinks unresolved), or null on a non-filesystem provider.</summary>
    internal static string? LogicalLocation(PSCmdlet cmdlet)
    {
        try { return cmdlet.SessionState.Path.CurrentFileSystemLocation?.Path; }
        catch { return null; }
    }
}
