using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class ArrayTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = input;

        result = ArrayLength().Replace(result, ArrayLengthReplacer);
        result = ArrayExpandAll().Replace(result, ArrayExpandAllReplacer);
        result = ArrayElementAccess().Replace(result, ArrayElementAccessReplacer);
        result = ArrayAppend().Replace(result, ArrayAppendReplacer);
        result = ArrayAssignment().Replace(result, ArrayAssignmentReplacer);

        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    private static string ArrayLengthReplacer(Match m) =>
        $"${m.Groups[1].Value}.Count";

    private static string ArrayExpandAllReplacer(Match m) =>
        $"${m.Groups[1].Value}";

    private static string ArrayElementAccessReplacer(Match m) =>
        $"${m.Groups[1].Value}[{m.Groups[2].Value}]";

    private static string ArrayAppendReplacer(Match m)
    {
        var name = m.Groups[1].Value;
        var items = m.Groups[2].Value.Trim();
        return $"${name} += '{items}'";
    }

    private static string ArrayAssignmentReplacer(Match m)
    {
        var name = m.Groups[1].Value;
        var body = m.Groups[2].Value.Trim();
        var items = Regex.Split(body, @"\s+");
        var quoted = string.Join(",", items.Select(i => $"'{i}'"));
        return $"${name} = @({quoted})";
    }

    // Order matters: length before expand-all (both use [@])

    [GeneratedRegex(@"\$\{#(\w+)\[@\]\}")]
    private static partial Regex ArrayLength();

    [GeneratedRegex(@"\$\{(\w+)\[[@*]\]\}")]
    private static partial Regex ArrayExpandAll();

    [GeneratedRegex(@"\$\{(\w+)\[(\d+)\]\}")]
    private static partial Regex ArrayElementAccess();

    [GeneratedRegex(@"(\w+)\+=\(([^)]+)\)")]
    private static partial Regex ArrayAppend();

    [GeneratedRegex(@"(\w+)=\(([^)]+)\)")]
    private static partial Regex ArrayAssignment();
}
