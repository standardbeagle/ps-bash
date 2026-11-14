using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>`$((` and `((` are arithmetic only when the inner `(` is closed by a `)` directly followed by `)` (bash's
/// parse_arith_cmd); otherwise they are a command substitution / subshell whose body starts with a subshell.</summary>
public class ArithOrSubshellTests
{
    private static WordPart FirstPart(string bash)
    {
        var cmd = Assert.IsType<Command.Simple>(BashParser.Parse(bash));
        return cmd.Words[1].Parts[0];
    }

    [Theory]
    [InlineData("echo $(( echo 1\necho 2\n(( x ))\n: $(( x ))\necho 3\n) )")]
    [InlineData("echo $((which gtk || echo /opt/x)2>/dev/null)")]
    [InlineData("echo \"$((which gtk || echo /opt/x)2>/dev/null)\"")]
    public void Transpile_DollarDoubleParenClosedBySpacedParen_IsCommandSub(string bash)
    {
        var part = FirstPart(bash);
        if (part is WordPart.DoubleQuoted dq) part = dq.Parts[0];
        Assert.IsType<WordPart.CommandSub>(part);
    }

    // bash: `echo $((1)+(2))` is a command substitution whose body `(1)+(2)` is a syntax error.
    [Fact]
    public void Transpile_DollarDoubleParenClosedByParenThenOperator_IsCommandSubSyntaxError()
        => Assert.Throws<ParseException>(() => BashParser.Parse("echo $((1)+(2))"));

    [Theory]
    [InlineData("echo $(( (1+2) * 3 ))")]
    [InlineData("echo $(( $(grep -c \"a)b\" f.txt) + 1 ))")]
    [InlineData("echo $(( $(echo ')') + 1 ))")]
    [InlineData("echo $((!(1 || 2)))")]
    [InlineData("echo $(( x ))")]
    public void Transpile_DollarDoubleParenClosedByDoubleParen_IsArith(string bash)
        => Assert.IsType<WordPart.ArithSub>(FirstPart(bash));

    [Fact]
    public void Transpile_DoubleParenSubshellPipelineInCommandSub_Parses()
    {
        const string bash = "st=$(\n  exec 4>&1\n  (gzip -cdfq -- \"$f1\" 4>&-; echo $? >&4) 3>&- |\n"
            + "      ((gzip -cdfq -- \"$f2\" 4>&-\n        echo $? >&4) 3>&- 5<&- </dev/null |\n"
            + "       eval \"$cmp\" /dev/fd/5 - >&3) 5<&0\n)\necho bye";
        Assert.Contains("bye", PsEmitter.Transpile(bash));
    }

    [Theory]
    [InlineData("((gzip -cd f) | cat)")]
    [InlineData("((echo a) ; (echo b) )")]
    public void Transpile_DoubleParenCommandClosedEarly_IsNestedSubshell(string bash)
        => Assert.IsType<Command.Subshell>(BashParser.Parse(bash));

    [Fact]
    public void Transpile_DoubleParenArithmeticCommand_StaysArith()
        => Assert.IsType<Command.ArithCommand>(BashParser.Parse("(( (x) + 1 ))"));
}
