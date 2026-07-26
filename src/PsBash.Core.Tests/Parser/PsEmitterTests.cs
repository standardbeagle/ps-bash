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

        // All-mapped, terminal-bound, plain-`|`, literal-arg pipeline → fused lane
        // with a phase-2b streaming -Stages list + phase-2a scriptblock Fallback.
        Assert.Equal(
            "Invoke-BashFusedPipeline -Stages @(@('cat', 'file'), @('head', '-n', '5'), @('sort')) "
                + "-Fallback { Invoke-BashCat file | Invoke-BashHead -n 5 | Invoke-BashSort }",
            result);
    }

    [Fact]
    public void Transpile_ArithExponent_RoutesToInvokeBashArith()
    {
        // $(( )) value context is evaluated by the runtime bash-arithmetic
        // evaluator, not a verbatim PowerShell $( ) subexpression (which
        // mistranslated **, integer /, bitwise, 1/0 comparisons, etc.).
        var result = PsEmitter.Transpile("echo $((2**10))");
        Assert.Equal("Invoke-BashEcho $(Invoke-BashArith '2**10')", result);
    }

    [Fact]
    public void Transpile_ArithWithVariableAndComparison_RoutesToInvokeBashArith()
    {
        var result = PsEmitter.Transpile("echo $(( n > 5 ? n * 100 : -1 ))");
        Assert.Contains("Invoke-BashArith 'n > 5 ? n * 100 : -1'", result);
    }

    [Fact]
    public void Transpile_ArithWithPositionalParam_RoutesToInvokeBashArith()
    {
        // Positional parameters ($1..$9) have no representation in the evaluator's
        // bare-identifier model (its lexer reads $1 as the literal 1), so the
        // emitter substitutes the runtime value and still routes the expression
        // through Invoke-BashArith — the same evaluator the non-positional path
        // uses — so bash-correct integer operators apply.
        var result = PsEmitter.Transpile("echo $(($1 + 1))");
        Assert.Equal(
            "Invoke-BashEcho $(Invoke-BashArith ('' + $(\"$(if ($global:BashPositional) { $global:BashPositional[0] } else { $args[0] })\" -replace '^$','0') + ' + 1'))",
            result);
    }

    [Fact]
    public void Transpile_ArithPositionalExponent_RoutesToInvokeBashArith()
    {
        // Regression: `$(($1 ** 2))` used to take the legacy verbatim $() path and
        // emit a literal `**`, which is not a PowerShell operator and failed to
        // parse. It must now go through Invoke-BashArith (which implements **) with
        // the positional value substituted in.
        var result = PsEmitter.Transpile("echo $(($1 ** 2))");
        Assert.Equal(
            "Invoke-BashEcho $(Invoke-BashArith ('' + $(\"$(if ($global:BashPositional) { $global:BashPositional[0] } else { $args[0] })\" -replace '^$','0') + ' ** 2'))",
            result);
        // The nonexistent-PowerShell-operator `**` must not appear as raw output.
        Assert.DoesNotContain("$($1 ** 2)", result);
    }

    [Fact]
    public void Transpile_ArithParamCount_RoutesToInvokeBashArith()
    {
        // $# (parameter count) is likewise substituted with its runtime value.
        var result = PsEmitter.Transpile("echo $(($# * 2))");
        Assert.Equal(
            "Invoke-BashEcho $(Invoke-BashArith ('' + $(\"$(if ($global:BashPositional) { $global:BashPositional.Count } else { $args.Count })\" -replace '^$','0') + ' * 2'))",
            result);
    }

    [Fact]
    public void Transpile_ArithUnsetPositionalNonAdditiveOp_DefaultsToZeroNotMalformed()
    {
        // Regression (reviewer finding): an unset positional must default to "0",
        // not an empty fragment. Without the ZeroDefault wrapper, `$(($1 * 2))`
        // with no args reassembled to the malformed string " * 2" and the
        // evaluator threw instead of yielding bash's 0. The `-replace '^$','0'`
        // guard turns an empty ($null / out-of-range) substitution into "0".
        var result = PsEmitter.Transpile("echo $(($1 * 2))");
        Assert.Equal(
            "Invoke-BashEcho $(Invoke-BashArith ('' + $(\"$(if ($global:BashPositional) { $global:BashPositional[0] } else { $args[0] })\" -replace '^$','0') + ' * 2'))",
            result);
        // The empty-default guard is present so an unset positional never yields a
        // leading-operator (malformed) arithmetic string.
        Assert.Contains("-replace '^$','0'", result);
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
    public void Transpile_BacktickNested_UnescapesInnerBackticks()
    {
        // `echo \`date\`` — bash un-escapes \` to ` inside the outer backticks,
        // yielding a nested command substitution. Without un-escaping the inner
        // backticks stayed literal and the nested `date` sub was not recognized.
        var result = PsEmitter.Transpile(@"echo `echo \`date\``");
        Assert.Contains("Invoke-BashDate", result);
    }

    [Fact]
    public void Transpile_EscapedQuoteInsideDoubleQuotes_BacktickEscapesForPowerShell()
    {
        // bash: echo "a\"b" -> literal a"b (oracle-verified).
        //
        // This used to be emitted as `" inside a PowerShell DOUBLE-quoted string. That
        // is valid on its own but does not survive nesting: inside `X="$( … )"` the
        // OUTER string scanner consumes the backtick escape, ending the inner string
        // early ("The string is missing the terminator") and failing the whole file.
        // A word with no expansion is a known literal, so it is emitted as a
        // SINGLE-quoted PowerShell string, where " and ` are both ordinary characters.
        var result = PsEmitter.Transpile("echo \"a\\\"b\"");
        Assert.Contains("'a\"b'", result);
        Assert.DoesNotContain("\"a\"b\"", result);
    }

    [Fact]
    public void Transpile_EscapedQuoteInsideCommandSubInsideString_EmitsNoNestedEscape()
    {
        // The shape the single-quoting exists for: a quoted literal nested two levels
        // deep. The emitted inner literal must carry NO backtick escape, since the
        // enclosing "$( … )" string would consume it ("The string is missing the
        // terminator"). The parse-level guard lives in
        // PsBash.Host.Tests WrapperParseabilityTests (that project has PowerShell).
        var result = PsEmitter.Transpile("X=\"$(echo \"q\\\"r\")\"");

        Assert.Contains("'q\"r'", result);
        Assert.DoesNotContain("`\"", result);
    }

    [Fact]
    public void Transpile_FindOrOperator_QuotesDashOToSurvivePowerShellBinder()
    {
        // bash find's infix `-o` (OR) prefix-collides with -OutVariable/-OutBuffer.
        // The emitter quotes it so it reaches Invoke-BashFind's Arguments in place
        // (a switch decoy on the cmdlet would resolve the crash but lose position).
        var result = PsEmitter.Transpile("find . -name a -o -name b");
        Assert.Equal("Invoke-BashFind . -name a \"-o\" -name b", result);
    }

    [Fact]
    public void Transpile_FindAndOperator_QuotesDashAToAvoidArgumentsParamCollision()
    {
        // find's `-a` (AND) prefix-matches the cmdlet's own -Arguments parameter,
        // which would bind it as named and swallow the next token. Quote it too.
        var result = PsEmitter.Transpile("find . -type f -a -name x");
        Assert.Equal("Invoke-BashFind . -type f \"-a\" -name x", result);
    }

    [Fact]
    public void Transpile_BangAfterCommandWord_KeptAsLiteralArgument()
    {
        // bash `!` is the negation reserved word only at pipeline start. A `!`
        // after the command word is a literal operand (find . ! -name x). The
        // parser used to break the command at `!`, dropping `! -name x`.
        var result = PsEmitter.Transpile("find . ! -name x");
        Assert.Equal("Invoke-BashFind . ! -name x", result);
    }

    [Fact]
    public void Transpile_LeadingBang_StillNegatesPipeline()
    {
        // The fix must not disturb real pipeline negation: a LEADING `!` is still
        // consumed by ParsePipeline as negation, not treated as an argument.
        var result = PsEmitter.Transpile("! grep -q pattern file");
        Assert.Contains("LASTEXITCODE", result); // negation bridges exit code
    }

    [Fact]
    public void Transpile_FindDashOInOtherCommands_NotQuoted()
    {
        // The force-quote is scoped to find; grep -o stays bare (grep declares its
        // own O decoy parameter, so the bare token binds correctly there).
        var result = PsEmitter.Transpile("grep -o foo file");
        Assert.DoesNotContain("\"-o\"", result);
    }

    [Fact]
    public void Transpile_EscapedBacktickInsideDoubleQuotes_DoublesBacktickForPowerShell()
    {
        // bash: echo "a\`b" -> literal a`b (oracle-verified). Same reasoning as the
        // escaped-quote case above: a pure-literal word emits as a SINGLE-quoted
        // PowerShell string, where a backtick is an ordinary character and no escape
        // can be swallowed by an enclosing string.
        var result = PsEmitter.Transpile("echo \"a\\`b\"");
        Assert.Contains("'a`b'", result);
    }

    [Fact]
    public void Transpile_PlainDoubleQuotedLiteral_KeepsDoubleQuotes()
    {
        // Guard the narrow claim: only a literal that WOULD need an escape switches
        // to single quotes; the ordinary shape keeps its readable double-quoted form.
        Assert.Contains("\"plain text\"", PsEmitter.Transpile("echo \"plain text\""));
    }

    [Fact]
    public void Transpile_DoubleQuotedWithExpansion_KeepsDoubleQuotes()
    {
        // A word carrying an expansion must stay interpolating.
        Assert.Contains("\"has $env:V var\"", PsEmitter.Transpile("echo \"has $V var\""));
    }

    [Fact]
    public void Transpile_AssignmentTildeAfterColon_ExpandsBothTildes()
    {
        // bash expands ~ at the start of an assignment value AND after each
        // unquoted ':' — PATH=~/bin:~/x -> $HOME/bin:$HOME/x.
        var result = PsEmitter.Transpile("PATH=~/bin:~/x");
        var homeCount = System.Text.RegularExpressions.Regex.Matches(result, @"\$HOME").Count;
        Assert.True(homeCount >= 2, $"expected >= 2 $HOME, got {homeCount}: {result}");
    }

    [Theory]
    // SplitOnUnquotedColon (which finds the PATH-style `:` boundaries of an
    // assignment value) scanned a double-quoted region by looking for the next `"`.
    // A nested command substitution has its OWN quotes, so the region ended at the
    // INNER quote and the colon after it looked unquoted — the value was split
    // there, tearing `:b f)` out of the command and leaving a mangled pattern.
    [InlineData("X=\"$(grep \"a:b\" f)\"", "\"a:b\" f")]
    [InlineData("X=\"$(echo \"p\" | grep \"a:b\")\"", "\"a:b\"")]
    [InlineData("X=\"$(grep \"^$v:\" f)\"", "\"^${env:v}:\" f")]
    public void Transpile_AssignmentWithNestedQuotedColonInCommandSub_NotSplit(
        string bash, string expectedFragment)
    {
        Assert.Contains(expectedFragment, PsEmitter.Transpile(bash));
    }

    [Theory]
    // PowerShell reads `$field:` as a provider-qualified path, so a variable
    // followed by ':' inside a double-quoted string must be braced. The guard
    // existed for `$field:` but NOT for the equally common braced bash form
    // `${field}:` — a suffix-less ${name} emits the same bare `$name`, so one
    // `grep "^${field}:"` broke the parse of the whole file.
    [InlineData("for f in a; do grep \"^${f}:\" x; done", "\"^${f}:\"")]
    [InlineData("for f in a; do grep \"^$f:\" x; done", "\"^${f}:\"")]
    [InlineData("grep \"^${nl}:\" x", "\"^${env:nl}:\"")]
    public void Transpile_VarFollowedByColonInDoubleQuotes_IsBraced(
        string bash, string expected)
    {
        Assert.Contains(expected, PsEmitter.Transpile(bash));
    }

    [Fact]
    public void Transpile_BracedVarWithSuffix_KeepsItsOwnEmission()
    {
        // Guard the narrow claim: only a SUFFIX-LESS ${name} takes the bracing
        // path; an operator form still routes through EmitBracedVar.
        var result = PsEmitter.Transpile("echo \"${v:-d}:\"");

        Assert.Contains("??", result);
    }

    [Fact]
    public void Transpile_AssignmentPathColons_StillSplitForTildeExpansion()
    {
        // Guard the narrow claim: a genuinely unquoted PATH-style colon still
        // separates segments, so each `~` expands.
        var result = PsEmitter.Transpile("PATH=~/bin:~/x");
        var homeCount = System.Text.RegularExpressions.Regex.Matches(result, @"\$HOME").Count;

        Assert.True(homeCount >= 2, $"expected >= 2 $HOME, got {homeCount}: {result}");
    }

    [Fact]
    public void Transpile_NonAssignmentColonTilde_NotExpanded()
    {
        // `echo a:~b` is NOT an assignment; the tilde after ':' stays literal.
        var result = PsEmitter.Transpile("echo a:~b");
        Assert.DoesNotContain("$HOME", result);
        Assert.Contains("a:~b", result);
    }

    [Fact]
    public void Transpile_AssignmentQuotedColon_NotSplitNoTilde()
    {
        // A quoted ':' is not a split point — no spurious expansion.
        var result = PsEmitter.Transpile("x=\"a:b\"");
        Assert.Contains("a:b", result);
        Assert.DoesNotContain("$HOME", result);
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
    public void Transpile_NamedFdRedirect_DoesNotCrash_DropsPrefix()
    {
        // {fd}>file used to crash (mis-parsed as a brace group). Now it parses;
        // the named-fd capture has no PowerShell equivalent so it degrades to a
        // plain redirect (the {fd} prefix is dropped, not left as an operand).
        var result = PsEmitter.Transpile("cmd {fd}>out.txt");
        // ps-bash emits stdout-to-file via Invoke-BashRedirect; the point is it
        // parses (no crash) and the {fd} prefix is gone.
        Assert.Equal("cmd | Invoke-BashRedirect -Path out.txt", result);
        Assert.DoesNotContain("{fd}", result);
    }

    [Fact]
    public void Transpile_BraceGroup_StillWorks_NotNamedFd()
    {
        // Regression: a real brace group is unaffected by the named-fd lexer rule.
        var result = PsEmitter.Transpile("{ echo hi; }");
        Assert.Contains("Invoke-BashEcho hi", result);
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
    public void Transpile_GreatAndFileTarget_RedirectsBothStreamsToFile()
    {
        // `cmd >&file` is a bash synonym for `&>file`: stdout AND stderr to the FILE.
        // `1>&file` is invalid PowerShell (>& takes a stream number), so emit `&>` form.
        var result = PsEmitter.Transpile("cmd >&out.txt");
        Assert.Equal("cmd >out.txt 2>&1", result);
    }

    [Fact]
    public void Transpile_GreatAndNumericTarget_KeepsFdMerge()
    {
        // Numeric target is a genuine fd-merge, NOT a file: keep the `{fd}>&{n}` form.
        // (`>&2` has its own dedicated stdout→stderr path; use fd 3 to exercise the
        // digit branch of the >& arm directly.)
        var result = PsEmitter.Transpile("cmd >&3");
        Assert.Equal("cmd 1>&3", result);
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
    public void Transpile_LocaleTranslationQuote_MatchesDoubleQuotedInterpolationAndEscaping()
    {
        var localized = PsEmitter.Transpile("echo $\"say \\\"hi\\\" to $USER; cost \\$5; slash \\\\\"");
        var plain = PsEmitter.Transpile("echo \"say \\\"hi\\\" to $USER; cost \\$5; slash \\\\\"");

        Assert.Equal(plain, localized);
        Assert.Contains("say `\"hi`\"", localized);
        Assert.Contains("$env:USER", localized);
        Assert.Contains("cost `$5", localized);
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
    public void Transpile_EmptyAssignment_EmitsEmptyStringAssignment()
    {
        var result = PsEmitter.Transpile("FOO=");

        Assert.Equal("$env:FOO = \"\"", result);
    }

    [Fact]
    public void Transpile_AssignmentWithCommand_EmitsEnvPrefix()
    {
        var result = PsEmitter.Transpile("FOO=bar baz");

        Assert.Equal("$__saved_FOO = $env:FOO; try { $env:FOO = \"bar\"; baz } finally { $env:FOO = $__saved_FOO; }", result);
    }

    [Fact]
    public void Transpile_EmptyAssignmentWithCommand_EmitsEmptyEnvPrefix()
    {
        var result = PsEmitter.Transpile("FOO= baz");

        Assert.Equal("$__saved_FOO = $env:FOO; try { $env:FOO = \"\"; baz } finally { $env:FOO = $__saved_FOO; }", result);
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
            "{ @() } else { @($env:FOO -split '\\s+' | Where-Object { $_ -ne '' }) }); " +
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
            "{ @() } else { @($env:PATH -split '\\s+' | Where-Object { $_ -ne '' }) }); " +
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
    public void Transpile_BracedVarColonlessDefault_EmitsNullCoalescing()
    {
        // ${VAR-w} (unset-only default) must not silently drop to bare $env:VAR.
        var result = PsEmitter.Transpile("echo ${VAR-fallback}");

        Assert.Equal("Invoke-BashEcho ($env:VAR ?? \"fallback\")", result);
    }

    [Fact]
    public void Transpile_BracedVarColonlessAssignDefault_EmitsNullCoalescingAssign()
    {
        var result = PsEmitter.Transpile("echo ${VAR=default}");

        Assert.Equal("Invoke-BashEcho ($env:VAR ?? ($env:VAR = \"default\"))", result);
    }

    [Fact]
    public void Transpile_BracedVarColonlessAlternative_EmitsConditional()
    {
        var result = PsEmitter.Transpile("echo ${VAR+yes}");

        Assert.Equal("Invoke-BashEcho ($env:VAR ? \"yes\" : \"\")", result);
    }

    [Fact]
    public void Transpile_BracedVarColonlessError_EmitsThrow()
    {
        var result = PsEmitter.Transpile("echo ${VAR?must be set}");

        Assert.Equal("Invoke-BashEcho ($env:VAR ?? $(throw \"must be set\"))", result);
    }

    [Fact]
    public void Transpile_BracedVarTransformQuote_EmitsQuotingExpression()
    {
        // ${VAR@Q} quotes the value for reuse as input — must not drop to bare $env:VAR.
        var result = PsEmitter.Transpile("echo ${VAR@Q}");

        Assert.Equal("Invoke-BashEcho (\"'\" + ($env:VAR -replace \"'\",\"'\\''\") + \"'\")", result);
    }

    [Fact]
    public void Transpile_BracedVarTransformUppercase_EmitsToUpper()
    {
        var result = PsEmitter.Transpile("echo ${VAR@U}");

        Assert.Equal("Invoke-BashEcho $env:VAR.ToUpper()", result);
    }

    [Fact]
    public void Transpile_BracedVarTransformLowercase_EmitsToLower()
    {
        var result = PsEmitter.Transpile("echo ${VAR@L}");

        Assert.Equal("Invoke-BashEcho $env:VAR.ToLower()", result);
    }

    [Fact]
    public void Transpile_BracedVarTransformPrompt_PreservesValue()
    {
        // @P has no PowerShell equivalent: degrade to the bare value, not a silent total drop
        // of the whole expansion — the variable still expands.
        var result = PsEmitter.Transpile("echo ${VAR@P}");

        Assert.Equal("Invoke-BashEcho $env:VAR", result);
    }

    [Fact]
    public void Transpile_ArraySliceOffsetLength_EmitsRangeIndex()
    {
        // ${a[@]:1:2} -> elements at index 1,2 -> PowerShell range index $a[1..2].
        var result = PsEmitter.Transpile("echo ${a[@]:1:2}");
        // Runtime-clamped slice (bash offset/length semantics) so an out-of-range
        // offset yields @() instead of a reversed PowerShell range. Wrapped in $(...)
        // so the scriptblock is a valid command argument.
        Assert.Equal("Invoke-BashEcho $(& { $__psbA = @($a); $__psbO = 1; if ($__psbO -lt 0) { $__psbO = $__psbA.Count + $__psbO }; $__psbO = [Math]::Max(0, [Math]::Min($__psbO, $__psbA.Count)); $__psbN = 2; if ($__psbN -lt 0) { $__psbN = 0 }; $__psbN = [Math]::Min($__psbN, $__psbA.Count - $__psbO); if ($__psbN -le 0) { @() } else { $__psbA[$__psbO..($__psbO + $__psbN - 1)] } })", result);
    }

    [Fact]
    public void Transpile_ArraySliceOffsetOnly_EmitsRangeToEnd()
    {
        var result = PsEmitter.Transpile("echo ${a[@]:2}");
        Assert.Equal("Invoke-BashEcho $(& { $__psbA = @($a); $__psbO = 2; if ($__psbO -lt 0) { $__psbO = $__psbA.Count + $__psbO }; $__psbO = [Math]::Max(0, [Math]::Min($__psbO, $__psbA.Count)); $__psbN = $__psbA.Count - $__psbO; if ($__psbN -lt 0) { $__psbN = 0 }; $__psbN = [Math]::Min($__psbN, $__psbA.Count - $__psbO); if ($__psbN -le 0) { @() } else { $__psbA[$__psbO..($__psbO + $__psbN - 1)] } })", result);
    }

    [Fact]
    public void Transpile_ArraySliceNegativeOffset_EmitsTailRange()
    {
        // ${arr[@]: -2} -> last 2 elements, runtime-clamped (negative offset from end).
        var result = PsEmitter.Transpile("echo ${arr[@]: -2}");
        Assert.Equal("Invoke-BashEcho $(& { $__psbA = @($arr); $__psbO = -2; if ($__psbO -lt 0) { $__psbO = $__psbA.Count + $__psbO }; $__psbO = [Math]::Max(0, [Math]::Min($__psbO, $__psbA.Count)); $__psbN = $__psbA.Count - $__psbO; if ($__psbN -lt 0) { $__psbN = 0 }; $__psbN = [Math]::Min($__psbN, $__psbA.Count - $__psbO); if ($__psbN -le 0) { @() } else { $__psbA[$__psbO..($__psbO + $__psbN - 1)] } })", result);
    }

    [Fact]
    public void Transpile_ArrayElementDefault_AppliesScalarOpToElement()
    {
        // ${arr[0]:-x} -> the operator must apply to the indexed value, not be dropped.
        var result = PsEmitter.Transpile("echo ${arr[0]:-x}");
        Assert.Equal("Invoke-BashEcho ($arr[0] ?? \"x\")", result);
    }

    [Fact]
    public void Transpile_ArrayKeys_IndexedArray_EmitsIndicesNotDotKeys()
    {
        // ${!arr[@]} on an indexed array is its INDICES (0..n-1), and `.Keys`
        // does not exist on a PS array (the old emission crashed). Branch on type.
        var result = PsEmitter.Transpile("echo ${!arr[@]}");
        Assert.Contains("0..($arr.Count - 1)", result);     // indexed-array indices
        Assert.Contains("IDictionary", result);              // associative -> .Keys branch
    }

    [Fact]
    public void Transpile_ArrayKeys_BareArg_UsesArraySubexprNotDollarSubexpr()
    {
        // Regression (parity-followups-2026-06-17): as a BARE argument the
        // indices expansion must use `@(...)`, not `$(...)`. A `$(...)` that
        // yields an empty collection still binds ONE $null positional argument,
        // so `echo ${!arr[@]}` on an empty array crashed the cmdlet
        // (ConvertFromBashArgs NPE). `@(...)` unrolls empty → zero args (bash
        // parity: a blank line) and populated → N separate args.
        var result = PsEmitter.Transpile("echo ${!arr[@]}");
        Assert.Contains("@(if ($arr -is [System.Collections.IDictionary])", result);
        Assert.DoesNotContain("$(if ($arr -is [System.Collections.IDictionary])", result);
    }

    [Fact]
    public void Transpile_ArrayKeys_InsideDoubleQuotes_UsesDollarSubexpr()
    {
        // Inside a double-quoted string the expansion is interpolated into the
        // string (no null-arg hazard), so the array subexpression must be the
        // string-embeddable `$(...)` form, not `@(...)`.
        var result = PsEmitter.Transpile("echo \"${!arr[@]}\"");
        Assert.Contains("$(if ($arr -is [System.Collections.IDictionary])", result);
    }

    [Fact]
    public void Transpile_QuotedArrayAll_ForIn_IteratesElements()
    {
        // `for x in "${arr[@]}"` iterates per element; the quoted expansion must
        // not stringify the array into one word. Emit the array variable directly.
        var result = PsEmitter.Transpile("for x in \"${arr[@]}\"; do echo $x; done");
        Assert.Contains("foreach ($x in $arr)", result);
    }

    [Fact]
    public void Transpile_ArrayElementSlice_AppliesSubstringToElement()
    {
        // Substring indices are clamped so an out-of-range slice yields "" rather
        // than throwing (bash is lenient where .NET Substring is strict).
        var result = PsEmitter.Transpile("echo ${c[1]:0:2}");
        Assert.Contains("$c[1].Substring([Math]::Min(0, $c[1].Length)", result);
    }

    [Fact]
    public void Transpile_AssocElementAlternative_AppliesScalarOpToKey()
    {
        var result = PsEmitter.Transpile("echo ${m[key]:+yes}");
        Assert.Equal("Invoke-BashEcho ($m['key'] ? \"yes\" : \"\")", result);
    }

    [Fact]
    public void Transpile_BracedParamCount_EmitsArgsCount()
    {
        // ${#} must map like $# (count), not $env:.Length.
        var result = PsEmitter.Transpile("echo ${#}");
        Assert.Equal("Invoke-BashEcho $(if ($global:BashPositional) { $global:BashPositional.Count } else { $args.Count })", result);
    }

    [Fact]
    public void Transpile_BracedParamAt_EmitsArgs()
    {
        var result = PsEmitter.Transpile("echo ${@}");
        Assert.Equal("Invoke-BashEcho $(if ($global:BashPositional) { $global:BashPositional } else { $args })", result);
    }

    [Fact]
    public void Transpile_BracedParamStar_EmitsArgs()
    {
        var result = PsEmitter.Transpile("echo ${*}");
        Assert.Equal("Invoke-BashEcho $(if ($global:BashPositional) { $global:BashPositional } else { $args })", result);
    }

    [Fact]
    public void Transpile_BracedPositional10_EmitsPositionalIndex9()
    {
        // ${10} is the ONLY way to write positional >= 10 — must map to index 9, not $env:10.
        var result = PsEmitter.Transpile("echo ${10}");
        Assert.Equal("Invoke-BashEcho $(if ($global:BashPositional) { $global:BashPositional[9] } else { $args[9] })", result);
    }

    [Fact]
    public void Transpile_BracedPositional11_EmitsPositionalIndex10()
    {
        var result = PsEmitter.Transpile("echo ${11}");
        Assert.Equal("Invoke-BashEcho $(if ($global:BashPositional) { $global:BashPositional[10] } else { $args[10] })", result);
    }

    [Fact]
    public void Transpile_BracedVarSuffixRemoval_EmitsReplace()
    {
        var result = PsEmitter.Transpile("echo ${VAR%%pattern}");

        Assert.Equal("Invoke-BashEcho ($env:VAR -replace 'pattern$','')", result);
    }

    [Theory]
    // In a bash PATTERN, `\X` means the LITERAL character X — it strips X's glob
    // meaning, it does not mean "a backslash then X". GlobToRegex escaped the
    // backslash itself, so `${value%\'}` compiled to `^(.*)\\'$`, which requires an
    // actual backslash before the quote and therefore NEVER matched: the standard
    // quote-stripping idiom silently did nothing.
    // Oracle: `value="'hi'"; value="${value%\'}"; value="${value#\'}"` -> `hi`.
    [InlineData(@"echo ${VAR%\'}", @"-replace '^(.*)''$','$1'")]
    [InlineData(@"echo ${VAR#\'}", @"-replace '^''',''")]
    [InlineData(@"echo ${VAR%\""}", @"-replace '^(.*)""$','$1'")]
    // An escaped glob metachar becomes a literal one, not a wildcard.
    [InlineData(@"echo ${VAR%\*}", @"-replace '^(.*)\*$','$1'")]
    [InlineData(@"echo ${VAR%\?}", @"-replace '^(.*)\?$','$1'")]
    // An escaped backslash is one literal backslash.
    [InlineData(@"echo ${VAR%\\}", @"-replace '^(.*)\\$','$1'")]
    public void Transpile_BracedVarPatternWithEscape_TreatsNextCharAsLiteral(
        string bash, string expected)
    {
        Assert.Contains(expected, PsEmitter.Transpile(bash));
    }

    [Fact]
    public void Transpile_BracedVarPatternGlobChars_StillWildcards()
    {
        // Guard the narrow claim: only a BACKSLASH-escaped metachar becomes literal;
        // an unescaped one keeps its glob meaning.
        Assert.Contains("-replace '^(.*).*$','$1'", PsEmitter.Transpile("echo ${VAR%*}"));
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

        // Assignment command-sub preserves internal newlines and strips trailing ones
        // (bash), instead of the array $OFS-joining with a space (which flattened the
        // file to one line).
        Assert.Equal("$env:VAR = \"$((@(Invoke-BashCat file | ForEach-Object { Get-BashText $_ }) -join \"`n\") -replace '(\\r?\\n)+$','')\"", result);
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

        Assert.Equal("$env:dir = \"$((@(Invoke-BashPwd | ForEach-Object { Get-BashText $_ }) -join \"`n\") -replace '(\\r?\\n)+$','')\"", result);
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
    public void Transpile_DevNullAsArgument_StaysLiteralPath()
    {
        // As a command OPERAND /dev/null is a literal path (an empty file), NOT the $null
        // discard sink — bash `echo /dev/null` prints "/dev/null", and `grep x /dev/null`
        // reads an empty file. The $null mapping is only for redirect targets.
        var result = PsEmitter.Transpile("echo /dev/null");

        Assert.Equal("Invoke-BashEcho /dev/null", result);
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
    public void Transpile_CdTildeUser_QuotesUnresolvedTildeLiteral()
    {
        // `~user` has no PowerShell equivalent (WordPart.TildeSub degrades to the literal
        // bareword `~user`, matching bash's own behavior of leaving an unknown-user tilde
        // literal). The emitted PS assignment must quote it — an unquoted `~user` bareword
        // is invalid PowerShell syntax at `$__psbash_cd_target = ~user`.
        var result = PsEmitter.Transpile("cd ~missinguser");

        Assert.StartsWith("$__psbash_cd_target = '~missinguser'", result);
        // No unquoted bareword `= ~...` should survive.
        Assert.DoesNotMatch(@"=\s*~[^'""\s]", result);
    }

    [Fact]
    public void Transpile_CdRecordsOldPwdOnSuccess()
    {
        // Every successful cd must stash the dir it leaves into $OLDPWD so `cd -` can return.
        var result = PsEmitter.Transpile("cd /tmp");

        Assert.Contains("$env:OLDPWD = [System.Environment]::CurrentDirectory", result);
    }

    [Fact]
    public void Transpile_CdDash_TargetsOldPwdAndPrints()
    {
        var result = PsEmitter.Transpile("cd -");

        Assert.StartsWith("$__psbash_cd_target = $env:OLDPWD", result);
        // bash echoes the directory it lands in for `cd -`.
        Assert.Contains("Write-Output $__psbash_cd_resolved", result);
        // Unset OLDPWD must fail with bash's message, not try to resolve an empty path.
        Assert.Contains("cd: OLDPWD not set", result);
        Assert.Contains("[string]::IsNullOrEmpty([string]$__psbash_cd_target)", result);
    }

    [Fact]
    public void Transpile_CdNonDash_DoesNotPrintTarget()
    {
        // Only `cd -` echoes; a normal cd stays silent.
        var result = PsEmitter.Transpile("cd /tmp");

        Assert.DoesNotContain("Write-Output $__psbash_cd_resolved", result);
        Assert.DoesNotContain("cd: OLDPWD not set", result);
    }

    [Fact]
    public void Transpile_CdInAndChain_WrapsStatementOperand()
    {
        var result = PsEmitter.Transpile("cd C:/Temp && echo ok");

        Assert.StartsWith("$($__psbash_cd_target = 'C:/Temp'", result);
        Assert.Contains(") && Invoke-BashEcho ok", result);
    }

    [Fact]
    public void Transpile_CdWindowsBackslashPath_RestoresSeparatorsAndQuotes()
    {
        // Bash's lexer eats `\U` etc. as escapes (`C:\Users` -> `C:Users`); a Windows user
        // means the backslashes as separators. cd must reconstruct the drive path and quote it.
        var result = PsEmitter.Transpile(@"cd C:\Users\andyb\work\beagle-term");

        Assert.StartsWith(@"$__psbash_cd_target = 'C:\Users\andyb\work\beagle-term'", result);
    }

    [Fact]
    public void Transpile_CdWindowsDriveRoot_Quotes()
    {
        var result = PsEmitter.Transpile(@"cd C:\");

        Assert.StartsWith(@"$__psbash_cd_target = 'C:\'", result);
    }

    [Fact]
    public void Transpile_CdWindowsPathWithEscapedSpace_KeepsSpaceNotBackslash()
    {
        // `\ ` is a genuine bash escape (space), not a Windows separator — the reconstructed
        // path keeps the space so `C:\Users\andyb\3D\ Objects` targets "C:\Users\andyb\3D Objects".
        var result = PsEmitter.Transpile(@"cd C:\Users\andyb\3D\ Objects");

        Assert.StartsWith(@"$__psbash_cd_target = 'C:\Users\andyb\3D Objects'", result);
    }

    [Fact]
    public void Transpile_CdEscapedSpaceOnlyWord_StaysOnNormalEmission()
    {
        // No backslash survives reconstruction (`my\ dir` -> "my dir"), so the word is not a
        // Windows path and must not be hijacked into a quoted drive-path literal.
        var result = PsEmitter.Transpile(@"cd my\ dir");

        Assert.DoesNotContain(@"$__psbash_cd_target = 'my", result);
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

        // A command condition tests its EXIT CODE (bash semantics), not its output truthiness.
        Assert.Equal("if ((& { [void](cmd); $global:LASTEXITCODE -eq 0 })) { Invoke-BashEcho yes }", result);
    }

    [Fact]
    public void Transpile_IfThenElseFi_EmitsIfElseBlock()
    {
        var result = PsEmitter.Transpile("if cmd; then a; else b; fi");

        Assert.Equal("if ((& { [void](cmd); $global:LASTEXITCODE -eq 0 })) { a } else { b }", result);
    }

    [Fact]
    public void Transpile_IfNegatedCommandCondition_SuppressesOutputWithVoid()
    {
        // Regression: a negated-pipeline condition (`if ! cmd`) must wrap the command in
        // [void]. Without it, `& { cmd; $global:LASTEXITCODE -ne 0 }` returns the command's
        // OUTPUT alongside the boolean — a 2-element array PowerShell reads as truthy — so the
        // condition silently inverts (`if ! echo X; then A; else B` ran A and swallowed X).
        var result = PsEmitter.Transpile("if ! cmd; then a; else b; fi");

        Assert.Equal("if ((& { [void](cmd); $global:LASTEXITCODE -ne 0 })) { a } else { b }", result);
    }

    [Fact]
    public void Transpile_IfElifElseFi_EmitsFullChain()
    {
        var result = PsEmitter.Transpile("if cmd1; then a; elif cmd2; then b; else c; fi");

        Assert.Equal("if ((& { [void](cmd1); $global:LASTEXITCODE -eq 0 })) { a } elseif ((& { [void](cmd2); $global:LASTEXITCODE -eq 0 })) { b } else { c }", result);
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

        Assert.Equal("if ((& { [void](cmd1); $global:LASTEXITCODE -eq 0 })) { if ((& { [void](cmd2); $global:LASTEXITCODE -eq 0 })) { inner } }", result);
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

        Assert.Equal("if ((& { [void](cmd); $global:LASTEXITCODE -eq 0 })) { a; b }", result);
    }

    [Fact]
    public void Transpile_StandaloneFileTest_EmitsTestPath()
    {
        var result = PsEmitter.Transpile("[ -f file ]");

        Assert.Equal("$(if ((Test-Path \"file\" -PathType Leaf)) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })", result);
    }

    [Fact]
    public void Transpile_StandaloneDirTest_EmitsTestPathContainer()
    {
        var result = PsEmitter.Transpile("[ -d dir ]");

        Assert.Equal("$(if ((Test-Path \"dir\" -PathType Container)) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })", result);
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

        Assert.Equal("$(if (([string]::IsNullOrEmpty($env:VAR))) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })", result);
    }

    [Fact]
    public void Transpile_StandaloneNonEmptyTest_EmitsNegatedIsNullOrEmpty()
    {
        var result = PsEmitter.Transpile("[ -n \"$VAR\" ]");

        Assert.Equal("$(if ((-not [string]::IsNullOrEmpty($env:VAR))) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })", result);
    }

    [Fact]
    public void Transpile_ExtendedFileTest_EmitsTestPath()
    {
        var result = PsEmitter.Transpile("[[ -f file ]]");

        Assert.Equal("$(if ((Test-Path \"file\" -PathType Leaf)) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })", result);
    }

    [Fact]
    public void Transpile_ExtendedStringEquals_EmitsEq()
    {
        var result = PsEmitter.Transpile("[[ $var == \"foo\" ]]");

        // Literal operands are single-quoted (equivalent to "foo" for comparison,
        // and avoids accidental PowerShell interpolation).
        Assert.Equal("$(if (($env:var -eq 'foo')) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })", result);
    }

    [Fact]
    public void Transpile_ExtendedIntComparison_EmitsOp()
    {
        var result = PsEmitter.Transpile("[[ $a -eq $b ]]");

        // Numeric operators cast both operands to [long] so the compare is integer, not
        // string ('10' -gt 9 must be true).
        Assert.Equal("$(if (([long]($env:a) -eq [long]($env:b))) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })", result);
    }

    // ── a double-quoted test operand keeps its expansions live ───────────────

    [Theory]
    // `[ -z "${V:-}" ]` is everywhere in defensive shell scripts. EmitTestArg kept
    // the unwrapped text only when it started with `$`, so every other expansion
    // shape was single-quoted as a LITERAL: the test compared against the source
    // text `($env:V ?? "")`, which is never empty — `-z` was always false and `-n`
    // always true. Silent wrong answer, no error.
    [InlineData("[ -z \"${V:-}\" ]", "[string]::IsNullOrEmpty(($env:V ?? ''))")]
    [InlineData("[ -n \"${V:-}\" ]", "-not [string]::IsNullOrEmpty(($env:V ?? ''))")]
    [InlineData("[ -z \"$V\" ]", "[string]::IsNullOrEmpty($env:V)")]
    public void Transpile_TestOperand_QuotedExpansion_StaysAnExpression(
        string bash, string expectedFragment)
    {
        var result = PsEmitter.Transpile(bash);

        // Normalize the emitted double quotes so the InlineData stays readable.
        Assert.Contains(expectedFragment.Replace("''", "\"\""), result);
    }

    [Fact]
    public void Transpile_TestOperand_MixedLiteralAndExpansion_Interpolates()
    {
        // "pre$V" must stay a PowerShell double-quoted (interpolating) string; the
        // single-quoted form froze $env:V as literal text.
        var result = PsEmitter.Transpile("[ \"x\" = \"pre$V\" ]");

        Assert.Contains("\"pre$env:V\"", result);
        Assert.DoesNotContain("'pre$env:V'", result);
    }

    // ── [[ ( … ) ]] grouping ─────────────────────────────────────────────────
    //
    // The lexer emits grouping parens as LParen/RParen and ParseTestExpr used to
    // BREAK on them, dropping the group's operands from the word list — so the
    // clause silently collapsed to a constant (and once emitted `if (())`).

    [Theory]
    [InlineData("[[ ( -n $x ) ]]", "-not [string]::IsNullOrEmpty($env:x)")]
    [InlineData("[[ (-n $x) ]]", "-not [string]::IsNullOrEmpty($env:x)")]
    public void Transpile_ExtendedTestGrouping_TranslatesTheGroupedOperand(
        string bash, string expected)
    {
        var result = PsEmitter.Transpile(bash);

        Assert.Contains(expected, result);
        Assert.DoesNotContain("(())", result);
    }

    [Fact]
    public void Transpile_ExtendedTestGrouping_KeepsOperatorAssociation()
    {
        // `a || ( b && c )` must NOT flatten to `a || b && c` — the splitter only
        // breaks at paren depth 0, so the group stays one operand.
        var result = PsEmitter.Transpile("[[ -n $a || ( -n $b && -n $c ) ]]");

        Assert.Contains("-or", result);
        Assert.Contains("-and", result);
        // The && belongs to the grouped sub-expression, so its operands are the
        // b/c tests, not a/b.
        Assert.Matches(@"env:b\)\).*-and.*env:c", result);
    }

    [Fact]
    public void Transpile_ExtendedTestGrouping_SiblingGroups_SplitAtTopLevel()
    {
        // `( a ) || ( b )` — the leading paren closes before the end, so it is NOT
        // one enclosing group; the depth-aware split handles it.
        var result = PsEmitter.Transpile("[[ ( -n $a ) || ( -n $b ) ]]");

        Assert.Contains("env:a", result);
        Assert.Contains("env:b", result);
        Assert.Contains("-or", result);
    }

    [Fact]
    public void Transpile_UnsupportedTestOperator_DegradesToFalsePlusDiagnostic()
    {
        // `[ -o PROMPT_SUBST ]` (shell-option test) is not implemented. The old
        // fallback joined the operands with spaces and emitted `'-o' 'PROMPT_SUBST'`
        // — two adjacent values, never valid PowerShell — so one such line broke the
        // parse of the ENTIRE file. Wrong-but-visible beats unparseable.
        var result = PsEmitter.Transpile("[ -o PROMPT_SUBST ] && echo on");

        Assert.Contains("unsupported test operator", result);
        Assert.DoesNotContain("'-o' 'PROMPT_SUBST'", result);
    }

    [Fact]
    public void Transpile_SingleOperandTest_StillTestsNonEmpty()
    {
        // Guard the narrow claim: the one-operand `[ str ]` non-empty test is
        // unchanged by the multi-operand degradation.
        Assert.Contains("$env:x", PsEmitter.Transpile("[ \"$x\" ] && echo y"));
    }

    [Fact]
    public void Transpile_TestOperand_PureLiteral_StaysSingleQuoted()
    {
        // Guard the narrow claim: a literal with no expansion is unchanged, so
        // PowerShell does not try to run it as a command.
        Assert.Contains("'abc'", PsEmitter.Transpile("[ \"abc\" = \"abc\" ]"));
    }

    [Fact]
    public void Transpile_ExtendedRegex_EmitsMatch()
    {
        var result = PsEmitter.Transpile("[[ $a =~ ^[0-9]+$ ]]");

        Assert.Equal("$(if (($env:a -match '^[0-9]+$')) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })", result);
    }

    // ── =~ right-hand side is a REGEX, not a shell token stream ──────────────
    //
    // The lexer is context-free and splits `^(a|b)$` into LParen/Pipe/RParen
    // tokens. ParseTestExpr used to stop at the `(`, which silently truncated
    // the pattern to `^` on the `&&` path and threw "Expected 'then'" inside an
    // `if`. The RHS is now re-read from the raw source as one word.

    [Theory]
    // Alternation group — the shape that broke (7 real-world scripts in a
    // 100-file corpus sweep).
    [InlineData("[[ $a =~ ^(x|y)$ ]]", "^(x|y)$")]
    [InlineData("[[ $a =~ ^(ls|pwd)(1|2) ]]", "^(ls|pwd)(1|2)")]
    // Backslash escapes reach the regex engine verbatim. Oracle-verified:
    // `[[ axb =~ ^a\.b$ ]]` does NOT match, so the backslash must survive —
    // the ordinary word decomposer dropped it, widening `\.` to "any char".
    [InlineData(@"[[ $a =~ ^a\.b$ ]]", @"^a\.b$")]
    // POSIX bracket classes are valid ERE but not valid .NET regex.
    [InlineData("[[ $a =~ ^[[:digit:]]+$ ]]", "^[0-9]+$")]
    [InlineData("[[ $a =~ [^[:alpha:]] ]]", "[^a-zA-Z]")]
    [InlineData("[[ $a =~ [[:space:]] ]]", @"[\s]")]
    // An unknown class name is left alone rather than guessed at.
    [InlineData("[[ $a =~ [[:zzz:]] ]]", "[[:zzz:]]")]
    public void Transpile_ExtendedRegexRhs_PreservesPatternVerbatim(
        string bash, string expectedPattern)
    {
        var result = PsEmitter.Transpile(bash);

        Assert.Contains($"-match '{expectedPattern}'", result);
    }

    [Fact]
    public void Transpile_ExtendedRegexInIfCondition_ParsesGroupedPattern()
    {
        // Same pattern inside an `if` — the context that threw before.
        var result = PsEmitter.Transpile("if [[ $a =~ ^(x|y)$ ]]; then echo hit; fi");

        Assert.Contains("-match '^(x|y)$'", result);
        Assert.Contains("hit", result);
    }

    [Theory]
    [InlineData("[[ $a =~ $re ]]", "$env:re")]
    [InlineData("[[ $a =~ ${re} ]]", "$env:re")]
    public void Transpile_ExtendedRegexRhs_SoleVariable_PassedBareNotQuoted(
        string bash, string expected)
    {
        // `re='^a.b$'; [[ $x =~ $re ]]` is the idiomatic bash regex form.
        // Single-quoting the emitted variable would match the literal text
        // "$env:re" instead of the pattern the variable holds.
        var result = PsEmitter.Transpile(bash);

        Assert.Contains($"-match {expected}", result);
        Assert.DoesNotContain($"-match '{expected}'", result);
    }

    [Fact]
    public void Transpile_ExtendedRegexRhs_MixedLiteralAndVar_StaysQuoted()
    {
        // Only a SOLE expansion goes bare; a mixed word keeps the literal path.
        var result = PsEmitter.Transpile("[[ $a =~ ^${p}[0-9]+$ ]]");

        Assert.Contains("-match '", result);
    }

    // ── here-string on a COMPOUND command, `for` single item, multi-var `read` ──

    [Fact]
    public void Transpile_WhileReadWithHereString_FeedsTheLoop()
    {
        // `done <<< "$x"` — `<<<` was not a compound redirect op, so the operator and
        // its word were never consumed: the here-string was DROPPED and the loop read
        // nothing. In some surroundings the stray tokens also emitted an empty
        // PowerShell pipe element, breaking the parse of the whole file.
        var result = PsEmitter.Transpile(
            "while read -r l; do echo \"$l\"; done <<< \"$output\"");

        Assert.Contains("Emit-BashLine", result);
        Assert.Contains("env:output", result);
        Assert.DoesNotContain("| ;", result);
    }

    [Fact]
    public void Transpile_ForLoopWithHereString_FeedsTheLoop()
    {
        var result = PsEmitter.Transpile("for x in a; do echo $x; done <<< \"y\"");

        Assert.Contains("Emit-BashLine", result);
    }

    [Theory]
    // The bug: a ONE-item list emitted `foreach ($x in one)`, where PowerShell
    // treats the bare word as a command to invoke ("one: command not found").
    // The two-item form was correctly quoted, which is what hid it.
    [InlineData("for x in one; do echo $x; done", "foreach ($x in 'one')")]
    [InlineData("for x in one two; do echo $x; done", "foreach ($x in 'one','two')")]
    public void Transpile_ForInList_QuotesBareLiterals(string bash, string expected)
    {
        Assert.Contains(expected, PsEmitter.Transpile(bash));
    }

    [Theory]
    // Anything EmitWord already rendered as a PowerShell value must pass through.
    [InlineData("for x in \"$list\"; do echo $x; done", "\"$env:list\"")]
    [InlineData("for x in $list; do echo $x; done", "$env:list")]
    public void Transpile_ForInSingleItem_LeavesPowerShellValuesUnquoted(
        string bash, string expected)
    {
        Assert.Contains($"foreach ($x in {expected})", PsEmitter.Transpile(bash));
    }

    [Fact]
    public void Transpile_WhileReadMultipleVars_BindsEveryVariable()
    {
        // `read a b` splits the line across BOTH: a=first field, b=the remainder.
        // Only the LAST name used to be bound, so `$a` stayed unset and `$b` got the
        // whole line. Oracle: `read a b` over "alpha beta gamma" -> a=alpha,
        // b="beta gamma".
        var result = PsEmitter.Transpile("while read -r a b; do echo \"$a\"; done < f");

        Assert.Contains("${a} =", result);
        Assert.Contains("${b} =", result);
        // The limit argument is what gives the LAST variable the remainder.
        Assert.Contains("-split '\\s+', 2", result);
    }

    [Fact]
    public void Transpile_WhileReadMultipleVars_UsesArraySubexpressionNotDollar()
    {
        // `$( … )` collapses a ONE-element split to a scalar string, and `$str[0]`
        // is then its first CHARACTER — a single-field line bound `a` to "s"
        // instead of "solo". Must be the array subexpression `@( … )`.
        var result = PsEmitter.Transpile("while read -r a b; do echo \"$a\"; done < f");

        Assert.Contains("$__psbash_readf = @(", result);
        Assert.DoesNotContain("$__psbash_readf = $(", result);
    }

    [Fact]
    public void Transpile_WhileReadSingleVar_KeepsTheDirectBinding()
    {
        // Guard the narrow claim: the one-variable form is unchanged (no split).
        var result = PsEmitter.Transpile("while read -r l; do echo \"$l\"; done < f");

        Assert.Contains("${l} = $_", result);
        Assert.DoesNotContain("__psbash_readf", result);
    }

    [Fact]
    public void Transpile_ExtendedGlob_EmitsLike()
    {
        var result = PsEmitter.Transpile("[[ $a == foo* ]]");

        Assert.Equal("$(if (($env:a -like 'foo*')) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })", result);
    }

    [Fact]
    public void Transpile_ExtendedLogicalAnd_EmitsAndOp()
    {
        var result = PsEmitter.Transpile("[[ -f file && -d dir ]]");

        Assert.Equal("$(if (((Test-Path \"file\" -PathType Leaf) -and (Test-Path \"dir\" -PathType Container))) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })", result);
    }

    [Fact]
    public void Transpile_ExtendedLogicalOr_EmitsOrOp()
    {
        var result = PsEmitter.Transpile("[[ $a == \"x\" || $b == \"y\" ]]");

        Assert.Equal("$(if ((($env:a -eq 'x') -or ($env:b -eq 'y'))) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })", result);
    }

    [Fact]
    public void Transpile_ExtendedNotEquals_EmitsNe()
    {
        var result = PsEmitter.Transpile("[[ $a != \"bar\" ]]");

        Assert.Equal("$(if (($env:a -ne 'bar')) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })", result);
    }

    [Fact]
    public void Transpile_TestBareStringLiterals_AreQuotedNotBarewords()
    {
        // Regression: `[ abc = abc ]` emitted `(abc -eq abc)` — bare `abc` ran as
        // a PowerShell command ("command not found"). Literal operands must be
        // single-quoted strings. Numeric operands stay bare for numeric compares.
        Assert.Contains("'abc' -eq 'abc'", PsEmitter.Transpile("[ abc = abc ]"));
        Assert.Contains("'abc' -ne 'xyz'", PsEmitter.Transpile("[ abc != xyz ]"));
        Assert.Contains("$env:x -eq 'abc'", PsEmitter.Transpile("x=1; [ $x = abc ]"));
        Assert.Contains("[long](5) -eq [long](5)", PsEmitter.Transpile("[ 5 -eq 5 ]"));   // numeric compare casts to [long]
    }

    [Fact]
    public void Transpile_TestBareLiteral_ZeroLength()
        => Assert.Contains("[string]::IsNullOrEmpty('abc')", PsEmitter.Transpile("[ -z abc ]"));

    [Fact]
    public void Transpile_ExtendedLessThan_EmitsStringCompare()
    {
        var result = PsEmitter.Transpile("[[ $a < $b ]]");

        Assert.Equal("$(if (([string]::Compare($env:a, $env:b, [System.StringComparison]::Ordinal) -lt 0)) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })", result);
    }

    [Fact]
    public void Transpile_ExtendedGreaterThan_EmitsStringCompare()
    {
        var result = PsEmitter.Transpile("[[ $a > $b ]]");

        Assert.Equal("$(if (([string]::Compare($env:a, $env:b, [System.StringComparison]::Ordinal) -gt 0)) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })", result);
    }

    [Fact]
    public void Transpile_ExtendedNumericLt_StillEmitsLt()
    {
        var result = PsEmitter.Transpile("[[ $a -lt $b ]]");

        Assert.Equal("$(if (([long]($env:a) -lt [long]($env:b))) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })", result);
    }

    [Fact]
    public void Transpile_ExtendedNumericGt_StillEmitsGt()
    {
        var result = PsEmitter.Transpile("[[ $a -gt $b ]]");

        Assert.Equal("$(if (([long]($env:a) -gt [long]($env:b))) { $global:LASTEXITCODE = 0 } else { $global:LASTEXITCODE = 1 })", result);
    }

    [Fact]
    public void Transpile_IfCdThen_UsesSubexpressionForMultiStatementCondition()
    {
        // Regression: `cd` emits a multi-statement block, so the if-condition exit-code
        // test must wrap it in [void]$(...) (a subexpression). [void](...) (grouping
        // parens) cannot hold a statement list — `if cd /tmp; then …` was unparseable
        // PowerShell ("Missing closing ')'").
        var result = PsEmitter.Transpile("if cd /tmp; then echo ok; fi");
        Assert.Contains("[void]$(", result);
        Assert.DoesNotContain("[void]($__psbash_cd_target", result);
    }

    [Fact]
    public void Transpile_AwkFieldSepExplicitlyQuoted_EmitsSingleStringNotArray()
    {
        // Regression: `awk -F"," …` — the word -F"," (a bare -F literal adjacent to a
        // double-quoted comma) must emit ONE single-quoted argument '-F,'. The old
        // re-wrap produced "-F",", which PowerShell parses as the two-element array
        // @('-F',''), corrupting the flag.
        var result = PsEmitter.Transpile("echo x | awk -F\",\" '{print $1}'");
        Assert.Contains("Invoke-BashAwk '-F,'", result);
        Assert.DoesNotContain("\"-F\",\"", result);
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
    public void Transpile_ForInGlob_EmitsResolveBashGlob()
    {
        var result = PsEmitter.Transpile("for f in *.txt; do cat $f; done");

        // Resolve-BashGlob (not Resolve-Path): a matching glob expands, but an
        // unmatched glob falls back to the literal word so the loop still runs
        // once — bash nullglob is OFF by default.
        Assert.Equal("$__psbash_iter = 0; foreach ($f in (Resolve-BashGlob *.txt)) { if (++$__psbash_iter -gt ($env:PSBASH_MAX_ITERATIONS ?? 100000)) { throw \"ps-bash: loop iteration limit exceeded ($(($env:PSBASH_MAX_ITERATIONS ?? 100000)))\" }; Invoke-BashCat $f }", result);
    }

    [Fact]
    public void Transpile_ForInSingleNonMatchingGlob_FallsBackToLiteralWordViaResolveBashGlob()
    {
        // bash: `for f in *.xyz` with no match iterates ONCE with the literal
        // word `*.xyz` (nullglob off). Resolve-Path would error + yield nothing
        // (zero iterations). Resolve-BashGlob returns the literal on no-match.
        var result = PsEmitter.Transpile("for f in *.xyz; do echo $f; done");

        Assert.Contains("foreach ($f in (Resolve-BashGlob *.xyz))", result);
        Assert.DoesNotContain("Resolve-Path", result);
    }

    [Fact]
    public void Transpile_ForInMixedGlobList_RoutesWholeListThroughResolveBashGlob()
    {
        // A single non-matching glob/literal must NOT nuke the rest of the list:
        // Resolve-BashGlob resolves each operand independently, so `a.txt` and
        // `missing.xyz` survive even when `*.log` matches nothing.
        var result = PsEmitter.Transpile("for f in a.txt *.log missing.xyz; do echo $f; done");

        Assert.Contains("foreach ($f in (Resolve-BashGlob a.txt *.log missing.xyz))", result);
        Assert.DoesNotContain("Resolve-Path", result);
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

        // The condition is evaluated through Invoke-BashArith (like a standalone
        // (( … )) condition) rather than the old naive </> string-replace, so the
        // full C operator set — including << / >> shifts — is honored. See
        // Transpile_ForArith_ShiftOperatorInCondition_NotShredded.
        Assert.Equal("$__psbash_iter = 0; for (${i} = $(Invoke-BashArith 'i=0'); ((Invoke-BashArith 'i<10') -ne 0); $null = Invoke-BashArith 'i++') { if (++$__psbash_iter -gt ($env:PSBASH_MAX_ITERATIONS ?? 100000)) { throw \"ps-bash: loop iteration limit exceeded ($(($env:PSBASH_MAX_ITERATIONS ?? 100000)))\" }; Invoke-BashEcho $i }", result);
    }

    // Regression: TranslateArithCondition string-replaced < and > unconditionally,
    // shredding the << and >> shift operators — `i < (n<<1)` became
    // `[int]$env:n -lt -lt 1`, an invalid PowerShell parse. Routing the clause
    // through Invoke-BashArith keeps the shift intact and produces parseable PS.
    [Fact]
    public void Transpile_ForArith_ShiftOperatorInCondition_NotShredded()
    {
        var result = PsEmitter.Transpile("for ((i=0; i < (n<<1); i++)); do echo $i; done");

        Assert.Contains("(Invoke-BashArith 'i < (n<<1)') -ne 0", result);
        Assert.DoesNotContain("-lt -lt", result);
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

        Assert.Equal("$__psbash_iter = 0; while ((& { [void](cmd); $global:LASTEXITCODE -eq 0 })) { if (++$__psbash_iter -gt ($env:PSBASH_MAX_ITERATIONS ?? 100000)) { throw \"ps-bash: loop iteration limit exceeded ($(($env:PSBASH_MAX_ITERATIONS ?? 100000)))\" }; body }", result);
    }

    [Fact]
    public void Transpile_UntilCmd_EmitsNegatedWhileLoop()
    {
        var result = PsEmitter.Transpile("until cmd; do body; done");

        Assert.Equal("$__psbash_iter = 0; while (-not ((& { [void](cmd); $global:LASTEXITCODE -eq 0 }))) { if (++$__psbash_iter -gt ($env:PSBASH_MAX_ITERATIONS ?? 100000)) { throw \"ps-bash: loop iteration limit exceeded ($(($env:PSBASH_MAX_ITERATIONS ?? 100000)))\" }; body }", result);
    }

    [Fact]
    public void Transpile_WhileReadLine_EmitsForEachObjectPipeline()
    {
        var result = PsEmitter.Transpile("while read line; do echo $line; done");

        // `$input |` drains the scriptblock's piped input into the chain (a leading ForEach-Object
        // does not auto-receive it when this is a pipe target wrapped in `& { ... }`); the
        // `$null -ne $_` guard makes the BashText probe null-safe. The read var is bound with a real
        // `${line} = $_` assignment (not a text rewrite of $line -> $_, which clobbered literals).
        Assert.Equal(
            "$input | ForEach-Object { if ($null -ne $_ -and $_.PSObject.Properties['BashText']) { $_.BashText } else { \"$_\" } } | ForEach-Object { ($_ -replace \"`n$\",\"\") -split \"`n\" } | ForEach-Object { ${line} = $_; Invoke-BashEcho $line }",
            result);
    }

    [Fact]
    public void Transpile_WhileReadLine_PreservesReadVarInsideSingleQuotedLiteral()
    {
        // Regression (#6): the old $line -> $_ text rewrite clobbered the name inside
        // single-quoted PS literals. A bash '... $line ...' must stay literal (bash
        // prints it verbatim), so the emitted single-quoted string must be untouched.
        var result = PsEmitter.Transpile("while read line; do echo 'lit=$line'; done");

        Assert.Contains("${line} = $_;", result);
        Assert.Contains("'lit=$line'", result);   // literal preserved, NOT rewritten to $_
        Assert.DoesNotContain("'lit=$_'", result);
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

        Assert.Equal("switch ($env:x) { 'a' { Invoke-BashEcho a; break } 'b' { Invoke-BashEcho b; break } }", result);
    }

    [Fact]
    public void Transpile_CaseMultiplePatterns_EmitsSeparateClauses()
    {
        var result = PsEmitter.Transpile("case $x in a|b) echo ab;; esac");

        Assert.Equal("switch ($env:x) { 'a' { Invoke-BashEcho ab; break } 'b' { Invoke-BashEcho ab; break } }", result);
    }

    [Fact]
    public void Transpile_CaseDefaultStar_EmitsDefault()
    {
        var result = PsEmitter.Transpile("case $x in a) echo a;; *) echo other;; esac");

        Assert.Equal("switch ($env:x) { 'a' { Invoke-BashEcho a; break } default { Invoke-BashEcho other; break } }", result);
    }

    [Fact]
    public void Transpile_CaseFallThrough_RunsNextArmBody()
    {
        // `;&` = fall through: matching `a` runs `echo a` AND the next arm's
        // `echo b`. PowerShell switch has no clause fall-through, so the next
        // body is inlined into the matched clause.
        var result = PsEmitter.Transpile("case $x in a) echo a ;& b) echo b;; esac");

        Assert.Equal(
            "switch ($env:x) { 'a' { Invoke-BashEcho a; Invoke-BashEcho b; break } 'b' { Invoke-BashEcho b; break } }",
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

        // The ;;& arm 'a' emits no break (continue testing); the trailing ;; arm 'b' does.
        Assert.Equal(
            "switch ($env:x) { 'a' { Invoke-BashEcho a } 'b' { Invoke-BashEcho b; break } }",
            result);
    }

    [Fact]
    public void Transpile_CaseChainedFallThrough_InlinesAllChainedBodies()
    {
        // `;&` -> `;&` -> `;;`: matching `a` runs a, b, AND c.
        var result = PsEmitter.Transpile("case $x in a) echo a ;& b) echo b ;& c) echo c;; esac");

        Assert.Equal(
            "switch ($env:x) { " +
            "'a' { Invoke-BashEcho a; Invoke-BashEcho b; Invoke-BashEcho c; break } " +
            "'b' { Invoke-BashEcho b; Invoke-BashEcho c; break } " +
            "'c' { Invoke-BashEcho c; break } }",
            result);
    }

    [Fact]
    public void Transpile_NestedCase_EmitsNestedSwitch()
    {
        var result = PsEmitter.Transpile(
            "case $x in a) case $y in b) echo b;; esac;; esac");

        Assert.Equal(
            "switch ($env:x) { 'a' { switch ($env:y) { 'b' { Invoke-BashEcho b; break } }; break } }",
            result);
    }

    [Fact]
    public void Transpile_CaseWithGlobPattern_EmitsWildcard()
    {
        var result = PsEmitter.Transpile("case $f in *.txt) echo text;; *) echo other;; esac");

        Assert.Equal(
            "switch -Wildcard ($env:f) { '*.txt' { Invoke-BashEcho text; break } default { Invoke-BashEcho other; break } }",
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

        // The subshell is a chain OPERAND, so it must be wrapped in `$( … )`.
        // `&&` / `||` are pipeline-chain operators and require a pipeline; the
        // previous bare `try { … } finally { … } && …` was not parseable
        // PowerShell at all ("Unexpected token '&&'"), which this assertion used
        // to pin. Same reason `while` / `case` operands are wrapped.
        Assert.StartsWith("$(try { Push-Location; $($__psbash_cd_target = '/tmp'", result);
        Assert.Contains("&& Invoke-BashPwd } finally { Pop-Location }) && Invoke-BashPwd", result);
    }

    [Theory]
    // A compound command emits a PowerShell STATEMENT, which cannot be a
    // pipeline-chain operand: `cmd || switch (…) {…}` and `cmd && while (…) {…}`
    // were both parse errors that broke the whole file.
    [InlineData("true || case $x in a) echo a;; esac", "|| $(switch")]
    [InlineData("true && while false; do echo x; done", "&& $(")]
    [InlineData("true || (echo a)", "|| $(try {")]
    public void Transpile_CompoundCommandInAndOrChain_IsWrappedInSubexpression(
        string bash, string expected)
    {
        Assert.Contains(expected, PsEmitter.Transpile(bash));
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

    // $(( )) value context now routes to the runtime bash-arithmetic evaluator
    // (Invoke-BashArith) instead of a verbatim PowerShell $( ) subexpression. The
    // old emission mistranslated almost every non-trivial operator (integer /,
    // **, bitwise/shift, 1/0 comparisons) — see BashArithTests for the oracle
    // values. The evaluator resolves bare variables itself, so no $env: prefix.
    [Fact]
    public void Transpile_ArithSub_BasicAddition()
    {
        var result = PsEmitter.Transpile("echo $((x + 1))");
        Assert.Equal("Invoke-BashEcho $(Invoke-BashArith 'x + 1')", result);
    }

    [Fact]
    public void Transpile_ArithSub_LiteralAddition()
    {
        var result = PsEmitter.Transpile("echo $((2 + 3))");
        Assert.Equal("Invoke-BashEcho $(Invoke-BashArith '2 + 3')", result);
    }

    [Fact]
    public void Transpile_ArithSub_Multiplication()
    {
        var result = PsEmitter.Transpile("echo $((x * y))");
        Assert.Equal("Invoke-BashEcho $(Invoke-BashArith 'x * y')", result);
    }

    [Fact]
    // Standalone (( expr )) now evaluates via the bash-arithmetic evaluator and
    // sets $LASTEXITCODE (0 iff result != 0) with no stdout — so **, bitwise,
    // etc. are correct and `(( … )) && cmd` chains work. We assert the evaluator
    // routing; the exact $LASTEXITCODE wrapper is PsBuild.SilentExitFromBool.
    public void Transpile_ArithCommand_Increment()
        => Assert.Contains("Invoke-BashArith 'x++'", PsEmitter.Transpile("(( x++ ))"));

    [Fact]
    public void Transpile_ArithCommand_Decrement()
        => Assert.Contains("Invoke-BashArith 'x--'", PsEmitter.Transpile("(( x-- ))"));

    [Fact]
    public void Transpile_ArithCommand_PreIncrement()
        => Assert.Contains("Invoke-BashArith '++x'", PsEmitter.Transpile("(( ++x ))"));

    [Fact]
    public void Transpile_ArithCommand_PreDecrement()
        => Assert.Contains("Invoke-BashArith '--x'", PsEmitter.Transpile("(( --x ))"));

    [Fact]
    public void Transpile_ArithCommand_Comparison_GreaterThan()
        => Assert.Contains("Invoke-BashArith 'x > 5'", PsEmitter.Transpile("(( x > 5 ))"));

    [Fact]
    public void Transpile_ArithCommand_Comparison_LessThan()
        => Assert.Contains("Invoke-BashArith 'x < 5'", PsEmitter.Transpile("(( x < 5 ))"));

    [Fact]
    public void Transpile_ArithCommand_Comparison_GreaterEqual()
        => Assert.Contains("Invoke-BashArith 'x >= 5'", PsEmitter.Transpile("(( x >= 5 ))"));

    [Fact]
    public void Transpile_ArithCommand_Comparison_LessEqual()
        => Assert.Contains("Invoke-BashArith 'x <= 5'", PsEmitter.Transpile("(( x <= 5 ))"));

    [Fact]
    public void Transpile_ArithCommand_Comparison_Equal()
        => Assert.Contains("Invoke-BashArith 'x == 5'", PsEmitter.Transpile("(( x == 5 ))"));

    [Fact]
    public void Transpile_ArithCommand_Comparison_NotEqual()
        => Assert.Contains("Invoke-BashArith 'x != 5'", PsEmitter.Transpile("(( x != 5 ))"));

    [Fact]
    public void Transpile_ArithCommand_Ternary()
        => Assert.Contains("Invoke-BashArith 'x > 0 ? 1 : 0'", PsEmitter.Transpile("(( x > 0 ? 1 : 0 ))"));

    [Fact]
    public void Transpile_ArithCommand_Standalone_SetsExitCode()
        => Assert.Contains("$global:LASTEXITCODE", PsEmitter.Transpile("(( x++ ))"));

    [Fact]
    public void Transpile_IfArithCommand_Condition_RoutesToEvaluatorNonZeroTest()
    {
        // The old native emission made `if (( 2 ** 10 > 1000 ))` a PowerShell
        // parse error (** ). Now the condition is true iff the evaluator result
        // is non-zero.
        var result = PsEmitter.Transpile("if (( 2 ** 10 > 1000 )); then echo big; fi");
        Assert.Contains("(Invoke-BashArith '2 ** 10 > 1000') -ne 0", result);
    }

    [Fact]
    public void Transpile_ArithSub_InAssignment()
    {
        var result = PsEmitter.Transpile("result=$((x + 1))");
        Assert.Equal("$env:result = \"$(Invoke-BashArith 'x + 1')\"", result);
    }

    [Fact]
    public void Transpile_ArithSub_Power()
    {
        // The old emission was a literal PowerShell `$(2 ** 3)`, which is a PARSE
        // ERROR (** is not a PowerShell operator). Now evaluated correctly = 8.
        var result = PsEmitter.Transpile("echo $((2 ** 3))");
        Assert.Equal("Invoke-BashEcho $(Invoke-BashArith '2 ** 3')", result);
    }

    [Fact]
    public void Transpile_ArithSub_Modulo()
    {
        var result = PsEmitter.Transpile("echo $((10 % 3))");
        Assert.Equal("Invoke-BashEcho $(Invoke-BashArith '10 % 3')", result);
    }

    [Fact]
    public void Transpile_ArithSub_NestedInString()
    {
        var result = PsEmitter.Transpile("echo \"result is $((x + 1))\"");
        Assert.Equal("Invoke-BashEcho \"result is $(Invoke-BashArith 'x + 1')\"", result);
    }

    [Theory]
    [InlineData("echo $(($0 + 1))", "$global:BashPositional0")]
    [InlineData("echo $(($9 + 1))", "$global:BashPositional[8]")]
    [InlineData("echo $(($# + $? + $$ + $!))", "$global:BashBgLastPid")]
    public void Transpile_ArithSub_SpecialParameters_AreSplicedIntoUnifiedEvaluator(string source, string expected)
    {
        string result = PsEmitter.Transpile(source);

        Assert.Contains("Invoke-BashArith", result);
        Assert.Contains(expected, result);
        Assert.DoesNotContain("$env:#", result);
        Assert.DoesNotContain("$env:?", result);
    }

    [Fact]
    public void Transpile_ArithCommand_WithPositional_UsesSameEvaluatorHandoff()
    {
        string result = PsEmitter.Transpile("(( $1 ** 2 ))");

        Assert.Contains("Invoke-BashArith", result);
        Assert.Contains("$global:BashPositional[0]", result);
        Assert.Contains("$global:LASTEXITCODE", result);
    }

    [Theory]
    [InlineData("echo $(($10 + 1))", "$global:BashPositional[0]", "'0 + 1'")]
    [InlineData("echo $((${10} + 1))", "$global:BashPositional[9]", "' + 1'")]
    [InlineData("echo $((${1}+1))", "$global:BashPositional[0]", "'+1'")]
    [InlineData("(( ${1} > 0 ))", "$global:BashPositional[0]", "' > 0'")]
    [InlineData("for ((i=${1}; i<${10}; i++)); do echo $i; done", "$global:BashPositional[9]", "'i<'")]
    public void Transpile_ArithmeticParameters_PreserveExpansionBoundariesAcrossContexts(
        string source, string expectedReference, string expectedLiteral)
    {
        string result = PsEmitter.Transpile(source);

        Assert.Contains("Invoke-BashArith", result);
        Assert.Contains(expectedReference, result);
        Assert.Contains(expectedLiteral, result);
    }

    [Fact]
    public void Transpile_BracedNamedArithmeticParameter_RemainsEvaluatorResolvedSource()
        => Assert.Equal("Invoke-BashEcho $(Invoke-BashArith '${x} + 1')",
            PsEmitter.Transpile("echo $((${x} + 1))"));

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

    [Theory]
    [InlineData("[[:alpha:]]", "[A-Za-z]")]
    [InlineData("[[:digit:][:upper:]]", "[0-9A-Z]")]
    [InlineData("[![:xdigit:]]", "[!A-Fa-f0-9]")]
    [InlineData("[[:blank:]]", "[` `t]")]
    [InlineData("[[:space:]]", "[` `t`r`n`f`v]")]
    public void Transpile_PosixGlobCharClass_NormalizesForPowerShellWildcard(string pattern, string expected)
    {
        var result = PsEmitter.Emit(Assert.IsType<Command.Simple>(BashParser.Parse($"echo {pattern}")));

        Assert.Contains(expected, result);
        Assert.DoesNotContain("[:", result);
    }

    [Theory]
    [InlineData("[[:unknown:]]")]
    [InlineData("[[.x[:alpha:].]]")]
    [InlineData("[[=x[:alpha:]=]]")]
    public void Transpile_UnsupportedNestedGlobClass_PreservesPattern(string pattern)
    {
        var result = PsEmitter.Emit(Assert.IsType<Command.Simple>(BashParser.Parse($"echo {pattern}")));

        Assert.Contains(pattern, result);
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
    public void Transpile_ForInGlobCharClass_EmitsResolveBashGlob()
    {
        var result = PsEmitter.Transpile("for f in [abc]*.txt; do cat $f; done");
        Assert.Equal("$__psbash_iter = 0; foreach ($f in (Resolve-BashGlob [abc]*.txt)) { if (++$__psbash_iter -gt ($env:PSBASH_MAX_ITERATIONS ?? 100000)) { throw \"ps-bash: loop iteration limit exceeded ($(($env:PSBASH_MAX_ITERATIONS ?? 100000)))\" }; Invoke-BashCat $f }", result);
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
    public void Transpile_AdjacentBraceTuples_CrossMultiplies()
    {
        // bash {a,b}{1,2} -> Cartesian product, not literal second-brace text.
        var result = PsEmitter.Transpile("echo a{b,c}{1,2}");
        Assert.Equal("Invoke-BashEcho @('ab1','ab2','ac1','ac2')", result);
    }

    [Fact]
    public void Transpile_AdjacentBraceTuplesWithMidLiteral_CrossMultiplies()
    {
        var result = PsEmitter.Transpile("echo pre{a,b}post{1,2}");
        Assert.Equal("Invoke-BashEcho @('preapost1','preapost2','prebpost1','prebpost2')", result);
    }

    [Fact]
    public void Transpile_AdjacentBraceRanges_CrossMultiplies()
    {
        var result = PsEmitter.Transpile("echo {1..2}{3..4}");
        Assert.Equal("Invoke-BashEcho @('13','14','23','24')", result);
    }

    [Fact]
    public void Transpile_CommandSubContainingCase_DoesNotAbortParse()
    {
        // $(case $y in a) echo MATCH;; esac) — the pattern ')' must not close the
        // command-sub early. Previously this threw a ParseException and aborted the
        // whole transpile.
        var ex = Record.Exception(() => PsEmitter.Transpile("echo $(case $y in a) echo MATCH;; esac)"));
        Assert.Null(ex);

        var result = PsEmitter.Transpile("echo $(case $y in a) echo MATCH;; esac)");
        Assert.Contains("switch", result);   // case -> switch
        Assert.Contains("MATCH", result);
    }

    [Fact]
    public void Transpile_CommandSubCaseWithSubshellBody_DoesNotAbortParse()
    {
        // A subshell ( ... ) inside a case arm body: its ')' is a real close (deeper than
        // the case depth), distinct from the pattern terminator ')'.
        var ex = Record.Exception(() => PsEmitter.Transpile("x=$(case $y in a) (echo hi);; esac)"));
        Assert.Null(ex);
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
        // Elements route through EmitAssignmentValue now (bare literals → double-quoted),
        // so a "$x" element expands and $'a\'b' stays balanced (var-expansion fix).
        Assert.Equal("$arr = @(\"a\",\"b\",\"c\")", result);
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
        Assert.Equal("$map['key'] = \"val\"", result);
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

    // A body line that IS the PowerShell here-string terminator (`"@`) would close
    // the @"..."@ string early — bash prints it literally, ps-bash used to throw
    // "missing terminator". The emitter must fall back to an ordinary double-quoted
    // string (same value, expansion preserved) and never emit @".
    [Fact]
    public void Transpile_ExpandingHeredocBodyHasTerminatorLine_FallsBackToQuotedString()
    {
        var result = PsEmitter.Transpile("cat <<EOF\nbefore\n\"@\nafter\nEOF");

        Assert.DoesNotContain("@\"", result);
        Assert.Equal("\"before\n`\"@\nafter\n\" | Emit-BashLine | Invoke-BashCat", result);
    }

    // The literal-heredoc analogue: a body line of `'@` would close @'...'@ early.
    [Fact]
    public void Transpile_LiteralHeredocBodyHasTerminatorLine_FallsBackToSingleQuotedString()
    {
        var result = PsEmitter.Transpile("cat <<'EOF'\nbefore\n'@\nafter\nEOF");

        Assert.DoesNotContain("@'", result);
        Assert.Equal("'before\n''@\nafter\n' | Emit-BashLine | Invoke-BashCat", result);
    }

    // End-to-end: a command after a heredoc whose body has an unbalanced quote
    // must still be parsed. Before the lexer skipped bodies, the lone " folded the
    // delimiter line and `echo DONE` into one token, so the post-heredoc command
    // vanished from the transpiled output.
    [Fact]
    public void Transpile_CommandAfterHeredocWithUnbalancedQuoteInBody_NotSwallowed()
    {
        var result = PsEmitter.Transpile("cat <<EOF\nval=\"oops\nmore\nEOF\necho DONE");

        Assert.Contains("Invoke-BashCat", result);
        Assert.Contains("DONE", result);
    }

    // Stacked heredocs end-to-end: bash connects only the LAST heredoc to stdin (so
    // `beta`, not `alpha`, is the body), and the command after both bodies survives.
    // The first body must still be consumed by the scanner so its lines aren't
    // mistaken for commands.
    [Fact]
    public void Transpile_StackedHeredocs_LastBodyUsedAndTrailingCommandSurvives()
    {
        var result = PsEmitter.Transpile("cat <<A <<B\nalpha\nA\nbeta\nB\necho DONE");

        Assert.Contains("beta", result);     // last heredoc → stdin
        Assert.DoesNotContain("alpha", result); // first heredoc opened then discarded
        Assert.Contains("DONE", result);     // trailing command not swallowed
    }

    [Fact]
    public void Transpile_BackslashDelimiter_EmitsLiteralHereString()
    {
        // `<<\EOF` disables expansion exactly like `<<'EOF'` — $NAME must stay literal,
        // and the literal single-quote here-string form (@'...'@) must be used.
        var result = PsEmitter.Transpile("cat <<\\EOF\nhello $NAME\nEOF");

        Assert.Equal("@'\nhello $NAME\n\n'@ | Emit-BashLine | Invoke-BashCat", result);
    }

    [Fact]
    public void Transpile_MidWordBackslashDelimiter_EmitsLiteralHereString()
    {
        // A backslash ANYWHERE in the delimiter (here `E\OF`) disables expansion; the
        // backslash is stripped so the terminator line `EOF` still matches.
        var result = PsEmitter.Transpile("cat <<E\\OF\nhello $NAME\nEOF");

        Assert.Equal("@'\nhello $NAME\n\n'@ | Emit-BashLine | Invoke-BashCat", result);
    }

    [Fact]
    public void Transpile_StripTabsBackslashDelimiter_EmitsLiteralHereString()
    {
        // `<<-\EOF` combines tab-stripping with backslash-quoting (non-expanding).
        var result = PsEmitter.Transpile("cat <<-\\EOF\nhello $NAME\nEOF");

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
            "Invoke-BashEcho \"$((@(Invoke-BashEcho \"hi there\" | ForEach-Object { Get-BashText $_ }) -join \"`n\") -replace '(\\r?\\n)+$','')\"",
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
            "else { @($env:FOO -split '\\s+' | Where-Object { $_ -ne '' }) }); " +
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

    // ${str//foo/bar} -> replace all (literal find, escaped)
    [Fact]
    public void Transpile_ParamReplaceAll_EmitsEscapedLiteralReplace()
    {
        var result = PsEmitter.Transpile("echo ${str//foo/bar}");
        Assert.Equal("Invoke-BashEcho (([regex][regex]::Escape('foo')).Replace($env:str, 'bar'))", result);
    }

    // ${p//./_} -> the dot must be a LITERAL, not a regex "any char". Regression for
    // the regex-injection bug where raw `-replace '.','_'` matched every character.
    [Fact]
    public void Transpile_ParamReplaceAllRegexMetachar_EscapesFind()
    {
        var result = PsEmitter.Transpile("echo ${p//./_}");
        Assert.Equal("Invoke-BashEcho (([regex][regex]::Escape('.')).Replace($env:p, '_'))", result);
    }

    // ${name:0:2} -> substring
    [Fact]
    public void Transpile_ParamSlice_EmitsSubstring()
    {
        var result = PsEmitter.Transpile("echo ${name:0:2}");
        Assert.Contains("$env:name.Substring([Math]::Min(0, $env:name.Length)", result);
    }

    [Fact]
    public void Transpile_ParamSlice_NegativeOffset_CountsFromEnd()
    {
        // ${s: -2} (space disambiguates from the :-default operator) = last 2
        // chars; offset maps to Length - 2. Regression: was ignored, returning $s.
        var result = PsEmitter.Transpile("echo ${s: -2}");
        Assert.Contains("Substring([Math]::Max(0, $env:s.Length - 2))", result);
    }

    [Fact]
    public void Transpile_ParamRemoveShortestSuffix_KeepsGreedyPrefix()
    {
        // ${p%.*} removes the SHORTEST suffix matching `.*` -> keep `foo.bar`,
        // not `foo`. Emitted as `^(.*)\..*$` -> `$1` (greedy prefix capture).
        var result = PsEmitter.Transpile("echo ${p%.*}");
        Assert.Contains("-replace '^(.*)", result);
        Assert.Contains("$1", result);
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

        // All-mapped, terminal-bound, literal-arg pipeline → fused lane with a
        // phase-2b streaming -Stages list + phase-2a scriptblock Fallback.
        Assert.Equal(
            "Invoke-BashFusedPipeline -Stages @(@('cat', 'file'), @('nl', '-ba')) "
                + "-Fallback { Invoke-BashCat file | Invoke-BashNl -ba }",
            result);
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
        // RC3: the default word is decomposed, but a PURE LITERAL is emitted single-quoted
        // (EmitBracedArgWordValue) — a nested double-quoted string inside "$( … )" mis-parses
        // when empty or quote-bearing, so literals must stay single-quoted here.
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
        // RC3: alternative word decomposed; a pure literal stays single-quoted (see above).
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
        Assert.Contains("@(\"one\",\"two\",\"three\")", result);
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
    public void Transpile_AssignmentFlattenVarFollowedByColon_EmitsBracedVar()
    {
        // Regression: EmitAssignmentValue's multi-part path (SingleQuoted + SimpleVarSub +
        // Literal, e.g. 'a'$x:b) goes through FlattenPartsToDoubleQuotedString, a SEPARATE
        // code path from AppendDoubleQuotedInner (which the "$x: world" test above already
        // covers). The flatten path was missing the same drive-reference guard: unbraced
        // "a$env:x:b" is a PowerShell drive-qualified path ($env:x:b), not string concatenation.
        var result = PsEmitter.Transpile("x=hello; y='a'$x:b");
        Assert.Contains("${env:x}:", result);
        Assert.DoesNotContain("$env:x:b", result);
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
        Assert.Equal("if ((([long](1) -eq [long](1)) -and ([long](2) -eq [long](2)))) { Invoke-BashEcho yes }", result);
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
        Assert.Contains("@($env:x -split '\\s+' | Where-Object { $_ -ne '' })", result);
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

    [Fact]
    public void CompactCommandChain_GitAddCommitPush_ReturnsStableRouteAndSummary()
    {
        var chain = MakeAndOrChain("&&", "&&",
            MakeSimple("git", "add", "."),
            MakeSimple("git", "commit", "-m", "message"),
            MakeSimple("git", "push"));

        Assert.True(CompactCommandChain.TryClassify(chain, out var result));
        Assert.NotNull(result);
        Assert.Equal("git.stage-commit-push.v1", result.RouteKey);
        Assert.Equal("Stage changes, create a commit, and push it.", result.ActionSummary);
    }

    [Theory]
    [InlineData("||", "&&")]
    [InlineData("&&", "||")]
    [InlineData("||", "||")]
    public void CompactCommandChain_OrOrOrMixedOperators_IsRejected(string first, string second)
    {
        var chain = MakeAndOrChain(first, second,
            MakeSimple("git", "add", "."), MakeSimple("git", "commit", "-m", "x"), MakeSimple("git", "push"));

        Assert.False(CompactCommandChain.TryClassify(chain, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void CompactCommandChain_PipelineNode_IsRejected()
    {
        var pipeline = new Command.Pipeline(
            ImmutableArray.Create<Command>(MakeSimple("git", "add", "."), MakeSimple("cat")),
            ImmutableArray.Create("|"), false);
        var chain = MakeAndOrChain("&&", "&&", pipeline,
            MakeSimple("git", "commit", "-m", "x"), MakeSimple("git", "push"));

        Assert.False(CompactCommandChain.TryClassify(chain, out _));
    }

    [Fact]
    public void CompactCommandChain_CompoundNode_IsRejected()
    {
        var subshell = new Command.Subshell(MakeSimple("git", "add", "."), ImmutableArray<Redirect>.Empty);
        var chain = MakeAndOrChain("&&", "&&", subshell,
            MakeSimple("git", "commit", "-m", "x"), MakeSimple("git", "push"));

        Assert.False(CompactCommandChain.TryClassify(chain, out _));
    }

    [Fact]
    public void CompactCommandChain_Redirect_IsRejected()
    {
        var add = MakeSimple("git", "add", ".") with
        {
            Redirects = ImmutableArray.Create(new Redirect(">", 1, MakeWord("log")))
        };
        var chain = MakeAndOrChain("&&", "&&", add,
            MakeSimple("git", "commit", "-m", "x"), MakeSimple("git", "push"));

        Assert.False(CompactCommandChain.TryClassify(chain, out _));
    }

    [Fact]
    public void CompactCommandChain_EnvironmentPrefix_IsRejected()
    {
        var add = MakeSimple("git", "add", ".") with
        {
            EnvPairs = ImmutableArray.Create(new EnvPair("MODE", MakeWord("safe")))
        };
        var chain = MakeAndOrChain("&&", "&&", add,
            MakeSimple("git", "commit", "-m", "x"), MakeSimple("git", "push"));

        Assert.False(CompactCommandChain.TryClassify(chain, out _));
    }

    [Fact]
    public void CompactCommandChain_HereDocument_IsRejected()
    {
        var add = MakeSimple("git", "add", ".") with
        {
            HereDocs = ImmutableArray.Create(new HereDoc("input", false, false))
        };
        var chain = MakeAndOrChain("&&", "&&", add,
            MakeSimple("git", "commit", "-m", "x"), MakeSimple("git", "push"));

        Assert.False(CompactCommandChain.TryClassify(chain, out _));
    }

    [Fact]
    public void CompactCommandChain_ExtraCommand_IsRejected()
    {
        var chain = new Command.AndOrList(
            ImmutableArray.Create<Command>(MakeSimple("git", "add", "."),
                MakeSimple("git", "commit", "-m", "x"), MakeSimple("git", "push"), MakeSimple("echo", "done")),
            ImmutableArray.Create("&&", "&&", "&&"));

        Assert.False(CompactCommandChain.TryClassify(chain, out _));
    }

    [Fact]
    public void CompactCommandChain_DynamicCommandWord_IsRejected()
    {
        var dynamicGit = new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.SimpleVarSub("git")));
        var add = new Command.Simple(
            ImmutableArray.Create(dynamicGit, MakeWord("add"), MakeWord(".")),
            ImmutableArray<EnvPair>.Empty, ImmutableArray<Redirect>.Empty);
        var chain = MakeAndOrChain("&&", "&&", add,
            MakeSimple("git", "commit", "-m", "x"), MakeSimple("git", "push"));

        Assert.False(CompactCommandChain.TryClassify(chain, out _));
    }

    [Fact]
    public void CompactCommandChain_DynamicArgument_IsRejected()
    {
        var dynamicArg = new CompoundWord(ImmutableArray.Create<WordPart>(new WordPart.SimpleVarSub("files")));
        var add = new Command.Simple(
            ImmutableArray.Create(MakeWord("git"), MakeWord("add"), dynamicArg),
            ImmutableArray<EnvPair>.Empty, ImmutableArray<Redirect>.Empty);
        var chain = MakeAndOrChain("&&", "&&", add,
            MakeSimple("git", "commit", "-m", "x"), MakeSimple("git", "push"));

        Assert.False(CompactCommandChain.TryClassify(chain, out _));
    }

    [Theory]
    [InlineData("status", "commit", "push")]
    [InlineData("add", "status", "push")]
    [InlineData("add", "commit", "fetch")]
    public void CompactCommandChain_DifferentGitSequence_IsRejected(string first, string second, string third)
    {
        var chain = MakeAndOrChain("&&", "&&",
            MakeSimple("git", first, "."), MakeSimple("git", second, "-m", "x"), MakeSimple("git", third));

        Assert.False(CompactCommandChain.TryClassify(chain, out _));
    }

    [Fact]
    public void CompactCommandChain_MissingRequiredAddOrCommitOperand_IsRejected()
    {
        var noAddOperand = MakeAndOrChain("&&", "&&",
            MakeSimple("git", "add"), MakeSimple("git", "commit", "-m", "x"), MakeSimple("git", "push"));
        var noCommitOperand = MakeAndOrChain("&&", "&&",
            MakeSimple("git", "add", "."), MakeSimple("git", "commit"), MakeSimple("git", "push"));

        Assert.False(CompactCommandChain.TryClassify(noAddOperand, out _));
        Assert.False(CompactCommandChain.TryClassify(noCommitOperand, out _));
    }

    private static Command.Simple MakeSimple(params string[] words) =>
        new(words.Select(MakeWord).ToImmutableArray(), ImmutableArray<EnvPair>.Empty, ImmutableArray<Redirect>.Empty);

    private static Command.AndOrList MakeAndOrChain(string firstOp, string secondOp, params Command[] commands) =>
        new(commands.ToImmutableArray(), ImmutableArray.Create(firstOp, secondOp));

    private static CompoundWord MakeWord(string value) =>
        new(ImmutableArray.Create<WordPart>(new WordPart.Literal(value)));
}
