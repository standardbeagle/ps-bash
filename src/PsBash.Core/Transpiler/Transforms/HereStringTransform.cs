using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class HereStringTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = HereString().Replace(input, HereStringReplacer);
        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    private static string HereStringReplacer(Match m)
    {
        var command = m.Groups["cmd"].Value;
        var value = m.Groups["val"].Value;
        return $"{value} | {command}";
    }

    [GeneratedRegex(@"(?<cmd>\S+(?:\s+(?!<<<)\S+)*)\s+<<<\s+(?<val>'[^']*'|""[^""]*""|\S+)")]
    private static partial Regex HereString();
}
