using PsBash.Core.Transpiler.Transforms;

namespace PsBash.Core.Transpiler;

public static class BashTranspiler
{
    private static readonly ITransform[] Pipeline =
    [
        new DevNullTransform(),
        new TmpPathTransform(),
        new HomePathTransform(),
        new ExportTransform(),
        new FileTestTransform(),
        new PipeTransform(),
        new EnvVarTransform(),
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
