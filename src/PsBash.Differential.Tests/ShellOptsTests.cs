using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>$SHELLOPTS tracks <c>set -o</c> options and is readonly.</summary>
public class ShellOptsTests
{
    [Fact]
    public Task ShellOpts_TracksXtraceAndPipefail() => AssertOracle.EqualAsync(
        "( set -x; echo $SHELLOPTS | grep -c xtrace; set +x ) 2>/dev/null; echo $SHELLOPTS | grep -c xtrace; set -o pipefail; echo $SHELLOPTS | grep -c pipefail");

    // vi/emacs/braceexpand were absent from $SHELLOPTS and `test -o`; `set -v` was refused; $BASHOPTS was unset.
    [Fact]
    public Task ShellOpts_RememberedOptions_AndBashopts() => AssertOracle.EqualAsync(
        "set -o vi; echo $SHELLOPTS; test -o vi && echo vi; test -o emacs || echo noemacs; test -o braceexpand && echo brace; set -v; echo \"[$-]\"; set +v; echo $BASHOPTS; shopt -s nullglob; echo $BASHOPTS | grep -c nullglob");

    [Fact]
    public Task ShellOpts_AssignmentFails() => AssertOracle.EqualAsync(
        "{ SHELLOPTS=x; } 2>/dev/null\necho status=$?");
}
