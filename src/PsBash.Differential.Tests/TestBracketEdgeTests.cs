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
}
