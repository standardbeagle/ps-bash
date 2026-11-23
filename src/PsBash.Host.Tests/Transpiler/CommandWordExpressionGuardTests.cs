using System.Management.Automation.Language;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Host.Tests.Transpiler;

/// <summary>
/// A command word names a command; it is never a PowerShell statement that outputs itself. REGRESSION:
/// <c>$'\nWrite-Output PWNED'</c> (bash: <c>command not found</c>, 127) was emitted as a bare string statement, so it
/// printed its own text with status 0: <c>IsExpandedCommandWord</c> knew every quoted part but <c>$'…'</c>. Primitive:
/// <see cref="PsBuild.CommandWordReadsAsExpression"/>, applied to the emitted word in <c>EmitGeneralCommand</c>, whatever
/// spelling produced it. Oracle (qa-rubric Directive 1): PowerShell's own parser reads the transpiled text. End-to-end
/// bash parity: QuotingValueDifferentialTests.
/// </summary>
public class CommandWordExpressionGuardTests
{
    // Each holds the marker QQZ in its command word.
    public static TheoryData<string> Spellings => new()
    {
        "$'QQZ1'",
        "$'\\nQQZ2'",
        "$'a\\nWrite-Output QQZ3'",
        "$'QQZ4 x' arg",
        "$'QQZ5'x",
        "x$'QQZ6'",
        "'QQZ7'",
        "\"QQZ8\"",
        "'QQZ9'x",
        "\"$(printf 'a\\nQQZ10')\"",
        "$'\\n$(Write-Output QQZ11)'",
        "$'QQZ12' | cat",
        "echo a | $'QQZ13'",
        "$'QQZ14' 2>/dev/null; echo $?",
        "f() { $'QQZ15'; }; f",
        "\\$QQZ16",
        "$'a\\n\"QQZ17'",
    };

    [Theory]
    [MemberData(nameof(Spellings))]
    public void Transpile_CommandWord_IsNeverAnExpressionStatement(string bash)
    {
        string ps = PsEmitter.Transpile(bash)!;
        var ast = Parser.ParseInput(ps, out _, out ParseError[] errors);
        Assert.True(errors.Length == 0, "transpiled text does not parse:\n" + ps);
        var printed = ast.FindAll(a => a is CommandExpressionAst ce && PrintsItsValue(ce), searchNestedScriptBlocks: true)
            .Where(a => a.Extent.Text.Contains("QQZ", StringComparison.Ordinal))
            .Select(a => a.Extent.Text)
            .ToList();
        Assert.True(printed.Count == 0, $"command word emitted as an expression statement: {string.Join(" | ", printed)}\n{ps}");
    }

    [Theory]
    [InlineData("'x'", true)]
    [InlineData("\"x\"", true)]
    [InlineData("$x", true)]
    [InlineData("(x)", true)]
    [InlineData("‘x’", true)]
    [InlineData("“x”", true)]
    [InlineData("Invoke-BashEcho", false)]
    [InlineData("git", false)]
    [InlineData("./x", false)]
    [InlineData("", false)]
    public void CommandWordReadsAsExpression_AgreesWithThePowerShellParser(string word, bool expression)
    {
        Assert.Equal(expression, PsBuild.CommandWordReadsAsExpression(word));
        if (word.Length == 0) return;
        var ast = Parser.ParseInput(word, out _, out _);
        var first = ast.EndBlock.Statements[0] as PipelineAst;
        Assert.Equal(expression, first?.PipelineElements[0] is CommandExpressionAst);
    }

    // A string, a number or a variable standing as its own pipeline element prints its value; one on the right of an
    // assignment does not.
    private static bool PrintsItsValue(CommandExpressionAst ce)
        => ce.Expression is StringConstantExpressionAst or ExpandableStringExpressionAst or ConstantExpressionAst or VariableExpressionAst
            && ce.Parent is not AssignmentStatementAst
            && !(ce.Parent is PipelineAst { Parent: AssignmentStatementAst });
}
