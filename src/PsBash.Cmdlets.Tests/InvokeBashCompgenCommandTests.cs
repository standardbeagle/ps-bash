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
    public void WordList_FailingExpansion_IsStatusOneWithNoOutput()
        => Assert.Equal(new[] { "1" }, Run("$null = Invoke-BashCompgen '-W' 'a $(( 1 / 0 )) b' 2>$null; [string]$global:LASTEXITCODE"));

    [Fact]
    public void BuiltinAction_ListsBuiltinsWithThePrefix()
        => Assert.Equal(new[] { "getopts" }, Run("Invoke-BashCompgen '-A' builtin g"));

    [Fact]
    public void FunctionResults_AreNotFilteredByThePrefix()
        => Assert.Equal(new[] { "one", "two" }, Run("function g { $global:COMPREPLY = @('one','two') }; Invoke-BashCompgen '-F' g zzz"));

    // bash 5.2 with `shopt -s extglob` (bash-completion sets it): `compgen -X "@(two|bin)" -W "one two three bin"`
    // prints one three; a leading ! keeps the matches.
    [Theory]
    [InlineData("@(two|bin)", new[] { "one", "three" })]
    [InlineData("!@(two|bin)", new[] { "two", "bin" })]
    [InlineData("t*", new[] { "one", "bin" })]
    public void Filter_RemovesMatchingCandidates(string filter, string[] expected)
    {
        Assert.Equal(expected, Run($"Invoke-BashShopt -s extglob; Invoke-BashCompgen '-X' '{filter}' '-W' 'one two three bin'"));
        Assert.Equal(expected, Run($"Invoke-BashShopt -s extglob; function g {{ $global:COMPREPLY = @('one','two','three','bin') }}; Invoke-BashCompgen '-X' '{filter}' '-F' g"));
    }

    // bash 5.2, extglob off (a script's default): the filter's @( ) is literal text, so it removes nothing, and with
    // a leading ! it keeps nothing.
    [Theory]
    [InlineData("@(two|bin)", new[] { "one", "two", "three", "bin" })]
    [InlineData("!@(two|bin)", new string[0])]
    public void Filter_ExtGlobOff_ListSyntaxIsLiteral(string filter, string[] expected)
        => Assert.Equal(expected, Run($"Invoke-BashShopt -u extglob; Invoke-BashCompgen '-X' '{filter}' '-W' 'one two three bin'"));

    [Fact]
    public void Filter_Ampersand_IsTheWordBeingCompleted()
        => Assert.Equal(new[] { "two", "three" }, InvokeBashCompgenCommand.ApplyFilter(["t*", "two", "three"], "&", "t*"));

    [Fact]
    public void Filter_EverythingRemoved_IsStatusOne()
        => Assert.Equal(new[] { "1" }, Run("$null = Invoke-BashCompgen '-X' '*' '-W' 'a b'; [string]$global:LASTEXITCODE"));

    // bash: a command the completion function runs that is not found does not fail compgen (its COMPREPLY is used).
    [Fact]
    public void FunctionRunningMissingCommand_StillReturnsReply()
        => Assert.Equal(new[] { "one" }, Run("function g { psbash-no-such-command-xyz 2>$null; $global:COMPREPLY = @('one') }; Invoke-BashCompgen '-F' g"));

    // bash: compgen -F prints the reply with status 0 even when the function's last command failed (127).
    [Fact]
    public void FunctionWithFailingCommand_StatusIsZero()
        => Assert.Equal(new[] { "one", "0" },
            Run("function g { $global:COMPREPLY = @('one'); $global:LASTEXITCODE = 127 }; Invoke-BashCompgen '-F' g; [string]$global:LASTEXITCODE"));

    // bash: compgen -A user lists the passwd account names in file order.
    [Fact]
    public void Users_AreThePasswdNames_OrTheCurrentUserWithoutOne()
    {
        var passwd = Path.Combine(Path.GetTempPath(), "psbash-compgen-passwd-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(passwd, "root:x:0:0:root:/root:/bin/bash\n#comment\ndaemon:x:1:1::/:/usr/sbin/nologin\n\n");
            Assert.Equal(["root", "daemon"], InvokeBashCompgenCommand.UserNames(passwd, "root"));
            // An account outside the file (macOS Directory Services) is still listed, after the file's names.
            Assert.Equal(["root", "daemon", "runner"], InvokeBashCompgenCommand.UserNames(passwd, "runner"));
        }
        finally { File.Delete(passwd); }
        Assert.Equal([Environment.UserName], InvokeBashCompgenCommand.UserNames(null));
        Assert.Equal([Environment.UserName], InvokeBashCompgenCommand.UserNames(passwd));
        Assert.Contains(Environment.UserName, Run("Invoke-BashCompgen '-A' user"));
    }

    // bash: `unset -v COMPREPLY; COMPREPLY=hello` in the function completes hello (the unset of a name with no
    // variable is not an error); a real runtime error still fails compgen.
    [Fact]
    public void FunctionUnsettingReply_ThenScalarReply_IsTheCandidate()
    {
        Assert.Equal(["hello"], Run("function g { Invoke-BashUnset '-v' COMPREPLY; $global:COMPREPLY = 'hello' }; Invoke-BashCompgen '-F' g"));
        Assert.Equal(["1"], Run("function g { $global:COMPREPLY = @('a'); Write-Error 'boom' }; $null = Invoke-BashCompgen '-F' g 2>$null; [string]$global:LASTEXITCODE"));
    }

    // bash: compgen -A command xarg prints xargs. Here xargs is a global AllScope alias, which Get-Command omits.
    [Fact]
    public void Commands_IncludeTheRuntimeAliases()
    {
        Assert.Equal(["xargs"], Run("Set-Alias -Name 'xargs' -Value 'Invoke-BashXargs' -Force -Scope Global -Option AllScope; Invoke-BashCompgen '-A' command xarg"));
        Assert.Contains("eval", Run("Invoke-BashCompgen '-A' command eva"));
    }

    // Windows spells the variable Path; bash code (and compgen -e/-v) sees PATH.
    [Fact]
    public void Exported_PathIsListedAsPATH()
        => Assert.Contains("PATH", Run("Invoke-BashCompgen '-e' PA"));
}

/// <summary>compgen cases that set the process environment (<c>$env:IFS</c>), so they run alone and restore it.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public class InvokeBashCompgenEnvironmentTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashCompgenEnvironmentTests(SharedPwshFixture fixture) => _fixture = fixture;

    [Fact]
    public void WordList_SplitsOnIfs_AndKeepsBackslashEscapedDelimiter()
    {
        using var env = PsBash.Testing.ProcessEnv.Preserve("IFS");
        var lines = _fixture.AcquireFresh().AddScript("$env:IFS = ':%'; Invoke-BashCompgen '-W' 'spam:eggs%ham cheese\\:colon'")
            .Invoke().Select(o => o?.ToString() ?? "").ToArray();
        Assert.Equal(new[] { "spam", "eggs", "ham cheese:colon" }, lines);
    }
}
