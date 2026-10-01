using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>uniq [INPUT [OUTPUT]]</c>: the SECOND operand is the file the result is written to
/// (GNU coreutils 9.4; <c>-</c> = stdin / stdout), never a second input. Before, <c>uniq in out</c>
/// printed the dedup of both files and left <c>out</c> untouched.
/// </summary>
public class UniqOutputFileTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    public UniqOutputFileTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), $"psb-uqo-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmp);
        File.WriteAllText(P("in"), "a\na\nb\nb\nb\nc\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    private string P(string rel) => Path.Combine(_tmp, rel);

    private CmdResult Run(string body) => CmdResult.Run(_fixture.AcquireFresh(),
        $"Push-Location '{_tmp}'; try {{ {body} }} finally {{ Pop-Location }}");

    [Fact]
    public void TwoOperands_WriteTheResultToTheSecond_AndPrintNothing()
    {
        var r = Run("Invoke-BashUniq in out").AssertSuccess();
        Assert.Equal("", r.Stdout);
        Assert.Equal("a\nb\nc\n", File.ReadAllText(P("out")));
        Assert.Equal("a\na\nb\nb\nb\nc\n", File.ReadAllText(P("in")));
    }

    [Fact]
    public void Counts_GoToTheOutputFile()
    {
        Run("Invoke-BashUniq '-c' in out").AssertSuccess();
        Assert.Equal("      2 a\n      3 b\n      1 c\n", File.ReadAllText(P("out")));
    }

    [Fact]
    public void OutputFile_IsTruncatedNotAppended()
    {
        File.WriteAllText(P("out"), "old\nold\nold\nold\nold\nold\nold\n");
        Run("Invoke-BashUniq '-d' in out").AssertSuccess();
        Assert.Equal("a\nb\n", File.ReadAllText(P("out")));
    }

    [Fact]
    public void EmptyResult_StillCreatesTheOutputFile()
    {
        File.WriteAllText(P("u"), "a\na\n");
        Run("Invoke-BashUniq '-u' u out").AssertSuccess();
        Assert.True(File.Exists(P("out")));
        Assert.Equal("", File.ReadAllText(P("out")));
    }

    [Fact]
    public void StdinInput_DashThenOutputFile()
    {
        var r = Run("'x','x','y' | Invoke-BashUniq - out").AssertSuccess();
        Assert.Equal("", r.Stdout);
        Assert.Equal("x\ny\n", File.ReadAllText(P("out")));
    }

    [Fact]
    public void InputThenDash_OutputGoesToStdout()
    {
        var r = Run("Invoke-BashUniq in -").AssertSuccess();
        Assert.Equal("a\nb\nc", r.Stdout);
    }

    [Fact]
    public void MissingInput_ErrorsExit1_AndDoesNotCreateTheOutput()
    {
        Run("Invoke-BashUniq nosuch out").AssertFailed(1, "nosuch");
        Assert.False(File.Exists(P("out")));
    }

    [Fact]
    public void UnwritableOutput_Exit1()
    {
        Run("Invoke-BashUniq in nodir/out").AssertFailed(1, "nodir/out");
    }

    [Fact]
    public void ThirdOperand_IsStillAnExtraOperandError()
    {
        Run("Invoke-BashUniq in out third").AssertFailed(1, "extra operand 'third'");
        Assert.False(File.Exists(P("out")));
    }
}
