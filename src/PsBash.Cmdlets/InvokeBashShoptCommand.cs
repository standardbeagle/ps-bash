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
/// Unknown option name routes through <see cref="FileSystemHelpers.WriteBashError"/> matching
/// the oracle byte-for-byte. <c>--help</c> delegates to psm1
/// <c>Show-BashHelp</c> via parameter-bound <c>InvokeCommand.InvokeScript</c>
/// (AOT-safe). The <c>shopt</c> alias stays in psm1 and resolves to this
/// cmdlet automatically.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashShopt")]
[OutputType(typeof(string))]
public sealed class InvokeBashShoptCommand : PSCmdlet
{
    // bash 5.2's shopt table, in the order `shopt` lists it, with the defaults of a non-interactive shell.
    private static readonly (string Name, bool On)[] ShoptTable =
    {
        ("autocd", false), ("assoc_expand_once", false), ("cdable_vars", false), ("cdspell", false), ("checkhash", false),
        ("checkjobs", false), ("checkwinsize", true), ("cmdhist", true), ("compat31", false), ("compat32", false),
        ("compat40", false), ("compat41", false), ("compat42", false), ("compat43", false), ("compat44", false),
        ("complete_fullquote", true), ("direxpand", false), ("dirspell", false), ("dotglob", false), ("execfail", false),
        ("expand_aliases", false), ("extdebug", false), ("extglob", false), ("extquote", true), ("failglob", false),
        ("force_fignore", true), ("globasciiranges", true), ("globskipdots", true), ("globstar", false), ("gnu_errfmt", false),
        ("histappend", false), ("histreedit", false), ("histverify", false), ("hostcomplete", true), ("huponexit", false),
        ("inherit_errexit", false), ("interactive_comments", true), ("lastpipe", false), ("lithist", false),
        ("localvar_inherit", false), ("localvar_unset", false), ("login_shell", false), ("mailwarn", false),
        ("no_empty_cmd_completion", false), ("nocaseglob", false), ("nocasematch", false), ("noexpand_translation", false),
        ("nullglob", false), ("patsub_replacement", true), ("progcomp", true), ("progcomp_alias", false), ("promptvars", true),
        ("restricted_shell", false), ("shift_verbose", false), ("sourcepath", true), ("varredir_close", false), ("xpg_echo", false),
    };

    // `set -o` names in `shopt -o` order. The first six are real runtime flags kept in globals (and set by `set`);
    // the others are remembered here so `set -o vi` / `shopt -po` agree with each other.
    private static readonly (string Name, bool On)[] SetoptTable =
    {
        ("allexport", false), ("braceexpand", true), ("emacs", false), ("errexit", false), ("errtrace", false),
        ("functrace", false), ("hashall", true), ("histexpand", false), ("history", false), ("ignoreeof", false),
        ("interactive-comments", true), ("keyword", false), ("monitor", false), ("noclobber", false), ("noexec", false),
        ("noglob", false), ("nolog", false), ("notify", false), ("nounset", false), ("onecmd", false), ("physical", false),
        ("pipefail", false), ("posix", false), ("privileged", false), ("verbose", false), ("vi", false), ("xtrace", false),
    };

    private static readonly Dictionary<string, string> SetoptGlobals = new(StringComparer.Ordinal)
    {
        ["errexit"] = "__BashErrexit", ["noclobber"] = "__BashNoclobber", ["noglob"] = "__BashNoglob",
        ["nounset"] = "__BashNounset", ["pipefail"] = "__BashPipefail", ["xtrace"] = "__BashXtrace",
    };

    private static Dictionary<string, bool> NewDefaults()
    {
        var d = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (name, on) in ShoptTable) d[name] = on;
        foreach (var (name, on) in SetoptTable) if (!SetoptGlobals.ContainsKey(name)) d["set:" + name] = on;
        return d;
    }

    // Option state belongs to the SHELL SESSION, i.e. the runspace: the host process outlives a `-c`
    // command (a pooled daemon serves many), and a `shopt -s nullglob` in one command must not change
    // pathname expansion in the next. The glob cmdlets read the same table through IsEnabled.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        System.Management.Automation.Runspaces.Runspace, Dictionary<string, bool>> PerRunspace = new();

    private static Dictionary<string, bool> Options =>
        System.Management.Automation.Runspaces.Runspace.DefaultRunspace is { } rs
            ? PerRunspace.GetValue(rs, _ => NewDefaults())
            : Fallback;

    private static readonly Dictionary<string, bool> Fallback = NewDefaults();

    /// <summary>Current value of a shell option in the calling runspace (false for an unknown name).</summary>
    internal static bool IsEnabled(string name) => Options.TryGetValue(name, out var on) && on;

    /// <summary>A copy of the calling runspace's option table (<see cref="BashShellState"/>: subshell scope).</summary>
    internal static Dictionary<string, bool> SnapshotOptions() => new(Options, StringComparer.Ordinal);

    /// <summary>Put a <see cref="SnapshotOptions"/> copy back, in place (the per-runspace table is shared by reference).</summary>
    internal static void RestoreOptions(Dictionary<string, bool> snapshot)
    {
        var options = Options;
        options.Clear();
        foreach (var (k, v) in snapshot) options[k] = v;
    }

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

        bool setMode = false, unsetMode = false, printMode = P.IsPresent, quiet = false, setoptMode = false;
        var operands = new List<string>();
        bool endOfFlags = false;
        foreach (var arg in args)
        {
            if (endOfFlags || arg.Length < 2 || arg[0] != '-') { operands.Add(arg); continue; }
            if (arg == "--") { endOfFlags = true; continue; }
            foreach (char f in arg.AsSpan(1))
            {
                switch (f)
                {
                    case 's': setMode = true; break;
                    case 'u': unsetMode = true; break;
                    case 'p': printMode = true; break;
                    case 'q': quiet = true; break;
                    case 'o': setoptMode = true; break;
                    default:
                        FileSystemHelpers.WriteBashError(this, $"shopt: -{f}: invalid option");
                        FileSystemHelpers.WriteBashError(this, "shopt: usage: shopt [-pqsu] [-o] [optname ...]");
                        FileSystemHelpers.SetLastExitCode(this, 2);
                        return;
                }
            }
        }
        if (setMode && unsetMode)
        {
            FileSystemHelpers.WriteBashError(this, "shopt: cannot set and unset shell options simultaneously");
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        var table = setoptMode ? SetoptTable : ShoptTable;
        int status = 0;
        if (operands.Count == 0)
        {
            foreach (var (name, _) in table)
            {
                bool on = Get(name, setoptMode);
                if ((setMode && !on) || (unsetMode && on)) continue;
                if (!quiet) WriteObject(BashRuntime.NewBashObject(Line(name, on, printMode, setoptMode)));
            }
            return;
        }

        foreach (var opt in operands)
        {
            if (Array.FindIndex(table, t => t.Name == opt) < 0)
            {
                FileSystemHelpers.WriteBashError(this, $"shopt: {opt}: invalid {(setoptMode ? "option" : "shell option")} name");
                status = 1;
                continue;
            }
            if (setMode || unsetMode)
            {
                Set(opt, setMode, setoptMode);
                continue;
            }
            bool on = Get(opt, setoptMode);
            if (!on) status = 1;
            if (!quiet) WriteObject(BashRuntime.NewBashObject(Line(opt, on, printMode, setoptMode)));
        }
        FileSystemHelpers.SetLastExitCode(this, status);
    }

    private static string Line(string name, bool on, bool print, bool setopt) =>
        print ? (setopt ? $"set {(on ? "-o" : "+o")} {name}" : $"shopt {(on ? "-s" : "-u")} {name}")
              : $"{name,-15}\t{(on ? "on" : "off")}";

    private bool Get(string name, bool setopt)
    {
        if (!setopt) return Options.TryGetValue(name, out var v) && v;
        if (SetoptGlobals.TryGetValue(name, out var global)) return SessionState.PSVariable.GetValue(global) is true;
        return Options.TryGetValue("set:" + name, out var w) ? w : false;
    }

    private void Set(string name, bool on, bool setopt)
    {
        if (!setopt) { Options[name] = on; return; }
        if (SetoptGlobals.TryGetValue(name, out var global))
        {
            InvokeCommand.InvokeScript("param($n, $v) Set-Variable -Name $n -Value $v -Scope Global -Force", global, on);
            return;
        }
        if (on && name == "vi") Options["set:emacs"] = false;
        if (on && name == "emacs") Options["set:vi"] = false;
        Options["set:" + name] = on;
    }

    /// <summary>
    /// Test hook: reset the options table to oracle defaults. Used by parity
    /// tests so order-dependent assertions don't leak state. Not part of the
    /// public bash shopt surface.
    /// </summary>
    public static void ResetForTests()
    {
        PerRunspace.Clear();
        Fallback.Clear();
        foreach (var kv in NewDefaults()) Fallback[kv.Key] = kv.Value;
    }
}
