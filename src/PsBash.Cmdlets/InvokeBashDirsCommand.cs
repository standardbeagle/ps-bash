using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashDirs</c> function
/// (REFACTOR-2 dir-stack batch). Implements the bash <c>dirs</c> builtin:
/// list the current location stack. <c>-c</c> clears the stack, <c>-p</c>
/// emits one entry per line, <c>-v</c> emits numbered entries; default emits
/// a single space-joined line with the current location first then the
/// stack contents reversed (oracle parity).
///
/// Behavioral parity oracle: the original psm1 function, which uses
/// <c>Get-Location -Stack</c> and <c>Pop-Location -Stack</c>. The cmdlet
/// preserves that identity exactly — see <see cref="InvokeBashPushdCommand"/>
/// for the state-ownership rationale.
///
/// <para>Three colliding flags declared as explicit <see cref="SwitchParameter"/>s
/// with single-letter names so the binder routes bare tokens by exact-name
/// match (beats common-parameter prefix-matching, same playbook pattern as
/// <see cref="InvokeBashShoptCommand"/>): <c>-c</c> vs <c>-Confirm</c> → <c>C</c>;
/// <c>-p</c> vs <c>-PipelineVariable</c> / <c>-ProgressAction</c> → <c>P</c>;
/// <c>-v</c> vs <c>-Verbose</c> → <c>V</c>.</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashDirs")]
public sealed class InvokeBashDirsCommand : PSCmdlet
{
    [Parameter]
    public SwitchParameter C { get; set; }

    [Parameter]
    public SwitchParameter P { get; set; }

    [Parameter]
    public SwitchParameter V { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "dirs", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "dirs"))
            {
                WriteObject(line);
            }
            return;
        }

        // `dirs` takes only +N / -N (and `-l`); any other operand is an error (status 1).
        bool longForm = false;
        foreach (var a in args)
        {
            if (a == "-l") { longForm = true; continue; }
            if (a == "--") continue;
            bool numbered = a.Length > 1 && a[0] is '+' or '-' && a.Skip(1).All(char.IsAsciiDigit);
            if (numbered) continue;
            if (a.Length > 1 && a[0] == '-')
            {
                FileSystemHelpers.WriteBashError(this, $"dirs: {a}: invalid option");
                FileSystemHelpers.WriteBashError(this, "dirs: usage: dirs [-clpv] [+N] [-N]");
                FileSystemHelpers.SetLastExitCode(this, 2);
                return;
            }
            FileSystemHelpers.WriteBashError(this, $"dirs: {a}: invalid number");
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        if (C.IsPresent)
        {
            // Drain the location stack without moving the current directory (Pop-Location moves it).
            string? keep = SessionState.Path.CurrentFileSystemLocation?.Path;
            // Pop until the stack is empty (PopLocation throws once it is), then return to where we were.
            int depth = SessionState.Path.LocationStack(null).Count;
            for (int i = 0; i < depth; i++)
            {
                try { SessionState.Path.PopLocation(null); } catch (InvalidOperationException) { break; }
            }
            if (keep != null) SessionState.Path.SetLocation(keep);
            return;
        }

        var entries = StackEntries(this, longForm);

        if (V.IsPresent)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                WriteObject(BashRuntime.NewBashObject($"{i,2}  {entries[i]}"));
            }
        }
        else if (P.IsPresent)
        {
            foreach (var entry in entries)
            {
                WriteObject(BashRuntime.NewBashObject(entry));
            }
        }
        else
        {
            WriteObject(BashRuntime.NewBashObject(string.Join(' ', entries)));
        }
    }

    /// <summary>The directory stack, current directory first (bash's order); a leading $HOME shows as ~ unless <paramref name="longForm"/>.</summary>
    internal static List<string> StackEntries(PSCmdlet cmdlet, bool longForm)
    {
        // Get-Location -Stack is top-first (most recently pushed-from directory first) @T bash's order,
        // after the current directory.
        var entries = new List<string>();
        foreach (var item in cmdlet.InvokeCommand.InvokeScript("(Get-Location).Path"))
        {
            if (item?.BaseObject is string c) { entries.Add(c); break; }
        }
        foreach (var item in cmdlet.InvokeCommand.InvokeScript(@"@(Get-Location -Stack | ForEach-Object { $_.Path })"))
        {
            if (item?.BaseObject is string s) entries.Add(s);
        }

        // bash abbreviates a leading $HOME to ~ unless -l (long form) is given.
        if (!longForm)
        {
            var home = BashRuntime.MapPath(BashVariableStore.Get("HOME") ?? "");
            for (int i = 0; i < entries.Count; i++) entries[i] = TildeAbbreviate(entries[i], home);
        }
        return entries;
    }

    /// <summary>pushd / popd print the new stack on one line after a successful change, as bash does.</summary>
    internal static void PrintStack(PSCmdlet cmdlet)
        => cmdlet.WriteObject(BashRuntime.NewBashObject(string.Join(' ', StackEntries(cmdlet, false))));

    /// <summary>
    /// <c>$HOME[/rest]</c> as <c>~[/rest]</c>, only at a directory boundary (<c>/tmp/oil_tests</c> is not under
    /// <c>HOME=/tmp/oil</c>) and never for an empty or root HOME. Separators in <paramref name="rest"/> are kept as they are.
    /// </summary>
    internal static string TildeAbbreviate(string path, string? home)
    {
        if (string.IsNullOrEmpty(home)) return path;
        var h = home.TrimEnd('/', '\\');
        if (h.Length == 0 || (h.Length == 2 && h[1] == ':')) return path;
        if (!path.StartsWith(h, StringComparison.OrdinalIgnoreCase)) return path;
        if (path.Length == h.Length) return "~";
        return path[h.Length] is '/' or '\\' ? "~" + path[h.Length..] : path;
    }
}