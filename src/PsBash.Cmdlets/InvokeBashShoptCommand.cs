using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashShopt</c> function
/// (REFACTOR-2 follow-on). Implements the bash <c>shopt</c> builtin: list,
/// set, unset, or query shell options.
///
/// Behavioral parity oracle: the original psm1 function. The option table
/// (<c>extglob</c>, <c>globstar</c>, <c>dotglob</c>, <c>nullglob</c>,
/// <c>nocaseglob</c>, <c>expand_aliases</c>, <c>cmdhist</c>,
/// <c>histappend</c>, <c>checkwinsize</c>, <c>progcomp</c>,
/// <c>login_shell</c>, <c>interactive_comments</c>, <c>sourcepath</c>,
/// <c>hostcomplete</c>) is owned here as a static <see cref="Dictionary{TKey,TValue}"/>
/// mirroring the psm1 oracle's <c>$script:BashShoptOptions</c> hashtable.
/// The state was exclusive to <c>Invoke-BashShopt</c> in the psm1, so no
/// sharing is broken by moving it into the cmdlet.
///
/// Flag surface: <c>-s</c> (set), <c>-u</c> (unset), <c>-p</c> (print),
/// <c>-q</c> (quiet). One colliding flag: <c>-p</c> prefix-collides with
/// <c>-PipelineVariable</c> / <c>-ProgressAction</c> per the migration
/// playbook's collision table — declared as a literal <c>SwitchParameter P</c>
/// so the binder routes the bare token by exact-name match (an
/// <c>[Alias("p")]</c> on a longer name does not beat common-parameter
/// prefix-matching). <c>-s</c> / <c>-u</c> / <c>-q</c> have no PowerShell
/// common-parameter prefix overlap and stay in <c>Arguments</c>.
///
/// Unknown option name routes through psm1 <c>Write-BashError</c> matching
/// the oracle byte-for-byte. <c>--help</c> delegates to psm1
/// <c>Show-BashHelp</c> via parameter-bound <c>InvokeCommand.InvokeScript</c>
/// (AOT-safe). The <c>shopt</c> alias stays in psm1 and resolves to this
/// cmdlet automatically.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashShopt")]
[OutputType(typeof(string))]
public sealed class InvokeBashShoptCommand : PSCmdlet
{
    // Mirror of the psm1 oracle's $script:BashShoptOptions defaults. The
    // state was scoped to Invoke-BashShopt only; no other psm1 function
    // reads or writes BashShoptOptions, so consolidating ownership here
    // preserves single-source semantics.
    private static readonly Dictionary<string, bool> Options = new(StringComparer.Ordinal)
    {
        ["extglob"] = false,
        ["globstar"] = true,
        ["dotglob"] = false,
        ["nullglob"] = false,
        ["nocaseglob"] = false,
        ["expand_aliases"] = true,
        ["cmdhist"] = true,
        ["histappend"] = true,
        ["checkwinsize"] = true,
        ["progcomp"] = true,
        ["login_shell"] = false,
        ["interactive_comments"] = true,
        ["sourcepath"] = true,
        ["hostcomplete"] = true,
    };

    /// <summary>
    /// Declared explicitly because the bare token <c>-p</c> prefix-matches
    /// <c>-PipelineVariable</c> / <c>-ProgressAction</c> under
    /// <see cref="PSCmdlet"/> parameter binding. An exact parameter name
    /// (case-insensitive) takes precedence over common-parameter
    /// prefix-matching, so naming the parameter <c>P</c> is what makes the
    /// bare <c>-p</c> token route here.
    /// </summary>
    [Parameter]
    public SwitchParameter P { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "shopt", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "shopt"))
            {
                WriteObject(line);
            }
            return;
        }

        bool setMode = false;
        bool unsetMode = false;
        bool printMode = P.IsPresent;
        // queryMode is accepted for arg-compat but only affects exit-code in
        // bash; the psm1 oracle never wired it to anything observable, so
        // we keep parity (silently accept).
        bool queryMode = false;

        var operands = new List<string>();
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "-s": setMode = true; break;
                case "-u": unsetMode = true; break;
                case "-p": printMode = true; break;
                case "-q": queryMode = true; break;
                default: operands.Add(arg); break;
            }
        }
        _ = queryMode; // suppress unused-warning; parity placeholder

        if (printMode && operands.Count == 0)
        {
            // List all options as "shopt -s NAME" lines, sorted by name —
            // matches the oracle's `Sort-Object Key` pass.
            var keys = new List<string>(Options.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                // The oracle emits "shopt -s NAME" regardless of the
                // stored on/off value (a quirk preserved from psm1).
                WriteObject(BashRuntime.NewBashObject($"shopt -s {key}"));
            }
            return;
        }

        foreach (var opt in operands)
        {
            if (setMode)
            {
                Options[opt] = true;
            }
            else if (unsetMode)
            {
                Options[opt] = false;
            }
            else
            {
                if (Options.TryGetValue(opt, out var val))
                {
                    var state = val ? "on" : "off";
                    WriteObject(BashRuntime.NewBashObject($"{opt} {state}"));
                }
                else
                {
                    FileSystemHelpers.WriteBashError(
                        this, $"bash: shopt: {opt}: invalid shell option name");
                }
            }
        }
    }

    /// <summary>
    /// Test hook: reset the options table to oracle defaults. Used by parity
    /// tests so order-dependent assertions don't leak state. Not part of the
    /// public bash shopt surface.
    /// </summary>
    public static void ResetForTests()
    {
        Options.Clear();
        Options["extglob"] = false;
        Options["globstar"] = true;
        Options["dotglob"] = false;
        Options["nullglob"] = false;
        Options["nocaseglob"] = false;
        Options["expand_aliases"] = true;
        Options["cmdhist"] = true;
        Options["histappend"] = true;
        Options["checkwinsize"] = true;
        Options["progcomp"] = true;
        Options["login_shell"] = false;
        Options["interactive_comments"] = true;
        Options["sourcepath"] = true;
        Options["hostcomplete"] = true;
    }
}
