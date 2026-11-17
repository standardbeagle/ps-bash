using System.Management.Automation;
using System.Management.Automation.Runspaces;
using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// What a job runspace differs from the shell's own in (docs/specs/background-jobs.md section 3): its <c>Env:</c> drive
/// is the job's scalar store, and a program a command word names starts through <c>Invoke-BashNative</c>
/// (<c>JobProgramLookup</c>). The shell's runspace keeps PowerShell's. Oracle note (qa-rubric Directive 1): the
/// bash-visible result is pinned by <c>BackgroundJobLaunchValuesDifferentialTests</c>; these pin the plumbing and that a
/// streaming pipeline through the stand-in still ends. Runs real jobs under a wall-clock bound, so the class runs alone.
/// </summary>
[Collection(WallClockBoundCollection.Name)]
public class BackgroundJobRunspaceTests : IClassFixture<SharedPwshFixture>
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);
    private readonly SharedPwshFixture _fixture;

    public BackgroundJobRunspaceTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        // The fixture runs the psm1 as a script body, so it cannot name itself for the job runspaces to import.
        BashJobRunspaces.WarmAhead(Path.Combine(AppContext.BaseDirectory, "PsBash.psm1"));
    }

    private string[] RunBash(string bash, string what)
    {
        var pwsh = _fixture.AcquireFresh();
        var output = BoundedPipeline.Invoke(pwsh, BashTranspiler.Transpile(bash)!, Bound, what);
        Assert.True(pwsh.Streams.Error.Count == 0,
            string.Join(" | ", pwsh.Streams.Error.Select(e => e.ToString())));
        return output.Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void AJobRunspace_EnvDriveIsTheJobStore_NotPowerShellsEnvironmentProvider()
    {
        using var rs = RunspaceFactory.CreateRunspace(BashJobRunspaces.InitialState(null));
        rs.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = rs;
        var provider = ps.AddScript("(Get-PSDrive env).Provider.Name").Invoke().Single().ToString();
        Assert.Equal(JobEnvironmentProvider.ProviderName, provider);
    }

    /// <summary>Importing the cmdlets registers the provider in the shell's session too; its Env: stays PowerShell's.</summary>
    [Fact]
    public void TheShellsRunspace_KeepsPowerShellsEnvDrive()
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(), "(Get-PSDrive env).Provider.Name").AssertSuccess();
        Assert.Equal("Environment", r.Lines[^1]);
    }

    /// <summary>The lookup hook is the job runspace's: running jobs leaves the shell's own hook as it was.</summary>
    [Fact]
    public void RunningAJob_LeavesTheShellsCommandLookupHookAlone()
    {
        var pwsh = _fixture.AcquireFresh();
        var hook = pwsh.Runspace.SessionStateProxy.InvokeCommand.PostCommandLookupAction;
        RunBash("{ echo in-job; } & wait", "a job that echoes");
        Assert.Same(hook, pwsh.Runspace.SessionStateProxy.InvokeCommand.PostCommandLookupAction);
    }

    [Fact]
    public void YesIntoHead_InAJob_Ends()
    {
        var lines = RunBash("{ yes | head -3; } & wait", "`yes | head -3` in a job (head must stop yes)");
        Assert.Equal(["y", "y", "y"], lines);
    }

    /// <summary>A program between them runs through the job's stand-in, which streams its input record by record.</summary>
    [SkippableFact]
    [Trait("Platform", "Windows")]
    public void YesThroughAProgramIntoHead_InAJob_Ends()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "findstr is a Windows program");
        var lines = RunBash("{ yes | findstr y | head -3; } & wait", "`yes | findstr y | head -3` in a job");
        Assert.Equal(["y", "y", "y"], lines);
    }
}
