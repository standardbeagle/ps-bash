using System.Management.Automation;
using Xunit;
using PsBash.Cmdlets;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The source prefix of a shell diagnostic (bash error.c <c>error_prolog</c>). bash 5.2 is the oracle (WSL):
/// <c>bash s.sh</c> with <c>nosuch</c> on line 3 is <c>s.sh: line 3: nosuch: command not found</c>, a sourced
/// file names itself (<c>./t.sh: line 2: ...</c>), <c>bash -c</c> is <c>bash: line 1: ...</c>, <c>bash -c '...' me</c>
/// is <c>me: line 1: ...</c>, an interactive shell is <c>bash: ...</c>. The end-to-end cases are
/// Differential.Tests <c>ShellDiagnosticLineDifferentialTests</c>; this is the pure rule.
/// </summary>
public class ShellDiagnosticTests
{
    [Theory]
    [InlineData("s.sh", "bash", 3, "s.sh: line 3: ")]
    [InlineData("./t.sh", "s.sh", 2, "./t.sh: line 2: ")]
    [InlineData("", "bash", 1, "bash: line 1: ")]
    [InlineData("", "me", 7, "me: line 7: ")]
    [InlineData(null, null, 4, "bash: line 4: ")]
    [InlineData("", "", 4, "bash: line 4: ")]
    public void Prefix_NonInteractive_NamesTheSourceThenArg0(string? source, string? arg0, int line, string expected)
        => Assert.Equal(expected, ShellDiagnostic.Prefix(source, arg0, line, interactive: false));

    [Fact]
    public void Prefix_BashSourceArray_UsesItsFirstElement()
        // BASH_SOURCE is an array in a function; element 0 is the file the running command came from.
        => Assert.Equal("./lib.sh: line 9: ",
            ShellDiagnostic.Prefix(new object[] { "./lib.sh", "main.sh" }, "bash", 9, interactive: false));

    [Fact]
    public void Prefix_EmptyBashSourceArray_FallsBackToArg0()
        => Assert.Equal("bash: line 2: ", ShellDiagnostic.Prefix(Array.Empty<object>(), "bash", 2, interactive: false));

    [Fact]
    public void Prefix_PSObjectWrappedValues_AreUnwrapped()
        => Assert.Equal("x.sh: line 5: ",
            ShellDiagnostic.Prefix(new PSObject(new[] { new PSObject("x.sh") }), new PSObject("bash"), 5, interactive: false));

    [Fact]
    public void Prefix_NoLineRecorded_LeavesTheLineOut()
        // A cmdlet called directly (no emitted SetLine) has line 0: never print "line 0".
        => Assert.Equal("s.sh: ", ShellDiagnostic.Prefix("s.sh", "bash", 0, interactive: false));

    [Fact]
    public void Prefix_Interactive_IsBashOnly()
        => Assert.Equal("bash: ", ShellDiagnostic.Prefix("s.sh", "me", 12, interactive: true));
}
