using System.Management.Automation;
using System.Reflection;

namespace PsBash.Cmdlets;

/// <summary>
/// Stops the upstream pipeline from inside a streaming consumer (<c>head -n 1</c>, <c>grep -q</c>,
/// <c>grep -m N</c>) — the early-exit a real pipe gets from SIGPIPE. PowerShell's internal
/// <c>StopUpstreamCommandsException</c> is the mechanism <c>Select-Object -First N</c> uses; it is internal,
/// so it is reached via reflection. A consumer must produce all its output and set its exit code BEFORE
/// calling <see cref="Throw"/>. If the exception cannot be built the call returns and the cmdlet simply
/// ignores the rest of its input (output stays correct; only the early stop is missed).
/// </summary>
internal static class UpstreamStop
{
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2026",
        Justification = "StopUpstreamCommandsException is an internal SMA type that is " +
            "always present in the PowerShell host runspace where this cmdlet executes " +
            "(the non-AOT ps-bash-host); it is never trimmed away.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2075",
        Justification = "The SMA assembly and the resolved exception type are present at " +
            "runtime in the host; reflecting over its constructors is safe.")]
    public static void Throw(Cmdlet cmdlet)
    {
        var t = typeof(PSObject).Assembly.GetType(
            "System.Management.Automation.StopUpstreamCommandsException");
        if (t == null) return;
        var ctor = t.GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault();
        if (ctor == null) return;
        Exception ex;
        try
        {
            ex = (Exception)ctor.Invoke(new object[] { cmdlet });
        }
        catch
        {
            return;
        }
        throw ex;
    }
}
