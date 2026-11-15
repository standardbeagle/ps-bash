using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// The line scope of a discarding expansion error (PsBuild.DiscardLineScope), a backtick body that does not parse
/// (a run-time error), and a quoted declaration operand that is NAME=VALUE. Run-time behaviour is checked against bash
/// in Differential.Tests LineDiscardTests.
/// </summary>
public class LineDiscardEmitTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    private const string Enter = "[PsBash.Cmdlets.BashLineDiscard]::Enter()";

    [Fact]
    public void DiscardLineScope_CatchesOnlyADiscardAndMarksTheLine()
    {
        string scope = PsBuild.DiscardLineScope("echo x");
        Assert.StartsWith("try { " + Enter + "; echo x }", scope);
        Assert.Contains("catch [PsBash.Cmdlets.BashParamErrorException] { if (-not [PsBash.Cmdlets.BashLineDiscard]::Discards($_.Exception)) { throw }", scope);
        Assert.Contains("$global:__BashFailGlob = $true", scope);
        Assert.EndsWith("finally { [PsBash.Cmdlets.BashLineDiscard]::Leave() }", scope);
    }

    // A function defined on one line and called on another: every top-level command gets the scope, and the commands
    // after the call on its line are guarded by the mark the catch sets.
    [Fact]
    public void Transpile_IndirectInAFunction_ScopesEveryTopLevelCommand()
    {
        string ps = T("check() { r=\"${!1}\"; }\ncheck 'a[0'; echo same\necho next");
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(ps, System.Text.RegularExpressions.Regex.Escape(Enter)).Count);
        Assert.Contains("if (-not $global:__BashFailGlob) { try { " + Enter + "; Invoke-BashEcho same", ps);
        Assert.DoesNotContain("__BashFailGlob) { try { " + Enter + "; Invoke-BashEcho next", ps);
    }

    // `for ...; done; echo same`: the command after a compound one on its line is guarded too (the AST has no end line
    // for a loop; the parser records which top-level commands begin a line). A command after a newline is not.
    [Fact]
    public void Transpile_CommandAfterALoopOnItsLine_IsGuarded()
    {
        string ps = T("echo x\nfor i in 1 2; do echo ${x!y}; done; echo same\nwhile false; do :; done\necho next");
        Assert.Contains("if (-not $global:__BashFailGlob) { try { " + Enter + "; Invoke-BashEcho same", ps);
        Assert.DoesNotContain("__BashFailGlob) { try { " + Enter + "; Invoke-BashEcho next", ps);
        Assert.DoesNotContain("__BashFailGlob) { try { " + Enter + "; while", ps);
    }

    [Fact]
    public void Transpile_NothingThatCanDiscard_HasNoScope()
    {
        Assert.DoesNotContain("BashLineDiscard", T("f() { echo $1; }\nf a; echo b\necho c"));
        Assert.DoesNotContain("BashLineDiscard", T("set -u\necho $x; echo b"));
    }

    [Fact]
    public void Transpile_LoneCommandThatCanDiscard_IsScoped()
        => Assert.StartsWith("try { " + Enter, T("echo ${x!y}"));

    // bash -i -c: the launcher's prelude marks the shell interactive; under set -u every command gets the scope.
    [Fact]
    public void Transpile_InteractiveNounset_ScopesEveryTopLevelCommand()
    {
        string ps = T("shopt -s psbash_interactive; set -u; echo $x; echo after\necho line2");
        Assert.Contains("if (-not $global:__BashFailGlob) { try { " + Enter + "; Invoke-BashEcho after", ps);
        Assert.Contains("try { " + Enter + "; Invoke-BashEcho line2", ps);
        Assert.DoesNotContain("BashLineDiscard", T("shopt -s psbash_interactive; echo $x"));
    }

    // bash parses a backtick body when it runs: a syntax error in it is reported then (status 2), not a parse error
    // of the whole script. A $( ) body is parsed with the script.
    [Fact]
    public void Transpile_BacktickBodySyntaxError_IsARunTimeReport()
    {
        string ps = T("echo `echo \\\\\\\\\"`; echo after");
        Assert.Contains("bash: command substitution: line 1: unexpected EOF while looking for matching", ps);
        Assert.Contains("Invoke-BashEcho after", ps);
        Assert.Throws<ParseException>(() => T("echo $(echo \")"));
    }

    [Theory]
    [InlineData("\"x=v\"", "x", AssignOp.Equal)]
    [InlineData("'x=$y'", "x", AssignOp.Equal)]
    [InlineData("\"x\"=v", "x", AssignOp.Equal)]
    [InlineData("\"x=$y\"", "x", AssignOp.Equal)]
    [InlineData("'x+=a b'", "x", AssignOp.PlusEqual)]
    public void QuotedDeclarationAssignment_QuotedNameValue_IsTheAssignment(string raw, string name, AssignOp op)
    {
        var pair = BashParser.QuotedDeclarationAssignment(raw);
        Assert.NotNull(pair);
        Assert.Equal(name, pair!.Name);
        Assert.Equal(op, pair.Op);
    }

    // Not an assignment after quote removal, or a value whose unquoted part bash would split, glob or tilde-expand
    // differently as an ordinary word.
    [Theory]
    [InlineData("\"a b=1\"")]
    [InlineData("\"x\"")]
    [InlineData("\"x=\"$y")]
    [InlineData("\"x=\"*")]
    [InlineData("\"x=\"~")]
    [InlineData("\"$n=1\"")]
    public void QuotedDeclarationAssignment_OtherWords_AreNot(string raw)
        => Assert.Null(BashParser.QuotedDeclarationAssignment(raw));

    [Fact]
    public void Transpile_QuotedLocalAssignment_DeclaresALocal()
    {
        string ps = T("f() { local \"lv=inner\"; }");
        Assert.Contains("DeclareLocal('lv'", ps);
        Assert.DoesNotContain("Set-Item -LiteralPath ('Env:'", ps);
    }
}
