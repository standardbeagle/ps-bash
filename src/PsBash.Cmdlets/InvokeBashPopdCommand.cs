using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashPopd</c> function
/// (REFACTOR-2 dir-stack batch). Implements the bash <c>popd</c> builtin:
/// remove the top of the directory stack and chdir to it; <c>+N</c> / <c>-N</c> remove
/// that entry instead (<see cref="DirectoryStack"/>), <c>-n</c> keeps the directory.
///
/// The stack is PowerShell's location stack, shared with <c>pushd</c>, <c>dirs</c> and
/// subshell restore — see <see cref="InvokeBashPushdCommand"/> for the state-ownership
/// rationale. No PowerShell common-parameter prefix collision.
///
/// <para>Like <c>pushd</c>, every location change also writes
/// <see cref="Environment.CurrentDirectory"/> via
/// <see cref="InvokeBashPushdCommand.SyncProcessWorkingDirectory"/> — moving only the
/// PowerShell half of a bash working directory produces silently wrong output in any
/// consumer that resolves relative paths through raw .NET.</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashPopd")]
public sealed class InvokeBashPopdCommand : BashCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "popd", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "popd"))
            {
                WriteObject(line);
            }
            return;
        }

        // bash's popd (pushd.def): -n keeps the directory, `--` ends the scan, a +/- word is a stack index (not a number:
        // "invalid number"), any other word an "invalid argument", both with the usage line, status 2. The last index wins.
        bool noCd = false, fromLeft = true;
        long index = 0;
        string indexWord = "";
        foreach (var a in args)
        {
            if (a == "-n") { noCd = true; continue; }
            if (a == "--") break;
            if (a.Length > 0 && a[0] is '+' or '-')
            {
                if (!DirectoryStack.TryParseIndex(a, out fromLeft, out index))
                {
                    FileSystemHelpers.WriteBuiltinUsageError(this, "popd", $"popd: {a}: invalid number");
                    return;
                }
                indexWord = a;
                continue;
            }
            if (a.Length == 0) break;
            FileSystemHelpers.WriteBuiltinUsageError(this, "popd", $"popd: {a}: invalid argument");
            return;
        }

        // Nothing pushed, or an index outside the stack: bash's "directory stack empty" / "+N: directory stack index out
        // of range", status 1.
        var list = BashShellState.ReadDirectoryStack(SessionState);
        int offset = list.Count - 1;
        int p = DirectoryStack.Position(fromLeft, index, offset);
        if (offset == 0 || p < 0)
        {
            FileSystemHelpers.WriteBuiltinError(this, "popd: " + DirectoryStack.RangeError(offset, indexWord));
            return;
        }

        // A top entry that is gone is bash's cd failure: status 1, the stack as it was (ApplyStack).
        if (!InvokeBashPushdCommand.ApplyStack(this, "popd", DirectoryStack.Pop(list, p, noCd))) return;
        InvokeBashDirsCommand.PrintStack(this);
    }
}
