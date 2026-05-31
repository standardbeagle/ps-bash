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

        Assert.Equal("Invoke-BashEcho hello", result);
    }

    [Fact]
    public void Emit_SimpleCommand_WithEnvPair_EmitsPsEnvAssignment()
    {
        var cmd = new Command.Simple(
            ImmutableArray.Create(MakeWord("cmd")),
            ImmutableArray.Create(new EnvPair("FOO", MakeWord("bar"))),
            ImmutableArray<Redirect>.Empty);

        var result = PsEmitter.Emit(cmd);

        Assert.Equal("$__saved_FOO = $env:FOO; try { $env:FOO = \"bar\"; cmd } finally { $env:FOO = $__saved_FOO; }", result);
    }

    [Fact]
    public void Emit_SimpleCommand_WithNullEnvPairValue_EmitsEmptyString()
    {
        var cmd = new Command.Simple(
            ImmutableArray.Create(MakeWord("cmd")),
            ImmutableArray.Create(new EnvPair("FOO", null)),
            ImmutableArray<Redirect>.Empty);

        var result = PsEmitter.Emit(cmd);

        Assert.Equal("$__saved_FOO = $env:FOO; try { $env:FOO = \"\"; cmd } finally { $env:FOO = $__saved_FOO; }", result);
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

        Assert.Equal("Invoke-BashLs -la /tmp", result);
    }

    [Fact]
    public void Transpile_LsPipeGrep_EmitsMappedPipeline()
    {
        var result = PsEmitter.Transpile("ls | grep foo");

        Assert.Equal("Invoke-BashLs | Invoke-BashGrep foo", result);
    }

    [Fact]
    public void Transpile_CatPipeHeadPipeSort_EmitsMultiStagePipeline()
    {
        var result = PsEmitter.Transpile("cat file | head -n 5 | sort");

        Assert.Equal("Invoke-BashCat file | Invoke-BashHead -n 5 | Invoke-BashSort", result);
    }

    [Fact]
    public void Transpile_PipeAmpersand_EmitsStderrMerge()
    {
        var result = PsEmitter.Transpile("cmd |& other");

        Assert.Equal("cmd 2>&1 | other", result);
    }

    [Fact]
    public void Transpile_AmpGreat_RedirectsBothStreams()
    {
        // `&>file` = redirect stdout AND stderr → PowerShell `>file 2>&1`.
        // Previously `&` lexed as background, dropping the stderr redirect.
        var result = PsEmitter.Transpile("cmd &> out.log");

        Assert.Equal("cmd >out.log 2>&1", result);
    }

    [Fact]
    public void Transpile_AmpDGreat_AppendsBothStreams()
    {
        var result = PsEmitter.Transpile("cmd &>> out.log");

        Assert.Equal("cmd >>out.log 2>&1", result);
    }

    [Fact]
    public void Transpile_AmpGreatDevNull_MapsToNullSink()
    {
        // The most common real-world `&>` use: discard all output. The
        // /dev/null -> $null target transform must still apply.
        var result = PsEmitter.Transpile("cmd &> /dev/null");

        Assert.Equal("cmd >$null 2>&1", result);
    }

    [Fact]
    public void Transpile_NegatedCommand_EmitsExitCodeNegation()
    {
        var result = PsEmitter.Transpile("! grep -q pattern file");

        // Negation checks $global:LASTEXITCODE (bash exit code) not PowerShell's $?.
        // This ensures grep's no-match (exit 1) is correctly negated to 0.
        Assert.Equal(
            "Invoke-BashGrep -q pattern file; $global:LASTEXITCODE = if ($global:LASTEXITCODE -eq 0) { 1 } else { 0 }",
            result);
    }

    [Fact]
    public void Transpile_TildePlus_MapsToPwd()
    {
        // ~+ -> $PWD (was emitted as the literal ~+).
        var result = PsEmitter.Transpile("echo ~+");
        Assert.Contains("$PWD", result);
        Assert.DoesNotContain("~+", result);
    }

    [Fact]
    public void Transpile_TildeMinus_MapsToOldPwd()
    {
        var result = PsEmitter.Transpile("echo ~-");
        Assert.Contains("$env:OLDPWD", result);
        Assert.DoesNotContain("~-", result);
    }

    [Fact]
    public void Transpile_TildeSlash_StillHome()
    {
        // Regression guard: plain ~/path is unchanged ($HOME ...).
        var result = PsEmitter.Transpile("echo ~/bin");
        Assert.Contains("$HOME", result);
        Assert.DoesNotContain("$PWD", result);
    }

    [Fact]
    public void Transpile_TildeUser_KeptLiteral()
    {
        // ~user has no PowerShell equivalent; kept literal (degrade), not a bogus var.
        var result = PsEmitter.Transpile("echo ~alice");
        Assert.Contains("~alice", result);
    }

    [Fact]
    public void Transpile_NamePrefixIndirection_Star_ExpandsNames()
    {
        // ${!FOO*} = names of variables starting with FOO (NOT $FOO.Keys).
        var result = PsEmitter.Transpile("echo ${!FOO*}");

        Assert.Contains("Get-ChildItem env:", result);
        Assert.Contains("-like 'FOO*'", result);
        Assert.DoesNotContain(".Keys", result);
    }

    [Fact]
    public void Transpile_NamePrefixIndirection_At_ExpandsNames()
    {
        var result = PsEmitter.Transpile("echo ${!FOO@}");

        Assert.Contains("Get-ChildItem env:", result);
        Assert.Contains("-like 'FOO*'", result);
    }

    [Fact]
    public void Transpile_ArrayKeysIndirection_Unaffected()
    {
        // Regression guard: ${!arr[@]} (keys) must still map to $arr.Keys, NOT
        // the name-prefix expansion.
        var result = PsEmitter.Transpile("echo ${!arr[@]}");

        Assert.Contains(".Keys", result);
        Assert.DoesNotContain("Get-ChildItem", result);
    }

    [Fact]
    public void Transpile_CloseStdout_DiscardsToNull()
    {
        // `>&-` closes stdout; PowerShell has no fd-close, so discard to $null.
        // Previously emitted invalid `1>&-`.
        var result = PsEmitter.Transpile("cmd >&-");
        Assert.Equal("cmd >$null", result);
    }

    [Fact]
    public void Transpile_CloseFd2_DiscardsFdToNull()
    {
        var result = PsEmitter.Transpile("cmd 2>&-");
        Assert.Equal("cmd 2>$null", result);
    }

    [Fact]
    public void Transpile_StderrToStdoutMerge_Unaffected()
    {
        // Regression guard: the normal merge form `2>&1` (target "1", not "-")
        // must NOT be treated as a close.
        var result = PsEmitter.Transpile("cmd 2>&1");
        Assert.Equal("cmd 2>&1", result);
    }

    [Fact]
    public void Transpile_CloseStdin_DegradesToComment()
    {
        // `<&-` closes stdin — no PowerShell equivalent; documented no-op comment
        // instead of the invalid `0<&-`.
        var result = PsEmitter.Transpile("cmd <&-");
        Assert.Contains("<#", result);
        Assert.Contains("<&-", result);
        Assert.DoesNotContain("0<&-", result);
    }

    [Fact]
    public void Transpile_LocaleQuoting_DropsDollar_SameAsDoubleQuote()
    {
        // $"..." = locale translation; with no catalog it is identical to a plain
        // double-quoted string. Bash strips the $; ps-bash must not emit a stray $.
        var localized = PsEmitter.Transpile("echo $\"hello world\"");
        var plain = PsEmitter.Transpile("echo \"hello world\"");

        Assert.Equal(plain, localized);
        Assert.DoesNotContain("$\"", localized);
    }

    [Fact]
    public void Transpile_LocaleQuoting_WithVariable_ExpandsLikeDoubleQuote()
    {
        // $"...$x..." still expands $x (double-quote semantics), just no leading $.
        var localized = PsEmitter.Transpile("echo $\"hi $USER\"");
        var plain = PsEmitter.Transpile("echo \"hi $USER\"");

        Assert.Equal(plain, localized);
    }

    [Fact]
    public void Transpile_DoubleNegation_IsIdentity_NoNegationSuffix()
    {
        // `! ! cmd` = double negation = identity. The command must run unwrapped
        // (no exit-code negation), and must NOT be dropped. Previously the second
        // `!` was a stray Bang that produced an empty command.
        var result = PsEmitter.Transpile("! ! grep -q pattern file");

        Assert.Equal("Invoke-BashGrep -q pattern file", result);
    }

    [Fact]
    public void Transpile_DoubleNegatedPipeline_IsIdentity()
    {
        // `! ! cmd1 | cmd2` — double negation applies to the whole pipeline =
        // identity. No exit-code negation suffix; pipeline runs as-is.
        var result = PsEmitter.Transpile("! ! cmd1 | cmd2");

        Assert.Equal("cmd1 | cmd2", result);
    }

    [Fact]
    public void Transpile_TripleNegation_NegatesOnce()
    {
        var result = PsEmitter.Transpile("! ! ! grep -q pattern file");

        Assert.Equal(
            "Invoke-BashGrep -q pattern file; $global:LASTEXITCODE = if ($global:LASTEXITCODE -eq 0) { 1 } else { 0 }",
            result);
    }

    [Fact]
    public void Transpile_NegatedPipeline_EmitsExitCodeNegation()
    {
        var result = PsEmitter.Transpile("! cmd1 | cmd2");

        // Negation checks $global:LASTEXITCODE (bash exit code) not PowerShell's $?.
        Assert.Equal(
            "cmd1 | cmd2; $global:LASTEXITCODE = if ($global:LASTEXITCODE -eq 0) { 1 } else { 0 }",
            result);
    }

    [Fact]
    public void Transpile_EchoPipeWcL_EmitsMeasureObject()
    {
        var result = PsEmitter.Transpile("echo hello | wc -l");

        Assert.Equal("Invoke-BashEcho hello | Invoke-BashWc -l", result);
    }

    [Fact]
    public void Transpile_PsPipeBrowse_EmitsBrowseMappedCommand()
    {
        var result = PsEmitter.Transpile("ps | browse");

        Assert.Equal("Invoke-BashPs | Invoke-BashBrowse", result);
    }

    [Fact]
    public void Transpile_CatPipeLess_EmitsShellPagerCommand()
    {
        var result = PsEmitter.Transpile("cat file | less");

        Assert.Equal("Invoke-BashCat file | Invoke-BashLess", result);
    }

    [Fact]
    public void Transpile_LessFile_EmitsShellPagerCommand()
    {
        var result = PsEmitter.Transpile("less file");

        Assert.Equal("Invoke-BashLess file", result);
    }

    [Fact]
    public void Transpile_CatPipeMore_EmitsModulePagerCommand()
    {
        var result = PsEmitter.Transpile("cat file | more");

        Assert.Equal("Invoke-BashCat file | Invoke-BashMore", result);
    }

    [Fact]
    public void Transpile_MoreFile_EmitsModulePagerCommand()
    {
        var result = PsEmitter.Transpile("more file");

        Assert.Equal("Invoke-BashMore file", result);
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

        Assert.Equal("Invoke-BashEcho", result);
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

        Assert.Equal("$__saved_FOO = $env:FOO; try { $env:FOO = \"bar\"; baz } finally { $env:FOO = $__saved_FOO; }", result);
    }

    [Fact]
    public void Transpile_MultipleAssignmentsWithCommand_EmitsEnvPairs()
    {
        var result = PsEmitter.Transpile("FOO=1 BAR=2 cmd");

        Assert.Equal("$__saved_FOO = $env:FOO; $__saved_BAR = $env:BAR; try { $env:FOO = \"1\"; $env:BAR = \"2\"; cmd } finally { $env:FOO = $__saved_FOO; $env:BAR = $__saved_BAR; }", result);
    }

    [Fact]
    public void Transpile_ExportPathWithExpansion_EmitsCorrectExpansion()
    {
        var result = PsEmitter.Transpile("export PATH=\"$PATH:/new\"");

        Assert.Equal("$env:PATH = \"${env:PATH}:/new\"", result);
    }

    [Fact]
    public void Transpile_ExportPathAdjacentDoubleQuotedSegments_ProducesSingleOuterQuote()
    {
        // export PATH="C:\\prefix":"$PATH"  — two DoubleQuoted parts joined by a Literal(":")
        // Bug: EmitWord emitted "prefix":"$env:PATH", then EmitAssignmentValue wrapped it in
        // another "...", producing ""prefix":"$env:PATH"" — invalid PowerShell.
        var result = PsEmitter.Transpile("export PATH=\"/a/b\":\"$PATH\"");

        Assert.Equal("$env:PATH = \"/a/b:$env:PATH\"", result);
    }

    [Fact]
    public void Transpile_EchoHello_ReturnsPassthrough()
    {
        var result = PsEmitter.Transpile("echo hello");

        Assert.Equal("Invoke-BashEcho hello", result);
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

        Assert.Equal("Invoke-BashLs", result);
    }

    [Fact]
    public void Transpile_MultipleWords_ReturnsPassthrough()
    {
        var result = PsEmitter.Transpile("git commit -m \"message\"");

        Assert.Equal("git commit -m \"message\"", result);
    }

    [Fact]
    public void Transpile_DoubleQuotedExecutable_PrefixesWithCallOperator()
    {
        var result = PsEmitter.Transpile("\"C:/Users/andyb/.bun/bin/bun.exe\" -e 'console.log(\"hello\")'");

        Assert.Equal("\u0026 \"C:/Users/andyb/.bun/bin/bun.exe\" -e 'console.log(\"hello\")'", result);
    }

    [Fact]
    public void Transpile_SingleQuotedExecutable_PrefixesWithCallOperator()
    {
        var result = PsEmitter.Transpile("'/usr/local/bin/node' -e 'console.log(1)'");

        Assert.Equal("\u0026 '/usr/local/bin/node' -e 'console.log(1)'", result);
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

        Assert.Equal("$__saved_FOO = $env:FOO; $__saved_BAZ = $env:BAZ; try { $env:FOO = \"bar\"; $env:BAZ = \"qux\"; cmd } finally { $env:FOO = $__saved_FOO; $env:BAZ = $__saved_BAZ; }", result);
    }

    [Fact]
    public void Transpile_SingleQuoted_PassthroughInSingleQuotes()
    {
        var result = PsEmitter.Transpile("echo 'hello world'");

        Assert.Equal("Invoke-BashEcho 'hello world'", result);
    }

    [Fact]
    public void Transpile_DoubleQuotedWithVar_EmitsEnvVar()
    {
        var result = PsEmitter.Transpile("echo \"hello $USER\"");

        Assert.Equal("Invoke-BashEcho \"hello $env:USER\"", result);
    }

    [Fact]
    public void Transpile_BackslashEscape_EmitsBactickEscape()
    {
        var result = PsEmitter.Transpile("echo hello\\ world");

        Assert.Equal("Invoke-BashEcho hello` world", result);
    }

    [Fact]
    public void Transpile_DoubleQuotedWithApostrophe_Preserved()
    {
        var result = PsEmitter.Transpile("echo \"it's fine\"");

        Assert.Equal("Invoke-BashEcho \"it's fine\"", result);
    }

    [Fact]
    public void Transpile_SingleQuotedWithDoubleQuotes_Preserved()
    {
        var result = PsEmitter.Transpile("echo 'say \"hi\"'");

        Assert.Equal("Invoke-BashEcho 'say \"hi\"'", result);
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

        Assert.Equal("Invoke-BashEcho $null", result);
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

        Assert.Equal("Invoke-BashEcho hello` world", result);
    }

    [Fact]
    public void Transpile_OutputRedirectToFile_EmitsBashRedirect()
    {
        var result = PsEmitter.Transpile("cmd > file");

        Assert.Equal("cmd | Invoke-BashRedirect -Path file", result);
    }

    [Fact]
    public void Transpile_AppendRedirectToFile_EmitsBashRedirectAppend()
    {
        var result = PsEmitter.Transpile("cmd >> file");

        Assert.Equal("cmd | Invoke-BashRedirect -Path file -Append", result);
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

        Assert.Equal("cmd | Invoke-BashRedirect -Path $env:TEMP\\out.log", result);
    }

    [Fact]
    public void Transpile_MkdirAndCd_Passthrough()
    {
        var result = PsEmitter.Transpile("mkdir dir && cd dir");

        Assert.StartsWith("Invoke-BashMkdir dir && $($__psbash_cd_target = 'dir'", result);
        Assert.Contains("$global:__PsBashCwd = $__psbash_cd_resolved", result);
        Assert.Contains("[System.Environment]::CurrentDirectory = $__psbash_cd_resolved", result);
        Assert.Contains("$env:PWD = $__psbash_cd_resolved", result);
    }

    [Fact]
    public void Transpile_TestOrEcho_Passthrough()
    {
        var result = PsEmitter.Transpile("test -f file || echo missing");

        Assert.Equal("Invoke-BashTest -f file || Invoke-BashEcho missing", result);
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

        Assert.Equal("Invoke-BashTest -f file || Invoke-BashEcho missing", result);
    }

    [Fact]
    public void Transpile_EchoHome_PsBuiltinPassthrough()
    {
        var result = PsEmitter.Transpile("echo $HOME");

        Assert.Equal("Invoke-BashEcho $HOME", result);
    }

    [Fact]
    public void Transpile_EchoFoo_EmitsEnvVar()
    {
        // RC-7: a bare unquoted ordinary ($env:-backed) variable operand is
        // word-split and elided-when-empty via a splat temp. This matches real
        // bash — see Differential_UnquotedVar_WordSplitsOnSpaces and
        // Differential_EmptyVar_UnquotedIsOmitted, the oracle for this shape.
        var result = PsEmitter.Transpile("echo $FOO");

        Assert.Equal(
            "& { $__bashsplat0 = @(if ([string]::IsNullOrEmpty($env:FOO)) " +
            "{ @() } else { @($env:FOO -split '\\s+') }); " +
            "Invoke-BashEcho @__bashsplat0 }",
            result);
    }

    [Fact]
    public void Transpile_BracedVar_EmitsEnvVar()
    {
        // RC-7: a suffix-less braced ordinary variable is an unquoted variable
        // operand — word-split + elide-when-empty splat, same as $FOO. Oracle:
        // Differential_UnquotedVar_WordSplitsOnSpaces.
        var result = PsEmitter.Transpile("echo ${PATH}");

        Assert.Equal(
            "& { $__bashsplat0 = @(if ([string]::IsNullOrEmpty($env:PATH)) " +
            "{ @() } else { @($env:PATH -split '\\s+') }); " +
            "Invoke-BashEcho @__bashsplat0 }",
            result);
    }

    [Fact]
    public void Transpile_BracedVarWithDefault_EmitsNullCoalescing()
    {
        var result = PsEmitter.Transpile("echo ${VAR:-fallback}");

        Assert.Equal("Invoke-BashEcho ($env:VAR ?? \"fallback\")", result);
    }

    [Fact]
    public void Transpile_SpecialVarQuestionMark_EmitsLastExitCode()
    {
        // $? emits $global:LASTEXITCODE so negated-pipeline results are visible across scopes.
        var result = PsEmitter.Transpile("echo $?");

        Assert.Equal("Invoke-BashEcho $global:LASTEXITCODE", result);
    }

    [Fact]
    public void Transpile_BracedVarLength_EmitsLength()
    {
        var result = PsEmitter.Transpile("echo ${#VAR}");

        Assert.Equal("Invoke-BashEcho $env:VAR.Length", result);
    }

    [Fact]
    public void Transpile_SpecialVarAt_EmitsArgs()
    {
        var result = PsEmitter.Transpile("echo $@");

        Assert.Equal("Invoke-BashEcho $(if ($global:BashPositional) { $global:BashPositional } else { $args })", result);
    }

    [Fact]
    public void Transpile_SpecialVarHash_EmitsArgsCount()
    {
        var result = PsEmitter.Transpile("echo $#");

        Assert.Equal("Invoke-BashEcho $(if ($global:BashPositional) { $global:BashPositional.Count } else { $args.Count })", result);
    }

    [Fact]
    public void Transpile_SpecialVarDollarDollar_EmitsPid()
    {
        var result = PsEmitter.Transpile("echo $$");

        Assert.Equal("Invoke-BashEcho $PID", result);
    }

    [Fact]
    public void Transpile_PositionalVar1_EmitsArgsIndex()
    {
        var result = PsEmitter.Transpile("echo $1");

        Assert.Equal("Invoke-BashEcho $(if ($global:BashPositional) { $global:BashPositional[0] } else { $args[0] })", result);
    }

    [Fact]
    public void Transpile_PositionalVar9_EmitsArgsIndex()
    {
        var result = PsEmitter.Transpile("echo $9");

        Assert.Equal("Invoke-BashEcho $(if ($global:BashPositional) { $global:BashPositional[8] } else { $args[8] })", result);
    }

    [Fact]
    public void Transpile_SpecialVar0_EmitsMyCommand()
    {
        var result = PsEmitter.Transpile("echo $0");

        Assert.Equal("Invoke-BashEcho $MyInvocation.MyCommand.Name", result);
    }

    [Fact]
    public void Transpile_BracedVarAssignDefault_EmitsNullCoalescingAssign()
    {
        var result = PsEmitter.Transpile("echo ${VAR:=default}");

        Assert.Equal("Invoke-BashEcho ($env:VAR ?? ($env:VAR = \"default\"))", result);
    }

    [Fact]
    public void Transpile_BracedVarAlternative_EmitsConditional()
    {
        var result = PsEmitter.Transpile("echo ${VAR:+yes}");

        Assert.Equal("Invoke-BashEcho ($env:VAR ? \"yes\" : \"\")", result);
    }

    [Fact]
    public void Transpile_BracedVarError_EmitsThrow()
    {
        var result = PsEmitter.Transpile("echo ${VAR:?error msg}");

        Assert.Equal("Invoke-BashEcho ($env:VAR ?? $(throw \"error msg\"))", result);
    }

    [Fact]
    public void Transpile_BracedVarSuffixRemoval_EmitsReplace()
    {
        var result = PsEmitter.Transpile("echo ${VAR%%pattern}");

        Assert.Equal("Invoke-BashEcho ($env:VAR -replace 'pattern$','')", result);
    }

    [Fact]
    public void Transpile_BracedVarPrefixRemoval_EmitsReplace()
    {
        var result = PsEmitter.Transpile("echo ${VAR##pattern}");

        Assert.Equal("Invoke-BashEcho ($env:VAR -replace '^pattern','')", result);
    }

    [Fact]
    public void Transpile_BracedVarInsideDoubleQuotes_EmitsEnvVar()
    {
        var result = PsEmitter.Transpile("echo \"${USER}\"");

        Assert.Equal("Invoke-BashEcho \"$env:USER\"", result);
    }

    [Fact]
    public void Transpile_SpecialVarStar_EmitsArgs()
    {
        var result = PsEmitter.Transpile("echo $*");

        Assert.Equal("Invoke-BashEcho $(if ($global:BashPositional) { $global:BashPositional } else { $args })", result);
    }

    [Fact]
    public void Transpile_BracedVarHomePsBuiltin_EmitsHomeDirect()
    {
        var result = PsEmitter.Transpile("echo ${HOME}");

        Assert.Equal("Invoke-BashEcho $HOME", result);
    }

    [Fact]
    public void Transpile_CommandSub_SimpleCommand_Passthrough()
    {
        var result = PsEmitter.Transpile("echo $(whoami)");

        // RC-8d: command-substitution emit wraps inner output in
        // `| ForEach-Object { Get-BashText $_ }` so the captured value is the
        // bash-text payload, never a typed BashObject's default ToString().
        Assert.Equal("Invoke-BashEcho $(Invoke-BashWhoami | ForEach-Object { Get-BashText $_ })", result);
    }

    [Fact]
    public void Transpile_CommandSub_InnerPipeline_TranspilesInnerCommands()
    {
        var result = PsEmitter.Transpile("echo $(ls | grep foo)");

        Assert.Equal("Invoke-BashEcho $(Invoke-BashLs | Invoke-BashGrep foo | ForEach-Object { Get-BashText $_ })", result);
    }

    [Fact]
    public void Transpile_BacktickCommandSub_NormalizedToDollarParen()
    {
        var result = PsEmitter.Transpile("echo `date`");

        Assert.Equal("Invoke-BashEcho $(Invoke-BashDate | ForEach-Object { Get-BashText $_ })", result);
    }

    [Fact]
    public void Transpile_AssignmentWithCommandSub_EmitsEnvAssignment()
    {
        var result = PsEmitter.Transpile("VAR=$(cat file)");

        Assert.Equal("$env:VAR = \"$(Invoke-BashCat file | ForEach-Object { Get-BashText $_ })\"", result);
    }

    [Fact]
    public void Transpile_NestedCommandSub_EmitsCorrectNesting()
    {
        var result = PsEmitter.Transpile("echo $(echo $(whoami))");

        Assert.Equal("Invoke-BashEcho $(Invoke-BashEcho $(Invoke-BashWhoami | ForEach-Object { Get-BashText $_ }) | ForEach-Object { Get-BashText $_ })", result);
    }

    /// <summary>
    /// RC-8d regression: `dir=$(pwd)` must capture the bash-text path string,
    /// not the typed PwdLine BashObject's default hashtable ToString. The
    /// emitter wraps user command-substitutions in
    /// `| ForEach-Object { Get-BashText $_ }` so PowerShell's string
    /// interpolation receives a plain string instead of `@{BashText=...; Command=pwd}`.
    /// </summary>
    [Fact]
    public void Transpile_AssignmentWithPwdCommandSub_ExtractsBashText_RC8d()
    {
        var result = PsEmitter.Transpile("dir=$(pwd)");

        Assert.Equal("$env:dir = \"$(Invoke-BashPwd | ForEach-Object { Get-BashText $_ })\"", result);
    }

    [Fact]
    public void Transpile_TildePathDocs_EmitsHomePath()
    {
        var result = PsEmitter.Transpile("ls ~/docs");

        Assert.Equal("Invoke-BashLs $HOME\\docs", result);
    }

    [Fact]
    public void Transpile_TmpPath_EmitsTempEnv()
    {
        var result = PsEmitter.Transpile("cat /tmp/log.txt");

        Assert.Equal("Invoke-BashCat $env:TEMP\\log.txt", result);
    }

    [Fact]
    public void Transpile_DevNullAsArgument_EmitsNull()
    {
        var result = PsEmitter.Transpile("echo /dev/null");

        Assert.Equal("Invoke-BashEcho $null", result);
    }

    [Fact]
    public void Transpile_SemicolonTwoCommands_EmitsCommandList()
    {
        var result = PsEmitter.Transpile("echo a; echo b");

        Assert.Equal("Invoke-BashEcho a; Invoke-BashEcho b", result);
    }

    [Fact]
    public void Transpile_SemicolonThreeCommands_EmitsCommandList()
    {
        var result = PsEmitter.Transpile("echo a; echo b; echo c");

        Assert.Equal("Invoke-BashEcho a; Invoke-BashEcho b; Invoke-BashEcho c", result);
    }

    [Fact]
    public void Transpile_BareTilde_EmitsHome()
    {
        var result = PsEmitter.Transpile("cd ~");

        Assert.StartsWith("$__psbash_cd_target = $HOME", result);
        Assert.Contains("Set-Location -LiteralPath $__psbash_cd_resolved -ErrorAction SilentlyContinue", result);
    }

    [Fact]
    public void Transpile_CdInAndChain_WrapsStatementOperand()
    {
        var result = PsEmitter.Transpile("cd C:/Temp && echo ok");

        Assert.StartsWith("$($__psbash_cd_target = 'C:/Temp'", result);
        Assert.Contains(") && Invoke-BashEcho ok", result);
    }

    [Fact]
    public void Transpile_TildeNestedPath_EmitsHomePath()
    {
        var result = PsEmitter.Transpile("ls ~/.config/app");

        Assert.Equal("Invoke-BashLs $HOME\\.config/app", result);
    }

    [Fact]
    public void Transpile_TildeUser_Passthrough()
    {
        var result = PsEmitter.Transpile("ls ~bob/docs");

        Assert.Equal("Invoke-BashLs ~bob\\docs", result);
    }

    [Fact]
    public void Transpile_TrailingSemicolon_EmitsSingleCommand()
    {
        var result = PsEmitter.Transpile("echo a;");

        Assert.Equal("Invoke-BashEcho a", result);
    }

    [Fact]
    public void Transpile_IfThenFi_EmitsIfBlock()
    {
        var result = PsEmitter.Transpile("if cmd; then echo yes; fi");

        Assert.Equal("if (cmd) { Invoke-BashEcho yes }", result);
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

        Assert.Equal("if ((Test-Path \"file\" -PathType Leaf)) { Invoke-BashEcho yes }", result);
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

        Assert.Equal("if ((Test-Path \"dir\" -PathType Container)) { Invoke-BashEcho yes }", result);
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

        Assert.Equal("$(if ((Test-Path \"file\" -PathType Leaf)) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1; Write-Error '' -ErrorAction SilentlyContinue }) && Invoke-BashEcho yes", result);
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

        Assert.Equal("((Test-Path \"file\" -PathType Leaf) -and (Test-Path \"dir\" -PathType Container))", result);
    }

    [Fact]
    public void Transpile_ExtendedLogicalOr_EmitsOrOp()
    {
        var result = PsEmitter.Transpile("[[ $a == \"x\" || $b == \"y\" ]]");

        Assert.Equal("(($env:a -eq \"x\") -or ($env:b -eq \"y\"))", result);
    }

    [Fact]
    public void Transpile_ExtendedNotEquals_EmitsNe()
    {
        var result = PsEmitter.Transpile("[[ $a != \"bar\" ]]");

        Assert.Equal("($env:a -ne \"bar\")", result);
    }

    [Fact]
    public void Transpile_ExtendedLessThan_EmitsStringCompare()
    {
        var result = PsEmitter.Transpile("[[ $a < $b ]]");

        Assert.Equal("([string]::Compare($env:a, $env:b, [System.StringComparison]::Ordinal) -lt 0)", result);
    }

    [Fact]
    public void Transpile_ExtendedGreaterThan_EmitsStringCompare()
    {
        var result = PsEmitter.Transpile("[[ $a > $b ]]");

        Assert.Equal("([string]::Compare($env:a, $env:b, [System.StringComparison]::Ordinal) -gt 0)", result);
    }

    [Fact]
    public void Transpile_ExtendedNumericLt_StillEmitsLt()
    {
        var result = PsEmitter.Transpile("[[ $a -lt $b ]]");

        Assert.Equal("($env:a -lt $env:b)", result);
    }

    [Fact]
    public void Transpile_ExtendedNumericGt_StillEmitsGt()
    {
        var result = PsEmitter.Transpile("[[ $a -gt $b ]]");

        Assert.Equal("($env:a -gt $env:b)", result);
    }

    [Fact]
    public void Transpile_StandaloneFileTestWithOr_EmitsVoidWrapped()
    {
        var result = PsEmitter.Transpile("[ -f file ] || echo no");

        Assert.Equal("$(if ((Test-Path \"file\" -PathType Leaf)) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1; Write-Error '' -ErrorAction SilentlyContinue }) || Invoke-BashEcho no", result);
    }

    [Fact]
    public void Transpile_ForInWords_EmitsForeach()
    {
        var result = PsEmitter.Transpile("for x in a b c; do echo $x; done");

        Assert.Equal("$__psbash_iter = 0; foreach ($x in 'a','b','c') { if (++$__psbash_iter -gt ($env:PSBASH_MAX_ITERATIONS ?? 100000)) { throw \"ps-bash: loop iteration limit exceeded ($(($env:PSBASH_MAX_ITERATIONS ?? 100000)))\" }; Invoke-BashEcho $x }", result);
    }

    [Fact]
    public void Transpile_ForInNumbers_EmitsForeach()
    {
        var result = PsEmitter.Transpile("for i in 1 2 3; do echo $i; done");

        Assert.Equal("$__psbash_iter = 0; foreach ($i in 1,2,3) { if (++$__psbash_iter -gt ($env:PSBASH_MAX_ITERATIONS ?? 100000)) { throw \"ps-bash: loop iteration limit exceeded ($(($env:PSBASH_MAX_ITERATIONS ?? 100000)))\" }; Invoke-BashEcho $i }", result);
    }

    [Fact]
    public void Transpile_ForInGlob_EmitsResolvePath()
    {
        var result = PsEmitter.Transpile("for f in *.txt; do cat $f; done");

        Assert.Equal("$__psbash_iter = 0; foreach ($f in (Resolve-Path *.txt)) { if (++$__psbash_iter -gt ($env:PSBASH_MAX_ITERATIONS ?? 100000)) { throw \"ps-bash: loop iteration limit exceeded ($(($env:PSBASH_MAX_ITERATIONS ?? 100000)))\" }; Invoke-BashCat $f }", result);
    }

    [Fact]
    public void Transpile_ForImplicitArgs_EmitsArgsIteration()
    {
        var result = PsEmitter.Transpile("for x; do echo $x; done");

        // $(if ...) subexpression — a bare (if ...) is parsed by PowerShell as an
        // invocation of a command named "if" and fails at runtime; the subexpression
        // operator is required for the implicit-$@ iteration to actually run.
        Assert.Equal("$__psbash_iter = 0; foreach ($x in $(if ($global:BashPositional) { $global:BashPositional } else { $args })) { if (++$__psbash_iter -gt ($env:PSBASH_MAX_ITERATIONS ?? 100000)) { throw \"ps-bash: loop iteration limit exceeded ($(($env:PSBASH_MAX_ITERATIONS ?? 100000)))\" }; Invoke-BashEcho $x }", result);
    }

    [Fact]
    public void Transpile_ForArith_EmitsCStyleFor()
    {
        var result = PsEmitter.Transpile("for ((i=0; i<10; i++)); do echo $i; done");

        Assert.Equal("$__psbash_iter = 0; for ($i = 0; $i -lt 10; $i++) { if (++$__psbash_iter -gt ($env:PSBASH_MAX_ITERATIONS ?? 100000)) { throw \"ps-bash: loop iteration limit exceeded ($(($env:PSBASH_MAX_ITERATIONS ?? 100000)))\" }; Invoke-BashEcho $i }", result);
    }

    [Fact]
    public void Transpile_ForIn_LoopVarNotEnvVar()
    {
        var result = PsEmitter.Transpile("for i in 1 2 3; do echo $i; done");

        Assert.Contains("$i", result);
        Assert.DoesNotContain("$env:i", result);
    }

    // ANSI-C quoting ($'...') — transpile-level coverage. The differential
    // unicode roundtrip is quarantined (host non-UTF-8 stdout, Dart z0GXccJmhX2H),
    // so the \u expansion is asserted here where there is no console-encoding
    // dependency.
    [Fact]
    public void Transpile_AnsiCUnicodeEscape_ExpandsToLiteralChar()
    {
        var result = PsEmitter.Transpile("echo $'caf\\u00e9'");

        Assert.Equal("Invoke-BashEcho 'café'", result);
    }

    [Fact]
    public void Transpile_AnsiCHexEscape_ExpandsToLiteralChar()
    {
        var result = PsEmitter.Transpile("echo $'\\x41\\x42'");

        Assert.Equal("Invoke-BashEcho 'AB'", result);
    }

    [Fact]
    public void Transpile_AnsiCInjection_StaysSingleQuotedLiteral()
    {
        // Directive 12: a command-sub-looking payload inside $'...' must emit as a
        // single-quoted PS literal, never as an executable subexpression.
        var result = PsEmitter.Transpile("echo $'$(echo PWN)'");

        Assert.Equal("Invoke-BashEcho '$(echo PWN)'", result);
    }

    [Fact]
    public void Transpile_AdjacentSingleThenDouble_FlattensToOneArg()
    {
        var result = PsEmitter.Transpile("echo 'hello'\"world\"");

        Assert.Equal("Invoke-BashEcho \"helloworld\"", result);
    }

    [Fact]
    public void Transpile_ForIn_SimilarVarNameNotClobbered()
    {
        // $idx is an ordinary env var, $i is the loop binding. The loop-var
        // substitution must not clobber $idx. With RC-7, $idx (env-backed) is
        // routed through the word-split splat while $i (loop var) stays bare.
        var result = PsEmitter.Transpile("for i in 1 2; do echo $idx $i; done");

        Assert.Contains("$env:idx", result);
        Assert.Contains("Invoke-BashEcho @__bashsplat0 $i", result);
        Assert.DoesNotContain("$env:i ", result);
    }

    [Fact]
    public void Transpile_WhileTrue_EmitsWhileLoop()
    {
        var result = PsEmitter.Transpile("while true; do echo hi; done");

        Assert.Equal("$__psbash_iter = 0; while ($true) { if (++$__psbash_iter -gt ($env:PSBASH_MAX_ITERATIONS ?? 100000)) { throw \"ps-bash: loop iteration limit exceeded ($(($env:PSBASH_MAX_ITERATIONS ?? 100000)))\" }; Invoke-BashEcho hi }", result);
    }

    [Fact]
    public void Transpile_WhileCmd_EmitsWhileLoop()
    {
        var result = PsEmitter.Transpile("while cmd; do body; done");

        Assert.Equal("$__psbash_iter = 0; while (cmd) { if (++$__psbash_iter -gt ($env:PSBASH_MAX_ITERATIONS ?? 100000)) { throw \"ps-bash: loop iteration limit exceeded ($(($env:PSBASH_MAX_ITERATIONS ?? 100000)))\" }; body }", result);
    }

    [Fact]
    public void Transpile_UntilCmd_EmitsNegatedWhileLoop()
    {
        var result = PsEmitter.Transpile("until cmd; do body; done");

        Assert.Equal("$__psbash_iter = 0; while (-not (cmd)) { if (++$__psbash_iter -gt ($env:PSBASH_MAX_ITERATIONS ?? 100000)) { throw \"ps-bash: loop iteration limit exceeded ($(($env:PSBASH_MAX_ITERATIONS ?? 100000)))\" }; body }", result);
    }

    [Fact]
    public void Transpile_WhileReadLine_EmitsForEachObjectPipeline()
    {
        var result = PsEmitter.Transpile("while read line; do echo $line; done");

        Assert.Equal(
            "ForEach-Object { if ($_.PSObject.Properties['BashText']) { $_.BashText } else { \"$_\" } } | ForEach-Object { ($_ -replace \"`n$\",\"\") -split \"`n\" } | ForEach-Object { Invoke-BashEcho $_ }",
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

        Assert.Equal("$__psbash_iter = 0; while ((Test-Path \"file\" -PathType Leaf)) { if (++$__psbash_iter -gt ($env:PSBASH_MAX_ITERATIONS ?? 100000)) { throw \"ps-bash: loop iteration limit exceeded ($(($env:PSBASH_MAX_ITERATIONS ?? 100000)))\" }; Invoke-BashEcho yes }", result);
    }

    [Fact]
    public void Transpile_UntilFileTest_EmitsNegatedWhileWithTestPath()
    {
        var result = PsEmitter.Transpile("until [ -f file ]; do sleep 1; done");

        Assert.Equal("$__psbash_iter = 0; while (-not ((Test-Path \"file\" -PathType Leaf))) { if (++$__psbash_iter -gt ($env:PSBASH_MAX_ITERATIONS ?? 100000)) { throw \"ps-bash: loop iteration limit exceeded ($(($env:PSBASH_MAX_ITERATIONS ?? 100000)))\" }; Invoke-BashSleep 1 }", result);
    }

    [Fact]
    public void Transpile_WhileMultipleBodyCommands_EmitsAll()
    {
        var result = PsEmitter.Transpile("while true; do echo a; echo b; done");

        Assert.Equal("$__psbash_iter = 0; while ($true) { if (++$__psbash_iter -gt ($env:PSBASH_MAX_ITERATIONS ?? 100000)) { throw \"ps-bash: loop iteration limit exceeded ($(($env:PSBASH_MAX_ITERATIONS ?? 100000)))\" }; Invoke-BashEcho a; Invoke-BashEcho b }", result);
    }

    [Fact]
    public void Transpile_SimpleCase_EmitsSwitch()
    {
        var result = PsEmitter.Transpile("case $x in a) echo a;; b) echo b;; esac");

        Assert.Equal("switch ($env:x) { 'a' { Invoke-BashEcho a } 'b' { Invoke-BashEcho b } }", result);
    }

    [Fact]
    public void Transpile_CaseMultiplePatterns_EmitsSeparateClauses()
    {
        var result = PsEmitter.Transpile("case $x in a|b) echo ab;; esac");

        Assert.Equal("switch ($env:x) { 'a' { Invoke-BashEcho ab } 'b' { Invoke-BashEcho ab } }", result);
    }

    [Fact]
    public void Transpile_CaseDefaultStar_EmitsDefault()
    {
        var result = PsEmitter.Transpile("case $x in a) echo a;; *) echo other;; esac");

        Assert.Equal("switch ($env:x) { 'a' { Invoke-BashEcho a } default { Invoke-BashEcho other } }", result);
    }

    [Fact]
    public void Transpile_CaseFallThrough_RunsNextArmBody()
    {
        // `;&` = fall through: matching `a` runs `echo a` AND the next arm's
        // `echo b`. PowerShell switch has no clause fall-through, so the next
        // body is inlined into the matched clause.
        var result = PsEmitter.Transpile("case $x in a) echo a ;& b) echo b;; esac");

        Assert.Equal(
            "switch ($env:x) { 'a' { Invoke-BashEcho a; Invoke-BashEcho b } 'b' { Invoke-BashEcho b } }",
            result);
    }

    [Fact]
    public void Transpile_Select_DegradesToBlockComment()
    {
        var result = PsEmitter.Transpile("select x in a b c; do echo $x; done");

        Assert.Equal("<# ps-bash: 'select x' menu loop is not supported (omitted) #>", result);
    }

    [Fact]
    public void Transpile_SelectInScript_OtherStatementsStillEmit()
    {
        // Degradation: select no longer aborts the whole transpile; the block
        // comment is inline-safe so the surrounding commands still emit.
        var result = PsEmitter.Transpile("echo before; select x in a; do echo $x; done; echo after");

        Assert.Contains("Invoke-BashEcho before", result);
        Assert.Contains("Invoke-BashEcho after", result);
        Assert.Contains("<# ps-bash: 'select x'", result);
    }

    [Fact]
    public void Transpile_CaseContinueTest_EmitsOwnBodyOnly()
    {
        // `;;&` (continue testing) emits just the arm's own body, no break —
        // PowerShell switch's default no-break behavior continues testing
        // subsequent clauses (the bash ;;& semantic for non-overlapping patterns).
        var result = PsEmitter.Transpile("case $x in a) echo a ;;& b) echo b;; esac");

        Assert.Equal(
            "switch ($env:x) { 'a' { Invoke-BashEcho a } 'b' { Invoke-BashEcho b } }",
            result);
    }

    [Fact]
    public void Transpile_CaseChainedFallThrough_InlinesAllChainedBodies()
    {
        // `;&` -> `;&` -> `;;`: matching `a` runs a, b, AND c.
        var result = PsEmitter.Transpile("case $x in a) echo a ;& b) echo b ;& c) echo c;; esac");

        Assert.Equal(
            "switch ($env:x) { " +
            "'a' { Invoke-BashEcho a; Invoke-BashEcho b; Invoke-BashEcho c } " +
            "'b' { Invoke-BashEcho b; Invoke-BashEcho c } " +
            "'c' { Invoke-BashEcho c } }",
            result);
    }

    [Fact]
    public void Transpile_NestedCase_EmitsNestedSwitch()
    {
        var result = PsEmitter.Transpile(
            "case $x in a) case $y in b) echo b;; esac;; esac");

        Assert.Equal(
            "switch ($env:x) { 'a' { switch ($env:y) { 'b' { Invoke-BashEcho b } } } }",
            result);
    }

    [Fact]
    public void Transpile_CaseWithGlobPattern_EmitsWildcard()
    {
        var result = PsEmitter.Transpile("case $f in *.txt) echo text;; *) echo other;; esac");

        Assert.Equal(
            "switch -Wildcard ($env:f) { '*.txt' { Invoke-BashEcho text } default { Invoke-BashEcho other } }",
            result);
    }

    // Helper constant for the save/restore preamble and epilogue added around every
    // function body so that recursive calls each see their own positional args.
    private const string FnPre = "$__bp = $global:BashPositional; $global:BashPositional = @() + $args; try { ";
    private const string FnPost = " } finally { $global:BashPositional = $__bp }";

    [Fact]
    public void Transpile_FunctionKeywordForm_EmitsPsFunction()
    {
        var result = PsEmitter.Transpile("function greet { Invoke-BashEcho hello }");

        Assert.Equal($"function greet {{ {FnPre}Invoke-BashEcho hello{FnPost} }}", result);
    }

    [Fact]
    public void Transpile_FunctionParensForm_EmitsPsFunction()
    {
        var result = PsEmitter.Transpile("greet() { echo hello }");

        Assert.Equal($"function greet {{ {FnPre}Invoke-BashEcho hello{FnPost} }}", result);
    }

    [Fact]
    public void Transpile_FunctionParensWithSpace_EmitsPsFunction()
    {
        var result = PsEmitter.Transpile("greet () { echo hello }");

        Assert.Equal($"function greet {{ {FnPre}Invoke-BashEcho hello{FnPost} }}", result);
    }

    [Fact]
    public void Transpile_FunctionWithLocalVars_EmitsLocalAssignment()
    {
        var result = PsEmitter.Transpile("function add { local result=42; echo $result }");

        Assert.Equal($"function add {{ {FnPre}$result = \"42\"; Invoke-BashEcho $result{FnPost} }}", result);
    }

    [Fact]
    public void Transpile_FunctionCallingFunction_EmitsNestedCalls()
    {
        var result = PsEmitter.Transpile(
            "function greet { Invoke-BashEcho hello }; function main { greet }");

        Assert.Equal(
            $"function greet {{ {FnPre}Invoke-BashEcho hello{FnPost} }}; function main {{ {FnPre}greet{FnPost} }}",
            result);
    }

    [Fact]
    public void Transpile_FunctionWithMultilineBody_EmitsFunction()
    {
        var result = PsEmitter.Transpile("function setup {\n  echo start\n  echo end\n}");

        Assert.Equal($"function setup {{ {FnPre}Invoke-BashEcho start; Invoke-BashEcho end{FnPost} }}", result);
    }

    /// <summary>
    /// DART-ccPtGZB92fur: recursive function must save/restore $global:BashPositional
    /// so inner frames see their own args, not the outer frame's.
    /// The function body must wrap with: save -> set from $args -> try{body}finally{restore}.
    /// </summary>
    [Fact]
    public void Transpile_Function_SavesAndRestoresBashPositionalAroundBody()
    {
        var result = PsEmitter.Transpile("f() { echo $1; }");

        var expected = $"function f {{ {FnPre}Invoke-BashEcho $(if ($global:BashPositional) {{ $global:BashPositional[0] }} else {{ $args[0] }}){FnPost} }}";
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Transpile_SimpleSubshell_EmitsScriptBlockInvocation()
    {
        var result = PsEmitter.Transpile("(Invoke-BashEcho hello; Invoke-BashEcho world)");

        Assert.Equal("try { Push-Location; Invoke-BashEcho hello; Invoke-BashEcho world } finally { Pop-Location }", result);
    }

    [Fact]
    public void Transpile_BraceGroup_EmitsInline()
    {
        var result = PsEmitter.Transpile("{ Invoke-BashEcho hello; Invoke-BashEcho world; }");

        Assert.Equal("Invoke-BashEcho hello; Invoke-BashEcho world", result);
    }

    [Fact]
    public void Transpile_SubshellWithRedirect_EmitsBashRedirect()
    {
        var result = PsEmitter.Transpile("(echo hello) > out.txt");

        Assert.Equal("try { Push-Location; Invoke-BashEcho hello } finally { Pop-Location } | Invoke-BashRedirect -Path out.txt", result);
    }

    [Fact]
    public void Transpile_NestedSubshells_EmitsNestedBlocks()
    {
        var result = PsEmitter.Transpile("(echo a; (echo b))");

        Assert.Equal("try { Push-Location; Invoke-BashEcho a; try { Push-Location; Invoke-BashEcho b } finally { Pop-Location } } finally { Pop-Location }", result);
    }

    [Fact]
    public void Transpile_SubshellCdIsolation_EmitsPushdPopd()
    {
        var result = PsEmitter.Transpile("(cd /tmp && pwd) && pwd");

        Assert.StartsWith("try { Push-Location; $($__psbash_cd_target = '/tmp'", result);
        Assert.Contains("&& Invoke-BashPwd } finally { Pop-Location } && Invoke-BashPwd", result);
    }

    [Fact]
    public void Transpile_SetDashDash_EmitsBashPositional()
    {
        var result = PsEmitter.Transpile("set -- a b c");

        Assert.Equal("$global:BashPositional = @('a', 'b', 'c')", result);
    }

    [Fact]
    public void Transpile_SetDashDashEmpty_EmitsEmptyPositional()
    {
        var result = PsEmitter.Transpile("set --");

        Assert.Equal("$global:BashPositional = @()", result);
    }

    [Fact]
    public void Transpile_Background_EmitsInvokeBashBackground()
    {
        var result = PsEmitter.Transpile("sleep 0.1 & echo hello");

        Assert.Equal("Invoke-BashBackground { Invoke-BashSleep 0.1 }; Invoke-BashEcho hello", result);
    }

    [Fact]
    public void Transpile_ArithSub_BasicAddition()
    {
        var result = PsEmitter.Transpile("echo $((x + 1))");
        Assert.Equal("Invoke-BashEcho $([int]$env:x + 1)", result);
    }

    [Fact]
    public void Transpile_ArithSub_LiteralAddition()
    {
        var result = PsEmitter.Transpile("echo $((2 + 3))");
        Assert.Equal("Invoke-BashEcho $(2 + 3)", result);
    }

    [Fact]
    public void Transpile_ArithSub_Multiplication()
    {
        var result = PsEmitter.Transpile("echo $((x * y))");
        Assert.Equal("Invoke-BashEcho $([int]$env:x * [int]$env:y)", result);
    }

    [Fact]
    public void Transpile_ArithCommand_Increment()
    {
        var result = PsEmitter.Transpile("(( x++ ))");
        Assert.Equal("$env:x = [int]$env:x + 1", result);
    }

    [Fact]
    public void Transpile_ArithCommand_Decrement()
    {
        var result = PsEmitter.Transpile("(( x-- ))");
        Assert.Equal("$env:x = [int]$env:x + -1", result);
    }

    [Fact]
    public void Transpile_ArithCommand_PreIncrement()
    {
        var result = PsEmitter.Transpile("(( ++x ))");
        Assert.Equal("$env:x = [int]$env:x + 1", result);
    }

    [Fact]
    public void Transpile_ArithCommand_PreDecrement()
    {
        var result = PsEmitter.Transpile("(( --x ))");
        Assert.Equal("$env:x = [int]$env:x + -1", result);
    }

    [Fact]
    public void Transpile_ArithCommand_Comparison_GreaterThan()
    {
        var result = PsEmitter.Transpile("(( x > 5 ))");
        Assert.Equal("[int]$env:x -gt 5", result);
    }

    [Fact]
    public void Transpile_ArithCommand_Comparison_LessThan()
    {
        var result = PsEmitter.Transpile("(( x < 5 ))");
        Assert.Equal("[int]$env:x -lt 5", result);
    }

    [Fact]
    public void Transpile_ArithCommand_Comparison_GreaterEqual()
    {
        var result = PsEmitter.Transpile("(( x >= 5 ))");
        Assert.Equal("[int]$env:x -ge 5", result);
    }

    [Fact]
    public void Transpile_ArithCommand_Comparison_LessEqual()
    {
        var result = PsEmitter.Transpile("(( x <= 5 ))");
        Assert.Equal("[int]$env:x -le 5", result);
    }

    [Fact]
    public void Transpile_ArithCommand_Comparison_Equal()
    {
        var result = PsEmitter.Transpile("(( x == 5 ))");
        Assert.Equal("[int]$env:x -eq 5", result);
    }

    [Fact]
    public void Transpile_ArithCommand_Comparison_NotEqual()
    {
        var result = PsEmitter.Transpile("(( x != 5 ))");
        Assert.Equal("[int]$env:x -ne 5", result);
    }

    [Fact]
    public void Transpile_ArithCommand_Ternary()
    {
        var result = PsEmitter.Transpile("(( x > 0 ? 1 : 0 ))");
        Assert.Equal("if ([int]$env:x -gt 0) { 1 } else { 0 }", result);
    }

    [Fact]
    public void Transpile_ArithSub_InAssignment()
    {
        var result = PsEmitter.Transpile("result=$((x + 1))");
        Assert.Equal("$env:result = \"$([int]$env:x + 1)\"", result);
    }

    [Fact]
    public void Transpile_ArithSub_Power()
    {
        var result = PsEmitter.Transpile("echo $((2 ** 3))");
        Assert.Equal("Invoke-BashEcho $(2 ** 3)", result);
    }

    [Fact]
    public void Transpile_ArithSub_Modulo()
    {
        var result = PsEmitter.Transpile("echo $((10 % 3))");
        Assert.Equal("Invoke-BashEcho $(10 % 3)", result);
    }

    [Fact]
    public void Transpile_ArithSub_NestedInString()
    {
        var result = PsEmitter.Transpile("echo \"result is $((x + 1))\"");
        Assert.Equal("Invoke-BashEcho \"result is $([int]$env:x + 1)\"", result);
    }

    [Fact]
    public void Transpile_GlobStar_PassesThrough()
    {
        var result = PsEmitter.Transpile("echo *.py");
        Assert.Equal("Invoke-BashEcho *.py", result);
    }

    [Fact]
    public void Transpile_GlobQuestionMark_PassesThrough()
    {
        var result = PsEmitter.Transpile("echo file?.txt");
        Assert.Equal("Invoke-BashEcho file?.txt", result);
    }

    [Fact]
    public void Transpile_GlobCharClass_PassesThrough()
    {
        var result = PsEmitter.Transpile("echo [abc]*");
        Assert.Equal("Invoke-BashEcho [abc]*", result);
    }

    [Fact]
    public void Transpile_GlobMixedWithLiteral_PassesThrough()
    {
        var result = PsEmitter.Transpile("echo src/*.py");
        Assert.Equal("Invoke-BashEcho src/*.py", result);
    }

    [Fact]
    public void Transpile_GlobStandalone_PassesThrough()
    {
        var result = PsEmitter.Transpile("echo *");
        Assert.Equal("Invoke-BashEcho *", result);
    }

    [Fact]
    public void Transpile_ExtGlob_PassesThrough()
    {
        var result = PsEmitter.Transpile("echo +(*.py|*.js)");
        Assert.Equal("Invoke-BashEcho +(*.py|*.js)", result);
    }

    [Fact]
    public void Transpile_GlobPrefix_PassesThrough()
    {
        var result = PsEmitter.Transpile("echo *.log");
        Assert.Equal("Invoke-BashEcho *.log", result);
    }

    [Fact]
    public void Transpile_GlobSuffix_PassesThrough()
    {
        var result = PsEmitter.Transpile("echo test*");
        Assert.Equal("Invoke-BashEcho test*", result);
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
        Assert.Equal("$__psbash_iter = 0; foreach ($f in (Resolve-Path [abc]*.txt)) { if (++$__psbash_iter -gt ($env:PSBASH_MAX_ITERATIONS ?? 100000)) { throw \"ps-bash: loop iteration limit exceeded ($(($env:PSBASH_MAX_ITERATIONS ?? 100000)))\" }; Invoke-BashCat $f }", result);
    }

    [Fact]
    public void Transpile_BracedTuple_EmitsArray()
    {
        var result = PsEmitter.Transpile("echo {a,b,c}");
        Assert.Equal("Invoke-BashEcho @('a','b','c')", result);
    }

    [Fact]
    public void Transpile_BracedRange_EmitsPsRange()
    {
        var result = PsEmitter.Transpile("echo {1..10}");
        Assert.Equal("Invoke-BashEcho @(1..10)", result);
    }

    [Fact]
    public void Transpile_BracedRangeLeadingZeros_EmitsZeroPaddedArray()
    {
        var result = PsEmitter.Transpile("echo {01..05}");
        Assert.Equal("Invoke-BashEcho @('01','02','03','04','05')", result);
    }

    [Fact]
    public void Transpile_BracedTupleWithPrefixSuffix_EmitsExpandedArray()
    {
        var result = PsEmitter.Transpile("echo file{1,2,3}.txt");
        Assert.Equal("Invoke-BashEcho @('file1.txt','file2.txt','file3.txt')", result);
    }

    [Fact]
    public void Transpile_BracedRangeWithPrefix_EmitsExpandedArray()
    {
        var result = PsEmitter.Transpile("echo log{1..3}.txt");
        Assert.Equal("Invoke-BashEcho @('log1.txt','log2.txt','log3.txt')", result);
    }

    [Fact]
    public void Transpile_DiffWithTwoInputProcessSubs()
    {
        var result = PsEmitter.Transpile("diff <(ls dir1) <(ls dir2)");
        Assert.Equal("Invoke-BashDiff (Invoke-ProcessSub { Invoke-BashLs dir1 }) (Invoke-ProcessSub { Invoke-BashLs dir2 })", result);
    }

    [Fact]
    public void Transpile_DiffWithSeqProcessSubs_StaysOnTempFilePath()
    {
        var result = PsEmitter.Transpile("diff <(seq 1 10) <(seq 1 10)");
        Assert.Equal("Invoke-BashDiff (Invoke-ProcessSub { Invoke-BashSeq 1 10 }) (Invoke-ProcessSub { Invoke-BashSeq 1 10 })", result);
    }

    [Fact]
    public void Transpile_SortWithInputProcessSub_RoutesToPipelineObjectPath()
    {
        var result = PsEmitter.Transpile("sort -u <(cat foo)");
        Assert.Equal("Invoke-BashSort -u (Invoke-ProcessSubPipeline { Invoke-BashCat foo })", result);
    }

    [Fact]
    public void Transpile_HeadWithInputProcessSub_RoutesToPipelineObjectPath()
    {
        var result = PsEmitter.Transpile("head -n 1 <(seq 1 10)");
        Assert.Equal("Invoke-BashHead -n 1 (Invoke-ProcessSubPipeline { Invoke-BashSeq 1 10 })", result);
    }

    [Fact]
    public void Transpile_TailWithInputProcessSub_RoutesToPipelineObjectPath()
    {
        var result = PsEmitter.Transpile("tail -n 1 <(seq 1 10)");
        Assert.Equal("Invoke-BashTail -n 1 (Invoke-ProcessSubPipeline { Invoke-BashSeq 1 10 })", result);
    }

    [Fact]
    public void Transpile_UniqWithInputProcessSub_RoutesToPipelineObjectPath()
    {
        var result = PsEmitter.Transpile("uniq <(seq 1 3)");
        Assert.Equal("Invoke-BashUniq (Invoke-ProcessSubPipeline { Invoke-BashSeq 1 3 })", result);
    }

    [Fact]
    public void Transpile_WcWithInputProcessSub_StaysOnTempFilePath()
    {
        // RC-8b: wc reclassified off Tier-2 pipeline allowlist back to Tier-1 temp-file.
        // wc's output format depends on file-vs-stdin mode (file echoes filename, stdin doesn't),
        // so the stdin-substitutable assumption doesn't hold for wc.
        var result = PsEmitter.Transpile("wc -l <(seq 1 100)");
        Assert.Equal("Invoke-BashWc -l (Invoke-ProcessSub { Invoke-BashSeq 1 100 })", result);
    }

    [Fact]
    public void Transpile_SortWithTwoProcessSubs_StaysOnTempFilePath()
    {
        var result = PsEmitter.Transpile("sort <(seq 1 3) <(seq 4 6)");
        Assert.Equal("Invoke-BashSort (Invoke-ProcessSub { Invoke-BashSeq 1 3 }) (Invoke-ProcessSub { Invoke-BashSeq 4 6 })", result);
    }

    [Fact]
    public void Transpile_CommWithProcessSubs_StaysOnTempFilePath()
    {
        var result = PsEmitter.Transpile("comm <(sort left) <(sort right)");
        Assert.Equal("Invoke-BashComm (Invoke-ProcessSub { Invoke-BashSort left }) (Invoke-ProcessSub { Invoke-BashSort right })", result);
    }

    [Fact]
    public void Transpile_CmpWithProcessSubs_StaysOnTempFilePath()
    {
        var result = PsEmitter.Transpile("cmp <(sort left) <(sort right)");
        Assert.Equal("cmp (Invoke-ProcessSub { Invoke-BashSort left }) (Invoke-ProcessSub { Invoke-BashSort right })", result);
    }

    [Fact]
    public void Transpile_UnknownExternalWithProcessSub_StaysOnTempFilePath()
    {
        var result = PsEmitter.Transpile("external-tool <(seq 1 3)");
        Assert.Equal("external-tool (Invoke-ProcessSub { Invoke-BashSeq 1 3 })", result);
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
            "Invoke-BashDiff (Invoke-ProcessSub { Invoke-BashSort (Invoke-ProcessSubPipeline { Invoke-BashCat file1 }) }) (Invoke-ProcessSub { Invoke-BashSort file2 })",
            result);
    }

    [Fact]
    public void Transpile_ProcessSubWithPipe()
    {
        var result = PsEmitter.Transpile("diff <(sort file1 | uniq) file2");
        Assert.Equal("Invoke-BashDiff (Invoke-ProcessSub { Invoke-BashSort file1 | Invoke-BashUniq }) file2", result);
    }

    [Fact]
    public void Transpile_GrepWithProcessSub()
    {
        var result = PsEmitter.Transpile("grep -f <(cat patterns.txt) data.txt");
        Assert.Equal("Invoke-BashGrep -f (Invoke-ProcessSub { Invoke-BashCat patterns.txt }) data.txt", result);
    }

    // --- T10 step 1+2: string-capture classifier for source/dot <(...) ---
    //
    // These tests lock in that the emitter classifies `source <(producer)` and
    // `. <(producer)` as the string-capture path (Invoke-ProcessSubSource), NOT
    // the temp-file path (Invoke-ProcessSub). Source-style consumers need the
    // producer's bash text captured + transpiled + executed in caller scope —
    // a temp-file path would only give them a file path back, which `source`
    // would treat as a script filename. See psm1 Invoke-ProcessSubSource.

    [Fact]
    public void Transpile_SourceWithProcessSub_RoutesToStringCapturePath()
    {
        var result = PsEmitter.Transpile("source <(echo 'PSBASH_T10_VAR=hello')");
        Assert.Equal("Invoke-ProcessSubSource { Invoke-BashEcho 'PSBASH_T10_VAR=hello' }", result);
    }

    [Fact]
    public void Transpile_DotWithProcessSub_RoutesToStringCapturePath()
    {
        // bash's `.` is a synonym for `source` and must classify identically.
        var result = PsEmitter.Transpile(". <(echo 'A=1')");
        Assert.Equal("Invoke-ProcessSubSource { Invoke-BashEcho 'A=1' }", result);
    }

    [Fact]
    public void Transpile_SourceWithProcessSubMultiCommand_PreservesProducerPipeline()
    {
        // The producer can be any pipeline; classifier must transpile it as a
        // nested command and wrap the whole thing in Invoke-ProcessSubSource.
        var result = PsEmitter.Transpile("source <(cat config.env | grep -v '^#')");
        Assert.Equal(
            "Invoke-ProcessSubSource { Invoke-BashCat config.env | Invoke-BashGrep -v '^#' }",
            result);
    }

    [Fact]
    public void Transpile_SourceWithLiteralFile_StaysOnPassthroughPath()
    {
        // Negative case: source with a real filename must NOT route through
        // the string-capture path — it goes through Invoke-BashSource as a
        // normal file argument. This guards against the classifier overreaching.
        var result = PsEmitter.Transpile("source script.sh");
        Assert.Equal("Invoke-BashSource script.sh", result);
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
        Assert.Equal("Invoke-BashEcho $arr[0]", result);
    }

    [Fact]
    public void Transpile_ArrayAllElements_EmitsPsArrayRef()
    {
        var result = PsEmitter.Transpile("echo ${arr[@]}");
        Assert.Equal("Invoke-BashEcho $arr", result);
    }

    [Fact]
    public void Transpile_ArrayLength_EmitsPsCount()
    {
        var result = PsEmitter.Transpile("echo ${#arr[@]}");
        Assert.Equal("Invoke-BashEcho $arr.Count", result);
    }

    [Fact]
    public void Transpile_ArrayIteration_EmitsForEachOverArray()
    {
        var result = PsEmitter.Transpile("for item in ${arr[@]}; do echo $item; done");
        Assert.Equal("$__psbash_iter = 0; foreach ($item in $arr) { if (++$__psbash_iter -gt ($env:PSBASH_MAX_ITERATIONS ?? 100000)) { throw \"ps-bash: loop iteration limit exceeded ($(($env:PSBASH_MAX_ITERATIONS ?? 100000)))\" }; Invoke-BashEcho $item }", result);
    }

    [Fact]
    public void Transpile_DeclareAssociativeArray_EmitsHashtable()
    {
        var result = PsEmitter.Transpile("declare -A map");
        Assert.Equal("$global:map = @{}" + "", result);
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
        Assert.Equal("Invoke-BashEcho $map['key']", result);
    }

    [Fact]
    public void Transpile_BasicHeredoc_EmitsDoubleQuoteHereString()
    {
        var result = PsEmitter.Transpile("cat <<EOF\nline 1\nline 2\nEOF");

        Assert.Equal("@\"\nline 1\nline 2\n\n\"@ | Emit-BashLine | Invoke-BashCat", result);
    }

    [Fact]
    public void Transpile_HeredocWithVariableExpansion_EmitsPsEnvVar()
    {
        var result = PsEmitter.Transpile("cat <<EOF\nhello $NAME\nEOF");

        Assert.Equal("@\"\nhello $env:NAME\n\n\"@ | Emit-BashLine | Invoke-BashCat", result);
    }

    [Fact]
    public void Transpile_QuotedDelimiter_EmitsSingleQuoteHereString()
    {
        var result = PsEmitter.Transpile("cat <<'EOF'\nhello $NAME\nEOF");

        Assert.Equal("@'\nhello $NAME\n\n'@ | Emit-BashLine | Invoke-BashCat", result);
    }

    // Bug: the heredoc body was rebuilt by space-joining lexer tokens, which
    // split punctuation (`(` `)` `<` `>`) into operator tokens and re-joined
    // them with spaces — so a git commit message piped through a heredoc came
    // back as "best-ranked ( score-desc, newest-first ) < x@y.z >". The body is
    // raw text and must be sliced verbatim from source.
    [Fact]
    public void Transpile_HeredocBodyWithPunctuation_PreservedVerbatim()
    {
        var result = PsEmitter.Transpile("cat <<'EOF'\nbest-ranked (score-desc, newest-first) <x@y.z>\nEOF");

        Assert.Equal("@'\nbest-ranked (score-desc, newest-first) <x@y.z>\n\n'@ | Emit-BashLine | Invoke-BashCat", result);
    }

    // Bug: the lexer collapses runs of whitespace, so a token-joined body lost
    // the original spacing. Raw slicing keeps it.
    [Fact]
    public void Transpile_HeredocBodyWithRunsOfSpaces_PreservesSpacing()
    {
        var result = PsEmitter.Transpile("cat <<'EOF'\na    b      c\nEOF");

        Assert.Equal("@'\na    b      c\n\n'@ | Emit-BashLine | Invoke-BashCat", result);
    }

    // Bug: the lexer drops '#' comment lines, so a body line beginning with '#'
    // vanished from the token stream. Raw slicing recovers it.
    [Fact]
    public void Transpile_HeredocBodyWithHashLine_NotDroppedAsComment()
    {
        var result = PsEmitter.Transpile("cat <<'EOF'\n# !/bin/sh style line\nEOF");

        Assert.Equal("@'\n# !/bin/sh style line\n\n'@ | Emit-BashLine | Invoke-BashCat", result);
    }

    // Bug: '#' begins a comment only at a word boundary in bash; a '#' mid-word
    // is literal. The lexer broke words on '#' and ate the rest as a comment, so
    // "abc#def" lost "#def" and URLs lost their fragment.
    [Fact]
    public void Transpile_HashMidWord_KeptAsLiteral()
    {
        Assert.Equal("Invoke-BashEcho abc#def", PsEmitter.Transpile("echo abc#def"));
        Assert.Equal("Invoke-BashEcho http://x/p#section", PsEmitter.Transpile("echo http://x/p#section"));
    }

    // Bug: command-substitution boundary scanning ignored quotes, so a ')' inside
    // a string closed the $( ) early — "$(grep \")\" f)" leaked "f)" out of the
    // sub. Boundary scanning is now quote-aware (shared with the lexer).
    [Fact]
    public void Transpile_CommandSub_QuotedCloseParen_NotTreatedAsClose()
    {
        var result = PsEmitter.Transpile("echo $(grep \")\" file.txt)");

        Assert.Contains("Invoke-BashGrep \")\" file.txt", result);
        Assert.DoesNotContain("file.txt)\"", result); // no leaked operand outside the sub
    }

    // Bug: the double-quote scanner terminated at the first inner '"', breaking a
    // $(...) embedded in a double-quoted word. It now recurses through $(...).
    [Fact]
    public void Transpile_NestedDoubleQuoteInCommandSubInDoubleQuote_Intact()
    {
        var result = PsEmitter.Transpile("echo \"$(echo \"hi there\")\"");

        Assert.Equal(
            "Invoke-BashEcho \"$(Invoke-BashEcho \"hi there\" | ForEach-Object { Get-BashText $_ })\"",
            result);
    }

    // Bug: process-substitution boundary scanning ignored quotes too.
    [Fact]
    public void Transpile_ProcessSub_QuotedCloseParen_NotTreatedAsClose()
    {
        var result = PsEmitter.Transpile("cat <(grep \")\" a)");

        Assert.Contains("Invoke-BashGrep \")\" a", result);
    }

    [Fact]
    public void Transpile_DLessDash_StripsLeadingTabs()
    {
        var result = PsEmitter.Transpile("cat <<-EOF\n\tline 1\n\tline 2\nEOF");

        Assert.Equal("@\"\nline 1\nline 2\n\n\"@ | Emit-BashLine | Invoke-BashCat", result);
    }

    [Fact]
    public void Transpile_HeredocWithCommandArgs_PipesToCommand()
    {
        var result = PsEmitter.Transpile("grep -i foo <<EOF\nhello foo\nbar\nEOF");

        Assert.Equal("@\"\nhello foo\nbar\n\n\"@ | Emit-BashLine | Invoke-BashGrep -i foo", result);
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
        Assert.Equal("Invoke-BashEcho \"a,b,c\" | Invoke-BashAwk \"-F,\" '{print $1, $3}'", result);
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
        Assert.Equal("Invoke-BashEcho \"hello $env:NAME\"", result);
    }

    [Fact]
    public void Transpile_BracedVarInsideDoubleQuotes_EmitsDollarEnv()
    {
        var result = PsEmitter.Transpile("echo \"${FOO} world\"");
        Assert.Equal("Invoke-BashEcho \"$env:FOO world\"", result);
    }

    // Bug: [void]() wrapping needed when assignment is chained with && or ||
    [Fact]
    public void Transpile_ExportWithAndChain_WrapsInVoid()
    {
        // The export assignment keeps its [void](...) wrap; the `echo $FOO`
        // operand goes through the RC-7 word-split splat (bare unquoted env
        // var). The splat command is wrapped in $(& { ... }) so it stays a
        // single and-or-list element.
        var result = PsEmitter.Transpile("export FOO=bar && echo $FOO");
        Assert.Equal(
            "[void]($env:FOO = \"bar\") && $(& { $__bashsplat0 = " +
            "@(if ([string]::IsNullOrEmpty($env:FOO)) { @() } " +
            "else { @($env:FOO -split '\\s+') }); " +
            "Invoke-BashEcho @__bashsplat0 })",
            result);
    }

    [Fact]
    public void Transpile_AssignmentWithOrChain_WrapsInVoid()
    {
        var result = PsEmitter.Transpile("x=1 || Invoke-BashEcho failed");
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

    // REFACTOR-4: `cmd >&2` rewrites to Write-BashHostStderr, NOT
    // [Console]::Error.WriteLine. The host's inherited fd 2 is detached to
    // /dev/null (commit cc8bf88's hang fix); Write-BashHostStderr routes
    // through $Host.UI.WriteErrorLine into a STDERR-tagged IPC frame.
    [Fact]
    public void Transpile_StdoutToStderr_EmitsHostStderrPipe()
    {
        var result = PsEmitter.Transpile("echo hello >&2");
        Assert.Equal("Invoke-BashEcho hello | ForEach-Object { Write-BashHostStderr $_ }", result);
    }

    [Fact]
    public void Transpile_ExplicitFd1ToStderr_EmitsHostStderrPipe()
    {
        var result = PsEmitter.Transpile("echo hello 1>&2");
        Assert.Equal("Invoke-BashEcho hello | ForEach-Object { Write-BashHostStderr $_ }", result);
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
    public void Transpile_MultipleHeredocs_UsesLastForStdin()
    {
        var result = PsEmitter.Transpile("cat <<EOF1 <<EOF2\nfirst\nEOF1\nsecond\nEOF2");

        Assert.Contains("second", result);
        Assert.Contains("Invoke-BashCat", result);
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

    // read -p "prompt" VAR -> Invoke-BashRead -p "prompt" VAR
    [Fact]
    public void Transpile_ReadWithPrompt_EmitsInvokeBashRead()
    {
        var result = PsEmitter.Transpile("read -p \"Enter name: \" NAME");
        Assert.Contains("Invoke-BashRead", result);
        Assert.Contains("Enter name:", result);
        Assert.Contains("NAME", result);
    }

    [Fact]
    public void Transpile_ReadNoPrompt_EmitsInvokeBashRead()
    {
        var result = PsEmitter.Transpile("read -r LINE");
        Assert.Equal("Invoke-BashRead -r LINE", result);
    }

    // set -euo pipefail -> $ErrorActionPreference = 'Stop'; Set-StrictMode -Version Latest
    [Fact]
    public void Transpile_SetEuoPipefail_EmitsErrorActionStopAndStrictMode()
    {
        var result = PsEmitter.Transpile("set -euo pipefail");
        Assert.Equal("$ErrorActionPreference = 'Stop'; $global:__BashErrexit = $true; Set-StrictMode -Version Latest", result);
    }

    [Fact]
    public void Transpile_SetOErrexit_EmitsErrorActionStop()
    {
        var result = PsEmitter.Transpile("set -o errexit");
        Assert.Equal("$ErrorActionPreference = 'Stop'; $global:__BashErrexit = $true", result);
    }

    [Fact]
    public void Transpile_SetX_EmitsPSDebugTrace()
    {
        var result = PsEmitter.Transpile("set -x");
        Assert.Equal("Set-PSDebug -Trace 1", result);
    }

    [Fact]
    public void Transpile_SetU_EmitsStrictMode()
    {
        var result = PsEmitter.Transpile("set -u");
        Assert.Equal("Set-StrictMode -Version Latest", result);
    }

    [Fact]
    public void Transpile_SetONounset_EmitsStrictMode()
    {
        var result = PsEmitter.Transpile("set -o nounset");
        Assert.Equal("Set-StrictMode -Version Latest", result);
    }

    [Fact]
    public void Transpile_SetEU_EmitsErrorActionStopAndStrictMode()
    {
        var result = PsEmitter.Transpile("set -eu");
        Assert.Equal("$ErrorActionPreference = 'Stop'; $global:__BashErrexit = $true; Set-StrictMode -Version Latest", result);
    }

    // source file.sh -> Invoke-BashSource ./lib.sh
    [Fact]
    public void Transpile_SourceShFile_EmitsInvokeBashSource()
    {
        var result = PsEmitter.Transpile("source ./lib.sh");
        Assert.Equal("Invoke-BashSource ./lib.sh", result);
    }

    [Fact]
    public void Transpile_DotSourceShFile_EmitsInvokeBashSource()
    {
        var result = PsEmitter.Transpile(". ./lib.sh");
        Assert.Equal("Invoke-BashSource ./lib.sh", result);
    }

    [Fact]
    public void Transpile_SourceShFileWithArgs_EmitsInvokeBashSourceWithArgs()
    {
        var result = PsEmitter.Transpile("source ./setup.sh arg1 arg2");
        Assert.Equal("Invoke-BashSource ./setup.sh arg1 arg2", result);
    }

    // -e file exists test
    [Fact]
    public void Transpile_FileExistsTest_EmitsTestPath()
    {
        var result = PsEmitter.Transpile("if [[ -e file.txt ]]; then echo yes; fi");
        Assert.Contains("Test-Path", result);
        Assert.Contains("file.txt", result);
    }

    // declare -i -> [int]$global:var = 0
    [Fact]
    public void Transpile_DeclareInt_EmitsTypedVar()
    {
        var result = PsEmitter.Transpile("declare -i count");
        Assert.Equal("[int]$global:count = 0", result);
    }

    // ${str/foo/bar} -> replace first
    [Fact]
    public void Transpile_ParamReplaceFirst_EmitsRegexReplace()
    {
        var result = PsEmitter.Transpile("echo ${str/foo/bar}");
        // Uses instance overload ([regex]pattern).Replace(str, rep, count=1) for first-only replacement
        Assert.Contains("[regex]", result);
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

        Assert.Equal("Invoke-BashEcho hello | Invoke-BashRev", result);
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

        Assert.Equal("Invoke-BashCat file | Invoke-BashNl -ba", result);
    }

    [Fact]
    public void Transpile_PipeToColumnWithFlag_EmitsInvokeBashColumn()
    {
        var result = PsEmitter.Transpile("cat data.csv | column -t");

        Assert.Equal("Invoke-BashCat data.csv | Invoke-BashColumn -t", result);
    }

    [Fact]
    public void Transpile_PipeToTee_EmitsInvokeBashTee()
    {
        var result = PsEmitter.Transpile("echo hello | tee output.txt");

        Assert.Equal("Invoke-BashEcho hello | Invoke-BashTee output.txt", result);
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
        Assert.Equal("Invoke-BashEcho \"$($env:UNSET_VAR ?? 'fallback')\"", result);
    }

    [Fact]
    public void Transpile_BracedVarSuffixRemovalInsideDoubleQuotes_EmitsSubexpression()
    {
        // GlobToRegex translates glob * to regex .* so l* becomes l.* (greedy longest suffix).
        var result = PsEmitter.Transpile("echo \"${FOO%%l*}\"");
        Assert.Equal("Invoke-BashEcho \"$($env:FOO -replace 'l.*$','')\"", result);
    }

    [Fact]
    public void Transpile_BracedVarLengthInsideDoubleQuotes_EmitsSubexpression()
    {
        var result = PsEmitter.Transpile("echo \"${#VAR}\"");
        Assert.Equal("Invoke-BashEcho \"$($env:VAR.Length)\"", result);
    }

    [Fact]
    public void Transpile_BracedVarPrefixRemovalInsideDoubleQuotes_EmitsSubexpression()
    {
        // GlobToRegex translates glob * to regex .* so */ becomes .* + / = .*/ (greedy longest prefix).
        var result = PsEmitter.Transpile("echo \"${PATH##*/}\"");
        Assert.Equal("Invoke-BashEcho \"$($env:PATH -replace '^.*/','')\"", result);
    }

    [Fact]
    public void Transpile_BracedVarAlternativeInsideDoubleQuotes_EmitsSubexpression()
    {
        var result = PsEmitter.Transpile("echo \"${VAR:+yes}\"");
        Assert.Equal("Invoke-BashEcho \"$($env:VAR ? 'yes' : '')\"", result);
    }

    [Fact]
    public void Transpile_SimpleBracedVarInsideDoubleQuotes_NoSubexpression()
    {
        var result = PsEmitter.Transpile("echo \"${USER}\"");
        Assert.Equal("Invoke-BashEcho \"$env:USER\"", result);
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

    [Fact]
    public void Transpile_ReadlinkCanonical_EmitsPassthrough()
    {
        var result = PsEmitter.Transpile("readlink -f /some/path");
        Assert.Equal("Invoke-BashReadlink -f /some/path", result);
    }

    [Fact]
    public void Transpile_ReadlinkBare_EmitsPassthrough()
    {
        var result = PsEmitter.Transpile("readlink /some/link");
        Assert.Equal("Invoke-BashReadlink /some/link", result);
    }

    [Fact]
    public void Transpile_Mktemp_EmitsPassthrough()
    {
        var result = PsEmitter.Transpile("mktemp");
        Assert.Equal("Invoke-BashMktemp", result);
    }

    [Fact]
    public void Transpile_MktempDirectory_EmitsPassthrough()
    {
        var result = PsEmitter.Transpile("mktemp -d");
        Assert.Equal("Invoke-BashMktemp -d", result);
    }

    [Fact]
    public void Transpile_TypeCommand_EmitsPassthrough()
    {
        var result = PsEmitter.Transpile("type echo");
        Assert.Equal("Invoke-BashType echo", result);
    }

    [Fact]
    public void Transpile_TypeWithFlag_EmitsPassthrough()
    {
        var result = PsEmitter.Transpile("type -t echo");
        Assert.Equal("Invoke-BashType -t echo", result);
    }

    [Fact]
    public void Transpile_InstallCommand_EmitsPassthrough()
    {
        var result = PsEmitter.Transpile("install -m 755 ./build/myapp /usr/local/bin/myapp");
        Assert.Equal("Invoke-BashInstall -m 755 ./build/myapp /usr/local/bin/myapp", result);
    }

    [Fact]
    public void Transpile_InstallInPipeline_EmitsPassthrough()
    {
        var result = PsEmitter.Transpile("echo myapp | install -t /usr/local/bin");
        Assert.Contains("Invoke-BashInstall", result);
    }

    [Fact]
    public void Transpile_WriteAndAppendChain_EmitsBashRedirectPipes()
    {
        var result = PsEmitter.Transpile("echo line1 > /tmp/test.txt && echo append >> /tmp/test.txt");

        Assert.Contains("Invoke-BashEcho line1 | Invoke-BashRedirect -Path $env:TEMP\\test.txt", result);
        Assert.Contains("Invoke-BashEcho append | Invoke-BashRedirect -Path $env:TEMP\\test.txt -Append", result);
    }

    [Fact]
    public void Transpile_OutputToDevNull_KeepsNativeRedirect()
    {
        var result = PsEmitter.Transpile("cmd > /dev/null");

        Assert.Equal("cmd >$null", result);
    }

    [Fact]
    public void Transpile_PasteWithProcessSubs()
    {
        var result = PsEmitter.Transpile("paste <(echo a) <(echo b)");
        Assert.Equal("Invoke-BashPaste (Invoke-ProcessSub { Invoke-BashEcho a }) (Invoke-ProcessSub { Invoke-BashEcho b })", result);
    }

    [Fact]
    public void Transpile_ProcessSubWithSemicolon()
    {
        var result = PsEmitter.Transpile("diff <(cmd1; cmd2) file");
        Assert.Equal("Invoke-BashDiff (Invoke-ProcessSub { cmd1; cmd2 }) file", result);
    }

    [Fact]
    public void Transpile_PasteWithSemicolonProcessSubs()
    {
        var result = PsEmitter.Transpile("paste <(echo a; echo c) <(echo b; echo d)");
        Assert.Equal(
            "Invoke-BashPaste (Invoke-ProcessSub { Invoke-BashEcho a; Invoke-BashEcho c }) (Invoke-ProcessSub { Invoke-BashEcho b; Invoke-BashEcho d })",
            result);
    }

    [Fact]
    public void Transpile_PasteAsPipeTarget()
    {
        var result = PsEmitter.Transpile("cat file.txt | paste -d, -s");
        Assert.Equal("Invoke-BashCat file.txt | Invoke-BashPaste \"-d,\" -s", result);
    }

    [Fact]
    public void Transpile_BraceRangeNonDivisibleStep_DoesNotOvershoot()
    {
        var result = PsEmitter.Transpile("echo {1..10..7}");
        Assert.Equal("Invoke-BashEcho @(1,8)", result);
    }

    [Fact]
    public void Transpile_BraceRangeNonDivisibleStepReverse_DoesNotOvershoot()
    {
        var result = PsEmitter.Transpile("echo {10..1..3}");
        Assert.Equal("Invoke-BashEcho @(10,7,4,1)", result);
    }

    [Fact]
    public void Transpile_BraceRangeStepDivisible_IncludesEnd()
    {
        var result = PsEmitter.Transpile("echo {1..10..3}");
        Assert.Equal("Invoke-BashEcho @(1,4,7,10)", result);
    }

    [Fact]
    public void Transpile_BraceRangeDefaultStep_Works()
    {
        var result = PsEmitter.Transpile("echo {1..5}");
        Assert.Equal("Invoke-BashEcho @(1..5)", result);
    }

    [Fact]
    public void Transpile_BraceRangeReverseDefaultStep_Works()
    {
        var result = PsEmitter.Transpile("echo {5..1}");
        Assert.Equal("Invoke-BashEcho @(5..1)", result);
    }

    [Fact]
    public void Transpile_WhileTrue_ContainsIterGuard()
    {
        var result = PsEmitter.Transpile("while true; do echo hi; done");
        Assert.Contains("$__psbash_iter = 0;", result);
        Assert.Contains("++$__psbash_iter", result);
        Assert.Contains("PSBASH_MAX_ITERATIONS", result);
        Assert.Contains("loop iteration limit exceeded", result);
    }

    [Fact]
    public void Transpile_ForIn_ContainsIterGuard()
    {
        var result = PsEmitter.Transpile("for x in a b; do echo $x; done");
        Assert.Contains("$__psbash_iter = 0;", result);
        Assert.Contains("++$__psbash_iter", result);
    }

    [Fact]
    public void Transpile_ForArith_ContainsIterGuard()
    {
        var result = PsEmitter.Transpile("for ((i=0; i<10; i++)); do echo $i; done");
        Assert.Contains("$__psbash_iter = 0;", result);
        Assert.Contains("++$__psbash_iter", result);
    }

    [Fact]
    public void Transpile_WhileReadLine_NoIterGuard()
    {
        var result = PsEmitter.Transpile("while read line; do echo $line; done");
        Assert.DoesNotContain("$__psbash_iter", result);
    }

    [Fact]
    public void Transpile_WhileRead_StripsTrailingNewlineBeforeSplit()
    {
        var result = PsEmitter.Transpile("while read x; do echo $x; done");
        Assert.Contains(@"($_ -replace ""`n$"","""") -split ""`n""", result);
    }

    [Fact]
    public void Transpile_IterGuard_DefaultIs100000()
    {
        var result = PsEmitter.Transpile("while true; do echo hi; done");
        Assert.Contains("?? 100000)", result);
    }

    // Bug fix: $1-$9 inside double quotes need $() subexpression wrapping

    [Fact]
    public void Transpile_PositionalVarInDoubleQuotes_EmitsSubexpression()
    {
        var result = PsEmitter.Transpile("echo \"hello $1\"");
        Assert.Equal("Invoke-BashEcho \"hello $(if ($global:BashPositional) { $global:BashPositional[0] } else { $args[0] })\"",
            result);
    }

    [Fact]
    public void Transpile_MultiplePositionalVarsInDoubleQuotes_EmitsSubexpressions()
    {
        var result = PsEmitter.Transpile("echo \"$1 and $2\"");
        Assert.Equal("Invoke-BashEcho \"$(if ($global:BashPositional) { $global:BashPositional[0] } else { $args[0] }) and $(if ($global:BashPositional) { $global:BashPositional[1] } else { $args[1] })\"", result);
    }

    [Fact]
    public void Transpile_PositionalVarOutsideQuotes_NoSubexpression()
    {
        var result = PsEmitter.Transpile("echo $1");
        Assert.Equal("Invoke-BashEcho $(if ($global:BashPositional) { $global:BashPositional[0] } else { $args[0] })", result);
    }

    [Fact]
    public void Transpile_ArgCountInDoubleQuotes_EmitsSubexpression()
    {
        var result = PsEmitter.Transpile("echo \"count: $#\"");
        Assert.Equal("Invoke-BashEcho \"count: $(if ($global:BashPositional) { $global:BashPositional.Count } else { $args.Count })\"", result);
    }

    [Fact]
    public void Transpile_Var0InDoubleQuotes_EmitsSubexpression()
    {
        var result = PsEmitter.Transpile("echo \"script: $0\"");
        Assert.Equal("Invoke-BashEcho \"script: $($MyInvocation.MyCommand.Name)\"", result);
    }

    [Fact]
    public void Transpile_NewlineSeparatedCommands_EmitsBoth()
    {
        var result = PsEmitter.Transpile("array=(one two three)\necho ${#array[@]}");
        Assert.NotNull(result);
        Assert.Contains("@('one','two','three')", result);
        Assert.Contains("Invoke-BashEcho", result);
        Assert.Contains(".Count", result);
    }

    [Fact]
    public void Transpile_SortWithColonDelimiter_QuotesColonFlag()
    {
        var result = PsEmitter.Transpile("echo test | sort -t: -k2");
        Assert.Contains("Invoke-BashSort \"-t:\"", result);
    }

    [Fact]
    public void Transpile_AwkWithColonDelimiter_QuotesColonFlag()
    {
        var result = PsEmitter.Transpile("cat file | awk -F: '{print}'");
        Assert.Contains("Invoke-BashAwk \"-F:\"", result);
    }

    [Fact]
    public void Transpile_VarFollowedByColon_EmitsBracedVar()
    {
        var result = PsEmitter.Transpile("x=hello; echo \"$x: world\"");
        Assert.Contains("${env:x}:", result);
    }

    [Fact]
    public void Transpile_LoopVarFollowedByColon_EmitsBracedVar()
    {
        var result = PsEmitter.Transpile("for dir in a b; do echo \"$dir: done\"; done");
        Assert.Contains("${dir}:", result);
    }

    [Fact]
    public void Transpile_VarNotFollowedByColonOrDot_NoBracing()
    {
        var result = PsEmitter.Transpile("echo \"$x world\"");
        Assert.Contains("$env:x", result);
        Assert.DoesNotContain("${env:x}", result);
    }

    [Fact]
    public void Transpile_VarFollowedByDot_EmitsBracedVar()
    {
        var result = PsEmitter.Transpile("echo \"$file.txt\"");
        Assert.Contains("${env:file}.txt", result);
    }

    [Fact]
    public void Transpile_LoopVarFollowedByDot_EmitsBracedVar()
    {
        var result = PsEmitter.Transpile("for f in a b; do echo \"$f.log\"; done");
        Assert.Contains("${f}.log", result);
    }

    [Fact]
    public void Transpile_FindExecWithBraces_PreservesBraces()
    {
        var result = PsEmitter.Transpile("find src -name '*.cs' -exec wc -l {} +");
        Assert.Contains("Invoke-BashFind src -name '*.cs' -exec wc -l \"{}\" +", result);
    }

    [Fact]
    public void Transpile_EchoWithEmptyBraces_PreservesBraces()
    {
        var result = PsEmitter.Transpile("echo {} test");
        Assert.Contains("{}", result);
        Assert.Contains("test", result);
    }

    [Fact]
    public void Transpile_EchoEmptyString_PreservesEmptyArg()
    {
        var result = PsEmitter.Transpile("echo \"\"");
        Assert.Contains("Invoke-BashEcho \"\"", result);
    }

    // --- bash command mapping tests ---

    [Fact]
    public void Transpile_BashWithDashC_EmitsInvokeBashBash()
    {
        var result = PsEmitter.Transpile("bash -c \"echo hello\"");
        Assert.Equal("Invoke-BashBash -c \"echo hello\"", result);
    }

    [Fact]
    public void Transpile_BashScriptFile_EmitsInvokeBashBash()
    {
        var result = PsEmitter.Transpile("bash script.sh");
        Assert.Equal("Invoke-BashBash script.sh", result);
    }

    [Fact]
    public void Transpile_BashVersion_EmitsInvokeBashBash()
    {
        var result = PsEmitter.Transpile("bash --version");
        Assert.Equal("Invoke-BashBash --version", result);
    }

    [Fact]
    public void Transpile_BashPipeToGrep_EmitsMappedPipeline()
    {
        var result = PsEmitter.Transpile("bash -c \"echo hello\" | grep hello");
        Assert.Equal("Invoke-BashBash -c \"echo hello\" | Invoke-BashGrep hello", result);
    }

    [Fact]
    public void Transpile_Jobs_EmitsInvokeBashJobs()
    {
        var result = PsEmitter.Transpile("jobs");
        Assert.Equal("Invoke-BashJobs", result);
    }

    [Fact]
    public void Transpile_Wait_NoArgs_EmitsInvokeBashWait()
    {
        var result = PsEmitter.Transpile("wait");
        Assert.Equal("Invoke-BashWait", result);
    }

    [Fact]
    public void Transpile_Wait_WithPid_EmitsInvokeBashWaitPid()
    {
        var result = PsEmitter.Transpile("wait 1234");
        Assert.Equal("Invoke-BashWait 1234", result);
    }

    [Fact]
    public void Transpile_Wait_MultiplePids()
    {
        var result = PsEmitter.Transpile("wait 1234 5678");
        Assert.Equal("Invoke-BashWait 1234 5678", result);
    }

    [Fact]
    public void Transpile_Background_ThenWait()
    {
        var result = PsEmitter.Transpile("sleep 1 & wait");
        Assert.Equal("Invoke-BashBackground { Invoke-BashSleep 1 }; Invoke-BashWait", result);
    }

    // -----------------------------------------------------------------------
    // if condition: AndOrList (&&  / ||) — DART-6BxDBlSHAp6A
    // PowerShell's && / || are pipeline chain operators that cannot appear
    // inside if (...). The emitter must convert them to -and / -or.
    // -----------------------------------------------------------------------

    [Fact]
    public void Transpile_If_AndCondition_TrueAndTrue_EmitsAndExpr()
    {
        // if true && true; then echo yes; fi
        // EmitCondition returns "($true -and $true)"; EmitIf wraps in parens again.
        var result = PsEmitter.Transpile("if true && true; then echo yes; fi");
        Assert.Equal("if (($true -and $true)) { Invoke-BashEcho yes }", result);
    }

    [Fact]
    public void Transpile_If_AndCondition_FalseAndTrue_EmitsAndExpr()
    {
        // if false && true; then echo yes; fi
        var result = PsEmitter.Transpile("if false && true; then echo yes; fi");
        Assert.Equal("if (($false -and $true)) { Invoke-BashEcho yes }", result);
    }

    [Fact]
    public void Transpile_If_OrCondition_FalseOrTrue_EmitsOrExpr()
    {
        // if false || true; then echo yes; fi
        var result = PsEmitter.Transpile("if false || true; then echo yes; fi");
        Assert.Equal("if (($false -or $true)) { Invoke-BashEcho yes }", result);
    }

    [Fact]
    public void Transpile_If_AndCondition_BoolExprAndBoolExpr_EmitsAndWithTestExprs()
    {
        // if [ 1 -eq 1 ] && [ 2 -eq 2 ]; then echo yes; fi
        // Both sides are BoolExpr — no LASTEXITCODE wrapper needed.
        var result = PsEmitter.Transpile("if [ 1 -eq 1 ] && [ 2 -eq 2 ]; then echo yes; fi");
        Assert.Equal("if (((1 -eq 1) -and (2 -eq 2))) { Invoke-BashEcho yes }", result);
    }

    [Fact]
    public void Transpile_If_AndCondition_WithElse_EmitsCorrectBranches()
    {
        // if true && true; then echo yes; else echo no; fi
        var result = PsEmitter.Transpile("if true && true; then echo yes; else echo no; fi");
        Assert.Equal("if (($true -and $true)) { Invoke-BashEcho yes } else { Invoke-BashEcho no }", result);
    }

    // -----------------------------------------------------------------------
    // trap 'CMD' DEBUG -> Register-BashChpwdHook
    // -----------------------------------------------------------------------

    [Fact]
    public void Transpile_TrapDebug_SingleQuotedCmd_EmitsRegisterBashChpwdHook()
    {
        // trap 'do_something' DEBUG
        // rawCmd = "do_something", hash = d66c2688
        // transpiledCmd = "do_something" (unknown cmd passes through as-is)
        var result = PsEmitter.Transpile("trap 'do_something' DEBUG");

        const string warnGuard =
            "if (-not $global:__BashHookDebugTrapWarned) { " +
            "$global:__BashHookDebugTrapWarned = $true; " +
            "Write-Warning 'ps-bash: trap DEBUG mapped to Register-BashChpwdHook (fires on directory change, not every command)' }";
        Assert.NotNull(result);
        Assert.Contains("Register-BashChpwdHook -Name 'd66c2688'", result);
        Assert.Contains(warnGuard, result);
        Assert.Contains("Invoke-Expression", result);
    }

    [Fact]
    public void Transpile_TrapDebug_EmitsFirstUseWarningGuard()
    {
        // The emitted code must include the $global:__BashHookDebugTrapWarned guard.
        var result = PsEmitter.Transpile("trap 'fnm use' DEBUG");

        Assert.NotNull(result);
        Assert.Contains("$global:__BashHookDebugTrapWarned", result);
        // hook name derived from "fnm use" = d0104599
        Assert.Contains("Register-BashChpwdHook -Name 'd0104599'", result);
    }

    [Fact]
    public void Transpile_TrapDashDebug_EmitsUnregisterBashChpwdHook()
    {
        // trap - DEBUG  ->  Unregister-BashChpwdHook -Name '<hash of "-">'
        // hash("-") = 336d5ebc
        var result = PsEmitter.Transpile("trap - DEBUG");

        Assert.Equal("Unregister-BashChpwdHook -Name '336d5ebc'", result);
    }

    [Fact]
    public void Transpile_TrapOtherSignal_PassesThroughToInvokeBashTrap()
    {
        // trap 'cleanup' EXIT  ->  Invoke-BashTrap 'cleanup' EXIT  (not a hook)
        var result = PsEmitter.Transpile("trap 'cleanup' EXIT");

        Assert.NotNull(result);
        Assert.Contains("Invoke-BashTrap", result);
        Assert.DoesNotContain("Register-BashChpwdHook", result);
    }

    [Fact]
    public void Transpile_TrapDebug_TranspilesBodyCmd()
    {
        // trap 'update_terminal_title' DEBUG
        // rawCmd = "update_terminal_title", hash = b680c740
        var result = PsEmitter.Transpile("trap 'update_terminal_title' DEBUG");

        Assert.NotNull(result);
        Assert.Contains("Register-BashChpwdHook -Name 'b680c740'", result);
        Assert.Contains("ScriptBlock", result);
    }

    // -----------------------------------------------------------------------
    // PROMPT_COMMAND='CMD' -> Register-BashPromptHook
    // -----------------------------------------------------------------------

    [Fact]
    public void Transpile_PromptCommandSingleQuoted_EmitsRegisterBashPromptHook()
    {
        // PROMPT_COMMAND='do_something'
        var result = PsEmitter.Transpile("PROMPT_COMMAND='do_something'");

        Assert.NotNull(result);
        Assert.Contains("Register-BashPromptHook -Name 'prompt-command'", result);
        Assert.Contains("ScriptBlock", result);
        Assert.Contains("Invoke-Expression", result);
    }

    [Fact]
    public void Transpile_PromptCommandDoubleQuoted_EmitsRegisterBashPromptHook()
    {
        // PROMPT_COMMAND="do_something"
        var result = PsEmitter.Transpile("PROMPT_COMMAND=\"do_something\"");

        Assert.NotNull(result);
        Assert.Contains("Register-BashPromptHook -Name 'prompt-command'", result);
        Assert.Contains("Invoke-Expression", result);
    }

    [Fact]
    public void Transpile_UnsetPromptCommand_EmitsUnregisterBashPromptHook()
    {
        // unset PROMPT_COMMAND
        var result = PsEmitter.Transpile("unset PROMPT_COMMAND");

        Assert.Equal("Unregister-BashPromptHook -Name 'prompt-command'", result);
    }

    [Fact]
    public void Transpile_UnsetOtherVar_PassesThroughToInvokeBashUnset()
    {
        // unset FOO  ->  Invoke-BashUnset FOO  (not a hook)
        var result = PsEmitter.Transpile("unset FOO");

        Assert.NotNull(result);
        Assert.Contains("Invoke-BashUnset", result);
        Assert.DoesNotContain("Unregister-BashPromptHook", result);
    }

    [Fact]
    public void Transpile_PromptCommandWithComplexValue_EmitsEnvVarFallback()
    {
        // PROMPT_COMMAND="$(some_cmd)" — complex value with command substitution
        // Cannot statically transpile; falls back to env var assignment
        var result = PsEmitter.Transpile("PROMPT_COMMAND=\"$(some_cmd)\"");

        Assert.NotNull(result);
        Assert.Contains("$env:PROMPT_COMMAND", result);
        Assert.DoesNotContain("Register-BashPromptHook", result);
    }

    [Fact]
    public void ShortHash_KnownInput_ReturnsExpected8HexChars()
    {
        // Regression guard: hash must be stable across builds.
        Assert.Equal("d66c2688", PsEmitter.ShortHash("do_something"));
        Assert.Equal("336d5ebc", PsEmitter.ShortHash("-"));
        Assert.Equal("b680c740", PsEmitter.ShortHash("update_terminal_title"));
        Assert.Equal("d0104599", PsEmitter.ShortHash("fnm use"));
    }

    // ---- RC-7: unquoted variable word-splitting ----------------------------

    [Fact]
    public void Transpile_UnquotedVarArg_EmitsSplatWithWordSplit()
    {
        // bash: an unquoted $x operand is word-split on IFS and elided when
        // empty. The emitter hoists it to a temp var holding the split array
        // and PowerShell-splats it.
        var result = PsEmitter.Transpile("echo a $x b");

        Assert.Contains("$__bashsplat0 = @(if ([string]::IsNullOrEmpty($env:x))",
            result);
        Assert.Contains("@($env:x -split '\\s+')", result);
        Assert.Contains("@__bashsplat0", result);
        // Wrapped in & { } so the temp assignment never leaks into a pipeline.
        Assert.Contains("& {", result);
    }

    [Fact]
    public void Transpile_QuotedVarArg_NotSplat()
    {
        // "$x" (quoted) keeps the existing single-argument expansion — only
        // bare unquoted $x triggers word-splitting.
        var result = PsEmitter.Transpile("echo \"$x\"");

        Assert.DoesNotContain("__bashsplat", result);
    }

    [Fact]
    public void Transpile_CommandWordIsVar_NotSplat()
    {
        // The command word itself must resolve to a name; it is never splat.
        var result = PsEmitter.Transpile("$cmd arg");

        Assert.DoesNotContain("__bashsplat", result);
    }

    [Fact]
    public void Transpile_SpecialParamArg_NotSplatByRc7()
    {
        // $@ has its own dedicated expansion and must not be routed through the
        // RC-7 word-split splat.
        var result = PsEmitter.Transpile("echo $@");

        Assert.DoesNotContain("__bashsplat", result);
    }

    [Fact]
    public void Transpile_MappedCommandUnquotedVarArg_EmitsSplat()
    {
        // The passthrough path (mapped Invoke-Bash* cmdlets) also routes a pure
        // unquoted variable operand through the splat.
        var result = PsEmitter.Transpile("grep $pattern file.txt");

        Assert.Contains("__bashsplat0", result);
        Assert.Contains("@__bashsplat0", result);
    }

    [Fact]
    public void Transpile_MultipleUnquotedVarArgs_DistinctTempVars()
    {
        var result = PsEmitter.Transpile("echo $a $b");

        Assert.Contains("$__bashsplat0", result);
        Assert.Contains("$__bashsplat1", result);
    }

    private static CompoundWord MakeWord(string value) =>
        new(ImmutableArray.Create<WordPart>(new WordPart.Literal(value)));
}
