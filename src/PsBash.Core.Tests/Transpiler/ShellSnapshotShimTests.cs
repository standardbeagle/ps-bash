using Xunit;
using PsBash.Core.Transpiler;

namespace PsBash.Core.Tests.Transpiler;

/// <summary>
/// Regression tests for the Claude Code Bash-tool shell snapshot. When ps-bash is the
/// configured shell, Claude Code sources a generated snapshot that defines an <c>rg</c>
/// wrapper function. Two transpiler bugs made the emitted PowerShell unparseable:
///   (RC1) <c>$_cc_bin</c> was lexed as the special <c>$_</c> (last-arg) variable followed
///         by a literal <c>cc_bin</c>, producing the bogus token
///         <c>$global:BashLastArgcc_bin</c>. <c>_cc_bin</c> is a single ordinary variable.
///   (RC2) the unary file-test operator <c>-x</c> in <c>[[ -x $f ]]</c> was unhandled and
///         fell through to a fallback that emitted <c>('-x' $f)</c> — two adjacent tokens
///         with no operator, which PowerShell rejects.
/// Together they cascaded into "missing closing )", "Try is missing its Catch", etc.
/// </summary>
public class ShellSnapshotShimTests
{
    // ---- RC1: $_name is one variable, not $_ + literal ----

    [Fact]
    public void Transpile_DollarUnderscoreName_ReadsWholeVariableName()
    {
        var result = BashTranspiler.Transpile("echo $_cc_bin");
        Assert.DoesNotContain("BashLastArg", result);
        Assert.Contains("_cc_bin", result);
    }

    [Fact]
    public void Transpile_DollarUnderscoreNameInDoubleQuotes_ReadsWholeVariableName()
    {
        var result = BashTranspiler.Transpile("echo \"$_cc_bin\"");
        Assert.DoesNotContain("BashLastArg", result);
        Assert.Contains("_cc_bin", result);
    }

    [Fact]
    public void Transpile_BareDollarUnderscore_StillMapsToLastArg()
    {
        // $_ followed by a non-name char must keep its special meaning.
        var result = BashTranspiler.Transpile("echo $_ end");
        Assert.Contains("BashLastArg", result);
    }

    // ---- RC2: unary file-test operators translate to real PowerShell ----

    [Fact]
    public void Transpile_ExtendedTestDashX_DoesNotEmitBareFlagToken()
    {
        var result = BashTranspiler.Transpile("[[ -x $f ]]");
        // The broken fallback emitted the flag as a literal value: ('-x' $f).
        Assert.DoesNotContain("'-x'", result);
        Assert.Contains("Test-Path", result);
    }

    [Fact]
    public void Transpile_ExtendedTestNegatedDashX_DoesNotEmitBareFlagToken()
    {
        var result = BashTranspiler.Transpile("[[ ! -x $f ]]");
        Assert.DoesNotContain("'-x'", result);
        Assert.Contains("Test-Path", result);
    }

    [Theory]
    [InlineData("-r")]
    [InlineData("-w")]
    [InlineData("-s")]
    [InlineData("-L")]
    [InlineData("-h")]
    public void Transpile_ExtendedTestUnaryFileOps_DoNotEmitBareFlagTokens(string op)
    {
        var result = BashTranspiler.Transpile($"[[ {op} $f ]]");
        Assert.DoesNotContain($"'{op}'", result);
    }

    // ---- RC3: word-bearing parameter expansions expand their argument word ----
    //
    // The argument of ${VAR:-w} / ${VAR:=w} / ${VAR:+w} / ${VAR:?w} (and the colon-less
    // forms) is an expandable WORD in bash. The original port stored the whole suffix as a
    // raw string and the emitter wrapped that slice in quotes, so an argument containing an
    // expansion survived verbatim. The portable-"$@" idiom ${1+"$@"} (line 10 of the Claude
    // Code rg shim) emitted `... ? ""$@"" : ""` — a doubled quote plus an untranslated $@ —
    // which PowerShell could not parse, spewing errors on EVERY Bash-tool command.

    [Fact]
    public void Transpile_AlternativeValueWithPositionalParams_ExpandsDollarAt()
    {
        // ${1+"$@"}: must NOT leave the literal $@ (untranslated) nor a doubled-quote artifact.
        var result = BashTranspiler.Transpile("command rg ${1+\"$@\"}");
        Assert.DoesNotContain("\"\"$@", result);   // the broken ""$@"" doubled-quote token
        Assert.DoesNotContain("$@", result);        // $@ must translate, never survive literally
        Assert.Contains("BashPositional", result);  // it became the positional-param expansion
    }

    [Fact]
    public void Transpile_DefaultValueWithVariable_ExpandsInnerVariable()
    {
        // ${x:-$y}: the default word $y must become $env:y, not the literal text "$y".
        var result = BashTranspiler.Transpile("echo ${x:-$y}");
        Assert.Contains("env:y", result);
    }

    [Fact]
    public void Transpile_DefaultValueWithCommandSub_ExpandsCommandSubstitution()
    {
        // ${name:-$(whoami)}: the default must run the command, not emit literal $(whoami) text.
        var result = BashTranspiler.Transpile("echo ${name:-$(whoami)}");
        Assert.Contains("Invoke-BashWhoami", result);
    }

    [Fact]
    public void Transpile_DefaultValueLiteral_StillEmitsLiteralString()
    {
        // Regression guard: a plain literal default must remain a literal string.
        var result = BashTranspiler.Transpile("echo ${x:-hello}");
        Assert.Contains("hello", result);
        Assert.DoesNotContain("env:hello", result);
    }

    [Fact]
    public void Transpile_EmptyDefaultInsideDoubleQuotes_UsesSingleQuotedEmpty()
    {
        // local _cc_bin="${CLAUDE_CODE_EXECPATH:-}" (snapshot line 8). The empty default lands
        // inside "$( … )"; a nested EMPTY double-quoted string ("") makes PowerShell mis-parse
        // ("string is missing the terminator"). Must emit '' (single-quoted empty) instead.
        var result = BashTranspiler.Transpile("x=\"${CLAUDE_CODE_EXECPATH:-}\"");
        Assert.Contains("?? '')", result);
        Assert.DoesNotContain("?? \"\")", result);
    }

    // ---- Full snapshot rg-shim: every fragment must be present and well-formed ----

    [Fact]
    public void Transpile_RgShimFunction_ProducesNoBrokenTokens()
    {
        const string shim = """
            function rg {
              local _cc_bin="${CLAUDE_CODE_EXECPATH:-}"
              [[ -x $_cc_bin ]] || _cc_bin=/c/claude.exe
              if [[ ! -x $_cc_bin ]]; then command rg ${1+"$@"}; return; fi
            }
            """;
        var result = BashTranspiler.Transpile(shim);
        Assert.DoesNotContain("BashLastArg", result);
        Assert.DoesNotContain("'-x'", result);
        Assert.DoesNotContain("\"\"$@", result);
        Assert.DoesNotContain("$@", result);
    }

    // ---- RC4: an EMPTY case-arm body must not swallow its own `;;` ----
    //
    // The snapshot's pkill guard (added 2026-07) classifies pkill flags with a
    // `case` whose ignore-arms have empty bodies (`--signal=*|-e|--echo) ;;`).
    // ParseCaseArm called SkipTerminators() after `)`, which ate the `;;`; the
    // parser then read the NEXT arm's pattern as a command word and threw
    // "Unexpected token ')' (RParen)" — on EVERY Bash-tool command, since the
    // snapshot is sourced per invocation.

    [Fact]
    public void Transpile_PkillGuardFunction_ParsesAndEmitsAllCaseArms()
    {
        const string shim = """
            function pkill {
              if [ -n "${CLAUDE_PID:-}" ] && [ -r "/proc/${CLAUDE_PID}/comm" ]; then
                local _cc_skip="" _cc_a
                local -a _cc_probe=()
                for _cc_a in ${1+"$@"}; do
                  if [ -n "$_cc_skip" ]; then _cc_skip=""; continue; fi
                  case "$_cc_a" in
                    --signal) _cc_skip=1 ;;
                    --signal=*|-e|--echo) ;;
                    -[0-9]*) ;;
                    -[PUGOF]?*) _cc_probe+=("$_cc_a") ;;
                    *) _cc_probe+=("$_cc_a") ;;
                  esac
                done
              fi
              command pkill ${1+"$@"}
            }
            """;

        // Must not throw a bash ParseException, and must keep every arm: the
        // bug silently consumed the terminator rather than dropping an arm, so
        // assert the last arm's action survived into the emitted PowerShell.
        var result = BashTranspiler.Transpile(shim);
        Assert.Contains("_cc_probe", result);
        Assert.Contains("_cc_skip", result);
    }

    [Fact]
    public void Transpile_CaseWithEmptyArmBody_FollowedByAnotherArm_DoesNotThrow()
    {
        // Minimal reduction of the snapshot failure.
        var result = BashTranspiler.Transpile("case $a in x) ;; *) echo d ;; esac");
        Assert.Contains("echo", result, System.StringComparison.OrdinalIgnoreCase);
    }
}
