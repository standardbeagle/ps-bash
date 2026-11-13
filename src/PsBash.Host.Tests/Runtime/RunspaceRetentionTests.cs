using System.Management.Automation;
using System.Runtime.CompilerServices;
using PsBash.Host.Runtime;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// A runspace the host has discarded must be collectable. The host discards one per command, so anything
/// process-wide that keeps a runspace reachable grows the host by a whole runspace (every function, alias
/// and variable the psm1 defines) per command that touched it.
/// <para>
/// The case pinned here: the psm1's type-level <c>ToString</c> for the <c>PsBash.*</c> output types was a
/// <c>ScriptMethod</c>. Its script block was bound to the importing runspace's session state, and
/// PowerShell's process-wide binder rule caches capture the type table a rule was built against, so a
/// runspace that ever stringified one of those objects stayed alive for the life of the process. A warm
/// differential-suite host reached 94 live runspaces and over 1.5 GB of live heap, at which point its memory
/// guard stopped every command (exit 137) — v0.14.3's Windows release run.
/// </para>
/// Oracle note (Directive 1): host-internal, no bash equivalent; hand-written asserts.
/// </summary>
[Collection("SdkHost")]
public class RunspaceRetentionTests
{
    // Several runspaces: each builds its own call-site rules against its own type table.
    private const int Runspaces = 3;

    [Fact]
    public void DiscardedRunspace_ThatStringifiedPsBashObjects_IsCollected()
    {
        // Untracked first: PowerShell's process-wide cmdlet-entry cache (one per imported assembly) keeps the
        // PSModuleInfo of the FIRST runspace that imported a binary module, and so that runspace. That is one
        // runspace per process, not one per command, and it is PowerShell's; what this pins is the growth.
        UseAndDispose();

        var refs = new List<WeakReference>();
        for (int i = 0; i < Runspaces; i++)
            refs.Add(UseAndDispose());

        for (int i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        int alive = refs.Count(r => r.IsAlive);
        Assert.True(alive == 0, $"{alive} of {Runspaces} disposed runspaces are still reachable after a full collection");
    }

    // Each bash feature reaches a different part of the runtime (alias/trap/eval script blocks, job threads,
    // process-sub temp files, native children, regex and glob caches). Any process-wide cache that captures a
    // script block, session state or runspace from one of them keeps the whole runspace alive.
    [Theory]
    [InlineData("alias, function, eval", "alias ll='echo a'; f() { local v=$1; echo \"$v\"; }; f x; eval 'g() { echo $1; }; g y'; ll")]
    [InlineData("trap and errexit", "trap 'echo bye' EXIT; trap 'echo err' ERR; set -e; true; f() { return 0; }; f")]
    [InlineData("pipeline, command sub, process sub", "x=$(printf 'b\\na\\n' | sort | head -n 1); cat <(echo \"$x\") | grep -c a; while read -r l; do echo \"$l\"; done < <(seq 3)")]
    [InlineData("background job and wait", "sleep 0.05 & p=$!; wait $p; echo $?; (echo sub) & wait")]
    [InlineData("arrays, regex, printf -v, glob", "declare -A m=([k]=v); a=(1 2 3); [[ abc =~ (b)(c) ]] && echo ${BASH_REMATCH[1]}; printf -v s '%s-' \"${a[@]}\"; echo $s ${m[k]}; echo /nonexistent-*")]
    [InlineData("native child process", "cmd_out=$(hostname); echo ${#cmd_out}")]
    public void DiscardedRunspace_AfterRunningBash_IsCollected(string feature, string bash)
    {
        string ps = PsBash.Core.Transpiler.BashTranspiler.Transpile(bash)!;
        UseAndDispose(ps); // the one runspace PowerShell's per-assembly cmdlet-entry cache keeps (see above)

        var refs = new List<WeakReference>();
        for (int i = 0; i < Runspaces; i++)
            refs.Add(UseAndDispose(ps));

        for (int i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        int alive = refs.Count(r => r.IsAlive);
        Assert.True(alive == 0, $"{feature}: {alive} of {Runspaces} disposed runspaces are still reachable after a full collection");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference UseAndDispose(string script)
    {
        var sdk = SdkRunspace.Create();
        var weak = new WeakReference(sdk.Runspace);
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = sdk.Runspace;
            ps.AddScript(script);
            var output = ps.Invoke();
            // Every case prints: an empty result means the script died early and exercised nothing.
            Assert.True(output.Count > 0, "no output; errors: " + string.Join("; ", ps.Streams.Error.Select(e => e.ToString())));
        }
        sdk.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference UseAndDispose()
    {
        var sdk = SdkRunspace.Create();
        var weak = new WeakReference(sdk.Runspace);
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = sdk.Runspace;
            // Dynamic member access and string conversion on a PsBash-typed object: the call sites whose
            // rules consult the type table's ToString.
            ps.AddScript(
                "$o = [PsBash.Cmdlets.BashRuntime]::NewBashObject('x', 'PsBash.CatLine'); " +
                "[string]$o; \"$o\"; $o.ToString(); $o.BashText");
            var output = ps.Invoke();
            Assert.False(ps.HadErrors, string.Join("; ", ps.Streams.Error.Select(e => e.ToString())));
            Assert.Equal(new[] { "x", "x", "x", "x" }, output.Select(o => o?.ToString()));
        }
        sdk.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return weak;
    }
}
