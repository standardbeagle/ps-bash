using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// <c>set</c> with options it accepts is status 0, whatever ran before it (bash 5.2: <c>false</c> or a failed
/// <c>shopt -s nosuch</c>, then <c>set -o errexit</c> on the next line, then a command: it runs). The emitted errexit check
/// after every statement reads <c>$global:LASTEXITCODE</c>, so a <c>set</c> form that left it alone took the previous
/// command's 1 for its own and exited right there. REGRESSION: the errexit and xtrace forms (<c>set -e</c>,
/// <c>set -o errexit</c>, <c>set +e</c>, <c>set -x</c>, <c>set +o xtrace</c>, <c>set -e -o pipefail</c>) and every positional form (<c>set -- a</c>, <c>set a b</c>) set no
/// status; <c>$SH -c $'shopt -s nosuch\nset -o errexit\nset -o pipefail\nls | false | wc -l'</c> printed nothing
/// (Spec pipeline.spec.sh "shopt -s lastpipe and shopt -s no_last_fork interaction"). The one place: the end of
/// PsEmitter.EmitSet.
/// </summary>
public class SetBuiltinStatusGuardTests
{
    public static TheoryData<string> SetForms => new()
    {
        "set -e", "set -o errexit", "set +e", "set +o errexit",
        "set -x", "set +x", "set -o xtrace", "set +o xtrace",
        "set -f", "set +f", "set -u", "set +u", "set -o pipefail", "set +o pipefail",
        "set -e -o pipefail", "set -euo pipefail", "set -ex", "set -eu", "set -o nounset", "set -E",
        "set -- a b", "set --", "set a b", "set -- *.txt", "set -e -- a",
    };

    [Theory]
    [MemberData(nameof(SetForms))]
    public void Transpile_SetOptions_EndsWithStatusZero(string bash)
    {
        string t = PsEmitter.Transpile(bash)!;
        int check = t.IndexOf("; if ($global:LASTEXITCODE -ne 0", System.StringComparison.Ordinal);
        string own = (check < 0 ? t : t[..check]).TrimEnd().TrimEnd(';').TrimEnd();
        Assert.EndsWith(PsBuild.SetStatus(0), own);
    }
}
