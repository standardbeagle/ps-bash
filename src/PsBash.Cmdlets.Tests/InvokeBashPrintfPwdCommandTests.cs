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
    public void Printf_PercentS_NumericLookingArgs_PrintAsTyped()
    {
        // bash (oracle-checked): printf '[%s][%s][%s][%c][%5s]' 007 1.50 1e3 007 0x1F
        //   -> [007][1.50][1e3][0][ 0x1F]. %s/%c must not reformat through a numeric coercion.
        var lines = RunBashText("Invoke-BashPrintf '[%s][%s][%s][%c][%5s]' '007' '1.50' '1e3' '007' '0x1F'");
        Assert.Equal(new[] { "[007][1.50][1e3][0][ 0x1F]" }, lines);
    }

    [Fact]
    public void Printf_IntConversion()
    {
        var lines = RunBashText("Invoke-BashPrintf '%d' '42'");
        Assert.Equal(new[] { "42" }, lines);
    }

    [Fact]
    public void Printf_PercentC_PrintsFirstCharOfArg_NotAsciiCode()
    {
        // bash: printf '%c%c' 65 66 -> "66" (first char of "65", first char of "66"),
        // NOT "AB" (which would be the ASCII-code interpretation).
        var lines = RunBashText("Invoke-BashPrintf '%c%c' '65' '66'");
        Assert.Equal(new[] { "66" }, lines);
    }

    [Fact]
    public void Printf_PercentC_StringArg_TakesFirstChar()
    {
        var lines = RunBashText("Invoke-BashPrintf '%c' 'hello'");
        Assert.Equal(new[] { "h" }, lines);
    }

    [Fact]
    public void Printf_FloatZeroPadWidth()
    {
        // bash: printf '%05.2f' 3.14159 -> "03.14" (width 5, zero-padded). The old
        // code space-padded then Trim()'d it off, yielding "3.14".
        var lines = RunBashText("Invoke-BashPrintf '%05.2f' '3.14159'");
        Assert.Equal(new[] { "03.14" }, lines);
    }

    [Fact]
    public void Printf_FloatSpaceWidth()
    {
        var lines = RunBashText("Invoke-BashPrintf '%8.2f' '3.14159'");
        Assert.Equal(new[] { "    3.14" }, lines);
    }

    [Fact]
    public void Printf_FloatLeftAlignWidth()
    {
        var lines = RunBashText("Invoke-BashPrintf '[%-8.2f]' '3.14159'");
        Assert.Equal(new[] { "[3.14    ]" }, lines);
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
    public void Printf_CharConversion_TakesFirstCharOfArg()
    {
        // bash %c is the FIRST CHARACTER of the argument string: '65' -> '6'
        // (NOT the ASCII-code interpretation [char]65 -> 'A').
        var lines = RunBashText("Invoke-BashPrintf '%c' '65'");
        Assert.Equal(new[] { "6" }, lines);
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
        // bash prints the builtin help on stdout AND the invalid-option diagnostic (exit 2).
        var r = CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashPrintf '--help'");
        Assert.Contains(r.Stdout.Split('\n'), l => l.Contains("Usage: printf"));
        r.AssertFailed(2, "--: invalid option");
    }

    // ---- printf: builtin options (bash semantics, not getopt) ----

    [Fact]
    public void Printf_DashV_AssignsVariableAndPrintsNothing()
    {
        var pwsh = _fixture.AcquireFresh();
        var r = CmdResult.Run(pwsh, "Invoke-BashPrintf '-v' myvar '%s-%d' a 5; $myvar").AssertSuccess();
        Assert.Equal("a-5", r.Stdout.Trim());
        Assert.Equal("a-5", Environment.GetEnvironmentVariable("myvar"));
    }

    [Fact]
    public void Printf_DashVJoined_AndLastWins()
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashPrintf '-vpa' '-vpb' '%s' z; $pa + '|' + $pb").AssertSuccess();
        Assert.Equal("|", r.Stdout.Trim().Replace("z", ""));
    }

    [Fact]
    public void Printf_DashV_Direct_BareDecoyBinds()
        // A bare -v typed at PowerShell prefix-matches -Verbose; the V decoy rescues it (Pester path).
        => Assert.Equal("hi", CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashPrintf -v pv2 '%s' hi; $pv2").AssertSuccess().Stdout.Trim());

    [Fact]
    public void Printf_DashV_ArrayElement_SetsThatElement()
        => Assert.Equal("x|hi", CmdResult.Run(_fixture.AcquireFresh(),
            "$parr = @('x'); Invoke-BashPrintf '-v' 'parr[2]' '%s' hi; $parr[0] + '|' + $parr[2]").AssertSuccess().Stdout.Trim());

    [Theory]
    [InlineData("'-x'", "-x: invalid option")]
    [InlineData("'-v'", "-v: option requires an argument")]
    [InlineData("'--'", "usage: printf")]
    [InlineData("", "usage: printf")]
    [InlineData("'-v' x", "usage: printf")]
    [InlineData("'-v' '1bad' x", "not a valid identifier")]
    public void Printf_UsageErrors_Exit2(string args, string stderr)
        => CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashPrintf " + args).AssertFailed(2, stderr);

    [Fact]
    public void Printf_DashDash_EndsOptions_FormatVerbatim()
    {
        Assert.Equal(new[] { "-x" }, RunBashText("Invoke-BashPrintf '--' '-x'"));
        Assert.Equal(new[] { "-n" + "\n" }, RunBashText("Invoke-BashPrintf '%s\n' '-n'"));
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
