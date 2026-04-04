using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class ExportTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = ExportQuoted().Replace(input, "$env:${name} = \"${val}\"");
        result = ExportUnquoted().Replace(result, ExportUnquotedReplacer);
        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    private static string ExportUnquotedReplacer(Match m) =>
        $"$env:{m.Groups["name"].Value} = \"{m.Groups["val"].Value}\"";

    [GeneratedRegex(@"export\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)=""(?<val>[^""]*)""")]
    private static partial Regex ExportQuoted();

    [GeneratedRegex(@"export\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)=(?<val>\S+)")]
    private static partial Regex ExportUnquoted();
}
