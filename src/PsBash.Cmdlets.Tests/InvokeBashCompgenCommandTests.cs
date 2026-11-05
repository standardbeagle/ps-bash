using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>compgen: -P/-S wrap each candidate, -W expands and splits on IFS, -F/-C are not prefix-filtered, errors give status 1. Oracle: bash 5.2 (oils builtin-completion.spec).</summary>
public class InvokeBashCompgenCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashCompgenCommandTests(SharedPwshFixture fixture) => _fixture = fixture;

    private string[] Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        return pwsh.AddScript(script).Invoke().Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void PrefixAndSuffix_WrapEachCandidate()
        => Assert.Equal(new[] { "[nullglob]" }, Run("Invoke-BashCompgen '-A' shopt '-P' '[' '-S' ']' nu"));

    [Fact]
    public void WordList_SplitsOnIfs_AndKeepsBackslashEscapedDelimiter()
        => Assert.Equal(new[] { "spam", "eggs", "ham cheese:colon" },
            Run("$env:IFS = ':%'; try { Invoke-BashCompgen '-W' 'spam:eggs%ham cheese\\:colon' } finally { $env:IFS = $null }"));

    [Fact]
    public void WordList_FailingExpansion_IsStatusOneWithNoOutput()
        => Assert.Equal(new[] { "1" }, Run("$null = Invoke-BashCompgen '-W' 'a $(( 1 / 0 )) b' 2>$null; [string]$global:LASTEXITCODE"));

    [Fact]
    public void BuiltinAction_ListsBuiltinsWithThePrefix()
        => Assert.Equal(new[] { "getopts" }, Run("Invoke-BashCompgen '-A' builtin g"));

    [Fact]
    public void FunctionResults_AreNotFilteredByThePrefix()
        => Assert.Equal(new[] { "one", "two" }, Run("function g { $global:COMPREPLY = @('one','two') }; Invoke-BashCompgen '-F' g zzz"));
}
