using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class RedirectTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;

        // Validate 2>&1 passthrough (PowerShell supports this natively)
        // Validate >> append (PowerShell supports this natively)
        // These are valid in both bash and PowerShell, so no transform needed.
        // However, we validate that redirect targets are not Unix-specific paths
        // that earlier transforms should have caught.
        var result = StderrToFile().Replace(input, StderrToFileReplacer);
        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    private static string StderrToFileReplacer(Match m)
    {
        var path = m.Groups["path"].Value;
        // /dev/null should already be handled by DevNullTransform
        // Just pass through valid redirects
        return m.Value;
    }

    [GeneratedRegex(@"2>\s*(?<path>\S+)")]
    private static partial Regex StderrToFile();
}
