using System.Collections;
using System.Diagnostics;
using PsBash.Core;

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
    /// from set-to-empty, which the <c>${VAR-word}</c> operators rely on).
    /// <para>Every by-name member takes a bash name or a store name: a reserved spelling (<c>path</c>) is its own
    /// variable on Windows, not <c>PATH</c> (<see cref="BashNameCase.ToStore(string)"/>, idempotent;
    /// <c>environment-model.md</c> section 12).</para></summary>
    public static string? Get(string name)
    {
        name = BashNameCase.ToStore(name);
        // The session's secrets first: they are never in the process environment (ShellSecrets).
        if (ShellSecrets.Current is { } secrets && secrets.TryGet(name, out var secret)) return secret;
        return ScalarOverlay.Current is { } job ? job.Get(name) : Environment.GetEnvironmentVariable(name);
    }

    /// <summary>
    /// Write a bash variable. A null <paramref name="value"/> UNSETS it (the
    /// .NET convention, and what <c>unset</c> / an env-prefix restore needs).
    /// </summary>
    public static void Set(string name, string? value)
    {
        name = BashNameCase.ToStore(name);
        // A secret's name stays in its session: the write replaces (or, unset, drops) the session's value.
        if (ShellSecrets.Current is { } secrets && secrets.TryAssign(name, value)) return;
        if (ScalarOverlay.Current is { } job) job.Set(name, value);
        else Environment.SetEnvironmentVariable(name, value);
    }

    // An in-process background job's scalars are its own copy, taken at the `&` (ScalarOverlay, keyed by the job's
    // runspace): the foreground and the other jobs go on writing the process environment while it runs
    // (background-jobs.md section 3, environment-model.md section 8). Every member here resolves the store per call.

    /// <summary>Every bash variable currently set, for <c>env</c> / <c>printenv</c>, under its STORE name (callers key
    /// case-insensitive tables by it, where <c>path</c> and <c>PATH</c> would collide). Whatever PRINTS the name
    /// demangles it with <see cref="BashNameCase.ToBash"/>. A native spelling Windows created a variable under
    /// (<c>windir</c>, <c>Path</c>) comes back UPPER-cased (<see cref="BashNameCase.FromEnvironment"/>): that IS the
    /// variable, the native spelling being a separate (diverted) bash name.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Enumerate()
    {
        if (ScalarOverlay.Current is { } job)
        {
            foreach (var kv in job.Entries())
                yield return new KeyValuePair<string, string>(BashNameCase.FromEnvironment(kv.Key), kv.Value);
            yield break;
        }
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var key = e.Key as string;
            if (key is null) continue;
            yield return new KeyValuePair<string, string>(BashNameCase.FromEnvironment(key), e.Value as string ?? string.Empty);
        }
    }

    /// <summary>
    /// Every bash variable for a LISTING (<c>set</c>, <c>declare -p</c>, <c>Get-ChildItem Env:</c>): <see cref="Enumerate"/>,
    /// with the session's secrets listed under their names and <see cref="ShellSecrets.Mask"/> for the value, so a
    /// habitual <c>env | grep KEY</c> does not paint a key into the screen, a transcript or a log. Never for a copy of
    /// the values (a subshell's save, a job's state): <see cref="Enumerate"/> is that.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> EnumerateListed()
    {
        var secrets = ShellSecrets.Current;
        foreach (var kv in Enumerate())
            if (secrets is null || !secrets.Contains(BashNameCase.ToStore(kv.Key))) yield return kv;
        if (secrets is null) yield break;
        foreach (var name in secrets.Names)
            yield return new KeyValuePair<string, string>(BashNameCase.FromEnvironment(name), ShellSecrets.Mask);
    }

    /// <summary>Whether <paramref name="name"/> is one of the session's secrets (<see cref="ShellSecrets"/>).</summary>
    public static bool IsSecret(string name) => ShellSecrets.Current is { } secrets && secrets.Contains(BashNameCase.ToStore(name));

    /// <summary><paramref name="value"/> as a listing prints <paramref name="name"/>'s: <see cref="ShellSecrets.Mask"/> for a secret.</summary>
    public static string Listed(string name, string value) => IsSecret(name) ? ShellSecrets.Mask : value;

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

    /// <summary>The exported variables that are set: a child's environment, for <c>env</c> / <c>printenv</c> / <c>compgen -e</c>.
    /// Store names, as <see cref="Enumerate"/> (a native <c>windir</c> is <c>WINDIR</c>). A child's environment block is
    /// built from <see cref="PsBash.Core.ShellExports"/> directly and keeps Windows' own spelling.</summary>
    public static IEnumerable<KeyValuePair<string, string>> EnumerateExported()
    {
        foreach (var kv in PsBash.Core.ShellExports.Enumerate())
            yield return new KeyValuePair<string, string>(BashNameCase.FromEnvironment(kv.Key), kv.Value);
    }

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
    {
        // `NAME=v cmd` passes a secret the session holds on to that command, as bash exports NAME for it.
        ShellSecrets.Current?.BeginTempGrant(names);
        VarFrameStack.Current.PushTempExport(names, PsBash.Core.ShellExports.BeginTemp(names));
    }

    /// <summary>The command of the innermost <see cref="TempExport"/> ended: its names have their attribute back.</summary>
    public static void EndTempExport()
    {
        if (VarFrameStack.Current.PopTempExport() is { } frame)
        {
            PsBash.Core.ShellExports.EndTemp(frame.Names, frame.Was);
            ShellSecrets.Current?.EndTempGrant(frame.Names);
        }
    }
}
