using System.Collections.ObjectModel;
using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Runs a script that must END (a streaming command that stops its upstream, a program that must not wait on
/// stdin) under a wall-clock bound, so a hang fails one test instead of wedging the suite. The ONE place a test
/// in this project bounds a pipeline by the clock; the class that calls it belongs to
/// <see cref="WallClockBoundCollection"/> (both pinned by <see cref="WallClockBoundGuardTests"/>).
///
/// <para>On a timeout the pipeline is STOPPED before the test fails: left running it keeps the class's shared
/// runspace busy, and every later test in the class then fails in under a millisecond, hiding which one
/// hung.</para>
/// </summary>
internal static class BoundedPipeline
{
    /// <param name="what">Names the failure: what did not finish, and what that would mean.</param>
    public static Collection<PSObject> Invoke(PowerShell pwsh, string script, TimeSpan bound, string what)
    {
        var task = Task.Run(() => pwsh.AddScript(script).Invoke());
        if (!task.Wait(bound))
        {
            pwsh.Stop();
            task.Wait(TimeSpan.FromSeconds(10));
            pwsh.Commands.Clear();
            Assert.Fail($"{what}: the pipeline did not finish within {bound.TotalSeconds:0} s: {script}");
        }
        pwsh.Commands.Clear();
        return task.GetAwaiter().GetResult();
    }
}
