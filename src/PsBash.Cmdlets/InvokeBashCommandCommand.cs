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
public sealed class InvokeBashCommandCommand : PSCmdlet
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
                // Application: its path. Cmdlet: its module (the psm1 oracle's $cmd.Source).
                return cmd.Source;
        }
    }

    /// <summary>
    /// Run <c>NAME ARGS...</c> bypassing shell functions. <paramref name="argv"/>[0] is the name; the
    /// rest is splatted as an array of strings, which PowerShell binds as positional ARGUMENTS (never as
    /// parameter tokens) — the same shape the transpiler's single-quoted flags have.
    /// </summary>
    private static readonly HashSet<string> DeclarationBuiltins = new(StringComparer.Ordinal) { "export", "readonly", "local", "declare", "typeset" };

    private void RunInner(string[] argv)
    {
        // command command x, command builtin x: the wrappers only bypass functions, so peel them.
        while (argv.Length > 1 && argv[0] is "command" or "builtin" && !argv[1].StartsWith('-')) argv = argv[1..];
        // A declaration builtin has no cmdlet to resolve: run it as bash source (the operands are already expanded).
        if (DeclarationBuiltins.Contains(argv[0]))
        {
            var src = string.Join(' ', argv.Select(a => "'" + a.Replace("'", "'\\''") + "'"));
            src = argv[0] + src[(argv[0].Length + 2)..];
            var ps = PsBash.Core.Transpiler.BashTranspiler.TranspileWithMap(src, PsBash.Core.Transpiler.TranspileContext.Eval).PowerShell;
            foreach (var o in InvokeCommand.InvokeScript(false, ScriptBlock.Create(ps), null, Array.Empty<object>())) WriteObject(o);
            return;
        }
        var name = argv[0];
        CommandInfo? target = null;
        try
        {
            var found = InvokeCommand.InvokeScript(
                "param($n) Get-Command $n -CommandType Alias,Cmdlet,Application,ExternalScript " +
                "-ErrorAction SilentlyContinue | Select-Object -First 1", name);
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
            FileSystemHelpers.WriteBashError(this, $"bash: {name}: command not found");
            FileSystemHelpers.SetLastExitCode(this, 127);
            return;
        }

        // Fixed body: $args[0] is the resolved command, the rest is splatted. No user text is ever
        // part of the script.
        const string prologue =
            "$c = $args[0]; $rest = @(); " +
            "if ($args.Count -gt 1) { $rest = $args[1..($args.Count - 1)] }; ";
        // With upstream input the inner command must be fed it explicitly (`$input` is the list
        // handed to InvokeScript); without, it inherits the caller's stdin untouched.
        var invokeBody = _stdin.Count > 0 ? prologue + "$input | & $c @rest" : prologue + "& $c @rest";
        var invokeArgs = new object[argv.Length];
        invokeArgs[0] = target;
        for (int k = 1; k < argv.Length; k++) invokeArgs[k] = argv[k];

        try
        {
            var output = _stdin.Count > 0
                ? InvokeCommand.InvokeScript(invokeBody, false, PipelineResultTypes.None, _stdin, invokeArgs)
                : InvokeCommand.InvokeScript(invokeBody, invokeArgs);
            foreach (var item in output) WriteObject(item);
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
        try
        {
            var results = InvokeCommand.InvokeScript(
                "param($n) Get-Command $n -ErrorAction SilentlyContinue", name);
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
