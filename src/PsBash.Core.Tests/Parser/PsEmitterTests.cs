using System.Collections.Immutable;
using Xunit;
using PsBash.Core.Parser;
using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Tests.Parser;

public class PsEmitterTests
{
    /// <summary>
    /// The subshell wrapper's <c>finally</c>. Referenced through <see cref="PsBuild"/>
    /// rather than re-typed so a change to the restore fragment cannot leave five
    /// assertions asserting a stale copy of it.
    /// </summary>
    private const string SubshellPop = "finally { " + PsBuild.PopLocationRestoringProcessCwd + " }";

    [Fact]
    public void Transpile_SubshellExit_RestoresProcessWorkingDirectoryNotJustPsLocation()
    {
        // REGRESSION. A bash working directory is BOTH the PowerShell location and
        // [System.Environment]::CurrentDirectory; `cd` writes both. The subshell wrapper
        // popped only the PowerShell half, so after `(cd sub)` the PROCESS cwd was still
        // inside `sub` — and the fused streaming lane, which resolves relative operands
        // against CurrentDirectory, then read `sub/data.txt` at exit 0 while the unfused
        // pipeline read the outer one. Runtime proof:
        // LineStreamCatFileParityTests.Streamed_CatRelative_AfterBashSubshellCd_*.
        var result = PsEmitter.Transpile("(cd sub)")!;

        Assert.Contains("[System.Environment]::CurrentDirectory = $__psbash_subshell_pop.ProviderPath",
            result);
        // The restore must be in the FINALLY, so it also runs when the body throws or
        // takes the scoped-`exit` return.
        Assert.EndsWith(SubshellPop, result);
        Assert.Contains("$global:LASTEXITCODE = 7; return",
            PsEmitter.Transpile("(cd sub; exit 7)")!);
        Assert.EndsWith(SubshellPop + " }", PsEmitter.Transpile("(cd sub; exit 7)")!);
    }

    [Fact]
    public void Transpile_ColonBuiltin_ExpandsArgumentsAndSucceeds_NoCommandLookup()
    {
        var result = PsEmitter.Transpile(": ${x:=5}")!;
        Assert.DoesNotContain("Invoke-Expression", result);
        Assert.Contains("[void](", result);
        Assert.Contains("$global:LASTEXITCODE = 0", result);
        Assert.DoesNotMatch(@"(^|;)\s*:\s", result);
    }

    [Fact]
    public void Transpile_ColonAsWhileCondition_IsTrueConstant() =>
        Assert.Contains("while ($true)", PsEmitter.Transpile("while :; do echo a; break; done")!);

    [Fact]
    public void Transpile_TrueWithArguments_IgnoresThemWithStatusZero() =>
        Assert.Contains("$global:LASTEXITCODE = 0", PsEmitter.Transpile("true --help")!);

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

        Assert.Equal("Invoke-BashLs '-la' /tmp", result);
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
                + "-Fallback { Invoke-BashCat file | Invoke-BashHead '-n' 5 | Invoke-BashSort }",
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
    public void Transpile_ArithCommandSubOperand_RunsCommandBeforeEvaluating()
    {
        // Regression: the typed arithmetic lexer had no `$(` branch, so
        // `$(( $(date +%s) + 60 ))` failed the whole transpile with "invalid
        // arithmetic parameter" — the last real gap in the real-world .sh corpus
        // sweep. bash expands the substitution to text and THEN evaluates, so the
        // command must be spliced in as a value fragment ahead of Invoke-BashArith
        // rather than left in the string the evaluator parses.
        var result = PsEmitter.Transpile("echo $(( $(echo 5) + 60 ))");
        Assert.Equal(
            "Invoke-BashEcho $(Invoke-BashArith ('' + "
            + "$((@(Invoke-BashEcho 5 | ConvertTo-BashCapture) -join [string][char]10).Trim())"
            + " + ' + 60'))",
            result);
        // The evaluator must never receive the un-expanded substitution text.
        Assert.DoesNotContain("Invoke-BashArith '$(", result);
    }

    [Fact]
    public void Transpile_ArithBacktickCommandSubOperand_RunsCommandBeforeEvaluating()
    {
        var result = PsEmitter.Transpile("echo $(( `echo 7` - 1 ))");
        Assert.Contains("Invoke-BashEcho 7 | ConvertTo-BashCapture", result);
        Assert.Contains("+ ' - 1'", result);
    }

    [Fact]
    public void Transpile_ArithNestedArithSub_StaysOnTheLiteralEvaluatorPath()
    {
        // `$((…))` inside arithmetic is a NESTED arithmetic expansion, not a
        // subshell command substitution: dropping the `$` leaves `((…))`, which the
        // evaluator's existing paren grouping already handles. It must NOT be
        // mistaken for a command sub and shelled out.
        var result = PsEmitter.Transpile("echo $(( $((2+3)) * 4 ))");
        Assert.Equal("Invoke-BashEcho $(Invoke-BashArith '$((2+3)) * 4')", result);
    }

    [Fact]
    public void Transpile_ArithCommandSubWithParenInString_DoesNotEndAtTheQuotedParen()
    {
        // The span scan is quote-aware: a `)` inside a quoted argument must not be
        // taken as the substitution's closing paren (which would tear the command
        // in half and emit unparseable PowerShell).
        var result = PsEmitter.Transpile("echo $(( $(grep -c \"a)b\" f.txt) + 1 ))");
        Assert.Contains("Invoke-BashGrep '-c' \"a)b\" f.txt", result);
        Assert.Contains("+ ' + 1'", result);
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

        Assert.Equal("cmd 2>&1 | ForEach-Object { Get-BashText $_ } | other", result);
    }

    [Fact]
    public void Transpile_AmpGreat_RedirectsBothStreams()
    {
        // `&>file` = redirect stdout AND stderr → PowerShell `>file 2>&1`.
        // Previously `&` lexed as background, dropping the stderr redirect.
        var result = PsEmitter.Transpile("cmd &> out.log");

        Assert.Equal("cmd 2>&1 | Invoke-BashRedirect -Path out.log", result);
    }

    [Fact]
    public void Transpile_AmpDGreat_AppendsBothStreams()
    {
        var result = PsEmitter.Transpile("cmd &>> out.log");

        Assert.Equal("cmd 2>&1 | Invoke-BashRedirect -Path out.log -Append", result);
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
            "Invoke-BashGrep '-q' pattern file; $global:LASTEXITCODE = if ($global:LASTEXITCODE -eq 0) { 1 } else { 0 }",
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
        Assert.Equal("Invoke-BashFind . '-name' a '-o' '-name' b", result);
    }

    [Fact]
    public void Transpile_FindAndOperator_QuotesDashAToAvoidArgumentsParamCollision()
    {
        // find's `-a` (AND) prefix-matches the cmdlet's own -Arguments parameter,
        // which would bind it as named and swallow the next token. Quote it too.
        var result = PsEmitter.Transpile("find . -type f -a -name x");
        Assert.Equal("Invoke-BashFind . '-type' f '-a' '-name' x", result);
    }

    // find is on OrderedArgCommands: the whole expression (operators, grouping, the -exec argv and its
    // terminator) reaches Invoke-BashFind verbatim and in order; no per-flag force-quote set is involved.
    [Theory]
    [InlineData("find . -name a -o -name b", "Invoke-BashFind . '-name' a '-o' '-name' b")]
    [InlineData("find . \\( -name a -o -name b \\) -print", "Invoke-BashFind . `( '-name' a '-o' '-name' b `) '-print'")]
    [InlineData("find . ! -type d -a -name x", "Invoke-BashFind . ! '-type' d '-a' '-name' x")]
    [InlineData("find -H . -maxdepth 2 -print0", "Invoke-BashFind '-H' . '-maxdepth' 2 '-print0'")]
    [InlineData("find . -name '*.c' -exec grep -il -e foo {} +", "Invoke-BashFind . '-name' '*.c' '-exec' grep '-il' '-e' foo \"{}\" +")]
    public void Transpile_FindExpression_ReachesTheCmdletVerbatimAndInOrder(string bash, string expected)
    {
        Assert.Equal(expected, PsEmitter.Transpile(bash));
    }

    [Fact]
    public void Transpile_BangAfterCommandWord_KeptAsLiteralArgument()
    {
        // bash `!` is the negation reserved word only at pipeline start. A `!`
        // after the command word is a literal operand (find . ! -name x). The
        // parser used to break the command at `!`, dropping `! -name x`.
        var result = PsEmitter.Transpile("find . ! -name x");
        Assert.Equal("Invoke-BashFind . ! '-name' x", result);
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
        // Guard the narrow claim: UN-NESTED, only a literal that would need an escape
        // switches to single quotes; the ordinary shape keeps its readable form.
        Assert.Contains("\"plain text\"", PsEmitter.Transpile("echo \"plain text\""));
    }

    [Theory]
    // `$(<file)` is bash's read-a-file shorthand: an input redirect with NO command,
    // whose value is the file's contents. The usual `Get-Content f | <cmd>` shape left
    // a dangling `|` when <cmd> was empty ("An empty pipe element is not allowed"),
    // failing the parse of the entire file. With no command, the redirect IS the
    // command. Oracle-verified: `x=$(<f)` equals `x=$(cat f)`.
    [InlineData("x=$(<f.txt)", "Get-Content f.txt |")]
    [InlineData("x=$(< f.txt)", "Get-Content f.txt |")]
    public void Transpile_ReadFileShorthand_EmitsNoDanglingPipe(
        string bash, string notExpected)
    {
        var result = PsEmitter.Transpile(bash);

        Assert.Contains("Get-Content f.txt", result);
        Assert.DoesNotContain("|  |", result);
        Assert.DoesNotContain(notExpected + " |", result);
    }

    [Fact]
    public void Transpile_InputRedirectWithCommand_StillPipes()
    {
        // Guard the narrow claim: a redirect that DOES have a command still pipes. The file is the
        // command's stdin BYTE STREAM, so a mapped consumer is fed exact records by Invoke-BashCat.
        Assert.Contains("Invoke-BashCat f.txt | Invoke-BashWc '-l'",
            PsEmitter.Transpile("wc -l < f.txt"));
    }

    [Fact]
    public void Transpile_NestedEmptyDoubleQuotedString_EmitsSingleQuotedEmpty()
    {
        // `X="$(cmd || echo "")"` — an inner `""` used to close the OUTER string ("The string
        // is missing the terminator", stop-hook.sh). The value is now a bare $( ) with no
        // outer string at all, so the inner empty literal cannot terminate anything.
        var result = PsEmitter.Transpile("X=\"$(echo \"\")\"");

        Assert.StartsWith("$env:X = $((@(Invoke-BashEcho ", result);
        Assert.DoesNotContain("= \"$(", result);
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
        var result = PsEmitter.Transpile("PATH=~/bin:~/x")!;
        var homeCount = System.Text.RegularExpressions.Regex.Matches(result, @"\$HOME").Count;
        Assert.True(homeCount >= 2, $"expected >= 2 $HOME, got {homeCount}: {result}");
    }

    [Theory]
    // SplitOnUnquotedColon (which finds the PATH-style `:` boundaries of an
    // assignment value) scanned a double-quoted region by looking for the next `"`.
    // A nested command substitution has its OWN quotes, so the region ended at the
    // INNER quote and the colon after it looked unquoted — the value was split
    // there, tearing `:b f)` out of the command and leaving a mangled pattern.
    // The value is a bare $( ) (no outer string), so inner literals keep their plain
    // double-quoted form. Either way the colon must survive un-split.
    [InlineData("X=\"$(grep \"a:b\" f)\"", "Invoke-BashGrep \"a:b\" f")]
    [InlineData("X=\"$(echo \"p\" | grep \"a:b\")\"", "Invoke-BashGrep \"a:b\"")]
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

        // The `:-` operator form emits the empty-or-unset ternary, not a bare
        // `${v}` bracing.
        Assert.Contains("$env:v ? $env:v :", result);
    }

    [Fact]
    public void Transpile_AssignmentPathColons_StillSplitForTildeExpansion()
    {
        // Guard the narrow claim: a genuinely unquoted PATH-style colon still
        // separates segments, so each `~` expands.
        var result = PsEmitter.Transpile("PATH=~/bin:~/x")!;
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
        Assert.Equal("cmd 2>&1 | Invoke-BashRedirect -Path out.txt", result);
    }

    [Fact]
    public void Transpile_GreatAndUserFdTarget_DegradesToComment()
    {
        // This previously asserted `cmd 1>&3`, on the assumption that a numeric
        // target is a genuine fd-merge worth keeping. But `1>&3` is NOT valid
        // PowerShell — its `>&` operator accepts only `n>&1` with n != 1, so the
        // emitted file failed to parse ENTIRELY ("Missing file specification after
        // redirection operator"). Found in dotnet-install.sh and the Visual Studio
        // prereq scripts, which use the `exec 3>&1` + `>&3` logging idiom.
        //
        // bash's user fds cannot be modeled (they are opened by `exec`), so this
        // degrades to a visible inline no-op, as `<&-` already does.
        var result = PsEmitter.Transpile("cmd >&3");
        Assert.Equal("cmd <# ps-bash: fd merge 1>&3 has no PowerShell equivalent #>", result);
    }

    [Fact]
    public void Transpile_GreatAndUserFdFromStderr_DegradesToComment()
    {
        var result = PsEmitter.Transpile("cmd 2>&3");
        Assert.Equal("cmd <# ps-bash: fd merge 2>&3 has no PowerShell equivalent #>", result);
    }

    [Fact]
    public void Transpile_SupportedFdMergeIntoStdout_Kept()
    {
        // `n>&1` (n != 1) IS valid PowerShell and must keep the real merge.
        Assert.Equal("cmd 2>&1", PsEmitter.Transpile("cmd 2>&1"));
        Assert.Equal("cmd 3>&1", PsEmitter.Transpile("cmd 3>&1"));
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

        Assert.Equal("Invoke-BashGrep '-q' pattern file", result);
    }

    [Fact]
    public void Transpile_DoubleNegatedPipeline_IsIdentity()
    {
        // `! ! cmd1 | cmd2` — double negation applies to the whole pipeline =
        // identity. No exit-code negation suffix; pipeline runs as-is. The
        // non-mapped pipe target carries the shared Get-BashText conversion.
        var result = PsEmitter.Transpile("! ! cmd1 | cmd2");

        Assert.Equal("cmd1 | ForEach-Object { Get-BashText $_ } | cmd2", result);
    }

    [Fact]
    public void Transpile_TripleNegation_NegatesOnce()
    {
        var result = PsEmitter.Transpile("! ! ! grep -q pattern file");

        Assert.Equal(
            "Invoke-BashGrep '-q' pattern file; $global:LASTEXITCODE = if ($global:LASTEXITCODE -eq 0) { 1 } else { 0 }",
            result);
    }

    [Fact]
    public void Transpile_NegatedPipeline_EmitsExitCodeNegation()
    {
        var result = PsEmitter.Transpile("! cmd1 | cmd2");

        // Negation checks $global:LASTEXITCODE (bash exit code) not PowerShell's $?.
        Assert.Equal(
            "cmd1 | ForEach-Object { Get-BashText $_ } | cmd2; $global:LASTEXITCODE = if ($global:LASTEXITCODE -eq 0) { 1 } else { 0 }",
            result);
    }

    [Fact]
    public void Transpile_EchoPipeWcL_EmitsMeasureObject()
    {
        var result = PsEmitter.Transpile("echo hello | wc -l");

        Assert.Equal("Invoke-BashEcho hello | Invoke-BashWc '-l'", result);
    }

    [Fact]
    public void Transpile_TeeDoubleDash_QuotesDoubleDashSoBinderDoesNotEatIt()
    {
        // The PS binder swallows a bare `--`, so tee could not tell `-zz` (after `--`) from an option.
        var result = PsEmitter.Transpile("echo x | tee -- -zz");

        Assert.Matches(@"Invoke-BashTee ['""]--['""] ", result);
    }

    // ── OrderedArgCommands: every dash-leading literal reaches Arguments as a quoted string ──

    [Fact]
    public void OrderedArgCommands_IsExactlyTheMigratedSet()
    {
        // Batch 1 of the shared ordered parser. Adding a command here also means adding it to
        // CommonParameterCollisionGuardTests.EmitterForceQuoted (Cmdlets.Tests) — that map is
        // how the guard knows the emitter, not a decoy, protects the colliding letters.
        Assert.Equal(new[] { "awk", "base64", "bash", "cat", "column", "comm", "command", "cp", "cut", "declare", "diff", "du", "echo", "env", "expand", "file", "find", "fold", "grep", "gzip", "head", "join", "jq", "ln", "ls", "md5sum", "mkdir", "mv", "nl", "paste", "printf", "rg", "rm", "rmdir", "sed", "sha1sum", "sha256sum", "sort", "split", "stat", "strings", "tac", "tail", "tar", "tee", "test", "time", "touch", "tree", "type", "unexpand", "uniq", "wc", "xargs" }, PsEmitter.OrderedArgCommands.OrderBy(x => x).ToArray());
    }

    // `bash` is on OrderedArgCommands: the script's own args (`bash s.sh -v -e -c x`) and the
    // args after `-c CMD NAME` are positional parameters in bash, so none may become a
    // PowerShell parameter token (`-c` is a declared parameter of Invoke-BashBash).
    [Theory]
    [InlineData("awk -v a=1 -v b=2 'BEGIN{print a+b}'", "Invoke-BashAwk '-v' a=1 '-v' b=2 'BEGIN{print a+b}'")]
    [InlineData("awk -F: -va=1 '{print $1}' f", "Invoke-BashAwk '-F:' '-va=1' '{print $1}' f")]
    [InlineData("awk -- '{print}' f", "Invoke-BashAwk '--' '{print}' f")]
    public void Transpile_AwkFlags_AreSingleQuotedSoRepeatedDashVReachesTheCmdlet(string bash, string expected)
    {
        Assert.Equal(expected, PsEmitter.Transpile(bash));
    }

    // grep is on OrderedArgCommands: -e/-E/-i/-ve bundles, -A/-C/-NUM, -f FILE and `--` all reach
    // Arguments verbatim and in order (repeated -e used to need the psm1 proxy to rescue it).
    [Theory]
    [InlineData("grep -e a -e b f", "Invoke-BashGrep '-e' a '-e' b f")]
    [InlineData("grep -ie A f", "Invoke-BashGrep '-ie' A f")]
    [InlineData("grep -E -v 'a|b' f", "Invoke-BashGrep '-E' '-v' 'a|b' f")]
    [InlineData("grep -A2 -C 3 -5 x f", "Invoke-BashGrep '-A2' '-C' 3 '-5' x f")]
    [InlineData("grep -ftemplate.txt f", "Invoke-BashGrep '-ftemplate.txt' f")]
    [InlineData("grep -- -x f", "Invoke-BashGrep '--' '-x' f")]
    [InlineData("grep --color=auto --include='*.c' -r x .", "Invoke-BashGrep '--color=auto' '--include=*.c' '-r' x .")]
    public void Transpile_GrepFlags_AreSingleQuotedAndReachTheCmdletInOrder(string bash, string expected)
    {
        Assert.Equal(expected, PsEmitter.Transpile(bash));
    }

    // sed is on OrderedArgCommands too: repeated -e, -ne/-nE bundles, -i.bak, -s/-z and `--` arrive
    // verbatim; the script text itself is never a flag.
    [Theory]
    [InlineData("sed -e 's/a/b/' -e 's/c/d/' f", "Invoke-BashSed '-e' 's/a/b/' '-e' 's/c/d/' f")]
    [InlineData("sed -ne 2p f", "Invoke-BashSed '-ne' 2p f")]
    [InlineData("sed -nE 's/(a)/\\1/p' f", "Invoke-BashSed '-nE' 's/(a)/\\1/p' f")]
    [InlineData("sed -i.bak s/a/b/ f", "Invoke-BashSed '-i.bak' s/a/b/ f")]
    [InlineData("sed -s -n '$p' a b", "Invoke-BashSed '-s' '-n' '$p' a b")]
    [InlineData("sed -z 's/\\n/,/g' f", "Invoke-BashSed '-z' 's/\\n/,/g' f")]
    [InlineData("sed -n -- 2p f", "Invoke-BashSed '-n' '--' 2p f")]
    [InlineData("sed --in-place=.b --expression=p f", "Invoke-BashSed '--in-place=.b' '--expression=p' f")]
    public void Transpile_SedFlags_AreSingleQuotedAndReachTheCmdletInOrder(string bash, string expected)
    {
        Assert.Equal(expected, PsEmitter.Transpile(bash));
    }

    [Theory]
    [InlineData("bash s.sh -v -e -c x", "Invoke-BashBash s.sh '-v' '-e' '-c' x")]
    [InlineData("bash -c 'echo hi' zero -d", "Invoke-BashBash '-c' 'echo hi' zero '-d'")]
    [InlineData("bash -c 'echo \"$0\"' zero -Verbose", "Invoke-BashBash '-c' 'echo \"$0\"' zero '-Verbose'")]
    public void Transpile_BashScriptArgs_AreSingleQuotedPositionals(string bash, string expected)
    {
        Assert.Equal(expected, PsEmitter.Transpile(bash));
    }

    [Theory]
    [InlineData("cp -rfv a b", "Invoke-BashCp '-rfv' a b")]
    [InlineData("cp -i -p -d a b", "Invoke-BashCp '-i' '-p' '-d' a b")]
    [InlineData("cp --no-clobber a b", "Invoke-BashCp '--no-clobber' a b")]
    [InlineData("cp -- -a b", "Invoke-BashCp '--' '-a' b")]
    [InlineData("mv -fv a b", "Invoke-BashMv '-fv' a b")]
    [InlineData("mv -n a -v b", "Invoke-BashMv '-n' a '-v' b")]
    [InlineData("mv --backup=numbered a b", "Invoke-BashMv '--backup=numbered' a b")]
    [InlineData("mv - a", "Invoke-BashMv '-' a")]
    [InlineData("rm -rf a", "Invoke-BashRm '-rf' a")]
    [InlineData("rm -i -I -d a", "Invoke-BashRm '-i' '-I' '-d' a")]
    [InlineData("rm -- -rf", "Invoke-BashRm '--' '-rf'")]
    [InlineData("rm a -v", "Invoke-BashRm a '-v'")]
    [InlineData("mkdir -pv a/b", "Invoke-BashMkdir '-pv' a/b")]
    [InlineData("mkdir -p -m 755 d", "Invoke-BashMkdir '-p' '-m' 755 d")]
    [InlineData("mkdir -- -d", "Invoke-BashMkdir '--' '-d'")]
    [InlineData("mkdir --parents d", "Invoke-BashMkdir '--parents' d")]
    [InlineData("touch -am f", "Invoke-BashTouch '-am' f")]
    [InlineData("touch -d 2020-01-01 f", "Invoke-BashTouch '-d' 2020-01-01 f")]
    [InlineData("touch -t 202401011200 f", "Invoke-BashTouch '-t' 202401011200 f")]
    [InlineData("touch -r ref f -c", "Invoke-BashTouch '-r' ref f '-c'")]
    [InlineData("touch -- -a", "Invoke-BashTouch '--' '-a'")]
    [InlineData("touch --date=\"2020-01-01 10:00\" f", "Invoke-BashTouch '--date=2020-01-01 10:00' f")]
    [InlineData("ln -sfn a b", "Invoke-BashLn '-sfn' a b")]
    [InlineData("ln -s -T a b", "Invoke-BashLn '-s' '-T' a b")]
    [InlineData("ln -- -a b", "Invoke-BashLn '--' '-a' b")]
    [InlineData("ln -s a -f b", "Invoke-BashLn '-s' a '-f' b")]
    [InlineData("ln -i -d -P a b", "Invoke-BashLn '-i' '-d' '-P' a b")]
    [InlineData("rmdir -pv a/b", "Invoke-BashRmdir '-pv' a/b")]
    [InlineData("rmdir -- -d", "Invoke-BashRmdir '--' '-d'")]
    [InlineData("rmdir --ignore-fail-on-non-empty d", "Invoke-BashRmdir '--ignore-fail-on-non-empty' d")]
    [InlineData("rm --preserve-root=all a", "Invoke-BashRm '--preserve-root=all' a")]
    [InlineData("head -n 5 f", "Invoke-BashHead '-n' 5 f")]
    [InlineData("head -qn2 f", "Invoke-BashHead '-qn2' f")]
    [InlineData("head -5 f", "Invoke-BashHead '-5' f")]
    [InlineData("head -c 1K f", "Invoke-BashHead '-c' 1K f")]
    [InlineData("head --lines=3 f", "Invoke-BashHead '--lines=3' f")]
    [InlineData("head -- -n", "Invoke-BashHead '--' '-n'")]
    // batch 3b rows
    [InlineData("fold -w 5 f", "Invoke-BashFold '-w' 5 f")]
    [InlineData("fold -sw5 f", "Invoke-BashFold '-sw5' f")]
    [InlineData("fold -s -w 10 -- -f", "Invoke-BashFold '-s' '-w' 10 '--' '-f'")]
    [InlineData("expand -t 4,8 f", "Invoke-BashExpand '-t' '4,8' f")]
    [InlineData("expand -t4,8 f", "Invoke-BashExpand '-t4,8' f")]
    [InlineData("expand -i --tabs=3 f", "Invoke-BashExpand '-i' '--tabs=3' f")]
    [InlineData("unexpand -a -t 4 f", "Invoke-BashUnexpand '-a' '-t' 4 f")]
    [InlineData("unexpand -t 2,/4 f", "Invoke-BashUnexpand '-t' '2,/4' f")]
    [InlineData("paste -d, a b", "Invoke-BashPaste '-d,' a b")]
    [InlineData("paste -sd, f", "Invoke-BashPaste '-sd,' f")]
    [InlineData("paste -d ',;' a b", "Invoke-BashPaste '-d' ',;' a b")]
    [InlineData("paste -d '\\n' a b", "Invoke-BashPaste '-d' '\\n' a b")]
    [InlineData("paste - - f", "Invoke-BashPaste '-' '-' f")]
    [InlineData("join -t, -1 2 a b", "Invoke-BashJoin '-t,' '-1' 2 a b")]
    [InlineData("join -a1 -v 2 -i a b", "Invoke-BashJoin '-a1' '-v' 2 '-i' a b")]
    [InlineData("comm -12 a b", "Invoke-BashComm '-12' a b")]
    [InlineData("comm --output-delimiter=, a b", "Invoke-BashComm '--output-delimiter=,' a b")]
    [InlineData("split -l 2 -d f p", "Invoke-BashSplit '-l' 2 '-d' f p")]
    [InlineData("split -b1K -a 3 f", "Invoke-BashSplit '-b1K' '-a' 3 f")]
    [InlineData("strings -n 6 f", "Invoke-BashStrings '-n' 6 f")]
    [InlineData("strings -a -e S f", "Invoke-BashStrings '-a' '-e' S f")]
    [InlineData("base64 -w0 f", "Invoke-BashBase64 '-w0' f")]
    [InlineData("base64 -d", "Invoke-BashBase64 '-d'")]
    [InlineData("base64 -di -w 0", "Invoke-BashBase64 '-di' '-w' 0")]
    [InlineData("head -v -z f", "Invoke-BashHead '-v' '-z' f")]
    [InlineData("tail -n 5 f", "Invoke-BashTail '-n' 5 f")]
    [InlineData("tail -n +3 f", "Invoke-BashTail '-n' +3 f")]
    [InlineData("tail -qn2 f", "Invoke-BashTail '-qn2' f")]
    [InlineData("tail -c 1K f", "Invoke-BashTail '-c' 1K f")]
    [InlineData("tail --follow=name f", "Invoke-BashTail '--follow=name' f")]
    [InlineData("tail -F -- -n", "Invoke-BashTail '-F' '--' '-n'")]
    [InlineData("wc -l f", "Invoke-BashWc '-l' f")]
    [InlineData("wc -lwc f", "Invoke-BashWc '-lwc' f")]
    [InlineData("wc --max-line-length f", "Invoke-BashWc '--max-line-length' f")]
    [InlineData("wc -w -- -c", "Invoke-BashWc '-w' '--' '-c'")]
    [InlineData("cat -n f", "Invoke-BashCat '-n' f")]
    [InlineData("cat -nET f", "Invoke-BashCat '-nET' f")]
    [InlineData("cat --squeeze-blank f", "Invoke-BashCat '--squeeze-blank' f")]
    [InlineData("cat -e -v -A f", "Invoke-BashCat '-e' '-v' '-A' f")]
    [InlineData("cat - f -n", "Invoke-BashCat '-' f '-n'")]
    [InlineData("cat -- -n", "Invoke-BashCat '--' '-n'")]
    [InlineData("tac -s x f", "Invoke-BashTac '-s' x f")]
    [InlineData("tac -sx f", "Invoke-BashTac '-sx' f")]
    [InlineData("tac --separator=, f", "Invoke-BashTac '--separator=,' f")]
    [InlineData("nl -ba f", "Invoke-BashNl '-ba' f")]
    [InlineData("nl -w 3 -v 5 -i 2 f", "Invoke-BashNl '-w' 3 '-v' 5 '-i' 2 f")]
    [InlineData("nl -s: -nrz f", "Invoke-BashNl '-s:' '-nrz' f")]
    [InlineData("nl --number-width=3 f", "Invoke-BashNl '--number-width=3' f")]
    [InlineData("uniq -c f", "Invoke-BashUniq '-c' f")]
    [InlineData("uniq -D f", "Invoke-BashUniq '-D' f")]
    [InlineData("uniq -f 1 -s 2 -w 3 f", "Invoke-BashUniq '-f' 1 '-s' 2 '-w' 3 f")]
    [InlineData("uniq --all-repeated=prepend f", "Invoke-BashUniq '--all-repeated=prepend' f")]
    [InlineData("uniq -cdi -- -z", "Invoke-BashUniq '-cdi' '--' '-z'")]
    [InlineData("command -v ls", "Invoke-BashCommand '-v' ls")]
    [InlineData("command -pv ls", "Invoke-BashCommand '-pv' ls")]
    [InlineData("command ls -d .", "Invoke-BashCommand ls '-d' .")]
    [InlineData("command -p echo -e x", "Invoke-BashCommand '-p' echo '-e' x")]
    [InlineData("command grep -i x f", "Invoke-BashCommand grep '-i' x f")]
    [InlineData("command -- ls -d", "Invoke-BashCommand '--' ls '-d'")]
    // batch 5: du tree column gzip tar md5sum sha1sum sha256sum
    [InlineData("du -sh -d 1 .", "Invoke-BashDu '-sh' '-d' 1 .")]
    [InlineData("du -P -c d", "Invoke-BashDu '-P' '-c' d")]
    [InlineData("tree -L 2 -I '*.o' -a dir", "Invoke-BashTree '-L' 2 '-I' '*.o' '-a' dir")]
    [InlineData("tree -d -C dir", "Invoke-BashTree '-d' '-C' dir")]
    [InlineData("column -t -s: -o ' | ' f", "Invoke-BashColumn '-t' '-s:' '-o' ' | ' f")]
    [InlineData("column -e -d -V", "Invoke-BashColumn '-e' '-d' '-V'")]
    [InlineData("gzip -dc -9 f.gz", "Invoke-BashGzip '-dc' '-9' f.gz")]
    [InlineData("gzip -S .z -- -x", "Invoke-BashGzip '-S' .z '--' '-x'")]
    [InlineData("tar -xf a.tar -C out", "Invoke-BashTar '-xf' a.tar '-C' out")]
    [InlineData("tar -cvf a.tar -c d", "Invoke-BashTar '-cvf' a.tar '-c' d")]
    [InlineData("tar czf a.tgz d", "Invoke-BashTar czf a.tgz d")]
    [InlineData("tar --exclude=x --strip-components=1 -xf a.tar", "Invoke-BashTar '--exclude=x' '--strip-components=1' '-xf' a.tar")]
    [InlineData("md5sum -c -w sums", "Invoke-BashMd5sum '-c' '-w' sums")]
    [InlineData("sha256sum --tag -z f", "Invoke-BashSha256sum '--tag' '-z' f")]
    [InlineData("sha1sum -b -- -c", "Invoke-BashSha1sum '-b' '--' '-c'")]
    public void Transpile_OrderedArgCommand_QuotesEveryDashLiteral(string bash, string expected)
    {
        Assert.Equal(expected, PsEmitter.Transpile(bash));
    }

    [Theory]
    [InlineData("echo a | tee -a -i -p f", "Invoke-BashEcho a | Invoke-BashTee '-a' '-i' '-p' f")]
    [InlineData("echo a | tee --append -- -zz", "Invoke-BashEcho a | Invoke-BashTee '--append' '--' '-zz'")]
    public void Transpile_TeeFlags_AreAllQuoted(string bash, string expected)
    {
        Assert.Equal(expected, PsEmitter.Transpile(bash));
    }

    [Fact]
    public void Transpile_OrderedArgCommand_QuotedAndMixedDashWordsBecomeOneSingleQuotedLiteral()
    {
        Assert.Equal("Invoke-BashCp '-r' a b", PsEmitter.Transpile("cp \"-r\" a b"));
        Assert.Equal("Invoke-BashCp '--target=a b' x", PsEmitter.Transpile("cp --target=\"a b\" x"));
        // an embedded single quote is doubled by PsBuild.SingleQuote, not corrupted
        Assert.Equal("Invoke-BashCp '-a''b' x y", PsEmitter.Transpile("cp \"-a'b\" x y"));
    }

    [Fact]
    public void Transpile_OrderedArgCommand_LeavesNonDashWordsAndOtherCommandsAlone()
    {
        Assert.Equal("Invoke-BashCp a/b c-d", PsEmitter.Transpile("cp a/b c-d"));
        // diff is not opted in: `-u` stays a bare flag
        Assert.Contains("Invoke-BashDiff '-u' ", PsEmitter.Transpile("diff -u a b"));
    }

    [Fact]
    public void Transpile_OrderedArgCommand_UnquotedVariableStillSplatsAndFlagsStayQuoted()
    {
        // RC-7 word-splitting path (EmitCommandWithSplatArgs) shares the same arg renderer.
        var result = PsEmitter.Transpile("cp -r $f dst")!;
        Assert.Contains("'-r'", result);
        Assert.Contains("@__bashsplat0", result);
        Assert.DoesNotContain(" -r ", result);
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
    public void Transpile_InputRedirect_NativeConsumer_FeedsExactRecordsAsText()
    {
        var result = PsEmitter.Transpile("cmd < input.txt");

        // Exact records (no CRLF/BOM rewrite, invalid bytes kept), converted to text for the native binder.
        Assert.Equal("Invoke-BashCat input.txt | ForEach-Object { Get-BashText $_ } | cmd", result);
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

        Assert.Equal("cmd | Invoke-BashRedirect -Path \"$($env:OS -eq 'Windows_NT' ? $env:TEMP : '/tmp')/out.log\"", result);
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

        Assert.Equal("Invoke-BashTest '-f' file || Invoke-BashEcho missing", result);
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

        Assert.Equal("Invoke-BashTest '-f' file || Invoke-BashEcho missing", result);
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
            "& { $__bashsplat0 = @(ConvertTo-BashWords $env:FOO); " +
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
            "& { $__bashsplat0 = @(ConvertTo-BashWords $env:PATH); " +
            "Invoke-BashEcho @__bashsplat0 }",
            result);
    }

    [Fact]
    public void Transpile_BracedVarWithDefault_EmitsEmptyOrUnsetTest()
    {
        var result = PsEmitter.Transpile("echo ${VAR:-fallback}");

        // The `:` forms act when the variable is unset OR EMPTY. This asserted
        // `??`, which only tests $null, so `x=; echo ${x:-d}` printed NOTHING
        // instead of `d` — the most common parameter expansion in shell, silently
        // wrong. The ternary is exactly bash's test ("" and $null falsy, "0" truthy).
        Assert.Equal("Invoke-BashEcho ($env:VAR ? $env:VAR : \"fallback\")", result);
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
    public void Transpile_SpecialVar0_EmitsLauncherNameDefaultingToBash()
    {
        var result = PsEmitter.Transpile("echo $0");

        Assert.Equal("Invoke-BashEcho $(if ($global:BashPositional0) { $global:BashPositional0 } else { 'bash' })", result);
    }

    [Fact]
    public void Transpile_BracedVarAssignDefault_EmitsEmptyOrUnsetTest()
    {
        var result = PsEmitter.Transpile("echo ${VAR:=default}");

        Assert.Equal("Invoke-BashEcho ($env:VAR ? $env:VAR : ($env:VAR = \"default\"))", result);
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

        Assert.Equal("Invoke-BashEcho ($env:VAR ? $env:VAR : $(throw \"error msg\"))", result);
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
        Assert.Equal("Invoke-BashEcho ($arr[0] ? $arr[0] : \"x\")", result);
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
        var result = PsEmitter.Transpile("echo x$(whoami)");

        // RC-8d: command-substitution emit wraps inner output in
        // `| ForEach-Object { Get-BashText $_ }` so the captured value is the
        // bash-text payload, never a typed BashObject's default ToString(). Glued to `x`, the
        // whole word is one join expression (a bareword `x$( … )` has PowerShell's naive
        // paren scan — see TryEmitJoinedWord).
        Assert.Equal("Invoke-BashEcho (-join @('x', (@(Invoke-BashWhoami | ConvertTo-BashCapture) -join ' ')))", result);
    }

    [Fact]
    public void Transpile_CommandSub_InnerPipeline_TranspilesInnerCommands()
    {
        var result = PsEmitter.Transpile("echo x$(ls | grep foo)");

        Assert.Equal("Invoke-BashEcho (-join @('x', (@(Invoke-BashLs | Invoke-BashGrep foo | ConvertTo-BashCapture) -join ' ')))", result);
    }

    [Fact]
    public void Transpile_BacktickCommandSub_NormalizedToDollarParen()
    {
        var result = PsEmitter.Transpile("echo x`date`");

        Assert.Equal("Invoke-BashEcho (-join @('x', (@(Invoke-BashDate | ConvertTo-BashCapture) -join ' ')))", result);
    }

    [Fact]
    public void Transpile_AssignmentWithCommandSub_EmitsEnvAssignment()
    {
        var result = PsEmitter.Transpile("VAR=$(cat file)");

        // Assignment command-sub preserves internal newlines and strips trailing ones
        // (bash), instead of the array $OFS-joining with a space (which flattened the
        // file to one line). A bare value, not wrapped in a "…" string (TryEmitJoinedValue).
        Assert.Equal("$env:VAR = $((@(Invoke-BashCat file | ConvertTo-BashCapture) -join [string][char]10) -replace '(\\r?\\n)+$','')", result);
    }

    [Fact]
    public void Transpile_NestedCommandSub_EmitsCorrectNesting()
    {
        var result = PsEmitter.Transpile("echo x$(echo y$(whoami))");

        Assert.Equal("Invoke-BashEcho (-join @('x', (@(Invoke-BashEcho (-join @('y', (@(Invoke-BashWhoami | ConvertTo-BashCapture) -join ' '))) | ConvertTo-BashCapture) -join ' ')))", result);
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

        Assert.Equal("$env:dir = $((@(Invoke-BashPwd | ConvertTo-BashCapture) -join [string][char]10) -replace '(\\r?\\n)+$','')", result);
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

        Assert.Equal("Invoke-BashCat \"$($env:OS -eq 'Windows_NT' ? $env:TEMP : '/tmp')/log.txt\"", result);
    }

    // The /tmp rewrite works on the word's PARTS: the leading `/tmp/` of the first part is
    // replaced by the runtime temp-dir expression and every remaining part is rendered through
    // the normal double-quote part emitters. It used to splice already-EMITTED text (which still
    // carried bash's quote characters) into a PowerShell string, so `/tmp/psb_'s p'` named a file
    // with LITERAL single quotes in it.
    private const string TmpDir = "$($env:OS -eq 'Windows_NT' ? $env:TEMP : '/tmp')";

    [Theory]
    [InlineData("cat /tmp/psb_'s p'", "Invoke-BashCat \"" + TmpDir + "/psb_s p\"")]
    [InlineData("cat /tmp/\"a b\"", "Invoke-BashCat \"" + TmpDir + "/a b\"")]
    [InlineData("cat \"/tmp/a b\"", "Invoke-BashCat \"" + TmpDir + "/a b\"")]
    [InlineData("cat '/tmp/a b'", "Invoke-BashCat \"" + TmpDir + "/a b\"")]
    [InlineData("cat \"/tmp/\"x", "Invoke-BashCat \"" + TmpDir + "/x\"")]
    [InlineData("cat /tmp/a$x", "Invoke-BashCat \"" + TmpDir + "/a$env:x\"")]
    [InlineData("cat /tmp/psb_\\$z", "Invoke-BashCat \"" + TmpDir + "/psb_`$z\"")]
    [InlineData("cat /tmp/it\\'s", "Invoke-BashCat \"" + TmpDir + "/it's\"")]
    [InlineData("cat /tmp/'a$b'", "Invoke-BashCat \"" + TmpDir + "/a`$b\"")]
    public void Transpile_TmpWordWithQuotedOrSpecialParts_RendersPartsNotEmittedText(string bash, string expected)
    {
        Assert.Equal(expected, PsEmitter.Transpile(bash));
    }

    [Fact]
    public void Transpile_TmpWordWithCommandSub_KeepsTheSubstitution()
    {
        var result = PsEmitter.Transpile("cat /tmp/$(echo hi).txt");

        Assert.StartsWith("Invoke-BashCat \"" + TmpDir + "/$(", result);
        Assert.Contains("Invoke-BashEcho hi", result);
        Assert.EndsWith(".txt\"", result);
    }

    [Fact]
    public void Transpile_TmpRedirectTargetWithQuotes_RendersPartsNotEmittedText()
    {
        var result = PsEmitter.Transpile("echo c > /tmp/psb_'s p'");

        Assert.Equal(
            "Invoke-BashEcho c | Invoke-BashRedirect -Path \"" + TmpDir + "/psb_s p\"", result);
    }

    [Fact]
    public void Transpile_TmpBraceExpansion_MapsEveryItemToTempDir()
    {
        var result = PsEmitter.Transpile("ls /tmp/{a,b}");

        Assert.Equal(
            "Invoke-BashLs @(\"" + TmpDir + "/a\",\"" + TmpDir + "/b\")", result);
    }

    [Theory]
    [InlineData("cat $HOME/tmp/x")]
    [InlineData("cat /var/tmp/x")]
    [InlineData("cat ./tmp/x")]
    [InlineData("cat x/tmp/y")]
    public void Transpile_NonRootTmp_IsNotRewritten(string bash)
    {
        Assert.DoesNotContain("Windows_NT", PsEmitter.Transpile(bash));
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
    [InlineData("[ -z \"${V:-}\" ]", "[string]::IsNullOrEmpty(($env:V ? $env:V : ''))")]
    [InlineData("[ -n \"${V:-}\" ]", "-not [string]::IsNullOrEmpty(($env:V ? $env:V : ''))")]
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
    public void Transpile_ShellOptionTest_RunsTheTestCmdlet()
    {
        // `[ -o PROMPT_SUBST ]` (shell-option test) has no PowerShell equivalent. The old fallback
        // joined the operands with spaces and emitted `'-o' 'PROMPT_SUBST'` — two adjacent values,
        // never valid PowerShell — so one such line broke the parse of the ENTIRE file. A `[ ]` the
        // translator does not model now runs the `test` cmdlet (bash's own argument grammar).
        var result = PsEmitter.Transpile("[ -o PROMPT_SUBST ] && echo on");

        Assert.Contains("Invoke-BashTest '-o' PROMPT_SUBST", result);
        Assert.DoesNotContain("unsupported test operator", result);
    }

    [Theory]
    [InlineData("[ -f = -f ]", "'-f' -eq '-f'")]            // arity 3: the middle operator wins
    public void Transpile_BracketArity3_OperatorInTheMiddleWins(string script, string expected)
        => Assert.Contains(expected, PsEmitter.Transpile(script));

    [Fact]
    public void Transpile_BracketParens_RunTheTestCmdlet()
        => Assert.Contains("Invoke-BashTest `( '-n' x `)", PsEmitter.Transpile(@"[ \( -n x \) -a -n y ]"));
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

        // The match is wrapped in a subexpression that also copies $Matches into
        // BASH_REMATCH (bash fills it on =~), so the emitted form is no longer a
        // bare `-match` boolean.
        Assert.Contains("$env:a -match '^[0-9]+$'", result);
        Assert.Contains("$global:BASH_REMATCH = $Matches", result);
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
    // NOTE the QUOTED form only: `for x in "$list"` is ONE iteration in bash.
    [InlineData("for x in \"$list\"; do echo $x; done", "\"$env:list\"")]
    public void Transpile_ForInSingleItem_LeavesPowerShellValuesUnquoted(
        string bash, string expected)
    {
        Assert.Contains($"foreach ($x in {expected})", PsEmitter.Transpile(bash));
    }

    [Fact]
    public void Transpile_ForInUnquotedVar_WordSplits()
    {
        // This case previously asserted the bare `foreach ($x in $env:list)`, which
        // iterates ONCE over the whole string. bash word-splits an UNQUOTED
        // expansion, so `list="a b c"` runs the loop THREE times — a silent wrong
        // result found by the oracle differential sweep, not by any parse check.
        // RC-7 splitting applied to command arguments but not to a for-in list.
        var result = PsEmitter.Transpile("for x in $list; do echo $x; done");

        Assert.Contains("@(ConvertTo-BashWords $env:list)", result);
        Assert.DoesNotContain("foreach ($x in $env:list)", result);
    }

    [Fact]
    public void Transpile_ForInMultipleUnquotedVars_EachWordSplits()
    {
        var result = PsEmitter.Transpile("for x in $a $b; do echo $x; done");

        Assert.Contains("ConvertTo-BashWords $env:a", result);
        Assert.Contains("ConvertTo-BashWords $env:b", result);
    }

    [Fact]
    public void Transpile_ForInMultipleUnquotedVars_FlattenIntoOneList()
    {
        // PowerShell's comma operator does NOT splice: `'a',@('b','c')` is two elements. The split
        // arrays must flatten (oracle: `a="x y"; for w in $a z` runs x, y, z).
        var result = PsEmitter.Transpile("for w in $a z; do echo $w; done");

        Assert.Contains("foreach ($w in @(@(ConvertTo-BashWords $env:a)", result);
        Assert.Contains("; 'z')", result);
    }

    [Fact]
    public void Transpile_ForInCommandSub_WordSplitsThroughConvertToBashWords()
    {
        // `for f in $(echo a b c)` ran ONCE over "a b c": the capture array only splits on lines.
        var result = PsEmitter.Transpile("for f in $(echo a b c); do echo \"<$f>\"; done");

        Assert.Contains("foreach ($f in @(ConvertTo-BashWords $((@(Invoke-BashEcho a b c | ConvertTo-BashCapture)", result);
    }

    [Fact]
    public void Transpile_ForInBacktickCommandSub_WordSplitsToo()
    {
        var result = PsEmitter.Transpile("for f in `echo a b`; do echo $f; done");

        Assert.Contains("foreach ($f in @(ConvertTo-BashWords ", result);
    }

    [Fact]
    public void Transpile_ForInQuotedCommandSub_StaysOneWordAndIsNotGlobbed()
    {
        // The regex in the quoted capture's `-replace '(\r?\n)+$'` once made the emitted TEXT look like a
        // glob (`?`), sending a quoted "$(cmd)" through Resolve-BashGlob.
        var result = PsEmitter.Transpile("for f in \"$(echo a b)\"; do echo \"<$f>\"; done");

        Assert.DoesNotContain("ConvertTo-BashWords", result);
        Assert.DoesNotContain("Resolve-BashGlob", result);
    }

    [Fact]
    public void Transpile_ForInMixedListWithCommandSub_FlattensAndKeepsLiterals()
    {
        var result = PsEmitter.Transpile("for f in x $(echo a b) y; do echo $f; done");

        Assert.Contains("foreach ($f in @('x'; @(ConvertTo-BashWords ", result);
        Assert.Contains("; 'y')", result);
    }

    [Fact]
    public void Transpile_UnquotedCommandSubOperand_HoistsWordSplitArrayAndSplats()
    {
        // `printf '%s\n' $(echo a b)`: the substitution's text is word-split (and an empty result is
        // elided) exactly like an unquoted $var operand (RC-7).
        var result = PsEmitter.Transpile("printf '%s\\n' $(echo a b)");

        Assert.StartsWith("& { $__bashsplat0 = @(ConvertTo-BashWords $((@(Invoke-BashEcho a b | ConvertTo-BashCapture)", result);
        Assert.EndsWith("; Invoke-BashPrintf '%s\\n' @__bashsplat0 }", result);
    }

    [Fact]
    public void Transpile_QuotedCommandSubOperand_NotSplat()
    {
        var result = PsEmitter.Transpile("echo \"$(echo a b)\"");

        Assert.DoesNotContain("__bashsplat", result);
    }

    [Fact]
    public void Transpile_SplatOperandOnPipeTarget_ForwardsPipelineInputIntoTheBlock()
    {
        // A script block hands the stage's pipeline input to its `$input`, not to the command inside:
        // `printf … | grep $x` used to print nothing.
        var result = PsEmitter.Transpile("printf 'a\\nb\\n' | grep $x");

        Assert.Contains("| & { $__bashsplat0 = ", result);
        Assert.Contains("; $input | Invoke-BashGrep @__bashsplat0 }", result);
    }

    [Fact]
    public void Transpile_SplatOperandOnFirstStage_DoesNotReadInput()
    {
        var result = PsEmitter.Transpile("echo $x | cat");

        Assert.DoesNotContain("$input", result);
    }

    [Fact]
    public void Transpile_CommandSubInsideSplatPipeStage_DoesNotStealTheStageInput()
    {
        // The substitution body is its own pipeline: no `$input |` inside it.
        var result = PsEmitter.Transpile("echo a | grep $(echo b $y)")!;

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, @"\$input \|"));
    }

    [Theory]
    [InlineData(": > f", "$($global:LASTEXITCODE = 0; @() | Invoke-BashRedirect -Path f; if ($global:LASTEXITCODE -ne 0) { Write-Error '' -ErrorAction SilentlyContinue } else { [void]$true })")]
    [InlineData(":>f", "$($global:LASTEXITCODE = 0; @() | Invoke-BashRedirect -Path f; if ($global:LASTEXITCODE -ne 0) { Write-Error '' -ErrorAction SilentlyContinue } else { [void]$true })")]
    [InlineData("true >> f", "$($global:LASTEXITCODE = 0; @() | Invoke-BashRedirect -Path f -Append; if ($global:LASTEXITCODE -ne 0) { Write-Error '' -ErrorAction SilentlyContinue } else { [void]$true })")]
    [InlineData("> f", "$($global:LASTEXITCODE = 0; @() | Invoke-BashRedirect -Path f; if ($global:LASTEXITCODE -ne 0) { Write-Error '' -ErrorAction SilentlyContinue } else { [void]$true })")]
    public void Transpile_NoOpBuiltinWithRedirect_OpensTheTarget(string bash, string expected)
    {
        // bash opens (creates / truncates) a redirection target even when the command writes nothing.
        Assert.Equal(expected, PsEmitter.Transpile(bash));
    }

    [Fact]
    public void Transpile_NoOpBuiltin_StderrToNullSilencesOnlyTheTargetsAfterIt()
    {
        // Redirections apply left to right: `: 2>/dev/null > f` is quiet when f cannot be opened,
        // `: > f 2>/dev/null` reports first (bash opens f before the stderr redirection exists).
        var after = PsEmitter.Transpile(": 2>/dev/null > f");
        var before = PsEmitter.Transpile(": > f 2>/dev/null");

        Assert.Contains("Invoke-BashRedirect -Path f 2>$null;", after);
        Assert.DoesNotContain("-Path f 2>$null", before);
    }

    [Fact]
    public void Transpile_FalseWithRedirect_OpensTheTargetThenFails()
    {
        var result = PsEmitter.Transpile("false > f");

        Assert.StartsWith("$(@() | Invoke-BashRedirect -Path f; $global:LASTEXITCODE = 1;", result);
    }

    [Fact]
    public void Transpile_NoOpBuiltinWithTwoRedirects_OpensBothInOrder()
    {
        var result = PsEmitter.Transpile(": > a >> b");

        Assert.Contains("Invoke-BashRedirect -Path a; @() | Invoke-BashRedirect -Path b -Append;", result);
    }

    [Theory]
    [InlineData(": > /dev/null")]
    [InlineData(": 2>&1")]
    [InlineData("true >&2")]
    public void Transpile_NoOpBuiltinWithNonFileRedirect_TouchesNothing(string bash)
    {
        Assert.DoesNotContain("Invoke-BashRedirect", PsEmitter.Transpile(bash));
    }

    [Fact]
    public void Transpile_BareRedirect_IsNotAnEmptyPipeElement()
    {
        // `> f` alone used to emit ` | Invoke-BashRedirect -Path f` — "An empty pipe element is not allowed".
        var result = PsEmitter.Transpile("> f");

        Assert.StartsWith("$($global:LASTEXITCODE = 0; @() | Invoke-BashRedirect -Path f;", result);
    }

    [Fact]
    public void Transpile_EnvPrefixOnPipeTarget_WrapsStageAndForwardsInput()
    {
        // The emitter used to drop `VAR=v` on every pipe-target stage: `seq 1 40 | COLUMNS=60 column`
        // ran `column` without COLUMNS.
        var result = PsEmitter.Transpile("seq 1 40 | COLUMNS=60 column");

        Assert.Equal(
            "Invoke-BashSeq 1 40 | & { $__saved_COLUMNS = $env:COLUMNS; try { $env:COLUMNS = \"60\"; $input | Invoke-BashColumn } finally { $env:COLUMNS = $__saved_COLUMNS; } }",
            result);
    }

    [Fact]
    public void Transpile_EnvPrefixOnMiddleStage_LeavesOtherStagesPlain()
    {
        var result = PsEmitter.Transpile("echo x | FOO=1 env | grep FOO");

        Assert.Equal(
            "Invoke-BashEcho x | & { $__saved_FOO = $env:FOO; try { $env:FOO = \"1\"; $input | Invoke-BashEnv } finally { $env:FOO = $__saved_FOO; } } | Invoke-BashGrep FOO",
            result);
    }

    [Fact]
    public void Transpile_EnvPrefixOnNativePipeTarget_ConvertsTextInsideTheWrapper()
    {
        var result = PsEmitter.Transpile("echo x | FOO=1 python3");

        Assert.Contains("try { $env:FOO = \"1\"; $input | ForEach-Object { Get-BashText $_ } | python3 }", result);
    }

    [Fact]
    public void Transpile_EnvPrefixOnFirstStage_KeepsTheStandaloneWrap()
    {
        // The first stage reads no pipe: unchanged (no `$input`).
        var result = PsEmitter.Transpile("FOO=1 env | grep FOO");

        Assert.DoesNotContain("$input", result);
        Assert.Contains("$__saved_FOO = $env:FOO; try {", result);
    }

    [Fact]
    public void Transpile_EnvPrefixOnTrueStage_ConsumesInputInsideTheWrapper()
    {
        var result = PsEmitter.Transpile("echo hi | FOO=1 true");

        Assert.Contains("$input | Out-Null; $global:LASTEXITCODE = 0 }", result);
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
    public void Transpile_ForInGlob_EmitsConvertToBashGlob()
    {
        var result = PsEmitter.Transpile("for f in *.txt; do cat $f; done");

        // ConvertTo-BashGlob: the shell's pathname expansion (relative names, no hidden files,
        // sorted); an unmatched glob falls back to the literal word so the loop still runs
        // once — bash nullglob is OFF by default.
        Assert.Equal("$__psbash_iter = 0; foreach ($f in @(ConvertTo-BashGlob '*.txt')) { if (++$__psbash_iter -gt ($env:PSBASH_MAX_ITERATIONS ?? 100000)) { throw \"ps-bash: loop iteration limit exceeded ($(($env:PSBASH_MAX_ITERATIONS ?? 100000)))\" }; Invoke-BashCat $f }", result);
    }

    [Fact]
    public void Transpile_ForInSingleNonMatchingGlob_FallsBackToLiteralWordViaConvertToBashGlob()
    {
        // bash: `for f in *.xyz` with no match iterates ONCE with the literal
        // word `*.xyz` (nullglob off). Resolve-Path would error + yield nothing
        // (zero iterations). ConvertTo-BashGlob returns the literal on no-match.
        var result = PsEmitter.Transpile("for f in *.xyz; do echo $f; done");

        Assert.Contains("foreach ($f in @(ConvertTo-BashGlob '*.xyz'))", result);
        Assert.DoesNotContain("Resolve-Path", result);
    }

    [Fact]
    public void Transpile_ForInMixedGlobList_FlattensEachGlobWordIndependently()
    {
        // A single non-matching glob/literal must NOT nuke the rest of the list: every glob word
        // expands on its own into one flat list, so `a.txt` and `missing.xyz` survive even when
        // `*.log` matches nothing.
        var result = PsEmitter.Transpile("for f in a.txt *.log missing.xyz; do echo $f; done");

        Assert.Contains("foreach ($f in @('a.txt'; @(ConvertTo-BashGlob '*.log'); 'missing.xyz'))", result);
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

        Assert.Equal("try { Push-Location; Invoke-BashEcho hello; Invoke-BashEcho world } " + SubshellPop, result);
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

        // The try/finally body is a STATEMENT and cannot head the redirect pipe
        // ("An empty pipe element is not allowed"), so it is wrapped in `& { }`.
        Assert.Equal("& { try { Push-Location; Invoke-BashEcho hello } " + SubshellPop + " } | Invoke-BashRedirect -Path out.txt", result);
    }

    [Fact]
    public void Transpile_NestedSubshells_EmitsNestedBlocks()
    {
        var result = PsEmitter.Transpile("(echo a; (echo b))");

        // Nesting: the SAME `$__psbash_subshell_pop` temp appears at both levels, which is
        // safe because the inner `finally` runs to completion inside the outer body — the
        // assignment and its reads never interleave across levels.
        Assert.Equal("try { Push-Location; Invoke-BashEcho a; try { Push-Location; Invoke-BashEcho b } "
            + SubshellPop + " } " + SubshellPop, result);
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
        Assert.Contains("&& Invoke-BashPwd } " + SubshellPop + ") && Invoke-BashPwd", result);
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
        string result = PsEmitter.Transpile(source)!;

        Assert.Contains("Invoke-BashArith", result);
        Assert.Contains(expected, result);
        Assert.DoesNotContain("$env:#", result);
        Assert.DoesNotContain("$env:?", result);
    }

    [Fact]
    public void Transpile_ArithCommand_WithPositional_UsesSameEvaluatorHandoff()
    {
        string result = PsEmitter.Transpile("(( $1 ** 2 ))")!;

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
        string result = PsEmitter.Transpile(source)!;

        Assert.Contains("Invoke-BashArith", result);
        Assert.Contains(expectedReference, result);
        Assert.Contains(expectedLiteral, result);
    }

    [Fact]
    public void Transpile_BracedNamedArithmeticParameter_RemainsEvaluatorResolvedSource()
        => Assert.Equal("Invoke-BashEcho $(Invoke-BashArith '${x} + 1')",
            PsEmitter.Transpile("echo $((${x} + 1))"));

    // A glob word is expanded by the SHELL (ConvertTo-BashGlob), so echo receives file names.
    // See emitter-strategy.md "Pathname expansion".
    private static string GlobSplat(string pattern, string command = "Invoke-BashEcho") =>
        $"& {{ $__bashsplat0 = @(ConvertTo-BashGlob '{pattern}'); {command} @__bashsplat0 }}";

    [Fact]
    public void Transpile_GlobStar_ExpandsViaConvertToBashGlob()
    {
        var result = PsEmitter.Transpile("echo *.py");
        Assert.Equal(GlobSplat("*.py"), result);
    }

    [Fact]
    public void Transpile_GlobQuestionMark_ExpandsViaConvertToBashGlob()
    {
        var result = PsEmitter.Transpile("echo file?.txt");
        Assert.Equal(GlobSplat("file?.txt"), result);
    }

    [Fact]
    public void Transpile_GlobCharClass_ExpandsViaConvertToBashGlob()
    {
        var result = PsEmitter.Transpile("echo [abc]*");
        Assert.Equal(GlobSplat("[abc]*"), result);
    }

    [Fact]
    public void Transpile_GlobMixedWithLiteral_ExpandsViaConvertToBashGlob()
    {
        var result = PsEmitter.Transpile("echo src/*.py");
        Assert.Equal(GlobSplat("src/*.py"), result);
    }

    [Fact]
    public void Transpile_GlobStandalone_ExpandsViaConvertToBashGlob()
    {
        var result = PsEmitter.Transpile("echo *");
        Assert.Equal(GlobSplat("*"), result);
    }

    [Fact]
    public void Transpile_ExtGlob_PassesThrough()
    {
        var result = PsEmitter.Transpile("echo +(*.py|*.js)");
        Assert.Equal("Invoke-BashEcho +(*.py|*.js)", result);
    }

    [Fact]
    public void Transpile_GlobPrefix_ExpandsViaConvertToBashGlob()
    {
        var result = PsEmitter.Transpile("echo *.log");
        Assert.Equal(GlobSplat("*.log"), result);
    }

    [Fact]
    public void Transpile_GlobSuffix_ExpandsViaConvertToBashGlob()
    {
        var result = PsEmitter.Transpile("echo test*");
        Assert.Equal(GlobSplat("test*"), result);
    }

    // ---- Pathname expansion: every consumer, not only the cmdlets that glob themselves ----

    [Fact]
    public void Transpile_PrintfWithGlobOperand_ExpandsTheGlobOnly()
    {
        Assert.Equal(
            "& { $__bashsplat0 = @(ConvertTo-BashGlob '*'); Invoke-BashPrintf '%s\\n' @__bashsplat0 }",
            PsEmitter.Transpile("printf '%s\\n' *"));
    }

    [Theory]
    [InlineData("f *", "f")]
    [InlineData("/bin/echo *", "/bin/echo")]
    [InlineData("cmd.exe /c echo *", "cmd.exe")]
    public void Transpile_GlobOperandOfFunctionOrNativeCommand_IsExpandedByTheShell(string bash, string command)
    {
        // Functions and native programs never expand a pattern themselves, and never get -ForCmdlet.
        var result = PsEmitter.Transpile(bash);

        Assert.Contains("$__bashsplat0 = @(ConvertTo-BashGlob '*');", result);
        Assert.Contains(command, result);
        Assert.EndsWith("@__bashsplat0 }", result);
    }

    [Theory]
    [InlineData("touch *.c", "Invoke-BashTouch")]
    [InlineData("tar cf a.tar *", "Invoke-BashTar")]
    [InlineData("cp *.c d", "Invoke-BashCp")]
    [InlineData("rm -f *.o", "Invoke-BashRm")]
    [InlineData("grep -l x *", "Invoke-BashGrep")]
    [InlineData("ls *", "Invoke-BashLs")]
    public void Transpile_GlobOperandOfMappedCommand_IsExpandedByTheShellForEveryCommand(
        string bash, string command)
    {
        // Every consumer gets names; the cmdlets that also glob their own operands then see plain names
        // (one expansion by the shell, hidden files and ordering decided in one place).
        var pattern = bash.Contains("*.c") ? "*.c" : bash.Contains("*.o") ? "*.o" : "*";

        Assert.Contains($"$__bashsplat0 = @(ConvertTo-BashGlob '{pattern}');", PsEmitter.Transpile(bash));
        Assert.Contains(command, PsEmitter.Transpile(bash));
    }

    [Theory]
    [InlineData("echo \"*\"")]
    [InlineData("echo '*.txt'")]
    [InlineData("echo \\*")]
    [InlineData("echo \\*.txt")]
    [InlineData("echo \"a\"*\"\" 'x'")]
    public void Transpile_QuotedOrEscapedGlobCharacter_IsNotExpanded(string bash)
    {
        // `echo "a"*` still globs (the star is bare); the others have only quoted stars.
        var result = PsEmitter.Transpile(bash);

        if (bash.Contains("\"a\""))
            Assert.Contains("ConvertTo-BashGlob 'a*'", result);
        else
            Assert.DoesNotContain("ConvertTo-BashGlob", result);
    }

    [Fact]
    public void Transpile_GlobWordWithQuotedParts_EscapesTheQuotedGlobCharacters()
    {
        // `"a*"*` : the first star is quoted (matches itself), the second is the glob.
        Assert.Equal(GlobSplat("a\\**"), PsEmitter.Transpile("echo \"a*\"*"));
        // Single-quoted and escaped text is escaped as well, and `?` / `[` too.
        Assert.Equal(GlobSplat("a\\?\\[b*"), PsEmitter.Transpile("echo 'a?'\\[b*"));
    }

    [Fact]
    public void Transpile_GlobWordWithVariable_QuotedValueIsEscapedUnquotedValueIsActive()
    {
        // bash: an unquoted $d keeps its glob characters live; "$d" never globs.
        var unquoted = PsEmitter.Transpile("echo $d/*");
        var quoted = PsEmitter.Transpile("echo \"$d\"/*");

        Assert.Contains("ConvertTo-BashGlob ('' + ($env:d) + '/*')", unquoted);
        Assert.Contains("ConvertTo-BashGlob ('' + ([PsBash.Cmdlets.BashGlobText]::Escape(\"$env:d\")) + '/*')", quoted);
    }

    [Fact]
    public void Transpile_GlobWordWithTilde_ReaddsTheSlashTheParserConsumed()
    {
        var result = PsEmitter.Transpile("echo ~/x*");

        Assert.Contains("([string]$HOME).Replace('\\','/')", result);
        Assert.Contains("+ '/x*')", result);
    }

    [Fact]
    public void Transpile_ExtglobWord_KeepsItsOldEmission()
    {
        // extglob (+(a|b)) is not implemented by the engine: it must not reach ConvertTo-BashGlob.
        Assert.DoesNotContain("ConvertTo-BashGlob", PsEmitter.Transpile("echo +(*.py|*.js)"));
    }

    [Fact]
    public void Transpile_ArrayAssignmentWithGlobElement_FlattensIntoOneArray()
    {
        // The comma form cannot splice an expansion that yields zero or many words.
        Assert.Equal("$arr = @(\"a\"; @(ConvertTo-BashGlob '*.txt'); \"b\")", PsEmitter.Transpile("arr=(a *.txt b)"));
        // No glob element: the historical comma form is unchanged.
        Assert.Equal("$arr = @(\"a\",\"b\")", PsEmitter.Transpile("arr=(a b)"));
    }

    [Fact]
    public void Transpile_BraceExpansionWithGlob_ExpandsBracesThenGlobsEachWord()
    {
        Assert.Equal(
            "& { $__bashsplat0 = @(@('sub/a*','sub/b*') | ConvertTo-BashGlob); Invoke-BashEcho @__bashsplat0 }",
            PsEmitter.Transpile("echo sub/{a,b}*"));
        // No glob character: the plain brace array is unchanged.
        Assert.Equal("Invoke-BashEcho @('a','b')", PsEmitter.Transpile("echo {a,b}"));
    }

    [Fact]
    public void Transpile_SetDashDashWithGlob_BuildsAFlatPositionalList()
    {
        Assert.Equal("$global:BashPositional = @('a'; @(ConvertTo-BashGlob '*.txt'))",
            PsEmitter.Transpile("set -- a *.txt"));
        // No expansion in the list: unchanged comma form.
        Assert.Equal("$global:BashPositional = @('a', 'b')", PsEmitter.Transpile("set -- a b"));
    }

    [Fact]
    public void Transpile_GlobWordInPipeStage_ForwardsThePipeInput()
    {
        // The splat hoist wraps the stage in a script block, which would swallow the pipe.
        var result = PsEmitter.Transpile("echo hi | tee *.log");

        Assert.Contains("$input | Invoke-BashTee @__bashsplat0", result);
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
    [InlineData("[[:alpha:]]")]
    [InlineData("[[:digit:][:upper:]]")]
    [InlineData("[![:xdigit:]]")]
    [InlineData("[[:blank:]]")]
    [InlineData("[[:space:]]")]
    public void Transpile_PosixGlobCharClass_ReachesTheGlobEngineVerbatim(string pattern)
    {
        // The engine (BashGlob / LsGlob) understands [:class:] itself, so the class is NOT rewritten
        // into a PowerShell wildcard range any more.
        var result = PsEmitter.Emit(Assert.IsType<Command.Simple>(BashParser.Parse($"echo {pattern}")));

        Assert.Equal(GlobSplat(pattern), result);
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
    public void Transpile_ForInGlobCharClass_EmitsConvertToBashGlob()
    {
        var result = PsEmitter.Transpile("for f in [abc]*.txt; do cat $f; done");
        Assert.Equal("$__psbash_iter = 0; foreach ($f in @(ConvertTo-BashGlob '[abc]*.txt')) { if (++$__psbash_iter -gt ($env:PSBASH_MAX_ITERATIONS ?? 100000)) { throw \"ps-bash: loop iteration limit exceeded ($(($env:PSBASH_MAX_ITERATIONS ?? 100000)))\" }; Invoke-BashCat $f }", result);
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
        Assert.Equal("Invoke-BashSort '-u' (Invoke-ProcessSubPipeline { Invoke-BashCat foo })", result);
    }

    [Fact]
    public void Transpile_HeadWithInputProcessSub_RoutesToPipelineObjectPath()
    {
        var result = PsEmitter.Transpile("head -n 1 <(seq 1 10)");
        Assert.Equal("Invoke-BashHead '-n' 1 (Invoke-ProcessSubPipeline { Invoke-BashSeq 1 10 })", result);
    }

    [Fact]
    public void Transpile_TailWithInputProcessSub_RoutesToPipelineObjectPath()
    {
        var result = PsEmitter.Transpile("tail -n 1 <(seq 1 10)");
        Assert.Equal("Invoke-BashTail '-n' 1 (Invoke-ProcessSubPipeline { Invoke-BashSeq 1 10 })", result);
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
        Assert.Equal("Invoke-BashWc '-l' (Invoke-ProcessSub { Invoke-BashSeq 1 100 })", result);
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
        Assert.Equal("Invoke-BashGrep '-f' (Invoke-ProcessSub { Invoke-BashCat patterns.txt }) data.txt", result);
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
            "Invoke-ProcessSubSource { Invoke-BashCat config.env | Invoke-BashGrep '-v' '^#' }",
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

        // The quoted $( ) is emitted as a bare value, never inside a PS "…" string, so the
        // inner "hi there" is not nested in anything (see TryEmitJoinedWord: PowerShell finds
        // a string's $( ) end by naive paren counting, so command text never goes in one).
        Assert.Equal(
            "Invoke-BashEcho $((@(Invoke-BashEcho \"hi there\" | ConvertTo-BashCapture) -join [string][char]10) -replace '(\\r?\\n)+$','')",
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

        Assert.Equal("@\"\nhello foo\nbar\n\n\"@ | Emit-BashLine | Invoke-BashGrep '-i' foo", result);
    }

    // Bug: a heredoc followed by `| && ;` on the SAME line had its body computed
    // from the parser's token cursor, so the operator tail landed in the body AND
    // was parsed as code. The lexer already knows the exact body span; these pin
    // the three bash-oracle repros for that.
    [Fact]
    public void Transpile_HeredocFollowedByPipe_TailIsNotInBody()
    {
        var result = PsEmitter.Transpile("cat <<EOF | tr a-z A-Z\nlower $((2+3))\nEOF");

        // The here-string body is only the heredoc body line; the `| tr a-z A-Z`
        // tail must be a pipe to Invoke-BashTr, never text inside the body.
        Assert.Contains("@\"\nlower $(Invoke-BashArith '2+3')\n\n\"@", result);
        Assert.DoesNotContain("| tr a-z A-Z\\n", result);
        Assert.EndsWith("Invoke-BashTr a-z A-Z", result);
    }

    [Fact]
    public void Transpile_HeredocFollowedByAndIf_TailIsNotInBody()
    {
        var result = PsEmitter.Transpile("cat <<EOF && echo ok\nhello\nEOF");

        Assert.Equal("@\"\nhello\n\n\"@ | Emit-BashLine | Invoke-BashCat && Invoke-BashEcho ok", result);
    }

    [Fact]
    public void Transpile_TwoHeredocsOnSameLineSemicolon_BothBodiesCorrect()
    {
        var result = PsEmitter.Transpile("cat <<A; cat <<B\nfirst\nA\nsecond\nB");

        Assert.Contains("@\"\nfirst\n\n\"@", result);
        Assert.Contains("@\"\nsecond\n\n\"@", result);
        Assert.DoesNotContain("second\nB", result);
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
        Assert.Equal("Invoke-BashEcho \"a,b,c\" | Invoke-BashAwk '-F,' '{print $1, $3}'", result);
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
            "@(ConvertTo-BashWords $env:FOO); " +
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
        Assert.Equal("cmd 2>&1 | Invoke-BashRedirect -ErrorPath err.log", result);
    }

    // The fd model (AppendRedirectTail): bash applies redirects left to right, per fd, last wins.
    // PowerShell rejects redirecting a stream twice, so a repeated redirect was a parse error for
    // the whole script; superseded file targets are still opened (created / truncated) by bash.
    [Theory]
    [InlineData("cmd >/dev/null >/dev/null", "cmd >$null")]
    [InlineData("cmd 2>/dev/null 2>/dev/null", "cmd 2>$null")]
    [InlineData("cmd &>/dev/null 2>&1", "cmd >$null 2>&1")]
    [InlineData("cmd >a >b", "cmd | Invoke-BashRedirect -Path b -Truncate @(& { $args } a)")]
    [InlineData("cmd >p >q >r", "cmd | Invoke-BashRedirect -Path r -Truncate @(& { $args } p q)")]
    [InlineData("cmd >>s >u", "cmd | Invoke-BashRedirect -Path u -Touch @(& { $args } s)")]
    [InlineData("cmd 2>f1 2>f2", "cmd 2>&1 | Invoke-BashRedirect -ErrorPath f2 -Truncate @(& { $args } f1)")]
    [InlineData("cmd >/dev/null >f", "cmd | Invoke-BashRedirect -Path f")]
    [InlineData("cmd >f >/dev/null", "cmd | Invoke-BashRedirect -Path $null -Truncate @(& { $args } f)")]
    [InlineData("cmd 2>>e", "cmd 2>&1 | Invoke-BashRedirect -ErrorPath e -ErrorAppend")]
    [InlineData("cmd >o 2>e", "cmd 2>&1 | Invoke-BashRedirect -Path o -ErrorPath e")]
    [InlineData("cmd >o 2>/dev/null", "cmd 2>$null | Invoke-BashRedirect -Path o")]
    [InlineData("cmd >/dev/null 2>e", "cmd 2>&1 | Invoke-BashRedirect -Path $null -ErrorPath e")]
    // `2>&1 >o`: stderr dup'd to the ORIGINAL stdout before stdout moves — it flows on, not into o.
    [InlineData("cmd 2>&1 >o", "cmd 2>&1 | Invoke-BashRedirect -Path o -PassErrors")]
    [InlineData("cmd >o 2>&1", "cmd 2>&1 | Invoke-BashRedirect -Path o")]
    public void Transpile_RedirectFdModel_LastWinsPerFd(string bash, string expected)
        => Assert.Equal(expected, PsEmitter.Transpile(bash));

    [Fact]
    public void Transpile_RedirectFdModel_UserFd_KeepsLegacyEmission()
    {
        // fd 3 is outside the model: the tail falls back to the pre-model emission unchanged.
        Assert.Equal("cmd 3>f", PsEmitter.Transpile("cmd 3>f"));
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

        Assert.Contains("'-I{}'", result);
    }

    // xargs is on OrderedArgCommands: xargs's own flags AND the flags of the command it runs
    // are all single-quoted, so none can prefix-match a cmdlet/common parameter (`-a` of
    // `basename -a` matched Invoke-BashXargs's -Arguments: "Missing an argument ... 'Arguments'").
    [Theory]
    [InlineData("xargs -0 basename -a", "Invoke-BashXargs '-0' basename '-a'")]
    [InlineData("xargs grep -i foo", "Invoke-BashXargs grep '-i' foo")]
    [InlineData("xargs -n1 echo -e x", "Invoke-BashXargs '-n1' echo '-e' x")]
    [InlineData("xargs -P2 -n1 ls -d", "Invoke-BashXargs '-P2' '-n1' ls '-d'")]
    [InlineData("xargs -I {} cp -v {} dst/", "Invoke-BashXargs '-I' \"{}\" cp '-v' \"{}\" dst/")]
    [InlineData("xargs -i echo x", "Invoke-BashXargs '-i' echo x")]
    [InlineData("xargs -d , -n 1 echo", "Invoke-BashXargs '-d' ',' '-n' 1 echo")]
    // Other wrappers that run a foreign command line: the inner argv is quoted too.
    [InlineData("time echo -e a", "Invoke-BashTime echo '-e' a")]
    [InlineData("env FOO=1 grep -i x f", "Invoke-BashEnv FOO=1 grep '-i' x f")]
    [InlineData("env basename -a a/b", "Invoke-BashEnv basename '-a' a/b")]
    [InlineData("find . -exec grep -i x {} \\;", "Invoke-BashFind . '-exec' grep '-i' x \"{}\" `;")]
    [InlineData("find . -exec basename -a {} +", "Invoke-BashFind . '-exec' basename '-a' \"{}\" +")]
    public void Transpile_XargsDashLiterals_AreAllSingleQuoted(string bash, string expected)
    {
        Assert.Contains(expected, PsEmitter.Transpile(bash));
    }

    [Fact]
    public void Transpile_HeadWithHeredoc_ParsesCorrectly()
    {
        // Issue 7: head -n 2 << EOF was misparsed as head -n 2<<EOF
        // (2 was reclassified as IoNumber). Now 2 stays as a word arg.
        var result = PsEmitter.Transpile("head -n 2 << EOF\nline1\nline2\nline3\nEOF");

        Assert.Contains("Invoke-BashHead '-n' 2", result);
        Assert.Contains("line1", result);
    }

    [Fact]
    public void Transpile_WcHeredoc_EmitsHereStringPipedToWc()
    {
        // Issue 8: wc -l with heredoc input
        var result = PsEmitter.Transpile("wc -l << EOF\nhello\nworld\nEOF");

        Assert.Contains("Invoke-BashWc '-l'", result);
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
        Assert.Contains("'-F,'", result);
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
        Assert.Equal("$ErrorActionPreference = 'Stop'; $global:__BashErrexit = $true; if (-not (Test-Path variable:global:__BashErrexitSuppress)) { $global:__BashErrexitSuppress = 0 }; Set-StrictMode -Version Latest", result);
    }

    [Fact]
    public void Transpile_SetOErrexit_EmitsErrorActionStop()
    {
        var result = PsEmitter.Transpile("set -o errexit");
        Assert.Equal("$ErrorActionPreference = 'Stop'; $global:__BashErrexit = $true; if (-not (Test-Path variable:global:__BashErrexitSuppress)) { $global:__BashErrexitSuppress = 0 }", result);
    }

    // errexit: a failing command at statement position must end a `set -e` script. Only the
    // `false` builtin and PowerShell-terminating errors did — `set -e; cat /nofile; echo after`
    // printed "after" and exited 0.

    [Theory]
    [InlineData("set -e", true)]
    [InlineData("set -euo pipefail", true)]
    [InlineData("set -o errexit", true)]
    [InlineData("set -o pipefail -e", true)]
    [InlineData("eval 'set -e; cat x'", true)]
    [InlineData("set -o pipefail", false)]
    [InlineData("set -u; echo reset -e", false)]
    [InlineData("echo set", false)]
    public void EnablesErrexit_DetectsSetE(string bash, bool expected)
        => Assert.Equal(expected, PsEmitter.EnablesErrexit(bash));

    [Fact]
    public void Transpile_WithoutSetE_EmitsNoErrexitChecks()
    {
        // Scripts that never use set -e must emit exactly what they always did.
        var result = PsEmitter.Transpile("cat /nofile; if f; then g; fi; a && b");
        Assert.DoesNotContain("__BashErrexit", result);
    }

    [Fact]
    public void Transpile_SetE_ChecksStatementButNotExemptOperands()
    {
        var result = PsEmitter.Transpile("set -e; cat /nofile; echo after")!;
        var check = PsBuild.ErrexitCheck("exit $global:LASTEXITCODE");
        Assert.Contains("Invoke-BashCat /nofile; " + check + "; Invoke-BashEcho after", result);

        // `a || b`: a is exempt; the script leaves only when the FINAL command ran and failed.
        var chain = PsEmitter.Transpile("set -e; cat /nofile || echo fallback")!;
        Assert.Contains("$global:__BashErrexitTail -ne 0", chain);
        Assert.DoesNotContain("Invoke-BashCat /nofile; " + check, chain);
    }

    [Fact]
    public void Transpile_SetE_ConditionRunsSuppressed_AndUntakenIfIsStatusZero()
    {
        // A function called from a condition keeps going past its own failures (bash ignores -e
        // there), and an `if` whose branch is not taken has status 0.
        var result = PsEmitter.Transpile("set -e; if f; then echo y; fi")!;
        Assert.Contains("$global:__BashErrexitSuppress++; try {", result);
        Assert.Contains("else { $global:LASTEXITCODE = 0 }", result);
    }

    [Fact]
    public void Transpile_SetE_ExportOfCommandSubIsNotChecked()
    {
        // bash: `export X=$(false)` has export's status (0); a bare `X=$(false)` fails.
        var check = PsBuild.ErrexitCheck("exit $global:LASTEXITCODE");
        Assert.DoesNotContain(check, PsEmitter.Transpile("set -e; export X=$(false)")!.Replace(
            PsEmitter.Transpile("set -e")!, ""));
        Assert.Contains(check, PsEmitter.Transpile("set -e; X=$(false)")!);
    }

    [Fact]
    public void Transpile_SetE_InsideSubshell_LeavesOnlyTheSubshell()
    {
        var result = PsEmitter.Transpile("set -e; (cat /nofile; echo in); echo after")!;
        Assert.Contains(PsBuild.ErrexitCheck("return"), result);
        Assert.StartsWith("& { try { Push-Location;", result[(result.IndexOf("& { try", StringComparison.Ordinal))..]);
    }

    [Fact]
    public void Transpile_SetPlusE_TurnsErrexitOff()
        => Assert.Equal("$ErrorActionPreference = 'Continue'; $global:__BashErrexit = $false",
            PsEmitter.Transpile("set +e"));

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
        Assert.Equal("$ErrorActionPreference = 'Stop'; $global:__BashErrexit = $true; if (-not (Test-Path variable:global:__BashErrexitSuppress)) { $global:__BashErrexitSuppress = 0 }; Set-StrictMode -Version Latest", result);
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
                + "-Fallback { Invoke-BashCat file | Invoke-BashNl '-ba' }",
            result);
    }

    [Fact]
    public void Transpile_PipeToColumnWithFlag_EmitsInvokeBashColumn()
    {
        var result = PsEmitter.Transpile("cat data.csv | column -t");

        Assert.Equal("Invoke-BashCat data.csv | Invoke-BashColumn '-t'", result);
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

        Assert.Equal("Invoke-BashHead '-n' 5 file.txt", result);
    }

    [Fact]
    public void Transpile_StandaloneWc_EmitsInvokeBashWc()
    {
        var result = PsEmitter.Transpile("wc -l file.txt");

        Assert.Equal("Invoke-BashWc '-l' file.txt", result);
    }

    [Fact]
    public void Transpile_StandaloneFind_EmitsInvokeBashFind()
    {
        var result = PsEmitter.Transpile("find . -name '*.txt'");

        Assert.Equal("Invoke-BashFind . '-name' '*.txt'", result);
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
        Assert.Equal("Invoke-BashEcho \"$($env:UNSET_VAR ? $env:UNSET_VAR : 'fallback')\"", result);
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
        // type is on OrderedArgCommands: flags arrive quoted, so `-p` and `-P` keep their case.
        Assert.Equal("Invoke-BashType '-t' echo", PsEmitter.Transpile("type -t echo"));
        Assert.Equal("Invoke-BashType '-P' cd", PsEmitter.Transpile("type -P cd"));
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

        Assert.Contains("Invoke-BashEcho line1 | Invoke-BashRedirect -Path \"$($env:OS -eq 'Windows_NT' ? $env:TEMP : '/tmp')/test.txt\"", result);
        Assert.Contains("Invoke-BashEcho append | Invoke-BashRedirect -Path \"$($env:OS -eq 'Windows_NT' ? $env:TEMP : '/tmp')/test.txt\" -Append", result);
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
        Assert.Equal("Invoke-BashCat file.txt | Invoke-BashPaste '-d,' '-s'", result);
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
        Assert.Equal("Invoke-BashEcho \"script: $(if ($global:BashPositional0) { $global:BashPositional0 } else { 'bash' })\"", result);
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
        Assert.Contains("Invoke-BashSort '-t:' '-k2'", result);
    }

    [Fact]
    public void Transpile_AwkWithColonDelimiter_QuotesColonFlag()
    {
        var result = PsEmitter.Transpile("cat file | awk -F: '{print}'");
        Assert.Contains("Invoke-BashAwk '-F:'", result);
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
        Assert.Contains("Invoke-BashFind src '-name' '*.cs' '-exec' wc '-l' \"{}\" +", result);
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
        Assert.Equal("Invoke-BashBash '-c' \"echo hello\"", result);
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
        Assert.Equal("Invoke-BashBash '--version'", result);
    }

    [Fact]
    public void Transpile_BashPipeToGrep_EmitsMappedPipeline()
    {
        var result = PsEmitter.Transpile("bash -c \"echo hello\" | grep hello");
        Assert.Equal("Invoke-BashBash '-c' \"echo hello\" | Invoke-BashGrep hello", result);
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

        Assert.Contains("$__bashsplat0 = @(ConvertTo-BashWords $env:x)", result);
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
    public void Transpile_ExitWithUnquotedVar_DoesNotSplat()
    {
        // `exit` is a PowerShell STATEMENT keyword, not a command, so
        // `exit @__bashsplat0` is a hard parse error that poisons the whole
        // emitted file. Found by the real-world .sh corpus sweep on
        // scripts/test.sh (`exit $test_exit`) and scripts/skip-report.sh.
        var result = PsEmitter.Transpile("exit $code");

        Assert.DoesNotContain("@__bashsplat", result);
        Assert.StartsWith("exit $(", result);
        // Empty unquoted operand is elided in bash, so `exit` keeps $? —
        // modeled as the $global:LASTEXITCODE fallback.
        Assert.Contains("$global:LASTEXITCODE", result);
    }

    [Fact]
    public void Transpile_ExitWithUnquotedVar_EmitsParseableSplitValue()
    {
        var result = PsEmitter.Transpile("exit $code");

        // The word-split array is still built (bash word-splitting semantics),
        // it is just consumed as a VALUE rather than splatted.
        Assert.Contains("ConvertTo-BashWords $env:code", result);
        Assert.Contains("$__bashexit[0]", result);
    }

    // ---- `:`-form empty test / statement keyword in a &&-|| chain ----------

    [Theory]
    // The `:` forms act on unset OR EMPTY, the colon-less forms on unset ONLY.
    // `$env:X` is $null when unset but "" when set-to-empty, so `??` is EXACT for
    // the colon-less forms and was WRONG for the `:` forms.
    [InlineData("echo ${V:-d}", "($env:V ? $env:V : \"d\")")]
    [InlineData("echo ${V-d}", "($env:V ?? \"d\")")]
    [InlineData("echo ${V:=d}", "($env:V ? $env:V : ($env:V = \"d\"))")]
    [InlineData("echo ${V=d}", "($env:V ?? ($env:V = \"d\"))")]
    public void Transpile_ParamExpansionColonForm_TestsEmptyNotJustUnset(
        string bash, string expected)
        => Assert.Equal($"Invoke-BashEcho {expected}", PsEmitter.Transpile(bash));

    [Theory]
    // PowerShell's &&/|| chain wants a COMMAND on the right; a statement keyword
    // there is parsed as a command NAME. `&& break` printed "The term 'break' is
    // not recognized" and the loop ran on, and `|| exit 1` simply DID NOT EXIT —
    // the script sailed past the guard it was written to enforce.
    [InlineData("cmd || exit 1", "-ne 0 })) { exit 1 }")]
    [InlineData("cmd && continue", "-eq 0 })) { continue }")]
    [InlineData("cmd && break", "-eq 0 })) { break }")]
    [InlineData("cmd || return", "-ne 0 })) { return }")]
    public void Transpile_ChainEndingInStatementKeyword_RewritesToIf(
        string bash, string expectedTail)
    {
        var result = PsEmitter.Transpile(bash);

        Assert.StartsWith("if ((& { [void](cmd);", result);
        Assert.EndsWith(expectedTail, result);
    }

    [Fact]
    public void Transpile_ChainOfOrdinaryCommands_KeepsNativeChainOperator()
    {
        // Only a trailing statement KEYWORD forces the rewrite.
        Assert.Equal("a && b", PsEmitter.Transpile("a && b"));
    }

    [Fact]
    public void Transpile_LongerChainEndingInKeyword_ConditionIsTheWholePrefix()
    {
        var result = PsEmitter.Transpile("a && b || exit 1");

        Assert.Equal("if ((& { [void](a && b); $global:LASTEXITCODE -ne 0 })) { exit 1 }", result);
    }

    // ---- subshell exit scoping / glob class with an expansion --------------

    [Fact]
    public void Transpile_ExitInsideSubshell_IsScopedNotProcessExit()
    {
        // bash: `exit` in `( … )` leaves only the SUBSHELL and sets $? in the
        // parent, so `(exit 7); echo $?` prints 7 and keeps running. Emitted as a
        // bare PowerShell `exit` it terminated the WHOLE shell and the `echo $?`
        // never ran — found by the oracle differential sweep.
        var result = PsEmitter.Transpile("(exit 7); echo $?");

        Assert.StartsWith("& { try { Push-Location;", result);
        Assert.Contains("$global:LASTEXITCODE = 7; return", result);
        Assert.EndsWith("Invoke-BashEcho $global:LASTEXITCODE", result);
    }

    [Fact]
    public void Transpile_ExitOutsideSubshell_StaysProcessExit()
    {
        Assert.Equal("exit 5", PsEmitter.Transpile("exit 5"));
    }

    [Fact]
    public void Transpile_SubshellWithoutExit_NotWrappedInScriptBlock()
    {
        // The script block is only needed to give the scoped `return` something to
        // return from; a plain subshell keeps its cheaper emission.
        Assert.Equal("try { Push-Location; Invoke-BashEcho a } " + SubshellPop,
            PsEmitter.Transpile("(echo a)"));
    }

    [Fact]
    public void Transpile_GlobClassContainingExpansion_PreservesTheVariable()
    {
        // `[$x]` was swallowed whole into a GlobPart, so the emitted pattern text
        // `[$x]` made PowerShell read `$x` as ITS OWN (undefined) variable and
        // `x="a b"; echo [$x]` printed `[]` — total, silent data loss.
        Assert.Equal("Invoke-BashEcho [$env:x]", PsEmitter.Transpile("echo [$x]"));
        Assert.Equal("Invoke-BashEcho x[$env:y]z", PsEmitter.Transpile("echo x[$y]z"));
    }

    [Fact]
    public void Transpile_StaticGlobClass_StillAGlob()
    {
        // A class with no expansion keeps its glob handling.
        Assert.Equal(GlobSplat("[abc]"), PsEmitter.Transpile("echo [abc]"));
        Assert.Equal(GlobSplat("[[:digit:]]"), PsEmitter.Transpile("echo [[:digit:]]"));
    }

    // ---- redirect / test-operator / unmodelable-expansion degradations -----

    [Fact]
    public void Transpile_RedundantStderrMerge_EmittedOnce()
    {
        // `&>` already emits the 2>&1 merge, so the redundant-but-legal bash
        // `cmd &>/dev/null 2>&1` emitted `>$null 2>&1 2>&1` — PowerShell rejects
        // redirecting a stream twice ("The error stream ... already redirected").
        Assert.Equal("cmd >$null 2>&1", PsEmitter.Transpile("cmd &>/dev/null 2>&1"));
    }

    [Fact]
    public void Transpile_StdoutFileRedirectWithStderrMerge_KeepsBoth()
    {
        // The dedupe must drop ONLY a bare duplicate merge, never a fragment that
        // also carries a stdout redirect.
        Assert.Equal("cmd >$null 2>&1", PsEmitter.Transpile("cmd >/dev/null 2>&1"));
    }

    [Theory]
    // Oracle-verified: inside [[ ]] both == and != GLOB-match, and quoting is
    // per-SEGMENT — bash drops the quotes and the quoted chars stay literal.
    [InlineData("[[ \"$x\" != \"http\"* ]]", "-notlike 'http*'")]
    [InlineData("[[ \"$x\" == \"http\"* ]]", "-like 'http*'")]
    [InlineData("[[ \"$x\" == http* ]]", "-like 'http*'")]
    // A fully-quoted pattern is LITERAL: `*` must be escaped, not left active.
    [InlineData("[[ \"$x\" == \"a*b\" ]]", "-like 'a`*b'")]
    public void Transpile_ExtendedTestGlob_NormalizesPatternPerSegment(
        string bash, string expected)
        => Assert.Contains(expected, PsEmitter.Transpile(bash));

    [Fact]
    public void Transpile_UnmodelableExpansion_DegradesToEmptyStringNotBrokenEnvRef()
    {
        // ZSH-only syntax in a dual-shell script's dead branch (git-completion.bash
        // guards it with [[ -n ${ZSH_VERSION-} ]]). bash PARSES the file fine, so
        // rejecting it would be stricter than the oracle — but the bare `$env:` we
        // used to emit is not valid PowerShell and killed the whole file's parse.
        var result = PsEmitter.Transpile("unset ${(M)${(k)parameters[@]}:#pat*}");

        Assert.DoesNotContain("$env:", result);
    }

    // ---- nested-context quoting / general-path arg quoting -----------------

    [Fact]
    public void Transpile_ParamExpansionInsideCommandSubInString_UsesInertSingleQuotes()
    {
        // A command substitution resets `inDoubleQuote` for its body but does NOT
        // leave the enclosing double-quoted string. Keying the quote character on
        // inDoubleQuote alone emitted `… ? "--dir=$env:x" : "" …` inside
        // `"$( … )"`, and the empty `""` closed the OUTER string ("The string is
        // missing the terminator") — it broke git-completion.bash.
        var result = PsEmitter.Transpile("echo \"$(git ${x:+--dir=$x} rev-parse)\"");

        // The quoted $( ) is a bare value now, so the body is not inside any string and the
        // `""` alternative is safe; what must hold is that no outer "$( … )" string exists.
        Assert.StartsWith("Invoke-BashEcho $((@(git ($env:x ? ", result);
        Assert.DoesNotContain("\"$(", result);
    }

    [Fact]
    public void Transpile_ParamExpansionUnnested_KeepsDoubleQuotedForm()
    {
        // Un-nested, the readable double-quoted shape is retained.
        Assert.Equal("Invoke-BashEcho ($env:x ? \"alt\" : \"\")",
            PsEmitter.Transpile("echo ${x:+alt}"));
    }

    [Fact]
    public void Transpile_ExternalCommandCommaFlag_IsQuoted()
    {
        // `,` is PowerShell's array separator: `cc -Wp,-v` emitted "Missing
        // argument in parameter list". The mapped-cmdlet path already quoted
        // these; the general/external path did not (Loom's build script).
        Assert.Equal("cc \"-Wp,-v\" -x c++ -", PsEmitter.Transpile("cc -Wp,-v -x c++ -"));
    }

    [Fact]
    public void Transpile_ExternalCommandColonFlag_NotQuoted()
    {
        // Deliberately narrower than the passthrough rule: `-Foo:bar` PARSES, so
        // quoting it would change named-parameter binding rather than fix a parse.
        Assert.Equal("cmd -Foo:bar", PsEmitter.Transpile("cmd -Foo:bar"));
    }

    [Fact]
    public void Transpile_ExternalCommandAlreadyQuotedFlag_NotDoubleWrapped()
    {
        // Re-wrapping an already-quoted arg is the `-F","` -> `"-F","` array-split trap.
        Assert.Equal("cmd \"-a,b\"", PsEmitter.Transpile("cmd \"-a,b\""));
    }

    // ---- bare `,` literal in an ARGUMENT word (PowerShell array separator) ----

    [Theory]
    [InlineData("sed -n 725,750p f", "Invoke-BashSed '-n' '725,750p' f")]
    [InlineData("cut -f1,3 f", "Invoke-BashCut -f1,3 f")]   // placeholder, asserted below
    [InlineData("echo a,b", "Invoke-BashEcho 'a,b'")]
    [InlineData("printf '%s\\n' x,y", "Invoke-BashPrintf '%s\\n' 'x,y'")]
    [InlineData("git log --format=%h,%s", "git log \"--format=%h,%s\"")]
    [InlineData("cmd 1,2 3,4", "cmd '1,2' '3,4'")]
    public void Transpile_UnquotedCommaLiteralArg_IsQuotedNotArray(string bash, string expected)
    {
        var result = PsEmitter.Transpile(bash);
        if (bash.StartsWith("cut"))
        {
            // cut is on OrderedArgCommands: every dash literal is single-quoted.
            Assert.Equal("Invoke-BashCut '-f1,3' f", result);
            return;
        }
        Assert.Equal(expected, result);
    }

    // A glob word that also carries a bare `,` was emitted bare: PowerShell bound it as an
    // ARRAY (`*.c,x` -> @('*.c','x')). Bash keeps the comma literal inside the pattern.
    [Theory]
    [InlineData("ls *.c,x", "*.c,x", "Invoke-BashLs")]
    [InlineData("echo a*,b", "a*,b", "Invoke-BashEcho")]
    [InlineData("cat f*,g", "f*,g", "Invoke-BashCat")]
    [InlineData("cat f?,g[12]", "f?,g[12]", "Invoke-BashCat")]
    public void Transpile_GlobWordWithCommaLiteral_IsOneQuotedPattern(
        string bash, string pattern, string command)
    {
        // The comma stays inside ONE single-quoted pattern string (an array in PowerShell otherwise).
        Assert.Equal(GlobSplat(pattern, command), PsEmitter.Transpile(bash));
    }

    [Fact]
    public void Transpile_CommaWordMixedWithVariable_FlattensToOneString()
    {
        var result = PsEmitter.Transpile("cmd $x,y a,\"b\"");
        Assert.DoesNotContain(" $env:x,y", result);
        Assert.Contains("\"$env:x,y\"", result);
        Assert.Contains("\"a,b\"", result);
    }

    [Fact]
    public void Transpile_CommaLiteralInSplatPath_IsQuoted()
    {
        var result = PsEmitter.Transpile("cmd $x 1,2");
        Assert.Contains("'1,2'", result);
    }

    [Fact]
    public void Transpile_BraceExpansionAndQuotedComma_Unchanged()
    {
        Assert.Equal("Invoke-BashEcho @('a','b')", PsEmitter.Transpile("echo {a,b}"));
        Assert.Equal("Invoke-BashEcho \"a,b\"", PsEmitter.Transpile("echo \"a,b\""));
        Assert.Equal("Invoke-BashEcho 'a,b'", PsEmitter.Transpile("echo 'a,b'"));
    }

    [Theory]
    [InlineData("echo hi > a,b", "Invoke-BashEcho hi | Invoke-BashRedirect -Path 'a,b'")]
    [InlineData("echo /tmp/a,b", "Invoke-BashEcho \"$($env:OS -eq 'Windows_NT' ? $env:TEMP : '/tmp')/a,b\"")]
    [InlineData("echo a\\,b", "Invoke-BashEcho a`,b")]
    [InlineData("x=a,b", "$env:x = \"a,b\"")]
    [InlineData("arr=(a,b c)", "$arr = @(\"a,b\",\"c\")")]
    [InlineData("for i in a,b c; do :; done", null)]
    public void Transpile_CommaInOtherContexts_StaysOneWord(string bash, string? expected)
    {
        var result = PsEmitter.Transpile(bash);
        if (expected is not null)
            Assert.Equal(expected, result);
        else
            Assert.Contains("'a,b','c'", result);
    }

    // ---- positional-parameter slices ${@:off[:len]} ------------------------

    [Theory]
    [InlineData("echo \"${@: -1}\"")]
    [InlineData("echo \"${@:1}\"")]
    [InlineData("echo \"${@:2}\"")]
    [InlineData("echo \"${@:1:2}\"")]
    [InlineData("echo \"${*: -2}\"")]
    public void Transpile_PositionalSlice_DoesNotEmitEmptyEnvReference(string bash)
    {
        // `@`/`*` are not var chars, so the braced-var name read as EMPTY and the
        // emitter produced the bare `$env:` — not valid PowerShell, and it broke
        // the whole file's parse. `${@: -1}` is how zoxide's shell hook reads its
        // target directory, so this hit every copy of that script.
        var result = PsEmitter.Transpile(bash);

        Assert.DoesNotContain("$env:\"", result);
        Assert.DoesNotContain("$env: ", result);
        Assert.Contains("$global:BashPositional", result);
    }

    [Theory]
    // Oracle-verified index mapping: bash counts positionals from $1, but the
    // emitted array's index 0 already holds $1, so a POSITIVE offset shifts down
    // by one while a NEGATIVE offset (counting from the end) passes through.
    [InlineData("echo \"${@:1}\"", "$__psbO = 0;")]
    [InlineData("echo \"${@:2}\"", "$__psbO = 1;")]
    [InlineData("echo \"${@: -1}\"", "$__psbO = -1;")]
    [InlineData("echo \"${@: -2}\"", "$__psbO = -2;")]
    public void Transpile_PositionalSlice_MapsOffsetToArrayIndex(
        string bash, string expectedOffset)
        => Assert.Contains(expectedOffset, PsEmitter.Transpile(bash));

    [Fact]
    public void Transpile_BarePositionalAll_UnaffectedBySliceParsing()
    {
        // The suffix-less ${@} must keep its dedicated expansion.
        Assert.Equal(
            "Invoke-BashEcho \"$(if ($global:BashPositional) { $global:BashPositional } else { $args })\"",
            PsEmitter.Transpile("echo \"${@}\""));
    }

    // ---- composed command words / subshell redirect / file-compare tests ---

    [Theory]
    // A command word that is not a bare NAME needs the `&` call operator AND must
    // be one PowerShell token. Every multi-part form below used to emit bare
    // concatenated parts, which PowerShell read as an expression — unparseable.
    [InlineData("$gobin/go help", "& \"$env:gobin/go\" help")]
    [InlineData("$dir/$name x", "& \"$env:dir/$env:name\" x")]
    [InlineData("\"$d\"/go v", "& \"$env:d/go\" v")]
    [InlineData("~/bin/foo a", "& \"$HOME\\bin/foo\" a")]
    public void Transpile_ComposedCommandWord_EmitsCallOperatorAndSingleToken(
        string bash, string expected)
        => Assert.Equal(expected, PsEmitter.Transpile(bash));

    [Theory]
    // A word made only of literals stays a bare command name, as in bash.
    [InlineData("echo hi", "Invoke-BashEcho hi")]
    [InlineData("ls -la", "Invoke-BashLs '-la'")]
    public void Transpile_LiteralCommandWord_StaysBareName(string bash, string expected)
        => Assert.Equal(expected, PsEmitter.Transpile(bash));

    [Fact]
    public void Transpile_SubshellWithStdoutRedirect_WrapsBodyAsPipelineHead()
    {
        // The subshell body is `try { … } finally { … }` — a statement, which
        // cannot head the `| Invoke-BashRedirect` pipe. Hit Go's mkerrors.sh.
        var result = PsEmitter.Transpile("(echo a; echo b) > f");

        Assert.StartsWith("& { try { Push-Location;", result);
        Assert.Contains("} | Invoke-BashRedirect -Path f", result);
    }

    [Fact]
    public void Transpile_SubshellWithoutRedirect_KeepsUnwrappedFastPath()
    {
        var result = PsEmitter.Transpile("(echo a; echo b)");

        Assert.DoesNotContain("& { try {", result);
    }

    [Theory]
    [InlineData("-nt")]
    [InlineData("-ot")]
    [InlineData("-ef")]
    public void Transpile_FileComparisonTestOperator_EmitsRealTest(string op)
    {
        // Previously unimplemented: the emitter joined the operands with spaces
        // and produced the never-valid `$env:a -ef $env:b` (Go's etetest.sh).
        var result = PsEmitter.Transpile($"[ \"$a\" {op} \"$b\" ]");

        Assert.DoesNotContain($" {op} ", result);
        Assert.Contains("Get-Item -LiteralPath $env:a", result);
        Assert.Contains("Get-Item -LiteralPath $env:b", result);
    }

    [Fact]
    public void Transpile_NewerThanTest_MissingOperandsFollowBash()
    {
        // Oracle-verified: `a -nt b` is true when a exists and b does not, and
        // false when a does not exist (regardless of b).
        var result = PsEmitter.Transpile("[ a -nt b ]");

        Assert.Contains("if ($null -eq $__psbash_ftA) { $false }", result);
        Assert.Contains("elseif ($null -eq $__psbash_ftB) { $true }", result);
        Assert.Contains("LastWriteTimeUtc -gt", result);
    }

    // ---- command-sub pipeline head / bare @ sigil --------------------------

    [Fact]
    public void Transpile_CommandSubOfEnvPrefixedCommand_WrapsStatementListAsPipelineHead()
    {
        // `LC_TIME=C date` emits `$__saved… = …; try { … } finally { … }` — a
        // statement LIST, which cannot head a pipeline. Unwrapped it emitted
        // "An empty pipe element is not allowed" (Go's make.bash).
        var result = PsEmitter.Transpile("echo x$(LC_TIME=C date)");

        Assert.Contains("@(& { $__saved_LC_TIME", result);
        Assert.Contains("} | ConvertTo-BashCapture)", result);
    }

    [Fact]
    public void Transpile_CommandSubOfCd_WrapsStatementListAsPipelineHead()
    {
        // cd emits an if/else statement list from a Command.Simple node — the
        // AST-type-only check called it pipeable and broke the parse.
        var result = PsEmitter.Transpile("echo x$(cd /tmp)");

        Assert.Contains("@(& { $__psbash_cd_target", result);
    }

    [Fact]
    public void Transpile_CommandSubOfPlainPipeline_KeepsUnwrappedFastPath()
    {
        // A genuine single pipeline must NOT pay for a redundant & { } wrap.
        var result = PsEmitter.Transpile("x=$(grep a b | head -1)");

        Assert.DoesNotContain("& {", result);
    }

    [Fact]
    public void Transpile_CommandSubWithSemicolonInsideQuotedOperand_KeepsFastPath()
    {
        // The pipeline-head classifier is quote-aware: a ';' inside a string
        // operand is not a statement separator.
        var result = PsEmitter.Transpile("echo x$(echo \"a;b\")");

        Assert.DoesNotContain("& {", result);
    }

    [Fact]
    public void Transpile_BareAtSignLiteral_EscapesPowerShellSplatSigil()
    {
        // `@` is an ordinary character in bash but PowerShell's splat sigil.
        // Bare `echo @` emitted an unparseable "Unrecognized token".
        var result = PsEmitter.Transpile("echo @");

        Assert.Equal("Invoke-BashEcho `@", result);
    }

    [Fact]
    public void Transpile_BareAtSignPrefixedLiteral_EscapesToAvoidSilentSplat()
    {
        // This one PARSED but was silently WRONG: `cmd @arg` splatted the
        // PowerShell variable $arg instead of passing the literal text `@arg`.
        var result = PsEmitter.Transpile("cmd @arg");

        Assert.Equal("cmd `@arg", result);
    }

    [Fact]
    public void Transpile_AtSignNotLeading_NotEscaped()
    {
        // Only a LEADING @ is a PowerShell sigil.
        Assert.Equal("Invoke-BashEcho a@b", PsEmitter.Transpile("echo a@b"));
    }

    [Fact]
    public void Transpile_BraceExpansionArray_NotEscapedAsSplatSigil()
    {
        // The emitter's OWN @(...) array is real PowerShell syntax — escaping it
        // would break brace expansion.
        Assert.Equal("Invoke-BashEcho @('a','b')", PsEmitter.Transpile("echo {a,b}"));
    }

    [Theory]
    [InlineData("exit $code")]
    [InlineData("return $code")]
    [InlineData("code=1; exit $code")]
    [InlineData("f() { return $rc; }")]
    [InlineData("break $n")]
    [InlineData("continue $n")]
    public void Transpile_StatementKeywordWithUnquotedVar_NeverSplats(string bash)
    {
        // Regression guard for the whole PsStatementKeywordCommands set: a
        // splatted argument after a PowerShell statement keyword never parses.
        // The parse itself is asserted by the parseability contract suite
        // (PsBash.Host.Tests/Transpiler) — Core.Tests has no PowerShell SDK.
        var result = PsEmitter.Transpile(bash);

        Assert.DoesNotContain("@__bashsplat", result);
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

    // -----------------------------------------------------------------------
    // R16 lowering regressions: IFS in while-read, brace group pipe stdin,
    // BASH_REMATCH population.
    // -----------------------------------------------------------------------

    [Fact]
    public void Transpile_WhileReadWithIfsPrefix_SplitsOnIfs()
    {
        // REGRESSION. The while-read fast path hard-coded `-split '\s+'` and never
        // looked at the `IFS=` env prefix on the read command, so `while IFS=: read`
        // gave the whole line to the first variable.
        var result = PsEmitter.Transpile("while IFS=: read -r a b; do echo $a; done")!;

        Assert.Contains("-split '[:]'", result);
        Assert.DoesNotContain("-split '\\s+', 2", result);
    }

    [Fact]
    public void Transpile_BraceGroupPipeTarget_ForwardsInput()
    {
        // REGRESSION. A brace group as a pipe target was emitted as `& { }` with no
        // stdin forwarding, so `read` inside the body saw no stdin. The fix drains
        // `$input` into the shared bash stdin queue the read builtin consumes.
        var result = PsEmitter.Transpile("echo hi | { read y; echo y=$y; }")!;

        Assert.Contains("$global:__BashStdIn", result);
        Assert.Contains("in $input", result);
    }

    [Fact]
    public void Transpile_RegexMatch_PopulatesBashRematch()
    {
        // REGRESSION. `=~` emitted `-match` but nothing copied `$Matches` into
        // `$global:BASH_REMATCH`, so `${BASH_REMATCH[1]}` indexed a null array.
        var result = PsEmitter.Transpile("[[ $s =~ (a)(b) ]]")!;

        Assert.Contains("-match", result);
        Assert.Contains("BASH_REMATCH", result);
    }

    [Fact]
    public void Transpile_PipeToNativeCommand_ConvertsObjectsToBashText()
    {
        // R21-2. A pipe target ps-bash does not map (a native/external program
        // such as od) receives the pipeline through PowerShell's native binder,
        // which FORMATS each object — a BashObject rendered as its property
        // table ("BashText NoTrai…"), so `printf 'a\n' | sed s/a/b/ | od -c`
        // saw a table, not "b\n". bash pipes bytes, so every upstream object must
        // be converted to its bash text first. This is the ONE shared rule for
        // cmdlet-output → native stdin.
        var result = PsEmitter.Transpile("printf 'a\\n' | sed s/a/b/ | od -c")!;

        Assert.Contains("| ForEach-Object { Get-BashText $_ } | od -c", result);
    }

    [Fact]
    public void Transpile_FunctionBodyWithTrailingRedirect_AppliesRedirectInsideFunction()
    {
        // bash applies `f() { …; } >/dev/null` on every CALL, so the redirect belongs
        // inside the emitted function, and the statement after the definition survives.
        var result = PsEmitter.Transpile("f() { echo in; } >/dev/null; echo after")!;

        var fnStart = result.IndexOf("function f {", StringComparison.Ordinal);
        Assert.True(fnStart >= 0, result);
        var nullAt = result.IndexOf("$null", fnStart, StringComparison.Ordinal);
        var afterAt = result.IndexOf("after", StringComparison.Ordinal);
        Assert.True(nullAt > fnStart && nullAt < afterAt, result);
    }

    [Fact]
    public void Transpile_ExportWithStderrRedirect_KeepsAssignmentAndTail()
    {
        var result = PsEmitter.Transpile("export A=1 2>/dev/null; echo after")!;

        Assert.Contains("$env:A", result);
        Assert.Contains("after", result);
    }

    private static Command.Simple MakeSimple(params string[] words) =>
        new(words.Select(MakeWord).ToImmutableArray(), ImmutableArray<EnvPair>.Empty, ImmutableArray<Redirect>.Empty);

    private static Command.AndOrList MakeAndOrChain(string firstOp, string secondOp, params Command[] commands) =>
        new(commands.ToImmutableArray(), ImmutableArray.Create(firstOp, secondOp));

    private static CompoundWord MakeWord(string value) =>
        new(ImmutableArray.Create<WordPart>(new WordPart.Literal(value)));
}
