namespace PsBash.Core.Transpiler.Transforms;

public sealed class TmpPathTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        if (!input.Contains("/tmp/"))
            return;

        context.Result = input.Replace("/tmp/", "$env:TEMP\\");
        context.Modified = true;
    }
}
