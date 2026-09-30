using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// `:` / true / false builtins and the `!`-leading forms of `[ ]` / `[[ ]]`, checked against bash.
/// </summary>
public class ColonAndBangTestDifferentialTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    [SkippableTheory]
    [InlineData(":; echo rc=$?")]
    [InlineData(": a b c; echo rc=$?")]
    [InlineData("x=; : ${x:=5}; echo $x")]
    [InlineData(": > /dev/null; echo rc=$?")]
    [InlineData("false; : ; echo rc=$?")]
    [InlineData(": $(echo side >&2) 2>&1; echo rc=$?")]
    [InlineData("while :; do echo once; break; done")]
    [InlineData("if :; then echo yes; fi")]
    [InlineData(": --help; echo rc=$?")]
    [InlineData("true --help; echo rc=$?")]
    [InlineData("false --help; echo rc=$?")]
    [InlineData("true x y; echo rc=$?")]
    [InlineData("false x y; echo rc=$?")]
    [InlineData("echo hi | :; echo rc=$?")]
    [InlineData(": && echo and; false || :; echo rc=$?")]
    public Task ColonTrueFalse_Builtins(string script) => Eq(script);

    [SkippableTheory]
    [InlineData("[ ! = ! ]; echo $?")]
    [InlineData("[ ! != ! ]; echo $?")]
    [InlineData("[ ! ]; echo $?")]
    [InlineData("[ ! ! ]; echo $?")]
    [InlineData("[ ! a ]; echo $?")]
    [InlineData("[ ! '' ]; echo $?")]
    [InlineData("[ ! = x ]; echo $?")]
    [InlineData("[ x = ! ]; echo $?")]
    [InlineData("[ ! -a ! ]; echo $?")]
    [InlineData("[ ! -o ! ]; echo $?")]
    [InlineData("[[ ! a ]]; echo $?")]
    [InlineData("[[ ! -z x ]]; echo $?")]
    public Task BangOperand_Tests(string script) => Eq(script);
}
