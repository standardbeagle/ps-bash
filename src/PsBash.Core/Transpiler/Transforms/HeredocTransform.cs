using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class HeredocTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = Heredoc().Replace(input, HeredocReplacer);
        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    private static string HeredocReplacer(Match m)
    {
        var command = m.Groups["cmd"].Value;
        var quoted = m.Groups["quote"].Value.Length > 0;
        var body = m.Groups["body"].Value;

        var open = quoted ? "@'" : "@\"";
        var close = quoted ? "'@" : "\"@";

        return $"{open}\n{body}\n{close} | {command}";
    }

    [GeneratedRegex(@"(?<cmd>\S+(?:\s+\S+)*?)\s*<<(?<quote>'?)(?<delim>\w+)\k<quote>\n(?<body>.*?)\n\k<delim>", RegexOptions.Singleline)]
    private static partial Regex Heredoc();
}
