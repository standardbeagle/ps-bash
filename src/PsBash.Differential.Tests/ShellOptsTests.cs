using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>$SHELLOPTS tracks <c>set -o</c> options and is readonly.</summary>
public class ShellOptsTests
{
    [Fact]
    public Task ShellOpts_TracksXtraceAndPipefail() => AssertOracle.EqualAsync(
        "( set -x; echo $SHELLOPTS | grep -c xtrace; set +x ) 2>/dev/null; echo $SHELLOPTS | grep -c xtrace; set -o pipefail; echo $SHELLOPTS | grep -c pipefail");

    [Fact]
    public Task ShellOpts_AssignmentFails() => AssertOracle.EqualAsync(
        "{ SHELLOPTS=x; } 2>/dev/null\necho status=$?");
}
