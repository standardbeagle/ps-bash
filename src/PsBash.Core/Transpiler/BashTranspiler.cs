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
        var context = new TranspileContext(bashCommand);
        foreach (var transform in Pipeline)
            transform.Apply(ref context);
        return context.Result;
    }
}
