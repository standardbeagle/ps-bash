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
    {
        try { return Transpile(bashCommand, TranspileContext.Default); }
        catch (ParseException ex) when (RunnablePrefix(bashCommand, ex) is { } recovered)
        {
            return Transpile(recovered, TranspileContext.Default);
        }
    }

    /// <summary>
    /// bash reads and runs a script one complete command at a time, so a syntax error on a LATER line is only
    /// reported after everything above it ran (<c>echo a</c>, newline, <c>}</c> prints <c>a</c>, then fails with status 2).
    /// This returns the script rewritten to do that: the longest run of whole lines before the failing one that
    /// parses on its own, followed by a command that prints the error and exits 2. Null when no line precedes the
    /// error or no prefix parses, in which case the caller reports the error as before.
    /// Only the launcher's own script goes through here; <c>eval</c> and <c>source</c> text keep failing as a whole.
    /// </summary>
    public static string? RunnablePrefix(string bashCommand, ParseException ex)
    {
        if (ex.Line < 2) return null;
        var lines = bashCommand.Split('\n');
        int last = Math.Min(ex.Line - 1, lines.Length - 1);
        for (int k = last, tries = 0; k >= 1 && tries < 64; k--, tries++)
        {
            string prefix = string.Join('\n', lines, 0, k);
            if (string.IsNullOrWhiteSpace(prefix)) return null;
            try { if (BashParser.ParseTopLevelWithPositions(prefix).Count == 0) continue; }
            catch (ParseException) { continue; }
            string message = "ps-bash: parse error: " + ex.Message;
            return prefix + "\n{ printf '%s\\n' '" + message.Replace("'", "'\\''") + "' >&2; exit 2; }\n";
        }
        return null;
    }

    /// <summary>
    /// Transpile a bash command string to PowerShell using the given context.
    /// </summary>
    /// <param name="bashCommand">The bash command to transpile.</param>
    /// <param name="context">Selects emitter behavior for the target host.</param>
    /// <returns>The equivalent PowerShell command string.</returns>
    /// <exception cref="ParseException">Thrown when the bash input cannot be parsed.</exception>
    public static string Transpile(string bashCommand, TranspileContext context)
        => Guarded(() => TranspileCore(bashCommand, context));

    /// <summary>
    /// <see cref="Transpile(string, TranspileContext)"/> for the text of an <c>eval</c> run inside a function:
    /// <paramref name="evalLocalNames"/> are the names that function declared <c>local</c> (an array it assigns is
    /// otherwise the shell's own, not a copy local to the eval).
    /// </summary>
    public static string Transpile(string bashCommand, TranspileContext context, string[]? evalLocalNames)
        => Transpile(bashCommand, context, evalLocalNames, inStdinScope: false);

    /// <summary>
    /// <see cref="Transpile(string, TranspileContext, string[])"/> for an <c>eval</c> that runs while a stdin scope is
    /// active (<c>… | eval "$c"</c>, <c>eval "$c" &lt;&lt;&lt; text</c>, a <c>while read</c> body): with
    /// <paramref name="inStdinScope"/> the text's commands read that stdin (<c>$global:__BashStdIn</c>), as the
    /// commands of a compound pipe stage do.
    /// </summary>
    public static string Transpile(string bashCommand, TranspileContext context, string[]? evalLocalNames, bool inStdinScope)
        => Guarded(() =>
        {
            var prior = PsEmitter.EvalLocalNames;
            var priorScope = PsEmitter.InStdinScope;
            PsEmitter.EvalLocalNames = evalLocalNames is null ? null : new HashSet<string>(evalLocalNames, StringComparer.Ordinal);
            // Set here, inside Guarded: the scope flag is per-thread and Guarded may move to a large-stack worker.
            PsEmitter.InStdinScope = inStdinScope;
            try { return TranspileCore(bashCommand, context); }
            finally { PsEmitter.EvalLocalNames = prior; PsEmitter.InStdinScope = priorScope; }
        });

    /// <summary>
    /// Every public entry runs here. The lexer/parser/emitter recursion is bounded by
    /// <see cref="NestingGuard"/>, so no input can overflow the stack (the transpiler runs inside the
    /// shared host for eval/source, where an overflow killed the daemon). Inline on the caller's thread;
    /// if that thread's stack is too small for an input the depth cap still accepts, retried once on a
    /// large-stack worker, carrying the emitter's thread-affine test seam across the hop.
    /// </summary>
    private static T Guarded<T>(Func<T> work)
    {
        var fusion = PsEmitter.FusionEnabledOverride;
        var inheritedPipefail = PsEmitter.InheritedPipefail;
        var inheritedNounset = PsEmitter.InheritedNounset;
        return NestingGuard.WithStackFallback(() =>
        {
            var prior = PsEmitter.FusionEnabledOverride;
            PsEmitter.FusionEnabledOverride = fusion;
            PsEmitter.InheritedPipefail = inheritedPipefail;
            PsEmitter.InheritedNounset = inheritedNounset;
            try { return work(); }
            finally { PsEmitter.FusionEnabledOverride = prior; }
        });
    }

    private static string TranspileCore(string bashCommand, TranspileContext context)
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
    public static string? TranspileWithLauncherStdin(string bashCommand) => Guarded(() =>
    {
        string? emitted;
        try { emitted = PsEmitter.TranspileWithLauncherStdin(bashCommand, TranspileContext.Default); }
        catch (ParseException ex) when (RunnablePrefix(bashCommand, ex) is { } recovered)
        {
            bashCommand = recovered;
            emitted = PsEmitter.TranspileWithLauncherStdin(bashCommand, TranspileContext.Default);
        }
        if (emitted is null || !ReadsSharedStdin(emitted))
            return null;
        return WrapWithTrapEpilogue(bashCommand, emitted);
    });

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
        => Guarded(() => TranspileWithMapCore(bashCommand, context));

    private static TranspileResult TranspileWithMapCore(string bashCommand, TranspileContext context)
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

    // The handler's own status never becomes the script's (bash ignores an EXIT trap's return value), and a bare
    // `exit` inside it uses the status from BEFORE the handler ran: record it in __BashPreTrapStatus (read by the
    // emitted bare `exit`), and put it back when the handler finishes.
    private const string TrapEpilogueClose =
        "} finally { " +
        "$__psbash_xs = $global:LASTEXITCODE; $global:__BashPreTrapStatus = $__psbash_xs; " +
        "try { if ((Test-Path Variable:Global:__BashTrapEXIT) -and $global:__BashTrapEXIT) { & $global:__BashTrapEXIT } } catch { }; " +
        "$global:__BashPreTrapStatus = $null; $global:LASTEXITCODE = $__psbash_xs " +
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
                bash, @"\btrap\s+(?:'[^']*'|""[^""]*""|[^\s'""]+)[^\n;|&]*\b(EXIT|EXIT_SIGNAL|ERR|0)\b",
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
