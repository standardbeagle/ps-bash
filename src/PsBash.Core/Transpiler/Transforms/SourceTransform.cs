using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class SourceTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = Source().Replace(input, ". ${file}");
        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    [GeneratedRegex(@"(?<!\w)source\s+(?<file>\S+)")]
    private static partial Regex Source();
}
