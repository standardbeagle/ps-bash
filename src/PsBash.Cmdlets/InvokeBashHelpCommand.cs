using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The <c>help</c> builtin: <c>help NAME</c> prints the builtin's synopsis line and its one-line description (bash's
/// first two lines; bash goes on with the long text, which ps-bash does not carry), <c>help -s NAME</c> the synopsis,
/// <c>help -d NAME</c> the description, <c>help</c> alone every synopsis. NAME may be a pattern (<c>help c*</c>). A name
/// with no topic is bash's error and status 1.
/// <para>
/// Without it <c>help</c> reached PowerShell's own <c>help</c> function (Get-Help through a pager): <c>help cd | head -1</c>
/// printed "The handle is invalid." and the error ended the script that ran it. A bash builtin name must never fall
/// through to a PowerShell command (guard <c>BuiltinNamesNeverReachPowerShellTests</c>).
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashHelp")]
public sealed class InvokeBashHelpCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Decoy: a bare <c>-d</c> in a DIRECT PowerShell call would bind <c>-Debug</c>. The emitter single-quotes every
    /// dash word of <c>help</c> (PsEmitter.OrderedArgCommands), so a script never binds it.</summary>
    [Parameter]
    [Alias("d")]
    public SwitchParameter DescriptionOnly { get; set; }

    /// <summary>Synopsis and one-line description of each bash 5.2 builtin (<c>help -s</c> / <c>help -d</c>).</summary>
    internal static readonly IReadOnlyDictionary<string, (string Synopsis, string Description)> Topics =
        new SortedDictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["."] = (". filename [arguments]", "Execute commands from a file in the current shell."),
        [":"] = (":", "Null command."),
        ["["] = ("[ arg... ]", "Evaluate conditional expression."),
        ["alias"] = ("alias [-p] [name[=value] ... ]", "Define or display aliases."),
        ["bg"] = ("bg [job_spec ...]", "Move jobs to the background."),
        ["bind"] = ("bind [-lpsvPSVX] [-m keymap] [-f filename] [-q name] [-u name] [-r keyseq] [-x keyseq:shell-command] [keyseq:readline-function or readline-command]", "Set Readline key bindings and variables."),
        ["break"] = ("break [n]", "Exit for, while, or until loops."),
        ["builtin"] = ("builtin [shell-builtin [arg ...]]", "Execute shell builtins."),
        ["caller"] = ("caller [expr]", "Return the context of the current subroutine call."),
        ["cd"] = ("cd [-L|[-P [-e]] [-@]] [dir]", "Change the shell working directory."),
        ["command"] = ("command [-pVv] command [arg ...]", "Execute a simple command or display information about commands."),
        ["compgen"] = ("compgen [-abcdefgjksuv] [-o option] [-A action] [-G globpat] [-W wordlist] [-F function] [-C command] [-X filterpat] [-P prefix] [-S suffix] [word]", "Display possible completions depending on the options."),
        ["complete"] = ("complete [-abcdefgjksuv] [-pr] [-DEI] [-o option] [-A action] [-G globpat] [-W wordlist] [-F function] [-C command] [-X filterpat] [-P prefix] [-S suffix] [name ...]", "Specify how arguments are to be completed by Readline."),
        ["compopt"] = ("compopt [-o|+o option] [-DEI] [name ...]", "Modify or display completion options."),
        ["continue"] = ("continue [n]", "Resume for, while, or until loops."),
        ["declare"] = ("declare [-aAfFgiIlnrtux] [name[=value] ...] or declare -p [-aAfFilnrtux] [name ...]", "Set variable values and attributes."),
        ["dirs"] = ("dirs [-clpv] [+N] [-N]", "Display directory stack."),
        ["disown"] = ("disown [-h] [-ar] [jobspec ... | pid ...]", "Remove jobs from current shell."),
        ["echo"] = ("echo [-neE] [arg ...]", "Write arguments to the standard output."),
        ["enable"] = ("enable [-a] [-dnps] [-f filename] [name ...]", "Enable and disable shell builtins."),
        ["eval"] = ("eval [arg ...]", "Execute arguments as a shell command."),
        ["exec"] = ("exec [-cl] [-a name] [command [argument ...]] [redirection ...]", "Replace the shell with the given command."),
        ["exit"] = ("exit [n]", "Exit the shell."),
        ["export"] = ("export [-fn] [name[=value] ...] or export -p", "Set export attribute for shell variables."),
        ["false"] = ("false", "Return an unsuccessful result."),
        ["fc"] = ("fc [-e ename] [-lnr] [first] [last] or fc -s [pat=rep] [command]", "Display or execute commands from the history list."),
        ["fg"] = ("fg [job_spec]", "Move job to the foreground."),
        ["getopts"] = ("getopts optstring name [arg ...]", "Parse option arguments."),
        ["hash"] = ("hash [-lr] [-p pathname] [-dt] [name ...]", "Remember or display program locations."),
        ["help"] = ("help [-dms] [pattern ...]", "Display information about builtin commands."),
        ["history"] = ("history [-c] [-d offset] [n] or history -anrw [filename] or history -ps arg [arg...]", "Display or manipulate the history list."),
        ["jobs"] = ("jobs [-lnprs] [jobspec ...] or jobs -x command [args]", "Display status of jobs."),
        ["kill"] = ("kill [-s sigspec | -n signum | -sigspec] pid | jobspec ... or kill -l [sigspec]", "Send a signal to a job."),
        ["let"] = ("let arg [arg ...]", "Evaluate arithmetic expressions."),
        ["local"] = ("local [option] name[=value] ...", "Define local variables."),
        ["logout"] = ("logout [n]", "Exit a login shell."),
        ["mapfile"] = ("mapfile [-d delim] [-n count] [-O origin] [-s count] [-t] [-u fd] [-C callback] [-c quantum] [array]", "Read lines from the standard input into an indexed array variable."),
        ["popd"] = ("popd [-n] [+N | -N]", "Remove directories from stack."),
        ["printf"] = ("printf [-v var] format [arguments]", "Formats and prints ARGUMENTS under control of the FORMAT."),
        ["pushd"] = ("pushd [-n] [+N | -N | dir]", "Add directories to stack."),
        ["pwd"] = ("pwd [-LP]", "Print the name of the current working directory."),
        ["read"] = ("read [-ers] [-a array] [-d delim] [-i text] [-n nchars] [-N nchars] [-p prompt] [-t timeout] [-u fd] [name ...]", "Read a line from the standard input and split it into fields."),
        ["readarray"] = ("readarray [-d delim] [-n count] [-O origin] [-s count] [-t] [-u fd] [-C callback] [-c quantum] [array]", "Read lines from a file into an array variable."),
        ["readonly"] = ("readonly [-aAf] [name[=value] ...] or readonly -p", "Mark shell variables as unchangeable."),
        ["return"] = ("return [n]", "Return from a shell function."),
        ["set"] = ("set [-abefhkmnptuvxBCEHPT] [-o option-name] [--] [-] [arg ...]", "Set or unset values of shell options and positional parameters."),
        ["shift"] = ("shift [n]", "Shift positional parameters."),
        ["shopt"] = ("shopt [-pqsu] [-o] [optname ...]", "Set and unset shell options."),
        ["source"] = ("source filename [arguments]", "Execute commands from a file in the current shell."),
        ["suspend"] = ("suspend [-f]", "Suspend shell execution."),
        ["test"] = ("test [expr]", "Evaluate conditional expression."),
        ["times"] = ("times", "Display process times."),
        ["trap"] = ("trap [-lp] [[arg] signal_spec ...]", "Trap signals and other events."),
        ["true"] = ("true", "Return a successful result."),
        ["type"] = ("type [-afptP] name [name ...]", "Display information about command type."),
        ["typeset"] = ("typeset [-aAfFgiIlnrtux] name[=value] ... or typeset -p [-aAfFilnrtux] [name ...]", "Set variable values and attributes."),
        ["ulimit"] = ("ulimit [-SHabcdefiklmnpqrstuvxPRT] [limit]", "Modify shell resource limits."),
        ["umask"] = ("umask [-p] [-S] [mode]", "Display or set file mode mask."),
        ["unalias"] = ("unalias [-a] name [name ...]", "Remove each NAME from the list of defined aliases."),
        ["unset"] = ("unset [-f] [-v] [-n] [name ...]", "Unset values and attributes of shell variables and functions."),
        ["wait"] = ("wait [-fn] [-p var] [id ...]", "Wait for job completion and return exit status."),
    };

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();
        if (FileSystemHelpers.TryHandleVersion(this, "help", args)) return;
        char form = DescriptionOnly ? 'd' : 'l';
        int i = 0;
        for (; i < args.Length && args[i].Length > 1 && args[i][0] == '-'; i++)
        {
            if (args[i] == "--") { i++; break; }
            foreach (char c in args[i].AsSpan(1))
            {
                if (c is 'd' or 's' or 'm') { form = c; continue; }
                FileSystemHelpers.WriteBashError(this, $"bash: help: -{c}: invalid option");
                FileSystemHelpers.WriteBashError(this, "help: usage: help [-dms] [pattern ...]");
                FileSystemHelpers.SetLastExitCode(this, 2);
                return;
            }
        }

        if (i == args.Length)
        {
            foreach (var (name, topic) in Topics) WriteObject(topic.Synopsis);
            FileSystemHelpers.SetLastExitCode(this, 0);
            return;
        }

        // bash prints a header when the FIRST word is a pattern, naming every word.
        if (IsPattern(args[i]))
        {
            WriteObject((args.Length - i > 1 ? "Shell commands matching keywords `" : "Shell commands matching keyword `")
                + string.Join(", ", args[i..]) + "'");
            WriteObject("");
        }

        int status = 0;
        for (; i < args.Length; i++)
        {
            string pattern = args[i];
            bool any = false;
            foreach (var (name, topic) in Topics)
            {
                if (!Matches(pattern, name)) continue;
                any = true;
                foreach (var line in Lines(name, topic, form)) WriteObject(line);
            }
            if (!any)
            {
                FileSystemHelpers.WriteBashError(this,
                    $"bash: help: no help topics match `{pattern}'.  Try `help help' or `man -k {pattern}' or `info {pattern}'.");
                status = 1;
            }
        }
        FileSystemHelpers.SetLastExitCode(this, status);
    }

    private static bool IsPattern(string word) => word.AsSpan().IndexOfAny("*?[") >= 0;

    /// <summary>bash's help: a topic matches when the word is a prefix of its name (<c>help e</c> lists echo, enable, ...)
    /// or, for a pattern, when the pattern matches the whole name.</summary>
    internal static bool Matches(string word, string name) =>
        name.StartsWith(word, StringComparison.Ordinal)
        || (IsPattern(word) && System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(word, name, ignoreCase: false));

    internal static IEnumerable<string> Lines(string name, (string Synopsis, string Description) topic, char form) => form switch
    {
        's' => [$"{name}: {topic.Synopsis}"],
        'd' => [$"{name} - {topic.Description}"],
        'm' => ["NAME", $"    {name} - {topic.Description}", "", "SYNOPSIS", $"    {topic.Synopsis}", ""],
        _ => [$"{name}: {topic.Synopsis}", $"    {topic.Description}", ""],
    };
}