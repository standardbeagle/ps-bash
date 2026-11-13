using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashPopd</c> function
/// (REFACTOR-2 dir-stack batch). Implements the bash <c>popd</c> builtin:
/// pop the top of the location stack and chdir to it. With <c>+N</c>, pop
/// the Nth entry from the stack.
///
/// Behavioral parity oracle: the original psm1 function, which delegates
/// to PowerShell's built-in location stack via <c>Pop-Location</c> /
/// <c>Pop-Location -Stack</c>. The cmdlet preserves that identity exactly —
/// see <see cref="InvokeBashPushdCommand"/> for the state-ownership
/// rationale. No PowerShell common-parameter prefix collision.
///
/// <para>Like <c>pushd</c>, every location change also writes
/// <see cref="Environment.CurrentDirectory"/> via
/// <see cref="InvokeBashPushdCommand.SyncProcessWorkingDirectory"/> — moving only the
/// PowerShell half of a bash working directory produces silently wrong output in any
/// consumer that resolves relative paths through raw .NET.</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashPopd")]
public sealed class InvokeBashPopdCommand : PSCmdlet
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

        // Option / operand validation (bash): `--` ends options, `-n` is accepted, `+N` / `-N` index the stack,
        // anything else is an invalid argument (status 2).
        var operands = new List<string>();
        bool optionsDone = false;
        foreach (var a in args)
        {
            if (!optionsDone && a == "--") { optionsDone = true; continue; }
            bool numbered = a.Length > 1 && a[0] is '+' or '-' && a.Skip(1).All(char.IsAsciiDigit);
            if (!optionsDone && !numbered && a.Length > 1 && a[0] == '-')
            {
                if (a == "-n") continue;
                FileSystemHelpers.WriteBashError(this, $"popd: {a}: invalid option");
                FileSystemHelpers.WriteBashError(this, "popd: usage: popd [-n] [+N | -N]");
                FileSystemHelpers.SetLastExitCode(this, 2);
                return;
            }
            if (!numbered)
            {
                FileSystemHelpers.WriteBashError(this, $"popd: {a}: invalid argument");
                FileSystemHelpers.SetLastExitCode(this, 2);
                return;
            }
            operands.Add(a);
        }
        args = operands.ToArray();

        var stackNow = InvokeCommand.InvokeScript("@(Get-Location -Stack | ForEach-Object { $_.Path } | Where-Object { $_ }).Count");
        if (stackNow.Count > 0 && stackNow[0]?.BaseObject is int depth && depth == 0)
        {
            FileSystemHelpers.WriteBashError(this, "popd: directory stack empty");
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        // +N: oracle pops (N+1) entries from the stack via Pop-Location -Stack.
        if (args.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(args[0], @"^\+(\d+)$"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(args[0], @"^\+(\d+)$");
            var n = BashRuntime.ParseCountClamped(m.Groups[1].Value);

            var before = InvokeBashPushdCommand.LogicalLocation(this);
            InvokeCommand.InvokeScript(
                @"param($n)
                for ($i = 0; $i -le $n; $i++) { Pop-Location -Stack -ErrorAction SilentlyContinue }",
                n);
            SyncProcessWorkingDirectory(before);
            InvokeBashDirsCommand.PrintStack(this);
            return;
        }

        // Default: pop the top of the stack and chdir there.
        try
        {
            var before = InvokeBashPushdCommand.LogicalLocation(this);
            InvokeCommand.InvokeScript("Pop-Location");
            SyncProcessWorkingDirectory(before);
            InvokeBashDirsCommand.PrintStack(this);
        }
        catch (System.Management.Automation.RuntimeException ex)
        {
            FileSystemHelpers.WriteBashError(this, $"popd: {ex.Message}");
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
    }

    private void SyncProcessWorkingDirectory(string? previousLocation)
        => InvokeBashPushdCommand.SyncProcessWorkingDirectory(this, previousLocation);
}
