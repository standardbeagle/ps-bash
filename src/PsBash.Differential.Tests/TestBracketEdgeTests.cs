using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>`[` / `test` edge forms: unclosed or over-closed `[`, runtime syntax errors (status 2), `-o` options, `{`/`}` as arguments.</summary>
public class TestBracketEdgeTests
{
    [Fact]
    public Task Bracket_MissingOrExtraClose_IsStatus2() => AssertOracle.EqualAsync(
        "{ [; } 2>/dev/null\necho s=$?\ntest\necho s=$?\n{ [ -n x; } 2>/dev/null\necho s=$?\n{ [ -n x ] y; } 2>/dev/null\necho s=$?\n{ [ -n x y; } 2>/dev/null\necho s=$?");

    [Fact]
    public Task Bracket_RuntimeSyntaxError_IsStatus2() => AssertOracle.EqualAsync(
        "{ [ '(' foo ]; } 2>/dev/null\necho s=$?\n[ a = a ]\necho s=$?\n[ a = b ]\necho s=$?");

    [Fact]
    public Task Test_OptionFlag_ReflectsSetO() => AssertOracle.EqualAsync(
        "test -o nounset; echo o1=$?\nset -o nounset\ntest -o nounset; echo o2=$?\n[ -o nounset ] && echo on\nset +o nounset\n[ -o nounset ]; echo o3=$?\ntest -o _bad_name_; echo o4=$?\nset -e\n[ -o errexit ] && echo e\nset +e");

    [Fact]
    public Task OpenBrace_AfterCommandWord_IsAnArgument() => AssertOracle.EqualAsync(
        "echo a {\necho { b\ntype -t for time ! fi do {");

    [Fact]
    public Task BareAssignment_ResetsStatusToZero() => AssertOracle.EqualAsync(
        "false; y=${u-$y}; echo s1=$?\nfalse; z=1; echo s2=$?\nfalse; w=$(true); echo s3=$?\nfalse; x=$(false); echo s4=$?\nfalse; a=(1 2); echo s5=$?\nfalse && z=2; echo s6=$?\nset -e\nx=$(false) || true\n[ -n \"$APORTSDIR\" ] && true\nB=${_B-$B}\necho s7=$?");

    /// <summary>
    /// `[` is a command: an unquoted expansion is split before test sees it and an empty one is no word, so with
    /// emp='' `[ -n $emp ]` is `[ -n ]` (true) and `[ $emp = '' ]` is a syntax error (status 2). Each was one operand.
    /// </summary>
    [Fact]
    public Task Bracket_UnquotedExpansion_IsSplitBeforeTest() => AssertOracle.EqualAsync(
        "emp=''; two='a b'; one=x; dsh='-e'\n[ -n $emp ] && echo 1T\n[ -z $emp ] && echo 2T\n"
        + "[ $emp = '' ] 2>/dev/null; echo \"3 st:$?\"\n[ $two = 'a b' ] 2>/dev/null; echo \"4 st:$?\"\n[ $one = x ] && echo 5T\n"
        + "[ -e $emp ] && echo 6T\n[ $dsh = -e ] && echo 7T\n[ ! $emp ] && echo 8T || echo 8F\n"
        + "[ \\( $one = x \\) -a -n \"$emp$one\" ] && echo 9T\nif [ -n $emp ]; then echo 10T; fi\n[ x$emp = x ] && echo 11T\n[ $# -eq 0 ] && echo 12T");

    /// <summary>A quoted `&lt;` / `&gt;` in `[ ]` is an ASCII string comparison: `[ a '&lt;' b ]` emitted invalid PowerShell
    /// (the operator was dropped), and `[ -z '&gt;' -- ]` is a binary `&gt;` (true), not `-z`.</summary>
    [Fact]
    public Task Bracket_QuotedAngleOperators_CompareStrings() => AssertOracle.EqualAsync(
        "[ -z ] && echo 1T\n[ -z '>' ] || echo 2F\n[ -z '>' -- ] && echo 3T\n[ a '<' b ] && echo 4T\n[ b '<' a ] || echo 5F\n"
        + "[ B '>' a ] || echo 6F\nx=abc; [ \"$x\" \\> abb ] && echo 7T\n[ a \\< b -a c \\> b ] && echo 8T\nif [ \"$x\" '<' z ]; then echo 9T; fi");

    [Fact]
    public Task LocalExportDeclare_ResetStatusToZero() => AssertOracle.EqualAsync(
        "f() { false; local q=1; echo s1=$?; false; local r; echo s2=$?; false; export Z=1; echo s3=$?; false; declare d=1; echo s4=$?; }\nf\nfalse; export Y=1; echo s5=$?");
}
