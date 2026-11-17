using System.Collections;
using System.Diagnostics;

namespace PsBash.Cmdlets;

/// <summary>
/// The store for ORDINARY BASH VARIABLES — the things a script sets with
/// <c>x=1</c> / <c>export x=1</c> and reads back as <c>$x</c>. ps-bash models
/// them as environment variables, so the emitter renders them <c>$env:NAME</c>
/// and the cmdlets that read or write a bash variable come through here.
///
/// <para><b>Why this type exists.</b> The process environment is per-PROCESS,
/// but the host is a long-lived daemon shared across every <c>-c</c> invocation
/// in a session. So a bash variable outlives the command that set it:
/// <c>ps-bash -c 'export X=hello'</c> is visible to the next <c>-c</c>, which
/// real bash (a fresh process per invocation) cannot do. See
/// <c>docs/specs/host-lifecycle-contract.md</c> §"the process environment is NOT
/// per-invocation". Routing every bash-variable access through one seam is what
/// makes a per-invocation store implementable later without hunting ~100 call
/// sites; today it is a faithful pass-through to <see cref="Environment"/>, so
/// behavior is unchanged.</para>
///
/// <para><b>Exported or not.</b> Being in the process environment does not make a variable exported: the export
/// attribute is a separate table (<see cref="PsBash.Core.ShellExports"/>), and a child process gets the exported
/// variables only (<see cref="ApplyTo"/>). See <c>docs/specs/environment-model.md</c>.</para>
///
/// <para><b>What does NOT belong here.</b> ps-bash's own configuration knobs —
/// <c>PSBASH_*</c>, <c>NO_COLOR</c>, <c>USERNAME</c>, <c>PATH</c> for tool
/// lookup — are host process state, not script state. They must keep reading
/// <see cref="Environment"/> directly: scoping them per invocation would break
/// the launcher's ability to configure the host at all. Only variables the
/// SCRIPT can observe or mutate come through this store.</para>
/// </summary>
public static class BashVariableStore
{
    /// <summary>Read a bash variable. Null when unset (bash's "unset" — distinct
    /// from set-to-empty, which the <c>${VAR-word}</c> operators rely on).</summary>
    public static string? Get(string name)
        => t_jobSnapshot is { } snapshot && snapshot.TryGetValue(name, out var held)
            ? held
            : Environment.GetEnvironmentVariable(name);

    /// <summary>
    /// Write a bash variable. A null <paramref name="value"/> UNSETS it (the
    /// .NET convention, and what <c>unset</c> / an env-prefix restore needs).
    /// </summary>
    public static void Set(string name, string? value)
    {
        if (t_jobSnapshot is { } snapshot && snapshot.ContainsKey(name)) { snapshot[name] = value; return; }
        Environment.SetEnvironmentVariable(name, value);
    }

    // The variables an in-process background job was handed at launch (`for j in 1 2; do echo $((j * 10)) & done`):
    // the job runs on a pool thread while the loop goes on writing the process environment both share, so the job
    // reads and writes its own copy of these names (environment-model.md section 8).
    [ThreadStatic] private static Dictionary<string, string?>? t_jobSnapshot;

    /// <summary>
    /// On the thread running an in-process background job: <paramref name="vars"/> (name to value at launch, from
    /// <c>Invoke-BashBackground -Vars</c>) answer for those names until <see cref="LeaveJobSnapshot"/>.
    /// </summary>
    public static void EnterJobSnapshot(IDictionary? vars)
    {
        if (vars is null || vars.Count == 0) { t_jobSnapshot = null; return; }
        var snapshot = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in vars)
            if (e.Key is string key) snapshot[key] = e.Value?.ToString();
        t_jobSnapshot = snapshot;
    }

    /// <summary>Ends <see cref="EnterJobSnapshot"/> (a pooled thread runs the next job without it).</summary>
    public static void LeaveJobSnapshot() => t_jobSnapshot = null;

    /// <summary>Every bash variable currently set, for <c>env</c> / <c>printenv</c>.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Enumerate()
    {
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var key = e.Key as string;
            if (key is null) continue;
            yield return new KeyValuePair<string, string>(key, e.Value as string ?? string.Empty);
        }
    }

    /// <summary>
    /// THE child-environment primitive: <paramref name="startInfo"/>'s environment becomes what a child of the shell
    /// gets - the EXPORTED variables only (<see cref="PsBash.Core.ShellExports"/>), plus whatever the caller set on
    /// it for the child.
    ///
    /// <para>Every scalar bash variable lives in the process environment, so without this a child would see the
    /// variables a script never exported (<c>x=1; sh -c 'echo $x'</c> printed 1). <see cref="ChildStdio.Start"/>
    /// calls it for every child ps-bash starts; the few spawns outside <see cref="ChildStdio"/> call it themselves
    /// (guards: <c>BashVariableStoreGuardTests</c>, <c>ChildEnvironmentGuardTests</c>). Idempotent.</para>
    /// </summary>
    public static void ApplyTo(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        PsBash.Core.ShellExports.RemoveUnexported(startInfo.Environment);
    }

    // ---------------------------------------------------------------- the export attribute

    /// <summary>
    /// A variable as a CHILD PROCESS sees it: null when unset or not exported. What a cmdlet that stands in for an
    /// external program reads its configuration with (<c>ls</c>: <c>COLUMNS</c>, <c>grep</c>: <c>GREP_COLORS</c>,
    /// <c>printenv</c>), and what <c>TZ</c> is for <c>printf %(...)T</c> (bash applies TZ only when exported).
    /// The shell's own view (expansions, builtins) is <see cref="Get"/>.
    /// </summary>
    public static string? GetExported(string name) => PsBash.Core.ShellExports.Get(name);

    /// <summary>The exported variables that are set: a child's environment, for <c>env</c> / <c>printenv</c> / <c>compgen -e</c>.</summary>
    public static IEnumerable<KeyValuePair<string, string>> EnumerateExported() => PsBash.Core.ShellExports.Enumerate();

    /// <summary>Whether <paramref name="name"/> has the export attribute.</summary>
    public static bool IsExported(string name) => PsBash.Core.ShellExports.IsExported(name);

    /// <summary><c>export NAME...</c> / <c>declare -x NAME...</c> (emitted: <c>PsBuild.MarkExported</c>).</summary>
    public static void Export(params string[] names) => PsBash.Core.ShellExports.Export(names);

    /// <summary><c>export -n NAME...</c> / <c>declare +x NAME...</c> (emitted: <c>PsBuild.MarkUnexported</c>).</summary>
    public static void Unexport(params string[] names) => PsBash.Core.ShellExports.Unexport(names);

    /// <summary><c>set -a</c> / <c>set +a</c> (emitted: <c>PsBuild.SetAllExport</c>).</summary>
    public static void SetAllExport(bool on) => PsBash.Core.ShellExports.AllExport = on;

    /// <summary>
    /// <c>NAME=v cmd</c> starts: the names are exported for the command (emitted: <c>PsBuild.TempExport</c>). Paired
    /// with <see cref="EndTempExport"/> in the binding's <c>finally</c>; the pairs nest per runspace.
    /// </summary>
    public static void TempExport(params string[] names)
        => VarFrameStack.Current.PushTempExport(names, PsBash.Core.ShellExports.BeginTemp(names));

    /// <summary>The command of the innermost <see cref="TempExport"/> ended: its names have their attribute back.</summary>
    public static void EndTempExport()
    {
        if (VarFrameStack.Current.PopTempExport() is { } frame)
            PsBash.Core.ShellExports.EndTemp(frame.Names, frame.Was);
    }
}
