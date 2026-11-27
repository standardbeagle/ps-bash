using System.Management.Automation;
using PsBash.Core;
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

    /// <summary>
    /// <c>Invoke-BashNative</c>'s own lookup (<see cref="CommandLookupRaw"/>) gets the program itself, never the guard's
    /// stand-in: the stand-in calls <c>Invoke-BashNative</c>, so answering its lookup with the stand-in again started the
    /// cmdlet over until the call depth overflowed (any <c>.cmd</c> run by command word in the host). A <c>#!</c> file
    /// the OS cannot start as it is takes the same branch on every platform; an ordinary lookup still gets the stand-in.
    /// </summary>
    [Fact]
    public void ARawLookup_GetsTheProgramItself_NotTheStandIn()
    {
        string dir = Directory.CreateTempSubdirectory("psbash-guard").FullName;
        try
        {
            string tool = Path.Combine(dir, "tool");
            File.WriteAllText(tool, "#!/bin/sh\necho hi\n");
            using var pwsh = PowerShell.Create();
            var invoke = pwsh.Runspace.SessionStateProxy.InvokeCommand;
            GetVerbFallbackGuard.Install(invoke);

            Assert.IsNotType<ApplicationInfo>(invoke.GetCommand(tool, CommandTypes.Application));
            Assert.IsType<ApplicationInfo>(CommandLookupRaw.Run(() => invoke.GetCommand(tool, CommandTypes.Application)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
