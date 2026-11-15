using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// File operands holding <c>*</c>/<c>?</c>: a cmdlet called DIRECTLY from PowerShell globs them itself (no shell in
/// front of it), but in a runspace that runs transpiled bash (<c>$global:__BashOperandsExpanded</c>, set by the host)
/// the shell has already expanded every unquoted pattern, so what is left was quoted and names a file literally.
/// Hand-written, not oracle: the direct-call half is ps-bash-only surface (the bash half is in
/// SubshellIdentityAndDynamicNamerefTests.QuotedPatternOperand_IsALiteralName_NotReglobbed).
/// </summary>
public class ShellExpandedOperandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public ShellExpandedOperandTests(SharedPwshFixture fixture) => _fixture = fixture;

    private static string NewDirWithTwoBar()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ps-bash", "shexp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "two-bar"), "hi\n");
        return dir;
    }

    [Theory]
    [InlineData("Invoke-BashHead 'two-*'", "head: cannot open 'two-*'")]
    [InlineData("Invoke-BashTail 'two-*'", "tail: cannot open 'two-*'")]
    [InlineData("Invoke-BashCat 'two-*'", "cat: two-*")]
    [InlineData("Invoke-BashWc -l 'two-?ar'", "wc: two-?ar")]
    [InlineData("Invoke-BashNl 'two-*'", "two-*")]
    public void ShellExpanded_PatternOperand_IsLiteral(string command, string stderr)
    {
        var dir = NewDirWithTwoBar();
        try
        {
            CmdResult.Run(_fixture.AcquireFresh(),
                    $"Set-Location -LiteralPath '{dir}'; $global:__BashOperandsExpanded = $true; try {{ {command} }} finally {{ Remove-Variable -Scope Global __BashOperandsExpanded }}")
                .AssertFailed(1, stderr);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("Invoke-BashHead 'two-*'")]
    [InlineData("Invoke-BashTail 'two-*'")]
    [InlineData("Invoke-BashCat 'two-*'")]
    [InlineData("Invoke-BashNl 'two-*'")]
    public void DirectCall_PatternOperand_IsStillGlobbed(string command)
    {
        var dir = NewDirWithTwoBar();
        try
        {
            var r = CmdResult.Run(_fixture.AcquireFresh(), $"Set-Location -LiteralPath '{dir}'; {command}").AssertSuccess();
            Assert.Contains("hi", r.Stdout);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
