using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// A quoted or backslash-escaped command name only escapes alias expansion in bash (5.2: <c>'local' v=3</c>,
/// <c>"eval" x</c>, <c>\export Q=1</c> run the builtin). REGRESSION: every builtin the emitter implements was looked up
/// as a program, <c>command not found</c> (127). The emitter's one primitive is <c>PsEmitter.UnquoteBuiltinName</c>,
/// run right after alias expansion; a declaration builtin's operands stay ordinary words, as behind <c>builtin</c>
/// (<c>BashParser.QuotedDeclarationStaysACommand</c>). Behavior vs bash: Differential QuotedBuiltinNameDifferentialTests.
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

    // The declaration builtins are productions of the parser: with no expansion in an operand the quoted spelling is the
    // same declaration as the bare one, as behind a wrapper (`builtin local v=3`).
    [Theory]
    [InlineData("f() { 'local' v=3; }", "f() { local v=3; }")]
    [InlineData("f() { \\local v=3 w; }", "f() { local v=3 w; }")]
    [InlineData("f() { \"declare\" -i n=1; }", "f() { declare -i n=1; }")]
    [InlineData("\\export QX=1", "export QX=1")]
    [InlineData("'export' QX=1 QY", "export QX=1 QY")]
    [InlineData("command 'local' v", "command local v")]
    public void Transpile_QuotedDeclarationBuiltin_IsTheBareDeclaration(string quoted, string bare)
        => Assert.Equal(T(bare), T(quoted));

    // REGRESSION (review of the port): a quoted declaration builtin's operands are ORDINARY words in bash 5.2, as behind
    // `builtin`: with x='a b', `'export' v=$x` exports v=a and b. It took the bare declaration's path (v='a b').
    [Theory]
    [InlineData("x='a b'; 'export' v=$x", "x='a b'; builtin export v=$x")]
    [InlineData("x='a b'; f() { \\local w=$x; }", "x='a b'; f() { builtin local w=$x; }")]
    [InlineData("x='a b'; f() { \"declare\" d=$x; }", "x='a b'; f() { builtin declare d=$x; }")]
    [InlineData("x='a b'; 'readonly' r=$x", "x='a b'; builtin readonly r=$x")]
    [InlineData("'typeset' t=\"$x\"$y", "builtin typeset t=\"$x\"$y")]
    public void Transpile_QuotedDeclarationBuiltinWithAnExpansion_IsTheWrapperForm(string quoted, string wrapped)
        => Assert.Equal(T(wrapped), T(quoted));

    // A value expanded only inside double quotes is neither split nor globbed, so it is the bare declaration (no run-time
    // transpile per call: conda's activate script runs `\local cmd="${1-__missing__}"` on every prompt).
    [Theory]
    [InlineData("f() { \\local cmd=\"${1-__missing__}\"; }", "f() { local cmd=\"${1-__missing__}\"; }")]
    [InlineData("'typeset' t=\"$x\"", "typeset t=\"$x\"")]
    [InlineData("\\export v=\"a $x\" w", "export v=\"a $x\" w")]
    public void Transpile_QuotedDeclarationBuiltinWithAQuotedValue_IsTheBareDeclaration(string quoted, string bare)
        => Assert.Equal(T(bare), T(quoted));

    // REGRESSION (review of the port): a run-time `local` (a quoted builtin, a wrapper, `local "$x"`) assigned the
    // variable without the function's local frame, so it outlived the function. Its eval hands over `local 'm=a b*'`,
    // which is the declaration. Behavior: Differential QuotedBuiltinNameDifferentialTests.
    [Theory]
    [InlineData("f() { local 'm=a b*'; }", "f() { local m='a b*'; }")]
    [InlineData("f() { local -i 'n=1+2' k; }", "f() { local -i n='1+2' k; }")]
    [InlineData("f() { local \"s+=x y\"; }", "f() { local s+=\"x y\"; }")]
    public void Transpile_QuotedStaticLocalOperand_IsTheDeclaration(string quoted, string bare)
        => Assert.Equal(T(bare), T(quoted));

    [Theory]
    [InlineData("'export' v=$x")]
    [InlineData("\\local w=$x")]
    [InlineData("\"declare\" d=`echo a b`")]
    public void Parse_QuotedDeclarationBuiltinWithAnExpansion_IsAPlainCommand(string decl)
        => Assert.IsType<Command.Simple>(BashParser.Parse(decl));

    // bash 5.2: an array initializer needs the bare declaration builtin; behind quotes it is `syntax error near
    // unexpected token `('` (status 2), as behind `builtin`.
    [Theory]
    [InlineData("'declare' -a A=(x y)")]
    [InlineData("f() { 'local' a=(1 2); }")]
    [InlineData("\\export Q=(1)")]
    [InlineData("\"readonly\" C=(m n)")]
    public void Parse_QuotedDeclarationBuiltinWithAnArray_IsASyntaxError(string decl)
        => Assert.Throws<ParseException>(() => BashParser.Parse(decl));

    [Theory]
    [InlineData("'local' v=3", "local", true)]
    [InlineData("\"typeset\" v=3", "typeset", true)]
    [InlineData("\\export v=3", null, false)]
    public void Parse_QuotedDeclarationBuiltin_IsAnAssignment(string decl, string? builtin, bool isLocal)
    {
        var fn = Assert.IsType<Command.ShFunction>(BashParser.Parse("f() { " + decl + "; }"));
        // A one-command body with no redirect is the command itself (Parse_DeclareAssignmentInAFunction_IsLocalUnlessGlobalFlag).
        var body = fn.Body is Command.BraceGroup group ? group.Body : fn.Body;
        var asg = Assert.IsType<Command.ShAssignment>(body is Command.CommandList list ? Assert.Single(list.Commands) : body);
        Assert.Equal(isLocal, asg.IsLocal);
        Assert.Equal(!isLocal, asg.IsExport);
        Assert.Equal(builtin, asg.Builtin);
    }

    // The quotes still escape alias expansion: an alias named like the builtin is not used, also where EmitSimple re-enters
    // itself for part of the command (the bare word is PsEmitter.NoAliasWord). REGRESSION: an aliased name stayed quoted
    // instead, so `alias eval='echo no'; 'eval' 'echo x'` ran eval as a program.
    [Theory]
    [InlineData("echo", "'echo' hi")]
    [InlineData("echo", "\\echo hi")]
    [InlineData("echo", "\"echo\" hi")]
    [InlineData("echo", "v=1 'echo' hi > out")]
    [InlineData("eval", "'eval' 'echo x'")]
    [InlineData("eval", "\\eval \"$c\"")]
    [InlineData("local", "f() { 'local' z=5; }")]
    [InlineData("local", "x='a b'; f() { 'local' z=$x; }")]
    [InlineData("export", "x='a b'; builtin export z=$x")]
    public void Transpile_QuotedBuiltinName_SkipsTheAlias(string alias, string use)
        // The alias's expansion, not just printf: a run-time declaration's field quoting runs printf itself (and the
        // alias definition itself carries the text).
        => Assert.DoesNotContain("Invoke-BashPrintf ALIASED", T("shopt -s expand_aliases\nalias " + alias + "='printf ALIASED'\n" + use));

    [Theory]
    [InlineData("\\eval \"$c\"")]
    [InlineData("'eval' \"$c\"")]
    public void Transpile_AliasedQuotedEval_IsTheEvalBuiltin(string use)
        => Assert.Contains("if (-not [string]::IsNullOrWhiteSpace($__psbash_eval_src)) { ",
            T("shopt -s expand_aliases\nalias eval='printf A'\n" + use));

    [Fact]
    public void Transpile_BareName_StillTakesTheAlias()
        => Assert.Contains("Invoke-BashPrintf ALIASED hi", T("shopt -s expand_aliases\nalias echo='printf ALIASED'\necho hi"));
}
