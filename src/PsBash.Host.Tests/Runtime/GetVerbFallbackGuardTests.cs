using PsBash.Host.Runtime;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>The lookup pair that marks PowerShell's default-verb retry (<c>WORD</c> then <c>get-WORD</c>).</summary>
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

    // A PATH hit Windows can only "open" (no PATHEXT extension, no #! line) is not a command.
    [Fact]
    public void IsRunnable_DocumentFileIsNot_ShebangAndExeAre()
    {
        string dir = Directory.CreateTempSubdirectory("psbash-lookup").FullName;
        try
        {
            string doc = Path.Combine(dir, "LICENSE"), md = Path.Combine(dir, "README.md");
            string script = Path.Combine(dir, "tool"), exe = Path.Combine(dir, "tool.EXE");
            File.WriteAllText(doc, "MIT License\n"); File.WriteAllText(md, "# readme\n");
            File.WriteAllText(script, "#!/bin/sh\necho hi\n"); File.WriteAllText(exe, "MZ");
            Assert.False(GetVerbFallbackGuard.IsRunnable(doc));
            Assert.False(GetVerbFallbackGuard.IsRunnable(md));
            Assert.True(GetVerbFallbackGuard.IsRunnable(script));
            Assert.True(GetVerbFallbackGuard.IsRunnable(exe));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("License", true)]
    [InlineData("./License", false)]
    [InlineData(@"C:\x\License", false)]
    [InlineData("", false)]
    public void IsBareWord_HasNoPathSeparator(string name, bool expected)
        => Assert.Equal(expected, GetVerbFallbackGuard.IsBareWord(name));

    [Fact]
    public void PermissionDeniedScript_QuotesThePathAndSets126()
        => Assert.Equal(
            "Write-Error -Message 'bash: C:/it''s/LICENSE: Permission denied' -Category PermissionDenied -ErrorAction Continue; $global:LASTEXITCODE = 126",
            GetVerbFallbackGuard.PermissionDeniedScript(@"C:\it's\LICENSE"));
}