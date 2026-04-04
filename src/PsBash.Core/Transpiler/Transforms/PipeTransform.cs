using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class PipeTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = PipeGrep().Replace(input, PipeGrepReplacer);
        result = PipeHead().Replace(result, "| Select-Object -First ${n}");
        result = PipeTail().Replace(result, "| Select-Object -Last ${n}");
        result = PipeWcL().Replace(result, "| Measure-Object -Line | Select-Object -Expand Lines");
        result = PipeSort().Replace(result, PipeSortReplacer);
        result = PipeUniq().Replace(result, "| Get-Unique");
        result = PipeSed().Replace(result, "| Invoke-Sed ${expr}");
        result = PipeAwk().Replace(result, "| Invoke-Awk ${expr}");
        result = PipeCut().Replace(result, PipeCutReplacer);
        result = PipeXargs().Replace(result, "| Invoke-Xargs");
        result = PipeTr().Replace(result, "| Invoke-Tr ${args}");
        result = PipeTee().Replace(result, "| Tee-Object ${file}");
        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    private static string PipeGrepReplacer(Match m)
    {
        var flags = m.Groups["flags"].Value.Trim();
        var pattern = m.Groups["pat"].Value;
        var rest = m.Groups["rest"].Value.Trim();

        var parts = new List<string> { "| Invoke-Grep" };

        if (flags.Contains('v'))
            parts.Add("-NotMatch");
        if (flags.Contains('i'))
            parts.Add("-CaseInsensitive");
        if (flags.Contains('r'))
            parts.Add("-Recurse");

        parts.Add($"\"{pattern}\"");

        if (!string.IsNullOrEmpty(rest))
            parts.Add(rest);

        return string.Join(' ', parts);
    }

    private static string PipeSortReplacer(Match m) =>
        m.Groups["rev"].Value == "-r"
            ? "| Sort-Object -Descending"
            : "| Sort-Object";

    private static string PipeCutReplacer(Match m) =>
        $"| Invoke-Cut -Delimiter {m.Groups["delim"].Value} -Field {m.Groups["field"].Value}";

    [GeneratedRegex(@"\|\s*grep\s+(?<flags>-[a-zA-Z]+\s+)?""?(?<pat>[^""\s|]+)""?(?:\s+(?<rest>[^|]+))?")]
    private static partial Regex PipeGrep();

    [GeneratedRegex(@"\|\s*head\s+-n\s*(?<n>\d+)")]
    private static partial Regex PipeHead();

    [GeneratedRegex(@"\|\s*tail\s+-n\s*(?<n>\d+)")]
    private static partial Regex PipeTail();

    [GeneratedRegex(@"\|\s*wc\s+-l")]
    private static partial Regex PipeWcL();

    [GeneratedRegex(@"\|\s*sort(?:\s+(?<rev>-r))?(?=\s*$|\s*\|)")]
    private static partial Regex PipeSort();

    [GeneratedRegex(@"\|\s*uniq(?=\s*$|\s*\|)")]
    private static partial Regex PipeUniq();

    [GeneratedRegex(@"\|\s*sed\s+(?<expr>'[^']*'|""[^""]*"")")]
    private static partial Regex PipeSed();

    [GeneratedRegex(@"\|\s*awk\s+(?<expr>'[^']*'|""[^""]*"")")]
    private static partial Regex PipeAwk();

    [GeneratedRegex(@"\|\s*cut\s+-d(?<delim>\S)\s+-f(?<field>\d+)")]
    private static partial Regex PipeCut();

    [GeneratedRegex(@"\|\s*xargs(?=\s*$|\s*\|)")]
    private static partial Regex PipeXargs();

    [GeneratedRegex(@"\|\s*tr\s+(?<args>'[^']*'\s+'[^']*'|""[^""]*""\s+""[^""]*"")")]
    private static partial Regex PipeTr();

    [GeneratedRegex(@"\|\s*tee\s+(?<file>\S+)")]
    private static partial Regex PipeTee();
}
