using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for how a diagnostic names an operand: as typed. Readers resolve the operand to a full path
/// before opening it, and OperandDisplay maps that back. An operand under the working directory that the reader never
/// recorded fell to the cwd-stripping guess, so <c>cat "$PWD/x"</c> and <c>cat ./x</c> both printed <c>cat: x: …</c>.
/// </summary>
public class OperandDisplayDifferentialTests
{
    [SkippableFact]
    public Task MissingOperand_InEveryTypedForm_IsNamedAsTyped()
        => AssertOracle.EqualAsync(
            "d=$(mktemp -d); cd \"$d\" || exit; mkdir sub; "
            + "for op in \"$d/nope\" ./nope nope sub/../nope \"$d/sub/nope\"; do "
            + "cat \"$op\"; head -n1 \"$op\"; wc -l \"$op\"; grep x \"$op\"; sort \"$op\"; tail -n1 \"$op\"; "
            + "done 2>&1 | sed \"s|$d|D|g\"; cd / && rm -rf \"$d\"",
            timeout: TimeSpan.FromSeconds(30));
    /// <summary>
    /// Two operands that name one file (<c>./f f</c>) each keep their own spelling, in headers, wc's name column and
    /// diagnostics alike. REGRESSION: the name was recovered from the resolved path, whose table holds the LATER
    /// spelling, so every command that resolves all operands before using them (tail, wc, paste, comm) said
    /// <c>f</c> twice. Fixed at the class: an operand carries its typed name (<c>FileSystemHelpers.OperandPath</c>)
    /// and claims it at its turn; the commands listed resolve lazily or eagerly, so both orders are covered.
    /// </summary>
    [SkippableFact]
    public Task SameFileNamedTwice_EachOperandKeepsItsSpelling()
        => AssertOracle.EqualAsync(
            "d=$(mktemp -d); cd \"$d\" || exit; printf 'a b\\n' > f; printf 'a c\\n' > g; "
            + "for c in cat 'cat -n' head 'head -c1' tail 'tail -c2' wc 'wc -l' 'grep -H a' nl md5sum paste comm strings; do "
            + "echo \"## $c\"; $c ./f f 2>&1; echo \"rc=$?\"; $c ./nosuch nosuch 2>&1; echo \"rc=$?\"; "
            + "done; comm ./f g; head -n1 ./f f ./f; cd / && rm -rf \"$d\"",
            timeout: TimeSpan.FromSeconds(60));

    /// <summary>
    /// Operands are read in argv order, <c>-</c> being the pipeline. REGRESSIONS: <c>wc - f</c> reported
    /// <c>wc: -: No such file or directory</c> (its resolver had no <c>-</c>); <c>cat f -</c> printed the pipeline
    /// before <c>f</c> (it streamed stdin whatever its position); <c>cat -n</c> numbered the piece that continues an
    /// unterminated line (<c>1 q</c> then <c>2 a</c> on one line, GNU <c>1 qa</c>); <c>paste -s</c> stopped at the first
    /// missing file, where GNU reports each and pastes the rest.
    /// </summary>
    [SkippableFact]
    public Task Operands_AreReadInOrder_WithDashAsThePipeline()
        => AssertOracle.EqualAsync(
            "d=$(mktemp -d); cd \"$d\" || exit; printf 'a b\\n' > f; printf 'c\\n' > g; printf q > u; printf '\\n\\nc\\n' > h; "
            + "echo hi | wc -l - f; echo x | wc - -; echo y | head -n1 f -; echo z | tail -n1 - f; "
            + "echo z | cat f - g; echo z | cat - f -; echo z | cat f /dev/stdin; printf 'p\\nq' | cat -n g - f; "
            + "cat -n u f; cat -b u h; cat -sn u h h; { printf q; echo a; echo; echo b; } | cat -bE; "
            + "paste -s nosuch f ./nosuch 2>/dev/null; echo \"rc=$?\"; cd / && rm -rf \"$d\"",
            timeout: TimeSpan.FromSeconds(30));
}
