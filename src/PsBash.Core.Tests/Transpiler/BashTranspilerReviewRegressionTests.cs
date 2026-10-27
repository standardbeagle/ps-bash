using Xunit;
using PsBash.Core.Parser;
using PsBash.Core.Transpiler;

namespace PsBash.Core.Tests.Transpiler;

/// <summary>
/// Regression tests for the 2026-07 transpiler subsystem review. Each test names the
/// finding it guards. Crash-class findings assert a clean <see cref="ParseException"/>
/// (or no throw), never a raw .NET exception — the fuzz-layer invariant. Semantic
/// findings assert the emitted PowerShell shape.
/// </summary>
public class BashTranspilerReviewRegressionTests
{
    // ── Finding: heredoc command-substitution injection ────────────────────
    // An expanding heredoc's $(...) / `...` were passed RAW into the interpolating
    // @"..."@ here-string, so PowerShell executed them as PowerShell (Get-Date for
    // $(date)) and backticks were swallowed as PS escapes. They must be transpiled.

    [Fact]
    public void Transpile_HereDocDollarParen_TranspilesCommandSubNotRawPowerShell()
    {
        var ps = BashTranspiler.Transpile("cat <<EOF\n$(echo hi)\nEOF");
        Assert.Contains("Invoke-BashEcho", ps); // transpiled bash, not raw execution
    }

    [Fact]
    public void Transpile_HereDocBacktick_TranspilesCommandSub()
    {
        var ps = BashTranspiler.Transpile("cat <<EOF\n`echo hi`\nEOF");
        Assert.Contains("Invoke-BashEcho", ps);
    }

    [Fact]
    public void Transpile_HereDocEscapedDollarParen_IsLiteralNotExecuted()
    {
        // bash: \$(...) in an expanding heredoc is a literal $(...), no expansion.
        var ps = BashTranspiler.Transpile("cat <<EOF\n\\$(echo hi)\nEOF");
        Assert.DoesNotContain("Invoke-BashEcho", ps);
        Assert.Contains("`$", ps); // literal dollar, backtick-escaped for @"..."@
    }

    // Oracle: bash expands $1 $# $@ $* $? $$ in an unquoted heredoc body (`f() { cat <<EOT\na=$1\nEOT\n}; f x` -> a=x);
    // only letter-led names were translated, so positional parameters printed as literal `$1`.

    [Theory]
    [InlineData("1")]
    [InlineData("#")]
    [InlineData("@")]
    [InlineData("*")]
    [InlineData("?")]
    public void Transpile_HereDocPositionalOrSpecialParam_IsExpandedNotLiteral(string name)
    {
        var ps = BashTranspiler.Transpile("f() { cat <<EOT\nv=$" + name + "\nEOT\n}");
        Assert.DoesNotContain("v=$" + name + "\n", ps);
    }

    [Fact]
    public void Transpile_HereDocEscapedPositional_StaysLiteral()
    {
        var ps = BashTranspiler.Transpile("cat <<EOT\nlit=\\$1\nEOT");
        Assert.Contains("lit=`$1", ps);
    }

    // ── Finding: brace-expansion items hand-quoted with no escaping ─────────
    // echo {a,b'c'd} emitted @('a','b'c'd') — a PowerShell parse error. Items must
    // route through PsBuild.SingleQuote (which doubles the embedded quote).

    [Fact]
    public void Transpile_BraceExpansionItemWithQuote_EscapesQuote()
    {
        var ps = BashTranspiler.Transpile("echo {a,b'c'd}");
        Assert.Contains("'b''c''d'", ps);      // ' doubled inside the PS literal
        Assert.DoesNotContain("'b'c'd'", ps);  // never the broken un-escaped form
    }

    // Oracle: bash prints `a1 b1` for `x=1; echo {a,b}$x`; the variable was spliced into a single-quoted item as `$env:x`.

    [Theory]
    [InlineData("echo {a,b}$x")]
    [InlineData("echo $x{a,b}")]
    [InlineData("echo {1..2}\"$x\"")]
    [InlineData("echo {a,b}$(echo c)")]
    public void Transpile_BraceExpansionNextToAnExpansion_DoesNotQuoteTheExpansionAsText(string bash)
    {
        var ps = BashTranspiler.Transpile(bash);
        Assert.DoesNotContain("'a$env:x'", ps);
        Assert.DoesNotContain("$env:x'", ps);
        Assert.DoesNotMatch(@"'[^']*\$\(", ps); // command text never sits inside a single-quoted item
    }

    // Oracle: bash `v=abcde; echo ${v/c*/XX}` -> abXX; `${s/#xx/yy}` / `${s/%xx/yy}` anchor at the start / end.
    // The find was always matched as literal text, so globs never matched and the anchors were literal `#`/`%`.

    [Theory]
    [InlineData("echo ${v/c*/XX}", "(?s)c.*")]
    [InlineData("echo ${s/#xx/yy}", "(?s)^xx")]
    [InlineData("echo ${s/%xx/yy}", "(?s)xx$")]
    [InlineData("echo ${s//[.]/_}", "(?s)[.]")]
    public void Transpile_PatternSubstitutionFind_IsAGlobWithAnchors(string bash, string regexStart)
    {
        var ps = BashTranspiler.Transpile(bash);
        Assert.Contains("'" + regexStart, ps);
        Assert.DoesNotContain("[regex]::Escape", ps);
    }

    // Oracle: bash `echo {a..e..2}` -> a c e (the step was dropped for letter ranges: a b c d e).

    [Theory]
    [InlineData("echo {a..e..2}", "a,c,e")]
    [InlineData("echo {e..a..2}", "e,c,a")]
    [InlineData("echo {a..e..-2}", "a,c,e")]
    [InlineData("echo {A..E..3}", "A,D")]
    public void Transpile_LetterRangeWithStep_SkipsByTheStep(string bash, string items)
    {
        var ps = BashTranspiler.Transpile(bash);
        Assert.Contains(string.Join(",", items.Split(',').Select(s => $"'{s}'")), ps.Replace("\"", "'"));
    }

    // Oracle: bash `[[ -v name ]]` is true when name is set (even to ""); it fell to the "unsupported test operator" stub.

    [Theory]
    [InlineData("[[ -v x ]] && echo set")]
    [InlineData("if [[ -v x && -n $x ]]; then :; fi")]
    [InlineData("[[ ! -v x ]]")]
    public void Transpile_DoubleBracketVariableIsSet_RoutesToTheTestCmdlet(string bash)
    {
        var ps = BashTranspiler.Transpile(bash);
        Assert.DoesNotContain("unsupported test operator", ps);
        Assert.Contains("Invoke-BashTest", ps);
    }

    // Oracle: bash word-splits an UNQUOTED trim/replace/slice/case result (`s="a  b"; printf '<%s>' ${s%b}` -> <a>),
    // exactly as it does `$s`; only the :- / :+ forms were split.

    [Theory]
    [InlineData("echo ${s#*o}")]
    [InlineData("echo ${s##*o}")]
    [InlineData("echo ${s%c}")]
    [InlineData("echo ${s/b/Z}")]
    [InlineData("echo ${s//b/Z}")]
    [InlineData("echo ${s:1}")]
    [InlineData("echo ${s:1:2}")]
    [InlineData("echo ${s^^}")]
    public void Transpile_UnquotedParamOperatorResult_IsWordSplit(string bash)
        => Assert.Contains("ConvertTo-BashWords", BashTranspiler.Transpile(bash));

    [Theory]
    [InlineData("echo \"${s#*o}\"")]
    [InlineData("echo \"${s/b/Z}\"")]
    public void Transpile_QuotedParamOperatorResult_IsNotWordSplit(string bash)
        => Assert.DoesNotContain("ConvertTo-BashWords", BashTranspiler.Transpile(bash));

    // Oracle: bash defines a function declared inside another function globally once the outer one runs
    // (`o() { i() { echo hi; }; }; o; i` -> hi); a PowerShell function in a function body is local to it.

    [Fact]
    public void Transpile_FunctionDefinedInsideAFunction_IsGlobal()
    {
        var ps = BashTranspiler.Transpile("o() { i() { echo hi; }; i; }");
        Assert.Contains("function global:i", ps);
        Assert.DoesNotContain("function global:o", ps);   // the outer one keeps its plain definition
    }

    // Oracle: `( trap 'echo bye' EXIT; echo body )` prints body then bye - a subshell fires its own EXIT trap
    // when it ends (it never fired), and a subshell with no trap pays nothing for it.

    [Fact]
    public void Transpile_SubshellSettingExitTrap_FiresItAtSubshellEnd()
        => Assert.Contains("__BashTrapEXIT", BashTranspiler.Transpile("( trap 'echo bye' EXIT; echo body )"));

    [Fact]
    public void Transpile_SubshellWithoutTrap_HasNoTrapEpilogue()
        => Assert.DoesNotContain("__BashTrapEXIT", BashTranspiler.Transpile("( echo body )"));

    // Oracle: `sh -c 'echo hi'` runs a POSIX shell; ps-bash had no `sh` ("sh: command not found") although
    // agent scripts and `#!/bin/sh` one-liners call it. It routes to the same nested-shell cmdlet as `bash`.

    [Fact]
    public void Transpile_ShDashC_RoutesToNestedShellCmdlet()
        => Assert.Contains("Invoke-BashBash", BashTranspiler.Transpile("sh -c 'echo hi'"));

    // Oracle: `$LINENO` is the 1-based source line of the command (it was always empty, so error messages
    // like `echo "failed at line $LINENO"` lost their location). Single-quoted and escaped forms stay literal.

    [Theory]
    [InlineData("echo $LINENO", "Invoke-BashEcho 1")]
    [InlineData("echo a\necho \"at ${LINENO}\"", "at 2")]
    public void Transpile_Lineno_IsTheSourceLine(string bash, string expected)
        => Assert.Contains(expected, BashTranspiler.Transpile(bash));

    [Theory]
    [InlineData("echo '$LINENO'")]
    [InlineData("echo \\$LINENO")]
    [InlineData("echo $LINENOX")]
    public void Transpile_LinenoLookalike_IsNotSubstituted(string bash)
        => Assert.DoesNotContain("echo 1", BashTranspiler.Transpile(bash).Replace("Invoke-BashEcho", "echo"));


    // Oracle: `${x//$'\n'/,}` replaces newlines (the operand is the quote-removed value). The raw source text
    // `$'\n'` was matched literally, so the join-lines idiom changed nothing.

    [Theory]
    [InlineData("echo \"${x//$'\\n'/,}\"")]
    [InlineData("echo \"${x//$nl/,}\"")]
    [InlineData("echo \"${x/'b'/B}\"")]
    public void Transpile_SubstituteQuotedOrVariableOperand_MatchesTheValue(string bash)
    {
        string ps = BashTranspiler.Transpile(bash);
        Assert.Contains("[regex]::Escape(", ps);
        Assert.DoesNotContain("$'", ps);
    }

    // Oracle: `${#@}` / `${#*}` are the positional count (`$#`); they parsed as the length of a variable named
    // "@" and printed `0@}`, and failed outright inside `$(( ))`.

    [Theory]
    [InlineData("echo ${#@}")]
    [InlineData("echo ${#*}")]
    [InlineData("echo $(( ${#@} + 1 ))")]
    public void Transpile_PositionalCountBraced_IsTheArgCount(string bash)
    {
        string ps = BashTranspiler.Transpile(bash);
        Assert.DoesNotContain("@}", ps);
        Assert.Contains("BashPositional", ps);
    }
    // Oracle: `f() { trap 'echo ret' RETURN; echo in; }; f` prints in then ret - a RETURN trap runs when the

    // Oracle: an empty pattern (`${x//$unset/r}`) replaces nothing; an empty regex would match between every character.
    [Fact]
    public void Transpile_SubstituteVariablePattern_EmptyValueMatchesNothing()
        => Assert.Contains("(?!)", BashTranspiler.Transpile("x=ab; echo ${x//$foo/r}"));

    // Oracle: the trim idiom `${s#"${s%%[![:space:]]*}"}`: a quoted / expanded strip pattern is the quote-removed value,
    // matched literally. The pattern text was regex-escaped as source (`"${s%%...}"` never matched).
    [Theory]
    [InlineData("s=\"  a\"; echo \"${s#\"${s%%[![:space:]]*}\"}\"")]
    [InlineData("s=\"a  \"; echo \"${s%\"${s##*[![:space:]]}\"}\"")]
    [InlineData("f=a.txt; e=.txt; echo \"${f%\"$e\"}\"")]
    public void Transpile_StripQuotedExpansionPattern_IsMatchedAsTheValue(string bash)
        => Assert.Contains("[regex]::Escape(", BashTranspiler.Transpile(bash));

    // Oracle: `[!x]` negates and `[[:space:]]` is a POSIX class in a strip pattern; the first `]` is not the close.
    [Theory]
    [InlineData("echo ${s%%[![:space:]]*}", "[^\\s]")]
    [InlineData("echo ${s#[[:digit:]]}", "[0-9]")]
    [InlineData("echo ${s#[!a]}", "[^a]")]
    public void Transpile_StripGlobBracketExpression_IsTranslatedToARegexClass(string bash, string expectedClass)
        => Assert.Contains(expectedClass, BashTranspiler.Transpile(bash));

    // Oracle: a function is a filter: `indent() { sed 's/^/  /'; }; cmd | indent`. The piped input never reached
    // the commands in the body (sed/grep/tr/cat/read saw an empty stdin). A body that reads stdin takes the pipe
    // it is called with; one that does not is unchanged.

    [Theory]
    [InlineData("f() { sed 's/^/> /'; }")]
    [InlineData("f() { cat | wc -l; }")]
    [InlineData("f() { read -r x; echo $x; }")]
    [InlineData("f() { mapfile -t a; echo ${#a[@]}; }")]
    public void Transpile_FunctionThatReadsStdin_TakesThePipeItIsCalledWith(string bash)
        => Assert.Contains("$MyInvocation.ExpectingInput", BashTranspiler.Transpile(bash));

    [Theory]
    [InlineData("f() { echo hi; }")]
    [InlineData("f() { local x=1; echo $x; }")]
    public void Transpile_FunctionThatReadsNoStdin_HasNoStdinScope(string bash)
        => Assert.DoesNotContain("ExpectingInput", BashTranspiler.Transpile(bash));

    // Oracle: `"$@"` / `$cmd` run the named command as bash resolves it: a function, then a builtin (`false`,
    // `true`, `echo`, `test`, `[` are not PowerShell commands of those names), then PATH.

    [Theory]
    [InlineData("r() { \"$@\"; }")]
    [InlineData("c=false; $c")]
    [InlineData("\"$tool\" --version")]
    public void Transpile_RunTimeCommandName_IsResolvedAsBashDoes(string bash)
        => Assert.Contains("Resolve-BashCommand", BashTranspiler.Transpile(bash));

    [Theory]
    [InlineData("echo hi")]
    [InlineData("ls -l")]
    public void Transpile_LiteralCommandName_IsNotResolvedAtRunTime(string bash)
        => Assert.DoesNotContain("Resolve-BashCommand", BashTranspiler.Transpile(bash));
    // Oracle: `seen[$w]=$(( ${seen[$w]:-0} + 1 ))` is the counting idiom. The subscript of `${m[$k]:-d}` was the
    // literal text `$k`; `${a[1]}` inside `$(( ))` was a parse error; and a bare RANDOM / SECONDS there read as 0.

    [Fact]
    public void Transpile_ElementWithOperatorAndVariableSubscript_UsesTheVariablesValue()
    {
        string ps = BashTranspiler.Transpile("echo \"${m[$k]:-0}\"");
        Assert.Contains("[$env:k]", ps);
        Assert.DoesNotContain("'$k'", ps);
    }

    [Theory]
    [InlineData("echo $(( ${a[1]} + 1 ))")]
    [InlineData("echo $(( ${m[$k]:-0} + 1 ))")]
    [InlineData("echo $(( ${x:-7} * 2 ))")]
    public void Transpile_ParameterExpansionInArithmetic_IsExpandedBeforeEvaluation(string bash)
    {
        string ps = BashTranspiler.Transpile(bash);
        Assert.DoesNotContain("${", ps);
    }

    [Theory]
    [InlineData("echo $(( RANDOM % 6 ))", true)]
    [InlineData("echo $(( SECONDS + 1 ))", true)]
    [InlineData("(( RANDOM = 5 ))", false)]
    [InlineData("echo $(( XRANDOM + 1 ))", false)]
    public void Transpile_ComputedSpecialVarInArithmetic_IsReadFromTheShell(string bash, bool expanded)
    {
        string ps = BashTranspiler.Transpile(bash);
        Assert.Equal(expanded, ps.Contains("Get-Random") || ps.Contains("[DateTime]") || ps.Contains("BashStartTime"));
    }
    // function returns (it was never run). A function with no trap carries no wrapper.

    [Fact]
    public void Transpile_FunctionSettingReturnTrap_FiresItOnReturn()
        => Assert.Contains("__BashTrapRETURN", BashTranspiler.Transpile("f() { trap 'echo ret' RETURN; echo in; }"));

    [Fact]
    public void Transpile_FunctionWithoutTrap_HasNoReturnWrapper()
        => Assert.DoesNotContain("__BashTrapRETURN", BashTranspiler.Transpile("f() { echo in; }"));

    // Oracle: `echo ${w=q} $w` prints `q q` and `echo $((n+=2)) $n` prints `5 5`: a word that assigns runs before a
    // later unquoted word reads the variable. The unquoted words are hoisted into temps first, so the assigning
    // word is hoisted (in order) with them.

    [Theory]
    [InlineData("echo ${w=q} $w")]
    [InlineData("echo ${w:=q} $w")]
    [InlineData("echo $((n+=2)) $n")]
    [InlineData("echo $((i++)) $i")]
    public void Transpile_AssigningWordBeforeUnquotedRead_IsEvaluatedInOrder(string bash)
    {
        string ps = BashTranspiler.Transpile(bash);
        int assign = ps.IndexOf("$__bashsplat0 = ", System.StringComparison.Ordinal);
        int read = ps.IndexOf("$__bashsplat1 = ", System.StringComparison.Ordinal);
        Assert.True(assign >= 0 && read > assign, ps);
    }

    [Fact]
    public void Transpile_ComparisonInArithmetic_IsNotTreatedAsAssignment()
        => Assert.DoesNotContain("$__bashsplat1 =", BashTranspiler.Transpile("echo $((n==2)) $n"));

    // Oracle: `for i in 1 2 3; do :; done; i=0; until [ $i -ge 3 ]; do i=$((i+1)); done; echo $i` prints 3. The
    // PowerShell loop variable lingered with its last value and arithmetic reads it before the shell variable (it
    // counted from 3 and printed 4), so the loop drops it.

    [Fact]
    public void Transpile_ForInLoop_DropsItsLexicalLoopVariableAfterwards()
        => Assert.Contains("Remove-Variable -Name i", BashTranspiler.Transpile("for i in 1 2 3; do :; done"));

    // Oracle: `declare -i n=5; n+=2; echo $n` prints 7 - an integer variable adds on `+=` (it appended: 52).
    // A plain variable still appends.

    [Fact]
    public void Transpile_PlusEqualOnIntegerVariable_IsArithmeticAdd()
        => Assert.Contains("Invoke-BashArith", BashTranspiler.Transpile("declare -i n=5; n+=2"));

    [Fact]
    public void Transpile_PlusEqualOnPlainVariable_StaysAppend()
        => Assert.Contains("$env:s += ", BashTranspiler.Transpile("s=ab; s+=cd"));

    // Oracle: `IFS=:; echo "$*"` joins with the first character of IFS (a:b:c); "$@" and "${a[@]}" always join with a
    // space. The quoted forms were hard-wired to a space.

    [Theory]
    [InlineData("echo \"$*\"")]
    [InlineData("echo \"${arr[*]}\"")]
    [InlineData("echo \"x $*\"")]
    public void Transpile_QuotedStarExpansion_JoinsWithIfs(string bash)
        => Assert.Contains("$env:IFS", BashTranspiler.Transpile(bash));

    [Theory]
    [InlineData("echo \"$@\"")]
    [InlineData("echo \"${arr[@]}\"")]
    public void Transpile_QuotedAtExpansion_DoesNotUseIfs(string bash)
        => Assert.DoesNotContain("$env:IFS", BashTranspiler.Transpile(bash));

    // Oracle: bash word-splits an unquoted expansion inside an array literal: `a=($(printf "%s\n" c a b))` has 3 elements
    // (the whole output was ONE element, so `sorted=($(... | sort))` was never an array of lines).

    [Theory]
    [InlineData("a=($(printf '%s\n' c a b))")]
    [InlineData("a=($x)")]
    [InlineData("a=($x $(echo 1) z)")]
    [InlineData("a+=($(echo m n))")]
    [InlineData("a=(${x#p})")]
    public void Transpile_ArrayLiteralWithUnquotedExpansion_IsWordSplit(string bash)
        => Assert.Contains("ConvertTo-BashWords", BashTranspiler.Transpile(bash));

    [Theory]
    [InlineData("a=(\"$(echo a b)\")")]
    [InlineData("a=(\"$x\")")]
    public void Transpile_ArrayLiteralWithQuotedExpansion_IsOneElement(string bash)
        => Assert.DoesNotContain("ConvertTo-BashWords", BashTranspiler.Transpile(bash));

    // Oracle: bash `x=010; echo $((10#$x + 1))` -> 11 and `h=ff; echo $((16#$h))` -> 255; `10#$x` was a parse error (invalid arithmetic number '10#').

    [Theory]
    [InlineData("echo $((10#$x + 1))")]
    [InlineData("echo $((16#${h}))")]
    [InlineData("echo $(( 10#$n + 10#$n ))")]
    public void Transpile_ArithmeticBaseWithVariableDigits_Transpiles(string bash)
        => Assert.NotEmpty(BashTranspiler.Transpile(bash));

    // ── Finding: subshell input redirect fell to the raw 0<file fallback ────
    // (cmd) < file -> "'<' is reserved for future use". Must become Invoke-BashCat -RawStdinBytes | & {}.

    [Fact]
    public void Transpile_SubshellInputRedirect_UsesExactBytesPipe()
    {
        var ps = BashTranspiler.Transpile("(cat foo) < bar");
        Assert.Contains("Invoke-BashCat bar -RawStdinBytes | & {", ps);
        Assert.DoesNotContain("0<", ps);
    }

    // ── Finding: crashes must become clean ParseExceptions (fuzz invariant) ─

    [Fact]
    public void Transpile_ForLoopWithDollarVar_ThrowsCleanParseException()
    {
        // `for $i in ...` previously emitted the broken `foreach ($$i ...)`.
        Assert.Throws<ParseException>(
            () => BashTranspiler.Transpile("for $i in a b; do echo $i; done"));
    }

    [Fact]
    public void Transpile_RedirectWithNoTarget_ThrowsCleanParseException()
    {
        // `echo > ; ls` previously swallowed the `;` as the redirect target,
        // silently merging two statements.
        Assert.Throws<ParseException>(
            () => BashTranspiler.Transpile("echo > ; ls"));
    }

    [Fact]
    public void Transpile_HugeFileDescriptor_ThrowsCleanParseException()
    {
        // A digit run past int range previously threw OverflowException.
        Assert.Throws<ParseException>(
            () => BashTranspiler.Transpile("1234567890123>x"));
    }

    [Fact]
    public void Transpile_HereStringLoneQuote_DoesNotRawCrash()
    {
        // `cat <<< '` previously did raw[1..^1] on a length-1 quote word ->
        // ArgumentOutOfRangeException. Must be a clean reject or valid emission.
        var ex = Record.Exception(() => BashTranspiler.Transpile("cat <<< '"));
        Assert.True(ex is null or ParseException, $"unexpected {ex?.GetType().Name}");
    }

    [Fact]
    public void Transpile_HugePositionalParam_DoesNotOverflow()
    {
        // ${10000000000} is an unset positional (empty in bash); int.Parse overflowed.
        var ex = Record.Exception(() => BashTranspiler.Transpile("echo ${10000000000}"));
        Assert.True(ex is null or ParseException, $"unexpected {ex?.GetType().Name}");
    }

    [Fact]
    public void Transpile_HugeBraceTupleInteger_DoesNotOverflow()
    {
        // {10000000000,2} — a tuple integer past int range; int.Parse overflowed
        // in FormatBraceArray's sequential-range check.
        var ex = Record.Exception(() => BashTranspiler.Transpile("echo {10000000000,2}"));
        Assert.True(ex is null or ParseException, $"unexpected {ex?.GetType().Name}");
    }

    // ── Finding: quote-blind ${...} boundary scan ──────────────────────────
    // ${x:-"}"} — the `}` inside the quotes must not close the expansion early.

    [Fact]
    public void Transpile_BracedDefaultWithBraceInQuotes_DoesNotCloseEarly()
    {
        var ex = Record.Exception(() => BashTranspiler.Transpile("echo ${x:-\"}\"}"));
        Assert.True(ex is null or ParseException, $"unexpected {ex?.GetType().Name}");
    }
}
