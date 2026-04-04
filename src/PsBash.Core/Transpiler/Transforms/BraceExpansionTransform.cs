using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class BraceExpansionTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = CommaExpansion().Replace(input, CommaReplacer);
        result = NumericRange().Replace(result, "($1..$2)");
        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    private static string CommaReplacer(Match m)
    {
        var prefix = m.Groups["prefix"].Value;
        var items = m.Groups["items"].Value;
        var suffix = m.Groups["suffix"].Value;
        var parts = items.Split(',');
        return string.Join(" ", parts.Select(p => $"{prefix}{p}{suffix}"));
    }

    [GeneratedRegex(@"(?<prefix>\S*)\{(?<items>[^{},]+(?:,[^{},]+)+)\}(?<suffix>\S*)")]
    private static partial Regex CommaExpansion();

    [GeneratedRegex(@"\{(\d+)\.\.(\d+)\}")]
    private static partial Regex NumericRange();
}
