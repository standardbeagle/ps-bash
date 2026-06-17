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

    // ---- Full snapshot rg-shim: every fragment must be present and well-formed ----

    [Fact]
    public void Transpile_RgShimFunction_ProducesNoBrokenTokens()
    {
        const string shim = """
            function rg {
              local _cc_bin="${CLAUDE_CODE_EXECPATH:-}"
              [[ -x $_cc_bin ]] || _cc_bin=/c/claude.exe
              if [[ ! -x $_cc_bin ]]; then command rg "$@"; return; fi
            }
            """;
        var result = BashTranspiler.Transpile(shim);
        Assert.DoesNotContain("BashLastArg", result);
        Assert.DoesNotContain("'-x'", result);
    }
}
