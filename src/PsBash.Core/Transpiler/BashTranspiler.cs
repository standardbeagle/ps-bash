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
    /// In-process eval (e.g. <c>Invoke-BashEval</c> cmdlet): the host pwsh
    /// is the user's real session and MUST NOT have its builtins hijacked.
    /// Disables the <c>PsBuiltinAliases</c> short-circuit so every mapped
    /// command emits as an explicit <c>Invoke-Bash*</c> call.
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
            return PsEmitter.Transpile(bashCommand, context) ?? bashCommand;
        }
        catch (ParseException ex)
        {
            if (debug) LogParseFailure(bashCommand, ex);
            throw;
        }
    }

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

            return new TranspileResult(sb.ToString(), map);
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
}
