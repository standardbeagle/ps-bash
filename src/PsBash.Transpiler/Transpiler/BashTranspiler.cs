using PsBash.Core.Parser;

namespace PsBash.Core.Transpiler;

/// <summary>
/// A single mapping entry from a 1-based PowerShell output line back to the
/// 1-based bash source line and column where the corresponding statement
/// begins. Produced by <see cref="BashTranspiler.TranspileWithMap(string)"/>.
/// </summary>
public readonly record struct LineMapping(int PwshLine, int BashLine, int BashCol);

/// <summary>
/// The result of transpiling bash to PowerShell with a line map. The line
/// map allows runtime errors reported against <see cref="PowerShell"/> line
/// numbers to be rewritten back to the original bash source location.
/// </summary>
public readonly record struct TranspileResult(string PowerShell, IReadOnlyList<LineMapping> LineMap);

/// <summary>
/// Selects emitter behaviors that depend on the host that will execute the
/// generated PowerShell.
/// </summary>
public enum TranspileContext
{
    /// <summary>
    /// Default behavior: assume the PsBash module is imported in the host
    /// pwsh and its aliases (<c>ls</c>, <c>cat</c>, <c>echo</c>, ...) resolve
    /// to <c>Invoke-Bash*</c>. Mapped commands MAY short-circuit through the
    /// host's <c>PsBuiltinAliases</c> when used standalone.
    /// </summary>
    Default = 0,

    /// <summary>
    /// Runtime eval context: the generated PowerShell runs inside a live
    /// session and MUST NOT depend on aliases hijacking built-in PowerShell
    /// names. Disables the <c>PsBuiltinAliases</c> short-circuit so every
    /// mapped command emits as an explicit <c>Invoke-Bash*</c> call.
    /// </summary>
    Eval = 1,
}

/// <summary>
/// Transpiles bash commands to equivalent PowerShell.
/// This is the recommended entry point for library consumers.
/// </summary>
public static class BashTranspiler
{
    private static bool IsDebug =>
        Environment.GetEnvironmentVariable("PSBASH_DEBUG") == "1";

    /// <summary>
    /// Transpile a bash command string to PowerShell using the
    /// <see cref="TranspileContext.Default"/> context.
    /// </summary>
    /// <param name="bashCommand">The bash command to transpile.</param>
    /// <returns>The equivalent PowerShell command string.</returns>
    /// <exception cref="ParseException">Thrown when the bash input cannot be parsed.</exception>
    public static string Transpile(string bashCommand)
        => Transpile(bashCommand, TranspileContext.Default);

    /// <summary>
    /// Transpile a bash command string to PowerShell using the given context.
    /// </summary>
    /// <param name="bashCommand">The bash command to transpile.</param>
    /// <param name="context">Selects emitter behavior for the target host.</param>
    /// <returns>The equivalent PowerShell command string.</returns>
    /// <exception cref="ParseException">Thrown when the bash input cannot be parsed.</exception>
    public static string Transpile(string bashCommand, TranspileContext context)
    {
        bool debug = IsDebug;
        try
        {
            var emitted = PsEmitter.Transpile(bashCommand, context) ?? bashCommand;
            return WrapWithTrapEpilogue(bashCommand, emitted);
        }
        catch (ParseException ex)
        {
            if (debug) LogParseFailure(bashCommand, ex);
            throw;
        }
    }

    /// <summary>
    /// Marker the emitter writes into a script that reads standard input through the launcher-stdin scope
    /// (the variable the host fills with a <c>StdinCursor</c>).
    /// </summary>
    public const string LauncherStdinVariable = "__BashStdIn";

    /// <summary>
    /// Transpile <paramref name="bashCommand"/> so that its stdin is the LAUNCHER's own forwarded stdin:
    /// the whole script is one stdin scope (<c>$global:__BashStdIn</c>, installed by the host). Returns
    /// <c>null</c> when the script has no stdin reader at all — the launcher then needs no forwarding and
    /// should use <see cref="Transpile(string)"/>, whose text is unchanged.
    /// </summary>
    /// <exception cref="ParseException">Thrown when the bash input cannot be parsed.</exception>
    public static string? TranspileWithLauncherStdin(string bashCommand)
    {
        var emitted = PsEmitter.TranspileWithLauncherStdin(bashCommand, TranspileContext.Default);
        if (emitted is null || !ReadsSharedStdin(emitted))
            return null;
        return WrapWithTrapEpilogue(bashCommand, emitted);
    }

    /// <summary>
    /// True when emitted PowerShell can read the shared stdin: a command was fed from the cursor variable, or a
    /// builtin that takes its lines from it at RUN time (<c>read</c>, <c>mapfile</c>), or a native program given
    /// it as its process stdin (<c>Enter-BashNativeStdin</c>) is present.
    /// </summary>
    private static bool ReadsSharedStdin(string emitted) =>
        emitted.Contains(LauncherStdinVariable, StringComparison.Ordinal)
        || emitted.Contains("Invoke-BashRead", StringComparison.Ordinal)
        || emitted.Contains("Invoke-BashMapfile", StringComparison.Ordinal)
        || emitted.Contains("Enter-BashNativeStdin", StringComparison.Ordinal);

    /// <summary>
    /// Transpile a bash command string to PowerShell, also producing a line
    /// map from each emitted PowerShell line back to its originating bash
    /// source location. Uses the <see cref="TranspileContext.Default"/> context.
    /// </summary>
    public static TranspileResult TranspileWithMap(string bashCommand)
        => TranspileWithMap(bashCommand, TranspileContext.Default);

    /// <summary>
    /// Transpile a bash command string to PowerShell with a line map under
    /// the given <see cref="TranspileContext"/>.
    /// </summary>
    /// <remarks>
    /// Emits one PowerShell line per top-level bash statement, joined by
    /// newlines. Each map entry records the 1-based pwsh line number paired
    /// with the 1-based bash line and column of the statement's first token.
    /// Comments and blank lines do not produce mappings; they are implicitly
    /// covered by the next statement's mapping.
    /// </remarks>
    public static TranspileResult TranspileWithMap(string bashCommand, TranspileContext context)
    {
        bool debug = IsDebug;
        try
        {
            var statements = BashParser.ParseTopLevelWithPositions(bashCommand);
            if (statements.Count == 0)
                return new TranspileResult(string.Empty, Array.Empty<LineMapping>());

            var sb = new System.Text.StringBuilder();
            var map = new List<LineMapping>(statements.Count);
            int pwshLine = 1;

            for (int i = 0; i < statements.Count; i++)
            {
                var (cmd, position) = statements[i];
                var (bashLine, bashCol) = ParseException.ComputeLineCol(bashCommand, position);

                string emitted = PsEmitter.EmitWithContext(cmd, context);

                if (i > 0)
                {
                    sb.Append('\n');
                    pwshLine++;
                }
                sb.Append(emitted);

                map.Add(new LineMapping(pwshLine, bashLine, bashCol));
            }

            var body = sb.ToString();
            if (!ScriptHasExitOrErrTrap(bashCommand))
                return new TranspileResult(body, map);

            // The epilogue adds fixed prelude lines before the body, so every
            // statement's PowerShell line shifts by that many.
            int preludeLines = TrapEpiloguePreludeLineCount;
            var shifted = new List<LineMapping>(map.Count);
            foreach (var entry in map)
                shifted.Add(entry with { PwshLine = entry.PwshLine + preludeLines });

            return new TranspileResult(
                TrapEpilogueOpen + "\n" + body + "\n" + TrapEpilogueClose,
                shifted);
        }
        catch (ParseException ex)
        {
            if (debug) LogParseFailure(bashCommand, ex);
            throw;
        }
    }

    private static void LogParseFailure(string input, ParseException ex)
    {
        Console.Error.WriteLine($"[ps-bash] parser input:    {input}");
        Console.Error.WriteLine($"[ps-bash] parser error:    {ex.Message}");
        Console.Error.WriteLine($"[ps-bash] parser location: line {ex.Line}, col {ex.Column}");
        Console.Error.WriteLine($"[ps-bash] parser rule:     {ex.Rule}");
    }

    // ── EXIT/ERR trap epilogue ───────────────────────────────────────────────
    //
    // bash fires a `trap … EXIT` handler when the shell terminates, however it
    // terminates. The handler was only invoked by InvokeBashEvalCommand's own
    // try/finally, so a plain `-c` run or script file never fired it. Wrap any
    // script that registers an EXIT/ERR trap in a try/finally that fires the
    // handler; PowerShell runs a try/finally on normal end, on `exit N`, and on
    // an errexit terminating error, which is exactly bash's surface. Scripts
    // with no such trap keep the historical bare emission.
    //
    // TrapEpilogueOpen is ONE line so the line-map shift is a constant.

    private const string TrapEpilogueOpen =
        "try {";

    private const int TrapEpiloguePreludeLineCount = 1;

    private const string TrapEpilogueClose =
        "} finally { " +
        "try { if ((Test-Path Variable:Global:__BashTrapEXIT) -and $global:__BashTrapEXIT) { & $global:__BashTrapEXIT } } catch { } " +
        "}";

    /// <summary>
    /// True when <paramref name="bash"/> registers a trap on EXIT (or its
    /// synonyms <c>0</c> / <c>ERR</c> — the eval path fires both from the same
    /// epilogue). A <c>trap ACTION</c> with no signal defaults to EXIT. The scan
    /// is source-level and deliberately conservative: it only decides whether to
    /// add the wrapper, so a false positive is harmless (the handler fires only
    /// if the runtime variable is set) and a false negative is a plain untrapped
    /// script.
    /// </summary>
    internal static bool ScriptHasExitOrErrTrap(string bash)
    {
        if (string.IsNullOrEmpty(bash)) return false;
        // `trap 'body' EXIT` / `trap handler ERR` / `trap - 0` etc.
        if (System.Text.RegularExpressions.Regex.IsMatch(
                bash, @"\btrap\b[^\n;|&]*\b(EXIT|EXIT_SIGNAL|ERR|0)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true;
        // `trap 'body'` with no signal operand (defaults to EXIT).
        return System.Text.RegularExpressions.Regex.IsMatch(
            bash, @"\btrap\s+(['""])[^\n;|&]*\1\s*(;|\n|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Wraps <paramref name="emitted"/> in the EXIT/ERR trap epilogue when the
    /// source registers such a trap; otherwise returns it unchanged.
    /// </summary>
    private static string WrapWithTrapEpilogue(string bashSource, string emitted)
        => ScriptHasExitOrErrTrap(bashSource)
            ? TrapEpilogueOpen + "\n" + emitted + "\n" + TrapEpilogueClose
            : emitted;
}
