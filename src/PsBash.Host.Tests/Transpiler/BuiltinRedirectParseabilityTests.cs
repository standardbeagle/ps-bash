using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Host.Tests.Transpiler;

/// <summary>
/// Every bash builtin, reached bare, through <c>builtin</c> or through <c>command</c>, standalone or as a pipe stage,
/// with each redirect shape, must emit PowerShell that parses (<see cref="ParseabilityContract"/>).
/// <para>Root cause guarded: the <c>builtin</c> rewrite re-emitted its command WITH the redirects, and every caller of the
/// mapped-command switch appends the redirect tail again, so <c>echo x | builtin cd /nosuch 2&gt;/dev/null</c> became
/// <c>. { … } 2&gt;$null 2&gt;$null</c>. PowerShell refused it ("The error stream for this command is already
/// redirected"), so the WHOLE script failed before its first line ran. The class covers any wrapper, any builtin and any
/// redirect, because the defect is in how a re-emitting wrapper and its caller share the tail.</para>
/// Oracle note (qa-rubric Directive 1): the oracle is PowerShell's parser; behavior parity of the same shapes is
/// <c>Differential.Tests/BuiltinWrapperRedirectDifferentialTests</c>.
/// </summary>
public class BuiltinRedirectParseabilityTests
{
    private static readonly string[] Redirects = { "2>/dev/null", ">/dev/null", "> out.txt 2>&1", "2>err.txt >out.txt", "&>/dev/null" };
    private static readonly string[] Wrappers = { "", "builtin ", "command " };

    public static IEnumerable<object[]> Builtins() =>
        BashBuiltins.Names.OrderBy(n => n, StringComparer.Ordinal).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Builtins))]
    public void Transpile_BuiltinWithRedirect_InEveryPosition_EmitsParseablePowerShell(string name)
    {
        var broken = new List<string>();
        foreach (var wrap in Wrappers)
        foreach (var redirect in Redirects)
        foreach (var shape in new[] { "{0}{1} /nosuch {2}", "echo x | {0}{1} /nosuch {2}", "echo x | {0}{1} /nosuch {2} | cat" })
        {
            var bash = string.Format(shape, wrap, name, redirect);
            if (ParseabilityContract.AssertNoCrash(bash) == ParseabilityContract.Outcome.BrokenPowerShell) broken.Add(bash);
        }
        Assert.True(broken.Count == 0, "emitted PowerShell does not parse:\n" + string.Join("\n", broken));
    }

    [Theory]
    // The shapes reported: one per caller of the mapped-command switch.
    [InlineData("echo x | builtin cd /nosuch 2>/dev/null")]
    [InlineData("builtin cd /nosuch 2>/dev/null")]
    [InlineData("echo x | builtin read v 2>/dev/null")]
    [InlineData("builtin read v < in.txt 2>/dev/null")]
    [InlineData("echo x | builtin builtin cd /nosuch 2>/dev/null")]
    public void Transpile_BuiltinWrapperRedirect_Parses(string bash) => ParseabilityContract.Assert(bash);
}
