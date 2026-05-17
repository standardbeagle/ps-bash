using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of Invoke-BashTest
/// (bash <c>test</c> / <c>[ ]</c> builtin) from a psm1 script function to a
/// binary cmdlet (PsBash.Cmdlets.dll).
///
/// Oracle: the psm1 <c>Invoke-BashTest</c> + <c>Test-BashCondition</c> pair.
/// File / string / integer / logical predicates are reproduced byte-for-byte
/// and exit codes are propagated via <c>$global:LASTEXITCODE</c> (0 true,
/// 1 false, 2 syntax error) — preserved here so tests can assert on the
/// observable exit code that bash callers depend on.
///
/// Both the <c>test</c> alias and the <c>[</c> alias resolve to the cmdlet.
/// For the <c>[</c> form the final operand must be <c>]</c>; the cmdlet
/// drops it before evaluating. Directive 12 injection probe ensures that
/// adversarial operand strings stay literal and never re-parse as PowerShell.
/// </summary>
public class InvokeBashTestCommandTests
{
    private static (bool? value, int exit, string[] errors) Run(string script)
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$global:LASTEXITCODE = 0; $error.Clear()").Invoke();
        pwsh.Commands.Clear();

        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        var exitResult = pwsh.AddScript("$global:LASTEXITCODE").Invoke();
        pwsh.Commands.Clear();
        int exit = 0;
        if (exitResult.Count > 0 && exitResult[0]?.BaseObject is int ix) exit = ix;

        var errs = pwsh.AddScript("$error | ForEach-Object { $_.ToString() }").Invoke();
        pwsh.Commands.Clear();

        bool? val = null;
        if (result.Count > 0 && result[0]?.BaseObject is bool b) val = b;

        return (val, exit, errs.Select(o => o?.ToString() ?? "").ToArray());
    }

    // ---- file predicates ----

    [Fact]
    public void FileExists_True()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var (val, exit, _) = Run($"Invoke-BashTest -f '{tmp.Replace("'", "''")}'");
            Assert.True(val);
            Assert.Equal(0, exit);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void FileExists_False()
    {
        var (val, exit, _) = Run("Invoke-BashTest -f 'Z:\\definitely-not-here-xyz.txt'");
        Assert.False(val);
        Assert.Equal(1, exit);
    }

    [Fact]
    public void DirExists_True()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "psbash-test-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var (val, exit, _) = Run($"Invoke-BashTest -d '{tmp.Replace("'", "''")}'");
            Assert.True(val);
            Assert.Equal(0, exit);
        }
        finally { Directory.Delete(tmp); }
    }

    [Fact]
    public void DirExists_False_OnFile()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var (val, exit, _) = Run($"Invoke-BashTest -d '{tmp.Replace("'", "''")}'");
            Assert.False(val);
            Assert.Equal(1, exit);
        }
        finally { File.Delete(tmp); }
    }

    // ---- string predicates ----

    [Fact]
    public void Empty_Z_True()
    {
        var (val, exit, _) = Run("Invoke-BashTest -z ''");
        Assert.True(val);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void NonEmpty_N_True()
    {
        var (val, exit, _) = Run("Invoke-BashTest -n 'hello'");
        Assert.True(val);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void StringEqual_True()
    {
        var (val, exit, _) = Run("Invoke-BashTest 'abc' '=' 'abc'");
        Assert.True(val);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void StringNotEqual_True()
    {
        var (val, exit, _) = Run("Invoke-BashTest 'abc' '!=' 'xyz'");
        Assert.True(val);
        Assert.Equal(0, exit);
    }

    // ---- integer predicates ----

    [Fact]
    public void IntegerEq_True()
    {
        var (val, exit, _) = Run("Invoke-BashTest '5' '-eq' '5'");
        Assert.True(val);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void IntegerLt_True()
    {
        var (val, exit, _) = Run("Invoke-BashTest '3' '-lt' '5'");
        Assert.True(val);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void IntegerGt_False()
    {
        var (val, exit, _) = Run("Invoke-BashTest '3' '-gt' '5'");
        Assert.False(val);
        Assert.Equal(1, exit);
    }

    // ---- logical ----

    [Fact]
    public void Not_True()
    {
        var (val, exit, _) = Run("Invoke-BashTest '!' ''");
        Assert.True(val);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void And_True()
    {
        // -z '' && -n 'x' both true
        var (val, exit, _) = Run("Invoke-BashTest -z '' -a -n 'x'");
        Assert.True(val);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void Or_TrueWhenOneSideTrue()
    {
        // -z 'x' (false) -o -n 'y' (true) -> true
        var (val, exit, _) = Run("Invoke-BashTest -z 'x' -o -n 'y'");
        Assert.True(val);
        Assert.Equal(0, exit);
    }

    // ---- bracket form ----

    [Fact]
    public void BracketForm_RequiresClosingBracket()
    {
        // `[ -n abc ]` via the [ alias — invoked via the call operator since
        // PowerShell's parser treats a bare `[` as the start of a type cast.
        var (val, exit, _) = Run("& '[' -n 'abc' ']'");
        Assert.True(val);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void BracketForm_TrailingCloseStripped()
    {
        // Defensive parity: even when invoked via the cmdlet's canonical
        // name (not the `[` alias), a trailing `]` token is stripped before
        // evaluation, so `Invoke-BashTest -n abc ]` behaves identically to
        // `Invoke-BashTest -n abc`.
        var (val, exit, _) = Run("Invoke-BashTest -n 'abc' ']'");
        Assert.True(val);
        Assert.Equal(0, exit);
    }

    // ---- alias resolution ----

    [Fact]
    public void TestAlias_Resolves()
    {
        var (val, exit, _) = Run("test -n 'hello'");
        Assert.True(val);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void BracketAlias_Resolves()
    {
        // Invoke the `[` alias via the call operator (`& '['`) since the
        // PowerShell parser treats a bare leading `[` as a type-cast token.
        var (val, exit, _) = Run("& '[' 'a' '=' 'a' ']'");
        Assert.True(val);
        Assert.Equal(0, exit);
    }

    // ---- --help ----

    [Fact]
    public void Help_EmitsUsage()
    {
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript("Invoke-BashTest --help").Invoke();
        var lines = result.Select(o => o?.ToString() ?? "").ToArray();
        Assert.NotEmpty(lines);
    }

    // ---- Directive 12: injection probe ----

    [Fact]
    public void Injection_ScriptblockChars_InOperand_StaysLiteral()
    {
        // Adversarial operand containing PS scriptblock + $() chars.
        // The cmdlet must NOT evaluate it; -n on the literal string is true
        // (it's non-empty) but no nested PowerShell evaluation occurs.
        var (val, exit, _) = Run(
            "Invoke-BashTest -n '$(throw \"INJECTED\"); rm -rf /'");
        Assert.True(val);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void Injection_StringEqual_BothSidesLiteral()
    {
        // Compare two strings that both contain script-injection payloads.
        // The cmdlet must compare them as literals, not eval.
        var (val, exit, _) = Run(
            "Invoke-BashTest '$(throw 1)' '=' '$(throw 1)'");
        Assert.True(val);
        Assert.Equal(0, exit);
    }
}
