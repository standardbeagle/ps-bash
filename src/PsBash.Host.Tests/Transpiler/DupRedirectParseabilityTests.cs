using System.Management.Automation.Language;
using PsBash.Core.Parser;
using Xunit;
using PsParser = System.Management.Automation.Language.Parser;

namespace PsBash.Host.Tests.Transpiler;

/// <summary>
/// A descriptor duplication or close (<c>&lt;&amp;N</c>, <c>N&gt;&amp;M</c>, <c>&lt;&amp;-</c>, <c>&gt;&amp;$fd</c>) is a bash
/// redirect, never PowerShell syntax: whatever the command, its prefix (<c>X=1</c>, <c>command</c>, <c>builtin</c>) and
/// where it runs (a pipe stage, a stdin scope, a function, a command substitution), the emitted PowerShell parses and holds
/// no background pipeline. PowerShell's only <c>&amp;</c> after a redirect operator is its <c>2&gt;&amp;1</c> merge; any
/// other <c>N&lt;&amp;M</c> / <c>N&gt;&amp;M</c> left raw ends in its background operator.
/// <para>REGRESSION: <c>echo q | { X=1 cat &lt;&amp;0; }</c> emitted <c>Invoke-BashCat 0&lt;&amp;0</c>, which PowerShell ran
/// as a job ("The pwsh executable cannot be found ... Start-Job"). Every duplication is built by
/// <c>PsEmitter.EmitDupRedirect</c>.</para>
/// Oracle note (qa-rubric Directive 1): the oracle is PowerShell's parser (a seam property with no bash equivalent);
/// behavior: Differential <c>ShellLanguageGapDifferentialTests.DupRedirects_AreRedirectsInEveryContext</c>.
/// </summary>
public class DupRedirectParseabilityTests
{
    private static readonly string[] Redirects =
    {
        "<&0", "0<&0", "<&1", "<&2", "<&3", "1<&0", "2<&1", "3<&0", "3<&4", "<&-", "0<&-", "5<&-",
        ">&1", ">&2", "1>&2", "2>&1", "2>&2", "1>&1", "0>&1", "2>&-", ">&-", "3>&1", "4>&3", "3>&-", "2>&3",
        "<&$fd", ">&$fd", "2>&$fd", "3>&$fd", "3<&$fd", "<&\"$fd\"", "2>&\"$fd\"", "2>&01", "<&00",
        "<&0 2>/dev/null", "2>&1 <&0", ">out 2>&1 <&0",
    };

    private static readonly string[] Commands =
    {
        "cat", "echo hi", ":", "read -r v", "g", "eval cat", "printf %s x", "sort",
        "X=1 cat", "X=1 echo hi", "X=1 g", "command cat", "builtin read -r v", "command eval cat", "X=1 command cat",
        "{ cat; }", "( cat )", "while read -r l; do echo $l; done", "if true; then cat; fi",
    };

    private static readonly string[] Contexts =
    {
        "{0}", "echo q | {0}", "{0} | cat", "echo q | {0} | cat", "echo q | {{ {0}; }}", "echo q | ( {0} )",
        "while read -r l; do {0}; done < f", "f() {{ {0}; }}", "x=$({0})", "{0} && echo ok", "if {0}; then :; fi",
        "set -o pipefail; echo q | {0}",
    };

    [Fact]
    public void Transpile_DupRedirect_InEveryContext_ParsesWithNoBackgroundPipeline()
    {
        var broken = new List<(string Redirect, string Command, string Why)>();
        foreach (var redirect in Redirects)
        foreach (var command in Commands)
        foreach (var context in Contexts)
        {
            var bash = "fd=3; g() { cat; }\n" + string.Format(context, command + " " + redirect);
            if (Problem(bash) is { } why) broken.Add((redirect, command, why));
        }
        // One line per redirect form: how many shapes broke, the commands they used, and the first of them.
        var summary = broken.GroupBy(b => b.Redirect).Select(g =>
            $"[{g.Count()}] ({string.Join(", ", g.Select(b => b.Command).Distinct())}) {g.First().Why}");
        Assert.True(broken.Count == 0, $"{broken.Count} broken:\n" + string.Join("\n", summary));
    }

    /// <summary>The emitted PowerShell does not parse, or runs a pipeline as a PowerShell background job.</summary>
    private static string? Problem(string bash)
    {
        string ps;
        try { ps = PsEmitter.Transpile(bash)!; }
        catch (ParseException) { return null; }
        var ast = PsParser.ParseInput(ps, out _, out ParseError[] errors);
        if (errors.Length > 0) return $"{Show(bash)}  =>  parse error: {errors[0].Message}";
        var background = ast.Find(a => a is PipelineAst { Background: true } or PipelineChainAst { Background: true }, true);
        return background is null ? null : $"{Show(bash)}  =>  background: {background.Extent.Text}";
    }

    private static string Show(string s) => s.Replace("\n", "\\n");
}
