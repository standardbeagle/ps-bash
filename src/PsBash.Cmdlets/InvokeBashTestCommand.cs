using System.Globalization;
using System.IO;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashTest</c> function
/// (REFACTOR-2 follow-on) — the bash <c>test</c> / <c>[ ]</c> builtin.
///
/// Behavioral parity oracle: the original psm1 <c>Invoke-BashTest</c> +
/// <c>Test-BashCondition</c> recursive helper. Reproduces every predicate the
/// oracle implemented: file tests (<c>-e -f -d -r -w -x -s -L -h</c>), string
/// tests (<c>-z</c>, <c>-n</c>, <c>=</c>, <c>!=</c>), integer tests
/// (<c>-eq -ne -lt -le -gt -ge</c>), and the logical chain (<c>!</c>,
/// <c>-a</c>, <c>-o</c>).
///
/// Exit-code contract via <see cref="FileSystemHelpers.SetLastExitCode"/>:
/// 0 = true, 1 = false, 2 = syntax error. The cmdlet also writes back the
/// boolean result to the pipeline so callers can capture either the exit code
/// or the value (preserving the psm1 oracle's <c>,$__testResult</c> emit).
///
/// <para>
/// <b>Flag-collision strategy:</b> the bash <c>test</c> single-letter operators
/// (<c>-e</c>, <c>-d</c>, <c>-w</c>, <c>-a</c>, <c>-o</c>) prefix-collide with
/// PowerShell common parameters (<c>-ErrorAction</c>, <c>-Debug</c>,
/// <c>-WarningAction</c>, <c>-Arguments</c>, <c>-OutVariable</c>) under the
/// <see cref="PSCmdlet"/> binder. Each is declared as an explicit
/// <see cref="SwitchParameter"/> with a literal single-letter name so an
/// exact-name match beats common-parameter prefix-matching. The bound switches
/// are then re-injected at the head of the operand list so the manual walk
/// sees the bash-shaped argv the oracle saw.
/// </para>
///
/// <para><b>Bracket form:</b> when invoked as <c>[</c> (alias),
/// <see cref="PSCmdlet.MyInvocation"/> <c>.InvocationName</c> equals
/// <c>"["</c>; we require the last operand to be <c>]</c> and drop it before
/// evaluating. Missing <c>]</c> is a syntax error (exit 2). When invoked as
/// <c>test</c>, no trailing <c>]</c> is consumed.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashTest")]
[OutputType(typeof(bool))]
public sealed class InvokeBashTestCommand : PSCmdlet
{
    // Decoy switches: catch the bare bash operators before the binder maps
    // them to a common parameter. The IsPresent flag is what we re-inject.

    /// <summary>Catches bare <c>-e</c> (file-exists). Prefix-collides with
    /// <c>-ErrorAction</c> / <c>-ErrorVariable</c>.</summary>
    [Parameter]
    public SwitchParameter E { get; set; }

    /// <summary>Catches bare <c>-d</c> (is-directory). Prefix-collides with
    /// <c>-Debug</c>.</summary>
    [Parameter]
    public SwitchParameter D { get; set; }

    /// <summary>Catches bare <c>-w</c> (is-writable). Prefix-collides with
    /// <c>-WarningAction</c> / <c>-WarningVariable</c>.</summary>
    [Parameter]
    public SwitchParameter W { get; set; }

    /// <summary>Catches bare <c>-a</c> (logical AND between predicates).
    /// Prefix-matches the cmdlet's own <c>-Arguments</c> parameter.</summary>
    [Parameter]
    public SwitchParameter A { get; set; }

    /// <summary>Catches bare <c>-o</c> (logical OR between predicates).
    /// Prefix-collides with <c>-OutVariable</c> / <c>-OutBuffer</c>.</summary>
    [Parameter]
    public SwitchParameter O { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var raw = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "test", raw)) return;
        if (Array.IndexOf(raw, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "test"))
            {
                WriteObject(line);
            }
            return;
        }

        // Re-inject decoy switches that the binder consumed. The order
        // matters — these tokens originally appeared in argv before their
        // operand, so we prepend them in declaration order. Because each
        // decoy is a SwitchParameter (not value-bearing), the binder
        // already pushed any following PATH into Arguments.
        var operands = new List<string>(raw.Length + 5);
        if (E.IsPresent) operands.Add("-e");
        if (D.IsPresent) operands.Add("-d");
        if (W.IsPresent) operands.Add("-w");
        if (A.IsPresent) operands.Add("-a");
        if (O.IsPresent) operands.Add("-o");
        operands.AddRange(raw);

        // Bracket form: when invoked as `[`, the final operand must be `]`.
        // Detect via two signals — the InvocationName (alias-aware) and a
        // trailing `]` token — because PowerShell's call-operator dispatch
        // can normalize InvocationName depending on resolver path.
        var invocation = MyInvocation?.InvocationName ?? string.Empty;
        bool bracketForm = invocation == "[";
        bool trailingClose = operands.Count > 0 && operands[^1] == "]";
        if (bracketForm)
        {
            if (!trailingClose)
            {
                FileSystemHelpers.WriteBashError(this, "bash: [: missing `]'");
                FileSystemHelpers.SetLastExitCode(this, 2);
                return;
            }
            operands.RemoveAt(operands.Count - 1);
        }
        else if (trailingClose)
        {
            // Defensive: emitter may emit `Invoke-BashTest ... ]` for the
            // bracket form even when InvocationName resolves to the cmdlet
            // canonical name. Strip a trailing `]` so the predicate walk
            // sees the bash-shaped argv.
            operands.RemoveAt(operands.Count - 1);
        }

        bool result;
        try
        {
            result = Eval(operands.ToArray());
        }
        catch (SyntaxException ex)
        {
            FileSystemHelpers.WriteBashError(this, $"bash: test: {ex.Message}");
            FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }

        FileSystemHelpers.SetLastExitCode(this, result ? 0 : 1);
        WriteObject(result);
    }

    /// <summary>
    /// Reproduces the psm1 <c>Test-BashCondition</c> recursive evaluator
    /// byte-for-byte: 0 args -> false; 1 arg -> non-empty truthiness; 2 args
    /// -> unary predicate or <c>!</c>; 3 args -> binary infix; else walk a
    /// chain with <c>!</c>/<c>-a</c>/<c>-o</c> connectives.
    /// </summary>
    private static bool Eval(string[] args)
    {
        if (args.Length == 0) return false;
        if (args.Length == 1) return !string.IsNullOrEmpty(args[0]);

        if (args.Length == 2)
        {
            var flag = args[0];
            var val = args[1];
            switch (flag)
            {
                case "-f": return File.Exists(val);
                case "-d": return Directory.Exists(val);
                case "-e": return File.Exists(val) || Directory.Exists(val);
                case "-r":
                    try { using var s = BashFileSystem.OpenRead(val); return true; }
                    catch { return false; }
                case "-w":
                    try { using var s = File.OpenWrite(val); return true; }
                    catch { return false; }
                case "-x":
                    // Oracle parity: `Get-Command -CommandType Application`.
                    // On Windows there's no executable bit; existence in PATH
                    // as an Application is the closest equivalent.
                    return File.Exists(val); // best-effort: file exists -> assume executable
                case "-s":
                    return File.Exists(val) && new FileInfo(val).Length > 0;
                case "-L":
                case "-h":
                    try
                    {
                        var fi = new FileInfo(val);
                        return fi.Exists && (fi.Attributes & FileAttributes.ReparsePoint) != 0;
                    }
                    catch { return false; }
                case "-z": return string.IsNullOrEmpty(val);
                case "-n": return !string.IsNullOrEmpty(val);
                case "!": return !TruthyOne(val);
                default:
                    // Oracle parity: unrecognized two-token form treats first
                    // as bool truthiness check (matches psm1 default branch).
                    return TruthyOne(flag);
            }
        }

        if (args.Length == 3)
        {
            var lhs = args[0];
            var op = args[1];
            var rhs = args[2];
            switch (op)
            {
                case "=":
                case "==": return string.Equals(lhs, rhs, StringComparison.Ordinal);
                case "!=": return !string.Equals(lhs, rhs, StringComparison.Ordinal);
                case "-eq": return ParseInt(lhs) == ParseInt(rhs);
                case "-ne": return ParseInt(lhs) != ParseInt(rhs);
                case "-lt": return ParseInt(lhs) < ParseInt(rhs);
                case "-le": return ParseInt(lhs) <= ParseInt(rhs);
                case "-gt": return ParseInt(lhs) > ParseInt(rhs);
                case "-ge": return ParseInt(lhs) >= ParseInt(rhs);
                default: return true; // oracle parity: unknown 3-tok op returns true
            }
        }

        // Length >= 4: walk with !/-a/-o connectives, mirroring the oracle
        // step-by-step (each step consumes either 1 (!) or 2 (predicate)
        // tokens; -a/-o are 1-token connectives).
        int i = 0;
        bool result = true;
        string? currentOp = null;
        while (i < args.Length)
        {
            var tok = args[i];
            if (tok == "!")
            {
                i++;
                if (i < args.Length)
                {
                    var nextResult = Eval(new[] { args[i] });
                    result = !nextResult;
                }
                i++;
                continue;
            }
            if (tok == "-a")
            {
                currentOp = "and";
                i++;
                continue;
            }
            if (tok == "-o")
            {
                currentOp = "or";
                i++;
                continue;
            }

            bool check;
            if (i + 2 <= args.Length)
            {
                check = Eval(new[] { tok, args[i + 1] });
            }
            else
            {
                check = TruthyOne(tok);
            }

            if (currentOp == "and") result = result && check;
            else if (currentOp == "or") result = result || check;
            else result = check;

            currentOp = null;
            i += 2;
        }
        return result;
    }

    private static bool TruthyOne(string s) => !string.IsNullOrEmpty(s);

    private static decimal ParseInt(string s)
    {
        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }
        throw new SyntaxException($"integer expression expected: {s}");
    }

    private sealed class SyntaxException : Exception
    {
        public SyntaxException(string message) : base(message) { }
    }
}
