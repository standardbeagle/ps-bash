using System.Collections.Immutable;
using Xunit;
using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Tests.Parser;

public class PsEmitterTests
{
    [Fact]
    public void Emit_SimpleCommand_EchoHello_Passthrough()
    {
        var cmd = new Command.Simple(
            ImmutableArray.Create(
                MakeWord("echo"),
                MakeWord("hello")),
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty);

        var result = PsEmitter.Emit(cmd);

        Assert.Equal("echo hello", result);
    }

    [Fact]
    public void Emit_SimpleCommand_WithEnvPair_EmitsPsEnvAssignment()
    {
        var cmd = new Command.Simple(
            ImmutableArray.Create(MakeWord("cmd")),
            ImmutableArray.Create(new EnvPair("FOO", MakeWord("bar"))),
            ImmutableArray<Redirect>.Empty);

        var result = PsEmitter.Emit(cmd);

        Assert.Equal("$env:FOO = \"bar\"; cmd", result);
    }

    [Fact]
    public void Emit_SimpleCommand_WithNullEnvPairValue_EmitsEmptyString()
    {
        var cmd = new Command.Simple(
            ImmutableArray.Create(MakeWord("cmd")),
            ImmutableArray.Create(new EnvPair("FOO", null)),
            ImmutableArray<Redirect>.Empty);

        var result = PsEmitter.Emit(cmd);

        Assert.Equal("$env:FOO = \"\"; cmd", result);
    }

    [Fact]
    public void Emit_SimpleCommand_MultipleWords()
    {
        var cmd = new Command.Simple(
            ImmutableArray.Create(
                MakeWord("ls"),
                MakeWord("-la"),
                MakeWord("/tmp")),
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty);

        var result = PsEmitter.Emit(cmd);

        Assert.Equal("ls -la /tmp", result);
    }

    [Fact]
    public void Transpile_LsPipeGrep_EmitsMappedPipeline()
    {
        var result = PsEmitter.Transpile("ls | grep foo");

        Assert.Equal("ls | Invoke-BashGrep foo", result);
    }

    [Fact]
    public void Transpile_CatPipeHeadPipeSort_EmitsMultiStagePipeline()
    {
        var result = PsEmitter.Transpile("cat file | head -n 5 | sort");

        Assert.Equal("cat file | Invoke-BashHead -n 5 | Invoke-BashSort", result);
    }

    [Fact]
    public void Transpile_PipeAmpersand_EmitsStderrMerge()
    {
        var result = PsEmitter.Transpile("cmd |& other");

        Assert.Equal("cmd 2>&1 | other", result);
    }

    [Fact]
    public void Transpile_EchoPipeWcL_EmitsMeasureObject()
    {
        var result = PsEmitter.Transpile("echo hello | wc -l");

        Assert.Equal("echo hello | Invoke-BashWc -l", result);
    }

    [Fact]
    public void Emit_AndOrList_EmitsPassthrough()
    {
        var andOr = new Command.AndOrList(
            ImmutableArray.Create<Command>(
                new Command.Simple(
                    ImmutableArray.Create(MakeWord("cmd1")),
                    ImmutableArray<EnvPair>.Empty,
                    ImmutableArray<Redirect>.Empty),
                new Command.Simple(
                    ImmutableArray.Create(MakeWord("cmd2")),
                    ImmutableArray<EnvPair>.Empty,
                    ImmutableArray<Redirect>.Empty)),
            ImmutableArray.Create("&&"));

        var result = PsEmitter.Emit(andOr);

        Assert.Equal("cmd1 && cmd2", result);
    }

    [Fact]
    public void Emit_CommandList_SingleCommand_EmitsCommand()
    {
        var list = new Command.CommandList(
            ImmutableArray.Create<Command>(
                new Command.Simple(
                    ImmutableArray.Create(MakeWord("echo")),
                    ImmutableArray<EnvPair>.Empty,
                    ImmutableArray<Redirect>.Empty)));

        var result = PsEmitter.Emit(list);

        Assert.Equal("echo", result);
    }

    [Fact]
    public void Emit_ShAssignment_EmitsEnvAssignment()
    {
        var assignment = new Command.ShAssignment(
            ImmutableArray.Create(
                new Assignment("x", AssignOp.Equal, MakeWord("1"))));

        var result = PsEmitter.Emit(assignment);

        Assert.Equal("$env:x = \"1\"", result);
    }

    [Fact]
    public void Transpile_ExportFooBar_EmitsEnvAssignment()
    {
        var result = PsEmitter.Transpile("export FOO=bar");

        Assert.Equal("$env:FOO = \"bar\"", result);
    }

    [Fact]
    public void Transpile_ExportFooQuotedValue_EmitsEnvAssignment()
    {
        var result = PsEmitter.Transpile("export FOO=\"hello world\"");

        Assert.Equal("$env:FOO = \"hello world\"", result);
    }

    [Fact]
    public void Transpile_BareAssignment_EmitsEnvAssignment()
    {
        var result = PsEmitter.Transpile("FOO=bar");

        Assert.Equal("$env:FOO = \"bar\"", result);
    }

    [Fact]
    public void Transpile_AssignmentWithCommand_EmitsEnvPrefix()
    {
        var result = PsEmitter.Transpile("FOO=bar baz");

        Assert.Equal("$env:FOO = \"bar\"; baz", result);
    }

    [Fact]
    public void Transpile_MultipleAssignmentsWithCommand_EmitsEnvPairs()
    {
        var result = PsEmitter.Transpile("FOO=1 BAR=2 cmd");

        Assert.Equal("$env:FOO = \"1\"; $env:BAR = \"2\"; cmd", result);
    }

    [Fact]
    public void Transpile_ExportPathWithExpansion_EmitsCorrectExpansion()
    {
        var result = PsEmitter.Transpile("export PATH=\"$PATH:/new\"");

        Assert.Equal("$env:PATH = \"$env:PATH:/new\"", result);
    }

    [Fact]
    public void Transpile_EchoHello_ReturnsPassthrough()
    {
        var result = PsEmitter.Transpile("echo hello");

        Assert.Equal("echo hello", result);
    }

    [Fact]
    public void Transpile_EmptyInput_ReturnsNull()
    {
        var result = PsEmitter.Transpile("");

        Assert.Null(result);
    }

    [Fact]
    public void Transpile_WhitespaceOnly_ReturnsNull()
    {
        var result = PsEmitter.Transpile("   \t  ");

        Assert.Null(result);
    }

    [Fact]
    public void Transpile_SingleWord_ReturnsPassthrough()
    {
        var result = PsEmitter.Transpile("ls");

        Assert.Equal("ls", result);
    }

    [Fact]
    public void Transpile_MultipleWords_ReturnsPassthrough()
    {
        var result = PsEmitter.Transpile("git commit -m \"message\"");

        Assert.Equal("git commit -m \"message\"", result);
    }

    [Fact]
    public void Emit_SimpleCommand_MultipleEnvPairs()
    {
        var cmd = new Command.Simple(
            ImmutableArray.Create(MakeWord("cmd")),
            ImmutableArray.Create(
                new EnvPair("FOO", MakeWord("bar")),
                new EnvPair("BAZ", MakeWord("qux"))),
            ImmutableArray<Redirect>.Empty);

        var result = PsEmitter.Emit(cmd);

        Assert.Equal("$env:FOO = \"bar\"; $env:BAZ = \"qux\"; cmd", result);
    }

    [Fact]
    public void Transpile_SingleQuoted_PassthroughInSingleQuotes()
    {
        var result = PsEmitter.Transpile("echo 'hello world'");

        Assert.Equal("echo 'hello world'", result);
    }

    [Fact]
    public void Transpile_DoubleQuotedWithVar_EmitsEnvVar()
    {
        var result = PsEmitter.Transpile("echo \"hello $USER\"");

        Assert.Equal("echo \"hello $env:USER\"", result);
    }

    [Fact]
    public void Transpile_BackslashEscape_EmitsBactickEscape()
    {
        var result = PsEmitter.Transpile("echo hello\\ world");

        Assert.Equal("echo hello` world", result);
    }

    [Fact]
    public void Transpile_DoubleQuotedWithApostrophe_Preserved()
    {
        var result = PsEmitter.Transpile("echo \"it's fine\"");

        Assert.Equal("echo \"it's fine\"", result);
    }

    [Fact]
    public void Transpile_SingleQuotedWithDoubleQuotes_Preserved()
    {
        var result = PsEmitter.Transpile("echo 'say \"hi\"'");

        Assert.Equal("echo 'say \"hi\"'", result);
    }

    [Fact]
    public void Emit_SimpleVarSub_PsBuiltin_SkipsEnvPrefix()
    {
        var cmd = new Command.Simple(
            ImmutableArray.Create(
                MakeWord("echo"),
                new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.SimpleVarSub("null")))),
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty);

        var result = PsEmitter.Emit(cmd);

        Assert.Equal("echo $null", result);
    }

    [Fact]
    public void Emit_EscapedLiteral_EmitsBactick()
    {
        var cmd = new Command.Simple(
            ImmutableArray.Create(
                MakeWord("echo"),
                new CompoundWord(ImmutableArray.Create<WordPart>(
                    new WordPart.Literal("hello"),
                    new WordPart.EscapedLiteral(" "),
                    new WordPart.Literal("world")))),
            ImmutableArray<EnvPair>.Empty,
            ImmutableArray<Redirect>.Empty);

        var result = PsEmitter.Emit(cmd);

        Assert.Equal("echo hello` world", result);
    }

    [Fact]
    public void Transpile_OutputRedirectToFile_Passthrough()
    {
        var result = PsEmitter.Transpile("cmd > file");

        Assert.Equal("cmd >file", result);
    }

    [Fact]
    public void Transpile_AppendRedirectToFile_Passthrough()
    {
        var result = PsEmitter.Transpile("cmd >> file");

        Assert.Equal("cmd >>file", result);
    }

    [Fact]
    public void Transpile_StderrToDevNull_EmitsNullTarget()
    {
        var result = PsEmitter.Transpile("cmd 2> /dev/null");

        Assert.Equal("cmd 2>$null", result);
    }

    [Fact]
    public void Transpile_OutputToDevNullWithStderrMerge_EmitsBoth()
    {
        var result = PsEmitter.Transpile("cmd > /dev/null 2>&1");

        Assert.Equal("cmd >$null 2>&1", result);
    }

    [Fact]
    public void Transpile_InputRedirect_EmitsGetContent()
    {
        var result = PsEmitter.Transpile("cmd < input.txt");

        Assert.Equal("Get-Content input.txt | cmd", result);
    }

    [Fact]
    public void Transpile_StderrToStdout_Passthrough()
    {
        var result = PsEmitter.Transpile("cmd 2>&1");

        Assert.Equal("cmd 2>&1", result);
    }

    [Fact]
    public void Transpile_IoNumber3_EmitsFdPrefix()
    {
        var result = PsEmitter.Transpile("cmd 3> file");

        Assert.Equal("cmd 3>file", result);
    }

    [Fact]
    public void Transpile_RedirectToTmpPath_TransformsTempEnv()
    {
        var result = PsEmitter.Transpile("cmd > /tmp/out.log");

        Assert.Equal("cmd >$env:TEMP\\out.log", result);
    }

    [Fact]
    public void Transpile_MkdirAndCd_Passthrough()
    {
        var result = PsEmitter.Transpile("mkdir dir && cd dir");

        Assert.Equal("mkdir dir && cd dir", result);
    }

    [Fact]
    public void Transpile_TestOrEcho_Passthrough()
    {
        var result = PsEmitter.Transpile("test -f file || echo missing");

        Assert.Equal("test -f file || echo missing", result);
    }

    [Fact]
    public void Transpile_ThreeCommandAndOrList_CorrectPrecedence()
    {
        var result = PsEmitter.Transpile("cmd1 && cmd2 || cmd3");

        Assert.Equal("cmd1 && cmd2 || cmd3", result);
    }

    [Fact]
    public void Emit_AndOrList_OrIf_EmitsPassthrough()
    {
        var andOr = new Command.AndOrList(
            ImmutableArray.Create<Command>(
                new Command.Simple(
                    ImmutableArray.Create(MakeWord("test"), MakeWord("-f"), MakeWord("file")),
                    ImmutableArray<EnvPair>.Empty,
                    ImmutableArray<Redirect>.Empty),
                new Command.Simple(
                    ImmutableArray.Create(MakeWord("echo"), MakeWord("missing")),
                    ImmutableArray<EnvPair>.Empty,
                    ImmutableArray<Redirect>.Empty)),
            ImmutableArray.Create("||"));

        var result = PsEmitter.Emit(andOr);

        Assert.Equal("test -f file || echo missing", result);
    }

    [Fact]
    public void Transpile_EchoHome_PsBuiltinPassthrough()
    {
        var result = PsEmitter.Transpile("echo $HOME");

        Assert.Equal("echo $HOME", result);
    }

    [Fact]
    public void Transpile_EchoFoo_EmitsEnvVar()
    {
        var result = PsEmitter.Transpile("echo $FOO");

        Assert.Equal("echo $env:FOO", result);
    }

    [Fact]
    public void Transpile_BracedVar_EmitsEnvVar()
    {
        var result = PsEmitter.Transpile("echo ${PATH}");

        Assert.Equal("echo $env:PATH", result);
    }

    [Fact]
    public void Transpile_BracedVarWithDefault_EmitsNullCoalescing()
    {
        var result = PsEmitter.Transpile("echo ${VAR:-fallback}");

        Assert.Equal("echo ($env:VAR ?? \"fallback\")", result);
    }

    [Fact]
    public void Transpile_SpecialVarQuestionMark_EmitsLastExitCode()
    {
        var result = PsEmitter.Transpile("echo $?");

        Assert.Equal("echo $LASTEXITCODE", result);
    }

    [Fact]
    public void Transpile_BracedVarLength_EmitsLength()
    {
        var result = PsEmitter.Transpile("echo ${#VAR}");

        Assert.Equal("echo $env:VAR.Length", result);
    }

    [Fact]
    public void Transpile_SpecialVarAt_EmitsArgs()
    {
        var result = PsEmitter.Transpile("echo $@");

        Assert.Equal("echo $args", result);
    }

    [Fact]
    public void Transpile_SpecialVarHash_EmitsArgsCount()
    {
        var result = PsEmitter.Transpile("echo $#");

        Assert.Equal("echo $args.Count", result);
    }

    [Fact]
    public void Transpile_SpecialVarDollarDollar_EmitsPid()
    {
        var result = PsEmitter.Transpile("echo $$");

        Assert.Equal("echo $PID", result);
    }

    [Fact]
    public void Transpile_PositionalVar1_EmitsArgsIndex()
    {
        var result = PsEmitter.Transpile("echo $1");

        Assert.Equal("echo $args[0]", result);
    }

    [Fact]
    public void Transpile_PositionalVar9_EmitsArgsIndex()
    {
        var result = PsEmitter.Transpile("echo $9");

        Assert.Equal("echo $args[8]", result);
    }

    [Fact]
    public void Transpile_SpecialVar0_EmitsMyCommand()
    {
        var result = PsEmitter.Transpile("echo $0");

        Assert.Equal("echo $MyInvocation.MyCommand.Name", result);
    }

    [Fact]
    public void Transpile_BracedVarAssignDefault_EmitsNullCoalescingAssign()
    {
        var result = PsEmitter.Transpile("echo ${VAR:=default}");

        Assert.Equal("echo ($env:VAR ?? ($env:VAR = \"default\"))", result);
    }

    [Fact]
    public void Transpile_BracedVarAlternative_EmitsConditional()
    {
        var result = PsEmitter.Transpile("echo ${VAR:+yes}");

        Assert.Equal("echo ($env:VAR ? \"yes\" : \"\")", result);
    }

    [Fact]
    public void Transpile_BracedVarError_EmitsThrow()
    {
        var result = PsEmitter.Transpile("echo ${VAR:?error msg}");

        Assert.Equal("echo ($env:VAR ?? $(throw \"error msg\"))", result);
    }

    [Fact]
    public void Transpile_BracedVarSuffixRemoval_EmitsReplace()
    {
        var result = PsEmitter.Transpile("echo ${VAR%%pattern}");

        Assert.Equal("echo ($env:VAR -replace 'pattern$','')", result);
    }

    [Fact]
    public void Transpile_BracedVarPrefixRemoval_EmitsReplace()
    {
        var result = PsEmitter.Transpile("echo ${VAR##pattern}");

        Assert.Equal("echo ($env:VAR -replace '^pattern','')", result);
    }

    [Fact]
    public void Transpile_BracedVarInsideDoubleQuotes_EmitsEnvVar()
    {
        var result = PsEmitter.Transpile("echo \"${USER}\"");

        Assert.Equal("echo \"$env:USER\"", result);
    }

    [Fact]
    public void Transpile_SpecialVarStar_EmitsArgs()
    {
        var result = PsEmitter.Transpile("echo $*");

        Assert.Equal("echo $args", result);
    }

    [Fact]
    public void Transpile_BracedVarHomePsBuiltin_EmitsHomeDirect()
    {
        var result = PsEmitter.Transpile("echo ${HOME}");

        Assert.Equal("echo $HOME", result);
    }

    [Fact]
    public void Transpile_CommandSub_SimpleCommand_Passthrough()
    {
        var result = PsEmitter.Transpile("echo $(whoami)");

        Assert.Equal("echo $(Invoke-BashWhoami)", result);
    }

    [Fact]
    public void Transpile_CommandSub_InnerPipeline_TranspilesInnerCommands()
    {
        var result = PsEmitter.Transpile("echo $(ls | grep foo)");

        Assert.Equal("echo $(ls | Invoke-BashGrep foo)", result);
    }

    [Fact]
    public void Transpile_BacktickCommandSub_NormalizedToDollarParen()
    {
        var result = PsEmitter.Transpile("echo `date`");

        Assert.Equal("echo $(Invoke-BashDate)", result);
    }

    [Fact]
    public void Transpile_AssignmentWithCommandSub_EmitsEnvAssignment()
    {
        var result = PsEmitter.Transpile("VAR=$(cat file)");

        Assert.Equal("$env:VAR = \"$(cat file)\"", result);
    }

    [Fact]
    public void Transpile_NestedCommandSub_EmitsCorrectNesting()
    {
        var result = PsEmitter.Transpile("echo $(echo $(whoami))");

        Assert.Equal("echo $(echo $(Invoke-BashWhoami))", result);
    }

    [Fact]
    public void Transpile_TildePathDocs_EmitsHomePath()
    {
        var result = PsEmitter.Transpile("ls ~/docs");

        Assert.Equal("ls $HOME\\docs", result);
    }

    [Fact]
    public void Transpile_TmpPath_EmitsTempEnv()
    {
        var result = PsEmitter.Transpile("cat /tmp/log.txt");

        Assert.Equal("cat $env:TEMP\\log.txt", result);
    }

    [Fact]
    public void Transpile_DevNullAsArgument_EmitsNull()
    {
        var result = PsEmitter.Transpile("echo /dev/null");

        Assert.Equal("echo $null", result);
    }

    [Fact]
    public void Transpile_SemicolonTwoCommands_EmitsCommandList()
    {
        var result = PsEmitter.Transpile("echo a; echo b");

        Assert.Equal("echo a; echo b", result);
    }

    [Fact]
    public void Transpile_SemicolonThreeCommands_EmitsCommandList()
    {
        var result = PsEmitter.Transpile("echo a; echo b; echo c");

        Assert.Equal("echo a; echo b; echo c", result);
    }

    [Fact]
    public void Transpile_BareTilde_EmitsHome()
    {
        var result = PsEmitter.Transpile("cd ~");

        Assert.Equal("cd $HOME", result);
    }

    [Fact]
    public void Transpile_TildeNestedPath_EmitsHomePath()
    {
        var result = PsEmitter.Transpile("ls ~/.config/app");

        Assert.Equal("ls $HOME\\.config/app", result);
    }

    [Fact]
    public void Transpile_TildeUser_Passthrough()
    {
        var result = PsEmitter.Transpile("ls ~bob/docs");

        Assert.Equal("ls ~bob\\docs", result);
    }

    [Fact]
    public void Transpile_TrailingSemicolon_EmitsSingleCommand()
    {
        var result = PsEmitter.Transpile("echo a;");

        Assert.Equal("echo a", result);
    }

    [Fact]
    public void Transpile_IfThenFi_EmitsIfBlock()
    {
        var result = PsEmitter.Transpile("if cmd; then echo yes; fi");

        Assert.Equal("if (cmd) { echo yes }", result);
    }

    [Fact]
    public void Transpile_IfThenElseFi_EmitsIfElseBlock()
    {
        var result = PsEmitter.Transpile("if cmd; then a; else b; fi");

        Assert.Equal("if (cmd) { a } else { b }", result);
    }

    [Fact]
    public void Transpile_IfElifElseFi_EmitsFullChain()
    {
        var result = PsEmitter.Transpile("if cmd1; then a; elif cmd2; then b; else c; fi");

        Assert.Equal("if (cmd1) { a } elseif (cmd2) { b } else { c }", result);
    }

    [Fact]
    public void Transpile_IfFileTest_EmitsTestPath()
    {
        var result = PsEmitter.Transpile("if [ -f file ]; then echo yes; fi");

        Assert.Equal("if ((Test-Path \"file\" -PathType Leaf)) { echo yes }", result);
    }

    [Fact]
    public void Transpile_NestedIf_EmitsNestedBlocks()
    {
        var result = PsEmitter.Transpile("if cmd1; then if cmd2; then inner; fi; fi");

        Assert.Equal("if (cmd1) { if (cmd2) { inner } }", result);
    }

    [Fact]
    public void Transpile_IfDirTest_EmitsTestPathContainer()
    {
        var result = PsEmitter.Transpile("if [ -d dir ]; then echo yes; fi");

        Assert.Equal("if ((Test-Path \"dir\" -PathType Container)) { echo yes }", result);
    }

    [Fact]
    public void Transpile_IfWithMultipleBodyCommands_EmitsAll()
    {
        var result = PsEmitter.Transpile("if cmd; then a; b; fi");

        Assert.Equal("if (cmd) { a; b }", result);
    }

    [Fact]
    public void Transpile_StandaloneFileTest_EmitsTestPath()
    {
        var result = PsEmitter.Transpile("[ -f file ]");

        Assert.Equal("(Test-Path \"file\" -PathType Leaf)", result);
    }

    [Fact]
    public void Transpile_StandaloneDirTest_EmitsTestPathContainer()
    {
        var result = PsEmitter.Transpile("[ -d dir ]");

        Assert.Equal("(Test-Path \"dir\" -PathType Container)", result);
    }

    [Fact]
    public void Transpile_StandaloneFileTestWithAnd_EmitsVoidWrapped()
    {
        var result = PsEmitter.Transpile("[ -f file ] && echo yes");

        Assert.Equal("[void](Test-Path \"file\" -PathType Leaf) && echo yes", result);
    }

    [Fact]
    public void Transpile_StandaloneZeroLengthTest_EmitsIsNullOrEmpty()
    {
        var result = PsEmitter.Transpile("[ -z \"$VAR\" ]");

        Assert.Equal("([string]::IsNullOrEmpty($env:VAR))", result);
    }

    [Fact]
    public void Transpile_StandaloneNonEmptyTest_EmitsNegatedIsNullOrEmpty()
    {
        var result = PsEmitter.Transpile("[ -n \"$VAR\" ]");

        Assert.Equal("(-not [string]::IsNullOrEmpty($env:VAR))", result);
    }

    [Fact]
    public void Transpile_ExtendedFileTest_EmitsTestPath()
    {
        var result = PsEmitter.Transpile("[[ -f file ]]");

        Assert.Equal("(Test-Path \"file\" -PathType Leaf)", result);
    }

    [Fact]
    public void Transpile_ExtendedStringEquals_EmitsEq()
    {
        var result = PsEmitter.Transpile("[[ $var == \"foo\" ]]");

        Assert.Equal("($env:var -eq \"foo\")", result);
    }

    [Fact]
    public void Transpile_ExtendedIntComparison_EmitsOp()
    {
        var result = PsEmitter.Transpile("[[ $a -eq $b ]]");

        Assert.Equal("($env:a -eq $env:b)", result);
    }

    [Fact]
    public void Transpile_ExtendedRegex_EmitsMatch()
    {
        var result = PsEmitter.Transpile("[[ $a =~ ^[0-9]+$ ]]");

        Assert.Equal("($env:a -match '^[0-9]+$')", result);
    }

    [Fact]
    public void Transpile_ExtendedGlob_EmitsLike()
    {
        var result = PsEmitter.Transpile("[[ $a == foo* ]]");

        Assert.Equal("($env:a -like 'foo*')", result);
    }

    [Fact]
    public void Transpile_ExtendedLogicalAnd_EmitsAndOp()
    {
        var result = PsEmitter.Transpile("[[ -f file && -d dir ]]");

        Assert.Equal("(Test-Path \"file\" -PathType Leaf -and Test-Path \"dir\" -PathType Container)", result);
    }

    [Fact]
    public void Transpile_ExtendedLogicalOr_EmitsOrOp()
    {
        var result = PsEmitter.Transpile("[[ $a == \"x\" || $b == \"y\" ]]");

        Assert.Equal("($env:a -eq \"x\" -or $env:b -eq \"y\")", result);
    }

    [Fact]
    public void Transpile_ExtendedNotEquals_EmitsNe()
    {
        var result = PsEmitter.Transpile("[[ $a != \"bar\" ]]");

        Assert.Equal("($env:a -ne \"bar\")", result);
    }

    [Fact]
    public void Transpile_ExtendedLessThan_EmitsLt()
    {
        var result = PsEmitter.Transpile("[[ $a < $b ]]");

        Assert.Equal("($env:a -lt $env:b)", result);
    }

    [Fact]
    public void Transpile_ExtendedGreaterThan_EmitsGt()
    {
        var result = PsEmitter.Transpile("[[ $a > $b ]]");

        Assert.Equal("($env:a -gt $env:b)", result);
    }

    [Fact]
    public void Transpile_StandaloneFileTestWithOr_EmitsVoidWrapped()
    {
        var result = PsEmitter.Transpile("[ -f file ] || echo no");

        Assert.Equal("[void](Test-Path \"file\" -PathType Leaf) || echo no", result);
    }

    [Fact]
    public void Transpile_ForInWords_EmitsForeach()
    {
        var result = PsEmitter.Transpile("for x in a b c; do echo $x; done");

        Assert.Equal("foreach ($x in 'a','b','c') { echo $x }", result);
    }

    [Fact]
    public void Transpile_ForInNumbers_EmitsForeach()
    {
        var result = PsEmitter.Transpile("for i in 1 2 3; do echo $i; done");

        Assert.Equal("foreach ($i in 1,2,3) { echo $i }", result);
    }

    [Fact]
    public void Transpile_ForInGlob_EmitsResolvePath()
    {
        var result = PsEmitter.Transpile("for f in *.txt; do cat $f; done");

        Assert.Equal("foreach ($f in (Resolve-Path *.txt)) { cat $f }", result);
    }

    [Fact]
    public void Transpile_ForImplicitArgs_EmitsArgsIteration()
    {
        var result = PsEmitter.Transpile("for x; do echo $x; done");

        Assert.Equal("foreach ($x in $args) { echo $x }", result);
    }

    [Fact]
    public void Transpile_ForArith_EmitsCStyleFor()
    {
        var result = PsEmitter.Transpile("for ((i=0; i<10; i++)); do echo $i; done");

        Assert.Equal("for ($i = 0; $i -lt 10; $i++) { echo $i }", result);
    }

    [Fact]
    public void Transpile_ForIn_LoopVarNotEnvVar()
    {
        var result = PsEmitter.Transpile("for i in 1 2 3; do echo $i; done");

        Assert.Contains("$i", result);
        Assert.DoesNotContain("$env:i", result);
    }

    [Fact]
    public void Transpile_ForIn_SimilarVarNameNotClobbered()
    {
        var result = PsEmitter.Transpile("for i in 1 2; do echo $idx $i; done");

        Assert.Contains("$env:idx", result);
        Assert.Contains("echo $env:idx $i", result);
    }

    [Fact]
    public void Transpile_WhileTrue_EmitsWhileLoop()
    {
        var result = PsEmitter.Transpile("while true; do echo hi; done");

        Assert.Equal("while ($true) { echo hi }", result);
    }

    [Fact]
    public void Transpile_WhileCmd_EmitsWhileLoop()
    {
        var result = PsEmitter.Transpile("while cmd; do body; done");

        Assert.Equal("while (cmd) { body }", result);
    }

    [Fact]
    public void Transpile_UntilCmd_EmitsNegatedWhileLoop()
    {
        var result = PsEmitter.Transpile("until cmd; do body; done");

        Assert.Equal("while (-not (cmd)) { body }", result);
    }

    [Fact]
    public void Transpile_WhileReadLine_EmitsForEachObjectPipeline()
    {
        var result = PsEmitter.Transpile("while read line; do echo $line; done");

        Assert.Equal(
            "ForEach-Object { if ($_.PSObject.Properties['BashText']) { $_.BashText } else { \"$_\" } } | ForEach-Object { $_ -split \"`n\" } | ForEach-Object { echo $_ }",
            result);
    }

    [Fact]
    public void Transpile_WhileReadLine_DoesNotReplaceSimilarVarNames()
    {
        var result = PsEmitter.Transpile("while read line; do echo $liner $line; done");

        Assert.Contains("$env:liner", result);
        Assert.Contains("$_", result);
    }

    [Fact]
    public void Transpile_WhileFileTest_EmitsWhileWithTestPath()
    {
        var result = PsEmitter.Transpile("while [ -f file ]; do echo yes; done");

        Assert.Equal("while ((Test-Path \"file\" -PathType Leaf)) { echo yes }", result);
    }

    [Fact]
    public void Transpile_UntilFileTest_EmitsNegatedWhileWithTestPath()
    {
        var result = PsEmitter.Transpile("until [ -f file ]; do sleep 1; done");

        Assert.Equal("while (-not ((Test-Path \"file\" -PathType Leaf))) { sleep 1 }", result);
    }

    [Fact]
    public void Transpile_WhileMultipleBodyCommands_EmitsAll()
    {
        var result = PsEmitter.Transpile("while true; do echo a; echo b; done");

        Assert.Equal("while ($true) { echo a; echo b }", result);
    }

    [Fact]
    public void Transpile_SimpleCase_EmitsSwitch()
    {
        var result = PsEmitter.Transpile("case $x in a) echo a;; b) echo b;; esac");

        Assert.Equal("switch ($env:x) { 'a' { echo a } 'b' { echo b } }", result);
    }

    [Fact]
    public void Transpile_CaseMultiplePatterns_EmitsSeparateClauses()
    {
        var result = PsEmitter.Transpile("case $x in a|b) echo ab;; esac");

        Assert.Equal("switch ($env:x) { 'a' { echo ab } 'b' { echo ab } }", result);
    }

    [Fact]
    public void Transpile_CaseDefaultStar_EmitsDefault()
    {
        var result = PsEmitter.Transpile("case $x in a) echo a;; *) echo other;; esac");

        Assert.Equal("switch ($env:x) { 'a' { echo a } default { echo other } }", result);
    }

    [Fact]
    public void Transpile_NestedCase_EmitsNestedSwitch()
    {
        var result = PsEmitter.Transpile(
            "case $x in a) case $y in b) echo b;; esac;; esac");

        Assert.Equal(
            "switch ($env:x) { 'a' { switch ($env:y) { 'b' { echo b } } } }",
            result);
    }

    [Fact]
    public void Transpile_CaseWithGlobPattern_EmitsWildcard()
    {
        var result = PsEmitter.Transpile("case $f in *.txt) echo text;; *) echo other;; esac");

        Assert.Equal(
            "switch -Wildcard ($env:f) { '*.txt' { echo text } default { echo other } }",
            result);
    }

    [Fact]
    public void Transpile_FunctionKeywordForm_EmitsPsFunction()
    {
        var result = PsEmitter.Transpile("function greet { echo hello }");

        Assert.Equal("function greet { echo hello }", result);
    }

    [Fact]
    public void Transpile_FunctionParensForm_EmitsPsFunction()
    {
        var result = PsEmitter.Transpile("greet() { echo hello }");

        Assert.Equal("function greet { echo hello }", result);
    }

    [Fact]
    public void Transpile_FunctionParensWithSpace_EmitsPsFunction()
    {
        var result = PsEmitter.Transpile("greet () { echo hello }");

        Assert.Equal("function greet { echo hello }", result);
    }

    [Fact]
    public void Transpile_FunctionWithLocalVars_EmitsLocalAssignment()
    {
        var result = PsEmitter.Transpile("function add { local result=42; echo $result }");

        Assert.Equal("function add { $result = \"42\"; echo $env:result }", result);
    }

    [Fact]
    public void Transpile_FunctionCallingFunction_EmitsNestedCalls()
    {
        var result = PsEmitter.Transpile(
            "function greet { echo hello }; function main { greet }");

        Assert.Equal(
            "function greet { echo hello }; function main { greet }",
            result);
    }

    [Fact]
    public void Transpile_FunctionWithMultilineBody_EmitsFunction()
    {
        var result = PsEmitter.Transpile("function setup {\n  echo start\n  echo end\n}");

        Assert.Equal("function setup { echo start; echo end }", result);
    }

    [Fact]
    public void Transpile_SimpleSubshell_EmitsScriptBlockInvocation()
    {
        var result = PsEmitter.Transpile("(echo hello; echo world)");

        Assert.Equal("& { echo hello; echo world }", result);
    }

    [Fact]
    public void Transpile_BraceGroup_EmitsInline()
    {
        var result = PsEmitter.Transpile("{ echo hello; echo world; }");

        Assert.Equal("echo hello; echo world", result);
    }

    [Fact]
    public void Transpile_SubshellWithRedirect_EmitsRedirect()
    {
        var result = PsEmitter.Transpile("(echo hello) > out.txt");

        Assert.Equal("& { echo hello } >out.txt", result);
    }

    [Fact]
    public void Transpile_NestedSubshells_EmitsNestedBlocks()
    {
        var result = PsEmitter.Transpile("(echo a; (echo b))");

        Assert.Equal("& { echo a; & { echo b } }", result);
    }

    [Fact]
    public void Transpile_ArithSub_BasicAddition()
    {
        var result = PsEmitter.Transpile("echo $((x + 1))");
        Assert.Equal("echo $($x + 1)", result);
    }

    [Fact]
    public void Transpile_ArithSub_LiteralAddition()
    {
        var result = PsEmitter.Transpile("echo $((2 + 3))");
        Assert.Equal("echo $(2 + 3)", result);
    }

    [Fact]
    public void Transpile_ArithSub_Multiplication()
    {
        var result = PsEmitter.Transpile("echo $((x * y))");
        Assert.Equal("echo $($x * $y)", result);
    }

    [Fact]
    public void Transpile_ArithCommand_Increment()
    {
        var result = PsEmitter.Transpile("(( x++ ))");
        Assert.Equal("$x++", result);
    }

    [Fact]
    public void Transpile_ArithCommand_Decrement()
    {
        var result = PsEmitter.Transpile("(( x-- ))");
        Assert.Equal("$x--", result);
    }

    [Fact]
    public void Transpile_ArithCommand_PreIncrement()
    {
        var result = PsEmitter.Transpile("(( ++x ))");
        Assert.Equal("++$x", result);
    }

    [Fact]
    public void Transpile_ArithCommand_PreDecrement()
    {
        var result = PsEmitter.Transpile("(( --x ))");
        Assert.Equal("--$x", result);
    }

    [Fact]
    public void Transpile_ArithCommand_Comparison_GreaterThan()
    {
        var result = PsEmitter.Transpile("(( x > 5 ))");
        Assert.Equal("$x -gt 5", result);
    }

    [Fact]
    public void Transpile_ArithCommand_Comparison_LessThan()
    {
        var result = PsEmitter.Transpile("(( x < 5 ))");
        Assert.Equal("$x -lt 5", result);
    }

    [Fact]
    public void Transpile_ArithCommand_Comparison_GreaterEqual()
    {
        var result = PsEmitter.Transpile("(( x >= 5 ))");
        Assert.Equal("$x -ge 5", result);
    }

    [Fact]
    public void Transpile_ArithCommand_Comparison_LessEqual()
    {
        var result = PsEmitter.Transpile("(( x <= 5 ))");
        Assert.Equal("$x -le 5", result);
    }

    [Fact]
    public void Transpile_ArithCommand_Comparison_Equal()
    {
        var result = PsEmitter.Transpile("(( x == 5 ))");
        Assert.Equal("$x -eq 5", result);
    }

    [Fact]
    public void Transpile_ArithCommand_Comparison_NotEqual()
    {
        var result = PsEmitter.Transpile("(( x != 5 ))");
        Assert.Equal("$x -ne 5", result);
    }

    [Fact]
    public void Transpile_ArithCommand_Ternary()
    {
        var result = PsEmitter.Transpile("(( x > 0 ? 1 : 0 ))");
        Assert.Equal("if ($x -gt 0) { 1 } else { 0 }", result);
    }

    [Fact]
    public void Transpile_ArithSub_InAssignment()
    {
        var result = PsEmitter.Transpile("result=$((x + 1))");
        Assert.Equal("$env:result = \"$($x + 1)\"", result);
    }

    [Fact]
    public void Transpile_ArithSub_Power()
    {
        var result = PsEmitter.Transpile("echo $((2 ** 3))");
        Assert.Equal("echo $(2 ** 3)", result);
    }

    [Fact]
    public void Transpile_ArithSub_Modulo()
    {
        var result = PsEmitter.Transpile("echo $((10 % 3))");
        Assert.Equal("echo $(10 % 3)", result);
    }

    [Fact]
    public void Transpile_ArithSub_NestedInString()
    {
        var result = PsEmitter.Transpile("echo \"result is $((x + 1))\"");
        Assert.Equal("echo \"result is $($x + 1)\"", result);
    }

    [Fact]
    public void Transpile_GlobStar_PassesThrough()
    {
        var result = PsEmitter.Transpile("echo *.py");
        Assert.Equal("echo *.py", result);
    }

    [Fact]
    public void Transpile_GlobQuestionMark_PassesThrough()
    {
        var result = PsEmitter.Transpile("echo file?.txt");
        Assert.Equal("echo file?.txt", result);
    }

    [Fact]
    public void Transpile_GlobCharClass_PassesThrough()
    {
        var result = PsEmitter.Transpile("echo [abc]*");
        Assert.Equal("echo [abc]*", result);
    }

    [Fact]
    public void Transpile_GlobMixedWithLiteral_PassesThrough()
    {
        var result = PsEmitter.Transpile("echo src/*.py");
        Assert.Equal("echo src/*.py", result);
    }

    [Fact]
    public void Transpile_GlobStandalone_PassesThrough()
    {
        var result = PsEmitter.Transpile("echo *");
        Assert.Equal("echo *", result);
    }

    [Fact]
    public void Transpile_ExtGlob_PassesThrough()
    {
        var result = PsEmitter.Transpile("echo +(*.py|*.js)");
        Assert.Equal("echo +(*.py|*.js)", result);
    }

    [Fact]
    public void Transpile_GlobPrefix_PassesThrough()
    {
        var result = PsEmitter.Transpile("echo *.log");
        Assert.Equal("echo *.log", result);
    }

    [Fact]
    public void Transpile_GlobSuffix_PassesThrough()
    {
        var result = PsEmitter.Transpile("echo test*");
        Assert.Equal("echo test*", result);
    }

    [Fact]
    public void Parse_GlobStar_ProducesGlobPart()
    {
        var cmd = Assert.IsType<Command.Simple>(BashParser.Parse("echo *.py"));
        var parts = cmd.Words[1].Parts;
        Assert.Equal(2, parts.Length);
        Assert.IsType<WordPart.GlobPart>(parts[0]);
        Assert.Equal("*", ((WordPart.GlobPart)parts[0]).Pattern);
        Assert.Equal(".py", ((WordPart.Literal)parts[1]).Value);
    }

    [Fact]
    public void Parse_GlobQuestionMark_ProducesGlobPart()
    {
        var cmd = Assert.IsType<Command.Simple>(BashParser.Parse("echo file?.txt"));
        var parts = cmd.Words[1].Parts;
        Assert.Equal(3, parts.Length);
        Assert.Equal("file", ((WordPart.Literal)parts[0]).Value);
        Assert.IsType<WordPart.GlobPart>(parts[1]);
        Assert.Equal("?", ((WordPart.GlobPart)parts[1]).Pattern);
        Assert.Equal(".txt", ((WordPart.Literal)parts[2]).Value);
    }

    [Fact]
    public void Parse_GlobCharClass_ProducesGlobPart()
    {
        var cmd = Assert.IsType<Command.Simple>(BashParser.Parse("echo [abc]*"));
        var parts = cmd.Words[1].Parts;
        Assert.Equal(2, parts.Length);
        Assert.IsType<WordPart.GlobPart>(parts[0]);
        Assert.Equal("[abc]", ((WordPart.GlobPart)parts[0]).Pattern);
        Assert.IsType<WordPart.GlobPart>(parts[1]);
        Assert.Equal("*", ((WordPart.GlobPart)parts[1]).Pattern);
    }

    [Fact]
    public void Parse_ExtGlob_ProducesGlobPart()
    {
        var cmd = Assert.IsType<Command.Simple>(BashParser.Parse("echo +(*.py|*.js)"));
        var parts = cmd.Words[1].Parts;
        Assert.Single(parts);
        Assert.IsType<WordPart.GlobPart>(parts[0]);
        Assert.Equal("+(*.py|*.js)", ((WordPart.GlobPart)parts[0]).Pattern);
    }

    [Fact]
    public void Parse_GlobMixedWithPath_SplitsCorrectly()
    {
        var cmd = Assert.IsType<Command.Simple>(BashParser.Parse("echo src/*.py"));
        var parts = cmd.Words[1].Parts;
        Assert.Equal(3, parts.Length);
        Assert.Equal("src/", ((WordPart.Literal)parts[0]).Value);
        Assert.Equal("*", ((WordPart.GlobPart)parts[1]).Pattern);
        Assert.Equal(".py", ((WordPart.Literal)parts[2]).Value);
    }

    [Fact]
    public void Transpile_ForInGlobCharClass_EmitsResolvePath()
    {
        var result = PsEmitter.Transpile("for f in [abc]*.txt; do cat $f; done");
        Assert.Equal("foreach ($f in (Resolve-Path [abc]*.txt)) { cat $f }", result);
    }

    [Fact]
    public void Transpile_BracedTuple_EmitsArray()
    {
        var result = PsEmitter.Transpile("echo {a,b,c}");
        Assert.Equal("echo @('a','b','c')", result);
    }

    [Fact]
    public void Transpile_BracedRange_EmitsPsRange()
    {
        var result = PsEmitter.Transpile("echo {1..10}");
        Assert.Equal("echo 1..10", result);
    }

    [Fact]
    public void Transpile_BracedRangeLeadingZeros_EmitsZeroPaddedArray()
    {
        var result = PsEmitter.Transpile("echo {01..05}");
        Assert.Equal("echo @('01','02','03','04','05')", result);
    }

    [Fact]
    public void Transpile_BracedTupleWithPrefixSuffix_EmitsExpandedArray()
    {
        var result = PsEmitter.Transpile("echo file{1,2,3}.txt");
        Assert.Equal("echo @('file1.txt','file2.txt','file3.txt')", result);
    }

    [Fact]
    public void Transpile_BracedRangeWithPrefix_EmitsExpandedArray()
    {
        var result = PsEmitter.Transpile("echo log{1..3}.txt");
        Assert.Equal("echo @('log1.txt','log2.txt','log3.txt')", result);
    }

    [Fact]
    public void Transpile_DiffWithTwoInputProcessSubs()
    {
        var result = PsEmitter.Transpile("diff <(ls dir1) <(ls dir2)");
        Assert.Equal("diff (Invoke-ProcessSub { ls dir1 }) (Invoke-ProcessSub { ls dir2 })", result);
    }

    [Fact]
    public void Transpile_OutputProcessSub()
    {
        var result = PsEmitter.Transpile("cmd >(tee log.txt)");
        Assert.Equal("cmd (Invoke-ProcessSub { Invoke-BashTee log.txt })", result);
    }

    [Fact]
    public void Transpile_NestedProcessSub()
    {
        var result = PsEmitter.Transpile("diff <(sort <(cat file1)) <(sort file2)");
        Assert.Equal(
            "diff (Invoke-ProcessSub { sort (Invoke-ProcessSub { cat file1 }) }) (Invoke-ProcessSub { sort file2 })",
            result);
    }

    [Fact]
    public void Transpile_ProcessSubWithPipe()
    {
        var result = PsEmitter.Transpile("diff <(sort file1 | uniq) file2");
        Assert.Equal("diff (Invoke-ProcessSub { sort file1 | Invoke-BashUniq }) file2", result);
    }

    [Fact]
    public void Transpile_GrepWithProcessSub()
    {
        var result = PsEmitter.Transpile("grep -f <(cat patterns.txt) data.txt");
        Assert.Equal("Invoke-BashGrep -f (Invoke-ProcessSub { cat patterns.txt }) data.txt", result);
    }

    // --- Array and associative array tests ---

    [Fact]
    public void Transpile_ArrayDeclaration_EmitsPsArray()
    {
        var result = PsEmitter.Transpile("arr=(a b c)");
        Assert.Equal("$arr = @('a','b','c')", result);
    }

    [Fact]
    public void Transpile_ArrayIndexAccess_EmitsPsIndex()
    {
        var result = PsEmitter.Transpile("echo ${arr[0]}");
        Assert.Equal("echo $arr[0]", result);
    }

    [Fact]
    public void Transpile_ArrayAllElements_EmitsPsArrayRef()
    {
        var result = PsEmitter.Transpile("echo ${arr[@]}");
        Assert.Equal("echo $arr", result);
    }

    [Fact]
    public void Transpile_ArrayLength_EmitsPsCount()
    {
        var result = PsEmitter.Transpile("echo ${#arr[@]}");
        Assert.Equal("echo $arr.Count", result);
    }

    [Fact]
    public void Transpile_ArrayIteration_EmitsForEachOverArray()
    {
        var result = PsEmitter.Transpile("for item in ${arr[@]}; do echo $item; done");
        Assert.Equal("foreach ($item in $arr) { echo $item }", result);
    }

    [Fact]
    public void Transpile_DeclareAssociativeArray_EmitsHashtable()
    {
        var result = PsEmitter.Transpile("declare -A map");
        Assert.Equal("$map = @{}", result);
    }

    [Fact]
    public void Transpile_AssociativeArrayAssignment_EmitsHashtableEntry()
    {
        var result = PsEmitter.Transpile("map[key]=val");
        Assert.Equal("$map['key'] = 'val'", result);
    }

    [Fact]
    public void Transpile_AssociativeArrayAccess_EmitsHashtableAccess()
    {
        var result = PsEmitter.Transpile("echo ${map[key]}");
        Assert.Equal("echo $map['key']", result);
    }

    [Fact]
    public void Transpile_BasicHeredoc_EmitsDoubleQuoteHereString()
    {
        var result = PsEmitter.Transpile("cat <<EOF\nline 1\nline 2\nEOF");

        Assert.Equal("@\"\nline 1\nline 2\n\"@ | cat", result);
    }

    [Fact]
    public void Transpile_HeredocWithVariableExpansion_EmitsPsEnvVar()
    {
        var result = PsEmitter.Transpile("cat <<EOF\nhello $NAME\nEOF");

        Assert.Equal("@\"\nhello $env:NAME\n\"@ | cat", result);
    }

    [Fact]
    public void Transpile_QuotedDelimiter_EmitsSingleQuoteHereString()
    {
        var result = PsEmitter.Transpile("cat <<'EOF'\nhello $NAME\nEOF");

        Assert.Equal("@'\nhello $NAME\n'@ | cat", result);
    }

    [Fact]
    public void Transpile_DLessDash_StripsLeadingTabs()
    {
        var result = PsEmitter.Transpile("cat <<-EOF\n\tline 1\n\tline 2\nEOF");

        Assert.Equal("@\"\nline 1\nline 2\n\"@ | cat", result);
    }

    [Fact]
    public void Transpile_HeredocWithCommandArgs_PipesToCommand()
    {
        var result = PsEmitter.Transpile("grep -i foo <<EOF\nhello foo\nbar\nEOF");

        Assert.Equal("@\"\nhello foo\nbar\n\"@ | Invoke-BashGrep -i foo", result);
    }

    // ── Regression tests: bugs found in integration testing ─────────────────

    // Bug: BraceExpansionTransform/parser expanded awk '{print $1, $3}' as
    // comma brace expansion because it contained a comma inside braces.
    [Fact]
    public void Transpile_AwkWithCommaInsideBraces_NotBraceExpanded()
    {
        // awk standalone: mapped through Invoke-BashAwk; braces+comma must NOT be brace-expanded
        var result = PsEmitter.Transpile("awk '{print $1, $3}' file.txt");
        Assert.Equal("Invoke-BashAwk '{print $1, $3}' file.txt", result);
        Assert.DoesNotContain("@(", result); // no brace expansion array
    }

    [Fact]
    public void Transpile_AwkInPipelineWithFlagAndCommaExpression_NotBraceExpanded()
    {
        // awk in a pipeline gets Invoke-BashAwk; braces+comma still must not be expanded
        var result = PsEmitter.Transpile("echo \"a,b,c\" | awk -F, '{print $1, $3}'");
        Assert.Equal("echo \"a,b,c\" | Invoke-BashAwk \"-F,\" '{print $1, $3}'", result);
        Assert.DoesNotContain("@(", result);
    }

    [Fact]
    public void Transpile_AwkWithMultipleFields_NotBraceExpanded()
    {
        // standalone awk — braces with multiple commas must not be expanded
        var result = PsEmitter.Transpile("awk '{print $1, $2, $3}'");
        Assert.Equal("Invoke-BashAwk '{print $1, $2, $3}'", result);
        Assert.DoesNotContain("@(", result);
    }

    // Bug: parameter expansion inside double quotes wasn't subexpression-wrapped
    [Fact]
    public void Transpile_VarExpansionInsideDoubleQuotes_EmitsDollarEnvInString()
    {
        var result = PsEmitter.Transpile("echo \"hello $NAME\"");
        Assert.Equal("echo \"hello $env:NAME\"", result);
    }

    [Fact]
    public void Transpile_BracedVarInsideDoubleQuotes_EmitsDollarEnv()
    {
        var result = PsEmitter.Transpile("echo \"${FOO} world\"");
        Assert.Equal("echo \"$env:FOO world\"", result);
    }

    // Bug: [void]() wrapping needed when assignment is chained with && or ||
    [Fact]
    public void Transpile_ExportWithAndChain_WrapsInVoid()
    {
        var result = PsEmitter.Transpile("export FOO=bar && echo $FOO");
        Assert.Equal("[void]($env:FOO = \"bar\") && echo $env:FOO", result);
    }

    [Fact]
    public void Transpile_AssignmentWithOrChain_WrapsInVoid()
    {
        var result = PsEmitter.Transpile("x=1 || echo failed");
        Assert.Contains("[void]", result);
        Assert.DoesNotContain("True\n", result);
    }

    // Bug: != in [ ] test — lexer splits ! and =word as separate tokens
    [Fact]
    public void Transpile_SingleBracketNotEqual_EmitsCorrectly()
    {
        var result = PsEmitter.Transpile("[ \"$A\" != \"$B\" ] && echo diff");
        Assert.Contains("-ne", result);
        Assert.Contains("$env:A", result);
        Assert.Contains("$env:B", result);
    }

    [Fact]
    public void Transpile_ExtendedTestNotEqual_EmitsCorrectly()
    {
        var result = PsEmitter.Transpile("[[ $A != $B ]]");
        Assert.Contains("$env:A", result);
        Assert.Contains("$env:B", result);
        Assert.Contains("-ne", result);
    }

    // IoNumber reclassification edge cases
    [Fact]
    public void Transpile_StderrToStdout_2And1Passthrough()
    {
        var result = PsEmitter.Transpile("cmd 2>&1");
        Assert.Equal("cmd 2>&1", result);
    }

    [Fact]
    public void Transpile_StdoutAndStderrToDevNull_EmitsBothNulls()
    {
        var result = PsEmitter.Transpile("cmd > /dev/null 2>&1");
        Assert.Equal("cmd >$null 2>&1", result);
    }

    [Fact]
    public void Transpile_StderrToFile_EmitsFileRedirect()
    {
        var result = PsEmitter.Transpile("cmd 2> err.log");
        Assert.Equal("cmd 2>err.log", result);
    }

    // Backslash escapes inside double quotes
    [Fact]
    public void Transpile_BackslashNInDoubleQuotes_PreservedAsLiteral()
    {
        // \n inside double quotes stays as-is in word output
        var result = PsEmitter.Transpile("echo \"line1\\nline2\"");
        Assert.Contains("line1", result);
        Assert.Contains("line2", result);
    }

    [Fact]
    public void Transpile_BackslashDollarInDoubleQuotes_LiteralDollar()
    {
        // \$ escapes the dollar — should not become $env:
        var result = PsEmitter.Transpile("echo \"cost \\$5\"");
        Assert.Contains("$5", result);
        Assert.DoesNotContain("$env:5", result);
    }

    [Fact]
    public void Transpile_BackslashQuoteInDoubleQuotes_LiteralQuote()
    {
        var result = PsEmitter.Transpile("echo \"say \\\"hi\\\"\"");
        Assert.Contains("hi", result);
    }

    // Brace expansion: leading-zero edge cases (real bug from IsPlainInteger)
    [Fact]
    public void Transpile_BraceRangeLeadingZero_EmitsStringArray()
    {
        var result = PsEmitter.Transpile("echo {01..05}");
        // Should emit padded strings, NOT 1..5 range operator
        Assert.Contains("'01'", result);
        Assert.Contains("'05'", result);
        Assert.DoesNotContain("1..5", result);
    }

    [Fact]
    public void Transpile_BraceRangeNoLeadingZero_EmitsRangeOperator()
    {
        var result = PsEmitter.Transpile("echo {1..5}");
        Assert.Contains("1..5", result);
        Assert.DoesNotContain("'01'", result);
    }

    // --- Regression tests for reported runtime issues ---

    [Fact]
    public void Transpile_XargsWithBraces_QuotesBracesToPreventScriptBlockParsing()
    {
        // Issue 14: -I{} was parsed by PowerShell as -I + empty scriptblock
        var result = PsEmitter.Transpile("echo test | xargs -I{} echo \"found: {}\"");

        Assert.Contains("\"-I{}\"", result);
    }

    [Fact]
    public void Transpile_HeadWithHeredoc_ParsesCorrectly()
    {
        // Issue 7: head -n 2 << EOF was misparsed as head -n 2<<EOF
        // (2 was reclassified as IoNumber). Now 2 stays as a word arg.
        var result = PsEmitter.Transpile("head -n 2 << EOF\nline1\nline2\nline3\nEOF");

        Assert.Contains("Invoke-BashHead -n 2", result);
        Assert.Contains("line1", result);
    }

    [Fact]
    public void Transpile_WcHeredoc_EmitsHereStringPipedToWc()
    {
        // Issue 8: wc -l with heredoc input
        var result = PsEmitter.Transpile("wc -l << EOF\nhello\nworld\nEOF");

        Assert.Contains("Invoke-BashWc -l", result);
        Assert.Contains("hello", result);
        Assert.Contains("world", result);
    }

    [Fact]
    public void Transpile_AwkWithFieldSepComma_QuotesFlag()
    {
        // Issue 6: awk -F, should quote the flag to prevent PS array interpretation
        var result = PsEmitter.Transpile("echo test | awk -F, '{print $1, $3}'");

        Assert.Contains("Invoke-BashAwk", result);
        Assert.Contains("\"-F,\"", result);
    }

    [Fact]
    public void Transpile_TrWithEscapeChar_PassesThrough()
    {
        // Issue 12: tr ' ' '\n' should pass through literal \n for runtime expansion
        var result = PsEmitter.Transpile("echo test | tr ' ' '\\n'");

        Assert.Contains("Invoke-BashTr", result);
    }

    // ── Real-world pattern coverage (from web audit) ──────────────────────────

    // Array literal: single-quoted elements must not double-quote
    [Fact]
    public void Transpile_ArrayLiteralSingleQuoted_NoDoubledQuotes()
    {
        var result = PsEmitter.Transpile("Fruits=('Apple' 'Banana' 'Orange')");
        Assert.Contains("@('Apple','Banana','Orange')", result);
        Assert.DoesNotContain("''Apple''", result);
    }

    [Fact]
    public void Transpile_ArrayAppend_EmitsPlusEquals()
    {
        var result = PsEmitter.Transpile("Fruits+=('Watermelon')");
        Assert.Contains("$Fruits += @('Watermelon')", result);
    }

    // while read -r VAR (with flag) triggers ForEach-Object path
    [Fact]
    public void Transpile_WhileReadDashR_EmitsForEachObject()
    {
        var result = PsEmitter.Transpile("while read -r line; do echo $line; done");
        Assert.Contains("ForEach-Object", result);
    }

    // read -p "prompt" VAR -> $VAR = Read-Host "prompt"
    [Fact]
    public void Transpile_ReadWithPrompt_EmitsReadHost()
    {
        var result = PsEmitter.Transpile("read -p \"Enter name: \" NAME");
        Assert.Contains("$NAME = Read-Host", result);
        Assert.Contains("Enter name:", result);
    }

    [Fact]
    public void Transpile_ReadNoPrompt_EmitsReadHost()
    {
        var result = PsEmitter.Transpile("read -r LINE");
        Assert.Equal("$LINE = Read-Host", result);
    }

    // set -euo pipefail -> $ErrorActionPreference = 'Stop'
    [Fact]
    public void Transpile_SetEuoPipefail_EmitsErrorActionStop()
    {
        var result = PsEmitter.Transpile("set -euo pipefail");
        Assert.Equal("$ErrorActionPreference = 'Stop'", result);
    }

    [Fact]
    public void Transpile_SetOErrexit_EmitsErrorActionStop()
    {
        var result = PsEmitter.Transpile("set -o errexit");
        Assert.Equal("$ErrorActionPreference = 'Stop'", result);
    }

    [Fact]
    public void Transpile_SetX_EmitsPSDebugTrace()
    {
        var result = PsEmitter.Transpile("set -x");
        Assert.Equal("Set-PSDebug -Trace 1", result);
    }

    // source file.sh -> . file.ps1
    [Fact]
    public void Transpile_SourceShFile_EmitsDotSourcePs1()
    {
        var result = PsEmitter.Transpile("source ./lib.sh");
        Assert.Equal(". ./lib.ps1", result);
    }

    [Fact]
    public void Transpile_DotSourceShFile_EmitsDotSourcePs1()
    {
        var result = PsEmitter.Transpile(". ./lib.sh");
        Assert.Equal(". ./lib.ps1", result);
    }

    // -e file exists test
    [Fact]
    public void Transpile_FileExistsTest_EmitsTestPath()
    {
        var result = PsEmitter.Transpile("if [[ -e file.txt ]]; then echo yes; fi");
        Assert.Contains("Test-Path", result);
        Assert.Contains("file.txt", result);
    }

    // declare -i -> [int]$var = 0
    [Fact]
    public void Transpile_DeclareInt_EmitsTypedVar()
    {
        var result = PsEmitter.Transpile("declare -i count");
        Assert.Equal("[int]$count = 0", result);
    }

    // ${str/foo/bar} -> replace first
    [Fact]
    public void Transpile_ParamReplaceFirst_EmitsRegexReplace()
    {
        var result = PsEmitter.Transpile("echo ${str/foo/bar}");
        Assert.Contains("-replace", result);
        Assert.Contains("foo", result);
        Assert.Contains("bar", result);
    }

    // ${str//foo/bar} -> replace all
    [Fact]
    public void Transpile_ParamReplaceAll_EmitsReplace()
    {
        var result = PsEmitter.Transpile("echo ${str//foo/bar}");
        Assert.Contains("-replace", result);
        Assert.Contains("foo", result);
        Assert.Contains("bar", result);
    }

    // ${name:0:2} -> substring
    [Fact]
    public void Transpile_ParamSlice_EmitsSubstring()
    {
        var result = PsEmitter.Transpile("echo ${name:0:2}");
        Assert.Contains("Substring(0, 2)", result);
    }

    // ${str^^} -> ToUpper
    [Fact]
    public void Transpile_ParamUpperCase_EmitsToUpper()
    {
        var result = PsEmitter.Transpile("echo ${str^^}");
        Assert.Contains(".ToUpper()", result);
    }

    // ${str,,} -> ToLower
    [Fact]
    public void Transpile_ParamLowerCase_EmitsToLower()
    {
        var result = PsEmitter.Transpile("echo ${str,,}");
        Assert.Contains(".ToLower()", result);
    }

    // ${!arr[@]} -> .Keys
    [Fact]
    public void Transpile_ArrayKeys_EmitsDotKeys()
    {
        var result = PsEmitter.Transpile("echo ${!sounds[@]}");
        Assert.Contains("$sounds.Keys", result);
    }

    // {5..50..5} brace range with step
    [Fact]
    public void Transpile_BraceRangeWithStep_ExpandsCorrectly()
    {
        var result = PsEmitter.Transpile("echo {5..50..5}");
        Assert.Contains("5", result);
        Assert.Contains("50", result);
        Assert.DoesNotContain("5..50..5", result);
    }

    [Fact]
    public void Transpile_PipeToRev_EmitsInvokeBashRev()
    {
        var result = PsEmitter.Transpile("echo hello | rev");

        Assert.Equal("echo hello | Invoke-BashRev", result);
    }

    [Fact]
    public void Transpile_PipeToJqWithFilter_EmitsInvokeBashJq()
    {
        var result = PsEmitter.Transpile("curl http://api | jq .name");

        Assert.Equal("curl http://api | Invoke-BashJq .name", result);
    }

    [Fact]
    public void Transpile_PipeToNlWithFlags_EmitsInvokeBashNl()
    {
        var result = PsEmitter.Transpile("cat file | nl -ba");

        Assert.Equal("cat file | Invoke-BashNl -ba", result);
    }

    [Fact]
    public void Transpile_PipeToColumnWithFlag_EmitsInvokeBashColumn()
    {
        var result = PsEmitter.Transpile("cat data.csv | column -t");

        Assert.Equal("cat data.csv | Invoke-BashColumn -t", result);
    }

    [Fact]
    public void Transpile_PipeToTee_EmitsInvokeBashTee()
    {
        var result = PsEmitter.Transpile("echo hello | tee output.txt");

        Assert.Equal("echo hello | Invoke-BashTee output.txt", result);
    }

    // --- Standalone mapped command tests ---

    [Fact]
    public void Transpile_StandaloneHead_EmitsInvokeBashHead()
    {
        var result = PsEmitter.Transpile("head -n 5 file.txt");

        Assert.Equal("Invoke-BashHead -n 5 file.txt", result);
    }

    [Fact]
    public void Transpile_StandaloneWc_EmitsInvokeBashWc()
    {
        var result = PsEmitter.Transpile("wc -l file.txt");

        Assert.Equal("Invoke-BashWc -l file.txt", result);
    }

    [Fact]
    public void Transpile_StandaloneFind_EmitsInvokeBashFind()
    {
        var result = PsEmitter.Transpile("find . -name '*.txt'");

        Assert.Equal("Invoke-BashFind . -name '*.txt'", result);
    }

    [Fact]
    public void Transpile_StandaloneGrep_EmitsInvokeBashGrep()
    {
        var result = PsEmitter.Transpile("grep error log.txt");

        Assert.Equal("Invoke-BashGrep error log.txt", result);
    }

    [Fact]
    public void Transpile_BracedVarDefaultInsideDoubleQuotes_EmitsSubexpression()
    {
        var result = PsEmitter.Transpile("echo \"${UNSET_VAR:-fallback}\"");
        Assert.Equal("echo \"$($env:UNSET_VAR ?? 'fallback')\"", result);
    }

    [Fact]
    public void Transpile_BracedVarSuffixRemovalInsideDoubleQuotes_EmitsSubexpression()
    {
        var result = PsEmitter.Transpile("echo \"${FOO%%l*}\"");
        Assert.Equal("echo \"$($env:FOO -replace 'l*$','')\"", result);
    }

    [Fact]
    public void Transpile_BracedVarLengthInsideDoubleQuotes_EmitsSubexpression()
    {
        var result = PsEmitter.Transpile("echo \"${#VAR}\"");
        Assert.Equal("echo \"$($env:VAR.Length)\"", result);
    }

    [Fact]
    public void Transpile_BracedVarPrefixRemovalInsideDoubleQuotes_EmitsSubexpression()
    {
        var result = PsEmitter.Transpile("echo \"${PATH##*/}\"");
        Assert.Equal("echo \"$($env:PATH -replace '^*/','')\"", result);
    }

    [Fact]
    public void Transpile_BracedVarAlternativeInsideDoubleQuotes_EmitsSubexpression()
    {
        var result = PsEmitter.Transpile("echo \"${VAR:+yes}\"");
        Assert.Equal("echo \"$($env:VAR ? 'yes' : '')\"", result);
    }

    [Fact]
    public void Transpile_SimpleBracedVarInsideDoubleQuotes_NoSubexpression()
    {
        var result = PsEmitter.Transpile("echo \"${USER}\"");
        Assert.Equal("echo \"$env:USER\"", result);
    }

    [Fact]
    public void Transpile_TrapCommandExit_EmitsPassthrough()
    {
        var result = PsEmitter.Transpile("trap 'echo cleanup' EXIT");
        Assert.Equal("Invoke-BashTrap 'echo cleanup' EXIT", result);
    }

    [Fact]
    public void Transpile_TrapCommandErr_EmitsPassthrough()
    {
        var result = PsEmitter.Transpile("trap 'echo error' ERR");
        Assert.Equal("Invoke-BashTrap 'echo error' ERR", result);
    }

    [Fact]
    public void Transpile_TrapEmptyInt_EmitsPassthrough()
    {
        var result = PsEmitter.Transpile("trap '' INT");
        Assert.Equal("Invoke-BashTrap '' INT", result);
    }

    private static CompoundWord MakeWord(string value) =>
        new(ImmutableArray.Create<WordPart>(new WordPart.Literal(value)));
}
