using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class ExportTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;

        // Handle export VAR=val first
        var result = ExportQuoted().Replace(input, "$env:${name} = \"${val}\"");
        result = ExportUnquoted().Replace(result, AssignReplacer);

        // Handle bare VAR="val" and VAR=val at statement boundaries
        result = BareQuoted().Replace(result, m => InsideSingleQuotes(result, m) ? m.Value : AssignReplacer(m));
        result = BareUnquoted().Replace(result, m => InsideSingleQuotes(result, m) ? m.Value : AssignReplacer(m));

        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    private static bool InsideSingleQuotes(string text, Match m)
    {
        var count = 0;
        for (var i = 0; i < m.Index; i++)
        {
            if (text[i] == '\'')
                count++;
        }
        return count % 2 != 0;
    }

    private static string AssignReplacer(Match m) =>
        $"$env:{m.Groups["name"].Value} = \"{m.Groups["val"].Value}\"";

    [GeneratedRegex(@"export\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)=""(?<val>[^""]*)""")]
    private static partial Regex ExportQuoted();

    [GeneratedRegex(@"export\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)=(?<val>\S+)")]
    private static partial Regex ExportUnquoted();

    [GeneratedRegex(@"(?:^|(?<=;\s{0,9})|(?<=&&\s{0,9})|(?<=\|\|\s{0,9}))(?<name>[A-Za-z_][A-Za-z0-9_]*)=""(?<val>[^""]*)""")]
    private static partial Regex BareQuoted();

    [GeneratedRegex(@"(?:^|(?<=;\s{0,9})|(?<=&&\s{0,9})|(?<=\|\|\s{0,9}))(?<name>[A-Za-z_][A-Za-z0-9_]*)=(?![=(])(?<val>[^\s;]+)")]
    private static partial Regex BareUnquoted();
}
