using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class ForLoopTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = ForInDoLoop().Replace(input, m => ReplaceForLoop(m));
        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    private static string ReplaceForLoop(Match m)
    {
        var varName = m.Groups["var"].Value;
        var list = m.Groups["list"].Value.Trim();
        var body = m.Groups["body"].Value.Trim();

        var psList = ConvertList(list);
        body = Regex.Replace(body, @$"\$env:{varName}(?!\w)", $"${varName}");

        return $"foreach (${varName} in {psList}) {{ {body} }}";
    }

    private static string ConvertList(string list)
    {
        var items = list.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (items.Length == 1)
        {
            var item = items[0];
            if (ContainsGlob(item))
                return $"(Resolve-Path {item})";
            return item;
        }

        if (items.Any(ContainsGlob))
            return $"(Resolve-Path {list})";

        var converted = items.Select(FormatItem);
        return string.Join(',', converted);
    }

    private static bool ContainsGlob(string item)
    {
        return item.Contains('*') || item.Contains('?');
    }

    private static string FormatItem(string item)
    {
        if (double.TryParse(item, out _))
            return item;
        return $"'{item}'";
    }

    [GeneratedRegex(@"(?<!\w)for\s+(?<var>\w+)\s+in\s+(?<list>.+?);\s*do\s+(?<body>.+?);\s*done")]
    private static partial Regex ForInDoLoop();
}
