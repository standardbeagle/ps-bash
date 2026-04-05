using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class StderrRedirectTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;

        // Order matters: &>> must be replaced before &>
        var result = AppendBoth().Replace(input, "*>>");
        result = RedirectBothAmpFirst().Replace(result, "*>");
        result = RedirectBothGtFirst().Replace(result, "*>");

        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    // &>> → *>> (append both stdout+stderr)
    [GeneratedRegex(@"(?<!&)&>>")]
    private static partial Regex AppendBoth();

    // &> → *> (redirect both, ampersand-first form; not &&>)
    [GeneratedRegex(@"(?<!&)&>(?!>)")]
    private static partial Regex RedirectBothAmpFirst();

    // >& → *> (redirect both, gt-first form; not 2>&1 or similar fd merges)
    [GeneratedRegex(@"(?<!\d)>&(?!\d)")]
    private static partial Regex RedirectBothGtFirst();
}
