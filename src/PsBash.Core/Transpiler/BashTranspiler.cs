using PsBash.Core.Parser;

namespace PsBash.Core.Transpiler;

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

    private static void LogParseFailure(string input, ParseException ex)
    {
        Console.Error.WriteLine($"[ps-bash] parser input:    {input}");
        Console.Error.WriteLine($"[ps-bash] parser error:    {ex.Message}");
        Console.Error.WriteLine($"[ps-bash] parser location: line {ex.Line}, col {ex.Column}");
        Console.Error.WriteLine($"[ps-bash] parser rule:     {ex.Rule}");
    }
}
