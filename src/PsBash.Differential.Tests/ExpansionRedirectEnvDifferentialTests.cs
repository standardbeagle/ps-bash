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

    // ───────────── (2) redirections on commands that write nothing ─────────────

    [SkippableFact] public Task Colon_RedirectOut_CreatesFile() =>
        EqualAsync(Tree(("a", "A")), ": > f; echo rc=$?");

    [SkippableFact] public Task Colon_RedirectOut_TruncatesExistingFile() =>
        EqualAsync(Tree(("f", "old content")), ": > f; echo rc=$?");

    [SkippableFact] public Task Colon_NoSpace_RedirectOut_Truncates() =>
        EqualAsync(Tree(("f", "old content")), ":>f; echo rc=$?");

    [SkippableFact] public Task Colon_RedirectAppend_CreatesAndKeepsContent() =>
        EqualAsync(Tree(("keep", "stays")), ": >> new; : >> keep; echo rc=$?");

    [SkippableFact] public Task True_RedirectOut_Truncates() =>
        EqualAsync(Tree(("f", "old")), "true > f; echo rc=$?");

    [SkippableFact] public Task True_RedirectAppend_Creates() =>
        EqualAsync(Tree(("a", "A")), "true >> f; echo rc=$?");

    [SkippableFact] public Task False_RedirectOut_CreatesFileAndFails() =>
        EqualAsync(Tree(("a", "A")), "false > f; echo rc=$?");

    [SkippableFact] public Task BareRedirect_CreatesFile() =>
        EqualAsync(Tree(("a", "A")), "> f; echo rc=$?");

    [SkippableFact] public Task BareRedirect_TruncatesExistingFile() =>
        EqualAsync(Tree(("f", "old")), "> f; echo rc=$?");

    [SkippableFact] public Task BareRedirect_Append_CreatesAndKeeps() =>
        EqualAsync(Tree(("keep", "stays")), ">> new; >> keep; echo rc=$?");

    [SkippableFact] public Task Colon_TwoRedirects_OpensBoth() =>
        EqualAsync(Tree(("a", "old")), ": > a > b; echo rc=$?");

    [SkippableFact] public Task Colon_RedirectToDevNull_CreatesNothing() =>
        EqualAsync(Tree(("a", "A")), ": > /dev/null; echo rc=$?");

    [SkippableFact] public Task Colon_RedirectInMissingDirectory_FailsWithStatus1() =>
        EqualAsync(Tree(("a", "A")), "{ : > nodir/f; } 2>/dev/null; echo rc=$?");

    [SkippableFact] public Task True_RedirectInMissingDirectory_FailsWithStatus1() =>
        EqualAsync(Tree(("a", "A")), "{ true >> nodir/f; } 2>/dev/null; echo rc=$?");

    [SkippableFact] public Task Colon_RedirectFailure_FlipsOrChain() =>
        EqualAsync(Tree(("a", "A")), ": 2>/dev/null > nodir/f || echo failed; echo rc=$?");

    [SkippableFact] public Task Colon_RedirectSuccess_DoesNotTakeOrBranch() =>
        EqualAsync(Tree(("a", "A")), ": > f || echo failed; echo rc=$?");

    [SkippableFact] public Task Echo_RedirectInMissingDirectory_FailsWithStatus1() =>
        EqualAsync(Tree(("a", "A")), "{ echo hi > nodir/f; } 2>/dev/null; echo rc=$?");

    // ───────────── (3) env prefix on a pipe-target stage ─────────────

    [SkippableTheory]
    [InlineData("seq 1 40 | COLUMNS=60 column")]
    [InlineData("seq 1 40 | COLUMNS=20 column | head -3")]
    [InlineData("echo x | FOO=1 env | grep FOO")]
    [InlineData("echo x | FOO=1 cat; echo \"[$FOO]\"")]
    [InlineData("FOO=outer; echo x | FOO=1 env | grep '^FOO='; echo \"after=$FOO\"")]
    [InlineData("printf 'b\\na\\n' | LC_ALL=C sort")]
    [InlineData("printf 'b\\na\\n' | LC_ALL=C sort | head -1")]
    [InlineData("echo x | A=1 B=2 env | grep -E '^(A|B)=' | sort")]
    [InlineData("echo hi | FOO=1 true; echo rc=$?")]
    [InlineData("echo hi | FOO=1 grep -c hi; echo \"[$FOO]\"")]
    [InlineData("echo a b | FOO=$(echo v) env | grep '^FOO='")]
    public Task PipeTarget_EnvPrefix_AppliesToThatStageOnly(string script) => Eq(script);

    [SkippableTheory]
    [InlineData("FOO=1 env | grep '^FOO='; echo \"[$FOO]\"")]
    [InlineData("env | FOO=1 grep -c '^FOO='")]
    public Task EnvPrefix_StandaloneAndOnLaterStage(string script) => Eq(script);

    // The RC-7 / command-substitution operand hoist wraps the stage in a script block; the pipe input
    // must still reach the command (it used to be dropped: `printf … | grep $x` printed nothing).
    [SkippableTheory]
    [InlineData("x=b; printf 'a\\nb\\n' | grep $x")]
    [InlineData("printf 'a\\nb\\n' | grep $(echo b)")]
    [InlineData("printf 'a\\nb\\n' | grep \"$(echo b)\"")]
    [InlineData("printf 'a\\nb\\nc\\n' | grep -v $(echo b)")]
    public Task PipeTarget_SplitOperand_StillReceivesPipeInput(string script) => Eq(script);
}
