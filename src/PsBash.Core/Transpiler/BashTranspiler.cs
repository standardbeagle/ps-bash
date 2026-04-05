using PsBash.Core.Parser;
using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Transpiler;

public static class BashTranspiler
{
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
        var mode = Environment.GetEnvironmentVariable("PSBASH_PARSER") ?? "auto";

        if (mode == "v1")
            return TranspileRegex(bashCommand);
        if (mode == "v2")
            return PsEmitter.Transpile(bashCommand) ?? bashCommand;

        // auto mode: try parser first, fall back to regex on any failure
        try
        {
            var result = PsEmitter.Transpile(bashCommand);
            if (result is not null)
                return result;
        }
        catch
        {
            // Parser failed; fall through to regex pipeline
        }

        return TranspileRegex(bashCommand);
    }

    private static string TranspileRegex(string bashCommand)
    {
        var context = new TranspileContext(bashCommand);
        foreach (var transform in Pipeline)
            transform.Apply(ref context);
        return context.Result;
    }
}
