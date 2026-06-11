using System.Collections;
using System.Linq;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashEnv</c>
/// (REFACTOR-2 follow-on). Prints environment variables — with no args,
/// emits all variables sorted by name; with one operand, emits just that
/// variable (or a bash-style error if unset).
///
/// Behavioral parity oracle: the original psm1 function. Output is a
/// typed <c>PsBash.EnvEntry</c> PSObject per variable with <c>Name</c>,
/// <c>Value</c>, <c>BashText = "NAME=VALUE"</c>. Missing-variable error
/// message matches the oracle byte-for-byte: <c>env: 'NAME': not set</c>.
///
/// No colliding flags. The only flag the psm1 oracle accepts is
/// <c>--help</c>, which delegates to psm1 <c>Show-BashHelp</c> via
/// parameter-bound <c>InvokeCommand.InvokeScript</c> (AOT-safe — no
/// concatenation of user-controlled tokens into the script body).
///
/// Aliases <c>env</c> and <c>printenv</c> stay in psm1 and resolve to
/// this cmdlet automatically once <c>PsBash.Cmdlets.dll</c> is loaded.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashEnv")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashEnvCommand : PSCmdlet
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

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "env", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "env"))
            {
                WriteObject(line);
            }
            return;
        }

        // env's core form: `env [-i] [-u NAME] [NAME=VALUE]... [COMMAND [ARG]...]`
        // — run a command with a modified environment. Activated only when an
        // assignment or an -i/-u/-- flag is present, so the legacy print paths
        // (bare `env`, the `env NAME` print-var-ism, `printenv`) are untouched.
        if (i.IsPresent || HasEnvModForm(args))
        {
            HandleEnvModForm(args, i.IsPresent);
            return;
        }

        // `printenv NAME...` prints the VALUE only (one per line), matching bash;
        // `env NAME` is a ps-bash-ism that keeps the NAME=VALUE shape. The two
        // resolve to this same cmdlet via aliases, so distinguish them by the
        // invocation name (Dart wpCPSd25qMuI). `printenv` is never emitter-mapped,
        // so the alias name survives into InvocationName (like gunzip/zcat).
        bool asPrintenv = string.Equals(
            MyInvocation?.InvocationName, "printenv", StringComparison.OrdinalIgnoreCase);

        if (args.Length > 0)
        {
            if (asPrintenv)
            {
                // bash printenv prints each named var's value; a missing var
                // contributes no line (bash exits 1 silently, no "env:" error).
                foreach (var name in args)
                {
                    var v = Environment.GetEnvironmentVariable(name);
                    if (v != null) WriteObject(BuildEntry(name, v, valueOnly: true));
                }
                return;
            }

            var varName = args[0];
            var val = Environment.GetEnvironmentVariable(varName);
            if (val == null)
            {
                FileSystemHelpers.WriteBashError(this, $"env: '{varName}': not set");
                return;
            }
            WriteObject(BuildEntry(varName, val));
            return;
        }

        // No args: enumerate all environment variables, sorted by name
        // (the psm1 oracle's `Sort-Object` step).
        var entries = Environment.GetEnvironmentVariables();
        var names = new List<string>(entries.Count);
        foreach (DictionaryEntry e in entries)
        {
            names.Add(e.Key?.ToString() ?? string.Empty);
        }
        names.Sort(StringComparer.Ordinal);

        foreach (var key in names)
        {
            var v = entries[key]?.ToString() ?? string.Empty;
            WriteObject(BuildEntry(key, v));
        }
    }

    /// <summary>
    /// True when the args use env's run-a-command / modify-environment form:
    /// any <c>NAME=VALUE</c> assignment, or an <c>-i</c> / <c>--ignore-environment</c>
    /// / <c>-u</c> / <c>--unset</c> / <c>--</c> flag. Bare <c>env</c> and the
    /// single-name print forms return false and keep their legacy behavior.
    /// </summary>
    private static bool HasEnvModForm(string[] args)
    {
        foreach (var a in args)
        {
            if (a is "-i" or "--ignore-environment" or "-u" or "--unset" or "--") return true;
            if (a.StartsWith("--unset=", StringComparison.Ordinal)) return true;
            if (IsAssignment(a)) return true;
        }
        return false;
    }

    private void HandleEnvModForm(string[] args, bool ignoreEnvFromFlag)
    {
        bool ignoreEnv = ignoreEnvFromFlag;
        var unset = new List<string>();
        var assignments = new List<(string Name, string Value)>();
        int idx = 0;

        // Leading options.
        while (idx < args.Length)
        {
            var a = args[idx];
            if (a is "-i" or "--ignore-environment") { ignoreEnv = true; idx++; continue; }
            if (a is "-u" or "--unset") { idx++; if (idx < args.Length) unset.Add(args[idx]); idx++; continue; }
            if (a.StartsWith("--unset=", StringComparison.Ordinal)) { unset.Add(a.Substring("--unset=".Length)); idx++; continue; }
            if (a == "--") { idx++; break; }
            break;
        }

        // NAME=VALUE assignments (stop at the first non-assignment — the command).
        while (idx < args.Length && IsAssignment(args[idx]))
        {
            var a = args[idx];
            int eq = a.IndexOf('=');
            assignments.Add((a.Substring(0, eq), a.Substring(eq + 1)));
            idx++;
        }

        // A command follows → run it with the modified environment; otherwise
        // print the effective environment.
        if (idx < args.Length)
        {
            var cmdName = args[idx];
            var cmdArgs = args.Skip(idx + 1).ToArray();
            RunWithModifiedEnv(ignoreEnv, unset, assignments, cmdName, cmdArgs);
        }
        else
        {
            PrintEffectiveEnv(ignoreEnv, unset, assignments);
        }
    }

    /// <summary>
    /// Apply <c>-i</c> / <c>-u</c> / <c>NAME=VALUE</c> to the process environment,
    /// run <paramref name="cmdName"/> in-process (so ps-bash aliases/cmdlets
    /// resolve), then restore the environment. Restoration runs in a finally so a
    /// throwing command cannot leak the temporary environment.
    /// </summary>
    private void RunWithModifiedEnv(
        bool ignoreEnv, List<string> unset, List<(string Name, string Value)> assignments,
        string cmdName, string[] cmdArgs)
    {
        var saved = new List<(string Name, string? Old)>();
        try
        {
            if (ignoreEnv)
            {
                foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
                {
                    var k = e.Key?.ToString();
                    if (k == null) continue;
                    saved.Add((k, e.Value?.ToString()));
                    Environment.SetEnvironmentVariable(k, null);
                }
            }
            foreach (var u in unset)
            {
                saved.Add((u, Environment.GetEnvironmentVariable(u)));
                Environment.SetEnvironmentVariable(u, null);
            }
            foreach (var (n, v) in assignments)
            {
                saved.Add((n, Environment.GetEnvironmentVariable(n)));
                Environment.SetEnvironmentVariable(n, v);
            }

            try
            {
                foreach (var r in InvokeCommand.InvokeScript("param($c,$a) & $c @a", cmdName, cmdArgs))
                {
                    WriteObject(r);
                }
            }
            catch (CommandNotFoundException)
            {
                FileSystemHelpers.WriteBashError(this, $"env: '{cmdName}': No such file or directory");
                FileSystemHelpers.SetLastExitCode(this, 127);
            }
        }
        finally
        {
            // Restore in reverse so an -i full-clear followed by an assignment of
            // the same name returns to the original value.
            for (int k = saved.Count - 1; k >= 0; k--)
            {
                Environment.SetEnvironmentVariable(saved[k].Name, saved[k].Old);
            }
        }
    }

    private void PrintEffectiveEnv(
        bool ignoreEnv, List<string> unset, List<(string Name, string Value)> assignments)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!ignoreEnv)
        {
            foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            {
                var k = e.Key?.ToString();
                if (k != null) dict[k] = e.Value?.ToString() ?? string.Empty;
            }
        }
        foreach (var u in unset) dict.Remove(u);
        foreach (var (n, v) in assignments) dict[n] = v;

        var names = new List<string>(dict.Keys);
        names.Sort(StringComparer.Ordinal);
        foreach (var key in names) WriteObject(BuildEntry(key, dict[key]));
    }

    /// <summary>A <c>NAME=VALUE</c> token: a valid env-var name, then <c>=</c>, then anything.</summary>
    private static bool IsAssignment(string a)
    {
        int eq = a.IndexOf('=');
        return eq > 0 && IsValidName(a.Substring(0, eq));
    }

    private static bool IsValidName(string name)
    {
        if (name.Length == 0) return false;
        if (name[0] != '_' && !char.IsLetter(name[0])) return false;
        for (int i = 1; i < name.Length; i++)
        {
            if (name[i] != '_' && !char.IsLetterOrDigit(name[i])) return false;
        }
        return true;
    }

    private static PSObject BuildEntry(string name, string value, bool valueOnly = false)
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
