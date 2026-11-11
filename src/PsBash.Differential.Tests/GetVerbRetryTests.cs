using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// An unknown bare word is "command not found" (127). PowerShell retries such a word as <c>Get-WORD</c>, which made
/// <c>Package x</c> run Get-Package (minutes, or a prompt), <c>process x</c> Get-Process, <c>module x</c> Get-Module.
/// </summary>
public class GetVerbRetryTests
{
    [Theory]
    [InlineData("{ Package glibc; } 2>/dev/null; echo st=$?")]
    [InlineData("{ process x; } 2>/dev/null; echo st=$?")]
    [InlineData("{ module x; } 2>/dev/null; echo st=$?")]
    [InlineData("{ Location; } 2>/dev/null; echo st=$?")]
    [InlineData("{ ChildItem .; } 2>/dev/null; echo st=$?")]
    [InlineData("{ Random; } 2>/dev/null; echo \"st=$? out=[$(Random 2>/dev/null)]\"")]
    [InlineData("cmd=Variable; { $cmd; } 2>/dev/null; echo st=$?")]
    [InlineData("{ Package glibc || echo fallback; } 2>/dev/null; Package 2>/dev/null | wc -l")]
    public Task UnknownBareWord_IsNotFound(string script) => AssertOracle.EqualAsync(script);

    // A bare name holding PowerShell syntax is looked up too: `{ls` printed itself, `a,b` was an array (a parse error for
    // the whole script), `%x` ran ForEach-Object (bash reads a leading % as a job spec, so x%y here). bash: command not found (127).
    [Theory]
    [InlineData("{ {ls; } 2>/dev/null; echo st=$?")]
    [InlineData("{ a,b; } 2>/dev/null; echo st=$?")]
    [InlineData("{ x%y; } 2>/dev/null; echo st=$?")]
    [InlineData("{ @foo; } 2>/dev/null; echo st=$?")]
    [InlineData("{ f@ x; } 2>/dev/null; echo st=$?; echo after")]
    public Task OddBareName_IsNotFound(string script) => AssertOracle.EqualAsync(script);
}
