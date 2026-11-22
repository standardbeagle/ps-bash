using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Coreutils <c>env</c>: with no COMMAND, print the (modified) environment as <c>NAME=VALUE</c> lines, sorted by name;
/// with one, run it with the modified environment: <c>env [-i] [-0] [-u NAME] [-C DIR] [-] [NAME=VALUE]... [COMMAND
/// [ARG]...]</c>. Usage errors are status 125, a COMMAND that is not found 127, one that cannot run 126 (GNU).
/// <c>printenv</c> is its own cmdlet (<see cref="InvokeBashPrintenvCommand"/>): an alias name does not survive an
/// indirect call (<c>env X=1 printenv X</c> runs <c>&amp; $c</c>, where InvocationName is <c>&amp;</c>), so a command's
/// identity is never read from InvocationName (guard <c>InvocationIdentityGuardTests</c>).
///
/// Output is a typed <c>PsBash.EnvEntry</c> PSObject per variable with <c>Name</c>, <c>Value</c>,
/// <c>BashText = "NAME=VALUE"</c>. <c>-i</c> collides with <c>-InformationAction</c>, hence the decoy (a direct
/// call binds it; the transpiler's words arrive in <see cref="Arguments"/>).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashEnv")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashEnvCommand : BashCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>
    /// Bash <c>-i</c> (ignore-environment) decoy. A bare <c>-i</c> is an
    /// ambiguous prefix of the common parameters <c>-InformationAction</c> /
    /// <c>-InformationVariable</c>, so without an exact single-letter declaration
    /// the binder rejects it before it reaches <see cref="Arguments"/>. The long
    /// form <c>--ignore-environment</c> has no collision and is parsed from
    /// <see cref="Arguments"/>.
    /// </summary>
    [Parameter] public SwitchParameter i { get; set; }

    /// <summary>
    /// The pipe into <c>env</c>: the COMMAND's stdin (<c>echo x | env FOO=1 cat</c> prints x). A cmdlet that declares no
    /// pipeline parameter REJECTS piped records ("The input object cannot be bound"); they were bound here and dropped,
    /// so the command read nothing. <c>env</c> alone (the listing) ignores them.
    /// </summary>
    [Parameter(ValueFromPipeline = true)]
    [System.Management.Automation.AllowNull]
    public PSObject? InputObject { get; set; }

    private readonly List<object> _stdin = new();

    // The work runs ONCE, in EndProcessing: with a pipeline parameter PowerShell calls ProcessRecord per
    // input record, and not at all when the upstream stage produced nothing.
    protected override void ProcessRecord()
    {
        if (InputObject is not null) _stdin.Add(InputObject);
    }

    protected override void EndProcessing()
    {
        // A DIRECT call (`Invoke-BashEnv -i ...`) binds the decoy; the transpiler's words arrive in Arguments.
        var args = BashRuntime.PrependDecoys(Arguments, (i.IsPresent, "-i"));

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (args.Length > 0 && args[0] == "--help")
        {
            foreach (var line in InvokeCommand.InvokeScript("param($n) Show-BashHelp $n", "env"))
                WriteObject(line);
            return;
        }
        if (args.Length > 0 && args[0] == "--version" && FileSystemHelpers.TryHandleVersion(this, "env", args)) return;

        // GNU `env [OPTION]... [-] [NAME=VALUE]... [COMMAND [ARG]...]`. Options end at the first operand, `--`
        // or `-`; usage errors are status 125 (env's own failure, distinct from the command's status).
        if (ParseOptions(args) is not { } opts) return;
        int idx = opts.Next;

        var assignments = new List<(string Name, string Value)>();
        while (idx < args.Length && IsAssignment(args[idx]))
        {
            var a = args[idx];
            int eq = a.IndexOf('=');
            assignments.Add((a.Substring(0, eq), a.Substring(eq + 1)));
            idx++;
        }

        if (idx < args.Length)
        {
            if (opts.Null)
            {
                UsageError("cannot specify --null (-0) with command");
                return;
            }
            RunWithModifiedEnv(opts.IgnoreEnv, opts.Unset, assignments, opts.ChDir, args[idx], args.Skip(idx + 1).ToArray());
            return;
        }
        if (opts.ChDir is not null)
        {
            UsageError("must specify command with --chdir (-C)");
            return;
        }
        PrintEffectiveEnv(opts.IgnoreEnv, opts.Unset, assignments, opts.Null);
    }

    private sealed record EnvOptions(bool IgnoreEnv, List<string> Unset, bool Null, string? ChDir, int Next);

    /// <summary>GNU env's option scan; null after writing the usage error (status 125).</summary>
    private EnvOptions? ParseOptions(string[] args)
    {
        bool ignoreEnv = false, nul = false;
        string? chdir = null;
        var unset = new List<string>();
        int idx = 0;
        while (idx < args.Length)
        {
            var a = args[idx];
            if (a == "--") { idx++; break; }
            if (a == "-") { ignoreEnv = true; idx++; break; }
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                int eq = a.IndexOf('=');
                string name = eq < 0 ? a[2..] : a[2..eq];
                string? inline = eq < 0 ? null : a[(eq + 1)..];
                string? Value()
                {
                    if (inline is not null) return inline;
                    if (idx + 1 < args.Length) return args[++idx];
                    UsageError($"option '--{name}' requires an argument");
                    return null;
                }
                switch (name)
                {
                    case "ignore-environment": ignoreEnv = true; break;
                    case "null": nul = true; break;
                    case "unset": { if (Value() is not { } v) return null; unset.Add(v); break; }
                    case "chdir": { if (Value() is not { } v) return null; chdir = v; break; }
                    case "split-string" or "debug" or "default-signal" or "ignore-signal" or "block-signal" or "list-signal-handling":
                        UsageError($"option '{a}' is recognized but not supported by ps-bash", hint: false);
                        return null;
                    default:
                        UsageError($"unrecognized option '{a}'");
                        return null;
                }
                idx++;
                continue;
            }
            if (a.Length < 2 || a[0] != '-') break;

            for (int k = 1; k < a.Length; k++)
            {
                char ch = a[k];
                if (ch is 'u' or 'C')
                {
                    string? v = k + 1 < a.Length ? a[(k + 1)..] : idx + 1 < args.Length ? args[++idx] : null;
                    if (v is null) { UsageError($"option requires an argument -- '{ch}'"); return null; }
                    if (ch == 'u') unset.Add(v); else chdir = v;
                    break;
                }
                if (ch == 'i') ignoreEnv = true;
                else if (ch == '0') nul = true;
                else if (ch is 'S' or 'v')
                {
                    UsageError($"option '-{ch}' is recognized but not supported by ps-bash", hint: false);
                    return null;
                }
                else { UsageError($"invalid option -- '{ch}'"); return null; }
            }
            idx++;
        }
        return new EnvOptions(ignoreEnv, unset, nul, chdir, idx);
    }

    /// <summary><c>env: WHAT</c> (+ the usage hint), status 125 — env's own failure.</summary>
    private void UsageError(string what, bool hint = true)
    {
        string message = "env: " + what;
        if (hint && FileSystemHelpers.UsageHint("env") is { } h) message += "\n" + h;
        FileSystemHelpers.WriteBashError(this, message);
        FileSystemHelpers.SetLastExitCode(this, 125);
    }

    /// <summary>
    /// Run <paramref name="cmdName"/> in-process (so ps-bash aliases/cmdlets resolve) with its OWN environment: the
    /// shell's exported variables with <c>-i</c> / <c>-u</c> / <c>NAME=VALUE</c> applied
    /// (<see cref="PsBash.Core.ScalarOverlay.ForCommand"/>), attached to this runspace for the command only. The process
    /// environment is never written: <c>env -i</c> cleared it around the command, and every other runspace of the host
    /// process saw TEMP and PATH unset meanwhile (environment-model.md section 11). A program it starts gets that
    /// environment (<see cref="BashVariableStore.ApplyTo"/>). A COMMAND that is not a program is GNU's
    /// <c>env: ‘X’: No such file or directory</c> 127; a directory <c>Permission denied</c> 126.
    /// </summary>
    private void RunWithModifiedEnv(
        bool ignoreEnv, List<string> unset, List<(string Name, string Value)> assignments,
        string? chdir, string cmdName, string[] cmdArgs)
    {
        var environment = PsBash.Core.ScalarOverlay.ForCommand(ignoreEnv, unset,
            assignments.Select(a => new KeyValuePair<string, string>(a.Name, a.Value)));
        IDisposable? attached = null;
        ChildDirectory? childDir = null;
        try
        {
            if (chdir is not null)
            {
                var target = FileSystemHelpers.ProviderPath(this, chdir);
                if (!Directory.Exists(target))
                {
                    FileSystemHelpers.WriteBashError(this, $"env: cannot change directory to {GnuQuote.Always(chdir)}: No such file or directory");
                    FileSystemHelpers.SetLastExitCode(this, 125);
                    return;
                }
                childDir = ChildDirectory.Enter(this, target);
            }
            attached = environment.AttachScoped(BashJobRunspaces.ScalarOwner());

            if (Unrunnable(cmdName) is { } failure)
            {
                FileSystemHelpers.WriteBashError(this, $"env: {GnuQuote.Locale(cmdName)}: {failure.Why}");
                FileSystemHelpers.SetLastExitCode(this, failure.Status);
                return;
            }

            // The shared runner: the pipe is the command's stdin, its errors go where env's do (`2>/dev/null`), a program
            // under `2>&1` gets one pipe for both streams.
            InnerCommand.Run(this, EnvWrapper, [cmdName, .. cmdArgs], _stdin);
        }
        finally
        {
            attached?.Dispose();
            childDir?.Dispose();
        }
    }

    /// <summary>GNU env: a name no program answers to is <c>env: ‘X’: No such file or directory</c>, 127; env runs
    /// programs, never a builtin the emitter implements.</summary>
    private static readonly InnerCommand.Wrapper EnvWrapper = new("env", static (cmdlet, name) =>
    {
        FileSystemHelpers.WriteBashError(cmdlet, $"env: {GnuQuote.Locale(name)}: No such file or directory");
        FileSystemHelpers.SetLastExitCode(cmdlet, 127);
    }, ShellBuiltins: false);
    /// <summary>
    /// Why <paramref name="name"/> cannot run as a program (execvp's answer), or null: a path naming a directory
    /// is 126 "Permission denied", a missing path or a name no command answers to is 127.
    /// </summary>
    private (string Why, int Status)? Unrunnable(string name)
    {
        bool isPath = name.Contains('/') || (OperatingSystem.IsWindows() && name.Contains('\\'));
        if (isPath)
        {
            var abs = FileSystemHelpers.ProviderPath(this, name);
            if (Directory.Exists(abs)) return ("Permission denied", 126);
            return File.Exists(abs) ? null : ("No such file or directory", 127);
        }
        if (name.Length == 0) return ("No such file or directory", 127);
        // Get-Command searches; InvokeCommand.GetCommand also fires the host's CommandNotFoundAction, which answers
        // EVERY name with a "command not found" stub — so `env nosuch` printed bash's message, not env's.
        foreach (var r in InvokeCommand.InvokeScript(
            "param($n) Get-Command ([WildcardPattern]::Escape($n)) -CommandType Alias,Cmdlet,Function,Application,ExternalScript " +
            "-ErrorAction SilentlyContinue | Select-Object -First 1", name))
        {
            if (r?.BaseObject is CommandInfo) return null;
        }
        return ("No such file or directory", 127);
    }
    private void PrintEffectiveEnv(
        bool ignoreEnv, List<string> unset, List<(string Name, string Value)> assignments, bool nul)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!ignoreEnv)
        {
            // Under the bash name: a diverted `path` (stored __psbash_cv_0_PATH, BashNameCase) prints as path=...
            foreach (var (k, v) in BashVariableStore.EnumerateExported())
                dict[PsBash.Core.BashNameCase.ToBash(k)] = v;
        }
        foreach (var u in unset) dict.Remove(u);
        foreach (var (n, v) in assignments) dict[n] = v;

        var names = new List<string>(dict.Keys);
        names.Sort(StringComparer.Ordinal);
        if (nul)
        {
            // -0: each entry ends with NUL, not newline — one exact-bytes record.
            var sb = new System.Text.StringBuilder();
            foreach (var key in names) sb.Append(key).Append('=').Append(dict[key]).Append('\0');
            if (sb.Length > 0) WriteObject(BashRuntime.BytesRecord(sb.ToString()));
            return;
        }
        foreach (var key in names) WriteObject(BuildEntry(key, dict[key]));
    }

    /// <summary>GNU: any operand with <c>=</c> after its first character is an assignment (<c>env 1=x printenv 1</c>).</summary>
    private static bool IsAssignment(string a) => a.IndexOf('=') > 0;

    internal static PSObject BuildEntry(string name, string value, bool valueOnly = false)
    {
        var pso = new PSObject();
        pso.TypeNames.Insert(0, "PsBash.EnvEntry");
        pso.Properties.Add(new PSNoteProperty("Name", name));
        pso.Properties.Add(new PSNoteProperty("Value", value));
        // printenv prints value-only; env keeps the NAME=VALUE shape.
        pso.Properties.Add(new PSNoteProperty("BashText", valueOnly ? value : $"{name}={value}"));
        return pso;
    }
}
