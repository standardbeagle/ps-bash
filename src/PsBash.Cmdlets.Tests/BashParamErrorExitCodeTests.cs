using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

public class BashParamErrorExitCodeTests
{
    // Oracle: `bash -c 'echo ${undef?msg}'` and `set -u; echo $u` end the shell with 127; a bad substitution with 1.
    [Theory]
    [InlineData("parameter null or not set", 127)]
    [InlineData("unbound variable", 127)]
    [InlineData("bad substitution", 1)]
    public void Reported_CarriesBashExitStatus(string message, int expected)
        => Assert.Equal(expected, BashParamErrorException.Reported("x: ", message).Data["psbash.exitcode"]);
}
