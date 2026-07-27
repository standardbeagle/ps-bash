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
    public static string? Get(string name) => Environment.GetEnvironmentVariable(name);

    /// <summary>
    /// Write a bash variable. A null <paramref name="value"/> UNSETS it (the
    /// .NET convention, and what <c>unset</c> / an env-prefix restore needs).
    /// </summary>
    public static void Set(string name, string? value)
        => Environment.SetEnvironmentVariable(name, value);

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
    /// Make the bash variables visible to a CHILD PROCESS about to be spawned.
    ///
    /// <para>This is the constraint that rules out most designs for a
    /// per-invocation store: <c>export</c> has to actually reach the child, or
    /// <c>export PATH=…</c> / <c>export NODE_ENV=…</c> stop working. Verified
    /// live — <c>export CHILDVAR=visible; pwsh -c '$env:CHILDVAR'</c> prints
    /// <c>visible</c>.</para>
    ///
    /// <para>While the store IS the process environment this is a NO-OP: the
    /// child inherits automatically. It exists as a called seam so that the day
    /// the store moves off the process environment, every spawn site is already
    /// wired and the change is one method body rather than an audit of every
    /// <c>ProcessStartInfo</c> in the tree.</para>
    /// </summary>
    public static void ApplyTo(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        // Intentionally empty — see the remarks above. Do NOT "optimize" this
        // call away at the call sites; the call IS the deliverable.
    }
}
