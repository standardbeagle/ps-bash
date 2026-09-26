using System.Collections.Generic;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

/// <summary>
/// R07: the automatic session token must key one daemon per logical session, not
/// per launcher invocation. The immediate parent of a one-shot ps-bash may be a
/// fresh short-lived shim (Claude Code's Bash tool spawns a new parent per call),
/// so the anchor must climb to the nearest stable shell/agent ancestor. It must
/// also carry that ancestor's start time so a recycled PID cannot attach a new
/// invocation to a stale daemon. These tests exercise the pure selection
/// algorithm with synthetic ancestry chains — no real processes, no global seams.
/// </summary>
public class ProcessAncestrySessionAnchorTests
{
    private sealed record ChainNode(string Name, long StartTicks, int Parent);

    /// <summary>
    /// Build a synthetic ancestry: <paramref name="immediateParent"/> is the
    /// launcher's parent, and <paramref name="nodes"/> describes the rest.
    /// Parent 0 terminates the chain.
    /// </summary>
    private static ProcessAncestry.ProcessIdentity? SelectFrom(
        int launcherPid, int immediateParent, Dictionary<int, ChainNode> nodes)
    {
        Func<int, int?> parentOf = pid =>
        {
            if (pid == launcherPid) return immediateParent;
            return nodes.TryGetValue(pid, out var n) && n.Parent != 0 ? n.Parent : null;
        };
        Func<int, (string? Name, long StartTicks)?> infoOf = pid =>
            nodes.TryGetValue(pid, out var n) ? (n.Name, n.StartTicks) : null;
        return ProcessAncestry.SelectSessionAnchor(launcherPid, parentOf, infoOf);
    }

    [Fact]
    public void SelectSessionAnchor_TransientLauncherChain_ClimbsToStableAgent()
    {
        // ps-bash <- pwsh (fresh shim, stable-named but per-call) <- opencode (agent)
        // <- dotnet (shared build host, stop-set).
        var nodes = new Dictionary<int, ChainNode>
        {
            [10] = new ChainNode("pwsh", 100, 20),
            [20] = new ChainNode("opencode", 50, 30),
            [30] = new ChainNode("dotnet", 1, 0),
        };

        var anchor = SelectFrom(launcherPid: 5, immediateParent: 10, nodes);

        Assert.NotNull(anchor);
        Assert.Equal(20, anchor.Value.Pid);
        Assert.Equal(50, anchor.Value.StartTimeUtcTicks);
    }

    [Fact]
    public void SelectSessionAnchor_StableShellIsImmediateParent_UsesIt()
    {
        // ps-bash <- bash (interactive shell) <- ... : the shell is the session.
        var nodes = new Dictionary<int, ChainNode>
        {
            [10] = new ChainNode("bash", 42, 20),
            [20] = new ChainNode("dotnet", 1, 0),
        };

        var anchor = SelectFrom(launcherPid: 5, immediateParent: 10, nodes);

        Assert.NotNull(anchor);
        Assert.Equal(10, anchor.Value.Pid);
        Assert.Equal(42, anchor.Value.StartTimeUtcTicks);
    }

    [Fact]
    public void SelectSessionAnchor_NoAncestorChain_UsesImmediateParent()
    {
        // Only one ancestor known and it is not a recognized session host.
        var nodes = new Dictionary<int, ChainNode>
        {
            [10] = new ChainNode("some-tool", 7, 0),
        };

        var anchor = SelectFrom(launcherPid: 5, immediateParent: 10, nodes);

        Assert.NotNull(anchor);
        Assert.Equal(10, anchor.Value.Pid);
        Assert.Equal(7, anchor.Value.StartTimeUtcTicks);
    }

    [Fact]
    public void SelectSessionAnchor_NoParentAtAll_ReturnsNull()
        => Assert.Null(SelectFrom(launcherPid: 5, immediateParent: 10, new Dictionary<int, ChainNode>()));

    [Fact]
    public void SelectSessionAnchor_SameChainSameAnchor_SameIdentity()
    {
        var nodes = new Dictionary<int, ChainNode>
        {
            [10] = new ChainNode("pwsh", 100, 20),
            [20] = new ChainNode("node", 50, 0),
        };

        var a = SelectFrom(5, immediateParent: 10, nodes);
        var b = SelectFrom(5, immediateParent: 10, nodes);

        Assert.Equal(a, b);
        Assert.Equal(20, a.Value.Pid);
    }

    [Fact]
    public void SelectSessionAnchor_PidReusedWithNewStartTime_YieldsNewIdentity()
    {
        // Same anchor PID, different start time => a different daemon key, so a
        // recycled PID can never attach to the previous session's stale daemon.
        var first = SelectFrom(5, immediateParent: 20, new Dictionary<int, ChainNode>
        {
            [20] = new ChainNode("opencode", 50, 0),
        });
        var second = SelectFrom(5, immediateParent: 20, new Dictionary<int, ChainNode>
        {
            [20] = new ChainNode("opencode", 999, 0),
        });

        Assert.Equal(20, first.Value.Pid);
        Assert.Equal(20, second.Value.Pid);
        Assert.NotEqual(first.Value.StartTimeUtcTicks, second.Value.StartTimeUtcTicks);
    }
}

