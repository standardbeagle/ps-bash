using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class CaseTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = CaseBlock().Replace(input, ReplaceCaseBlock);
        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    private static string ReplaceCaseBlock(Match m)
    {
        var expr = m.Groups["expr"].Value;
        var body = m.Groups["body"].Value.Trim();

        var cases = body.Split(";;", StringSplitOptions.RemoveEmptyEntries);
        var parts = new List<string>();

        foreach (var c in cases)
        {
            var trimmed = c.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            var parenIndex = trimmed.IndexOf(')');
            if (parenIndex < 0)
                continue;

            var pattern = trimmed[..parenIndex].Trim();
            var commands = trimmed[(parenIndex + 1)..].Trim();

            if (pattern == "*")
            {
                parts.Add($"default {{ {commands} }}");
            }
            else
            {
                parts.Add($"'{pattern}' {{ {commands} }}");
            }
        }

        return $"switch ({expr}) {{ {string.Join(' ', parts)} }}";
    }

    [GeneratedRegex(@"(?<!\w)case\s+(?<expr>\S+)\s+in\s+(?<body>.+?)\s*esac")]
    private static partial Regex CaseBlock();
}
