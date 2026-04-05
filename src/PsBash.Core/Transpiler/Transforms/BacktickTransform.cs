using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class BacktickTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = BacktickSubstitution().Replace(input, BacktickReplacer);
        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    private static string BacktickReplacer(Match m) =>
        $"$({m.Groups["content"].Value})";

    [GeneratedRegex(@"(?<!\\)`(?<content>[^`]+)`")]
    private static partial Regex BacktickSubstitution();
}
