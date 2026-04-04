using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class InputRedirectTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = InputRedirect().Replace(input, InputRedirectReplacer);
        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    private static string InputRedirectReplacer(Match m)
    {
        var command = m.Groups["cmd"].Value;
        var file = m.Groups["file"].Value;
        return $"Get-Content {file} | {command}";
    }

    [GeneratedRegex(@"(?<cmd>.+?)\s+(?<!<)(?<!>)(?<!\d)<(?!<)\s+(?<file>""[^""]*""|'[^']*'|\S+)")]
    private static partial Regex InputRedirect();
}
