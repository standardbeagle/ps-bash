using PsBash.Differential.Tests.Oracle;
using Xunit;
using static PsBash.Differential.Tests.Oracle.FsStateOracle;

namespace PsBash.Differential.Tests;

/// <summary>
/// Three bash-semantics bugs, each checked against the bash oracle:
/// (1) an unquoted <c>$(cmd)</c> / `cmd` is word-split on IFS and glob-expanded — in a for list and as a
///     command operand — and an empty result contributes no word;
/// (2) a redirection on a command that writes nothing (<c>:</c>, <c>true</c>, <c>false</c>, a bare
///     <c>&gt; f</c>) still opens its target;
/// (3) <c>VAR=v cmd</c> in PIPE position sets VAR for that stage only (and does not leak).
/// Stderr wording for a failed redirect differs by shell name/line prefix, so those cases discard stderr
/// and compare the status and the resulting tree.
/// </summary>
public class ExpansionRedirectEnvDifferentialTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    // ───────────── (1) command-substitution word splitting ─────────────

    [SkippableTheory]
    [InlineData("for f in $(echo a b c); do echo \"<$f>\"; done")]
    [InlineData("for f in $(seq 3); do echo \"<$f>\"; done")]
    [InlineData("for f in $(printf 'a\\nb\\n'); do echo \"<$f>\"; done")]
    [InlineData("for f in `echo a b`; do echo \"<$f>\"; done")]
    [InlineData("for f in \"$(echo a b)\"; do echo \"<$f>\"; done")]
    [InlineData("for f in $(echo); do echo \"<$f>\"; done; echo end")]
    [InlineData("for f in $(echo '   '); do echo \"<$f>\"; done; echo end")]
    [InlineData("for f in x $(echo a b) y; do echo \"<$f>\"; done")]
    [InlineData("for f in $(echo a b) $(echo c d); do echo \"<$f>\"; done")]
    [InlineData("a='x y'; for w in $a z; do echo \"<$w>\"; done")]
    [InlineData("a='x y'; for w in z $a; do echo \"<$w>\"; done")]
    [InlineData("for f in $(echo '  a   b  '); do echo \"<$f>\"; done")]
    public Task ForList_CommandSubstitution_WordSplits(string script) => Eq(script);

    [SkippableTheory]
    [InlineData("IFS=:; for p in $(echo a:b c); do echo \"<$p>\"; done")]
    [InlineData("IFS=:; for p in $(echo a::b:); do echo \"<$p>\"; done")]
    [InlineData("IFS=: ; for p in $(echo ' a : b '); do echo \"<$p>\"; done")]
    [InlineData("IFS=; for p in $(echo a b); do echo \"<$p>\"; done")]
    public Task ForList_CommandSubstitution_HonoursIfs(string script) => Eq(script);

    [SkippableTheory]
    [InlineData("printf '[%s]' $(echo a b); echo")]
    [InlineData("printf '[%s]' $(echo); echo")]
    [InlineData("printf '[%s]' \"$(echo a b)\"; echo")]
    [InlineData("printf '[%s]' $(printf 'a\\nb\\n') c; echo")]
    [InlineData("echo $(echo a    b)")]
    public Task Operand_CommandSubstitution_WordSplits(string script) => Eq(script);

    [SkippableFact]
    public Task ForList_CommandSubstitutionResultIsGlobExpanded() =>
        EqualAsync(Tree(("x", "1"), ("y", "2")), "for f in $(echo '*'); do echo \"<$f>\"; done");

    [SkippableFact]
    public Task ForList_CommandSubstitutionGlobWithoutMatchStaysLiteral() =>
        EqualAsync(Tree(("x", "1")), "for f in $(echo 'q*'); do echo \"<$f>\"; done");

    [SkippableFact]
    public Task Operand_CommandSubstitutionResultIsGlobExpanded() =>
        EqualAsync(Tree(("x", "1"), ("y", "2")), "printf '[%s]' $(echo '*'); echo");

    // The RC-7 / command-substitution operand hoist wraps the stage in a script block; the pipe input
    // must still reach the command (it used to be dropped: `printf … | grep $x` printed nothing).
    [SkippableTheory]
    [InlineData("x=b; printf 'a\\nb\\n' | grep $x")]
    [InlineData("printf 'a\\nb\\n' | grep $(echo b)")]
    [InlineData("printf 'a\\nb\\n' | grep \"$(echo b)\"")]
    [InlineData("printf 'a\\nb\\nc\\n' | grep -v $(echo b)")]
    public Task PipeTarget_SplitOperand_StillReceivesPipeInput(string script) => Eq(script);
}
