using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 Phase 1b migration of
/// Invoke-BashPrintf / Invoke-BashPwd from PsBash.psm1 script functions to
/// binary cmdlets (PsBash.Cmdlets.dll).
///
/// Oracle: the original psm1 functions, which were modeled on the bash
/// builtins. printf is a pure text transform (no file / pipeline surface), so
/// the applicable failure-surface axes are missing operand (no format
/// string), unicode, escape-sequence handling, fewer-args-than-conversions,
/// and quoting/injection (an operand containing PowerShell scriptblock
/// characters must be treated as a literal string, never executed). pwd reads
/// runspace state ($global:__PsBashCwd override + current location), so its
/// axes are the override path, the default path, the -P physical path,
/// StrictMode safety on an undefined override, and backslash normalization.
///
/// Note: Invoke-BashEcho was deliberately NOT migrated — echo's -e/-n/-E short
/// flags prefix-collide with PSCmdlet common parameters (-ErrorAction etc.),
/// so it stays a psm1 `param()` function. See the psm1 comment at the echo
/// definition.
///
/// The PwshTestFixture loads psm1 (which no longer defines printf / pwd) then
/// imports PsBash.Cmdlets.dll, mirroring the host load order — so these tests
/// also prove the function-shadowing removal worked and the psm1
/// `Set-Alias printf/pwd` lines still resolve to the cmdlets.
/// </summary>
public class InvokeBashPrintfPwdCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashPrintfPwdCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    private string[] RunLines(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        var err = pwsh.AddScript("$error | Select-Object -First 1").Invoke();
        pwsh.Commands.Clear();
        Assert.True(err.Count == 0 || err[0] == null,
            $"Unexpected error running [{script}]: {(err.Count > 0 ? err[0]?.ToString() : "none")}");

        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    private string[] RunBashText(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        return result
            .Select(o =>
            {
                var prop = o?.Properties["BashText"];
                return prop != null ? prop.Value?.ToString() ?? "" : o?.ToString() ?? "";
            })
            .ToArray();
    }

    // ---- printf: core behavior ----

    [Fact]
    public void Printf_PlainFormat_NoConversions()
    {
        var lines = RunBashText("Invoke-BashPrintf 'hello world'");
        Assert.Equal(new[] { "hello world" }, lines);
    }

    [Fact]
    public void Printf_StringConversion()
    {
        var lines = RunBashText("Invoke-BashPrintf '%s-%s' 'a' 'b'");
        Assert.Equal(new[] { "a-b" }, lines);
    }

    [Fact]
    public void Printf_IntConversion()
    {
        var lines = RunBashText("Invoke-BashPrintf '%d' '42'");
        Assert.Equal(new[] { "42" }, lines);
    }

    [Fact]
    public void Printf_IntZeroPadWidth()
    {
        var lines = RunBashText("Invoke-BashPrintf '%05d' '42'");
        Assert.Equal(new[] { "00042" }, lines);
    }

    [Fact]
    public void Printf_IntLeftAlignWidth()
    {
        var lines = RunBashText("Invoke-BashPrintf '%-5d|' '42'");
        Assert.Equal(new[] { "42   |" }, lines);
    }

    [Fact]
    public void Printf_IntShowPlus()
    {
        var lines = RunBashText("Invoke-BashPrintf '%+3d' '7'");
        Assert.Equal(new[] { " +7" }, lines);
    }

    [Fact]
    public void Printf_FloatPrecision()
    {
        var lines = RunBashText("Invoke-BashPrintf '%.2f' '3.14159'");
        Assert.Equal(new[] { "3.14" }, lines);
    }

    [Fact]
    public void Printf_FloatDefaultPrecision()
    {
        var lines = RunBashText("Invoke-BashPrintf '%f' '2.5'");
        Assert.Equal(new[] { "2.500000" }, lines);
    }

    [Fact]
    public void Printf_HexConversion()
    {
        var lines = RunBashText("Invoke-BashPrintf '%x' '255'");
        Assert.Equal(new[] { "ff" }, lines);
    }

    [Fact]
    public void Printf_HexWithHashPrefix()
    {
        var lines = RunBashText("Invoke-BashPrintf '%#X' '255'");
        Assert.Equal(new[] { "0XFF" }, lines);
    }

    [Fact]
    public void Printf_OctalConversion()
    {
        var lines = RunBashText("Invoke-BashPrintf '%o' '8'");
        Assert.Equal(new[] { "10" }, lines);
    }

    [Fact]
    public void Printf_CharConversion_FromIntCodepoint()
    {
        // 65 coerces to int -> [char]65 -> 'A'.
        var lines = RunBashText("Invoke-BashPrintf '%c' '65'");
        Assert.Equal(new[] { "A" }, lines);
    }

    [Fact]
    public void Printf_CharConversion_FromString_TakesFirstChar()
    {
        var lines = RunBashText("Invoke-BashPrintf '%c' 'xyz'");
        Assert.Equal(new[] { "x" }, lines);
    }

    [Fact]
    public void Printf_PercentLiteral()
    {
        var lines = RunBashText("Invoke-BashPrintf '100%%'");
        Assert.Equal(new[] { "100%" }, lines);
    }

    [Fact]
    public void Printf_EscapeSequencesInFormat()
    {
        // \n in the format becomes a record boundary -> the single
        // NoTrailingNewline object carries an embedded newline.
        var lines = RunBashText(@"Invoke-BashPrintf 'a\nb'");
        Assert.Equal(new[] { "a\nb" }, lines);
    }

    [Fact]
    public void Printf_DoubleBackslashN_LiteralBackslashN()
    {
        // \\n -> literal backslash + n (sentinel two-pass), not a newline.
        var lines = RunBashText(@"Invoke-BashPrintf 'a\\nb'");
        Assert.Equal(new[] { @"a\nb" }, lines);
    }

    [Fact]
    public void Printf_BConversion_ExpandsEscapesInArgument()
    {
        var lines = RunBashText(@"Invoke-BashPrintf '%b' 'x\ty'");
        Assert.Equal(new[] { "x\ty" }, lines);
    }

    [Fact]
    public void Printf_StringWidthPadLeft()
    {
        var lines = RunBashText("Invoke-BashPrintf '%5s' 'ab'");
        Assert.Equal(new[] { "   ab" }, lines);
    }

    [Fact]
    public void Printf_StringWidthLeftAlign()
    {
        var lines = RunBashText("Invoke-BashPrintf '%-5s|' 'ab'");
        Assert.Equal(new[] { "ab   |" }, lines);
    }

    [Fact]
    public void Printf_NoArguments_SetsExitCodeTwo()
    {
        // Missing-operand axis: printf with no format delegates to the psm1
        // Write-BashError, which sets $global:LASTEXITCODE = 2. The exit code
        // is the observable, runspace-visible contract (the stderr sink is a
        // script-scoped concern owned by Write-BashError).
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$global:LASTEXITCODE = 0").Invoke();
        pwsh.Commands.Clear();
        pwsh.AddScript("Invoke-BashPrintf 2>$null").Invoke();
        pwsh.Commands.Clear();
        var code = pwsh.AddScript("$global:LASTEXITCODE").Invoke();
        Assert.Single(code);
        Assert.Equal("2", code[0]?.ToString());
    }

    [Fact]
    public void Printf_NoArguments_ProducesNoFormattedOutput()
    {
        var lines = RunBashText("Invoke-BashPrintf 2>$null");
        Assert.Empty(lines);
    }

    [Fact]
    public void Printf_FewerArgsThanConversions_EmitsEmptyForMissing()
    {
        // psm1 oracle: a conversion with no remaining arg appends nothing.
        var lines = RunBashText("Invoke-BashPrintf '%s-%s' 'only'");
        Assert.Equal(new[] { "only-" }, lines);
    }

    [Fact]
    public void Printf_UnicodeArgument_PreservedExactly()
    {
        var lines = RunBashText("Invoke-BashPrintf '%s' 'é你好\U0001F600'");
        Assert.Equal(new[] { "é你好\U0001F600" }, lines);
    }

    [Fact]
    public void Printf_ArgumentLookingLikeScriptBlock_TreatedAsLiteral()
    {
        // Quoting/injection axis: a scriptblock-looking %s argument must be
        // emitted as a literal string, never executed.
        var lines = RunBashText("Invoke-BashPrintf '%s' '$(rm -rf x);{evil}'");
        Assert.Equal(new[] { "$(rm -rf x);{evil}" }, lines);
    }

    [Fact]
    public void Printf_EmitsNoTrailingNewlineTextOutputObject()
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript("Invoke-BashPrintf 'x'").Invoke();
        Assert.Single(result);
        Assert.Contains("PsBash.TextOutput", result[0]!.TypeNames);
        Assert.NotNull(result[0]!.Properties["NoTrailingNewline"]);
        Assert.True((bool)result[0]!.Properties["NoTrailingNewline"].Value);
    }

    [Fact]
    public void Printf_AliasResolvesToCmdlet()
    {
        var lines = RunBashText("printf '%s' 'aliased'");
        Assert.Equal(new[] { "aliased" }, lines);
    }

    [Fact]
    public void Printf_Help_DelegatesToShowBashHelp()
    {
        var lines = RunLines("Invoke-BashPrintf --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("Usage: printf"));
    }

    // ---- pwd: core behavior ----

    [Fact]
    public void Pwd_DefaultPath_ReturnsCurrentLocation()
    {
        // No __PsBashCwd override -> falls back to PowerShell's current
        // location. Backslashes are normalized to forward slashes.
        var lines = RunBashText(
            "Set-Location ([System.IO.Path]::GetTempPath()); Invoke-BashPwd");
        Assert.Single(lines);
        Assert.DoesNotContain('\\', lines[0]);
        Assert.NotEqual("", lines[0]);
    }

    [Fact]
    public void Pwd_HonorsPsBashCwdOverride()
    {
        var lines = RunBashText(
            "$global:__PsBashCwd = '/custom/override/path'; Invoke-BashPwd");
        Assert.Equal(new[] { "/custom/override/path" }, lines);
    }

    [Fact]
    public void Pwd_UndefinedOverride_DoesNotThrow_StrictModeSafe()
    {
        // Regression: $global:__PsBashCwd undefined must not trip StrictMode.
        var lines = RunBashText(
            "Set-StrictMode -Version Latest; " +
            "Set-Location ([System.IO.Path]::GetTempPath()); Invoke-BashPwd");
        Assert.Single(lines);
        Assert.NotEqual("", lines[0]);
    }

    [Fact]
    public void Pwd_NormalizesBackslashesInOverride()
    {
        var lines = RunBashText(
            @"$global:__PsBashCwd = 'C:\Users\me'; Invoke-BashPwd");
        Assert.Equal(new[] { "C:/Users/me" }, lines);
    }

    [Fact]
    public void Pwd_PhysicalFlag_ResolvesProviderPath()
    {
        // -P resolves the physical provider path of the current location and
        // ignores any __PsBashCwd override.
        var lines = RunBashText(
            "$global:__PsBashCwd = '/ignored/override'; " +
            "Set-Location ([System.IO.Path]::GetTempPath()); Invoke-BashPwd -P");
        Assert.Single(lines);
        Assert.DoesNotContain('\\', lines[0]);
        Assert.NotEqual("/ignored/override", lines[0]);
        Assert.NotEqual("", lines[0]);
    }

    [Fact]
    public void Pwd_EmitsTypedPwdLineObject()
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(
            "Set-Location ([System.IO.Path]::GetTempPath()); Invoke-BashPwd").Invoke();
        Assert.Single(result);
        Assert.Contains("PsBash.PwdLine", result[0]!.TypeNames);
        Assert.NotNull(result[0]!.Properties["BashText"]);
    }

    [Fact]
    public void Pwd_AliasResolvesToCmdlet()
    {
        var lines = RunBashText(
            "Set-Location ([System.IO.Path]::GetTempPath()); pwd");
        Assert.Single(lines);
        Assert.NotEqual("", lines[0]);
    }

    [Fact]
    public void Pwd_Help_DelegatesToShowBashHelp()
    {
        var lines = RunLines("Invoke-BashPwd --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("Usage: pwd"));
    }
}
