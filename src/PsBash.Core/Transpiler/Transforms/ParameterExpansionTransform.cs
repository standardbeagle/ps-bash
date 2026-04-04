using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class ParameterExpansionTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = input;

        result = DefaultValue().Replace(result, DefaultValueReplacer);
        result = StringLength().Replace(result, StringLengthReplacer);
        result = ReplaceAll().Replace(result, ReplaceAllReplacer);
        result = ReplaceFirst().Replace(result, ReplaceFirstReplacer);
        result = RemoveLongestPrefix().Replace(result, RemoveLongestPrefixReplacer);
        result = RemoveShortestPrefix().Replace(result, RemoveShortestPrefixReplacer);
        result = RemoveLongestSuffix().Replace(result, RemoveLongestSuffixReplacer);
        result = RemoveShortestSuffix().Replace(result, RemoveShortestSuffixReplacer);
        result = UppercaseAll().Replace(result, UppercaseAllReplacer);
        result = LowercaseAll().Replace(result, LowercaseAllReplacer);

        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    private static string DefaultValueReplacer(Match m) =>
        $"$($env:{m.Groups[1].Value} ?? '{m.Groups[2].Value}')";

    private static string StringLengthReplacer(Match m) =>
        $"$(($env:{m.Groups[1].Value}).Length)";

    private static string ReplaceAllReplacer(Match m) =>
        $"$($env:{m.Groups[1].Value} -replace '{m.Groups[2].Value}','{m.Groups[3].Value}')";

    private static string ReplaceFirstReplacer(Match m) =>
        $"$($env:{m.Groups[1].Value} -replace '{m.Groups[2].Value}','{m.Groups[3].Value}')";

    private static string RemoveLongestPrefixReplacer(Match m) =>
        $"$($env:{m.Groups[1].Value} -replace '^{m.Groups[2].Value}','')";

    private static string RemoveShortestPrefixReplacer(Match m) =>
        $"$($env:{m.Groups[1].Value} -replace '^{m.Groups[2].Value}','')";

    private static string RemoveLongestSuffixReplacer(Match m) =>
        $"$($env:{m.Groups[1].Value} -replace '{m.Groups[2].Value}$','')";

    private static string RemoveShortestSuffixReplacer(Match m) =>
        $"$($env:{m.Groups[1].Value} -replace '{m.Groups[2].Value}$','')";

    private static string UppercaseAllReplacer(Match m) =>
        $"$(($env:{m.Groups[1].Value}).ToUpper())";

    private static string LowercaseAllReplacer(Match m) =>
        $"$(($env:{m.Groups[1].Value}).ToLower())";

    // Order matters: longer operators must be matched before shorter ones

    [GeneratedRegex(@"\$\{(\w+):-([^}]*)\}")]
    private static partial Regex DefaultValue();

    [GeneratedRegex(@"\$\{#(\w+)\}")]
    private static partial Regex StringLength();

    [GeneratedRegex(@"\$\{(\w+)//([^/}]+)/([^}]*)\}")]
    private static partial Regex ReplaceAll();

    [GeneratedRegex(@"\$\{(\w+)/([^/}]+)/([^}]*)\}")]
    private static partial Regex ReplaceFirst();

    [GeneratedRegex(@"\$\{(\w+)##([^}]+)\}")]
    private static partial Regex RemoveLongestPrefix();

    [GeneratedRegex(@"\$\{(\w+)#([^}]+)\}")]
    private static partial Regex RemoveShortestPrefix();

    [GeneratedRegex(@"\$\{(\w+)%%([^}]+)\}")]
    private static partial Regex RemoveLongestSuffix();

    [GeneratedRegex(@"\$\{(\w+)%([^}]+)\}")]
    private static partial Regex RemoveShortestSuffix();

    [GeneratedRegex(@"\$\{(\w+)\^\^\}")]
    private static partial Regex UppercaseAll();

    [GeneratedRegex(@"\$\{(\w+),,\}")]
    private static partial Regex LowercaseAll();
}
