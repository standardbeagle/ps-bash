using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashMapfile</c> function
/// (REFACTOR-2 follow-on). Implements the bash <c>mapfile</c> / <c>readarray</c>
/// builtin: read pipeline input lines into a bash array variable in the
/// caller's scope.
///
/// Behavioral parity oracle: the original psm1 function. Pipeline-only (no
/// stdin / file read — the oracle never accepted operands as filenames, the
/// non-flag operand is the destination array variable name). Empty lines are
/// silently dropped (matches the oracle's <c>if ($line -ne '') { $lines.Add }</c>
/// branch byte-for-byte; this is a parity quirk, not a feature). The default
/// destination variable is <c>MAPFILE</c>.
///
/// Flag surface (parsed from <c>Arguments</c>):
/// <list type="bullet">
/// <item><c>-n N</c> / <c>-nN</c>: cap at most N lines.</item>
/// <item><c>-O ORIGIN</c> / <c>-OORIGIN</c>: start assigning at array index
/// ORIGIN (lower indices receive empty strings — matches the oracle's
/// <c>@(1..$origin | ForEach-Object { '' })</c> prefix slice).</item>
/// <item><c>-s N</c> (skip): the psm1 oracle did not implement this flag —
/// the cmdlet declares the explicit value-bearing parameter and applies it
/// because the task spec requires it, but the differential test for <c>-s</c>
/// will exercise only the cmdlet's added behavior.</item>
/// <item><c>-t</c>: strip trailing newline from each line (the oracle
/// implemented this via <c>TrimEnd("`n"[0], "`r"[0])</c>).</item>
/// <item><c>-d DELIM</c> / <c>-dDELIM</c>: the psm1 oracle accepted but
/// ignored <c>-d</c> (it always split on <c>\n</c>). The cmdlet preserves the
/// "accepted but ignored" contract for parity — the default delimiter is
/// always <c>\n</c>; the value is consumed and dropped.</item>
/// </list>
///
/// Colliding flags declared as explicit parameters (playbook collision table):
/// <list type="bullet">
/// <item><c>-O ORIGIN</c>: bare <c>-O</c> prefix-matches <c>-OutVariable</c> /
/// <c>-OutBuffer</c>. Declared as <c>int? O</c>.</item>
/// <item><c>-d DELIM</c>: bare <c>-d</c> prefix-matches <c>-Debug</c>.
/// Declared as <c>string? D</c>.</item>
/// </list>
/// <c>-n</c>, <c>-s</c>, and <c>-t</c> have no PowerShell common-parameter
/// prefix overlap; they stay in <c>Arguments</c> and are parsed by the manual
/// scan (which also recovers the joined <c>-nN</c> / <c>-OORIGIN</c> /
/// <c>-dDELIM</c> short forms that bypass the binder).
///
/// Writes the resulting array back via
/// <see cref="PSVariableIntrinsics.Set(string, object)"/> on
/// <c>PSCmdlet.SessionState.PSVariable</c>. No stdout output (variable
/// side-effect only). <c>--help</c> delegates to psm1 <c>Show-BashHelp</c>
/// via parameter-bound <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>
/// (AOT-safe). The <c>mapfile</c> and <c>readarray</c> aliases are added in
/// psm1 and resolve to this cmdlet automatically.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashMapfile")]
public sealed class InvokeBashMapfileCommand : PSCmdlet
{
    /// <summary>
    /// <c>-O ORIGIN</c> — start assigning at this array index. Declared
    /// explicitly because the bare token <c>-O</c> prefix-matches the
    /// PowerShell common parameters <c>-OutVariable</c> / <c>-OutBuffer</c>
    /// under <see cref="PSCmdlet"/> parameter binding. An exact param-name
    /// match beats a common-parameter prefix match.
    /// </summary>
    [Parameter]
    public int? O { get; set; }

    /// <summary>
    /// <c>-d DELIM</c> — record delimiter. The psm1 oracle accepted but
    /// ignored this flag (always split on <c>\n</c>); the cmdlet preserves
    /// that parity. Declared explicitly because the bare token <c>-d</c>
    /// prefix-matches <c>-Debug</c>.
    /// </summary>
    [Parameter]
    public string? D { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _pipeline = new();

    protected override void ProcessRecord()
    {
        if (InputObject != null) _pipeline.Add(InputObject);
    }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "mapfile"))
            {
                WriteObject(line);
            }
            return;
        }

        // Defaults match the psm1 oracle.
        int? count = null;
        int origin = O ?? 0;
        int skip = 0;
        bool stripTrailing = false;
        string varName = "MAPFILE";

        // Manual scan recovers joined short forms (-nN / -OORIGIN / -dDELIM)
        // and value-flag pairs the binder did not consume. The explicit -O
        // and -D parameters above are also honored (set the locals first).
        int i = 0;
        while (i < args.Length)
        {
            var a = args[i];
            if (a == "-t") { stripTrailing = true; i++; continue; }
            if (a == "-n" && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out var n)) count = n;
                i += 2; continue;
            }
            if (a.StartsWith("-n") && a.Length > 2)
            {
                if (int.TryParse(a.Substring(2), out var n)) count = n;
                i++; continue;
            }
            if (a == "-O" && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out var oVal)) origin = oVal;
                i += 2; continue;
            }
            if (a.StartsWith("-O") && a.Length > 2)
            {
                if (int.TryParse(a.Substring(2), out var oVal)) origin = oVal;
                i++; continue;
            }
            if (a == "-s" && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out var s)) skip = s;
                i += 2; continue;
            }
            if (a.StartsWith("-s") && a.Length > 2)
            {
                if (int.TryParse(a.Substring(2), out var s)) skip = s;
                i++; continue;
            }
            // -d DELIM and -dDELIM: consumed but the resulting value is
            // not used (oracle parity — always split on \n).
            if (a == "-d" && i + 1 < args.Length) { i += 2; continue; }
            if (a.StartsWith("-d") && a.Length > 2) { i++; continue; }

            // Non-flag = destination variable name. Matches the oracle's
            // "last non-flag wins" contract. Reject inputs containing
            // PowerShell scriptblock metacharacters defensively (Directive
            // 12) — emit a bash-style error and skip the assignment.
            if (!a.StartsWith('-'))
            {
                if (a.Contains('$') || a.Contains('(') || a.Contains(';') ||
                    a.Contains('{') || a.Contains('`') || a.Contains('"'))
                {
                    FileSystemHelpers.WriteBashError(
                        this, $"mapfile: `{a}': not a valid identifier");
                    return;
                }
                varName = a;
            }
            i++;
        }

        // Collect pipeline input into lines, dropping empty lines (oracle
        // parity — the psm1 oracle's `if ($line -ne '') { $lines.Add }`
        // branch is reproduced byte-for-byte).
        var lines = new List<string>();
        foreach (var item in _pipeline)
        {
            var text = BashRuntime.GetBashText(item);
            if (string.IsNullOrEmpty(text)) continue;
            var normalized = text.Replace("\r\n", "\n");
            foreach (var line in normalized.Split('\n'))
            {
                if (line.Length > 0) lines.Add(line);
            }
        }

        // -s N: skip the first N lines (cmdlet addition; oracle did not
        // implement, but the playbook task spec requires).
        if (skip > 0 && skip < lines.Count) lines.RemoveRange(0, skip);
        else if (skip >= lines.Count) lines.Clear();

        // -n N: cap to first N lines.
        if (count.HasValue && lines.Count > count.Value)
        {
            lines = lines.GetRange(0, count.Value);
        }

        // -t: strip trailing CR/LF from each line.
        if (stripTrailing)
        {
            for (int j = 0; j < lines.Count; j++)
            {
                lines[j] = lines[j].TrimEnd('\n', '\r');
            }
        }

        // Build result array. With origin > 0, prefix with that many empty
        // strings (oracle's `@(1..$origin | ForEach-Object { '' })` slice).
        string[] result;
        if (origin > 0)
        {
            result = new string[origin + lines.Count];
            for (int j = 0; j < origin; j++) result[j] = "";
            for (int j = 0; j < lines.Count; j++) result[origin + j] = lines[j];
        }
        else
        {
            result = lines.ToArray();
        }

        // Set the variable in the caller's scope. The psm1 oracle used
        // `Set-Variable -Name $varName -Value $result` with no -Scope flag,
        // which in a function body defaults to the caller's scope. For a
        // binary cmdlet, SessionState.PSVariable.Set has the same effect
        // (sets the variable in the runspace's current scope, which is the
        // caller's scope when called from a script / interactive session).
        SessionState.PSVariable.Set(varName, result);
    }
}
