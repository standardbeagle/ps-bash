using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class FileTestTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = TestFile().Replace(input, "(Test-Path \"${path}\" -PathType Leaf)");
        result = TestDir().Replace(result, "(Test-Path \"${path}\" -PathType Container)");
        result = TestEmpty().Replace(result, "([string]::IsNullOrEmpty(${val}))");
        result = TestNonEmpty().Replace(result, "(-not [string]::IsNullOrEmpty(${val}))");
        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    [GeneratedRegex(@"\[\s*-f\s+(?<path>[^\]]+?)\s*\]")]
    private static partial Regex TestFile();

    [GeneratedRegex(@"\[\s*-d\s+(?<path>[^\]]+?)\s*\]")]
    private static partial Regex TestDir();

    [GeneratedRegex(@"\[\s*-z\s+""?(?<val>[^""\]]+?)""?\s*\]")]
    private static partial Regex TestEmpty();

    [GeneratedRegex(@"\[\s*-n\s+""?(?<val>[^""\]]+?)""?\s*\]")]
    private static partial Regex TestNonEmpty();
}
