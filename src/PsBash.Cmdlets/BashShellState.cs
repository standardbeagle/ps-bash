using System.Collections;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Save / restore of the shell state a bash SUBSHELL (<c>( … )</c>, a wrapped <c>$( … )</c>) must not leak
/// into its parent. bash forks, so nothing a subshell changes survives it; ps-bash runs the body in the same
/// runspace and process, so the emitter (<c>PsBuild.ShellStateScope</c>) brackets it with
/// <see cref="Save"/> / <see cref="Restore"/>. The body also runs in a child PowerShell scope (<c>&amp; { }</c>),
/// which already isolates PS-scoped state: bash arrays (<c>$arr</c>), functions, <c>$ErrorActionPreference</c>
/// (<c>set -e</c>), <c>Set-StrictMode</c> (<c>set -u</c>). What a child scope does NOT isolate is saved here:
/// <list type="bullet">
/// <item>bash variables — <c>$env:NAME</c>, read and written through <see cref="BashVariableStore"/>;</item>
/// <item>the <c>$global:</c> shell state: errexit, <c>$-</c> flags, positional parameters, <c>BASH_REMATCH</c>,
/// traps, <c>$!</c>, the xtrace flag;</item>
/// <item>the per-runspace <c>shopt</c> table;</item>
/// <item><c>Set-PSDebug</c> tracing (<c>set -x</c>) — session-wide and not queryable, so it is tracked by
/// <c>$global:__BashXtrace</c> and <see cref="Restore"/> returns the level the caller must re-apply.</item>
/// </list>
/// NOT restored, on purpose: <c>$?</c>/<c>$LASTEXITCODE</c> (the subshell's status IS the parent's <c>$?</c>)
/// and the working directory (the emitter's <c>Push-Location</c>/<c>Pop-Location</c> pair owns it).
/// </summary>
public static class BashShellState
{
    /// <summary>The <c>$global:</c> variables that are shell state (see the class summary).</summary>
    private static readonly string[] GlobalNames =
    [
        "__BashErrexit", "__BashErrexitSuppress", "__BashErrexitTail", "__BashXtrace",
        "BashFlags", "BashPositional", "BashPositional0", "BASH_REMATCH",
        "BashTrapHandlers", "__BashTrapERR", "__BashTrapEXIT", "BashBgLastPid",
    ];

    private static readonly StringComparer EnvNames =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>A snapshot taken by <see cref="Save"/>. Opaque to PowerShell.</summary>
    public sealed class Snapshot
    {
        internal Snapshot(Dictionary<string, string> env, (bool Exists, object? Value)[] globals,
            Dictionary<string, bool> shopt, bool xtrace)
        {
            Env = env; Globals = globals; Shopt = shopt; Xtrace = xtrace;
        }

        internal Dictionary<string, string> Env { get; }
        internal (bool Exists, object? Value)[] Globals { get; }
        internal Dictionary<string, bool> Shopt { get; }
        internal bool Xtrace { get; }
    }

    /// <summary>Capture the state a subshell body may change. Call at the top of the subshell.</summary>
    public static Snapshot Save(SessionState session)
    {
        // Through the variable-store seam (BashVariableStoreGuardTests): the store is where bash
        // variables live, so a future per-invocation store keeps subshell scoping correct for free.
        var env = new Dictionary<string, string>(EnvNames);
        foreach (var (name, value) in BashVariableStore.Enumerate())
            env[name] = value;

        var globals = new (bool, object?)[GlobalNames.Length];
        for (int i = 0; i < GlobalNames.Length; i++)
        {
            var v = session.PSVariable.Get("global:" + GlobalNames[i]);
            globals[i] = v is null ? (false, null) : (true, CloneMutable(v.Value));
        }

        return new Snapshot(env, globals, InvokeBashShoptCommand.SnapshotOptions(),
            IsTrue(session.PSVariable.GetValue("global:__BashXtrace")));
    }

    /// <summary>
    /// Put <paramref name="saved"/> back. Returns <c>$null</c> when tracing is unchanged, else the tracing
    /// state to re-apply (<c>$true</c> = <c>Set-PSDebug -Trace 1</c>, <c>$false</c> = <c>Set-PSDebug -Off</c>);
    /// <c>Set-PSDebug</c> is a cmdlet, so the emitted <c>finally</c> runs it.
    /// </summary>
    public static object? Restore(SessionState session, Snapshot saved)
    {
        // Variables: drop what the body added, reset what it changed. Only differences are written.
        var current = new Dictionary<string, string>(EnvNames);
        foreach (var (name, value) in BashVariableStore.Enumerate())
            current[name] = value;
        foreach (var name in current.Keys)
        {
            if (!saved.Env.ContainsKey(name))
                BashVariableStore.Set(name, null);
        }
        foreach (var (name, value) in saved.Env)
        {
            if (!current.TryGetValue(name, out var now) || !string.Equals(now, value, StringComparison.Ordinal))
                BashVariableStore.Set(name, value);
        }

        bool tracingNow = IsTrue(session.PSVariable.GetValue("global:__BashXtrace"));
        for (int i = 0; i < GlobalNames.Length; i++)
        {
            var (exists, value) = saved.Globals[i];
            if (exists) session.PSVariable.Set("global:" + GlobalNames[i], value);
            else session.PSVariable.Remove("global:" + GlobalNames[i]);
        }

        InvokeBashShoptCommand.RestoreOptions(saved.Shopt);
        return tracingNow == saved.Xtrace ? null : saved.Xtrace;
    }

    private static bool IsTrue(object? v) => LanguagePrimitives.IsTrue(v);

    /// <summary>A copy of a collection value the body could mutate in place (<c>shift</c> on the positional
    /// array, <c>trap</c> on the handler table); scalars and strings are immutable and kept as-is.</summary>
    private static object? CloneMutable(object? value)
    {
        var raw = value is PSObject pso ? pso.BaseObject : value;
        return raw switch
        {
            object[] a => a.Clone(),
            Hashtable h => h.Clone(),   // shallow, keeps the table's key comparer
            IDictionary d => new Hashtable(d),
            ArrayList l => new ArrayList(l),
            IList l => new ArrayList(l),
            _ => value,
        };
    }
}
