using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A builtin reached through the <c>builtin</c> / <c>command</c> wrapper, with a redirect, standalone or as a pipe stage,
/// against bash 5.2 (WSL Ubuntu-24.04). Parseability of every builtin in every such shape is
/// <c>Host.Tests/Transpiler/BuiltinRedirectParseabilityTests</c>; this class pins the behavior.
/// </summary>
public class BuiltinWrapperRedirectDifferentialTests
{
    private static Task Eq(string script) => AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    // Class: a wrapper that re-emits its command and the caller that appends the command's redirect tail each wrote the
    // tail (`. { … } 2>$null 2>$null`): the WHOLE script failed to parse, so not one line ran. Primitive: the redirects
    // are the caller's (TryEmitMappedCommand "builtin"); a statement list is grouped once (WithRedirectTailOn), a single
    // command (`read`) takes the tail bare, as a `. { }` group would not hand it the pipe it reads.
    [SkippableFact]
    public Task Builtin_WithRedirect_StandaloneAndPipeStage()
        => Eq("cd \"$(mktemp -d)\" || exit; echo x | builtin cd /nosuch 2>/dev/null; echo \"rc=$?\"; "
            + "builtin cd /nosuch 2>/dev/null; echo \"rc=$?\"; echo x | command cd /nosuch 2>/dev/null; echo \"rc=$?\"; "
            + "builtin echo hi > b.txt; cat b.txt; printf 'line\\n' > in.txt; "
            + "read w < in.txt 2>/dev/null; echo \"rc=$? w=$w\"; builtin read w2 < in.txt 2>/dev/null; echo \"rc=$? w2=$w2\"; "
            + "builtin cd /nosuch 2>err.txt; echo \"rc=$?\"; sed 's/^.*cd: /cd: /' err.txt; "
            + "echo x | builtin builtin cd /nosuch 2>/dev/null; echo \"rc=$?\"; echo done");

    // Class: a pipe stage is a subshell, but a state builtin behind `builtin`/`command` was not recognised as one, so
    // `read`'s variable and `cd`'s directory leaked into the shell. Primitive: WrappedCommandName in the stage-scope rule.
    [SkippableFact]
    public Task WrappedStateBuiltin_AsPipeStage_StaysInItsSubshell()
        => Eq("cd \"$(mktemp -d)\" || exit; here=$PWD; echo x | read a1; echo \"a1=$a1\"; echo x | builtin read a3; echo \"a3=$a3\"; "
            + "echo x | builtin read a4 2>/dev/null; echo \"rc=$? a4=$a4\"; echo x | command read a5; echo \"a5=$a5\"; "
            + "echo x | builtin cd / 2>/dev/null; [ \"$PWD\" = \"$here\" ] && echo same; builtin cd / | cat; [ \"$PWD\" = \"$here\" ] && echo same2");

    // Class: a special form that only reports dropped its redirects (`return 3 2>/dev/null` at the top printed its
    // message), and `return` as a later pipe stage emitted an expression where PowerShell allows only a command
    // (`… | $(…)`: the script did not parse). Primitives: reportOnly + WithRedirectTailOn; WrapPipelineStageIfStatementList.
    [SkippableFact]
    public Task ReportOnlySpecials_TakeTheirRedirects()
        => Eq("cd \"$(mktemp -d)\" || exit; return 3 2>/dev/null; echo \"ret rc=$?\"; return 3 2>err1.txt; echo \"ret rc=$?\"; "
            + "wc -l < err1.txt; ( exit 1 2 2>/dev/null; echo \"after exit rc=$?\" ); "
            + "fn() { echo x | return 3; echo \"in fn rc=$?\"; }; fn; echo \"fn rc=$?\"; echo x | return 4 2>/dev/null; echo \"top rc=$?\"");

    // Class: `let` evaluates in a nested script whose error stream is not the cmdlet's, so `let /x 2>/dev/null` printed
    // anyway (InvokeBashLetCommand merges it, as RunInner does); an operand the static grammar rejects raw-crashed the
    // transpiler (TryEmitLetAsArithmetic now hands it to the cmdlet). The message wording is not compared.
    [SkippableFact]
    public Task Let_SyntaxError_HonoursRedirect()
        => Eq("let /nosuch 2>/dev/null; echo \"let rc=$?\"; let 'q=2+3' 2>/dev/null; echo \"rc=$? q=$q\"; "
            + "let /nosuch 2>&1 | grep -c 'let: /nosuch: '");
}
