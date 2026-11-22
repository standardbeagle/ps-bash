using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The pure rules behind <c>BASH_SOURCE</c>, a function's definition site and <c>caller</c> (bash 5.2; the end-to-end
/// oracle is <c>ShellLanguageGapDifferentialTests.FunctionSource_AndCaller</c> / <c>StdinScript_IsNotDashC</c>). Hand
/// asserts here pin the edge rows the differential script cannot reach one at a time.
/// </summary>
public class ShellFrameRuleTests
{
    // ─────────────── VarFrameStack.Compose (BASH_SOURCE) ───────────────

    [Fact]
    public void Compose_NoFunctions_IsTheFilesBeingRead()
        => Assert.Equal(["lib.sh", "main.sh"], VarFrameStack.Compose(["lib.sh", "main.sh"], []));

    [Fact]
    public void Compose_LibraryFunctionCalledFromTheScript_IsItsOwnFileThenTheScript()
        // main.sh: `. ./lib.sh; mf() { lf; }; mf` -- inside lf: BASH_SOURCE = (./lib.sh main.sh main.sh).
        => Assert.Equal(["./lib.sh", "main.sh", "main.sh"],
            VarFrameStack.Compose(["main.sh"], [("main.sh", 1), ("./lib.sh", 1)]));

    [Fact]
    public void Compose_FileSourcedInsideAFunction_SitsAboveThatFunction()
        // f (defined in main.sh) runs `. ./inc.sh`: the sourced file is read inside f's frame.
        => Assert.Equal(["./inc.sh", "main.sh", "main.sh"],
            VarFrameStack.Compose(["./inc.sh", "main.sh"], [("main.sh", 1)]));

    [Fact]
    public void Compose_FrameWithNoRecordedSource_IsSkipped()
        => Assert.Equal(["main.sh"], VarFrameStack.Compose(["main.sh"], [(null, 1)]));

    // ─────────────── FunctionSources.DefinitionSite ───────────────

    [Theory]
    [InlineData("./lib.sh", false, "./lib.sh")]
    [InlineData("./lib.sh", true, "./lib.sh")]
    [InlineData(null, true, "environment")]  // `bash -c 'f() { ...; }'`
    [InlineData(null, false, "main")]        // `printf 'f() { ...; }' | bash`, `bash -s`
    [InlineData("", false, "main")]
    public void DefinitionSite_FileElseEnvironmentForDashCElseMain(string? file, bool dashC, string expected)
        => Assert.Equal(expected, FunctionSources.DefinitionSite(file is null ? [] : [file], dashC));

    // ─────────────── ShellCaller.Report (`caller`) ───────────────

    [Fact]
    public void Caller_NoFrame_FailsWithNothing()
        => Assert.Equal(new ShellCaller.Result(null, null, 1), ShellCaller.Report([], [], [], (string?)null));

    [Fact]
    public void Caller_ScriptTopLevel_IsZeroNull()
        => Assert.Equal(new ShellCaller.Result("0 NULL", null, 0), ShellCaller.Report(["0"], ["main"], ["main.sh"], (string?)null));

    [Fact]
    public void Caller_InAFunction_IsTheCallLineAndFile()
        => Assert.Equal(new ShellCaller.Result("4 main.sh", null, 0),
            ShellCaller.Report(["4", "0"], ["mf", "main"], ["main.sh", "main.sh"], (string?)null));

    [Theory]
    [InlineData("0", "3 mf ./lib.sh", 0)]
    [InlineData(" 1 ", "4 main main.sh", 0)]
    [InlineData("2", null, 1)]   // past the outermost frame
    [InlineData("-1", null, 1)]
    public void Caller_N_IsLineFunctionAndFileOfThatFrame(string operand, string? output, int status)
    {
        var r = ShellCaller.Report(["3", "4", "0"], ["lf", "mf", "main"], ["./lib.sh", "./lib.sh", "main.sh"], operand);
        Assert.Equal(output, r.Output);
        Assert.Null(r.Error);
        Assert.Equal(status, r.Status);
    }

    [Fact]
    public void Caller_NonNumericOperand_IsInvalidNumberStatusTwo()
        => Assert.Equal(new ShellCaller.Result(null, "caller: x: invalid number", 2),
            ShellCaller.Report(["3", "0"], ["f", "main"], ["main.sh", "main.sh"], "x"));
}
