using System;
using System.Collections.Generic;
using System.Diagnostics;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// A background job's own scalar store (<see cref="ScalarOverlay"/>, docs/specs/background-jobs.md section 3): the
/// values and export attribute as of the launch, private to the job, and the source of its children's environment.
/// Oracle note (qa-rubric Directive 1): the store has no bash equivalent; the behavior it gives a script is pinned
/// against bash by <c>BackgroundJobLaunchValuesDifferentialTests</c>. These pin the store's own contract.
/// The "job" here is the test thread (<see cref="ScalarOverlay.CurrentOwner"/> answers an owner only on it), so tests
/// running beside these on other threads keep seeing the process environment.
/// </summary>
public sealed class ScalarOverlayTests : IDisposable
{
    [ThreadStatic] private static object? t_owner;
    private readonly Func<object?>? _was;
    private readonly object _job = new();
    private readonly string _name = "PSB_OVL_" + Guid.NewGuid().ToString("N")[..8];

    public ScalarOverlayTests()
    {
        _was = ScalarOverlay.CurrentOwner;
        var previous = _was;
        ScalarOverlay.CurrentOwner = () => t_owner ?? previous?.Invoke();
    }

    public void Dispose()
    {
        t_owner = null;
        ScalarOverlay.Detach(_job);
        ShellExports.Forget(_name);
        Environment.SetEnvironmentVariable(_name, null);
        ScalarOverlay.CurrentOwner = _was;
    }

    private ScalarOverlay Launch()
    {
        var overlay = ScalarOverlay.Capture();
        overlay.Attach(_job);
        t_owner = _job;
        return overlay;
    }

    [Fact]
    public void TheJob_SeesTheValueAtItsLaunch_NotALaterWriteOfTheShell()
    {
        Environment.SetEnvironmentVariable(_name, "a");
        var job = Launch();
        Environment.SetEnvironmentVariable(_name, "b");
        Assert.Equal("a", job.Get(_name));
    }

    [Fact]
    public void WhatTheJobWrites_NeverReachesTheProcessEnvironment()
    {
        Environment.SetEnvironmentVariable(_name, "a");
        var job = Launch();
        job.Set(_name, "inside");
        Assert.Equal("a", Environment.GetEnvironmentVariable(_name));
        job.Set(_name, null);
        Assert.Null(job.Get(_name));
        Assert.Equal("a", Environment.GetEnvironmentVariable(_name));
    }

    [Fact]
    public void AnEmptyValue_IsASetVariable()
    {
        var job = Launch();
        job.Set(_name, "");
        Assert.Equal("", job.Get(_name));
    }

    [Fact]
    public void TheJobsExport_DoesNotExportTheShellsVariable()
    {
        Launch();
        ShellExports.Export(_name);
        Assert.True(ShellExports.IsExported(_name));
        t_owner = null; // back in the shell
        Assert.False(ShellExports.IsExported(_name));
    }

    [Fact]
    public void AJobsChild_GetsTheJobsExportedValues_AndTheCallersOwnEntries()
    {
        Environment.SetEnvironmentVariable(_name, "shell");
        var job = Launch();
        job.Set(_name, "job");
        ShellExports.Export(_name);
        var unexported = _name + "_U";
        job.Set(unexported, "hidden");

        var psi = new ProcessStartInfo("x");
        psi.Environment["PSB_OVL_CALLER"] = "mine";
        ShellExports.RemoveUnexported(psi.Environment);

        Assert.Equal("job", psi.Environment[_name]);
        Assert.False(psi.Environment.ContainsKey(unexported));
        Assert.Equal("mine", psi.Environment["PSB_OVL_CALLER"]);
    }

    [Fact]
    public void AnEntryTheCallerRemoved_StaysOutOfAJobsChild()
    {
        Environment.SetEnvironmentVariable(_name, "shell");
        ShellExports.Export(_name); // exported in the shell, so in the job's copy too
        Launch();
        var psi = new ProcessStartInfo("x");
        psi.Environment.Remove(_name);
        ShellExports.RemoveUnexported(psi.Environment);
        Assert.False(psi.Environment.ContainsKey(_name));
    }

    [Fact]
    public void AJobLaunchedInAJob_CopiesTheOuterJobsStore()
    {
        Environment.SetEnvironmentVariable(_name, "shell");
        var outer = Launch();
        outer.Set(_name, "outer");
        var inner = ScalarOverlay.Capture();
        outer.Set(_name, "outer2");
        Assert.Equal("outer", inner.Get(_name));
    }

    [Fact]
    public void Detach_GivesTheRunspaceBackToTheProcessEnvironment()
    {
        Launch();
        Assert.NotNull(ScalarOverlay.Current);
        ScalarOverlay.Detach(_job);
        Assert.Null(ScalarOverlay.Current);
    }
}
