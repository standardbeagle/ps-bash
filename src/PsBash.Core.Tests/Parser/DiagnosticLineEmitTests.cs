using Xunit;
using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// The line a shell diagnostic shows (<c>s.sh: line N: x: command not found</c>) is recorded by the emitted code
/// (<see cref="PsBuild.SetCallLine"/>) before a statement that may raise one, and xtrace traces a command's executed
/// FIELDS. bash 5.2 (WSL) is the oracle for each shape; the end-to-end cases are Differential
/// <c>ShellDiagnosticLineDifferentialTests</c> / <c>XtraceFieldsDifferentialTests</c>. These pin the emitted shape.
/// </summary>
public class DiagnosticLineEmitTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Fact]
    public void Transpile_MissingCommandPastLine1_RecordsItsLine()
        => Assert.Contains(PsBuild.SetCallLine(3), T("echo a\necho b\nnosuch_x"));

    [Fact]
    public void Transpile_MissingCommandOnLine1_ReliesOnTheHostsLine1()
        // The host starts every command on line 1 (VarFrameStack.ResetFor): no SetLine needed.
        => Assert.DoesNotContain("SetLine(", T("nosuch_x"));

    [Fact]
    public void Transpile_MappedCommand_RecordsNoLine()
        // A mapped command or builtin cannot be "not found": no per-statement cost.
        => Assert.DoesNotContain("SetLine(", T("echo a\necho b\ncat f"));

    [Fact]
    public void Transpile_MultiLineQuotedWord_CountsItsNewlines()
        // bash: the command after `echo 'a<nl>b'` on line 1 is on line 3.
        => Assert.Contains(PsBuild.SetCallLine(3), T("echo 'a\nb'\nnosuch_x"));

    [Fact]
    public void Transpile_BackslashNewlineAfterTheName_IsTheNextWordsLine()
        // bash: `nosuch_x \<nl> arg` on lines 2-3 reports line 3.
        => Assert.Contains(PsBuild.SetCallLine(3), T("echo a\nnosuch_x \\\n arg"));

    /// <summary>
    /// bash numbers a simple command where the token after its first word ends (it reads that token to tell <c>x</c>
    /// from <c>x ()</c>). Each case is the last command of the input; expected lines from bash 5.2 (WSL, `bash lp.sh`).
    /// </summary>
    [Theory]
    [InlineData("nA \\\n  arg", 2)]
    [InlineData("nB a \\\nb", 1)]
    [InlineData("nC \\\n\\\nx", 3)]
    [InlineData("nD 'a\nb'", 2)]
    [InlineData("nE 'a\nb' 'c\nd'", 2)]
    [InlineData("'n\nF' x", 2)]
    [InlineData("nG \\\n\n", 2)]
    [InlineData("x=1 nJ \\\ny", 1)]
    [InlineData("nK \"a\nb\"", 2)]
    [InlineData("nM a \"b\nc\"", 1)]
    [InlineData("nN", 1)]
    [InlineData("nO;", 1)]
    public void Parse_SimpleCommand_ReportLineIsWhereTheSecondTokenEnds(string src, int expected)
    {
        var cmd = BashParser.Parse("echo first\n" + src)!;
        var last = cmd is Command.CommandList list ? list.Commands[^1] : cmd;
        var simple = Assert.IsType<Command.Simple>(last);
        Assert.Equal(2, simple.Line);
        Assert.Equal(expected + 1, simple.ReportLine);
    }

    [Fact]
    public void Transpile_InsideAFunction_RecordsTheScriptLine()
    {
        // bash: a function body's lines are the script's, also on the script's line 1.
        var ps = T("f() { nosuch_x; }\nf");
        Assert.Contains(PsBuild.SetCallLine(1), ps);
    }

    [Fact]
    public void Transpile_LoopReachingPastLine1_RecordsLine1Again()
    {
        // The second pass of line 1 runs after line 2 recorded its line.
        var ps = T("while nosuch_x; do\n nosuch_y; done");
        Assert.Contains(PsBuild.SetCallLine(1), ps);
        Assert.Contains(PsBuild.SetCallLine(2), ps);
    }

    [Fact]
    public void Transpile_StaticEval_NumbersItsTextFromTheEvalsLine()
        // bash: `eval 'true<nl>nosuch_x'` on lines 2-3 is numbered 3, so its text's line 2 is line 4.
        => Assert.Contains(PsBuild.SetCallLine(4), T("echo a\neval 'true\nnosuch_x'"));

    [Fact]
    public void Transpile_HoistedCondition_RecordsItsLine()
    {
        // bash: `if nosuch_if; then` on line 2 reports line 2; a loop's condition again on each pass.
        Assert.Contains(PsBuild.SetCallLine(2), T("echo a\nif nosuch_if; then :; fi"));
        Assert.Contains(PsBuild.SetCallLine(2), T("echo a\nuntil nosuch_un; do\n echo b\ndone"));
    }

    [Fact]
    public void Transpile_RunTimeEval_RunsInAnEvalLineScope()
    {
        var ps = T("echo a\neval \"$c\"");
        Assert.Contains("[PsBash.Cmdlets.VarFrameStack]::BeginEvalLines(); try { Invoke-Expression", ps);
        Assert.Contains(PsBuild.SetCallLine(2), ps);
    }

    [Fact]
    public void EvalLinesScope_EndsTheScopeEvenWhenTheStatementThrows()
        => Assert.Equal(
            "[PsBash.Cmdlets.VarFrameStack]::BeginEvalLines(); try { X } finally { [PsBash.Cmdlets.VarFrameStack]::EndEvalLines() }",
            PsBuild.EvalLinesScope("X"));

    [Fact]
    public void SourceLinesScope_EndsTheScopeEvenWhenTheFileThrows()
        => Assert.Equal(
            "[PsBash.Cmdlets.VarFrameStack]::BeginSourceLines(); try {\nX\n} finally { [PsBash.Cmdlets.VarFrameStack]::EndSourceLines() }",
            PsBuild.SourceLinesScope("X"));

    [Fact]
    public void ShellDiagnosticLine_BuildsThePrefixAtRunTime()
        => Assert.Equal("Write-Error -Message (" + PsBuild.DiagnosticPrefix + " + 'm') -ErrorAction Continue",
            PsBuild.ShellDiagnosticLine("'m'"));

    [Fact]
    public void Transpile_XtraceOfAnUnquotedVariable_TracesItsSplitFields()
    {
        // bash: `$u -c 'echo 2'` with u unset traces `+ -c 'echo 2'`: the trace gets the same field array the
        // command splats, so an empty word is no field (it traced `+ '' -c 'echo 2'`).
        var ps = T("set -x\n$u -c 'echo 2'");
        Assert.Contains("Write-BashXtrace (", ps);
        Assert.DoesNotContain("Write-BashXtrace ([string]($env:u", ps);
    }

    [Fact]
    public void Transpile_XtraceOfAQuotedVariable_IsOneField()
        // bash: `"$u" x` with u empty traces `+ '' x`.
        => Assert.Contains("Write-BashXtrace ([string](", T("set -x\n\"$u\" x"));

    [Fact]
    public void Transpile_XtraceOfAnAssignment_PassesNameAndValue()
        => Assert.Contains("Write-BashXtrace " + PsBuild.XtraceAssignment("v", "''"), T("set -x\nv="));

    [Fact]
    public void Transpile_XtraceOfACommandSubstitution_IsNotTraced()
        // Tracing would run the substitution twice.
        => Assert.DoesNotContain("Write-BashXtrace 'echo'", T("set -x\necho $(date)"));
}
