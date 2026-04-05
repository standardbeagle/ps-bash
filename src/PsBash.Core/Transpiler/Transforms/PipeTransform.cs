using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class PipeTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = PipeGrep().Replace(input, PipeGrepReplacer);
        result = PipeHead().Replace(result, "| Invoke-BashHead -n ${n}");
        result = PipeTail().Replace(result, "| Invoke-BashTail -n ${n}");
        result = PipeWcL().Replace(result, "| Invoke-BashWc -l");
        result = PipeSort().Replace(result, PipeSortReplacer);
        result = PipeUniq().Replace(result, "| Invoke-BashUniq");
        result = PipeSed().Replace(result, "| Invoke-BashSed ${expr}");
        result = PipeAwk().Replace(result, PipeAwkReplacer);
        result = PipeCut().Replace(result, PipeCutReplacer);
        result = PipeXargs().Replace(result, PipeXargsReplacer);
        result = PipeTr().Replace(result, "| Invoke-BashTr ${args}");
        result = PipeTee().Replace(result, "| Invoke-BashTee ${file}");
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

        var parts = new List<string> { "| Invoke-BashGrep" };

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
            ? "| Invoke-BashSort -r"
            : "| Invoke-BashSort";

    private static string PipeAwkReplacer(Match m)
    {
        var flags = m.Groups["flags"].Value.Trim();
        var expr = m.Groups["expr"].Value;
        if (string.IsNullOrEmpty(flags))
            return $"| Invoke-BashAwk {expr}";
        return $"| Invoke-BashAwk \"{flags}\" {expr}";
    }

    private static string PipeCutReplacer(Match m) =>
        $"| Invoke-BashCut -Delimiter {m.Groups["delim"].Value} -Field {m.Groups["field"].Value}";

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

    [GeneratedRegex(@"\|\s*awk\s+(?<flags>-\S+\s+)?(?<expr>'[^']*'|""[^""]*"")")]
    private static partial Regex PipeAwk();

    [GeneratedRegex(@"\|\s*cut\s+-d(?<delim>\S)\s+-f(?<field>\d+)")]
    private static partial Regex PipeCut();

    private static string PipeXargsReplacer(Match m)
    {
        var xargsArgs = m.Groups["xargs_args"].Value.Trim();
        if (string.IsNullOrEmpty(xargsArgs))
            return "| Invoke-BashXargs";
        // Split -I{} into -I '{}' and quote standalone {} for PowerShell
        xargsArgs = Regex.Replace(xargsArgs, @"-I\{\}", "-I '{}'");
        xargsArgs = Regex.Replace(xargsArgs, @"(?<=\s)\{\}(?=\s|$)", "'{}'");
        return $"| Invoke-BashXargs {xargsArgs}";
    }

    [GeneratedRegex(@"\|\s*xargs(?<xargs_args>\s+[^|]+)?(?=\s*$|\s*\|)")]
    private static partial Regex PipeXargs();

    [GeneratedRegex(@"\|\s*tr\s+(?<args>'[^']*'\s+'[^']*'|""[^""]*""\s+""[^""]*"")")]
    private static partial Regex PipeTr();

    [GeneratedRegex(@"\|\s*tee\s+(?<file>\S+)")]
    private static partial Regex PipeTee();
}
