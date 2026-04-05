using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class ArithmeticTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = ArithmeticExpansion().Replace(input, ArithmeticReplacer);
        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    private static string ArithmeticReplacer(Match m)
    {
        var inner = m.Groups["expr"].Value;
        var converted = BareVariable().Replace(inner, BareVariableReplacer);
        return $"$([int]({converted}))";
    }

    private static string BareVariableReplacer(Match m) =>
        $"$env:{m.Groups["name"].Value}";

    [GeneratedRegex(@"\$\(\((?<expr>.+?)\)\)")]
    private static partial Regex ArithmeticExpansion();

    [GeneratedRegex(@"\b(?<name>[a-zA-Z_][a-zA-Z0-9_]*)\b")]
    private static partial Regex BareVariable();
}
