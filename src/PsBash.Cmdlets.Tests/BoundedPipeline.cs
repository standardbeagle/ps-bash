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

    /// <summary>
    /// Start <paramref name="script"/>, block in <paramref name="waitUntilReady"/> until it reached the state under
    /// test (a child is running, a wait has begun), then STOP it (what Ctrl+C does) and require the pipeline to end
    /// within <paramref name="bound"/> of the stop. Returns how long the stop took. A pipeline that never ends is
    /// abandoned (its runspace stays busy), so give each such test a fresh <c>AcquireFresh</c> runspace.
    /// </summary>
    public static TimeSpan StopWhenReady(PowerShell pwsh, string script, Action waitUntilReady, TimeSpan bound, string what)
    {
        var task = Task.Run(() =>
        {
            try { pwsh.AddScript(script).Invoke(); }
            catch (PipelineStoppedException) { /* the expected end */ }
        });
        try { waitUntilReady(); }
        catch
        {
            pwsh.Stop();
            task.Wait(TimeSpan.FromSeconds(10));
            pwsh.Commands.Clear();
            throw;
        }
        Assert.False(task.IsCompleted, $"{what}: the pipeline ended before the stop; nothing was tested: {script}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stop = Task.Run(pwsh.Stop);
        bool ended = task.Wait(bound) && stop.Wait(bound);
        sw.Stop();
        Assert.True(ended, $"{what}: the pipeline did not end within {bound.TotalSeconds:0} s of the stop: {script}");
        pwsh.Commands.Clear();
        return sw.Elapsed;
    }
}
