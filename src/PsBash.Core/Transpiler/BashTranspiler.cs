using PsBash.Core.Parser;

namespace PsBash.Core.Transpiler;

/// <summary>
/// Transpiles bash commands to equivalent PowerShell.
/// This is the recommended entry point for library consumers.
/// </summary>
public static class BashTranspiler
{
    private static bool IsDebug =>
        Environment.GetEnvironmentVariable("PSBASH_DEBUG") == "1";

    /// <summary>
    /// Transpile a bash command string to PowerShell.
    /// </summary>
    /// <param name="bashCommand">The bash command to transpile.</param>
    /// <returns>The equivalent PowerShell command string.</returns>
    /// <exception cref="ParseException">Thrown when the bash input cannot be parsed.</exception>
    public static string Transpile(string bashCommand)
    {
        bool debug = IsDebug;
        try
        {
            return PsEmitter.Transpile(bashCommand) ?? bashCommand;
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
