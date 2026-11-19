using System.Management.Automation;
using System.Management.Automation.Runspaces;
using PsBash.Core.Parser;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet for the bash <c>command</c> builtin (originally the psm1 function, REFACTOR-2).
///
/// <para>
/// <b>Argument shape</b> (oracle: bash 5.2, <c>wsl bash</c>): <c>command [-pVv] [--] NAME [ARG...]</c>.
/// The option scan stops at the first non-option (or after <c>--</c>); EVERYTHING after NAME is the
/// inner command's argv, verbatim — <c>command ls -d .</c>, <c>command grep -i x f</c> and
/// <c>command echo -v</c> pass <c>-d</c>/<c>-i</c>/<c>-v</c> to the inner command, never to
/// <c>command</c> itself. An unknown option letter is <c>command: -x: invalid option</c> plus the
/// usage line (exit 2). <c>--help</c>/<c>--version</c> are honoured only as leading options.
/// </para>
/// <list type="bullet">
/// <item><c>-v</c>/<c>-V</c> (also in a bundle: <c>-pv</c>) select the lookup form: for each operand,
/// bash's lookup order as <c>type</c> resolves it. <c>-v</c> prints the name (or a file's path), <c>-V</c>
/// <c>type</c>'s sentence. A miss prints nothing under <c>-v</c> and <c>command: NAME: not found</c> under
/// <c>-V</c>; the status is 1 only when nothing resolved.</item>
/// <item>Without them NAME is RUN with the remaining args, bypassing shell FUNCTIONS (bash: functions
/// are skipped; <c>f() { …; }; command f</c> is "command not found", exit 127). Resolution is
/// alias, then cmdlet, then external application/script, so <c>command ls</c> reaches the runtime's
/// own ls. Pipeline input is forwarded to the inner command. <c>-p</c> (default PATH) is accepted and
/// ignored.</item>
/// </list>
///
/// <para>
/// <b>Flag collisions</b>: the transpiler puts <c>command</c> on <c>PsEmitter.OrderedArgCommands</c>, so
/// every dash literal arrives single-quoted in <see cref="Arguments"/>, in order, and the binder never
/// sees them. The <c>V</c>/<c>P</c> decoy switches exist for DIRECT PowerShell calls only
/// (<c>Invoke-BashCommand -v ls</c>) and are re-injected first via
/// <see cref="BashRuntime.PrependDecoys"/>; the case-insensitive binder makes <c>-V</c> land on <c>V</c>
/// too (same as the oracle, which treated the two identically).
/// </para>
///
/// Directive 12: command names are passed only as bound parameters/arguments of a fixed
/// <see cref="PSCmdlet.InvokeCommand"/> script body — never concatenated into it — so a name
/// containing <c>;</c> / <c>$()</c> / scriptblock chars / backticks stays a literal string.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashCommand")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashCommandCommand : BashCmdlet
{
    // Declared because the bare token -v prefix-matches the -Verbose common
    // parameter. Captures both -v and -V (binder is case-insensitive).
    [Parameter] public SwitchParameter V { get; set; }

    // Declared because the bare token -p prefix-matches -PipelineVariable /
    // -ProgressAction. Accepted and ignored (use default PATH).
    [Parameter] public SwitchParameter P { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Upstream pipeline objects, forwarded to the inner command as its stdin.</summary>
    [Parameter(ValueFromPipeline = true)]
    public PSObject? StdinObject { get; set; }

    private readonly List<object> _stdin = new();

    protected override void ProcessRecord()
    {
        if (StdinObject != null) _stdin.Add(StdinObject);
    }

    protected override void EndProcessing()
    {
        var args = BashRuntime.PrependDecoys(Arguments, (V.IsPresent, "-v"), (P.IsPresent, "-p"));
        // The binder is case-insensitive, so a swallowed `-V` is recovered from this command's own text.
        _verboseText = V.IsPresent && System.Text.RegularExpressions.Regex.IsMatch(BashRuntime.CurrentPipelineSegment(MyInvocation) ?? "", @"(?:^|\s)(?:command|Invoke-BashCommand)\s+(?:-[a-zA-Z]*\s+)*-[a-zA-Z]*V");

        FileSystemHelpers.SetLastExitCode(this, 0);

        // Leading option scan: stops at the first non-option; `--` ends it. Everything after is the
        // inner argv (or, under -v/-V, the names to look up).
        bool verbose = false;
        int i = 0;
        for (; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--") { i++; break; }
            if (a.Length < 2 || a[0] != '-') break;

            if (a == "--version")
            {
                FileSystemHelpers.TryHandleVersion(this, "command", new[] { a });
                return;
            }
            if (a == "--help")
            {
                foreach (var line in InvokeCommand.InvokeScript(
                             "param($n) Show-BashHelp $n", "command"))
                {
                    WriteObject(line);
                }
                return;
            }

            for (int k = 1; k < a.Length; k++)
            {
                switch (a[k])
                {
                    case 'v':
                    case 'V':
                        verbose = true;
                        if (a[k] == 'V') _verboseText = true;
                        break;
                    case 'p':
                        break; // default PATH: accepted, ignored
                    default:
                        FileSystemHelpers.WriteBashError(this, $"command: -{a[k]}: invalid option\n" +
                            "command: usage: command [-pVv] command [arg ...]");
                        FileSystemHelpers.SetLastExitCode(this, 2);
                        return;
                }
            }
        }

        if (i >= args.Length) return; // bash: `command` alone is a no-op, exit 0

        var rest = args.AsSpan(i).ToArray();
        if (verbose) LookUp(rest);
        else RunInner(rest);
    }

    /// <summary>
    /// The -v/-V form: one line per operand that resolves; a name that does not resolve prints nothing.
    /// Exit 1 only when NO operand resolved (bash 5.2: <c>command -v bash nosuch</c> prints bash's path, exit 0).
    /// </summary>
    private bool _verboseText;

    private void LookUp(string[] operands)
    {
        bool anyFound = false;
        foreach (var name in operands)
        {
            var output = Describe(name);
            if (output is null)
            {
                // bash: `command -V nosuch` reports the miss on stderr; `command -v nosuch` is silent.
                if (_verboseText) FileSystemHelpers.WriteBashError(this, $"bash: command: {name}: not found");
                continue;
            }
            anyFound = true;
            foreach (var line in BashRuntime.EmitBashLines(output + "\n"))
                WriteObject(line);
            // `command -V f` follows "f is a function" with the definition, as `type f` does.
            if (_verboseText && output == $"{name} is a function" && DeclarePrinter.FindBashFunction(this, name) is { } fn
                && PsBuild.TryReadFunctionSource(fn.Definition) is not null)
                DeclarePrinter.PrintFunction(this, fn);
        }
        if (!anyFound && operands.Length > 0) FileSystemHelpers.SetLastExitCode(this, 1);
    }

    /// <summary>
    /// What <c>command -v NAME</c> / <c>-V NAME</c> prints, by bash's lookup order (alias, keyword, function,
    /// builtin, file; <see cref="InvokeBashTypeCommand.Resolve"/>, the lookup <c>type</c> uses). <c>-V</c> is
    /// <c>type</c>'s sentence. <c>-v</c> prints an alias as <c>alias NAME='VALUE'</c>, a keyword, function or builtin
    /// as its name, and a file as its PATH entry was written (a NAME with a slash is printed as given). A ps-bash
    /// command (a runtime alias to an <c>Invoke-Bash*</c> cmdlet) prints its BASH name: `$(command -v ls)` must
    /// still run ls. A builtin the emitter implements (<c>true</c>, <c>eval</c>) has no PowerShell command behind
    /// it, so the lookup by <c>Get-Command</c> alone missed it.
    /// </summary>
    private string? Describe(string name)
    {
        if (name.Length == 0) return null;
        var match = InvokeBashTypeCommand.Resolve(this, name, pathSearchOnly: false, skipFunctions: false).FirstOrDefault();
        if (match is not null)
        {
            if (_verboseText) return match.Text;
            return match.Kind switch
            {
                "alias" => SessionState.PSVariable.GetValue("BashUserAliases") is System.Collections.IDictionary al
                    ? $"alias {name}='{al[name]?.ToString()?.Replace("'", "'\\''")}'"
                    : name,
                "file" => match.Path ?? name,
                _ => name,
            };
        }
        // A file named with a slash was answered above (or is not executable). Otherwise, under -v, what
        // PowerShell itself can run: the runtime's functions and cmdlets.
        if (_verboseText || name.Contains('/') || name.Contains('\\')) return null;
        var cmd = ResolveCommand(name);
        if (cmd is null) return null;
        switch (cmd.CommandType)
        {
            case CommandTypes.Alias:
                var definition = ((AliasInfo)cmd).Definition;
                return definition.StartsWith("Invoke-Bash", StringComparison.Ordinal) ? name : definition;
            case CommandTypes.Function:
                return cmd.Name;
            default:
                // Application: its path. Cmdlet: its module (the psm1 oracle's $cmd.Source). Shell spelling (C:/x).
                return PsBash.Core.BashPath.ToShell(cmd.Source);
        }
    }

    /// <summary>
    /// Run <c>NAME ARGS...</c> bypassing shell functions. <paramref name="argv"/>[0] is the name; the
    /// rest is splatted as an array of strings, which PowerShell binds as positional ARGUMENTS (never as
    /// parameter tokens) — the same shape the transpiler's single-quoted flags have.
    /// </summary>
    /// <summary>The records as the bytes a program reads (<see cref="NativeStdinBridge.RecordBytes"/>), one byte[] each.</summary>
    private static List<object> NativeStdinBytes(List<object> records)
    {
        var bytes = new List<object>(records.Count);
        foreach (var r in records)
        {
            var b = r is PSObject { BaseObject: byte[] raw } ? raw : r as byte[] ?? NativeStdinBridge.RecordBytes(r);
            if (b.Length > 0) bytes.Add(b);
        }
        return bytes;
    }

    private void RunInner(string[] argv)
    {
        // command command x, command builtin x: the wrappers only bypass functions, so peel them.
        while (argv.Length > 1 && argv[0] is "command" or "builtin" && !argv[1].StartsWith('-')) argv = argv[1..];
        // A builtin the emitter implements (`command eval …`, `builtin set -- a`, `command exec cmd`) has no cmdlet to
        // resolve: it runs as its static command, the operands already expanded (BashBuiltins.RunAsSource, the same
        // runner Resolve-BashCommand uses for `$cmd`).
        if (BashBuiltins.RunAsSource.Contains(argv[0]))
        {
            var callArgs = new object[argv.Length];
            callArgs[0] = ScriptBlock.Create(PsBuild.BuiltinAsSourceScript(argv[0]));
            for (int k = 1; k < argv.Length; k++) callArgs[k] = argv[k];
            // Dot-sourced: the caller's scope, as the literal builtin runs (`command eval 'x=1'` sets x there). 2>&1 and
            // re-written: the nested script's error stream is not this cmdlet's, so `command . 2>/dev/null` printed anyway.
            const string asSource = "$__psbash_cr = $args[0]; $__psbash_ca = @($args | Select-Object -Skip 1); ";
            var runner = ScriptBlock.Create(asSource + (_stdin.Count > 0 ? "$input | " : "") + ". $__psbash_cr @__psbash_ca 2>&1");
            foreach (var o in InvokeCommand.InvokeScript(false, runner, _stdin.Count > 0 ? _stdin : null, callArgs))
            {
                if (o?.BaseObject is ErrorRecord er) FileSystemHelpers.WriteErrorRecord(this, er);
                else WriteObject(o);
            }
            return;
        }
        var name = argv[0];
        // `command :`, `command false`, a bare `command builtin`: Resolve-BashCommand's own trivial blocks
        // (BashBuiltins.CallStackBuiltins); there is no command of that name to find.
        if (name is ":" or "true" or "builtin") { FileSystemHelpers.SetLastExitCode(this, 0); return; }
        if (name == "false") { FileSystemHelpers.SetLastExitCode(this, 1); return; }
        CommandInfo? target = null;
        try
        {
            // A builtin is its run-time command first (`command bind`, `command enable -n x`): Invoke-Bash<Name>, a cmdlet
            // or a module function, which the bare name finds only when an alias maps it (BashBuiltins, the one table).
            var found = InvokeCommand.InvokeScript(
                "param($n, $b) if ($b) { $f = Get-Command ('Invoke-Bash' + [WildcardPattern]::Escape($n)) -CommandType Cmdlet,Function " +
                "-ErrorAction SilentlyContinue | Select-Object -First 1; if ($f) { return $f } }; " +
                "Get-Command ([WildcardPattern]::Escape($n)) -CommandType Alias,Cmdlet,Application,ExternalScript " +
                "-ErrorAction SilentlyContinue | Select-Object -First 1", name, BashBuiltins.Names.Contains(name) && name != "[");
            foreach (var r in found)
            {
                if (r?.BaseObject is CommandInfo ci) { target = ci; break; }
            }
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
        }

        if (target == null)
        {
            FileSystemHelpers.WriteBashError(this, ShellDiagnostic.Prefix(SessionState) + $"{name}: command not found");
            FileSystemHelpers.SetLastExitCode(this, 127);
            return;
        }

        // A program file the OS cannot start as it is (`command ./configure`, a #! script on PATH): Get-Command hands
        // back the raw ApplicationInfo, so the lookup hook's stand-in never applied; ProgramFile gives the same one.
        object invokeTarget = target;
        if (target is ApplicationInfo file && PsBash.Core.ProgramFile.Plan(file.Path) is { } launch)
            invokeTarget = ScriptBlock.Create(launch.StandInScript(PsBash.Core.ProgramFile.Shown(name, file.Path)));

        // `command prog 2>&1`: a program gets ONE pipe for stdout and stderr (docs/specs/stdio-messaging.md §4). In a
        // background job always through RunMerged: `& $c` would give it the process environment, not the job's scalars.
        if (invokeTarget is ApplicationInfo app && !InvokeBashNativeCommand.IsBatchFile(app.Path)
            && (StderrMerge.JoinsStdout(this) || PsBash.Core.ScalarOverlay.Current is not null))
        {
            try
            {
                FileSystemHelpers.SetLastExitCode(this, InvokeBashNativeCommand.RunMerged(this, app.Path, argv.Skip(1),
                    inheritStdin: true, input: _stdin.Count > 0 ? _stdin : null));
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                FileSystemHelpers.WriteBashError(this, $"command: {name}: {ex.Message}");
            }
            return;
        }

        // Fixed body: $args[0] is the resolved command, the rest is splatted. No user text is ever
        // part of the script.
        const string prologue =
            "$c = $args[0]; $rest = @(); " +
            "if ($args.Count -gt 1) { $rest = $args[1..($args.Count - 1)] }; ";
        // With upstream input the inner command must be fed it explicitly (`$input` is the list
        // handed to InvokeScript); without, it inherits the caller's stdin untouched.
        // `[` is called by that name, spelled in the call: Invoke-BashTest knows its bracket form (the closing `]`)
        // from the text it was invoked by, and `& $c` reached it as plain `test` ("too many arguments").
        // 2>&1: the nested script's error stream is not this cmdlet's, so `command ls /x 2>/dev/null` printed the error
        // anyway; merged here, each ErrorRecord is re-written on this command's error stream below.
        string call = (name == "[" ? "& '[' @rest" : "& $c @rest") + " 2>&1";
        var invokeBody = _stdin.Count > 0 ? prologue + "$input | " + call : prologue + call;
        var invokeArgs = new object[argv.Length];
        invokeArgs[0] = invokeTarget;
        for (int k = 1; k < argv.Length; k++) invokeArgs[k] = argv[k];

        // A program is fed the records' BYTES, as a pipe into it is (ConvertTo-BashNativeStdin): PowerShell's native
        // pipe formats any other object, so `printf 'a\n' | command cat` gave cat a BashText table.
        var stdin = invokeTarget is ApplicationInfo ? NativeStdinBytes(_stdin) : _stdin;

        try
        {
            var output = _stdin.Count > 0
                ? InvokeCommand.InvokeScript(invokeBody, false, PipelineResultTypes.None, stdin, invokeArgs)
                : InvokeCommand.InvokeScript(invokeBody, invokeArgs);
            foreach (var item in output)
            {
                if (item?.BaseObject is ErrorRecord er) FileSystemHelpers.WriteErrorRecord(this, er);
                else WriteObject(item);
            }
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            FileSystemHelpers.WriteBashError(this, $"command: {name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Look up <paramref name="name"/> via <c>Get-Command</c>. The lookup is
    /// parameter-bound through <see cref="PSCmdlet.InvokeCommand"/> so the
    /// name token is never re-parsed as PowerShell (Directive 12).
    /// </summary>
    private CommandInfo? ResolveCommand(string name)
    {
        // `command -v`/`-V` describe NAME without running it, so nothing is hashed (as in bash).
        using var probe = PsBash.Core.CommandHashTable.Probe();
        try
        {
            // The name is a literal command name, not a wildcard pattern: `command [ -n x ]` failed with
            // "[: command not found" (Get-Command rejects the pattern `[`).
            var results = InvokeCommand.InvokeScript(
                "param($n) Get-Command ([WildcardPattern]::Escape($n)) -ErrorAction SilentlyContinue", name);
            foreach (var r in results)
            {
                if (r?.BaseObject is CommandInfo ci) return ci;
            }
        }
        catch
        {
            return null;
        }
        return null;
    }
}
