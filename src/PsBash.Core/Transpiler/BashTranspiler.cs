using PsBash.Core.Parser;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Transpiler;

public static class BashTranspiler
{
    private static bool IsDebug =>
        Environment.GetEnvironmentVariable("PSBASH_DEBUG") == "1";
    private static readonly ITransform[] Pipeline =
    [
        new BacktickTransform(),
        new ProcessSubTransform(),
        new BraceExpansionTransform(),
        new SourceTransform(),
        new HeredocTransform(),
        new DevNullTransform(),
        new TmpPathTransform(),
        new HomePathTransform(),
        new ExportTransform(),
        new ExtendedTestTransform(),
        new FileTestTransform(),
        new HereStringTransform(),
        new InputRedirectTransform(),
        new PipeTransform(),
        new ArrayTransform(),
        new ArithmeticTransform(),
        new ParameterExpansionTransform(),
        new EnvVarTransform(),
        new ForLoopTransform(),
        new WhileLoopTransform(),
        new CaseTransform(),
        new IfElseTransform(),
        new StderrRedirectTransform(),
        new RedirectTransform(),
        new ChainOperatorTransform(),
    ];

    public static string Transpile(string bashCommand)
    {
        var mode = Environment.GetEnvironmentVariable("PSBASH_PARSER");
        bool debug = IsDebug;

        // v1 regex is the default — parser-v2 is opt-in until feature parity
        // Set PSBASH_PARSER=v2 for parser-only, PSBASH_PARSER=auto for try-parser-then-regex
        if (mode == "v2")
        {
            try
            {
                return PsEmitter.Transpile(bashCommand) ?? bashCommand;
            }
            catch (ParseException ex)
            {
                if (debug) LogParseFailure(bashCommand, ex, fallbackToRegex: false);
                throw;
            }
        }

        if (mode == "auto")
        {
            try
            {
                var result = PsEmitter.Transpile(bashCommand);
                if (result is not null)
                    return result;
            }
            catch (ParseException ex)
            {
                if (debug) LogParseFailure(bashCommand, ex, fallbackToRegex: true);
                // Parser failed; fall through to regex pipeline
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (debug)
                    Console.Error.WriteLine($"[ps-bash] parser-v2 threw {ex.GetType().Name}: {ex.Message}; falling back to regex");
            }
        }

        return TranspileRegex(bashCommand);
    }

    private static void LogParseFailure(string input, ParseException ex, bool fallbackToRegex)
    {
        Console.Error.WriteLine($"[ps-bash] parser-v2 input:    {input}");
        Console.Error.WriteLine($"[ps-bash] parser-v2 error:    {ex.Message}");
        Console.Error.WriteLine($"[ps-bash] parser-v2 location: line {ex.Line}, col {ex.Column}");
        Console.Error.WriteLine($"[ps-bash] parser-v2 rule:     {ex.Rule}");
        Console.Error.WriteLine($"[ps-bash] parser-v2 fallback: {(fallbackToRegex ? "regex" : "none")}");
    }

    private static string TranspileRegex(string bashCommand)
    {
        var context = new TranspileContext(bashCommand);
        foreach (var transform in Pipeline)
            transform.Apply(ref context);
        return context.Result;
    }
}
