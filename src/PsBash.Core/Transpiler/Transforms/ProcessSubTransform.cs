using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class ProcessSubTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = ProcessSubstitution().Replace(input, ProcessSubReplacer);
        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    private static string ProcessSubReplacer(Match m) =>
        $"(Invoke-ProcessSub {{ {m.Groups["cmd"].Value} }})";

    [GeneratedRegex(@"(?<![<$])<\((?<cmd>[^)]+)\)")]
    private static partial Regex ProcessSubstitution();
}
