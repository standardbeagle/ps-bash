using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// A here-text on a command that reads no stdin is evaluated and discarded, then the command runs on its own (bash expands
/// the body and the command never reads it). Emitter shape only; the behavior is Differential
/// <c>RedirectOperatorDifferentialTests.HereText_OnACommandThatReadsNothing</c>.
/// </summary>
public class HereTextNonReaderEmitterTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    // REGRESSION: the body was piped into the command: `echo hi <<<x` printed "The input object cannot be bound" (echo has
    // no pipeline input) and `true <<<x` (an expression) made the whole script fail to parse.
    [Theory]
    [InlineData("echo hi <<<x")]
    [InlineData("echo {a} <<<x")]
    [InlineData("true <<<x")]
    [InlineData(": <<<x")]
    [InlineData("printf hi <<EOF\nbody\nEOF\n")]
    [InlineData("pwd <<<x")]
    public void Transpile_HereTextOnANonReader_IsDiscardedNotPiped(string bash)
    {
        var ps = T(bash);

        Assert.Contains("$null = ", ps);
        Assert.DoesNotContain("Emit-BashLine", ps);
    }

    // The readers keep the pipe: a reading command, a function that shadows a builtin name, a program, an operand that
    // may be `-` at run time.
    [Theory]
    [InlineData("cat <<<x")]
    [InlineData("read l <<<x")]
    [InlineData("echo() { cat; }; echo <<<x")]
    [InlineData("someprog_zz <<<x")]
    [InlineData("g=-; cat $g <<<x")]
    [InlineData("g=-; diff $g f <<<x")]
    public void Transpile_HereTextOnAReader_IsStillItsStdin(string bash)
        => Assert.Contains("Emit-BashLine", T(bash));

    // An operand with an expansion cannot make echo read stdin.
    [Fact]
    public void Transpile_HereTextOnEchoWithAnExpansion_IsDiscarded()
        => Assert.Contains("$null = ", T("echo $x <<<y"));

    // A bare `exec <<EOF` rebinds the shell's own stdin (Invoke-BashExecFd): the body is not discarded.
    [Fact]
    public void Transpile_HereTextOnABareExec_IsTheShellsStdin()
    {
        var ps = T("exec <<EOF\nbody\nEOF\n");

        Assert.Contains("Invoke-BashExecFd 0", ps);
        Assert.DoesNotContain("$null = @", ps);
    }

    [Fact]
    public void Transpile_HereTextOnANonReader_StillExpandsTheBody()
        => Assert.Contains("Invoke-BashDate", T("echo hi <<EOF\n$(date)\nEOF\n"));
}
