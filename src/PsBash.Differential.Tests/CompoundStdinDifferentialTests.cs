using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A compound command's stdin (a pipe stage, or <c>&lt; file</c> / <c>&lt;&lt;&lt;</c>) is shared by
/// EVERY command inside it, with one cursor — not just <c>read</c>. Each case is byte-diffed against bash.
/// Not covered on purpose (documented divergence): <c>{ head -n1; cat; } &lt; file</c>, where GNU head
/// seeks the regular file back so cat still sees the rest; fed from a pipe (below) bash and ps-bash agree.
/// </summary>
public class CompoundStdinDifferentialTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    [SkippableTheory]
    [InlineData("printf 'b\\na\\n' | { sort; }")]
    [InlineData("printf 'x\\n' | ( cat )")]
    [InlineData("printf 'b\\na\\nc\\n' | { sort -r; }")]
    [InlineData("printf '1\\n2\\n3\\n' | { head -n1; cat; }")]
    [InlineData("printf '1\\n2\\n3\\n' | { read x; sort -r; echo got $x; }")]
    [InlineData("printf '1\\n2\\n3\\n' | { read x; read y; echo \"$x$y\"; cat; }")]
    [InlineData("printf 'a\\nb\\n' | { cat; cat; echo end; }")]
    [InlineData("printf 'a\\nb\\n' | while read l; do echo \"<$l>\"; done")]
    [InlineData("printf 'a\\nb\\n' | { echo head; cat; echo tail; }")]
    [InlineData("printf 'b\\na\\nb\\n' | { grep -v a | sort; }")]
    [InlineData("printf 'b\\na\\nb\\n' | { sort | uniq -c; }")]
    public Task BraceGroupAndSubshellAsPipeStage_ShareStdin(string script) => Eq(script);

    [SkippableTheory]
    [InlineData("printf '1\\n2\\n3\\n' | if true; then sort -r; fi")]
    [InlineData("printf '1\\n2\\n3\\n' | if true; then cat; else echo no; fi")]
    [InlineData("printf '1\\n2\\n3\\n' | for i in 1; do cat; done")]
    [InlineData("printf '1\\n2\\n3\\n' | for i in 1 2; do cat; echo \"[$i]\"; done")]
    [InlineData("printf '1\\n2\\n3\\n' | case x in x) wc -l;; esac")]
    [InlineData("printf 'b\\na\\n' | case x in y) echo no;; *) sort;; esac")]
    [InlineData("printf 'a\\nb\\n' | while true; do cat; break; done")]
    public Task OtherCompoundsAsPipeStage_ShareStdin(string script) => Eq(script);

    [SkippableTheory]
    [InlineData("printf '3\\n1\\n2\\n' > /tmp/psb_cs_a.txt; { sort; } < /tmp/psb_cs_a.txt; rm -f /tmp/psb_cs_a.txt")]
    [InlineData("printf '3\\n1\\n2\\n' > /tmp/psb_cs_b.txt; ( sort ) < /tmp/psb_cs_b.txt; rm -f /tmp/psb_cs_b.txt")]
    [InlineData("printf '3\\n1\\n2\\n' > /tmp/psb_cs_c.txt; { read x; sort; echo $x; } < /tmp/psb_cs_c.txt; rm -f /tmp/psb_cs_c.txt")]
    [InlineData("printf '3\\n1\\n2\\n' > /tmp/psb_cs_d.txt; if true; then sort; fi < /tmp/psb_cs_d.txt; rm -f /tmp/psb_cs_d.txt")]
    [InlineData("printf '3\\n1\\n2\\n' > /tmp/psb_cs_e.txt; for i in 1; do cat; done < /tmp/psb_cs_e.txt; rm -f /tmp/psb_cs_e.txt")]
    [InlineData("printf 'a\\nb\\n' > /tmp/psb_cs_f.txt; { while read l; do echo \"<$l>\"; done; } < /tmp/psb_cs_f.txt; rm -f /tmp/psb_cs_f.txt")]
    public Task RedirectedInputFile_FeedsEveryCommandInside(string script) => Eq(script);

    [SkippableTheory]
    [InlineData("{ cat; } <<< hi")]
    [InlineData("{ cat; } <<< 'a b'")]
    [InlineData("( cat ) <<< hi")]
    [InlineData("{ read x; echo \"[$x]\"; } <<< hi")]
    [InlineData("for i in 1; do cat; done <<< hi")]
    [InlineData("{ sort; } <<EOF\nb\na\nEOF")]
    [InlineData("{ cat; echo mid; cat; } <<< hi")]
    public Task HereStringAndHeredocIntoCompound_FeedsEveryCommandInside(string script) => Eq(script);

    [SkippableTheory]
    [InlineData("printf 'a\\nb\\n' | { echo one; echo two; }")]
    [InlineData("printf 'a\\nb\\n' | { echo one; cat; }")]
    [InlineData("printf 'a\\nb\\n' | { cat /dev/null; cat; }")]
    [InlineData("printf 'a\\nb\\n' | { true; sort -r; }")]
    [InlineData("printf 'a\\nb\\n' | { printf 'x\\n'; cat; }")]
    [InlineData("printf 'a\\nb\\n' | { { cat; } | sort -r; }")]
    [InlineData("printf 'a\\nb\\n' | { cat | { sort -r; }; }")]
    [InlineData("printf 'q\\n' | { { read z; echo \"z=$z\"; } | cat; }")]
    public Task NonReadingCommandsAndNestedCompounds_DoNotStealStdin(string script) => Eq(script);
}
