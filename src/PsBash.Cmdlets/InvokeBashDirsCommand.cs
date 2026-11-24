using System.Management.Automation;
using PsBash.Core;

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
public sealed class InvokeBashDirsCommand : BashCmdlet
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

        // bash's dirs: the words -l -c -v -p (each on its own), `--` ends the scan, a +N / -N word picks one entry (the
        // last one wins); a +/- word that is not a number is "invalid number" and any other word "invalid option", each with
        // the usage line, status 2. An index outside the stack is status 1 (DirectoryStack).
        bool longForm = false, clear = C.IsPresent, verbose = V.IsPresent, perLine = P.IsPresent;
        string? indexWord = null;
        bool fromLeft = false;
        long index = 0;
        foreach (var a in args)
        {
            if (a == "-l") longForm = true;
            else if (a == "-c") clear = true;
            else if (a == "-v") verbose = true;
            else if (a == "-p") perLine = true;
            else if (a == "--") break;
            else if (a.Length > 0 && a[0] is '+' or '-')
            {
                if (!DirectoryStack.TryParseIndex(a, out fromLeft, out index))
                {
                    FileSystemHelpers.WriteBuiltinUsageError(this, "dirs", $"dirs: {a}: invalid number");
                    return;
                }
                indexWord = a;
            }
            else
            {
                FileSystemHelpers.WriteBuiltinUsageError(this, "dirs", $"dirs: {a}: invalid option");
                return;
            }
        }

        if (clear)
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

        if (indexWord is not null)
        {
            int p = DirectoryStack.Position(fromLeft, index, entries.Count - 1);
            if (p < 0)
            {
                // dirs names the index without its sign (bash `sh_erange (word + 1)`), unlike pushd and popd.
                FileSystemHelpers.WriteBuiltinError(this, "dirs: " + DirectoryStack.RangeError(entries.Count - 1, indexWord[1..]));
                return;
            }
            WriteObject(BashRuntime.NewBashObject(verbose ? $"{p,2}  {entries[p]}" : entries[p]));
            return;
        }

        if (verbose)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                WriteObject(BashRuntime.NewBashObject($"{i,2}  {entries[i]}"));
            }
        }
        else if (perLine)
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
        var entries = BashShellState.ReadDirectoryStack(cmdlet.SessionState);
        string? home = longForm ? null : BashRuntime.MapPath(BashVariableStore.Get("HOME") ?? "");
        Present(entries, home, OperatingSystem.IsWindows());
        return entries;
    }

    /// <summary>
    /// Stack entries as printed. On Windows they are written like `pwd` and $PWD (`C:/Users/x`): a stack of
    /// `C:\Users\x` entries did not match them, and `~\dir` mixed separators (a backslash is part of a directory
    /// name elsewhere, so only Windows). A leading <paramref name="home"/> becomes `~`; null = long form (`-l`).
    /// </summary>
    internal static void Present(List<string> entries, string? home, bool windows)
    {
        for (int i = 0; i < entries.Count; i++) entries[i] = BashPath.ToShell(entries[i], windows);
        if (home is not null) home = BashPath.ToShell(home, windows);
        if (home is null) return;
        for (int i = 0; i < entries.Count; i++) entries[i] = TildeAbbreviate(entries[i], home);
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