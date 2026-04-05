using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class ExtendedTestTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = ExtendedTest().Replace(input, ExtendedTestReplacer);
        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    private static string ExtendedTestReplacer(Match m)
    {
        var inner = m.Groups["inner"].Value.Trim();
        var parts = SplitLogical(inner);

        if (parts.Count == 1)
            return $"({TranslateCondition(parts[0].Expr)})";

        var translated = new List<string>();
        foreach (var part in parts)
        {
            var cond = TranslateCondition(part.Expr);
            if (part.Op is not null)
                translated.Add(cond + " " + part.Op);
            else
                translated.Add(cond);
        }

        return "(" + string.Join(" ", translated) + ")";
    }

    private static string TranslateCondition(string expr)
    {
        expr = expr.Trim();

        var fileTest = FileTestOp().Match(expr);
        if (fileTest.Success)
        {
            var flag = fileTest.Groups["flag"].Value;
            var path = fileTest.Groups["path"].Value.Trim().Trim('"');
            return flag switch
            {
                "f" => $"Test-Path \"{path}\" -PathType Leaf",
                "d" => $"Test-Path \"{path}\" -PathType Container",
                _ => expr,
            };
        }

        var stringTest = StringTestOp().Match(expr);
        if (stringTest.Success)
        {
            var flag = stringTest.Groups["flag"].Value;
            var val = stringTest.Groups["val"].Value.Trim().Trim('"');
            return flag switch
            {
                "z" => $"[string]::IsNullOrEmpty({val})",
                "n" => $"-not [string]::IsNullOrEmpty({val})",
                _ => expr,
            };
        }

        var regexMatch = RegexOp().Match(expr);
        if (regexMatch.Success)
        {
            var lhs = regexMatch.Groups["lhs"].Value.Trim();
            var pattern = regexMatch.Groups["pattern"].Value.Trim();
            return $"{lhs} -match '{pattern}'";
        }

        var comparison = ComparisonOp().Match(expr);
        if (comparison.Success)
        {
            var lhs = comparison.Groups["lhs"].Value.Trim();
            var op = comparison.Groups["op"].Value;
            var rhs = comparison.Groups["rhs"].Value.Trim();

            if (op is "==" or "=")
            {
                var unquoted = rhs.Trim('"');
                if (HasGlobChars(unquoted))
                    return $"{lhs} -like '{unquoted}'";
                return $"{lhs} -eq {rhs}";
            }

            var psOp = op switch
            {
                "!=" => "-ne",
                "<" => "-lt",
                ">" => "-gt",
                _ => op,
            };
            return $"{lhs} {psOp} {rhs}";
        }

        return expr;
    }

    private static bool HasGlobChars(string value)
    {
        return value.Contains('*') || value.Contains('?') || value.Contains('[');
    }

    private static List<LogicalPart> SplitLogical(string expr)
    {
        var parts = new List<LogicalPart>();
        var logicalOps = LogicalOp().Matches(expr);

        if (logicalOps.Count == 0)
        {
            parts.Add(new LogicalPart(expr, null));
            return parts;
        }

        var lastEnd = 0;
        foreach (Match m in logicalOps)
        {
            var condExpr = expr[lastEnd..m.Index].Trim();
            var op = m.Groups["op"].Value == "&&" ? "-and" : "-or";
            parts.Add(new LogicalPart(condExpr, op));
            lastEnd = m.Index + m.Length;
        }

        var remaining = expr[lastEnd..].Trim();
        if (remaining.Length > 0)
            parts.Add(new LogicalPart(remaining, null));

        return parts;
    }

    private readonly record struct LogicalPart(string Expr, string? Op);

    [GeneratedRegex(@"\[\[\s+(?<inner>.+?)\s+\]\]")]
    private static partial Regex ExtendedTest();

    [GeneratedRegex(@"^-(?<flag>[fd])\s+(?<path>.+)$")]
    private static partial Regex FileTestOp();

    [GeneratedRegex(@"^-(?<flag>[zn])\s+(?<val>.+)$")]
    private static partial Regex StringTestOp();

    [GeneratedRegex(@"^(?<lhs>.+?)\s+=~\s+(?<pattern>.+)$")]
    private static partial Regex RegexOp();

    [GeneratedRegex(@"^(?<lhs>.+?)\s+(?<op>==|!=|=|<|>)\s+(?<rhs>.+)$")]
    private static partial Regex ComparisonOp();

    [GeneratedRegex(@"\s+(?<op>&&|\|\|)\s+")]
    private static partial Regex LogicalOp();
}
