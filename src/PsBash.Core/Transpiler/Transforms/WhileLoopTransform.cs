using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class WhileLoopTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = WhileReadLoop().Replace(input, ReplaceWhileRead);
        result = UntilLoop().Replace(result, ReplaceUntil);
        result = WhileLoop().Replace(result, ReplaceWhile);
        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    private static string ReplaceWhileRead(Match m)
    {
        var varName = m.Groups["var"].Value;
        var body = m.Groups["body"].Value.Trim();

        body = Regex.Replace(body, @$"\$env:{varName}(?!\w)", "$$_");

        return $"ForEach-Object {{ {body} }}";
    }

    private static string ReplaceWhile(Match m)
    {
        var condition = m.Groups["cond"].Value.Trim();
        var body = m.Groups["body"].Value.Trim();

        condition = WrapCondition(condition);

        return $"while {condition} {{ {body} }}";
    }

    private static string ReplaceUntil(Match m)
    {
        var condition = m.Groups["cond"].Value.Trim();
        var body = m.Groups["body"].Value.Trim();

        condition = WrapCondition(condition);

        return $"while (-not {condition}) {{ {body} }}";
    }

    private static string WrapCondition(string condition)
    {
        if (condition.StartsWith('(') && condition.EndsWith(')'))
            return condition;
        return $"({condition})";
    }

    [GeneratedRegex(@"(?<!\w)while\s+read\s+(?<var>\w+);\s*do\s+(?<body>.+?);\s*done")]
    private static partial Regex WhileReadLoop();

    [GeneratedRegex(@"(?<!\w)until\s+(?<cond>.+?);\s*do\s+(?<body>.+?);\s*done")]
    private static partial Regex UntilLoop();

    [GeneratedRegex(@"(?<!\w)while\s+(?<cond>.+?);\s*do\s+(?<body>.+?);\s*done")]
    private static partial Regex WhileLoop();
}
