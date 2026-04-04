using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class HomePathTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = HomePath().Replace(input, "$HOME\\");
        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    [GeneratedRegex(@"(?<![""'\w])~/")]
    private static partial Regex HomePath();
}
