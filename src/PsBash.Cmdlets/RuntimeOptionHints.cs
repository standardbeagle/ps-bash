using System.Management.Automation;
using PsBash.Core.Parser;

namespace PsBash.Cmdlets;

/// <summary>
/// Carries the caller's live <c>set -o pipefail</c> / <c>set -u</c> / <c>set -e</c> / ERR trap / errtrace state into
/// the transpile of <c>source</c>d and <c>eval</c>ed text, so that text gets the per-stage status capture, unbound-variable
/// checks and errexit / ERR-trap checks it would have in bash even though it never mentions those options itself. Public:
/// emitted run-time eval (<c>PsBuild.RunBashTextAtRunTime</c>) applies it around its transpile, on the pipeline thread.
/// </summary>
public static class RuntimeOptionHints
{
    /// <summary>Set the transpiler's inherited-option inputs from <paramref name="session"/>'s live shell options until the
    /// result is disposed.</summary>
    public static Restore Apply(SessionState session)
    {
        var restore = new Restore(PsEmitter.InheritedPipefail, PsEmitter.InheritedNounset,
            PsEmitter.InheritedErrexit, PsEmitter.InheritedErrTrap, PsEmitter.InheritedErrtrace);
        PsEmitter.InheritedPipefail = IsOn(session, "global:__BashPipefail");
        PsEmitter.InheritedNounset = IsOn(session, "global:__BashNounset");
        PsEmitter.InheritedErrexit = IsOn(session, "global:__BashErrexit");
        PsEmitter.InheritedErrTrap = session.PSVariable.GetValue("global:__BashTrapERR") is { } trap && trap is not "";
        PsEmitter.InheritedErrtrace = IsOn(session, "global:__BashErrtrace");
        return restore;
    }

    private static bool IsOn(SessionState session, string name)
        => session.PSVariable.GetValue(name) is true;

    /// <summary>Puts the inputs back as they were before <see cref="Apply"/>.</summary>
    public sealed class Restore : IDisposable
    {
        private readonly bool _pipefail, _nounset, _errexit, _errTrap, _errtrace;

        internal Restore(bool pipefail, bool nounset, bool errexit, bool errTrap, bool errtrace)
            => (_pipefail, _nounset, _errexit, _errTrap, _errtrace) = (pipefail, nounset, errexit, errTrap, errtrace);

        /// <summary>Restore the inherited-option inputs.</summary>
        public void Dispose()
        {
            PsEmitter.InheritedPipefail = _pipefail;
            PsEmitter.InheritedNounset = _nounset;
            PsEmitter.InheritedErrexit = _errexit;
            PsEmitter.InheritedErrTrap = _errTrap;
            PsEmitter.InheritedErrtrace = _errtrace;
        }
    }
}
