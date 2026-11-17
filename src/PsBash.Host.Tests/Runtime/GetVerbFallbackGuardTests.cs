using PsBash.Host.Runtime;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// The lookup pair that marks PowerShell's default-verb retry (<c>WORD</c> then <c>get-WORD</c>). How a resolved program
/// file is started is <c>PsBash.Core.ProgramFile</c>'s (tests: <c>Core.Tests/ProgramFileTests</c>).
/// </summary>
public class GetVerbFallbackGuardTests
{
    [Theory]
    [InlineData("get-Package", "Package", true)]
    [InlineData("get-process", "process", true)]
    [InlineData("Get-Package", "Package", false)]   // written by the script: PowerShell's retry is lower-case
    [InlineData("get-package", "Package", false)]   // the retry keeps the word's own casing
    [InlineData("get-Packages", "Package", false)]
    [InlineData("xget-Package", "Package", false)]
    [InlineData("Package", "Package", false)]
    public void IsRetryOf_MatchesOnlyTheExactRetry(string name, string bare, bool expected)
        => Assert.Equal(expected, GetVerbFallbackGuard.IsRetryOf(name, bare));

    [Theory]
    [InlineData("Package", true)]
    [InlineData("Get-Package", false)]
    [InlineData(@"dir\tool", false)]
    [InlineData("", false)]
    public void CanBeRetried_IsAWordWithNoVerbOrPathSeparator(string name, bool expected)
        => Assert.Equal(expected, GetVerbFallbackGuard.CanBeRetried(name));
}
