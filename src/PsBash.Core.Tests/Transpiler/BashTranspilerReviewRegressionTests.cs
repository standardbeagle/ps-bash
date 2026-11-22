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
        var ps = BashTranspiler.Transpile("echo {a,b\\'c}");
        Assert.Contains("'b''c'", ps);         // ' doubled inside the PS literal (bash prints b'c)
        Assert.DoesNotContain("'b'c'", ps);    // never the broken un-escaped form
    }

    [Fact]
    public void Transpile_BraceExpansionQuotedItem_DropsTheQuotes()
    {
        // Oracle: bash prints `a bcd` for `echo {a,b'c'd}`.
        var ps = BashTranspiler.Transpile("echo {a,b'c'd}");
        Assert.Contains("'bcd'", ps);
    }

    [Fact]
    public void Transpile_InvalidBraceRange_KeepsTheBracesLiteral()
    {
        // Oracle: bash prints `-a{1...3}- -b{1...3}-`.
        var ps = BashTranspiler.Transpile("echo -{a,b}{1...3}-");
        Assert.Contains("-a{1...3}-", ps);
    }

    [Fact]
    public void Transpile_LiteralBracesInsideAWord_AreNotAScriptBlock()
    {
        // Oracle: bash prints `a{b}c`; bare `a{b}c` is `a`, a scriptblock and `c` in PowerShell.
        var ps = BashTranspiler.Transpile("echo a{b}c");
        Assert.Contains("\"a{b}c\"", ps);
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
    [InlineData("echo ${v/c*/XX}", "Substitute($env:v, 'c*', 'XX', 0)")]
    [InlineData("echo ${s/#xx/yy}", "Substitute($env:s, 'xx', 'yy', 2)")]
    [InlineData("echo ${s/%xx/yy}", "Substitute($env:s, 'xx', 'yy', 3)")]
    [InlineData("echo ${s//[.]/_}", "Substitute($env:s, '[.]', '_', 1)")]
    // The anchor is the operator, not pattern text: a quoted or escaped pattern keeps it (bash: v='*x'; ${v/#\*/X} is Xx).
    [InlineData("echo ${v/#\\*/X}", "Substitute($env:v, '\\*', 'X', 2)")]
    [InlineData("echo ${v/%\"x\"/Y}", "Substitute($env:v, 'x', 'Y', 3)")]
    public void Transpile_PatternSubstitutionFind_IsAGlobWithAnchors(string bash, string call)
    {
        var ps = BashTranspiler.Transpile(bash);
        Assert.Contains("[PsBash.Cmdlets.BashPatternMatch]::" + call, ps);
        Assert.DoesNotContain("[regex]", ps);
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

    // Oracle: `[ "$(echo -n)" = "" ]` is true - an empty command substitution is $null in PowerShell, and `$null -ceq ''` is false.
    [Theory]
    [InlineData("[ \"$(echo -n)\" = \"\" ]")]
    [InlineData("[ \"$(true)\" != \"x\" ]")]
    public void Transpile_TestOfAnEmptyCommandSub_ComparesAsAString(string bash)
        => Assert.Contains("(@($(", BashTranspiler.Transpile(bash));

    // Oracle: `for i in 1 2; do worker $i & done` runs worker with 1 and 2 - the loop moves on before the job reads the
    // variable; the job's own scalar store, copied at the `&`, holds the value at launch (no PowerShell copy).
    [Fact]
    public void Transpile_BackgroundJobInALoop_ReadsTheLoopVariableFromItsOwnStore()
    {
        var ps = BashTranspiler.Transpile("f() { return $1; }; for i in 1 2; do f $i & done");
        Assert.DoesNotContain("-Vars", ps);
    }

    [Fact]
    public void Transpile_BackgroundJobNotUsingTheLoopVariable_KeepsTheBareEmission()
        => Assert.Contains("Invoke-BashBackground -Live {", BashTranspiler.Transpile("for i in 1 2; do sleep 1 & done"));

    // Oracle: `exec 4<<<"fdline"; read -r -u 4 v` reads fdline - the fd number used to become a command word and emit a parse error that failed the whole script.
    [Fact]
    public void Transpile_ExecFdHereString_OpensTheDescriptorOnTheText()
    {
        var ps = BashTranspiler.Transpile("exec 4<<<\"fdline\"; read -r -u 4 v");
        Assert.StartsWith("Invoke-BashExecFd 4 '<<<' (", ps);   // the text itself (PsEmitter.FdOpenStep), no temp file
        Assert.Contains("fdline", ps);
    }

    // Oracle: `trap 'rm -f x; echo cleanup' EXIT` fires at script end - a `;` inside the quoted handler must not hide the EXIT from the wrapper detection.
    [Theory]
    [InlineData("trap 'rm -f x; echo cleanup' EXIT; echo a")]
    [InlineData("trap \"rm -f x; echo cleanup\" EXIT; echo a")]
    [InlineData("trap 'a | b & c' ERR; echo a")]
    public void Transpile_TrapHandlerWithSemicolon_StillFiresAtScriptEnd(string bash)
        => Assert.Contains("Test-Path Variable:Global:__BashTrapEXIT", BashTranspiler.Transpile(bash));

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
        => Assert.Contains(expected, BashTranspiler.Transpile(bash + "\ndeclare -p"));

    [Theory]
    [InlineData("echo '$LINENO'")]
    [InlineData("echo \\$LINENO")]
    [InlineData("echo $LINENOX")]
    public void Transpile_LinenoLookalike_IsNotSubstituted(string bash)
        => Assert.DoesNotContain("echo 1", BashTranspiler.Transpile(bash).Replace("Invoke-BashEcho", "echo"));

    // Oracle: a bare `LINENO` identifier inside arithmetic is the line too; lookalikes are not.
    [Theory]
    [InlineData("MYLINENO + 1", "MYLINENO + 1")]
    [InlineData("LINENO2", "LINENO2")]
    [InlineData("LINENO_x", "LINENO_x")]
    [InlineData("x = LINENO", "x = 7")]
    [InlineData("x = $LINENO + ${LINENO}", "x = 7 + 7")]
    [InlineData("LINENO+LINENO", "7+7")]
    public void SubstituteArithLineNumber_OnlyTheWholeIdentifier(string expr, string expected)
        => Assert.Equal(expected, PsBash.Core.Parser.BashLexer.SubstituteArithLineNumber(expr, 7));

    // Oracle: a for-list reports the line of the `for` command, even when the list continues on later lines.
    [Fact]
    public void Transpile_Lineno_ForListUsesTheForLine()
    {
        var ps = BashTranspiler.Transpile("echo one\nfor x in \\\n$LINENO zzz; do echo $x; done");
        Assert.Contains("2", ps);
        Assert.DoesNotContain("'3'", ps);
        Assert.DoesNotContain("\"3\"", ps);
    }

    [Fact]
    public void Transpile_Lineno_AfterForLoopIsItsOwnLine()
        => Assert.Contains("Invoke-BashEcho 3", BashTranspiler.Transpile("for x in a; do :; done\n\necho $LINENO"));


    // Oracle: `${x//$'\n'/,}` replaces newlines (the operand is the quote-removed value). The raw source text
    // `$'\n'` was matched literally, so the join-lines idiom changed nothing.

    [Theory]
    [InlineData("echo \"${x//$'\\n'/,}\"")]
    [InlineData("echo \"${x/'b'/B}\"")]
    public void Transpile_SubstituteQuotedOrVariableOperand_MatchesTheValue(string bash)
    {
        string ps = BashTranspiler.Transpile(bash);
        Assert.Contains("[PsBash.Cmdlets.BashPatternMatch]::Substitute($env:x, ", ps);
        Assert.DoesNotContain("$'", ps);
    }

    // Oracle: an UNQUOTED `$g` in the pattern is a glob, even inside "…" (`g='*'; "${v//$g/-}"` is `-`);
    // "$g" is the literal text.
    [Fact]
    public void Transpile_SubstituteUnquotedVariablePattern_IsConvertedAsAGlobAtRunTime()
    {
        string ps = BashTranspiler.Transpile("echo \"${x//$g/,}\"");
        Assert.Contains("Substitute($env:x, ($env:g), ',', 1)", ps);
        string quoted = BashTranspiler.Transpile("echo \"${x//\"$g\"/,}\"");
        Assert.Contains("Substitute($env:x, ([PsBash.Cmdlets.BashGlobText]::Escape(\"$env:g\")), ',', 1)", quoted);
    }

    // Oracle: the delimiter `/` is the first UNQUOTED one after the first character: `${p////\\/}` replaces `/`
    // with `\/`, `${p//'/'/x}` likewise, and `${x///}` deletes every slash.
    [Theory]
    [InlineData("echo ${p////\\\\/}")]
    [InlineData("echo ${p//'/'/\\\\/}")]
    [InlineData("echo ${p///}")]
    public void Transpile_SubstituteSlashInPattern_SplitsAtTheFirstUnquotedSlashAfterTheFirstChar(string bash)
    {
        string ps = BashTranspiler.Transpile(bash);
        Assert.DoesNotContain("/\\\\//", ps);
        Assert.Contains("Substitute($env:p, '/', ", ps);
    }

    // Oracle: quotes inside a bracket expression come off and the quoted characters stay literal.
    [Fact]
    public void Transpile_SubstituteQuotedBracketGlob_RemovesQuotes()
    {
        string ps = BashTranspiler.Transpile("echo ${p//[^'><+-.,[]']}");
        Assert.DoesNotContain("'><", ps);
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
    // The pattern's run-time value goes to Substitute, which treats an empty one as no match (BashPatternTests).
    [Fact]
    public void Transpile_SubstituteVariablePattern_EmptyValueMatchesNothing()
        => Assert.Contains("Substitute($env:x, ($env:foo), 'r', 1)", BashTranspiler.Transpile("x=ab; echo ${x//$foo/r}"));

    // Oracle: the trim idiom `${s#"${s%%[![:space:]]*}"}`: a quoted / expanded strip pattern is the quote-removed value,
    // matched literally. The pattern text was regex-escaped as source (`"${s%%...}"` never matched).
    [Theory]
    [InlineData("s=\"  a\"; echo \"${s#\"${s%%[![:space:]]*}\"}\"")]
    [InlineData("s=\"a  \"; echo \"${s%\"${s##*[![:space:]]}\"}\"")]
    [InlineData("f=a.txt; e=.txt; echo \"${f%\"$e\"}\"")]
    public void Transpile_StripQuotedExpansionPattern_IsMatchedAsTheValue(string bash)
        => Assert.Contains("[PsBash.Cmdlets.BashGlobText]::Escape(", BashTranspiler.Transpile(bash));

    // Oracle: `[!x]` negates and `[[:space:]]` is a POSIX class in a strip pattern; the first `]` is not the close.
    // The bracket reaches the one compiler intact (its meaning: BashPatternTests.Compile_Brackets).
    [Theory]
    [InlineData("echo ${s%%[![:space:]]*}", "Remove($env:s, '[![:space:]]*', $true, $true)")]
    [InlineData("echo ${s#[[:digit:]]}", "Remove($env:s, '[[:digit:]]', $false, $false)")]
    [InlineData("echo ${s#[!a]}", "Remove($env:s, '[!a]', $false, $false)")]
    public void Transpile_StripGlobBracketExpression_IsTranslatedToARegexClass(string bash, string expectedClass)
        => Assert.Contains(expectedClass, BashTranspiler.Transpile(bash));

    // Oracle: an operator on a whole-array expansion applies to EACH element (`${a[@]/o/0}`, `"${a[@]%o}"` is N words),
    // and a lone `"${a[@]}"` argument to a function is N arguments, not one joined string.
    [Theory]
    [InlineData("echo ${a[@]/o/0}")]
    [InlineData("echo \"${a[*]//o/0}\"")]
    [InlineData("echo ${a[@]#?}")]
    [InlineData("echo ${a[@]%o}")]
    [InlineData("echo ${a[@]^^}")]
    public void Transpile_OperatorOnWholeArray_MapsEachElement(string bash)
        => Assert.Contains("ForEach-Object", BashTranspiler.Transpile(bash));

    [Theory]
    [InlineData("f \"${a[@]}\"")]
    [InlineData("f \"${a[@]%o}\"")]
    public void Transpile_LoneQuotedArrayArgumentToFunction_IsSplatted(string bash)
        => Assert.Contains("@__bashsplat", BashTranspiler.Transpile(bash));

    // Oracle: `${!#}` is the last argument, `${!n}` the positional named by n, `${@:$#}` / `${a[@]:$i:2}` slice by a variable,
    // and `${a[@]:-w}` / `${m[@]:+w}` test the elements (an associative array printed "System.Collections.Hashtable").
    [Theory]
    [InlineData("echo \"${!#}\"", "$__pp")]
    [InlineData("n=2; echo \"${!n}\"", "$__pp")]
    [InlineData("echo \"${@:$#}\"", "$__psbO")]
    [InlineData("echo \"${a[@]:$i:2}\"", "$__psbN")]
    [InlineData("echo \"${m[@]:-none}\"", ".Count -gt 1 -or (")]
    [InlineData("echo \"${m[@]:+yes}\"", ".Count -gt 1 -or (")]
    public void Transpile_IndirectPositionalVariableSliceAndArrayDefault_AreRuntimeEvaluated(string bash, string expected)
        => Assert.Contains(expected, BashTranspiler.Transpile(bash + "\ndeclare -p"));

    // Oracle: `trap 'echo rc=$?' EXIT; exit 4` prints rc=4 - `exit N` records N as the status the EXIT trap sees.
    [Fact]
    public void Transpile_ExitWithCode_RecordsStatusForExitTrap()
        => Assert.Contains("$global:LASTEXITCODE = 4; exit $global:LASTEXITCODE", BashTranspiler.Transpile("trap 'echo rc=$?' EXIT; exit 4"));

    // Oracle: `declare -r B=1; B=2` fails "B: readonly variable" like `readonly B=1`.
    [Theory]
    [InlineData("declare -r B=1; B=2")]
    [InlineData("declare -ri N=5")]
    [InlineData("typeset -r B")]
    public void Transpile_DeclareReadonly_MarksTheName(string bash)
        => Assert.Contains("MarkReadonly(", BashTranspiler.Transpile(bash));

    // Oracle: `((c[$k]++))`, `((a[i]*=10))`, `$((a[i]+a[2]))` operate on array elements (they were a parse error), and
    // `${a[i]}` with a bare-name subscript is the arithmetic index (it read the literal key "i").
    [Theory]
    [InlineData("((c[$k]++))", "c[$k]++")]
    [InlineData("((arr[i]*=10))", "arr[i]*=10")]
    [InlineData("echo $((arr[i] + arr[2]))", "arr[i] + arr[2]")]
    public void Transpile_ArithmeticArrayElement_ParsesAsOneOperand(string bash, string expected)
        => Assert.Contains(expected, BashTranspiler.Transpile(bash + "\ndeclare -p"));

    [Fact]
    public void Transpile_BareNameSubscript_IsArithmeticOnIndexedArrays()
        => Assert.Contains("Invoke-BashArith", BashTranspiler.Transpile("a=(x y); i=0; echo \"${a[i+1]}\""));

    // Oracle: an array a function writes without declaring it `local` is the nearest caller's local, else the shell's
    // own - push() { st+=("$1"); } must reach the caller (VarFrameStack.ArraySlot decides at run time).
    // The slot is looked up once per write statement (`$__psbSlot`), not once for the target and again for the base.
    [Theory]
    [InlineData("push() { st+=(\"$1\"); }; push a", "$__psbSlot = [PsBash.Cmdlets.VarFrameStack]::ArraySlot($ExecutionContext.SessionState, 'st'); $__psbSlot.Value = Add-BashArray ($__psbSlot.Value ?? ${env:st}) @(")]
    [InlineData("f() { a=(x y); }; f", "$__psbSlot = [PsBash.Cmdlets.VarFrameStack]::ArraySlot($ExecutionContext.SessionState, 'a'); $__psbSlot.Value = @(")]
    [InlineData("f() { a[2]=z; }; f", "$__psbSlot = [PsBash.Cmdlets.VarFrameStack]::ArraySlot($ExecutionContext.SessionState, 'a'); if ($null -eq $__psbSlot.Value -and $null -ne $env:a) { $__psbSlot.Value = @($env:a) }; $__psbSlot.Value = Set-BashElement ($__psbSlot.Value)")]
    // A function defined inside a subshell too (the slot is then the subshell's, at run time). REGRESSION: it wrote `$a`,
    // a variable of its own, so `( f() { a=(x y); }; f; echo "${a[*]}" )` printed the old value.
    [InlineData("( f() { a=(x y); }; f )", "$__psbSlot = [PsBash.Cmdlets.VarFrameStack]::ArraySlot($ExecutionContext.SessionState, 'a'); $__psbSlot.Value = @(")]
    [InlineData("g() { ( f() { a+=(z); }; f ); }; g", "$__psbSlot = [PsBash.Cmdlets.VarFrameStack]::ArraySlot($ExecutionContext.SessionState, 'a'); $__psbSlot.Value = Add-BashArray (")]
    public void Transpile_ArrayWrittenInFunction_TargetsTheCallersSlot(string bash, string expected)
        => Assert.Contains(expected, BashTranspiler.Transpile(bash + "\ndeclare -p"));

    [Theory]
    [InlineData("f() { local st=(a); st+=(b); }; f", "$st = Add-BashArray ($st ?? ${env:st}) @(")]
    public void Transpile_LocalOrSubshellArrayWrite_StaysLocal(string bash, string expected)
    {
        var ps = BashTranspiler.Transpile(bash);
        Assert.Contains(expected, ps);
        Assert.DoesNotContain("$global:st", ps);
    }

    // Oracle: a subshell's array writes end with it - `declare -A d=([a]=1); ( d[a]=9 ); echo ${d[a]}` prints 1. The body
    // writes the array its code reads through the slot, which the subshell's journal puts back (copying an associative
    // or sparse array first). REGRESSION: it wrote `$d = Set-BashElement $d ...`, a variable of the subshell's scope, but
    // Set-BashElement had already changed the shell's table in place, so 9 survived the subshell.
    [Theory]
    [InlineData("declare -A d=([a]=1); ( d[a]=9 )", "$__psbSlot = [PsBash.Cmdlets.VarFrameStack]::ArraySlot($ExecutionContext.SessionState, 'd'); $__psbSlot.Value = Set-BashElement ($__psbSlot.Value)")]
    [InlineData("sp=([3]=x); echo q | { read -r w; sp+=(\"$w\"); }", "$__psbSlot = [PsBash.Cmdlets.VarFrameStack]::ArraySlot($ExecutionContext.SessionState, 'sp'); $__psbSlot.Value = Add-BashArray (")]
    [InlineData("f() { ( st+=(b) ); }; f", "$__psbSlot = [PsBash.Cmdlets.VarFrameStack]::ArraySlot($ExecutionContext.SessionState, 'st'); $__psbSlot.Value = Add-BashArray (")]
    [InlineData("( a=(x y) )", "$__psbSlot = [PsBash.Cmdlets.VarFrameStack]::ArraySlot($ExecutionContext.SessionState, 'a'); $__psbSlot.Value = @(")]
    public void Transpile_SubshellArrayWrite_GoesThroughTheJournaledSlot(string bash, string expected)
        => Assert.Contains(expected, BashTranspiler.Transpile(bash));

    // Oracle: bash ignores -r on a nameref - `typeset -n -r ref=x; ref=v` still assigns through it.
    [Fact]
    public void Transpile_DeclareNamerefReadonly_DoesNotMarkReadonly()
        => Assert.DoesNotContain("MarkReadonly(", BashTranspiler.Transpile("x=1; typeset -n -r ref=x; ref=2"));

    // Oracle: `[[ -v cfg[host] ]]` tests the element; the operand is a name, not a glob.
    [Fact]
    public void Transpile_TestVWithSubscript_PassesTheNameQuoted()
    {
        var ps = BashTranspiler.Transpile("[[ -v cfg[host] ]]");
        Assert.Contains("Invoke-BashTest '-v' 'cfg[host]'", ps);
        Assert.DoesNotContain("ConvertTo-BashGlob", ps);
    }

    // Oracle: ${s//\\/\\\\} doubles backslashes, ${s//\"/\\\"} escapes quotes, ${s//$'\n'/\\n} writes `\n` (json_escape).
    [Theory]
    [InlineData("echo \"${s//\\\\/\\\\\\\\}\"")]
    [InlineData("echo \"${s//\\\"/\\\\\\\"}\"")]
    public void Transpile_SubstitutionWithBackslashEscapes_UsesTheQuoteRemovedText(string bash)
        => Assert.DoesNotContain("[regex]::Escape('\\\\')", BashTranspiler.Transpile(bash).Replace("\\\\\\\\", "XX"));

    // Oracle: `false; echo ${PIPESTATUS[*]}` is `1` - a plain command is a one-stage pipeline.
    [Fact]
    public void Transpile_PipestatusAfterPlainCommand_RecordsItsStatus()
        => Assert.Contains("$global:PIPESTATUS = @([int]$global:LASTEXITCODE)", BashTranspiler.Transpile("false; echo ${PIPESTATUS[*]}"));

    // Oracle: a per-stage pipeline whose last stage redirects (`cat >/dev/null`) threw a NullReferenceException.
    [Fact]
    public void Transpile_PipestatusPipelineEndingInRedirect_WrapsTheRedirectedStage()
        => Assert.Contains("& { $input | Invoke-BashCat >$null }", BashTranspiler.Transpile("echo hi | cat >/dev/null; echo ${PIPESTATUS[*]}"));

    // Oracle: `r=$(exit 4) || echo caught` falls back with 4 - the assignment's status is its command substitution's.
    [Fact]
    public void Transpile_CommandSubAssignmentBeforeOr_BridgesItsExitStatus()
    {
        var ps = BashTranspiler.Transpile("r=$(exit 4) || echo fb");
        Assert.Matches(@"\|\| ?Invoke-BashEcho fb", ps);
        Assert.Contains(PsBuild.SignalFailIfFailed(), ps);
        Assert.DoesNotContain(PsBuild.SignalFailIfFailed(), BashTranspiler.Transpile("r=1 || echo fb"));
    }

    // Oracle: `${x:?}` in a pipeline stage or a command substitution ends only that subshell, not the script.
    [Theory]
    [InlineData("echo \"${var:?}\" | head -1; echo after")]
    [InlineData("echo a | cat \"${v:?}\"; echo after")]
    [InlineData("x=$(echo \"${var:?}\"); echo after")]
    public void Transpile_ParamErrorInStageOrCommandSub_IsCaughtThere(string bash)
        => Assert.Contains("catch [PsBash.Cmdlets.BashParamErrorException]", BashTranspiler.Transpile(bash));

    [Fact]
    public void Transpile_ParamErrorAtTopLevel_IsNotCaught()
        => Assert.DoesNotContain("catch [PsBash.Cmdlets.BashParamErrorException]", BashTranspiler.Transpile("echo \"${var:?top}\"; echo not-reached"));

    // Oracle: `declare -x e=2; declare -p e` prints `declare -x e="2"`.
    [Fact]
    public void Transpile_DeclareExport_MarksTheAttribute()
        => Assert.Contains("MarkAttribute('e', 'x')", BashTranspiler.Transpile("declare -x e=2"));

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

    // Oracle: an unquoted name splits first. `SUDO=; $SUDO rm f` runs `rm f`, `CC='gcc -O2'; $CC x.c` runs gcc with -O2,
    // and an empty name with no operands runs nothing (status 0). Before, the unsplit value was the name: empty leaked
    // PowerShell's "expression after '&'" error, and `x="echo a"; $x b` looked up the command `echo a`.
    [Theory]
    [InlineData("$x echo hi", "& (Resolve-BashCommand -Words @(ConvertTo-BashWords $env:x)) echo hi")]
    [InlineData("${x} echo hi", "& (Resolve-BashCommand -Words @(ConvertTo-BashWords $env:x)) echo hi")]
    public void Transpile_UnquotedRunTimeCommandName_IsSplitAtRunTime(string bash, string expected)
        => Assert.Contains(expected, BashTranspiler.Transpile(bash));

    // Oracle: `"$x" echo hi` with x empty is `bash: : command not found` (127): a quoted name stays one word.
    [Fact]
    public void Transpile_QuotedRunTimeCommandName_StaysOneWord()
    {
        var ps = BashTranspiler.Transpile("\"$x\" echo hi");
        Assert.Contains("& (Resolve-BashCommand \"$env:x\") echo hi", ps);
        Assert.DoesNotContain("-Words", ps);
    }

    // Oracle: `if "$(true)"; then echo y; else echo n; fi` prints n (`bash: : command not found`, 127): a quoted command
    // substitution is one word, never elided. It was `& "$(...)"`, a PowerShell error that counted as success
    // (oils strict-options "empty argv WITHOUT strict_argv").
    [Fact]
    public void Transpile_QuotedCommandSubCommandName_IsLookedUpAsOneWord()
    {
        var ps = BashTranspiler.Transpile("\"$(echo foo >/dev/null)\"");
        Assert.Contains("& (Resolve-BashCommand $((@(Invoke-BashEcho", ps);
        Assert.DoesNotContain("-Words", ps);
    }

    // Oracle: `printf 'a\n' | { p=printf; $p 'X\n'; c=cat; $c; }` prints X then a. The stdin scope's queue was piped into
    // every run-time name, so printf failed to bind the queued line (and on an empty queue never ran); the runtime
    // now feeds only a command that reads stdin.
    [Fact]
    public void Transpile_RunTimeCommandNameInStdinScope_LetsTheRuntimeDecideTheFeed()
    {
        var ps = BashTranspiler.Transpile("printf 'a\\n' | { p=printf; $p 'X\\n'; c=cat; \"$c\"; }");
        Assert.Contains("& (Resolve-BashCommand -Fed -Words @(ConvertTo-BashWords $env:p))", ps);
        Assert.Contains("& (Resolve-BashCommand -Fed \"$env:c\")", ps);
        Assert.DoesNotContain("} | & (Resolve-BashCommand", ps);
    }

    // Oracle: `CDPATH=/srv; cd app` lands in /srv/app and echoes it. A relative or run-time target asks the runtime;
    // a target that cannot use CDPATH (absolute, `./x`, `..`, `-`, `~`, none) does not pay for the lookup.
    [Theory]
    [InlineData("cd foo", true)]
    [InlineData("cd $d", true)]
    [InlineData("cd \"$d/sub\"", true)]
    [InlineData("cd /tmp", false)]
    [InlineData("cd ./foo", false)]
    [InlineData("cd ..", false)]
    [InlineData("cd -", false)]
    [InlineData("cd ~/x", false)]
    [InlineData("cd", false)]
    public void Transpile_CdTarget_UsesCdPathOnlyWhenItCanApply(string bash, bool lookup)
    {
        string ps = BashTranspiler.Transpile(bash);
        Assert.Equal(lookup, ps.Contains("BashRuntime]::CdPathLookup(", StringComparison.Ordinal));
        Assert.Equal(lookup, ps.Contains("if ($__psbash_cd_via) { Write-Output $env:PWD }", StringComparison.Ordinal));
    }
    // Oracle: `seen[$w]=$(( ${seen[$w]:-0} + 1 ))` is the counting idiom. The subscript of `${m[$k]:-d}` was the
    // literal text `$k`; `${a[1]}` inside `$(( ))` was a parse error; and a bare RANDOM / SECONDS there read as 0.

    [Fact]
    public void Transpile_ElementWithOperatorAndVariableSubscript_UsesTheVariablesValue()
    {
        string ps = BashTranspiler.Transpile("echo \"${m[$k]:-0}\"");
        Assert.Contains("$env:k", ps);
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

    // Oracle: `for i in 1 2 3; do :; done; i=0; until [ $i -ge 3 ]; do i=$((i+1)); done; echo $i` prints 3. A
    // PowerShell loop variable lingered with its last value and arithmetic read it before the shell variable (it
    // counted from 3 and printed 4). The loop binds only the shell variable now (environment-model.md section 8).

    [Fact]
    public void Transpile_ForInLoop_BindsOnlyTheShellVariable()
    {
        var ps = BashTranspiler.Transpile("for i in 1 2 3; do :; done");
        Assert.Contains("[PsBash.Cmdlets.BashVariableStore]::Set('i', [string]($__psbash_lv0))", ps);
        Assert.DoesNotContain("foreach ($i ", ps);
    }

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

    // bash refuses writes to a `local -r` variable inside the function; outside it the name is free again.
    [Fact]
    public void Transpile_LocalReadonly_RefusesLaterWritesOnlyInsideTheFunction()
    {
        var ps = BashTranspiler.Transpile("f() { local -r x=1; x=2; }; f; x=9");
        Assert.Contains("x: readonly variable", ps);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(ps, "readonly variable"));
        Assert.Contains("$env:x = \"9\"", ps);
    }

    // `set -eEuo pipefail` is the standard preamble; options that change nothing here must not abort the transpile.
    [Theory]
    [InlineData("set -eEuo pipefail")]
    [InlineData("set -E")]
    [InlineData("set -o errtrace")]
    [InlineData("set +o functrace")]
    [InlineData("set -Eeu")]
    public void Transpile_SetWithInertOptions_IsAccepted(string script)
    {
        var ex = Record.Exception(() => BashTranspiler.Transpile(script + "; echo ok"));
        Assert.Null(ex);
    }

    [Fact]
    public void Transpile_SetErrtraceWithErrexit_StillEnablesErrexit()
    {
        Assert.Contains("__BashErrexit = $true", BashTranspiler.Transpile("set -eE"));
    }

    // `timeout` must reach Invoke-BashTimeout (Windows' timeout.exe waits for a keypress), with every flag of the inner command intact.
    [Fact]
    public void Transpile_TimeoutWithInnerFlags_MapsToTheCmdletAndKeepsTheFlags()
    {
        var ps = BashTranspiler.Transpile("timeout 5 grep -v foo file");
        Assert.Contains("Invoke-BashTimeout 5 grep '-v' foo file", ps);
        Assert.DoesNotContain("& (Resolve-BashCommand", ps);
    }

    // `${#BASH_VERSINFO[@]}` counted the (nonexistent) variable $BASH_VERSINFO: 0 instead of 6.
    [Fact]
    public void Transpile_LengthOfBashVersinfo_CountsTheMappedArray()
    {
        var ps = BashTranspiler.Transpile("echo ${#BASH_VERSINFO[@]}");
        Assert.Contains("$global:BashVersionInfo", ps);
    }

    // `set -x` prints `+ cmd args` before each command; only words that render without running anything are traced.
    [Fact]
    public void Transpile_CommandAfterSetX_IsPrecededByAnXtraceCall()
    {
        var ps = BashTranspiler.Transpile("set -x\necho \"hi $x\" -n");
        Assert.Contains("Write-BashXtrace 'echo' ([string](\"hi $env:x\")) '-n'; ", ps);
    }

    [Fact]
    public void Transpile_CommandSubstitutionArgAfterSetX_IsNotTraced()
    {
        var ps = BashTranspiler.Transpile("set -x\necho $(date)");
        Assert.DoesNotContain("Write-BashXtrace 'echo'", ps);
    }

    [Fact]
    public void Transpile_ScriptWithoutSetX_HasNoXtraceCalls()
        => Assert.DoesNotContain("Write-BashXtrace", BashTranspiler.Transpile("echo hi"));

    // A subshell / $( ) runs the EXIT trap a function it calls set; the body text has no `trap` of its own.
    [Theory]
    [InlineData("f() { trap 'echo x' EXIT; }\n( f )")]
    [InlineData("f() { trap 'echo x' EXIT; }\nv=$( f; echo b )")]
    public void Transpile_SubshellCallingATrapSettingFunction_FiresTheExitTrapAtItsEnd(string bash)
        => Assert.Contains("$global:__BashTrapEXIT = $null; try {", BashTranspiler.Transpile(bash));

    [Fact]
    public void Transpile_SubshellInAScriptWithoutTrap_PaysNothingForExitTraps()
        => Assert.DoesNotContain("__BashTrapEXIT", BashTranspiler.Transpile("( echo a; cd /tmp )"));

    // `declare -p` prints the i and x attributes: `declare -i n=5` and a plain `export E=1` are recorded for it.
    [Theory]
    [InlineData("declare -i n=5", "MarkAttribute('n', 'i')")]
    [InlineData("declare -ix n=5", "MarkAttribute('n', 'i')")]
    [InlineData("export E=1", "BashVariableStore]::Export('E')")]
    [InlineData("export A=1 B=2", "BashVariableStore]::Export('A', 'B')")]
    public void Transpile_IntegerAndExportDeclarations_RecordTheirAttribute(string bash, string expected)
        => Assert.Contains(expected, BashTranspiler.Transpile(bash + "\ndeclare -p"));

    // Oracle: `declare -i ti=3; declare -n ri; w=ti; ri=$w; echo "[${ri@a}]"` prints [i]. The run-time `${r@a}` reads the
    // run-time table, so a script with an attribute transform records -i / -l / -u there too. REGRESSION: it printed [].
    [Theory]
    [InlineData("declare -i ti=3; declare -n ri; w=ti; ri=$w; echo \"[${ri@a}]\"", "MarkAttribute('ti', 'i')")]
    [InlineData("declare -u up=a; declare -n ru; w=up; ru=$w; echo \"${ru@A}\"", "MarkAttribute('up', 'u')")]
    [InlineData("declare -l lo=A; echo \"${lo@a}\"", "MarkAttribute('lo', 'l')")]
    public void Transpile_AScriptReadingAttributesAtRunTime_RecordsThemThere(string bash, string expected)
        => Assert.Contains(expected, BashTranspiler.Transpile(bash));

    [Fact]
    public void Transpile_IntegerDeclarationWithNoAttributeReader_RecordsNothing()
        => Assert.DoesNotContain("MarkAttribute", BashTranspiler.Transpile("declare -i ti=3; echo $ti"));

    [Fact]
    public void Transpile_ExportInAScriptWithoutDeclareP_RecordsNothing()
        => Assert.DoesNotContain("MarkAttribute", BashTranspiler.Transpile("export E=1"));

    // `local -n r=$1`: the target is only known at run time, so the body is emitted once per literal name the
    // script passes, with r resolved statically, and chosen by the argument.
    [Fact]
    public void Transpile_LocalNamerefFromPositional_EmitsOneBodyPerLiteralCaller()
    {
        var ps = BashTranspiler.Transpile("setv() { local -n r=$1; r=x; }\nsetv foo\nsetv bar");
        Assert.Contains("if ($__nrt -ceq 'foo') { [PsBash.Cmdlets.VarFrameStack]::SetLine(1); $global:LASTEXITCODE = 0; $env:foo = \"x\" }", ps);
        Assert.Contains("elseif ($__nrt -ceq 'bar') {", ps);
    }

    [Fact]
    public void Transpile_LocalNamerefToAnArray_AppendsToTheCallersArray()
    {
        var ps = BashTranspiler.Transpile("addto() { local -n a=$1; a+=(\"$2\"); }\nl=(a)\naddto l c");
        Assert.Contains("$__psbSlot = [PsBash.Cmdlets.VarFrameStack]::ArraySlot($ExecutionContext.SessionState, 'l'); $__psbSlot.Value = Add-BashArray ($__psbSlot.Value ?? ${env:l}) @(", ps);
    }

    [Fact]
    public void Transpile_LocalNamerefNeverCalledWithALiteral_KeepsTheOrdinaryLocal()
        => Assert.DoesNotContain("__nrt", BashTranspiler.Transpile("setv() { local -n r=$1; r=x; }\nsetv \"$name\""));

    // Oracle (bash): `f2() { f3 "$@"; }; f2 F1` makes f3's `local -n ref=$1` name F1 — the target is followed back
    // through the forwarding function to its literal callers ("$@" splat and "$N" alike), and a recursion ends.
    [Theory]
    [InlineData("f3() { local -n ref=$1; ref=x; }\nf2() { f3 \"$@\"; }\nf1() { local F1=F1; f2 F1; }\nf1", "F1")]
    [InlineData("f3() { local -n ref=$2; ref=x; }\nf2() { f3 a \"$@\"; }\nf2 V", "V")]
    [InlineData("f3() { local -n ref=$1; ref=x; }\nf2() { f3 \"$2\"; }\nf2 ignored W", "W")]
    [InlineData("f3() { local -n ref=$1; ref=x; f3 \"$1\"; }\nf3 R", "R")]
    public void Transpile_LocalNamerefTargetForwardedByAnotherFunction_EmitsTheCallersName(string script, string target)
        => Assert.Contains("($__nrt -ceq '" + target + "')", BashTranspiler.Transpile(script));

    // Oracle (bash): `cnt() { local -n c=$1; (( c++ )); }; n=4; cnt n` makes n 5. Arithmetic resolves names when it
    // runs, so the run-time nameref table learns the target the caller passed (whichever variant runs).
    [Fact]
    public void Transpile_LocalNamerefFromPositional_RecordsTheRunTimeTarget()
    {
        var ps = BashTranspiler.Transpile("cnt() { local -n c=$1; (( c++ )); }\ncnt n");
        int record = ps.IndexOf("[PsBash.Cmdlets.VarFrameStack]::LocalNameref('c', $__nrt); ", StringComparison.Ordinal);
        Assert.True(record > 0, "no run-time nameref record");
        Assert.Contains("$__nrt = \"", ps[..record]);
    }

    // Oracle (bash): `t=5; h() { local -n r=t; r=6; }; h` sets t; `local -n s=s` only warns (status 0) in a function;
    // `declare -n u=1` is refused naming the builtin. REGRESSION: a literal target was never recorded.
    [Theory]
    [InlineData("h() { local -n r=t; r=6; }", "[PsBash.Cmdlets.BashShellState]::SetNameref('r', 't')")]
    [InlineData("h() { local -n r=t; r=6; }", "$env:t = ")]
    [InlineData("h() { local -n r=t; r=6; }", "[PsBash.Cmdlets.VarFrameStack]::LocalAttributes('r')")]
    [InlineData("h() { local -n s=s; }", "local: warning: s: circular name reference")]
    [InlineData("h() { declare -n u=1; }", "declare: `1'': invalid variable name for name reference")]
    [InlineData("h() { local -n q=y; local -n q=x; q=4; }", "$env:x = ")]
    public void Transpile_LocalNamerefWithALiteralTarget_IsTheFunctionsOwn(string script, string expected)
        => Assert.Contains(expected, BashTranspiler.Transpile(script));

    // Oracle (bash): `declare -n g=y; f() { local g=9; }` leaves y alone: f's g is a plain variable.
    [Fact]
    public void Transpile_PlainLocalOverAnOuterNameref_DoesNotWriteTheTarget()
    {
        var ps = BashTranspiler.Transpile("declare -n g=y\nf() { local g=9; echo $g; }");
        string body = ps[ps.IndexOf("function f", StringComparison.Ordinal)..];
        Assert.DoesNotContain("$env:y", body);
    }

    // Oracle (bash): `declare c; c=x; declare -p c` prints `declare -- c="x"`. A plain no-value declaration only marks
    // the name; an empty array hid the later scalar. `declare -a e` still makes the array.
    [Fact]
    public void Transpile_DeclareWithNoValue_OnlyAnArrayDeclarationMakesTheArray()
    {
        Assert.DoesNotContain("$global:c = @()", BashTranspiler.Transpile("declare c"));
        Assert.Contains("$global:e = @()", BashTranspiler.Transpile("declare -a e"));
    }

    // Oracle (bash): a refused assignment in a function ends the function and its caller's line (status 1); in `$( )`
    // it ends only the substitution (`u=$(r=2); echo after` runs the echo); a readonly prefix binding is reported.
    [Fact]
    public void Transpile_RefusedAssignmentInAFunction_DiscardsTheLine()
        => Assert.Contains(PsBuild.AssignmentDiscardMark, BashTranspiler.Transpile("f() { local -r x=1; x=2; echo in; }\nf; echo after"));

    [Fact]
    public void Transpile_RefusedAssignmentInASubstitution_KeepsTheOuterLine()
        => Assert.Contains("Invoke-BashEcho after", BashTranspiler.Transpile("readonly r=1\nu=$(r=2); echo after"));

    [Fact]
    public void Transpile_ReadonlyPrefixBinding_IsReportedAndTheCommandRuns()
    {
        var ps = BashTranspiler.Transpile("readonly r=1\nr=3 true");
        Assert.Contains("r: readonly variable", ps);
        Assert.DoesNotContain("$env:r = '3'", ps);
    }

    // Oracle (bash): `f() { local -r ro=1; declare -p ro; }` prints `declare -r ro="1"`, and ro is writable after f
    // returns: the mark is the local's, taken back by its frame.
    [Fact]
    public void Transpile_LocalReadonly_MarksTheFramesLocal()
    {
        var ps = BashTranspiler.Transpile("f() { local -r ro=1; }");
        Assert.Contains("[PsBash.Cmdlets.VarFrameStack]::LocalAttributes('ro'); [PsBash.Cmdlets.BashShellState]::MarkReadonly('ro')", ps);
    }

    // Oracle: `f() { local -n lr=x; local +n lr; }; f` leaves no lr. The nameref local parks the caller's value and is the
    // frame's own, so the return restores it. REGRESSION: the +n write to $env:lr outlived the function.
    [Fact]
    public void Transpile_LocalNameref_IsTheFramesOwnName()
    {
        var ps = BashTranspiler.Transpile("f() { local -n lr=x; local +n lr; }");
        Assert.Contains("if ($null -eq $__lsd_lr) { $__lsd_lr = @{ v = $env:lr } }; [void]" + PsBuild.DeclareLocal("lr")
            + "; [PsBash.Cmdlets.VarFrameStack]::LocalAttributes('lr'); [PsBash.Cmdlets.BashShellState]::SetNameref('lr', 'x')", ps);
        Assert.Contains(PsBuild.RestoreLocal("lr"), ps);
    }

    // Oracle: under nounset `local -a x; echo $x` is "x: unbound variable"; an array's $x reads element 0.
    // REGRESSION: the array branch of a plain read skipped the check and printed an empty line.
    [Theory]
    [InlineData("set -u\nf() { local -a x; echo $x; }")]
    [InlineData("set -u\ndeclare -a x; echo \"$x\"")]
    public void Transpile_ArrayReadUnderNounset_IsChecked(string bash)
        => Assert.Contains("Reported('x: ', 'unbound variable')", BashTranspiler.Transpile(bash));

    // Oracle (bash): $BASHPID differs in ( ) and $( ) while $$ does not; the read goes through the subshell's id and a
    // $( ) that reads it takes the subshell scope that sets it (BashShellState.Save), even around a plain mapped echo.
    [Fact]
    public void Transpile_BashpidInCommandSub_ReadsTheSubshellIdInsideAScope()
    {
        var ps = BashTranspiler.Transpile("x=$(echo $BASHPID)");
        Assert.Contains("[PsBash.Cmdlets.BashRuntime]::BashPid($ExecutionContext.SessionState)", ps);
        Assert.Contains("[PsBash.Cmdlets.BashShellState]::Save(", ps);
        Assert.DoesNotContain("BashShellState]::Save(", BashTranspiler.Transpile("x=$(echo $$)"));
    }

    // Oracle (bash): `PWD=foo; echo $PWD` prints foo — $PWD is the shell variable cd maintains, not the location.
    [Fact]
    public void Transpile_PlainPwdRead_IsTheShellVariable()
        => Assert.Contains(PsBuild.PwdRead, BashTranspiler.Transpile("echo $PWD"));
    [Fact]
    public void Transpile_PatsubOfAnUnsetVarUnderNounset_ReadsItStrictly()
        => Assert.Contains("unbound variable", BashTranspiler.Transpile("set -o nounset\necho -${v/x/y}-"));

    [Fact]
    public void Transpile_PatsubWithoutNounset_DoesNotReadStrictly()
        => Assert.DoesNotContain("unbound variable", BashTranspiler.Transpile("echo -${v/x/y}-"));

    [Fact]
    public void Transpile_ForOverASingleDefaultExpansion_SplitsItsValue()
        => Assert.Contains("ConvertTo-BashWords", BashTranspiler.Transpile("for x in ${L:-zz}; do echo \"[$x]\"; done"));

    [Fact]
    public void Transpile_PatsubWithUnquotedVarBracketClass_UsesTheGlobEngine()
        => Assert.Contains("Substitute($env:t, ($env:p), 'z', 1)", BashTranspiler.Transpile("p='[z-a]'; t=foo; echo ${t//$p/z}"));

    // Oracle: bash expands the text first, then evaluates (`zero=0; echo $(( ${zero}11 ))` is octal 011 = 9).
    [Fact]
    public void Transpile_ArithWithAdjacentExpansion_SubstitutesTheTextBeforeEvaluating()
    {
        var ps = BashTranspiler.Transpile("zero=0\necho $(( ${zero}11 ))");
        Assert.Contains("Invoke-BashArith ('' + ", ps);
    }

    // Oracle: bash reports a bad arithmetic expression when it RUNS (status 1) and keeps going; it is no parse error.
    [Theory]
    [InlineData("echo $(( 1 + 2.3 ))")]
    [InlineData("echo $(( '1' + 2 ))")]
    public void Transpile_ArithSyntaxError_IsDeferredToRunTime(string bash)
        => Assert.Contains("Invoke-BashArith", BashTranspiler.Transpile(bash));

    // Oracle: inside [[ ]] numeric operands are arithmetic expressions (`[[ 017 -eq 15 ]]` is true); [ ] keeps plain integers.
    [Fact]
    public void Transpile_DoubleBracketNumericOperands_AreEvaluatedAsArithmetic()
    {
        Assert.Contains("Invoke-BashArith", BashTranspiler.Transpile("[[ a -eq 5 ]]"));
        Assert.DoesNotContain("Invoke-BashArith", BashTranspiler.Transpile("[ 1 -eq 5 ]"));
    }

    // Oracle: `[[ True || '' && '' ]]` is true — && binds tighter than ||.
    [Fact]
    public void Transpile_DoubleBracketAndBindsTighterThanOr()
        => Assert.Contains("-or ((", BashTranspiler.Transpile("[[ -n a || -n b && -n c ]]"));

    // Oracle: `set a b c` (no `--`) sets $1 $2 $3 exactly like `set -- a b c`.
    [Fact]
    public void Transpile_SetWithoutDashDash_SetsPositionalParameters()
        => Assert.Contains("BashPositional = @('a', 'b', 'c')", BashTranspiler.Transpile("set a b c"));

    // Oracle: unquoted $@ / $* split each parameter on IFS (IFS='' keeps "b c" whole) — never a bare array argument.
    [Fact]
    public void Transpile_UnquotedPositionalList_IsSplitPerParameter()
        => Assert.Contains("ConvertTo-BashWords -Prefix '' -Suffix '' -Fields @(", BashTranspiler.Transpile("printf '[%s]\\n' $@"));

    // Oracle: `echo -"$v"-` is ONE argument (-abc-); wrapping the dash-led word in more quotes split it into three.
    [Fact]
    public void Transpile_DashLedWordWithQuotedExpansion_IsOneArgument()
        => Assert.Contains("\"-$env:v-\"", BashTranspiler.Transpile("echo -\"$v\"-"));

    // Oracle: inside "…" a single quote in a ${v:-…} operand is an ordinary character.
    [Fact]
    public void Transpile_SingleQuotesInsideDoubleQuotedDefault_AreLiteral()
        => Assert.Contains("'a'", BashTranspiler.Transpile("echo \"${unset:-'a'}\""));

    // Oracle: ${x%$'b'*} removes from the quoted b on — the quoted part is literal, the * still a glob.
    [Fact]
    public void Transpile_PatternStripWithQuotedPartAndGlob_KeepsTheGlob()
        => Assert.Contains("Remove($env:x, 'b*', $true, $false)", BashTranspiler.Transpile("x=abc; echo ${x%$'b'*}"));

    // Oracle: `echo empty=${empty,}` — an unset variable converts to "" (no null-method call) beside a literal prefix.
    [Theory]
    [InlineData("echo e=${v,}")]
    [InlineData("echo e=${v^^}")]
    [InlineData("echo ${v,,A}")]
    public void Transpile_CaseConversion_IsNullSafeAndParseable(string bash)
        => Assert.DoesNotContain(".ToLower()", BashTranspiler.Transpile(bash).Replace("$args[0].Value.ToLower()", ""));

    // Oracle: `code='1+2'; ${a[$code]}` evaluates the variable's value as arithmetic on an indexed array (a[3]).
    [Fact]
    public void Transpile_IndexedArraySubscriptFromVariable_IsEvaluatedAsArithmetic()
        => Assert.Contains("Invoke-BashArith", BashTranspiler.Transpile("echo ${a[$code]}"));

    // Oracle: `${#a[0]}` is the length of ONE element; a plain scalar counts as a one-element array.
    [Fact]
    public void Transpile_ElementLengthAndScalarCount_UseTheElement()
    {
        Assert.Contains("BashLocale]::Length(", BashTranspiler.Transpile("echo ${#a[0]}"));
        Assert.Contains("elseif ($null -ne $env:c) { 1 }", BashTranspiler.Transpile("echo ${#c[@]}"));
    }

    // Oracle: `${a[@]: -5}` of 4 elements is empty (the offset reaches before the first element).
    [Fact]
    public void Transpile_ArraySliceNegativeOffsetBeforeStart_IsEmpty()
        => Assert.Contains("if ($__psbO -lt 0) { $__psbO = $__psbA.Count } }", BashTranspiler.Transpile("echo \"${a[@]: -5}\""));

    // Oracle: `unalias NAME` removes the alias; the shared alias cmdlet needs -u to tell the two apart.
    [Fact]
    public void Transpile_Unalias_RoutesToTheRemovalMode()
        => Assert.Contains("Invoke-BashAlias -u foo", BashTranspiler.Transpile("unalias foo"));

    // Oracle: `[ ] ]` tests the one-word string "]" (true); only the LAST `]` closes a `[` command.
    [Theory]
    [InlineData("[ ] ]")]
    [InlineData("[ -z ] ]")]
    [InlineData("[ -z -a ] ]")]
    public void Transpile_BracketWithOperandCloser_ParsesLastBracketAsTheClose(string script)
        => Assert.DoesNotContain("parse error", BashTranspiler.Transpile(script));

    // Oracle: `[ a -eq 1 ]` is status 2 ("integer expression expected"), not false and not a cast exception.
    [Fact]
    public void Transpile_BracketNumericTest_ReportsABadOperandAsStatus2()
    {
        var ps = BashTranspiler.Transpile("[ a -eq 1 ]");
        Assert.Contains("Invoke-BashTestInt", ps);
        Assert.Contains("if ($global:__BashTestBad) { 2 }", ps);
    }

    // Oracle: `[ \( foo \) ]` groups (true for a non-empty word); the escaped parens are not operands.
    [Theory]
    [InlineData("[ \\( foo \\) ]")]
    [InlineData("[ \\( -z foo \\) ]")]
    public void Transpile_BracketEscapedParens_AreGroupingNotStrings(string script)
        => Assert.DoesNotContain("'`('", BashTranspiler.Transpile(script));

    // Oracle: `set -u -- x y z` and `set -o errexit a b c` apply the options AND assign the parameters.
    [Theory]
    [InlineData("set -u -- x y z", "'x', 'y', 'z'")]
    [InlineData("set -o errexit a b c", "'a', 'b', 'c'")]
    public void Transpile_SetOptionsThenPositionals_AppliesBoth(string script, string positional)
    {
        var ps = BashTranspiler.Transpile(script);
        Assert.Contains("BashPositional = @(" + positional + ")", ps);
        Assert.DoesNotContain("'--'", ps);
    }

    // Oracle (bash 5.2): `set -o NOSUCH` / `set +o NOSUCH` report "invalid option name" and return 2 at run time;
    // it is not a transpile failure.
    [Fact]
    public void Transpile_SetUnknownLongOption_IsARuntimeError()
    {
        var ps = BashTranspiler.Transpile("set -o NOSUCH || true");
        Assert.Contains("set: NOSUCH: invalid option name", ps);
        Assert.Contains("$global:LASTEXITCODE = 2", ps);
    }

    // Oracle: `set -eux; echo $-` is `ehuxB` (no -c): $- never showed x.
    [Fact]
    public void Transpile_DollarDash_IncludesXtrace()
        => Assert.Contains("if ($global:__BashXtrace) { $f += 'x' }; $f += 'B'", BashTranspiler.Transpile("echo \"$-\""));

    // Oracle: `set -v; echo $-` is `hvB`, and $SHELLOPTS lists verbose; `set -v` used to be refused as unsupported.
    [Fact]
    public void Transpile_SetVerbose_IsTrackedInDollarDashAndShellopts()
    {
        Assert.Contains("$global:__BashVerbose = $true", BashTranspiler.Transpile("set -v"));
        Assert.Contains("$global:__BashVerbose = $false", BashTranspiler.Transpile("set +o verbose"));
        var dash = BashTranspiler.Transpile("echo \"$-\"");
        Assert.Contains("if ($global:__BashVerbose) { $f += 'v' }; if ($global:__BashXtrace) { $f += 'x' }", dash);
        Assert.Contains("if ($global:__BashVerbose) { $o += 'verbose' }", BashTranspiler.Transpile("echo \"$SHELLOPTS\""));
    }

    // Oracle: `export SHELLOPTS; set -x; bash -c 'echo 2'` traces `+ echo 2` in the child. `export SHELLOPTS` wrote
    // `$env:SHELLOPTS = $env:SHELLOPTS` (still nothing), so no child shell ever saw the options.
    [Fact]
    public void Transpile_ExportShellopts_PutsTheOptionsInTheEnvironment()
    {
        var ps = BashTranspiler.Transpile("export SHELLOPTS");
        Assert.Contains("$env:SHELLOPTS = $($o = @('braceexpand');", ps);
        Assert.DoesNotContain("$env:SHELLOPTS = $env:SHELLOPTS", ps);
        // Any other bare export keeps its own value.
        Assert.Contains("$env:FOO = $env:FOO", BashTranspiler.Transpile("export FOO"));
    }

    // Oracle: `set -n; echo hi` prints nothing, status 0, and no EXIT trap runs; `set +n` is a no-op.
    [Theory]
    [InlineData("set -n")]
    [InlineData("set -o noexec")]
    public void Transpile_SetNoexec_StopsTheShell(string bash)
        => Assert.Contains("$global:__BashTrapEXIT = $null; $global:LASTEXITCODE = 0; exit 0", BashTranspiler.Transpile(bash + "; echo hi"));

    // Oracle: `bash -c 'for i in a; do continue 1 x; done; echo st=$?'` prints nothing, status 1 (the error discards
    // the rest of the -c string); in a script file the rest of the LINE is dropped and the next line runs. Both go
    // through the one usage-error primitive (PsBuild.UsageDiscard), whose line scope the statement gets.
    [Theory]
    [InlineData("for i in a; do continue 1 x; done; echo st=$?", "continue")]
    [InlineData("for i in a; do break 1 2; done", "break")]
    public void Transpile_LoopControlTooManyArgs_EndsADashCShell(string bash, string keyword)
    {
        var ps = BashTranspiler.Transpile(bash);
        Assert.Contains(keyword + ": too many arguments'", ps);
        Assert.Contains(PsBuild.UsageDiscardMark, ps);
        Assert.Contains("[PsBash.Cmdlets.BashLineDiscard]::Enter()", ps);
    }

    // Oracle: `echo ${BASHOPTS:+set}` prints set: BASHOPTS lists the shopt options that are on.
    [Fact]
    public void Transpile_Bashopts_ReadsTheShoptTable()
        => Assert.Contains("[PsBash.Cmdlets.InvokeBashShoptCommand]::BashOpts()", BashTranspiler.Transpile("echo \"$BASHOPTS\""));

    // Oracle: `{ exit 9; } | { exit 2; } | { true; }; echo $?` prints 0, and with set -o pipefail 2: each stage is a
    // subshell, so its exit ends only that stage (it exited the whole shell with 9).
    [Fact]
    public void Transpile_ExitInBraceGroupPipeStage_EndsOnlyTheStage()
    {
        var ps = BashTranspiler.Transpile("{ exit 9; } | { exit 2; } | { true; }; echo $?");
        Assert.Contains("$global:LASTEXITCODE = 9; return", ps);
        Assert.DoesNotContain("exit 9", ps);
    }

    // Oracle: `exit 55 | (exit 44); echo ${PIPESTATUS[@]}` prints `55 44`: a simple `exit` pipe stage is a subshell too
    // (it exited the whole script with 55; oils pipeline "PIPESTATUS is set on simple commands").
    [Fact]
    public void Transpile_ExitSimplePipeStage_EndsOnlyTheStage()
    {
        var ps = BashTranspiler.Transpile("exit 55 | (exit 44); echo $?");
        Assert.Contains("$global:LASTEXITCODE = 55; return", ps);
        Assert.DoesNotContain("exit 55", ps);
    }

    // Oracle (bash 5.2): `false; { exit; } | cat; echo ${PIPESTATUS[*]}` prints `1 0`, `true; false | { exit; }; echo $?`
    // prints 0: every pipe stage is a subshell that starts with the status from before the pipeline, which a bare
    // `exit`/`return` or `$?` there sees. ps-bash printed `0 0` and 1: the stages started ahead had reset or set the status.
    // The invariant: every stage whose body is shell code starts with PsBuild.SeedStageStatus, and the pipeline captures
    // that status before any stage starts (PsBuild.WithPipeEntryStatus).
    [Theory]
    [InlineData("false; { exit; } | cat; echo ${PIPESTATUS[*]}", 1)]
    [InlineData("false; ( exit ) | cat", 1)]
    [InlineData("false; exit | cat", 1)]
    [InlineData("true; false | { exit; }; echo $?", 1)]
    [InlineData("false; { exit; } | { exit; } | cat", 2)]
    [InlineData("f() { return; }; false; f | cat", 1)]
    [InlineData("f() { echo $?; }; false; true | f", 1)]
    [InlineData("false; for i in 1; do echo $?; done | cat", 1)]
    [InlineData("false; while false; do :; done | if true; then exit; fi | case x in x) exit;; esac | cat", 3)]
    [InlineData("set -o pipefail; false; { exit; } | cat", 1)]
    [InlineData("false; true | echo $?", 1)]
    [InlineData("false; true | cat < /dev/null", 1)]
    public void Transpile_PipeStageWithShellBody_StartsWithTheStatusBeforeThePipeline(string bash, int seededStages)
    {
        var ps = BashTranspiler.Transpile(bash);
        int capture = ps.IndexOf(". { " + PsBuild.PipeEntryCapture + "; ", StringComparison.Ordinal);
        Assert.True(capture >= 0, ps);
        Assert.Equal(seededStages, CountOf(ps, PsBuild.SeedStageStatus));
        Assert.True(ps.IndexOf(PsBuild.SeedStageStatus, StringComparison.Ordinal) > capture, ps);
    }

    // A pipeline of simple commands only: each command sets its own status, so nothing is seeded or captured.
    [Theory]
    [InlineData("false; echo $? | cat")]
    [InlineData("seq 3 | sort -r | head -n 1")]
    public void Transpile_PipelineOfSimpleCommands_SeedsNoStage(string bash)
    {
        var ps = BashTranspiler.Transpile(bash);
        Assert.DoesNotContain(PsBuild.SeedStageStatus, ps);
        Assert.DoesNotContain(PsBuild.PipeEntryCapture, ps);
    }

    private static int CountOf(string text, string part)
    {
        int n = 0;
        for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }
    // Oracle: `f() { echo x; exit 3; }; f | cat; echo "after ${PIPESTATUS[*]}"` prints x, then `after 3 0`: a function
    // called as a pipe stage runs in that subshell, so exit in its body ends only the stage (it ended the whole script
    // with 3). The body is emitted once, so its exit decides at run time; the stage is the subshell scope that catches it.
    [Fact]
    public void Transpile_ExitInFunctionPipeStage_EndsOnlyTheStage()
    {
        var ps = BashTranspiler.Transpile("f() { echo x; exit 3; }; f | cat; echo \"after ${PIPESTATUS[*]}\"");
        Assert.Contains(PsBuild.DynamicExit("$global:LASTEXITCODE"), ps);
        Assert.Contains(PsBuild.ShellStateScope(PsBuild.SeedStageStatus + "f"), ps);
    }

    // Oracle: `exit 3` ends the script with 3. Only a function body's exit is decided at run time; a top-level exit
    // stays the plain PowerShell exit (a called function's exit falls back to it when no subshell scope is open).
    [Fact]
    public void Transpile_ExitOutsideAFunction_StaysAPlainExit()
    {
        var ps = BashTranspiler.Transpile("exit 3");
        Assert.Contains("exit $global:LASTEXITCODE", ps);
        Assert.DoesNotContain("BashSubshellExitException", ps);
    }

    // Oracle: `for pid in 1 2; do echo $pid; done` prints 1 and 2. `foreach ($pid in …)` threw "Cannot overwrite variable
    // PID" and the loop never ran (oils background "wait for N parallel jobs"); a PowerShell automatic name is iterated
    // through a private variable and read back as the shell variable.
    [Theory]
    [InlineData("pid")]
    [InlineData("HOME")]
    [InlineData("input")]
    public void Transpile_ForLoopOverPowerShellAutomaticName_IteratesAPrivateVariable(string name)
    {
        var ps = BashTranspiler.Transpile($"for {name} in 1 2; do echo ${name}; done");
        Assert.Contains("foreach ($__psbash_lv0 in", ps);
        Assert.Contains($"[PsBash.Cmdlets.BashVariableStore]::Set('{name}', [string]($__psbash_lv0))", ps);
        Assert.DoesNotContain($"foreach (${name} ", ps);
    }

    // Oracle: a script with `x=${ echo hi;}` then `echo "[$x] $?"` prints `[] 1`, and `echo ${ echo hi;} b` prints nothing:
    // a bad substitution (bash 5.3's `${ cmd;}` in 5.2) fails its command with status 1 and the script goes on. It threw
    // the parameter error that ends the script (oils command-sub-ksh).
    [Fact]
    public void Transpile_BadSubstitution_AbandonsOnlyItsCommand()
    {
        var ps = BashTranspiler.Transpile("x=${ echo hi;}\necho ${ echo hi;} b");
        Assert.Contains("Write-BashExpansionError '${ echo hi;}: ' 'bad substitution'", ps);
        Assert.Contains("$global:__BashExpansionFailed = $false; return", ps);
        Assert.DoesNotContain("BashParamErrorException]::Reported('${", ps);
        // The quoted text sits inside "$( )", whose end PowerShell finds by a quote-blind paren count: an unbalanced
        // `)` (`${ case foo in foo) ...;}`) ended it early, a parse error for the whole script (oils command-sub-ksh).
        var withParen = BashTranspiler.Transpile("x2=${ case foo in foo) echo sh-case ;; esac; }");
        Assert.Contains("Write-BashExpansionError ('${ case foo in foo' + [char]41 + ' echo sh-case ;; esac; }: ') 'bad substitution'", withParen);
    }

    // Oracle: `bash -c 'nosuchcmd; len=0'; echo $?` is 0. A bare assignment reset the status only in a script that reads
    // `$?`, so a script ending in one exited with the previous command's 127 (oils nul-bytes "read -n").
    [Fact]
    public void Transpile_ScriptEndingInAssignment_EndsWithStatusZero()
    {
        Assert.EndsWith("$env:len = \"0\"; $global:LASTEXITCODE = 0", BashTranspiler.Transpile("nosuchcmd\nlen=0"));
        // Only the last statement pays for it, and a lone assignment (status 0 already) does not.
        Assert.DoesNotContain("$env:a = \"1\"; $global:LASTEXITCODE = 0", BashTranspiler.Transpile("a=1\nnosuchcmd"));
        Assert.Equal("$env:a = \"1\"", BashTranspiler.Transpile("a=1"));
    }

    // Oracle: `a=(); a[-1]=1` exits 1 ("bad array subscript"). Set-BashElement leaves status 1, which a status of 0
    // set after the element assignment hid; the reset comes first instead.
    [Fact]
    public void Transpile_ScriptEndingInElementAssignment_KeepsABadSubscriptStatus()
    {
        var ps = BashTranspiler.Transpile("a=()\na[-1]=1");
        Assert.False(ps.EndsWith("$global:LASTEXITCODE = 0", StringComparison.Ordinal), ps);
        Assert.Contains("$global:LASTEXITCODE = 0; if ($null -eq $a", ps);
    }

    // Oracle: `for k in "${(@k)a}"; do echo in; done; echo $?` prints the bad substitution and 1, with no iteration.
    // The loop ran once over the empty word the failed expansion left.
    [Fact]
    public void Transpile_ForListThatFailsToExpand_RunsNoIteration()
    {
        var ps = BashTranspiler.Transpile("for k in \"${(@k)a}\"; do echo in; done");
        Assert.Contains("$global:__BashExpansionFailed = $false; $__psbash_fl = ", ps);
        Assert.Contains("if ($global:__BashExpansionFailed) { $global:__BashExpansionFailed = $false } else { ", ps);
        Assert.Contains("foreach ($__psbash_lv0 in $__psbash_fl)", ps);
        // A list that cannot fail keeps the plain loop.
        Assert.DoesNotContain("__psbash_fl", BashTranspiler.Transpile("for k in a b; do echo in; done"));
    }

    // Oracle: `cd / && set -- a b; echo $#` prints 2. The set operand was a bare `$global:BashPositional = @(...)` after
    // `&&`, "Unexpected token '='": a parse error for the whole script (seen running the spec helper in the Bash tool).
    [Fact]
    public void Transpile_SetDashDashAfterAndAnd_IsAChainOperand()
    {
        var ps = BashTranspiler.Transpile("cd / && set -- a b");
        Assert.Contains("&& $($global:BashPositional = @('a', 'b'))", ps);
        Assert.Contains("|| $($global:BashPositional = @())", BashTranspiler.Transpile("false || set --"));
    }

    // Oracle: `case $(( 42 / 0 )) in (*) echo hi ;; esac; echo inside=$?` prints the error and `inside=1`, never "hi":
    // the failed subject abandons the case. The switch ran its `*)` arm over the empty word (oils fatal-errors).
    [Fact]
    public void Transpile_CaseSubjectThatFailsToExpand_RunsNoArm()
    {
        var ps = BashTranspiler.Transpile("case $(( 42 / 0 )) in (*) echo hi ;; esac");
        Assert.Contains("$global:__BashExpansionFailed = $false; $__psbash_cx = ", ps);
        Assert.Contains("if ($global:__BashExpansionFailed) { $global:__BashExpansionFailed = $false } else { switch ($__psbash_cx)", ps);
        // A subject that cannot fail keeps the plain switch.
        Assert.DoesNotContain("__psbash_cx", BashTranspiler.Transpile("case $x in (*) echo hi ;; esac"));
    }

    // Oracle: `bash -c 'set -C; echo $-'` is `hBCc`: `c` comes last, and only under -c (the launcher's PSBASH_DASH_C mark);
    // a shell reading its script from stdin (`cat s | bash`, `bash -s`) or interactive has `s` there instead.
    [Fact]
    public void Transpile_DollarDash_EndsWithCUnderDashC()
        => Assert.Contains("if ($global:__BashNoclobber) { $f += 'C' }; if ($global:__BashErrtrace) { $f += 'E' }; if ([PsBash.Cmdlets.InvokeBashShoptCommand]::SetOptionOn('histexpand')) { $f += 'H' }; if ($env:PSBASH_DASH_C -eq '1') { $f += 'c' } elseif ($env:PSBASH_STDIN_SHELL -eq '1' -or [PsBash.Cmdlets.InvokeBashShoptCommand]::InteractiveShell) { $f += 's' }; $f", BashTranspiler.Transpile("echo \"$-\""));

    // Oracle: `bash -i -c 'echo $-'` is `himBHc`; `bash -c` has no i/m/H. Interactive = the launcher's PSBASH_DASH_I mark.
    [Fact]
    public void Transpile_DollarDash_InteractiveFlags()
    {
        var ps = BashTranspiler.Transpile("echo \"$-\"");
        Assert.Contains("$f += 'h'; if ([PsBash.Cmdlets.InvokeBashShoptCommand]::InteractiveShell) { $f += 'i' }; if ([PsBash.Cmdlets.InvokeBashShoptCommand]::SetOptionOn('monitor')) { $f += 'm' }", ps);
    }

    // Oracle: a bare `$BASH_SOURCE` / `${BASH_SOURCE##*/}` is element 0 of the array (the script file); it read $env:BASH_SOURCE.
    // The array is composed with the function frames (VarFrameStack.BashSource): in a function it is the defining file.
    [Theory]
    [InlineData("echo \"$BASH_SOURCE\"")]
    [InlineData("echo \"${BASH_SOURCE##*/}\"")]
    public void Transpile_BareBashSource_IsElementZero(string bash)
    {
        var ps = BashTranspiler.Transpile(bash);
        Assert.Contains("@([PsBash.Cmdlets.VarFrameStack]::BashSource($global:BASH_SOURCE))[0]", ps);
        Assert.DoesNotContain("$env:BASH_SOURCE", ps);
    }

    // Oracle: `set +` alone changes nothing (status 0); `set + a b` sets $1 $2 like `set -- a b`. Both used to be refused.
    [Theory]
    [InlineData("set +", "$global:LASTEXITCODE = 0")]
    [InlineData("set + a b", "$global:BashPositional = @('a', 'b')")]
    [InlineData("set + -", "$global:__BashXtrace = $false; $global:__BashVerbose = $false; Set-PSDebug -Off; $global:LASTEXITCODE = 0")]
    [InlineData("set - +", "$global:BashPositional = @('+')")]
    [InlineData("set -x - -v", "$global:BashPositional = @('-v')")]
    public void Transpile_SetLonePlus_IsAcceptedLikeSetDash(string bash, string expected)
        => Assert.Contains(expected, BashTranspiler.Transpile(bash));

    // Oracle: `echo hi | echo "[$(cat)]"` prints [hi]. echo ignores the pipe, but the command substitution inherits it.
    [Theory]
    [InlineData("echo hi | echo \"[$(cat)]\"")]
    [InlineData("echo hi | printf '<%s>' \"$(tr a-z A-Z)\"")]
    public void Transpile_SubstitutionInStdinIgnoringPipeStage_ReadsThePipe(string bash)
    {
        var ps = BashTranspiler.Transpile(bash);
        Assert.Contains(PsBash.Core.Parser.PsBuild.StdinFeed, ps);
        Assert.DoesNotContain("$null = @($input)", ps);
    }

    // Oracle: `a=A; echo -{$a,b}-` is `-A- -b-`; `{${a},b}` nests a braced expansion; an empty unquoted result is dropped.
    [Theory]
    [InlineData("a=A; echo -{$a,b}-", "$env:a")]
    [InlineData("a=A; echo {${a},b}_{c,d}", "$env:a")]
    [InlineData("a=A; echo {$a,b}_{c,d}", "Where-Object")]
    public void Transpile_BraceItemWithExpansion_ExpandsTheItem(string script, string expected)
    {
        var ps = BashTranspiler.Transpile(script);
        Assert.Contains(expected, ps);
        Assert.DoesNotContain("'-$a-'", ps);
    }

    // Oracle: `declare $var=x`, `declare "n=5"`, `export "$k=8"` assign the named scalar (they were "command not found").
    [Theory]
    [InlineData("declare $var=x")]
    [InlineData("declare \"n=5\"")]
    [InlineData("export \"$k=8\"")]
    public void Transpile_DeclareOrExportWithQuotedOrDynamicAssignment_AssignsTheScalar(string script)
    {
        var ps = BashTranspiler.Transpile(script);
        Assert.Contains("Set-Item -LiteralPath ('Env:'", ps);
        Assert.DoesNotContain("declare \"", ps);
    }

    // Oracle: `[[ foo =~ * ]] && echo true` is status 2 (malformed regex), and the chain does not run the echo.
    [Fact]
    public void Transpile_DoubleBracketBadRegexInChain_IsStatus2()
    {
        var ps = BashTranspiler.Transpile("[[ foo.py =~ * ]] && echo true");
        // The ERE engine reports regcomp's REG_BADRPT as status 2 (no exception to catch).
        Assert.Contains("if ($__psbash_ere.Status -eq 2) { $global:__BashTestBad = $true }", ps);
        Assert.Contains("$global:__BashTestBad) { 2 }", ps);
        Assert.Equal(2, PsBash.Core.Parser.BashEre.Match(PsBash.Core.Parser.BashEre.WordToRegex("*"), false, "foo.py").Status);
    }

    // A /tmp/... command word is emitted as the quoted TempPath string; without the call operator
    // it is a bare string expression, so `/tmp/x` printed itself instead of running (Linux/macOS).
    [Theory]
    [InlineData("/tmp/tool")]
    [InlineData("/tmp/tool arg")]
    public void Transpile_TmpCommandWord_IsInvokedNotEchoed(string script)
    {
        var ps = BashTranspiler.Transpile(script);
        Assert.Contains("& \"", ps);
    }
}
