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

    // Unix: a file with no execute bit is "Permission denied" (PowerShell would hand it to xdg-open and report 0).
    [SkippableFact]
    [Trait("Platform", "Unix")]
    public void HasExecuteBit_IsFalseOnlyWhenNoExecuteBitIsSet()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix file modes; Windows uses PATHEXT and #! instead.");
        if (OperatingSystem.IsWindows()) return; // tells the platform analyzer File.SetUnixFileMode is reachable only on Unix
        string dir = Directory.CreateTempSubdirectory("psbash-xbit").FullName;
        try
        {
            string doc = Path.Combine(dir, "doc"), tool = Path.Combine(dir, "tool"), group = Path.Combine(dir, "group");
            foreach (var f in new[] { doc, tool, group }) File.WriteAllText(f, "plain\n");
            File.SetUnixFileMode(doc, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.SetUnixFileMode(group, UnixFileMode.UserRead | UnixFileMode.GroupExecute);
            Assert.False(GetVerbFallbackGuard.HasExecuteBit(doc));
            Assert.True(GetVerbFallbackGuard.HasExecuteBit(tool));
            Assert.True(GetVerbFallbackGuard.HasExecuteBit(group));
            Assert.True(GetVerbFallbackGuard.HasExecuteBit(Path.Combine(dir, "missing")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // A #! script on Windows runs through its interpreter instead of the shell's "open with" dialog.
    [Theory]
    [InlineData("/bin/sh", "Invoke-BashBash 'C:\\t\\it''s' @args")]
    [InlineData("/usr/bin/env bash", "Invoke-BashBash 'C:\\t\\it''s' @args")]
    [InlineData("/bin/bash -e", "Invoke-BashBash '-e' 'C:\\t\\it''s' @args")]
    [InlineData("/usr/bin/env -S python3 -u", "& 'python3' '-u' 'C:\\t\\it''s' @args")]
    [InlineData("/usr/local/bin/node", "& 'node' 'C:\\t\\it''s' @args")]
    public void ShebangScript_RunsTheInterpreterWithTheFileThenTheArguments(string shebang, string run)
        => Assert.Equal(
            "$__psbash_in = @($input); if ($__psbash_in.Count -gt 0) { $__psbash_in | " + run + " } else { " + run + " }",
            GetVerbFallbackGuard.ShebangScript(@"C:\t\it's", shebang));

    [Fact]
    public void ShebangScript_EnvWithNoProgram_IsPermissionDenied()
        => Assert.Equal(GetVerbFallbackGuard.PermissionDeniedScript(@"C:\t\x"), GetVerbFallbackGuard.ShebangScript(@"C:\t\x", "/usr/bin/env"));

    [Fact]
    public void TryReadShebang_ReturnsTheLineWithoutCarriageReturn()
    {
        string dir = Directory.CreateTempSubdirectory("psbash-shebang").FullName;
        try
        {
            string a = Path.Combine(dir, "a"), b = Path.Combine(dir, "b");
            File.WriteAllText(a, "#!/usr/bin/env bash\r\necho hi\r\n"); File.WriteAllText(b, "echo no shebang\n");
            Assert.True(GetVerbFallbackGuard.TryReadShebang(a, out var sa));
            Assert.Equal("/usr/bin/env bash", sa);
            Assert.True(GetVerbFallbackGuard.TryReadShebang(b, out var sb));
            Assert.Null(sb);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PermissionDeniedScript_QuotesThePathAndSets126()
        => Assert.Equal(
            "Write-Error -Message 'bash: C:/it''s/LICENSE: Permission denied' -Category PermissionDenied -ErrorAction Continue; $global:LASTEXITCODE = 126",
            GetVerbFallbackGuard.PermissionDeniedScript(@"C:\it's\LICENSE"));
}