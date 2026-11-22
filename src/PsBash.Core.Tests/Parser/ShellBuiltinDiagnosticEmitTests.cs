using Xunit;
using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// The emitted shape of arithmetic and builtin diagnostics: the label bash puts on each arithmetic context, the line a
/// statement records so the shared prefix (<c>s.sh: line N: </c>) names it, and the line a compound command's redirect
/// error names (docs/specs/error-handling.md "Diagnostic prefix", "Redirect open errors"). bash 5.2 (WSL) is the oracle
/// for every expectation; end to end: Differential <c>ArithDiagnosticDifferentialTests</c>,
/// <c>BuiltinDiagnosticDifferentialTests</c>, <c>CompoundRedirectLineDifferentialTests</c>.
/// </summary>
public class ShellBuiltinDiagnosticEmitTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    // ── arithmetic labels ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Transpile_ArithExpansion_EvaluatesWithNoLabel()
    {
        // bash: `s.sh: line 1: 1/0 : division by 0 (error token is "0 ")` -- no label for $(( )).
        var ps = T("echo $(( 1/0 ))");
        Assert.Contains("Invoke-BashArith '1/0 ')", ps);
        Assert.DoesNotContain("-Label", ps);
    }

    [Fact]
    public void Transpile_DoubleBracketNumericOperand_EvaluatesWithTheDoubleBracketLabel()
        // bash: `[[: 1/0: division by 0 (error token is "0")`.
        => Assert.Contains("-Label '[['", T("[[ 1/0 -eq 1 ]]"));

    // ── the line a statement records ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("echo a\n(( 1/0 ))")]                      // ((: 1/0 : division by 0
    [InlineData("echo a\necho $(( 1/0 ))")]                // 1/0 : division by 0
    [InlineData("echo a\nx=$(( 2 + ))")]                   // 2 + : syntax error: operand expected
    [InlineData("echo a\n[[ 1/0 -eq 1 ]]")]                // [[: 1/0: division by 0
    [InlineData("echo a\necho ${a[i+1]}")]                 // a subscript is arithmetic: 1/0: division by 0
    [InlineData("echo a\n[ 1 -eq 1")]                      // [: missing `]'
    [InlineData("echo a\ncd /nonexist_zz")]                // cd: /nonexist_zz: No such file or directory
    [InlineData("echo a\ntype nosuchcmd_zz")]              // type: nosuchcmd_zz: not found
    [InlineData("echo a\ndeclare -Q")]                     // declare: -Q: invalid option
    [InlineData("echo a\nalias nosuchal_zz")]              // alias: nosuchal_zz: not found
    [InlineData("echo a\nwait %5")]                        // wait: %5: no such job
    [InlineData("echo a\nf() { local y=1; }")]             // local: y: readonly variable
    public void Transpile_StatementThatMayReportWithThePrefix_RecordsItsLine(string bash)
        => Assert.Contains(PsBuild.SetCallLine(2), T(bash));

    [Theory]
    [InlineData("(( 1/0 ))")]                              // line 1 is where every command starts
    [InlineData("echo a\necho \"${a[1]}\" \"${a[@]}\"")]   // a literal subscript evaluates nothing (unquoted, failglob may report)
    public void Transpile_StatementThatNeedsNoLine_RecordsNone(string bash)
        => Assert.DoesNotContain("VarFrameStack]::SetLine(", T(bash));

    [Fact]
    public void Transpile_ArithIfCondition_RecordsItsLineInsideTheCondition()
        // bash: `s.sh: line 2: ((: 1/0 : division by 0 (error token is "0 ")` -- it read line 1.
        => Assert.Contains("if ($(" + PsBuild.SetCallLine(2) + PsBuild.ConditionStatus(PsBuild.BoolStatus("(@(Invoke-BashArith '1/0 ' -Label '((') -ne 0)")) + "))",
            T("echo a\nif (( 1/0 )); then :; fi"));

    [Fact]
    public void Transpile_WhileCondition_RecordsItsLineEachTimeItIsTested()
    {
        // The body records line 3, then the condition fails: bash names line 2 (`[[: 1/0: division by 0`).
        var ps = T("echo a\nwhile [[ x -eq 1/0 ]]; do\n  cd /nonexist_zz\ndone");
        Assert.Contains("if (-not ($(" + PsBuild.SetCallLine(2), ps); // tested at the top of each pass (EmitWhileLoop)
        Assert.Contains(PsBuild.SetCallLine(3), ps);
    }

    [Fact]
    public void Transpile_ForArithClauses_EachRecordTheForsLine()
    {
        // bash: `s.sh: line 2: ((: i<1/0: division by 0 (error token is "0")` -- init, condition and step all name line 2.
        var ps = T("echo a\nfor (( i=0; i<1/0; i++ )); do\n  cd /nonexist_zz\ndone");
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(ps, System.Text.RegularExpressions.Regex.Escape(PsBuild.SetCallLine(2))).Count);
        // The init records whether it failed (a failed clause ends the loop, status 1).
        Assert.Contains("$__psbash_afe0 = @($(" + PsBuild.SetCallLine(2) + "Invoke-BashArith 'i=0' -Label '((')).Count -eq 0", ps);
    }

    // ── builtins report through the shared prefix ───────────────────────────────────────────────

    [Theory]
    [InlineData("cd a b", "'cd: too many arguments'")]
    [InlineData("builtin nosuch_zz", "'builtin: nosuch_zz: not a shell builtin'")]
    [InlineData("f() { local -p nov; }", "'local: nov: not found'")]    // no local declared yet: it ran a program `local`
    public void Transpile_BuiltinError_IsTheShellDiagnostic(string bash, string message)
        => Assert.Contains(PsBuild.ShellDiagnosticLine(message), T(bash));

    // ── a compound command's redirect error: the line bash names ────────────────────────────────

    [Theory]
    // The outermost statement's end line: `fi` / `done` / a here-document's last line.
    [InlineData("echo a\nif true; then\n  { :; } < nofile\nfi", 4)]
    [InlineData("echo a\nwhile read l; do\n  { :; } < nofile\ndone <<EOF\na\nEOF", 6)]
    [InlineData("echo a\necho z | while read l; do\n  { :; } < nofile\ndone", 4)]
    [InlineData("echo a\n(\n  { :; } < nofile\n)", 4)]
    // A for / case body names the for / case line.
    [InlineData("echo a\nfor i in 1; do\n  { :; } < nofile\ndone", 2)]
    [InlineData("echo a\ncase x in\n  x) { :; } < nofile ;;\nesac", 2)]
    public void Transpile_RedirectErrorInACompoundCommand_RecordsTheLineBashNames(string bash, int line)
        => Assert.Contains(PsBuild.SetCallLine(line) + "& { $__psbash_ir = [string](Write-Output nofile)", T(bash));

    // ── the parser stamps where a compound command ends ─────────────────────────────────────────

    [Theory]
    [InlineData("if true; then\n  :\nfi > out", 3)]
    [InlineData("while read l; do :; done <<EOF\na\nEOF", 3)]
    [InlineData("for i in 1; do\n  :\ndone", 3)]
    [InlineData("for ((;;)); do\n  :\ndone", 3)]
    [InlineData("case x in\n  x) ;;\nesac", 3)]
    [InlineData("(\n  :\n)", 3)]
    [InlineData("{ :; }", 1)]
    public void Parse_CompoundCommand_StampsItsEndLine(string bash, int endLine)
    {
        int actual = BashParser.Parse(bash) switch
        {
            Command.If c => c.EndLine,
            Command.While c => c.EndLine,
            Command.ForIn c => c.EndLine,
            Command.ForArith c => c.EndLine,
            Command.Case c => c.EndLine,
            Command.Subshell c => c.EndLine,
            Command.BraceGroup c => c.EndLine,
            var other => throw new Xunit.Sdk.XunitException("unexpected node " + other?.GetType().Name),
        };
        Assert.Equal(endLine, actual);
    }

    [Fact]
    public void Parse_BracketWithoutItsClose_KeepsItsLine()
    {
        // `[ 1 -eq 1` is the command `[` (status 2, "missing `]'"): it had line 0, so its report named an earlier line.
        var list = Assert.IsType<Command.CommandList>(BashParser.Parse("echo a\n[ 1 -eq 1"));
        Assert.Equal(2, Assert.IsType<Command.Simple>(list.Commands[1]).Line);
    }
}
