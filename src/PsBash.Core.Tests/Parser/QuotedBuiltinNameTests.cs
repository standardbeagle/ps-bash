using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// A quoted or backslash-escaped command name only escapes alias expansion in bash (5.2: <c>'local' v=3</c>,
/// <c>"eval" x</c>, <c>\export Q=1</c> run the builtin). REGRESSION: every builtin the emitter implements was looked up
/// as a program, <c>command not found</c> (127). The emitter's one primitive is <c>PsEmitter.UnquoteBuiltinName</c>,
/// run right after alias expansion. Behavior vs bash: Differential QuotedBuiltinNameDifferentialTests.
/// </summary>
public class QuotedBuiltinNameTests
{
    // A function definition carries its bash text for `declare -f` (PsBuild.FunctionSourceMark); the quoted and bare
    // spellings differ only there.
    private static string T(string bash)
        => System.Text.RegularExpressions.Regex.Replace(PsEmitter.Transpile(bash)!, "<#bash:[A-Za-z0-9+/=]*#> ", "");

    [Theory]
    [InlineData("'eval' 'echo ev'", "eval 'echo ev'")]
    [InlineData("\\eval 'echo ev'", "eval 'echo ev'")]
    [InlineData("\"set\" -- p q", "set -- p q")]
    [InlineData("'read' rv <<< rr", "read rv <<< rr")]
    [InlineData("\"declare\" -i di=2+3", "declare -i di=2+3")]
    [InlineData("f() { 'return' 6; }", "f() { return 6; }")]
    [InlineData("for i in 1 2; do 'break'; done", "for i in 1 2; do break; done")]
    [InlineData("for i in 1 2; do \"continue\"; done", "for i in 1 2; do continue; done")]
    [InlineData("( 'exit' 4 )", "( exit 4 )")]
    public void Transpile_QuotedBuiltinName_IsTheBareBuiltin(string quoted, string bare)
        => Assert.Equal(T(bare), T(quoted));

    // The declaration builtins are productions of the parser (BashParser.CommandWordName): the quoted spelling is the
    // same declaration as the bare one.
    [Theory]
    [InlineData("f() { 'local' v=3; }", "f() { local v=3; }")]
    [InlineData("f() { \\local v=3 w; }", "f() { local v=3 w; }")]
    [InlineData("f() { \"declare\" -i n=1; }", "f() { declare -i n=1; }")]
    [InlineData("\\export QX=1", "export QX=1")]
    [InlineData("'export' QX=1 QY", "export QX=1 QY")]
    [InlineData("command 'local' v", "command local v")]
    [InlineData("'declare' -a A=(x y)", "declare -a A=(x y)")]
    public void Transpile_QuotedDeclarationBuiltin_IsTheBareDeclaration(string quoted, string bare)
        => Assert.Equal(T(bare), T(quoted));

    [Theory]
    [InlineData("'local' v=3", "local", true)]
    [InlineData("\"typeset\" v=3", "typeset", true)]
    [InlineData("\\export v=3", null, false)]
    public void Parse_QuotedDeclarationBuiltin_IsAnAssignment(string decl, string? builtin, bool isLocal)
    {
        var fn = Assert.IsType<Command.ShFunction>(BashParser.Parse("f() { " + decl + "; }"));
        var body = Assert.IsType<Command.BraceGroup>(fn.Body).Body;
        var asg = Assert.IsType<Command.ShAssignment>(body is Command.CommandList list ? Assert.Single(list.Commands) : body);
        Assert.Equal(isLocal, asg.IsLocal);
        Assert.Equal(!isLocal, asg.IsExport);
        Assert.Equal(builtin, asg.Builtin);
    }

    // The quotes still escape alias expansion: an alias named like the builtin is not used (and the quoted name stays
    // quoted, so no re-entry into ExpandAliases sees a bare word).
    [Theory]
    [InlineData("'echo' hi")]
    [InlineData("\\echo hi")]
    [InlineData("\"echo\" hi")]
    public void Transpile_QuotedBuiltinName_SkipsTheAlias(string use)
        => Assert.DoesNotContain("Invoke-BashPrintf", T("shopt -s expand_aliases\nalias echo='printf A'\n" + use));

    [Fact]
    public void Transpile_BareName_StillTakesTheAlias()
        => Assert.Contains("Invoke-BashPrintf", T("shopt -s expand_aliases\nalias echo='printf A'\necho hi"));
}
