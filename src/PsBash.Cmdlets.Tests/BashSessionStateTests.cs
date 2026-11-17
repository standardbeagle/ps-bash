using System.Management.Automation.Runspaces;
using PsBash.Core;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The session states this assembly builds for bash code keep no PowerShell alias except the runtime's own
/// (<see cref="BashSessionState.RemoveForeignAliases"/>, <see cref="PowerShellAliases"/>). Oracle note (qa-rubric
/// Directive 1): runspace plumbing; the bash-visible result is pinned by Differential.Tests/PowerShellAliasWordTests.
/// </summary>
public class BashSessionStateTests
{
    [Fact]
    public void RemoveForeignAliases_DropsPowerShellsAndKeepsTheRuntimesOwn()
    {
        var iss = InitialSessionState.CreateDefault();
        // gci: an alias on every platform (pwsh off Windows has no write, ls, cat, sort, ...).
        Assert.Contains(iss.Commands.OfType<SessionStateAliasEntry>(), a => a.Name == "gci");
        iss.Commands.Add(new SessionStateAliasEntry("echo", "Invoke-BashEcho"));
        BashSessionState.RemoveForeignAliases(iss);
        var left = iss.Commands.OfType<SessionStateAliasEntry>().Select(a => a.Name).ToList();
        Assert.Equal(["echo"], left);
    }

    [Fact]
    public void JobSessionState_HoldsNoPowerShellAlias()
    {
        var kept = BashJobRunspaces.InitialState(null).Commands.OfType<SessionStateAliasEntry>()
            .Where(a => !PowerShellAliases.IsBashRuntimeAlias(a.Definition)).Select(a => a.Name).ToList();
        Assert.True(kept.Count == 0, "PowerShell aliases left in a job runspace: " + string.Join(", ", kept));
    }
}
