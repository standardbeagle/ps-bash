using System.Management.Automation;
using PsBash.Core.Parser;

namespace PsBash.Cmdlets;

/// <summary>
/// Carries the caller's live <c>set -o pipefail</c> / <c>set -u</c> state into the transpile of
/// <c>source</c>d and <c>eval</c>ed text, so that text gets the per-stage status capture and unbound-variable
/// checks it would have in bash even though it never mentions those options itself.
/// </summary>
internal static class RuntimeOptionHints
{
    public static IDisposable Apply(SessionState session)
    {
        var priorPipefail = PsEmitter.InheritedPipefail;
        var priorNounset = PsEmitter.InheritedNounset;
        PsEmitter.InheritedPipefail = IsOn(session, "global:__BashPipefail");
        PsEmitter.InheritedNounset = IsOn(session, "global:__BashNounset");
        return new Restore(priorPipefail, priorNounset);
    }

    private static bool IsOn(SessionState session, string name)
        => session.PSVariable.GetValue(name) is true;

    private sealed class Restore(bool pipefail, bool nounset) : IDisposable
    {
        public void Dispose()
        {
            PsEmitter.InheritedPipefail = pipefail;
            PsEmitter.InheritedNounset = nounset;
        }
    }
}
