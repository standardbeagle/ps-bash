using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class ChainOperatorTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = ChainedAssignment().Replace(input, "[void](${assign})${op}");
        result = ChainedParenExpr().Replace(result, "[void]${expr}${op}");
        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    [GeneratedRegex(@"(?<assign>\$env:[A-Za-z_][A-Za-z0-9_]*\s*=\s*""[^""]*"")(?<op>\s*(?:&&|\|\|))")]
    private static partial Regex ChainedAssignment();

    [GeneratedRegex(@"(?<!\[void\])(?<expr>\((?:[^()]*\([^()]*\))*[^()]*\))(?<op>\s*(?:&&|\|\|))")]
    private static partial Regex ChainedParenExpr();
}
