using System.Management.Automation;
using System.Management.Automation.Runspaces;

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
/// <item><c>-v</c>/<c>-V</c> (also in a bundle: <c>-pv</c>) select the lookup form: for each operand
/// run <c>Get-Command NAME</c> and emit the alias definition / function name / source; the first miss
/// sets <c>$LASTEXITCODE = 1</c> and stops (parity with the psm1 oracle; -v and -V are identical).</item>
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

    /// <summary>The -v/-V form: describe each operand (first miss = exit 1, stop).</summary>
    private void LookUp(string[] operands)
    {
        foreach (var name in operands)
        {
            string? output = null;

            var cmd = ResolveCommand(name);
            if (cmd != null)
            {
                switch (cmd.CommandType)
                {
                    case CommandTypes.Alias:
                        output = ((AliasInfo)cmd).Definition;
                        break;
                    case CommandTypes.Function:
                        output = cmd.Name;
                        break;
                    default:
                        // Oracle: $cmd.Source (Application / Cmdlet / etc.).
                        output = cmd.Source;
                        break;
                }
            }

            if (output != null)
            {
                foreach (var line in BashRuntime.EmitBashLines(output))
                {
                    WriteObject(line);
                }
            }
            else
            {
                FileSystemHelpers.SetLastExitCode(this, 1);
                return;
            }
        }
    }

    /// <summary>
    /// Run <c>NAME ARGS...</c> bypassing shell functions. <paramref name="argv"/>[0] is the name; the
    /// rest is splatted as an array of strings, which PowerShell binds as positional ARGUMENTS (never as
    /// parameter tokens) — the same shape the transpiler's single-quoted flags have.
    /// </summary>
    private void RunInner(string[] argv)
    {
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
