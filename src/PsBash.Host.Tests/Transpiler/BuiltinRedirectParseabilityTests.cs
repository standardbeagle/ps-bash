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

    /// <summary>Every other redirect operator, input ones first (each is its own emission path).</summary>
    private static readonly string[] OtherRedirects =
    {
        "< in.txt", "0< in.txt", "< /dev/null", "<<< word", "< in.txt > out.txt", ">> out.txt", ">| out.txt", "2>> err.txt",
        "<> rw.txt", "3>/dev/null", "1>&2", "2>&1",
    };

    /// <summary>
    /// The same contract for every builtin with every other redirect operator, with and without an operand, and with a
    /// here-document. REGRESSION: <c>true &lt; file</c> (and <c>:</c>, <c>shift</c>, <c>export</c>, any builtin emitted as
    /// statements) became a pipe stage after the file's feed, an expression PowerShell does not allow there, so the
    /// WHOLE script failed to transpile.
    /// </summary>
    [Theory]
    [MemberData(nameof(Builtins))]
    public void Transpile_BuiltinWithAnyRedirectOperator_EmitsParseablePowerShell(string name)
    {
        var broken = new List<string>();
        foreach (var wrap in Wrappers)
        {
            foreach (var redirect in OtherRedirects)
            foreach (var shape in new[] { "{0}{1} {2}", "{0}{1} /nosuch {2}", "echo x | {0}{1} {2}", "{0}{1} {2}; echo after" })
            {
                var bash = string.Format(shape, wrap, name, redirect);
                if (ParseabilityContract.AssertNoCrash(bash) == ParseabilityContract.Outcome.BrokenPowerShell) broken.Add(Describe(bash));
            }
            var heredoc = wrap + name + " <<EOF\nbody\nEOF\necho after";
            if (ParseabilityContract.AssertNoCrash(heredoc) == ParseabilityContract.Outcome.BrokenPowerShell) broken.Add(Describe(heredoc));
        }
        Assert.True(broken.Count == 0, "emitted PowerShell does not parse:\n" + string.Join("\n", broken));
    }

    /// <summary>The input, PowerShell's first parse error and the emitted text around it.</summary>
    private static string Describe(string bash)
    {
        string ps = PsBash.Core.Transpiler.BashTranspiler.Transpile(bash);
        System.Management.Automation.Language.Parser.ParseInput(ps, out _, out var errors);
        if (errors.Length == 0) return bash.Replace("\n", "\\n");
        int at = errors[0].Extent.StartOffset;
        string around = ps[Math.Max(0, at - 90)..Math.Min(ps.Length, at + 40)];
        return bash.Replace("\n", "\\n") + "  =>  " + errors[0].Message + "  @  " + around;
    }

    [Theory]
    // The shapes reported: one per caller of the mapped-command switch.
    [InlineData("true < in.txt; echo after")]
    [InlineData(": < in.txt")]
    [InlineData("echo x | builtin cd /nosuch 2>/dev/null")]
    [InlineData("builtin cd /nosuch 2>/dev/null")]
    [InlineData("echo x | builtin read v 2>/dev/null")]
    [InlineData("builtin read v < in.txt 2>/dev/null")]
    [InlineData("echo x | builtin builtin cd /nosuch 2>/dev/null")]
    public void Transpile_BuiltinWrapperRedirect_Parses(string bash) => ParseabilityContract.Assert(bash);
}
