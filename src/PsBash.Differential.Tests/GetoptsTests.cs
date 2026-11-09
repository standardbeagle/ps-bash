using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary><c>getopts</c> was a missing builtin (command not found), so every option-parsing script failed.</summary>
public class GetoptsTests
{
    [Fact]
    public Task Loop_WithArgumentsAndOptind() => AssertOracle.EqualAsync(
        "set -- -a -b X -c rest1 rest2; while getopts 'ab:c' o; do echo \"o=$o arg=[$OPTARG]\"; done; shift $((OPTIND-1)); echo \"left: $@\"");

    [Fact]
    public Task ClusteredFlags_AndAttachedArgument() => AssertOracle.EqualAsync(
        "set -- -abfile -c; while getopts 'ab:c' o; do echo \"$o [$OPTARG] $OPTIND\"; done");

    [Fact]
    public Task SilentMode_UnknownAndMissingArgument() => AssertOracle.EqualAsync(
        "set -- -x -b; while getopts ':ab:' o; do echo \"$o [$OPTARG]\"; done");

    [Fact]
    public Task NonSilent_Diagnostics_GoToStderr() => AssertOracle.EqualAsync(
        "set -- -x -b; while getopts 'ab:' o 2>/dev/null; do echo \"$o [$OPTARG]\"; done");

    [Fact]
    public Task DoubleDash_EndsOptions() => AssertOracle.EqualAsync(
        "set -- -a -- -b; while getopts 'ab' o; do echo \"$o\"; done; echo \"optind=$OPTIND\"");

    [Fact]
    public Task ExplicitArguments_AndExitStatus() => AssertOracle.EqualAsync(
        "getopts 'ab:' o -a; echo \"o=$o rc=$?\"; OPTIND=1; getopts 'ab:' o plain; echo \"o=$o rc=$?\"");

    [Fact]
    public Task InsideFunction_UsesFunctionArguments() => AssertOracle.EqualAsync(
        "f() { OPTIND=1; while getopts 'n:v' o; do echo \"$o=$OPTARG\"; done; }; f -n 5 -v; f -v");

    [Fact]
    public Task Optind_StartsAtOne() => AssertOracle.EqualAsync("echo \"[$OPTIND]\"");

    [Fact]
    public Task OptindPastTheEnd_ClampsToArgCountPlusOne() => AssertOracle.EqualAsync(
        "set -- -h; OPTIND=9; getopts 'hc:' o; echo \"rc=$? OPTIND=$OPTIND\"; set --; OPTIND=4; getopts 'hc:' o; echo \"rc=$? OPTIND=$OPTIND\"");

    [Fact]
    public Task SetPositionals_DoesNotResetOptind() => AssertOracle.EqualAsync(
        "set -- -a; while getopts 'ab:' o; do :; done; echo OPTIND=$OPTIND; set -- -c -d; while getopts 'cd' o; do echo $o; done; echo OPTIND=$OPTIND");

    [Fact]
    public Task InvalidVariableName_FailsButScans() => AssertOracle.EqualAsync(
        "set -- -c foo -h; getopts 'hc:' opt- 2>/dev/null; echo status=$? OPTARG=$OPTARG OPTIND=$OPTIND");
}