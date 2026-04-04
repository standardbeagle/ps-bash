using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class IfElseTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = IfThen().Replace(input, "if (${cond}) {");
        result = ElifThen().Replace(result, " } elseif (${cond}) {");
        result = ElseKeyword().Replace(result, " } else {");
        result = FiKeyword().Replace(result, " }");
        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    [GeneratedRegex(@"(?<!\w)if\s+(?<cond>.+?);\s*then")]
    private static partial Regex IfThen();

    [GeneratedRegex(@";\s*elif\s+(?<cond>.+?);\s*then")]
    private static partial Regex ElifThen();

    [GeneratedRegex(@";\s*else\b")]
    private static partial Regex ElseKeyword();

    [GeneratedRegex(@";\s*fi\b")]
    private static partial Regex FiKeyword();
}
