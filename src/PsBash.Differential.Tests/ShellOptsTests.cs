using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>$SHELLOPTS tracks <c>set -o</c> options and is readonly.</summary>
public class ShellOptsTests
{
    [SkippableFact]
    public Task ShellOpts_TracksXtraceAndPipefail() => AssertOracle.EqualAsync(
        "( set -x; echo $SHELLOPTS | grep -c xtrace; set +x ) 2>/dev/null; echo $SHELLOPTS | grep -c xtrace; set -o pipefail; echo $SHELLOPTS | grep -c pipefail");

    // vi/emacs/braceexpand were absent from $SHELLOPTS and `test -o`; `set -v` was refused; $BASHOPTS was unset.
    [SkippableFact]
    public Task ShellOpts_RememberedOptions_AndBashopts() => AssertOracle.EqualAsync(
        "set -o vi; echo $SHELLOPTS; test -o vi && echo vi; test -o emacs || echo noemacs; test -o braceexpand && echo brace; set -v; echo \"[$-]\"; set +v; echo $BASHOPTS; shopt -s nullglob; echo $BASHOPTS | grep -c nullglob");

    // An exported SHELLOPTS carries the options into a child shell, which turns each one on at startup; ps-bash's
    // child shell ignored it (no xtrace, no errexit there). Unexported, the child starts with its defaults.
    [SkippableFact]
    public Task ShellOpts_Exported_TurnsTheOptionsOnInAChildShell() => AssertOracle.EqualAsync(
        "set -o pipefail; bash -c 'echo \"$SHELLOPTS\"' | grep -c pipefail; set +o pipefail\n"
        + "export SHELLOPTS\n"
        + "( set -e; bash -c 'false; echo survived' ); echo \"e=$?\"\n"
        + "set -o pipefail; bash -c 'echo \"$SHELLOPTS\"' | tr : '\\n' | grep -c -x pipefail; set +o pipefail\n"
        + "( set -x; bash -c 'echo two' ) 2>&1 | grep -c -x '+ echo two'\n");

    [SkippableFact]
    public Task ShellOpts_AssignmentFails() => AssertOracle.EqualAsync(
        "{ SHELLOPTS=x; } 2>/dev/null\necho status=$?");
}
