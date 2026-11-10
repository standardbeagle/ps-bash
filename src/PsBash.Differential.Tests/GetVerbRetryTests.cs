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
}
