using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// Whether a unit gets the errexit statement checks is decided on its parsed commands (<c>PsEmitter.CanSetErrexit</c>,
/// <c>CanSetErrTrap</c>), never on the source text; errexit itself is decided once, at run time, by
/// <see cref="PsBuild.ErrexitCheck"/>. REGRESSION: a regex over the text missed <c>set -$o</c> / <c>set -o $nm</c>, and
/// the unit's bare <c>false</c> ran its own <c>exit</c> (<c>o=e; ( set -$o; f() { false; echo inf; }; f || echo caught )</c>
/// ended the whole script; bash prints <c>inf</c> and goes on). Emitted-text surface (which units carry checks); behavior vs
/// bash is in <c>Differential.Tests/ErrexitStatusDifferentialTests</c>.
/// </summary>
public class ErrexitModeEmitterTests
{
    private const string Check = "$global:__BashErrexit -and -not $global:__BashErrexitSuppress";

    private static bool Checked(string bash) => PsEmitter.Transpile(bash)!.Contains(Check, StringComparison.Ordinal);

    [Theory]
    [InlineData("o=e; set -$o; ls /nosuch; echo after")]
    [InlineData("nm=errexit; set -o $nm; ls /nosuch; echo after")]
    [InlineData("set -o \"$(echo errexit)\"; ls /nosuch")]
    [InlineData("c=set; $c -e; ls /nosuch")]
    [InlineData("eval \"$cmd\"; ls /nosuch")]
    [InlineData("eval 'set -e'; ls /nosuch")]
    [InlineData("alias se='set -e'; ls /nosuch")]
    [InlineData("trap 'echo E' ERR; ls /nosuch")]
    [InlineData("trap \"$h\" ERR; ls /nosuch")]
    [InlineData("s=ERR; trap 'echo E' $s; ls /nosuch")]
    [InlineData("set -euo pipefail\nls /nosuch")]
    [InlineData("set -o pipefail -e; ls /nosuch")]
    [InlineData("fc -s; ls /nosuch")]                    // re-runs a command known only at run time
    [InlineData("source ./lib.sh; ls /nosuch")]          // the file's set -e stays on here
    [InlineData(". ./lib.sh; ls /nosuch")]
    public void Transpile_UnitThatCanTurnErrexitOnOrSetAnErrTrap_GetsTheChecks(string bash)
        => Assert.True(Checked(bash));

    [Theory]
    [InlineData("grep -e x f; ls /nosuch")]               // an -e that is no set operand
    [InlineData("echo 'set -e'; ls /nosuch")]             // quoted data
    [InlineData("# set -e\nls /nosuch")]
    [InlineData("set -- -e; ls /nosuch")]                 // positional parameters
    [InlineData("set a -e; ls /nosuch")]
    [InlineData("grep ERR log; ls /nosuch")]
    [InlineData("trap 'echo bye' EXIT; ls /nosuch")]
    [InlineData("trap ERR; ls /nosuch")]                   // resets the trap
    [InlineData("set +e; ls /nosuch")]
    [InlineData("trap - DEBUG; ls /nosuch")]              // resets, runs nothing
    [InlineData("fc -l; ls /nosuch")]                     // a listing runs nothing
    public void Transpile_ErrexitOnlyAsData_GetsNoChecks(string bash)
        => Assert.False(Checked(bash));

    /// <summary>
    /// A unit without the checks still leaves on a failing <c>false</c> through the one run-time decision (the exempt
    /// counter and the subshell's scoped return), never a bare <c>exit</c> of its own.
    /// </summary>
    [Fact]
    public void Transpile_FalseOutsideErrexitMode_LeavesOnlyThroughTheOneCheck()
    {
        var ps = PsEmitter.Transpile("false")!;
        Assert.DoesNotContain("if ($global:__BashErrexit) { exit", ps, StringComparison.Ordinal);
        Assert.Contains(Check, ps, StringComparison.Ordinal);
    }

    /// <summary>
    /// An EXIT action's commands are checked like any others (<c>( set -e; trap 'false; echo x' EXIT; true )</c> prints
    /// nothing, status 1).
    /// </summary>
    [Fact]
    public void TranspileTrapAction_ExitAction_GetsTheChecks()
        => Assert.Contains(Check, PsBash.Core.Transpiler.BashTranspiler.TranspileTrapAction("ls /nosuch; echo x", "EXIT"), StringComparison.Ordinal);

    private static int Checks(string bash) => Regex.Matches(PsEmitter.Transpile(bash)!, Regex.Escape(Check)).Count;

    /// <summary>
    /// An assignment's status is always set (it shows through a function's status, <c>return</c>, <c>if</c>, errexit),
    /// not only when the text holds <c>$?</c>. REGRESSION: <c>f() { [ -n "" ] || y=def; }; if f; then …</c> took the
    /// else branch.
    /// </summary>
    [Theory]
    [InlineData("f() { [ -n \"\" ] || y=def; }")]
    [InlineData("false; y=def; echo hi")]
    public void Transpile_PlainAssignment_AlwaysSetsStatusZero(string bash)
        => Assert.Contains("$env:y = \"def\"; $global:LASTEXITCODE = 0", PsEmitter.Transpile(bash)!, StringComparison.Ordinal);

    /// <summary>
    /// The builtins that fail are errexit failures (only return/exit/break/continue leave by themselves): each carries a
    /// check that a <c>:</c> with no redirect does not.
    /// </summary>
    [Theory]
    [InlineData("shift 5")]
    [InlineData("declare -p nosuch")]
    [InlineData("shopt -s nosuch")]
    [InlineData("unalias nosuch")]
    [InlineData("local x")]
    [InlineData("readonly r=2")]
    [InlineData(": > /nodir/x")]
    [InlineData("x=1 > /nodir/x")]
    [InlineData("arr=( $(false) )")]
    public void Transpile_FailingBuiltinUnderErrexit_IsChecked(string command)
        => Assert.True(Checks("set -e; " + command) > Checks("set -e; :"), PsEmitter.Transpile("set -e; " + command));
}