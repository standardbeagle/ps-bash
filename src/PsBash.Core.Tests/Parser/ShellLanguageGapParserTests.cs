using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// Parser and emitter layer of the shell-language gaps the coverage probe found (behavior against bash:
/// Differential.Tests/ShellLanguageGapDifferentialTests). Each case failed before its fix.
/// </summary>
public class ShellLanguageGapParserTests
{
    private static string Emit(string bash) => PsEmitter.Transpile(bash) ?? "";

    // --- `<>` read-write redirect -----------------------------------------------------------------

    [Fact]
    public void Lex_ReadWriteOperator_IsOneLessTokenWithItsFd()
    {
        var tokens = BashLexer.Tokenize("exec 5<> f");
        Assert.Contains(tokens, t => t.Kind == BashTokenKind.IoNumber && t.Value == "5");
        Assert.Contains(tokens, t => t.Kind == BashTokenKind.Less && t.Value == "<>");
    }

    [Fact]
    public void Parse_ExecReadWrite_IsAReadWriteRedirect()
    {
        var cmd = Assert.IsType<Command.Simple>(BashParser.Parse("exec 5<> f"));
        var r = Assert.Single(cmd.Redirects);
        Assert.Equal(5, r.Fd);
        Assert.True(r.ReadWrite);
        Assert.Equal("<>", r.OpenOperator);
    }

    [Fact]
    public void Transpile_ExecReadWrite_OpensTheDescriptorReadWrite()
        => Assert.Equal("Invoke-BashExecFd 5 '<>' f", Emit("exec 5<> f"));

    // --- select / coproc: not supported, said so -------------------------------------------------

    [Fact]
    public void Parse_SelectWithHereString_KeepsItsRedirect()
    {
        var sel = Assert.IsType<Command.Select>(BashParser.Parse("select x in a b; do echo $x; done <<< 2"));
        Assert.Single(sel.Redirects);
    }

    [Theory]
    [InlineData("select x in a b c; do echo $x; break; done <<< 2", "select")]
    [InlineData("coproc cat", "coproc")]
    public void Transpile_UnsupportedConstruct_SaysNotSupportedWithStatus2(string bash, string construct)
        => Assert.Equal(PsBuild.NotSupported(construct), Emit(bash));

    [Fact]
    public void Transpile_SelectThenMoreCommands_KeepsTheRest()
        => Assert.EndsWith("Invoke-BashEcho after", Emit("select x in a; do :; done; echo after"));

    // --- operators on special parameters ----------------------------------------------------------

    /// <summary>`${!:+pid}` was read as an indirect reference (`${!name}`) and emitted a broken command.</summary>
    [Theory]
    [InlineData("${!:+pid}", "!", ":+pid")]
    [InlineData("${$:-x}", "$", ":-x")]
    [InlineData("${?:+r}", "?", ":+r")]
    [InlineData("${-:+dash}", "-", ":+dash")]
    [InlineData("${#:-z}", "#", ":-z")]
    [InlineData("${#!}", "!", "#L")]
    public void Parse_OperatorOnSpecialParameter_IsThatParameter(string word, string name, string suffix)
    {
        var cmd = Assert.IsType<Command.Simple>(BashParser.Parse("echo " + word));
        var sub = Assert.IsType<WordPart.BracedVarSub>(Assert.Single(cmd.Words[1].Parts));
        Assert.Equal(name, sub.Name);
        Assert.Equal(suffix, sub.Suffix);
    }

    /// <summary>`$?` and `$#` are ints in PowerShell; 0 is falsy there, so `${?:-q}` gave q after a success.</summary>
    [Theory]
    [InlineData("echo ${?:-q}", "([string]$global:LASTEXITCODE)")]
    [InlineData("echo ${#:+n}", "([string]")]
    public void Transpile_ColonOperatorOnNumericSpecial_TestsItAsText(string bash, string fragment)
        => Assert.Contains(fragment, Emit(bash));

    public static TheoryData<string> ComputedScalars()
    {
        var data = new TheoryData<string>();
        foreach (string name in PsBash.Core.ShellVariables.Computed)
            if (PsBuild.ShellArrayExpr(name) is null)
                data.Add(name);
        return data;
    }

    /// <summary>
    /// Guard over the whole computed-scalar table: `${SECONDS:+y}` gave "" in the first second (0 is falsy in
    /// PowerShell), and `$RANDOM` can be 0. A name added to ShellVariables.Computed is covered without a new case.
    /// </summary>
    [Theory]
    [MemberData(nameof(ComputedScalars))]
    public void Transpile_ColonOperatorOnComputedScalar_TestsItAsText(string name)
        => Assert.Contains("([string]", Emit($"echo ${{{name}:+y}}"));

    // --- positional slices as unquoted words ------------------------------------------------------

    [Theory]
    [InlineData("echo ${@:2:2}")]
    [InlineData("echo ${*: -1}")]
    public void Transpile_UnquotedPositionalSlice_IsSplitIntoWords(string bash)
        => Assert.Contains("ConvertTo-BashWords", Emit(bash));

    // --- associative keys with blanks and expansions ----------------------------------------------

    [Fact]
    public void Parse_AssocLiteralKeyWithBlank_IsOneElement()
    {
        string ps = Emit("declare -A m=([x y]=1 [b]=2)");
        Assert.Contains("'x y', (\"1\")", ps);
        Assert.Contains("'b', (\"2\")", ps);
    }

    [Fact]
    public void Transpile_AssocLiteralExpandedKey_IsTheExpandedText()
        => Assert.Contains("([string](\"$env:j-1\"))", Emit("j=x; declare -A o=([$j-1]=e)"));

    /// <summary>`a=([$i]=x [2]=y)` became the words `[1]=x [2]=y`: an indexed subscript with an expansion is arithmetic.</summary>
    [Fact]
    public void Transpile_IndexedLiteralExpandedSubscript_IsArithmetic()
    {
        string ps = Emit("i=1; a=([$i]=x [2]=y)");
        Assert.Contains("BashSparseArray]::FromEntries([string](Invoke-BashArith ([string]([string](\"$env:i\")))), (\"x\"), '2'", ps);
        Assert.DoesNotContain("ConvertTo-BashWords", ps);
    }

    // `${?:-q}` tests the text; `${!-dash}` tests set-ness: the [string] cast would make an unset `$!` set and empty.
    [Fact]
    public void Transpile_PlainOperatorOnBang_TestsSetNess()
        => Assert.DoesNotContain("[string]$global:BashBgLastPid", Emit("echo ${!-dash}"));

    // --- trap DEBUG on one line -------------------------------------------------------------------

    [Theory]
    [InlineData("trap 'echo D' DEBUG; echo a", true)]
    [InlineData("trap 'echo D' DEBUG\necho a", true)]
    [InlineData("trap 'echo D' DEBUG", false)]           // a lone prompt hook keeps the chpwd mapping
    [InlineData("# c\ntrap \"x\" DEBUG ;\n\n", false)]
    [InlineData("echo trap DEBUG", false)]
    public void SetsDebugTrapForOtherCommands_CountsCommandsNotLines(string bash, bool expected)
        => Assert.Equal(expected, PsEmitter.SetsDebugTrapForOtherCommands(bash));

    // --- ERR trap scope ---------------------------------------------------------------------------

    /// <summary>A function body hides the caller's ERR trap on entry (ErrTrapInheritance via VarFrameStack).</summary>
    [Fact]
    public void Transpile_FunctionBody_EntersAndExitsTheErrTrapScope()
    {
        string ps = Emit("trap 'echo e' ERR; f() { false; }; f");
        Assert.Contains("[PsBash.Cmdlets.VarFrameStack]::EnterFunction($ExecutionContext.SessionState, $MyInvocation.MyCommand.Name)", ps);
        Assert.Contains("[PsBash.Cmdlets.VarFrameStack]::ExitFunction($ExecutionContext.SessionState, $__bet)", ps);
    }

    /// <summary>A substitution that sets its own ERR trap runs its statements' checks (`x=$(trap 'echo E' ERR; false)`
    /// captures E); one that does not stays exempt.</summary>
    [Theory]
    [InlineData("trap 'echo E' ERR; false", true)]
    [InlineData("{ trap \"x\" ERR; }; false", true)]
    [InlineData("trap - ERR; false", false)]
    [InlineData("( trap 'x' ERR ); false", false)]
    [InlineData("trap 'x' EXIT; false", false)]
    public void BodySetsErrTrap_OnlyItsOwnLevel(string body, bool expected)
        => Assert.Equal(expected, PsEmitter.BodySetsErrTrap(BashParser.Parse(body)));
}
