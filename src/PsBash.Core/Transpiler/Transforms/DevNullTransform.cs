using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class DevNullTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = StderrDevNull().Replace(input, "2>$null");
        result = StdoutDevNull().Replace(result, ">$null");
        result = DevNullLiteral().Replace(result, "$null");
        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    [GeneratedRegex(@"2>\s*/dev/null")]
    private static partial Regex StderrDevNull();

    [GeneratedRegex(@"(?<!2)>\s*/dev/null")]
    private static partial Regex StdoutDevNull();

    [GeneratedRegex(@"/dev/null")]
    private static partial Regex DevNullLiteral();
}
